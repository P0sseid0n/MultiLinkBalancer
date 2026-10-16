// Ponte com o app C#. Dentro do WebView2 usa window.chrome.webview (sem servidor HTTP, sem porta aberta).
// Fora dele (npm run dev no navegador) usa um simulador com dados falsos.
// Protocolo: ver "Contrato entre o app e a interface" no ARCHITECTURE.md.
import { createMock } from './mock.js';

const host = window.chrome && window.chrome.webview;
const pendingReplies = new Map();
const listeners = new Set();
let lastId = 0;

function deliver(message) {
  if (message.type === 'reply') {
    const pending = pendingReplies.get(message.id);
    if (!pending) return;
    pendingReplies.delete(message.id);
    if (message.ok) pending.resolve(message.result);
    else pending.reject(new Error(message.error));
  } else {
    listeners.forEach((listener) => listener(message));
  }
}

// o simulador só existe no "npm run dev"; no build de produção ele é removido
const mock = !host && import.meta.env.DEV ? createMock(deliver) : null;
if (host) host.addEventListener('message', (event) => deliver(event.data));

export const isMock = !host;

/** Envia um comando ao app e espera a resposta. */
export function call(cmd, args = {}) {
  const id = ++lastId;
  return new Promise((resolve, reject) => {
    pendingReplies.set(id, { resolve, reject });
    const text = JSON.stringify({ id, cmd, args });
    if (host) host.postMessage(text);
    else if (mock) mock.receive(text);
    else reject(new Error('Abra pelo MultiLinkBalancer.exe.'));
  });
}

/** Recebe mensagens enviadas pelo app (estado a cada 1 s, novas linhas do registro de eventos). */
export function onMessage(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
