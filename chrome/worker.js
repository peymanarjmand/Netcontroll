const endpoint = 'http://127.0.0.1:17831/chrome-rules/';
const siteEndpoint = 'http://127.0.0.1:17831/chrome-site/';
const conflict = 'پراکسی این پروفایل در اختیار افزونه یا سیاست دیگری است. تنظیم پراکسی آن را در همین پروفایل بررسی کنید.';
const offline = 'ارتباط با Netcontroll برقرار نشد. برنامه را در ویندوز باز کنید و دوباره بررسی کنید.';
let watcher;
let changes = Promise.resolve();

function modeOf(value) {
  return value === 1 || value === 2 ? value : 0;
}

function localHost(host) {
  const h = host.toLowerCase().replace(/\.$/, '').replace(/^\[|\]$/g, '');
  return h === 'localhost' || h.endsWith('.localhost') || h === '::1' ||
    h === '0:0:0:0:0:0:0:1' || /^127(?:\.\d{1,3}){3}$/.test(h);
}

function siteHost(url) {
  try {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) return '';
    const host = parsed.hostname.toLowerCase().replace(/\.$/, '');
    if (localHost(host) || host.includes(':') || /^\d+(?:\.\d+){3}$/.test(host)) return '';
    return host;
  } catch { return ''; }
}

// PAC can choose by request host, not by tab ID. Forced Direct and VPN both go
// through Netcontroll, which binds the upstream socket to the selected adapter.
// Returning DIRECT for a Direct policy would wrongly follow a full-tunnel VPN.
function pacFor(rules, defaultMode = 0) {
  const table = JSON.stringify(rules.map(r => [
    r.host.toLowerCase().replace(/\.$/, ''), Boolean(r.sub),
    typeof r.mode === 'number' ? modeOf(r.mode) !== 0 : Boolean(r.route)
  ]).sort((a, b) => b[0].length - a[0].length));
  const fallback = modeOf(defaultMode) ? 'PROXY 127.0.0.1:17831' : 'DIRECT';
  return `// Netcontroll default mode: ${modeOf(defaultMode)}
function FindProxyForURL(url, host) {
    var h = host.toLowerCase().replace(/\\.$/, '').replace(/^\\[|\\]$/g, ''), rules = ${table};
    if (h === 'localhost' || h.slice(-10) === '.localhost' || h === '::1' ||
        h === '0:0:0:0:0:0:0:1' || /^127(?:\\.\\d{1,3}){3}$/.test(h)) return 'DIRECT';
    for (var i = 0; i < rules.length; i++) {
      var r = rules[i];
      if (h === r[0] || (r[1] && h.slice(-(r[0].length + 1)) === '.' + r[0])) {
        return r[2] ? 'PROXY 127.0.0.1:17831' : 'DIRECT';
      }
    }
    return '${fallback}';
  }`;
}

function verified(setting, script) {
  return setting.levelOfControl === 'controlled_by_this_extension' &&
    setting.value?.mode === 'pac_script' && setting.value.pacScript?.mandatory === true &&
    setting.value.pacScript.data === script;
}

function checkPolicy(policy) {
  if (!policy || typeof policy.revision !== 'string' || !Array.isArray(policy.rules) ||
      policy.rules.some(r => !r || typeof r.host !== 'string'))
    throw new Error('پاسخ برنامه معتبر نیست. Netcontroll و افزونه را با هم به‌روزرسانی کنید.');
  return policy;
}

async function readPolicy(revision = '', headers = {}, signal = AbortSignal.timeout(5000)) {
  let response;
  try {
    response = await fetch(endpoint + encodeURIComponent(revision), {
      headers: {'X-Netcontroll-Client': 'chrome-v1', ...headers}, cache: 'no-store', signal
    });
    if (!response.ok) throw new Error();
  } catch (e) {
    if (signal.aborted && e.name === 'AbortError') throw e;
    throw new Error(offline);
  }
  return checkPolicy(await response.json());
}

const current = () => chrome.proxy.settings.get({incognito:false});

async function ensurePolicy(policy) {
  const script = pacFor(policy.rules, policy.defaultMode);
  const setting = await current();
  if (verified(setting, script)) return;
  if (!['controllable_by_this_extension','controlled_by_this_extension'].includes(setting.levelOfControl))
    throw new Error(conflict);
  await chrome.proxy.settings.set({scope:'regular', value:{
    mode:'pac_script', pacScript:{mandatory:true, data:script}
  }});
  if (!verified(await current(), script))
    throw new Error('Chrome اعمال قوانین این پروفایل را تأیید نکرد.');
}

// Serialize writes so a cancelled long poll cannot re-enable the proxy after disconnect.
function change(action) {
  const next = changes.then(action);
  changes = next.catch(() => {});
  return next;
}

function stopFollowing() {
  if (watcher) { watcher.abort(); watcher = undefined; }
}

async function follow() {
  if (watcher) return;
  const controller = new AbortController();
  watcher = controller;
  try {
    const state = await chrome.storage.local.get(['enabled', 'profile']);
    if (!state.enabled || controller.signal.aborted) return;
    if (!await chrome.alarms.get('reconnect'))
      await chrome.alarms.create('reconnect',{periodInMinutes:0.5});
    let profile = state.profile;
    if (!profile) {
      profile = crypto.randomUUID();
      await chrome.storage.local.set({profile});
    }
    let policy = await readPolicy();
    while (!controller.signal.aborted) {
      await change(async () => {
        if (!controller.signal.aborted) await ensurePolicy(policy);
      });
      if (controller.signal.aborted) return;
      // Only an effective, read-back PAC for this revision counts as acknowledged.
      const signal = AbortSignal.any
        ? AbortSignal.any([controller.signal, AbortSignal.timeout(26000)]) : controller.signal;
      policy = await readPolicy(policy.revision, {'X-Netcontroll-Profile':profile,
        'X-Netcontroll-Applied':policy.revision}, signal);
    }
  } catch { /* The reconnect alarm retries when the app or proxy becomes available. */ }
  finally { if (watcher === controller) watcher = undefined; }
}

