// Runs in every page frame. An alert/confirm/prompt blocks the page until someone answers, and agents cannot answer
// a native box. The host decides synchronously: in a tab an agent is driving it answers for the agent and reports the
// text in the next snapshot; otherwise the page gets the normal browser dialog.
const { contextBridge, ipcRenderer } = require('electron');

const ask = (type, message, value) => ipcRenderer.sendSync('oinzi-dialog', { type, message, value });

contextBridge.executeInMainWorld({
  func: ask => {
    const original = { alert: window.alert, confirm: window.confirm, prompt: window.prompt };
    const wrap = (type, run) => function (message, value) {
      const answer = ask(type, message === undefined ? '' : String(message), value === undefined ? '' : String(value));
      return answer && answer.handled ? answer.result : run.apply(window, arguments);
    };
    window.alert = wrap('alert', original.alert);
    window.confirm = wrap('confirm', original.confirm);
    window.prompt = wrap('prompt', original.prompt);
  },
  args: [ask],
});
