<script>
  // Histórico: velocidade, latência e perda de pacotes por link (1 h, 6 h ou 24 h), quedas e dados por dia.
  // Um gráfico por vez, escolhido nas abas; quedas e dias mostram só os mais recentes até pedir o resto.
  import { onMount } from 'svelte';
  import { app, run } from '../../state/app.svelte.js';
  import { rateText, mbpsAxis, bytes, seriesColor, dateTime, duration, dayLabel } from '../../lib/format.js';
  import { outageLabel } from '../../lib/health.js';
  import PageHeader from '../../components/PageHeader.svelte';
  import LineChart from '../../components/LineChart.svelte';
  import EmptyState from '../../components/EmptyState.svelte';

  let { onBack } = $props();

  const RANGES = [[60, '1 h'], [360, '6 h'], [1440, '24 h']]; // [minutos, rótulo]
  const REFRESH_MS = 30000;
  const OUTAGES_SHOWN = 3, DAYS_SHOWN = 4, DAYS_KEPT = 14;
  // posição de cada valor nos pontos do app (UsageHistory): [recebido B/s, enviado B/s, latência ms, fração online, perda %]
  const RECEIVED = 0, LATENCY = 2, ONLINE = 3, LOSS = 4;

  let minutes = $state(60);
  let data = $state(null);
  let loading = $state(false);
  let metric = $state('speed'); // aba do gráfico: 'speed' | 'latency' | 'loss'
  let showAllOutages = $state(false);
  let showAllDays = $state(false);

  async function load() {
    loading = true;
    try { data = await run('history.get', { minutes }); } catch {}
    loading = false;
  }

  onMount(() => {
    load();
    const timer = setInterval(load, REFRESH_MS);
    return () => clearInterval(timer);
  });

  function selectRange(rangeMinutes) {
    minutes = rangeMinutes;
    load();
  }

  // a cor segue o link (mesma posição da tela inicial); links que não existem mais vêm depois
  const linkIds = $derived.by(() => {
    if (!data) return [];
    const currentOrder = app.state.links.map((link) => link.id);
    const seen = new Set();
    for (const point of data.points) for (const id in point.v) seen.add(id);
    for (const day of data.days) for (const id in day.v) seen.add(id);
    return [...currentOrder.filter((id) => seen.has(id)), ...[...seen].filter((id) => !currentOrder.includes(id))];
  });
  const colorOf = (id) => {
    const index = app.state.links.findIndex((link) => link.id === id);
    return seriesColor(index >= 0 ? index : app.state.links.length + linkIds.indexOf(id));
  };
  const nameOf = (id) => data?.names[id] ?? app.state.links.find((link) => link.id === id)?.name ?? '?';

  const times = $derived(data ? data.points.map((point) => point.t) : []);
  const seriesFor = (valueIndex) => linkIds.map((id) => ({
    id, name: nameOf(id), color: colorOf(id),
    values: data.points.map((point) => (point.v[id] && point.v[id][valueIndex] >= 0 ? point.v[id][valueIndex] : null)),
  }));
  const speedSeries = $derived(data ? seriesFor(RECEIVED) : []);
  const latencySeries = $derived(data ? seriesFor(LATENCY) : []);
  const lossSeries = $derived(data ? seriesFor(LOSS) : []);

  const average = (values) => values.reduce((sum, value) => sum + value, 0) / values.length;

  // resumo por link (também é a forma de ler os números sem passar o mouse)
  const summary = $derived(linkIds.map((id) => {
    const points = data.points.filter((point) => point.v[id]);
    const received = points.map((point) => point.v[id][RECEIVED]);
    const latencies = points.map((point) => point.v[id][LATENCY]).filter((value) => value >= 0);
    const losses = points.map((point) => point.v[id][LOSS]).filter((value) => value >= 0); // pontos antigos não têm perda
    return {
      id, name: nameOf(id), color: colorOf(id),
      averageRate: received.length ? average(received) : 0,
      peakRate: received.length ? Math.max(...received) : 0,
      averageLatency: latencies.length ? average(latencies) : null,
      online: points.length ? average(points.map((point) => point.v[id][ONLINE])) : null,
      averageLoss: losses.length ? average(losses) : null,
      peakLoss: losses.length ? Math.max(...losses) : null,
    };
  }));

  const formatSpeed = (value, isAxis) => (isAxis ? mbpsAxis(value) : rateText(value));
  const formatLatency = (value, isAxis) => (isAxis ? Math.round(value).toString() : `${Math.round(value)} ms`);
  const lossText = (value) => `${value < 10 ? value.toFixed(1).replace('.', ',') : Math.round(value)}%`;
  const formatLoss = (value, isAxis) => (isAxis ? String(+value.toFixed(2)).replace('.', ',') : lossText(value));
  const outageDuration = (outage) => duration(outage.start, outage.end ?? Date.now());
  const dayTotal = (day) => Object.values(day.v).reduce((sum, values) => sum + values[0], 0);

  const dash = '—';
  const ms = (value) => (value == null ? dash : `${Math.round(value)} ms`);
  const pct = (value) => (value == null ? dash : lossText(value));
  // cada aba: gráfico e os dois números da legenda de cada link
  const METRICS = $derived({
    speed: {
      tab: 'Velocidade', title: 'Velocidade recebida', series: speedSeries, format: formatSpeed,
      values: (row) => [['média', rateText(row.averageRate)], ['pico', rateText(row.peakRate)]],
    },
    latency: {
      tab: 'Latência', title: 'Latência', series: latencySeries, format: formatLatency,
      values: (row) => [['média', ms(row.averageLatency)], ['online', row.online == null ? dash : `${Math.round(row.online * 100)}%`]],
    },
    loss: {
      tab: 'Perda', title: 'Perda de pacotes', series: lossSeries, format: formatLoss,
      values: (row) => [['média', pct(row.averageLoss)], ['pico', pct(row.peakLoss)]],
    },
  });
  const current = $derived(METRICS[metric]);
  const daysShown = $derived(data ? data.days.slice(0, showAllDays ? DAYS_KEPT : DAYS_SHOWN) : []);
