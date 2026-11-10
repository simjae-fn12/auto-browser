// The chrome UI (native/chrome/chrome.html) was written for WebView2 and talks to its host through
// window.chrome.webview. This preload provides the same object over Electron IPC, so one HTML file serves both
// the Windows and the Linux build. The page is local and never navigates, so it shares the preload's world.
const { ipcRenderer } = require('electron');

const listeners = new Set();
ipcRenderer.on('chrome-message', (_event, data) => {
  for (const listener of listeners) {
    try { listener({ data }); } catch (error) { console.error(error); }
  }
});

// WebView2 places its controls in device pixels and the page reports layout that way; Electron works in DIPs.
const toDip = message => {
  if (message?.type !== 'layout' && message?.type !== 'sidebar.done') return message;
  const d = window.devicePixelRatio || 1;
  const scaled = { ...message };
  for (const key of ['x', 'y', 'w', 'h', 'r']) if (typeof scaled[key] === 'number') scaled[key] = Math.round(scaled[key] / d);
  return scaled;
};

window.chrome = window.chrome || {};
window.chrome.webview = {
  postMessage: message => ipcRenderer.send('chrome-message', toDip(message)),
  addEventListener: (type, listener) => { if (type === 'message') listeners.add(listener); },
  removeEventListener: (type, listener) => { if (type === 'message') listeners.delete(listener); },
};
// Lets the page tell the Linux host apart where wording differs (for example "Windows 알림").
window.oinziPlatform = 'linux';
