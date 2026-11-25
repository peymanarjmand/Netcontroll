const el = id => document.getElementById(id);
const modes = ['مسیر سیستم', 'اینترنت عادی', 'فقط VPN'];
const routes = ['system', 'direct', 'vpn'];
let tab, host = '', snapshot, busy = false;

async function request(message) {
  const result = await chrome.runtime.sendMessage(message);
  if (!result?.ok) throw new Error(result?.error || 'ارتباط با افزونه برقرار نشد.');
  return result.value;
}

function feedback(message, error = false) {
  el('feedback').textContent = message;
  el('feedback').classList.toggle('error', error);
}

function lock(value) {
  busy = value;
  const canEdit = !busy && host && snapshot?.canEdit;
  document.querySelectorAll('[data-route]').forEach(button => {button.disabled = !canEdit;});
  el('subdomains').disabled = !canEdit;
  el('reset').disabled = !canEdit || !snapshot?.site?.exact;
  el('reload').disabled = busy || !host;
  el('refresh').disabled = busy;
  el('toggle').disabled = busy || !snapshot ||
    (snapshot.blocked && !snapshot.canDisconnect) || (!snapshot.online && !snapshot.canDisconnect);
}

function render(value) {
  snapshot = value;
  el('state').textContent = value.message;
  el('count').textContent = value.online ? `${value.ruleCount.toLocaleString('fa-IR')} قانون فعال` : '';
  el('default-mode').textContent = value.online ? modes[value.defaultMode] : '—';
  const badge = el('badge');
  badge.textContent = value.applied ? 'متصل' : value.online ? 'آماده' : 'قطع ارتباط';
  badge.className = 'badge ' + (value.applied ? 'online' : value.online ? 'pending' : 'offline');
  el('toggle').textContent = value.canDisconnect ? 'قطع اتصال این پروفایل' : 'اتصال این پروفایل';
  el('toggle').classList.toggle('connected', value.canDisconnect);
  const site = value.site;
  document.querySelectorAll('[data-route]').forEach(button => {
    button.setAttribute('aria-pressed', Boolean(site?.exact && button.dataset.route === routes[site.mode]));
  });
  el('subdomains').checked = Boolean(site?.exact && site.subdomains);
  if (!host) {
    el('site-status').textContent = 'این صفحه قانون دامنه نمی‌پذیرد؛ یک سایت http یا https را باز کنید.';
  } else if (!value.online) {
    el('site-status').textContent = 'برای دیدن مسیر مؤثر، برنامه را در ویندوز باز کنید.';
  } else if (!value.canEdit) {
    el('site-status').textContent = value.applied
      ? 'برای تغییر مسیر سایت، برنامهٔ ویندوز را به‌روزرسانی کنید.'
      : 'برای انتخاب مسیر این سایت، ابتدا پروفایل را متصل کنید.';
  } else {
    const mode = site?.mode === null ? 'قانون برنامه' : modes[site?.mode ?? value.defaultMode];
    el('site-status').textContent = `مسیر مؤثر: ${mode} · ` + (site?.inherited
      ? `از قانون ${site.matchedHost}` : site?.exact ? 'قانون اختصاصی' : 'پیش‌فرض پروفایل');
  }
  lock(busy);
}

async function refresh() {
  render(await request({action:'status', host}));
}

async function operation(action) {
  if (busy) return;
  lock(true);
  feedback('');
  try { await action(); }
  catch (error) { feedback(error.message, true); }
  finally { lock(false); }
}

el('toggle').onclick = () => operation(async () => {
  await request({action:'toggle', enable:!snapshot.canDisconnect});
  await refresh();
});

async function setRoute(mode) {
  await request({action:'site', mode, host, tabId:tab.id, subdomains:el('subdomains').checked});
  await refresh();
  feedback(mode === 'default'
    ? 'قانون اختصاصی حذف شد. قانون دامنهٔ بالاتر یا مسیر پیش‌فرض اعمال می‌شود.'
    : 'مسیر ذخیره و تأیید شد. برای بارگذاری دوبارهٔ سایت، «تازه‌کردن تب» را بزنید.');
}

document.querySelectorAll('[data-route]').forEach(button => {
  button.onclick = () => operation(() => setRoute(button.dataset.route));
});
el('reset').onclick = () => operation(() => setRoute('default'));
el('reload').onclick = () => operation(async () => {
  await chrome.tabs.reload(tab.id);
  feedback('تب تازه شد. مسیر اتصال را در «فعالیت اتصال‌ها»ی برنامه بررسی کنید.');
});
el('refresh').onclick = () => operation(refresh);

(async () => {
  try {
    [tab] = await chrome.tabs.query({active:true, currentWindow:true});
    if (tab?.url) {
      const parsed = new URL(tab.url);
      const name = parsed.hostname.toLowerCase().replace(/\.$/, '');
      if (['http:', 'https:'].includes(parsed.protocol) && name && !name.includes(':') &&
          name !== 'localhost' && !name.endsWith('.localhost') && !/^\d+(?:\.\d+){3}$/.test(name)) host = name;
    }
    el('host').textContent = host || 'هیچ سایت قابل انتخابی نیست';
    el('host').title = host;
    await refresh();
  } catch (error) {
    el('state').textContent = error.message;
    feedback(error.message, true);
    lock(false);
  }
})();
