const state = document.getElementById('state');
const toggle = document.getElementById('toggle');

async function request(message) {
  const result = await chrome.runtime.sendMessage(message);
  if (!result?.ok) throw new Error(result?.error || 'ارتباط با افزونه برقرار نشد.');
  return result.value;
}

async function refresh() {
  const {canDisconnect, blocked, message} = await request({action:'status'});
  state.textContent = message;
  toggle.disabled = blocked && !canDisconnect;
  toggle.textContent = canDisconnect ? 'قطع اتصال این پروفایل' : 'اتصال این پروفایل';
  toggle.onclick = async () => {
    toggle.disabled = true;
    try {
      await request({action:'toggle',enable:!canDisconnect});
      await refresh();
    } catch (error) {
      state.textContent = error.message;
      toggle.disabled = false;
    }
  };
}
refresh().catch(error => {state.textContent = error.message;});