async function toggle(enable) {
  stopFollowing();
  if (!enable) {
    // Clear only this extension's own setting; Chrome restores the underlying
    // settings, including another extension or enterprise policy where present.
    await chrome.proxy.settings.clear({scope:'regular'});
    const {profile} = await chrome.storage.local.get('profile');
    await chrome.storage.local.set({enabled:false});
    await chrome.alarms.clear('reconnect');
    if (profile) await readPolicy('', {'X-Netcontroll-Profile':profile}).catch(() => {});
    return;
  }
  const policy = await readPolicy();
  await ensurePolicy(policy);
  await chrome.storage.local.set({enabled:true});
  follow();
}

function matchSite(policy, host) {
  if (!host) return null;
  const rule = policy.rules.filter(r => host === r.host || (r.sub && host.endsWith('.' + r.host)))
    .sort((a, b) => b.host.length - a.host.length)[0];
  return {
    host, mode: rule ? (typeof rule.mode === 'number' ? modeOf(rule.mode) : rule.route ? null : 0)
      : modeOf(policy.defaultMode),
    inherited: Boolean(rule && rule.host !== host),
    matchedHost: rule?.host || '', exact: Boolean(rule && rule.host === host),
    subdomains: Boolean(rule?.sub), source: rule ? 'rule' : 'default'
  };
}

async function status(host = '') {
  const {enabled} = await chrome.storage.local.get('enabled');
  const setting = await current();
  const owned = setting.levelOfControl === 'controlled_by_this_extension';
  const blocked = !owned && setting.levelOfControl !== 'controllable_by_this_extension';
  let message = blocked ? conflict : 'این پروفایل هنوز به Netcontroll متصل نیست.';
  let policy, applied = false, online = false;
  try {
    policy = await readPolicy();
    online = true;
    applied = Boolean(enabled && verified(await current(), pacFor(policy.rules, policy.defaultMode)));
    if (!blocked && (enabled || owned)) message = applied
      ? 'قوانین این پروفایل اعمال و توسط Chrome تأیید شده‌اند.'
      : 'قوانین فعلی هنوز در این پروفایل تأیید نشده‌اند؛ اتصال را دوباره برقرار کنید.';
  } catch (e) { if (!blocked) message = e.message; }
  return {canDisconnect:Boolean(enabled || owned), blocked, message, online, applied,
    defaultMode:modeOf(policy?.defaultMode), ruleCount:policy?.rules?.length || 0,
    canEdit:Boolean(applied && policy?.token), site: policy ? matchSite(policy, host) : null};
}

async function setSite(message) {
  if (!['direct', 'vpn', 'system', 'default'].includes(message.mode))
    throw new Error('مسیر انتخاب‌شده معتبر نیست.');
  const {enabled} = await chrome.storage.local.get('enabled');
  if (!enabled) throw new Error('ابتدا این پروفایل را به Netcontroll متصل کنید.');
  // Re-read the granted active tab: never trust a stale host after navigation.
  const tab = await chrome.tabs.get(message.tabId);
  const host = siteHost(tab.url || '');
  if (!host || host !== message.host)
    throw new Error('صفحه تغییر کرده است. افزونه را دوباره باز کنید.');
  const policy = await readPolicy();
  if (!policy.token) throw new Error('برای تغییر مسیر سایت، نسخهٔ جدید Netcontroll را نصب کنید.');
  if (!verified(await current(), pacFor(policy.rules, policy.defaultMode)))
    throw new Error('ابتدا اتصال این پروفایل را دوباره برقرار کنید تا قوانین تأیید شوند.');
  stopFollowing();
  try {
    let response;
    try {
      response = await fetch(siteEndpoint, {
        method:'POST', cache:'no-store', signal:AbortSignal.timeout(5000),
        headers:{'Content-Type':'application/json', 'X-Netcontroll-Client':'chrome-v1',
          'X-Netcontroll-Token':policy.token},
        body:JSON.stringify({host, mode:message.mode, subdomains:message.subdomains === true})
      });
    } catch { throw new Error(offline); }
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error || 'تغییر مسیر ذخیره نشد. برنامه را بررسی کنید و دوباره تلاش کنید.');
    await ensurePolicy(checkPolicy(result));
    return {host, site:matchSite(result, host)};
  } finally { follow(); }
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (sender.id !== chrome.runtime.id || !msg || typeof msg !== 'object') return false;
  let action;
  if (msg.action === 'status') action = status(typeof msg.host === 'string' ? msg.host : '');
  else if (msg.action === 'toggle') action = change(() => toggle(msg.enable === true));
  else if (msg.action === 'site') action = change(() => setSite(msg));
  else return false;
  action.then(value=>reply({ok:true,value}), e=>reply({ok:false,error:e.message}));
  return true;
});
chrome.alarms.onAlarm.addListener(a=>{if(a.name==='reconnect')follow();});
chrome.runtime.onInstalled.addListener(({reason}) => {
  if (reason === 'install') chrome.tabs.create({url:chrome.runtime.getURL('popup.html')});
  follow();
});
chrome.runtime.onStartup.addListener(follow);
follow();
