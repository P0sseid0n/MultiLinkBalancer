// Simulador do app C# para desenvolver a interface no navegador (npm run dev).
// Imita o formato de UiState.Build e os comandos de AppController.Handle.

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const now = () => Date.now();

export function createMock(deliver) {
  const links = [
    createLink('{A1}', 'Principal', 'Intel(R) Ethernet Connection (17) I219-V', '192.168.1.2', '192.168.1.1', 72, 719),
    createLink('{B2}', 'Secundaria', 'Realtek USB GbE Family Controller', '192.168.100.226', '192.168.100.1', 9, 94),
  ];
  const [primary, secondary] = links;
  const state = {
    admin: false,
    proxy: { running: true, port: 1080, lan: false, system: true, systemApplied: true },
    settings: { mode: 0, affinity: false, quality: true, autoUpdate: true, autoRoute: false, startup: false, notify: true, notifySound: true, hotkeys: true },
    capture: { on: false, active: false, starting: false, error: null, ipv6: true },
    measuring: false,
    update: { current: '1.0.0', latest: null, state: 'idle', progress: 0, error: null, notes: null },
    links,
    download: null,
  };
  const log = [];
  let lastLogSeq = 0;
  let second = 0;

  function addLog(text, kind = 'info') {
    const entry = { seq: ++lastLogSeq, time: new Date().toTimeString().slice(0, 8), kind, text };
    log.push(entry);
    deliver({ type: 'log', entries: [entry] });
  }

  // [healthText, diagnosis] de cada estado simulado
  const healthTexts = {
    ok: ['Online', ''],
    noinet: ['Sem internet (operadora)', 'sem internet. O modem responde, então o problema é na operadora'],
    disabled: ['Desativado no Windows', 'adaptador desativado no Windows'],
  };

  function setHealth(link, health) {
    const previous = link.health;
    link.health = health;
    [link.healthText, link.diagnosis] = healthTexts[health];
    if (health !== 'ok' && previous === 'ok') {
      link.downSince = now();
      const others = links.filter((other) => other !== link && other.health === 'ok').map((other) => other.name).join(', ');
      if (!link.userDisabled) addLog(`${link.name}: ${link.diagnosis}. Tráfego seguindo por ${others}.`, 'down');
    }
    if (health === 'ok' && previous !== 'ok') {
      link.downSince = null;
      link.userDisabled = false;
      addLog(`${link.name} voltou a funcionar.`, 'up');
    }
    link.present = health !== 'disabled';
  }

  // tráfego simulado: rajadas de download divididas pelos pesos
  function simulateSecond() {
    second++;
    const busy = state.proxy.running && (second % 30) < 22;
    const totalRate = busy ? (380 + 240 * Math.sin(second / 5) + Math.random() * 60) * 1e6 / 8 : 0;
    // a cada ~45 s a Secundaria perde a internet por 12 s
    if (secondary.health !== 'disabled') setHealth(secondary, (second % 45) > 32 ? 'noinet' : 'ok');
    const usable = links.filter((link) => link.health === 'ok' && link.enabled && link.weight > 0);
    const weightSum = usable.reduce((sum, link) => sum + link.weight, 0) || 1;
    for (const link of links) {
      const share = usable.includes(link) ? link.weight / weightSum : 0;
      const capped = Math.min(totalRate * share, (link.measuredMbps * 1e6) / 8);
      link.rateDown = Math.round(capped * (0.9 + Math.random() * 0.2));
      link.rateUp = Math.round(link.rateDown * 0.03);
      link.active = busy && share ? Math.round(20 * share + Math.random() * 4) : 0;
      link.bytesDown += link.rateDown;
      link.bytesUp += link.rateUp;
      link.total += link.active ? 1 : 0;
      simulateQuality(link);
    }
    simulateDownload();
    deliver({ type: 'state', data: structuredClone(state) });
  }

  // a Principal fica instável (perda alta) por um trecho de cada ciclo
  function simulateQuality(link) {
    const online = link.health === 'ok';
    link.latency = online ? 3 + Math.round(Math.random() * 3) : -1;
    link.loss = !online ? -1 : link === primary && (second % 60) > 40 ? 12 + Math.round(Math.random() * 6) : Math.round(Math.random());
    link.jitter = !online ? -1 : link.loss > 5 ? 18 + Math.round(Math.random() * 10) : 1 + Math.round(Math.random() * 2);
  }

  function simulateDownload() {
    const download = state.download;
    if (!download || download.state !== 'running') return;
    const online = links.filter((link) => link.health === 'ok');
    download.rate = 0;
    for (const part of download.perLink) {
      const link = links.find((candidate) => candidate.id === part.id);
      part.rate = online.includes(link) ? (link.measuredMbps * 1e6) / 8 * 0.85 : 0;
      part.bytes += part.rate;
      download.rate += part.rate;
    }
    download.done = Math.min(download.total, download.done + download.rate);
    if (download.done >= download.total) {
      download.state = 'done';
      download.avgMbps = 690;
      download.rate = 0;
      download.perLink.forEach((part) => (part.rate = 0));
      addLog(`Download concluído: ${download.name}`, 'up');
    }
  }

  setInterval(simulateSecond, 1000);

  const handlers = {
    'ui.ready': () => ({ state: structuredClone(state), log }),
    'proxy.start': () => { state.proxy.running = true; addLog('Proxy iniciado em 127.0.0.1:1080 (SOCKS4/SOCKS5/HTTP)'); },
    'proxy.stop': () => { state.proxy.running = false; state.proxy.systemApplied = false; addLog('Proxy parado.'); },
    'settings.set': ({ key, value }) => {
      if (key === 'port') {
        if (state.proxy.running) throw new Error('Pare o proxy antes de mudar a porta.');
        state.proxy.port = Number(value);
      } else if (key === 'lan' || key === 'system') state.proxy[key] = value;
      else if (key === 'capture') {
        state.capture.on = value;
        state.capture.active = value && state.admin;
        if (value && !state.admin) return { needsAdmin: true };
      } else state.settings[key] = value;
      if (key === 'autoRoute') return { needsAdmin: value && !state.admin };
    },
    'link.set': ({ id, enabled, weight }) => {
      const link = links.find((candidate) => candidate.id === id);
      if (enabled !== undefined) link.enabled = enabled;
      if (weight !== undefined) link.weight = weight;
    },
    'link.toggleAdapter': async ({ id }) => {
      const link = links.find((candidate) => candidate.id === id);
      await sleep(900);
      if (link.health === 'disabled') { setHealth(link, 'ok'); return { enabled: true }; }
      link.userDisabled = true;
      setHealth(link, 'disabled');
      addLog(`[${link.name}] adaptador desativado no Windows`);
      return { enabled: false };
    },
    'links.test': () => {},
    'adapters.refresh': () => {},
    measure: async () => {
      state.measuring = true;
      addLog('Medindo a velocidade de 2 link(s) em paralelo (cerca de 8 s)...');
      await sleep(3000);
      state.measuring = false;
      addLog('[Principal] 712 Mbps, peso 71');
      primary.measuredMbps = 712;
      primary.weight = 71;
    },
    'download.start': ({ url }) => {
      if (!/^https?:\/\//.test(url || '')) throw new Error('Informe um link que comece com http:// ou https://');
      const name = url.split('/').pop() || 'download.bin';
      state.download = {
        state: 'running', name, path: `C:\\Users\\voce\\Downloads\\${name}`, message: null, total: 1.4e9, done: 0, rate: 0, avgMbps: 0,
        perLink: links.map((link) => ({ id: link.id, name: link.name, bytes: 0, rate: 0 })),
      };
      addLog(`Download acelerado: ${name} por 2 link(s)`);
    },
    'download.cancel': () => { state.download.state = 'canceled'; state.download.message = 'Download cancelado.'; },
    'download.reveal': () => {},
    'download.clear': () => { state.download = null; },
    'history.get': ({ minutes }) => fakeHistory(minutes),
    'update.check': async () => {
      state.update.state = 'checking';
      await sleep(800);
      Object.assign(state.update, { state: 'available', latest: '1.1.0' });
    },
    'update.install': async () => {
      state.update.state = 'downloading';
      for (let progress = 0; progress <= 100; progress += 20) { state.update.progress = progress; await sleep(300); }
      Object.assign(state.update, { state: 'uptodate', current: '1.1.0', progress: 0 });
    },
    'app.restartElevated': () => { state.admin = true; },
    'app.openNetworkConnections': () => {},
    'app.exit': () => {},
    'window.minimize': () => {},
    'window.close': () => {},
  };

  addLog('Adaptador detectado: Principal (Intel(R) Ethernet Connection (17) I219-V), IP 192.168.1.2, gateway 192.168.1.1');
  addLog('Adaptador detectado: Secundaria (Realtek USB GbE Family Controller), IP 192.168.100.226, gateway 192.168.100.1');

  return {
    async receive(text) {
      const { id, cmd, args } = JSON.parse(text);
      try {
        const handler = handlers[cmd];
        if (!handler) throw new Error('Comando desconhecido: ' + cmd);
        const result = await handler(args);
        deliver({ type: 'reply', id, ok: true, result: result ?? null });
      } catch (error) {
        deliver({ type: 'reply', id, ok: false, error: error.message });
      }
      deliver({ type: 'state', data: structuredClone(state) });
    },
  };
}

function createLink(id, name, description, ip, gateway, weight, measuredMbps) {
  return {
    id, name, description, wired: true, present: true, health: 'ok', healthText: 'Online', diagnosis: '',
    enabled: true, weight, ip, gateway, latency: 4, loss: 0, jitter: 1, measuredMbps, linkSpeed: 1e9,
    rateDown: 0, rateUp: 0, active: 0, total: 0, bytesDown: 0, bytesUp: 0,
    metricRaised: false, userDisabled: false, downSince: null,
  };
}

// Histórico com o mesmo formato de UsageHistory.Get: a Secundaria cai por alguns minutos a cada ~1,5 h.
function fakeHistory(minutes) {
  const stepMinutes = minutes <= 60 ? 1 : minutes <= 360 ? 5 : 15;
  const end = Math.floor(now() / 60000) * 60000;
  const points = [];
  for (let time = end - minutes * 60000; time < end; time += stepMinutes * 60000) {
    const minute = time / 60000;
    const load = 0.5 + 0.5 * Math.sin(minute / 9);
    const secondaryDown = minute % 97 > 90;
    const noise = () => 0.8 + Math.random() * 0.4;
    points.push({
      t: time,
      v: {
        '{A1}': [Math.round(load * 52e6 * noise()), 1e6, 4 + Math.random() * 3, 1, Math.random() < 0.9 ? 0 : Math.round(Math.random() * 30) / 10],
        '{B2}': secondaryDown ? [0, 0, -1, 0, -1] : [Math.round(load * 7e6 * noise()), 2e5, 18 + Math.random() * 10, 1, minute % 97 > 84 ? 5 + Math.round(Math.random() * 100) / 10 : Math.round(Math.random() * 15) / 10],
      },
    });
  }
  const daysAgo = (count) => new Date(now() - count * 864e5).toISOString().slice(0, 10);
  return {
    now: now(), minutes, step: stepMinutes, names: { '{A1}': 'Principal', '{B2}': 'Secundaria' }, points,
    outages: [
      { id: '{B2}', name: 'Secundaria', health: 'noinet', start: now() - 40 * 60000, end: now() - 33 * 60000 },
      { id: '{B2}', name: 'Secundaria', health: 'nogw', start: now() - 5 * 3600000, end: now() - 5 * 3600000 + 95000 },
    ],
    days: [0, 1, 2, 3].map((count) => ({ day: daysAgo(count), v: { '{A1}': [(40 - count * 7) * 1e9, 1e9], '{B2}': [(6 + count) * 1e9, 2e8] } })),
  };
}
