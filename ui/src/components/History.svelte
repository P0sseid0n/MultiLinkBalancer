<script>
  // Histórico: velocidade e latência por link (1 h, 6 h ou 24 h), quedas e dados por dia.
  import { onMount } from 'svelte';
  import { app, run } from '../lib/store.svelte.js';
  import { rateText, bytes, seriesVar } from '../lib/format.js';
  import LineChart from './LineChart.svelte';
  import WindowControls from './WindowControls.svelte';

  let { onBack } = $props();

  const ranges = [[60, '1 h'], [360, '6 h'], [1440, '24 h']];
  let minutes = $state(60);
  let data = $state(null);
  let loading = $state(false);

  async function load() {
    loading = true;
    try { data = await run('history.get', { minutes }); } catch {}
    loading = false;
  }

  onMount(() => {
    load();
    const t = setInterval(load, 30000);
    return () => clearInterval(t);
  });

  function pick(m) { minutes = m; load(); }

  // a cor segue o link (mesma posição da tela inicial); links que não existem mais vêm depois
  const ids = $derived.by(() => {
    if (!data) return [];
    const order = app.state.links.map((l) => l.id);
    const seen = new Set();
    for (const p of data.points) for (const id in p.v) seen.add(id);
    for (const d of data.days) for (const id in d.v) seen.add(id);
    return [...order.filter((id) => seen.has(id)), ...[...seen].filter((id) => !order.includes(id))];
  });
  const colorOf = (id) => {
    const i = app.state.links.findIndex((l) => l.id === id);
    return seriesVar(i >= 0 ? i : app.state.links.length + ids.indexOf(id));
  };
  const nameOf = (id) => data?.names[id] ?? app.state.links.find((l) => l.id === id)?.name ?? '?';

  const times = $derived(data ? data.points.map((p) => p.t) : []);
  const series = (k) => ids.map((id) => ({
    id, name: nameOf(id), color: colorOf(id),
    values: data.points.map((p) => (p.v[id] && p.v[id][k] >= 0 ? p.v[id][k] : null)),
  }));
  const speed = $derived(data ? series(0) : []);
  const latency = $derived(data ? series(2) : []);

  // resumo por link (também é a forma de ler os números sem passar o mouse)
  const summary = $derived(ids.map((id) => {
    const pts = data.points.filter((p) => p.v[id]);
    const down = pts.map((p) => p.v[id][0]);
    const lat = pts.map((p) => p.v[id][2]).filter((v) => v >= 0);
    const ok = pts.length ? pts.reduce((a, p) => a + p.v[id][3], 0) / pts.length : null;
    return {
      id, name: nameOf(id), color: colorOf(id),
      avg: down.length ? down.reduce((a, b) => a + b, 0) / down.length : 0,
      peak: down.length ? Math.max(...down) : 0,
      lat: lat.length ? lat.reduce((a, b) => a + b, 0) / lat.length : null,
      ok,
    };
  }));

  const mbps = (v, axis) => {
    const m = (v * 8) / 1e6;
    if (axis) return m === 0 ? '0' : m >= 100 ? Math.round(m).toString() : m >= 10 ? m.toFixed(0) : m.toFixed(1).replace('.', ',');
    return rateText(v);
  };
  const ms = (v, axis) => (axis ? Math.round(v).toString() : `${Math.round(v)} ms`);

  const healthText = { noinet: 'Sem internet (operadora)', nogw: 'Modem não responde', unplugged: 'Cabo desconectado', disabled: 'Desativado', nocfg: 'Sem IP/gateway' };
  function when(t) {
    const d = new Date(t);
    const today = new Date().toDateString() === d.toDateString();
    return today ? d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' })
      : d.toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  }
  function duration(o) {
    const s = Math.round(((o.end ?? Date.now()) - o.start) / 1000);
    if (s < 60) return `${s} s`;
    const m = Math.floor(s / 60);
    return m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${m % 60} min`;
  }
  const dayLabel = (d) => {
    const [y, m, dd] = d.split('-');
    const today = new Date();
    const iso = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
    return d === iso ? 'Hoje' : `${dd}/${m}`;
  };
  let allOutages = $state(false);
</script>

<header>
  <button class="icon-btn back" aria-label="Voltar para a tela inicial" title="Voltar" onclick={onBack}>
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
  </button>
  <h1>Histórico</h1>
  <div class="wc"><WindowControls /></div>
</header>

<div class="filters" role="radiogroup" aria-label="Período">
  {#each ranges as [m, text] (m)}
    <button role="radio" aria-checked={minutes === m} class:on={minutes === m} onclick={() => pick(m)}>{text}</button>
  {/each}
</div>

{#if !data}
  <p class="empty">Carregando…</p>
{:else if !data.points.length}
  <p class="empty">Ainda não há dados. O histórico guarda um ponto por minuto enquanto o MultiLink está aberto.</p>
{:else}
  <div class="sections" class:loading>
    <section>
      <h2>Velocidade recebida <span class="unit">Mbps</span></h2>
      <LineChart {times} series={speed} format={mbps} label="Velocidade recebida por link" />
      <ul class="legend">
        {#each summary as s (s.id)}
          <li>
            <span class="key" style="background: {s.color}"></span>
            <span class="name">{s.name}</span>
            <span class="vals">média <strong class="num">{rateText(s.avg)}</strong> · pico <strong class="num">{rateText(s.peak)}</strong></span>
          </li>
        {/each}
      </ul>
    </section>

    <section>
      <h2>Latência <span class="unit">ms</span></h2>
      <LineChart {times} series={latency} format={ms} label="Latência por link" height={110} />
      <ul class="legend">
        {#each summary as s (s.id)}
          <li>
            <span class="key" style="background: {s.color}"></span>
            <span class="name">{s.name}</span>
            <span class="vals">média <strong class="num">{s.lat == null ? '—' : `${Math.round(s.lat)} ms`}</strong>
              · online <strong class="num">{s.ok == null ? '—' : `${Math.round(s.ok * 100)}%`}</strong></span>
          </li>
        {/each}
      </ul>
    </section>
  </div>
{/if}

{#if data}
  <section class="block">
    <h2>Quedas</h2>
    {#if !data.outages.length}
      <p class="muted">Nenhuma queda registrada nos últimos 30 dias.</p>
    {:else}
      <ul class="outages">
        {#each allOutages ? data.outages : data.outages.slice(0, 8) as o (o.id + o.start)}
          <li>
            <span class="dot" class:open={!o.end} aria-hidden="true"></span>
            <span class="what"><strong>{o.name}</strong> <span class="muted">{healthText[o.health] ?? 'Fora do ar'}</span></span>
            <span class="when num">{when(o.start)}</span>
            <span class="dur num">{o.end ? duration(o) : `fora há ${duration(o)}`}</span>
          </li>
        {/each}
      </ul>
      {#if data.outages.length > 8}
        <button class="btn quiet more" onclick={() => (allOutages = !allOutages)}>{allOutages ? 'Mostrar menos' : `Ver todas (${data.outages.length})`}</button>
      {/if}
    {/if}
  </section>

  {#if data.days.length}
    <section class="block">
      <h2>Dados recebidos por dia</h2>
      <div class="table-wrap">
        <table>
          <thead>
            <tr><th scope="col">Dia</th>{#each ids as id (id)}<th scope="col"><span class="key" style="background: {colorOf(id)}"></span>{nameOf(id)}</th>{/each}<th scope="col">Total</th></tr>
          </thead>
          <tbody>
            {#each data.days.slice(0, 14) as d (d.day)}
              <tr>
                <th scope="row">{dayLabel(d.day)}</th>
                {#each ids as id (id)}<td class="num">{d.v[id] ? bytes(d.v[id][0]) : '—'}</td>{/each}
                <td class="num total">{bytes(Object.values(d.v).reduce((a, v) => a + v[0], 0))}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </section>
  {/if}
{/if}

<style>
  header { display: flex; align-items: center; gap: 4px; height: 40px; margin: 0 -8px 12px 0; app-region: drag; }
  .back { margin-left: -12px; color: var(--ink-2); }
  .wc { margin-left: auto; }
  h1 { font-size: 15px; font-weight: 600; }
  h2 { font-size: 13px; font-weight: 600; color: var(--ink-2); margin-bottom: 10px; }
  .unit { font-weight: 400; color: var(--ink-3); margin-left: 4px; }

  .filters { display: inline-flex; gap: 2px; padding: 2px; margin-bottom: 18px; background: var(--surface); border-radius: var(--radius-s); }
  .filters button { border: 0; background: none; padding: 4px 12px; border-radius: 4px; color: var(--ink-2); font-size: 12.5px; cursor: pointer; }
  .filters button:hover { color: var(--ink); }
  .filters button.on { background: var(--surface-3); color: var(--ink); font-weight: 600; }

  .sections { display: grid; gap: 26px; transition: opacity .2s; }
  .sections.loading { opacity: .7; }
  .block { margin-top: 28px; }

  .legend { list-style: none; padding: 0; margin: 10px 0 0; display: grid; gap: 4px; font-size: 12.5px; }
  .legend li { display: flex; align-items: center; gap: 8px; }
  .legend .name { color: var(--ink); }
  .vals { margin-left: auto; color: var(--ink-3); }
  .vals strong { color: var(--ink-soft); font-weight: 600; }
  .key { display: inline-block; width: 12px; height: 2px; border-radius: 1px; flex: none; vertical-align: middle; }
  th .key { margin-right: 6px; }

  .outages { list-style: none; padding: 0; margin: 0; border-top: 1px solid var(--line); }
  .outages li {
    display: grid; grid-template-columns: 8px 1fr auto auto; align-items: center; gap: 10px;
    padding: 8px 0; border-bottom: 1px solid var(--line-soft); font-size: 12.5px;
  }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--dot-neutral); }
  .dot.open { background: var(--critical); }
  .what { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .what strong { font-weight: 600; }
  .when { color: var(--ink-3); }
  .dur { color: var(--ink-soft); min-width: 64px; text-align: right; }
  .more { margin-top: 8px; }

  .table-wrap { overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; }
  th, td { padding: 6px 8px 6px 0; text-align: right; white-space: nowrap; border-bottom: 1px solid var(--line-soft); }
  thead th { color: var(--ink-3); font-weight: 500; border-bottom-color: var(--line); }
  th:first-child { text-align: left; }
  tbody th { font-weight: 500; color: var(--ink-2); }
  td { color: var(--ink-soft); }
  td.total { color: var(--ink); font-weight: 600; }

  .muted { color: var(--ink-3); font-size: 12.5px; }
  .empty { padding: 28px 0; color: var(--ink-3); font-size: 13px; text-align: center; }
</style>
