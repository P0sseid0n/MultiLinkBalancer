// Estados de um link enviados pelo app (LinkHealth no C#).

export const OK = 'ok';
export const DISABLED = 'disabled';

/** Rótulo curto de cada estado de queda, para o histórico. */
export const outageLabel = {
  noinet: 'Sem internet (operadora)',
  nogw: 'Modem não responde',
  unplugged: 'Cabo desconectado',
  disabled: 'Desativado',
  nocfg: 'Sem IP/gateway',
};

export const isOnline = (link) => link.health === OK;

/** Fora do ar sem ter sido desativado (cabo, operadora, modem...). */
export const isDown = (link) => link.health !== OK && link.health !== DISABLED;

/** Online, ativo e com peso: recebe conexões. */
export const isUsable = (link) => isOnline(link) && link.enabled && link.weight > 0;
