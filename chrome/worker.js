const endpoint = 'http://127.0.0.1:17831/chrome-rules/';
const conflict = 'پراکسی این پروفایل در اختیار افزونه یا سیاست دیگری است. تنظیم پراکسی آن را در همین پروفایل بررسی کنید.';
const offline = 'ارتباط با Netcontroll برقرار نشد. برنامه را در ویندوز باز کنید و دوباره بررسی کنید.';
let watcher;
let changes = Promise.resolve();

function pacFor(rules) {
  const table = JSON.stringify(rules.map(r => [r.host, r.sub, r.route]));
  return `function FindProxyForURL(url, host) {
    var h = host.toLowerCase().replace(/\\.$/, ''), rules = ${table};
    for (var i = 0; i < rules.length; i++) {
      var r = rules[i];
      if (h === r[0] || (r[1] && h.slice(-(r[0].length + 1)) === '.' + r[0])) {
        return r[2] ? 'PROXY 127.0.0.1:17831' : 'DIRECT';
      }
    }
    return 'DIRECT';
  }`;
}

function verified(setting, script) {
  return setting.levelOfControl === 'controlled_by_this_extension' &&
    setting.value?.mode === 'pac_script' && setting.value.pacScript?.mandatory === true &&
    setting.value.pacScript.data === script;
}

async function readPolicy(revision = '', headers = {}, signal = AbortSignal.timeout(5000)) {
  try {
    const response = await fetch(endpoint + encodeURIComponent(revision), {
      headers: {'X-Netcontroll-Client': 'chrome-v1', ...headers}, cache: 'no-store', signal
    });
    if (!response.ok) throw new Error();
    return await response.json();
  } catch (e) {
    if (signal.aborted && e.name === 'AbortError') throw e;
    throw new Error(offline);
  }
}

const current = () => chrome.proxy.settings.get({incognito:false});

async function ensurePolicy(policy) {
  const script = pacFor(policy.rules);
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

async function follow() {
  if (watcher) return;
  const controller = new AbortController();
  watcher = controller;
  try {
    const state = await chrome.storage.local.get(['enabled', 'profile']);
    if (!state.enabled) return;
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
  if (watcher) { watcher.abort(); watcher = undefined; }
  if (!enable) {
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

async function status() {
  const {enabled} = await chrome.storage.local.get('enabled');
  const setting = await current();
  const owned = setting.levelOfControl === 'controlled_by_this_extension';
  const blocked = !owned && setting.levelOfControl !== 'controllable_by_this_extension';
  let message = blocked ? conflict : 'با اتصال این پروفایل، قوانین دامنه روی همین Chrome اعمال می‌شوند.';
  if (!blocked && (enabled || owned)) {
    try {
      const policy = await readPolicy();
      message = verified(await current(), pacFor(policy.rules))
        ? 'Chrome دریافت قوانین فعلی را تأیید کرد. تعداد قوانین فعال: ' + policy.rules.length
        : 'قوانین فعلی هنوز در این پروفایل تأیید نشده‌اند.';
    } catch (e) { message = e.message; }
  }
  return {canDisconnect:Boolean(enabled || owned), blocked, message};
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (sender.id !== chrome.runtime.id) return false;
  let action;
  if (msg.action === 'status') action = status();
  else if (msg.action === 'toggle') action = change(() => toggle(msg.enable === true));
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
