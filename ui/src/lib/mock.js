// Simulador do app C# para desenvolver a UI no navegador (npm run dev).
// Imita o formato de GetState() e os comandos de AppCore.Handle.

export function createMock(deliver) {
  const now = () => Date.now();
  const links = [
    link('{A1}', 'Principal', 'Intel(R) Ethernet Connection (17) I219-V', '192.168.1.2', '192.168.1.1', 72, 719),
    link('{B2}', 'Secundaria', 'Realtek USB GbE Family Controller', '192.168.100.226', '192.168.100.1', 9, 94),
  ];
  const state = {
    admin: false,
    proxy: { running: true, port: 1080, lan: false, system: true, systemApplied: true },
    settings: { mode: 0, affinity: false, quality: true, autoRoute: false, startup: false, notify: true, notifySound: true, hotkeys: true },
    capture: { on: false, active: false, starting: false, error: null, ipv6: true },
    measuring: false,
    links,
    download: null,
  };
  const log = [];
  let seq = 0;
  let t = 0;

  function link(id, name, description, ip, gateway, weight, measuredMbps) {
    return {
      id, name, description, wired: true, present: true, health: 'ok', healthText: 'Online', diagnosis: '',
      enabled: true, weight, ip, gateway, latency: 4, loss: 0, jitter: 1, measuredMbps, linkSpeed: 1e9,
      rateDown: 0, rateUp: 0, active: 0, total: 0, bytesDown: 0, bytesUp: 0,
      metricRaised: false, userDisabled: false, downSince: null,
    };
  }

  function addLog(text, kind = 'info') {
    const entry = { seq: ++seq, time: new Date().toTimeString().slice(0, 8), kind, text };
    log.push(entry);
    deliver({ type: 'log', entries: [entry] });
  }

  const texts = {
    ok: ['Online', ''],
    noinet: ['Sem internet (operadora)', 'sem internet. O modem responde, então o problema é na operadora'],
    disabled: ['Desativado no Windows', 'adaptador desativado no Windows'],
  };

  function setHealth(l, h) {
    const prev = l.health;
    l.health = h;
    [l.healthText, l.diagnosis] = texts[h];
    if (h !== 'ok' && prev === 'ok') {
      l.downSince = now();
      if (!l.userDisabled) addLog(`${l.name}: ${l.diagnosis}. Tráfego seguindo por ${links.filter((x) => x !== l && x.health === 'ok').map((x) => x.name).join(', ')}.`, 'down');
    }
    if (h === 'ok' && prev !== 'ok') {
      l.downSince = null;
      l.userDisabled = false;
      addLog(`${l.name} voltou a funcionar.`, 'up');
    }
    l.present = h !== 'disabled';
  }

  // tráfego simulado: rajadas de download divididas pelos pesos
  setInterval(() => {
    t++;
    const busy = state.proxy.running && (t % 30) < 22;
    const total = busy ? (380 + 240 * Math.sin(t / 5) + Math.random() * 60) * 1e6 / 8 : 0;
    // a cada ~45 s a Secundaria perde a internet por 12 s
    const sec = links[1];
    if (sec.health !== 'disabled') setHealth(sec, (t % 45) > 32 ? 'noinet' : 'ok');
    const up = links.filter((l) => l.health === 'ok' && l.enabled && l.weight > 0);
    const wsum = up.reduce((s, l) => s + l.weight, 0) || 1;
    for (const l of links) {
      const share = up.includes(l) ? l.weight / wsum : 0;
      const capped = Math.min(total * share, (l.measuredMbps * 1e6) / 8);
      l.rateDown = Math.round(capped * (0.9 + Math.random() * 0.2));
      l.rateUp = Math.round(l.rateDown * 0.03);
      l.active = busy && share ? Math.round(20 * share + Math.random() * 4) : 0;
      l.bytesDown += l.rateDown;
      l.bytesUp += l.rateUp;
      l.total += l.active ? 1 : 0;
      l.latency = l.health === 'ok' ? 3 + Math.round(Math.random() * 3) : -1;
      // a Principal fica instável (perda alta) por um trecho de cada ciclo
      l.loss = l.health !== 'ok' ? -1 : l === links[0] && (t % 60) > 40 ? 12 + Math.round(Math.random() * 6) : Math.round(Math.random());
      l.jitter = l.health !== 'ok' ? -1 : l.loss > 5 ? 18 + Math.round(Math.random() * 10) : 1 + Math.round(Math.random() * 2);
    }
    tickDownload();
    deliver({ type: 'state', data: structuredClone(state) });
  }, 1000);

  let dlTimer = null;
  function tickDownload() {
    const d = state.download;
    if (!d || d.state !== 'running') return;
    const parts = links.filter((l) => l.health === 'ok');
    d.rate = 0;
    for (const p of d.perLink) {
      const l = links.find((x) => x.id === p.id);
      p.rate = parts.includes(l) ? (l.measuredMbps * 1e6) / 8 * 0.85 : 0;
      p.bytes += p.rate;
      d.rate += p.rate;
    }
    d.done = Math.min(d.total, d.done + d.rate);
    if (d.done >= d.total) {
      d.state = 'done';
      d.avgMbps = 690;
      d.rate = 0;
      d.perLink.forEach((p) => (p.rate = 0));
      addLog(`Download concluído: ${d.name}`, 'up');
    }
  }

  const handlers = {
    'ui.ready': () => ({ state: structuredClone(state), log }),
    'proxy.start': () => { state.proxy.running = true; addLog('Proxy iniciado em 127.0.0.1:1080 (SOCKS4/SOCKS5/HTTP)'); },
    'proxy.stop': () => { state.proxy.running = false; state.proxy.systemApplied = false; addLog('Proxy parado.'); },
    'settings.set': ({ key, value }) => {
      if (key === 'port') {
        if (state.proxy.running) throw new Error('Pare o proxy antes de mudar a porta.');
        state.proxy.port = Number(value);
      } else if (key === 'lan' || key === 'system') state.proxy[key] = value;
      else if (key === 'capture') { state.capture.on = value; state.capture.active = value && state.admin; if (value && !state.admin) return { needsAdmin: true }; }
      else state.settings[key] = value;
      if (key === 'autoRoute') return { needsAdmin: value && !state.admin };
    },
    'link.set': ({ id, enabled, weight }) => {
      const l = links.find((x) => x.id === id);
      if (enabled !== undefined) l.enabled = enabled;
      if (weight !== undefined) l.weight = weight;
    },
    'link.toggleAdapter': async ({ id }) => {
      const l = links.find((x) => x.id === id);
      await new Promise((r) => setTimeout(r, 900));
      if (l.health === 'disabled') { setHealth(l, 'ok'); return { enabled: true }; }
      l.userDisabled = true;
      setHealth(l, 'disabled');
      addLog(`[${l.name}] adaptador desativado no Windows`);
      return { enabled: false };
    },
    'links.test': () => {},
    'adapters.refresh': () => {},
    measure: async () => {
      state.measuring = true;
      addLog('Medindo a velocidade de 2 link(s) em paralelo (cerca de 8 s)...');
      await new Promise((r) => setTimeout(r, 3000));
      state.measuring = false;
      addLog('[Principal] 712 Mbps, peso 71');
      links[0].measuredMbps = 712;
      links[0].weight = 71;
    },
    'download.start': ({ url }) => {
      if (!/^https?:\/\//.test(url || '')) throw new Error('Informe um link que comece com http:// ou https://');
      const name = url.split('/').pop() || 'download.bin';
      state.download = {
        state: 'running', name, path: `C:\\Users\\voce\\Downloads\\${name}`, message: null, total: 1.4e9, done: 0, rate: 0, avgMbps: 0,
        perLink: links.map((l) => ({ id: l.id, name: l.name, bytes: 0, rate: 0 })),
      };
      addLog(`Download acelerado: ${name} por 2 link(s)`);
    },
    'download.cancel': () => { state.download.state = 'canceled'; state.download.message = 'Download cancelado.'; },
    'download.reveal': () => {},
    'download.clear': () => { state.download = null; },
    'history.get': ({ minutes }) => {
      const step = minutes <= 60 ? 1 : minutes <= 360 ? 5 : 15;
      const end = Math.floor(now() / 60000) * 60000;
      const points = [];
      for (let t = end - minutes * 60000; t < end; t += step * 60000) {
        const k = t / 60000;
        const busy = 0.5 + 0.5 * Math.sin(k / 9);
        const out = k % 97 > 90;
        points.push({ t, v: {
          '{A1}': [Math.round(busy * 52e6 * (0.8 + Math.random() * 0.4)), 1e6, 4 + Math.random() * 3, 1],
          '{B2}': out ? [0, 0, -1, 0] : [Math.round(busy * 7e6 * (0.8 + Math.random() * 0.4)), 2e5, 18 + Math.random() * 10, 1],
        } });
      }
      const day = (n) => new Date(now() - n * 864e5).toISOString().slice(0, 10);
      return {
        now: now(), minutes, step, names: { '{A1}': 'Principal', '{B2}': 'Secundaria' }, points,
        outages: [
          { id: '{B2}', name: 'Secundaria', health: 'noinet', start: now() - 40 * 60000, end: now() - 33 * 60000 },
          { id: '{B2}', name: 'Secundaria', health: 'nogw', start: now() - 5 * 3600000, end: now() - 5 * 3600000 + 95000 },
        ],
        days: [0, 1, 2, 3].map((n) => ({ day: day(n), v: { '{A1}': [(40 - n * 7) * 1e9, 1e9], '{B2}': [(6 + n) * 1e9, 2e8] } })),
      };
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
        const fn = handlers[cmd];
        if (!fn) throw new Error('Comando desconhecido: ' + cmd);
        const result = await fn(args);
        deliver({ type: 'reply', id, ok: true, result: result ?? null });
      } catch (e) {
        deliver({ type: 'reply', id, ok: false, error: e.message });
      }
      deliver({ type: 'state', data: structuredClone(state) });
    },
  };
}
