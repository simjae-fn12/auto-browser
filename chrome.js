const tabsEl = document.getElementById('tabs');
const address = document.getElementById('address');
let current = null;

window.browser.onState((state) => {
  current = state.active;
  tabsEl.replaceChildren();
  for (const tab of state.tabs) {
    const item = document.createElement('div');
    item.className = `tab ${tab.id === current ? 'active ' : ''}${tab.agent ? 'agent' : ''}`;
    const button = document.createElement('button');
    button.textContent = `${tab.agent ? '◆ ' : ''}${tab.title || '새 탭'}`;
    button.title = tab.title || tab.url;
    button.onclick = () => window.browser.action('select', tab.id);
    const close = document.createElement('button');
    close.className = 'close';
    close.textContent = '×';
    close.title = '탭 닫기';
    close.onclick = () => window.browser.action('close', tab.id);
    item.append(button, close);
    tabsEl.appendChild(item);
  }
  const active = state.tabs.find(tab => tab.id === current);
  if (document.activeElement !== address) address.value = active?.url || '';
  document.title = active?.title ? `${active.title} — Ego Windows` : 'Ego Windows';
});

window.browser.onFocusAddress(() => {
  address.focus();
  address.select();
});

document.getElementById('address-form').onsubmit = (event) => {
  event.preventDefault();
  window.browser.action('goto', address.value);
  address.blur();
};
document.getElementById('new').onclick = () => window.browser.action('new');
document.getElementById('back').onclick = () => window.browser.action('back');
document.getElementById('forward').onclick = () => window.browser.action('forward');
document.getElementById('reload').onclick = () => window.browser.action('reload');
