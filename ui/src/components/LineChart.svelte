<script>
  // Gráfico de linhas com um único eixo Y, uma linha por link (cor do link) e mira que
  // segue o ponteiro: o quadro lista o valor de todos os links no instante apontado.
  // series: [{ id, name, color, values: (number|null)[] }]  (null = sem dado, a linha é interrompida)
  let { times, series, format, label, height = 132 } = $props();

  let width = $state(0);
  let hover = $state(null); // índice do ponto sob a mira

  const pad = { l: 44, r: 8, t: 8, b: 20 };
  const w = $derived(Math.max(0, width - pad.l - pad.r));
  const h = $derived(height - pad.t - pad.b);

  const max = $derived.by(() => {
    let m = 0;
    for (const s of series) for (const v of s.values) if (v != null && v > m) m = v;
    return niceMax(m);
  });

  function niceMax(m) {
    if (m <= 0) return 1;
    const p = Math.pow(10, Math.floor(Math.log10(m)));
    for (const f of [1, 2, 2.5, 5, 10]) if (m <= f * p) return f * p;
    return 10 * p;
  }

  const x = (i) => pad.l + (times.length < 2 ? w / 2 : (i / (times.length - 1)) * w);
  const y = (v) => pad.t + h - (v / max) * h;

  function path(values) {
    let d = '';
    let pen = false;
    values.forEach((v, i) => {
      if (v == null) { pen = false; return; }
      d += `${pen ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`;
      pen = true;
    });
    return d;
  }

  const ticks = $derived([0, max / 2, max]);
  const hhmm = (t) => new Date(t).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  const xLabels = $derived(times.length ? [0, Math.floor((times.length - 1) / 2), times.length - 1] : []);

  function move(e) {
    if (!times.length || !w) return;
    const r = e.currentTarget.getBoundingClientRect();
    const px = e.clientX - r.left - pad.l;
    hover = Math.max(0, Math.min(times.length - 1, Math.round((px / w) * (times.length - 1))));
  }

  function key(e) {
    if (!times.length) return;
    if (e.key === 'ArrowLeft') hover = Math.max(0, (hover ?? times.length) - 1);
    else if (e.key === 'ArrowRight') hover = Math.min(times.length - 1, (hover ?? -1) + 1);
    else if (e.key === 'Escape') hover = null;
    else return;
    e.preventDefault();
  }

  const tipLeft = $derived(hover == null ? 0 : x(hover) > width / 2 ? x(hover) - 8 : x(hover) + 8);
</script>

<div class="chart" bind:clientWidth={width}>
  <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
  <svg {width} {height} role="img" aria-label={label} tabindex="0"
    onpointermove={move} onpointerleave={() => (hover = null)} onkeydown={key} onblur={() => (hover = null)}>
    {#each ticks as t, i (i)}
      <line class="grid" class:base={i === 0} x1={pad.l} x2={pad.l + w} y1={y(t)} y2={y(t)} />
      <text class="axis" x={pad.l - 6} y={y(t)} dy="0.32em" text-anchor="end">{format(t, true)}</text>
    {/each}
    {#each xLabels as i, k (k)}
      <text class="axis" x={x(i)} y={height - 4} text-anchor={k === 0 ? 'start' : k === 2 ? 'end' : 'middle'}>{hhmm(times[i])}</text>
    {/each}
    {#each series as s (s.id)}
      <path d={path(s.values)} stroke={s.color} />
    {/each}
    {#if hover != null}
      <line class="cross" x1={x(hover)} x2={x(hover)} y1={pad.t} y2={pad.t + h} />
      {#each series as s (s.id)}
        {#if s.values[hover] != null}
          <circle cx={x(hover)} cy={y(s.values[hover])} r="4" fill={s.color} />
        {/if}
      {/each}
    {/if}
  </svg>

  {#if hover != null}
    <div class="tip" class:left={x(hover) > width / 2} style="left: {tipLeft}px">
      <span class="when">{hhmm(times[hover])}</span>
      {#each series as s (s.id)}
        <span class="row">
          <span class="key" style="background: {s.color}"></span>
          <strong class="num">{s.values[hover] == null ? '—' : format(s.values[hover])}</strong>
          <span class="name">{s.name}</span>
        </span>
      {/each}
    </div>
  {/if}
</div>

<style>
  .chart { position: relative; }
  svg { display: block; overflow: visible; touch-action: none; }
  svg:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; border-radius: 4px; }
  .grid { stroke: var(--line); stroke-width: 1; }
  .grid.base { stroke: var(--line-strong); }
  .axis { fill: var(--ink-3); font-size: 10.5px; font-variant-numeric: tabular-nums; }
  path { fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
  .cross { stroke: var(--ink-4); stroke-width: 1; }
  circle { stroke: var(--page); stroke-width: 2; }
  .tip {
    position: absolute; top: 0; z-index: 2; pointer-events: none;
    display: grid; gap: 3px; padding: 7px 9px; min-width: 120px;
    background: var(--surface-2); border: 1px solid var(--surface-3); border-radius: var(--radius-s);
    box-shadow: var(--shadow-pop); font-size: 12px;
  }
  .tip.left { transform: translateX(-100%); }
  .when { color: var(--ink-3); font-size: 11px; }
  .row { display: flex; align-items: center; gap: 6px; white-space: nowrap; }
  .key { width: 10px; height: 2px; border-radius: 1px; flex: none; }
  .row strong { color: var(--ink); font-weight: 600; }
  .name { color: var(--ink-2); }
</style>
