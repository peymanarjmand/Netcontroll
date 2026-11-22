const state = document.getElementById('state');
const toggle = document.getElementById('toggle');
async function refresh() {
  const current = await chrome.proxy.settings.get({incognito: false});
  const owned = current.levelOfControl === 'controlled_by_this_extension';
  toggle.disabled = !owned && current.levelOfControl !== 'controllable_by_this_extension';
  toggle.textContent = owned ? 'بازگشت به تنظیمات قبلی Chrome' : 'اتصال این پروفایل';
  state.textContent = toggle.disabled ? 'تنظیمات پراکسی در اختیار سیاست سازمان یا افزونه دیگری است.' : owned ? 'قوانین دامنهٔ Netcontroll روی این پروفایل فعال‌اند.' : 'ابتدا Netcontroll را در ویندوز باز کنید.';
  toggle.onclick = async () => {
    try {
      toggle.disabled = true;
      const result = await chrome.runtime.sendMessage({action:'toggle',enable:!owned});
      if (!result?.ok) throw new Error(result?.error || 'اتصال به برنامه برقرار نشد.');
      await refresh();
    } catch (error) { state.textContent = error.message; toggle.disabled = false; }
  };
}
refresh().catch(error => {state.textContent = error.message;});
