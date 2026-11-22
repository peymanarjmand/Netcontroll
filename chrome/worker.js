const endpoint = 'http://127.0.0.1:17831/chrome-rules/';
let running = false;
let generation = 0;
let watch;

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

async function readPolicy(revision, signal) {
  const response = await fetch(endpoint + encodeURIComponent(revision), {
    headers: {'X-Netcontroll-Client': 'chrome-v1'}, cache: 'no-store', signal
  });
  if (!response.ok) throw new Error('ابتدا برنامهٔ Netcontroll را باز کنید.');
  return response.json();
}

async function apply(policy) {
  const current = await chrome.proxy.settings.get({incognito: false});
  if (!['controllable_by_this_extension','controlled_by_this_extension'].includes(current.levelOfControl))
    throw new Error('تنظیم پراکسی تحت کنترل سیاست یا افزونهٔ دیگری است.');
  await chrome.proxy.settings.set({scope:'regular', value:{
    mode:'pac_script', pacScript:{mandatory:true, data:pacFor(policy.rules)}
  }});
}

async function follow() {
  if (running) return;
  running = true;
  const token = generation;
  try {
    let {enabled, revision = ''} = await chrome.storage.local.get(['enabled', 'revision']);
    while (enabled && token === generation) {
      watch = new AbortController();
      const policy = await readPolicy(revision, watch.signal);
      if (token !== generation) break;
      if (policy.revision !== revision) {
        await apply(policy);
        if (token !== generation) break;
        revision = policy.revision;
        await chrome.storage.local.set({revision});
      }
    }
  } catch { /* The alarm resumes the watch when the local app returns. */ }
  finally { running = false; if (generation !== token) follow(); }
}

async function toggle(enable) {
  generation++;
  if (watch) watch.abort();
  if (!enable) {
    await chrome.storage.local.set({enabled:false,revision:''});
    await chrome.proxy.settings.clear({scope:'regular'});
    await chrome.alarms.clear('reconnect');
    return;
  }
  const policy = await readPolicy('', AbortSignal.timeout(5000));
  await apply(policy);
  await chrome.storage.local.set({enabled:true,revision:policy.revision});
  await chrome.alarms.create('reconnect',{periodInMinutes:0.5});
  follow();
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (sender.id !== chrome.runtime.id || msg.action !== 'toggle') return false;
  toggle(msg.enable).then(()=>reply({ok:true}), e=>reply({ok:false,error:e.message}));
  return true;
});
chrome.alarms.onAlarm.addListener(a=>{if(a.name==='reconnect')follow();});
chrome.runtime.onStartup.addListener(follow);
follow();
