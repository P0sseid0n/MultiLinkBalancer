<script>
  // Histórico: velocidade e latência por link (1 h, 6 h ou 24 h), quedas e dados por dia.
  import { onMount } from 'svelte';
  import { app, run } from '../../state/app.svelte.js';
  import { rateText, mbpsAxis, bytes, seriesColor, dateTime, duration, dayLabel } from '../../lib/format.js';
  import { outageLabel } from '../../lib/health.js';
  import PageHeader from '../../components/PageHeader.svelte';
  import LineChart from '../../components/LineChart.svelte';

  let { onBack } = $props();

  const RANGES = [[60, '1 h'], [360, '6 h'], [1440, '24 h']]; // [minutos, rótulo]
  const REFRESH_MS = 30000;
  const OUTAGES_SHOWN = 8, DAYS_SHOWN = 14;
  // posição de cada valor nos pontos do app (UsageHistory): [recebido B/s, enviado B/s, latência ms, fração online]
  const RECEIVED = 0, LATENCY = 2, ONLINE = 3;

  let minutes = $state(60);
  let data = $state(null);
  let loading = $state(false);
  let showAllOutages = $state(false);

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

  const average = (values) => values.reduce((sum, value) => sum + value, 0) / values.length;

  // resumo por link (também é a forma de ler os números sem passar o mouse)
  const summary = $derived(linkIds.map((id) => {
    const points = data.points.filter((point) => point.v[id]);
    const received = points.map((point) => point.v[id][RECEIVED]);
    const latencies = points.map((point) => point.v[id][LATENCY]).filter((value) => value >= 0);
    return {
      id, name: nameOf(id), color: colorOf(id),
      averageRate: received.length ? average(received) : 0,
      peakRate: received.length ? Math.max(...received) : 0,
      averageLatency: latencies.length ? average(latencies) : null,
      online: points.length ? average(points.map((point) => point.v[id][ONLINE])) : null,
    };
  }));

  const formatSpeed = (value, isAxis) => (isAxis ? mbpsAxis(value) : rateText(value));
  const formatLatency = (value, isAxis) => (isAxis ? Math.round(value).toString() : `${Math.round(value)} ms`);
  const outageDuration = (outage) => duration(outage.start, outage.end ?? Date.now());
  const dayTotal = (day) => Object.values(day.v).reduce((sum, values) => sum + values[0], 0);
</script>

<PageHeader title="Histórico" {onBack} />

<div class="filters" role="radiogroup" aria-label="Período">
  {#each RANGES as [rangeMinutes, rangeLabel] (rangeMinutes)}
    <button role="radio" aria-checked={minutes === rangeMinutes} class:on={minutes === rangeMinutes} onclick={() => selectRange(rangeMinutes)}>{rangeLabel}</button>
  {/each}
</div>

{#if !data}
  <p class="empty">Carregando…</p>
{:else if !data.points.length}
  <p class="empty">Ainda não há dados. O histórico guarda um ponto por minuto enquanto o MultiLink está aberto.</p>
{:else}
  <div class="charts" class:loading>
    <section>
      <h2>Velocidade recebida <span class="unit">Mbps</span></h2>
      <LineChart {times} series={speedSeries} format={formatSpeed} label="Velocidade recebida por link" />
      <ul class="legend">
        {#each summary as row (row.id)}
          <li>
            <span class="key" style="background: {row.color}"></span>
            <span class="name">{row.name}</span>
            <span class="values">média <strong class="num">{rateText(row.averageRate)}</strong> · pico <strong class="num">{rateText(row.peakRate)}</strong></span>
          </li>
        {/each}
      </ul>
    </section>

    <section>
      <h2>Latência <span class="unit">ms</span></h2>
      <LineChart {times} series={latencySeries} format={formatLatency} label="Latência por link" height={110} />
      <ul class="legend">
        {#each summary as row (row.id)}
          <li>
            <span class="key" style="background: {row.color}"></span>
            <span class="name">{row.name}</span>
            <span class="values">média <strong class="num">{row.averageLatency == null ? '—' : `${Math.round(row.averageLatency)} ms`}</strong>
              · online <strong class="num">{row.online == null ? '—' : `${Math.round(row.online * 100)}%`}</strong></span>
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

  {#if data.days.length}
    <section class="block">
      <h2>Dados recebidos por dia</h2>
      <div class="table-wrap">
        <table>
          <thead>
            <tr><th scope="col">Dia</th>{#each linkIds as id (id)}<th scope="col"><span class="key" style="background: {colorOf(id)}"></span>{nameOf(id)}</th>{/each}<th scope="col">Total</th></tr>
          </thead>
          <tbody>
            {#each data.days.slice(0, DAYS_SHOWN) as day (day.day)}
              <tr>
                <th scope="row">{dayLabel(day.day)}</th>
                {#each linkIds as id (id)}<td class="num">{day.v[id] ? bytes(day.v[id][0]) : '—'}</td>{/each}
                <td class="num total">{bytes(dayTotal(day))}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </section>
  {/if}
{/if}

<style>
  h2 { font-size: 13px; font-weight: 600; color: var(--ink-2); margin-bottom: 10px; }
  .unit { font-weight: 400; color: var(--ink-3); margin-left: 4px; }

  .filters { display: inline-flex; gap: 2px; padding: 2px; margin-bottom: 18px; background: var(--surface); border-radius: var(--radius-s); }
  .filters button { border: 0; background: none; padding: 4px 12px; border-radius: 4px; color: var(--ink-2); font-size: 12.5px; cursor: pointer; }
  .filters button:hover { color: var(--ink); }
  .filters button.on { background: var(--surface-3); color: var(--ink); font-weight: 600; }

  .charts { display: grid; gap: 26px; transition: opacity .2s; }
  .charts.loading { opacity: .7; }
  .block { margin-top: 28px; }

  .legend { list-style: none; padding: 0; margin: 10px 0 0; display: grid; gap: 4px; font-size: 12.5px; }
  .legend li { display: flex; align-items: center; gap: 8px; }
  .legend .name { color: var(--ink); }
  .values { margin-left: auto; color: var(--ink-3); }
  .values strong { color: var(--ink-soft); font-weight: 600; }
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
  .duration { color: var(--ink-soft); min-width: 64px; text-align: right; }
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
