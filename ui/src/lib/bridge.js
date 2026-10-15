// Ponte com o app C#. Dentro do WebView2 usa window.chrome.webview (sem servidor HTTP, sem porta aberta).
// Fora dele (npm run dev no navegador) usa um simulador com dados falsos.
import { createMock } from './mock.js';

const host = window.chrome && window.chrome.webview;
const pending = new Map();
const listeners = new Set();
let seq = 0;

function deliver(msg) {
  if (msg.type === 'reply') {
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    msg.ok ? p.resolve(msg.result) : p.reject(new Error(msg.error));
  } else {
    listeners.forEach((fn) => fn(msg));
  }
}

// o simulador só existe no "npm run dev"; no build de produção ele é removido
const mock = !host && import.meta.env.DEV ? createMock(deliver) : null;
if (host) host.addEventListener('message', (e) => deliver(e.data));

export const isMock = !host;

/** Envia um comando ao app e espera a resposta. */
export function call(cmd, args = {}) {
  const id = ++seq;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    const text = JSON.stringify({ id, cmd, args });
    if (host) host.postMessage(text);
    else if (mock) mock.receive(text);
    else reject(new Error('Abra pelo MultiLinkBalancer.exe.'));
  });
}

/** Recebe mensagens enviadas pelo app (estado a cada 1 s, novas linhas de log). */
export function onMessage(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
