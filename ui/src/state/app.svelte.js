// Estado global da interface, alimentado pelas mensagens do app.
import { call, onMessage } from '../api/bridge.js';

const MAX_LOG = 500;
const TOAST_MS = 6000;

export const app = $state({
  state: null,   // último estado enviado pelo app (UiState no C#)
  log: [],       // registro de eventos
  toasts: [],
  confirm: null, // { title, body, confirm, danger, resolve }
});

let started = false;
let lastToastId = 0;

export function initStore() {
  if (started) return;
  started = true;
  onMessage((message) => {
    if (message.type === 'state') app.state = message.data;
    else if (message.type === 'log') app.log = [...app.log, ...message.entries].slice(-MAX_LOG);
  });
  call('ui.ready').then((initial) => {
    app.log = initial.log || [];
    app.state = initial.state;
  });
}

/** Executa um comando; erros viram um aviso na tela e são relançados. */
export async function run(cmd, args) {
  try {
    return await call(cmd, args);
  } catch (error) {
    toast(error.message, 'error');
    throw error;
  }
}

/** Executa um comando sem esperar o resultado (o erro já aparece como aviso). */
export function fire(cmd, args) {
  run(cmd, args).catch(() => {});
}

/** Pergunta de confirmação no próprio app. Resolve true/false. */
export function ask(options) {
  return new Promise((resolve) => {
    app.confirm = { ...options, resolve };
  });
}

export function answer(confirmed) {
  const pending = app.confirm;
  app.confirm = null;
  if (pending) pending.resolve(confirmed);
}

export function toast(text, kind = 'info') {
  const id = ++lastToastId;
  app.toasts = [...app.toasts, { id, text, kind }];
  setTimeout(() => dismissToast(id), TOAST_MS);
}

export function dismissToast(id) {
  app.toasts = app.toasts.filter((item) => item.id !== id);
}
