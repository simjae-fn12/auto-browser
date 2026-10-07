const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('browser', {
  action: (name, value) => ipcRenderer.invoke('ui-action', name, value),
  onState: (callback) => ipcRenderer.on('browser-state', (_event, state) => callback(state)),
  onFocusAddress: (callback) => ipcRenderer.on('focus-address', callback),
});
