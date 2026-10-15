// Estado global da UI, alimentado pelas mensagens do app.
import { call, onMessage } from './bridge.js';

export const app = $state({
  state: null,
  log: [],
  toasts: [],
  confirm: null, // { title, body, confirm, danger, resolve }
});

/** Pergunta de confirmação no próprio app. Resolve true/false. */
export function ask(opts) {
  return new Promise((resolve) => {
    app.confirm = { ...opts, resolve };
  });
}

export function answer(yes) {
  const c = app.confirm;
  app.confirm = null;
  if (c) c.resolve(yes);
}

let started = false;
let toastSeq = 0;

export function initStore() {
  if (started) return;
  started = true;
  onMessage((m) => {
    if (m.type === 'state') applyState(m.data);
    else if (m.type === 'log') app.log = [...app.log, ...m.entries].slice(-500);
  });
  call('ui.ready').then((r) => {
    app.log = r.log || [];
    applyState(r.state);
  });
}

function applyState(s) {
  app.state = s;
}

/** Executa um comando; erros viram um aviso na tela e são relançados. */
export async function run(cmd, args) {
  try {
    return await call(cmd, args);
  } catch (e) {
    toast(e.message, 'error');
    throw e;
  }
}

export function toast(text, kind = 'info') {
  const id = ++toastSeq;
  app.toasts = [...app.toasts, { id, text, kind }];
  setTimeout(() => (app.toasts = app.toasts.filter((x) => x.id !== id)), 6000);
}

export function dismissToast(id) {
  app.toasts = app.toasts.filter((x) => x.id !== id);
}