</script>

<PageHeader title="Histórico" {onBack} />

<div class="filters" role="radiogroup" aria-label="Período">
  {#each RANGES as [rangeMinutes, rangeLabel] (rangeMinutes)}
    <button role="radio" aria-checked={minutes === rangeMinutes} class:on={minutes === rangeMinutes} onclick={() => selectRange(rangeMinutes)}>{rangeLabel}</button>
  {/each}
</div>

{#if !data}
  <!-- esqueleto com a forma das abas, do gráfico e da legenda -->
  <div class="skeleton" role="status" aria-label="Carregando o histórico">
    <span class="bone tabs-line"></span>
    <span class="bone" style:height="150px"></span>
    <span class="bone legend-line"></span>
    <span class="bone legend-line short"></span>
  </div>
{:else if !data.points.length}
  <EmptyState title="Ainda sem histórico">
    {#snippet icon()}<path d="M3 3v18h18" /><path d="m7 15 4-5 3 3 5-7" />{/snippet}
    O histórico guarda um ponto por minuto enquanto o MultiLink está aberto. Volte daqui a alguns minutos.
  </EmptyState>
{:else}
  <section class="chart" class:loading>
    <div class="tabs" role="tablist" aria-label="Gráfico">
      {#each Object.entries(METRICS) as [key, item] (key)}
        <button role="tab" id="tab-{key}" aria-selected={metric === key} aria-controls="chart-panel" class:on={metric === key}
          onclick={() => (metric = key)}>{item.tab}</button>
      {/each}
    </div>
    <div id="chart-panel" role="tabpanel" aria-labelledby="tab-{metric}">
      {#key metric}
        <div class="enter">
          <LineChart {times} series={current.series} format={current.format} label="{current.title} por link" height={150} />
        </div>
      {/key}
      <ul class="legend">
        {#each summary as row (row.id)}
          <li>
            <span class="key" style="background: {row.color}"></span>
            <span class="name">{row.name}</span>
            <span class="values">
              {#each current.values(row) as [caption, value], index (caption)}{#if index}{' · '}{/if}{caption} <strong class="num">{value}</strong>{/each}
            </span>
          </li>
        {/each}
      </ul>
    </div>
  </section>
{/if}

{#if data}
  <section class="block">
    <h2>Quedas {#if data.outages.length}<span class="count">{data.outages.length} nos últimos 30 dias</span>{/if}</h2>
    {#if !data.outages.length}
      <p class="muted">Nenhuma queda registrada nos últimos 30 dias.</p>
    {:else}
      <ul class="outages">
        {#each showAllOutages ? data.outages : data.outages.slice(0, OUTAGES_SHOWN) as outage (outage.id + outage.start)}
          <li>
            <span class="dot" class:open={!outage.end} aria-hidden="true"></span>
            <span class="what"><strong>{outage.name}</strong> <span class="muted">{outageLabel[outage.health] ?? 'Fora do ar'}</span></span>
            <span class="when num">{dateTime(outage.start)}</span>
            <span class="duration num">{outage.end ? outageDuration(outage) : `fora há ${outageDuration(outage)}`}</span>
          </li>
        {/each}
      </ul>
      {#if data.outages.length > OUTAGES_SHOWN}
        <button class="btn quiet more" onclick={() => (showAllOutages = !showAllOutages)}>{showAllOutages ? 'Mostrar menos' : `Ver todas (${data.outages.length})`}</button>
      {/if}
    {/if}
  </section>
{/if}

{#if data?.days.length}
  <section class="block">
    <h2>Dados recebidos por dia</h2>
    <div class="table-wrap">
      <table>
        <thead>
          <tr><th scope="col">Dia</th>{#each linkIds as id (id)}<th scope="col"><span class="key" style="background: {colorOf(id)}"></span>{nameOf(id)}</th>{/each}<th scope="col">Total</th></tr>
        </thead>
        <tbody>
          {#each daysShown as day (day.day)}
            <tr>
              <th scope="row">{dayLabel(day.day)}</th>
              {#each linkIds as id (id)}<td class="num">{day.v[id] ? bytes(day.v[id][0]) : dash}</td>{/each}
              <td class="num total">{bytes(dayTotal(day))}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    {#if data.days.length > DAYS_SHOWN}
      <button class="btn quiet more" onclick={() => (showAllDays = !showAllDays)}>{showAllDays ? 'Mostrar menos' : `Ver ${Math.min(data.days.length, DAYS_KEPT)} dias`}</button>
    {/if}
  </section>
{/if}

<style>
  /* mesmo ritmo dos Ajustes: seções bem separadas, título e uma linha antes do conteúdo */
  h2 { font-size: 14px; font-weight: 600; color: var(--ink-soft); margin-bottom: 4px; padding-bottom: 14px; border-bottom: 1px solid var(--line); }
  .count { font-weight: 400; font-size: 12.5px; color: var(--ink-3); margin-left: 6px; }

  .filters { display: inline-flex; gap: 2px; padding: 2px; margin-bottom: 22px; background: var(--surface); border-radius: var(--radius-s); }
  .filters button {
    border: 0; background: none; padding: 4px 12px; border-radius: 4px; color: var(--ink-2); font-size: 12.5px; cursor: pointer;
    transition: background-color .18s, color .15s, transform .12s var(--ease-out);
  }
  .filters button:hover { color: var(--ink); background: var(--surface-2); }
  .filters button:active { transform: var(--press); }
  .filters button.on { background: var(--surface-3); color: var(--ink); font-weight: 600; }

  /* abas do gráfico: texto com sublinhado, para não competir com o seletor de período acima */
  .tabs { display: flex; gap: 20px; margin-bottom: 16px; border-bottom: 1px solid var(--line); }
  .tabs button {
    position: relative; padding: 0 0 12px; border: 0; background: none;
    color: var(--ink-3); font-size: 14px; font-weight: 600; cursor: pointer;
    transition: color .15s;
  }
  .tabs button::after {
    content: ""; position: absolute; left: 0; right: 0; bottom: -1px; height: 2px; border-radius: 1px;
    background: var(--ink); transform: scaleX(0); transition: transform .22s var(--ease-out);
  }
  .tabs button:hover { color: var(--ink-soft); }
  .tabs button.on { color: var(--ink); }
  .tabs button.on::after { transform: scaleX(1); }
  .tabs button:focus-visible { outline-offset: 4px; }

  .chart { transition: opacity .2s; }
  .chart.loading { opacity: .7; }
  .block { margin-top: 44px; }

  .legend { list-style: none; padding: 0; margin: 14px 0 0; display: grid; gap: 8px; font-size: 12.5px; }
  .legend li { display: flex; align-items: center; gap: 8px; }
  .legend .name { color: var(--ink); }
  .values { margin-left: auto; color: var(--ink-3); }
  .values strong { color: var(--ink-soft); font-weight: 600; }
  .key { display: inline-block; width: 12px; height: 2px; border-radius: 1px; flex: none; vertical-align: middle; }
  th .key { margin-right: 6px; }

  .outages { list-style: none; padding: 0; margin: 0; }
  .outages li {
    display: grid; grid-template-columns: 8px 1fr auto auto; align-items: center; gap: 10px;
    padding: 10px 0; border-bottom: 1px solid var(--line-soft); font-size: 12.5px;
  }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--dot-neutral); }
  .dot.open { background: var(--critical); }
  .what { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .what strong { font-weight: 600; }
  .when { color: var(--ink-3); }
  .duration { color: var(--ink-soft); min-width: 64px; text-align: right; }
  .more { margin-top: 10px; margin-left: -14px; }

  .table-wrap { overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; }
  th, td { padding: 9px 8px 9px 0; text-align: right; white-space: nowrap; border-bottom: 1px solid var(--line-soft); }
  thead th { color: var(--ink-3); font-weight: 500; border-bottom-color: var(--line); }
  th:first-child { text-align: left; }
  tbody th { font-weight: 500; color: var(--ink-2); }
  td { color: var(--ink-soft); }
  td.total { color: var(--ink); font-weight: 600; }

  .muted { color: var(--ink-3); font-size: 12.5px; }

  .skeleton { display: grid; gap: 12px; }
  .skeleton .tabs-line { width: 220px; height: 16px; margin-bottom: 8px; }
  .skeleton .legend-line { height: 12px; }
  .skeleton .legend-line.short { width: 70%; }
</style>
