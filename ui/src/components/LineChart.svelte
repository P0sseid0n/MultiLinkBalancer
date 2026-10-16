<script>
  // Gráfico de linhas com um único eixo Y, uma linha por série (cor da série) e mira que
  // segue o ponteiro: o quadro lista o valor de todas as séries no instante apontado.
  // series: [{ id, name, color, values: (number|null)[] }]  (null = sem dado, a linha é interrompida)
  // format(value, isAxis): texto de um valor no eixo (curto) ou no quadro (com unidade)
  import { clock } from '../lib/format.js';

  let { times, series, format, label, height = 132 } = $props();

  const padding = { left: 44, right: 8, top: 8, bottom: 20 };

  let width = $state(0);
  let hoverIndex = $state(null); // ponto sob a mira

  const plotWidth = $derived(Math.max(0, width - padding.left - padding.right));
  const plotHeight = $derived(height - padding.top - padding.bottom);

  const maxValue = $derived.by(() => {
    let highest = 0;
    for (const line of series) for (const value of line.values) if (value != null && value > highest) highest = value;
    return roundUp(highest);
  });

  // topo do eixo num valor redondo: 1, 2, 2,5 ou 5 vezes uma potência de 10
  function roundUp(value) {
    if (value <= 0) return 1;
    const magnitude = Math.pow(10, Math.floor(Math.log10(value)));
    for (const factor of [1, 2, 2.5, 5, 10]) if (value <= factor * magnitude) return factor * magnitude;
    return 10 * magnitude;
  }

  const xAt = (index) => padding.left + (times.length < 2 ? plotWidth / 2 : (index / (times.length - 1)) * plotWidth);
  const yAt = (value) => padding.top + plotHeight - (value / maxValue) * plotHeight;

  function linePath(values) {
    let path = '';
    let drawing = false;
    values.forEach((value, index) => {
      if (value == null) { drawing = false; return; }
      path += `${drawing ? 'L' : 'M'}${xAt(index).toFixed(1)},${yAt(value).toFixed(1)}`;
      drawing = true;
    });
    return path;
  }

  const yTicks = $derived([0, maxValue / 2, maxValue]);
  const xLabelIndexes = $derived(times.length ? [0, Math.floor((times.length - 1) / 2), times.length - 1] : []);

  function onPointerMove(event) {
    if (!times.length || !plotWidth) return;
    const box = event.currentTarget.getBoundingClientRect();
    const offset = event.clientX - box.left - padding.left;
    hoverIndex = Math.max(0, Math.min(times.length - 1, Math.round((offset / plotWidth) * (times.length - 1))));
  }

  function onKeyDown(event) {
    if (!times.length) return;
    if (event.key === 'ArrowLeft') hoverIndex = Math.max(0, (hoverIndex ?? times.length) - 1);
    else if (event.key === 'ArrowRight') hoverIndex = Math.min(times.length - 1, (hoverIndex ?? -1) + 1);
    else if (event.key === 'Escape') hoverIndex = null;
    else return;
    event.preventDefault();
  }

  // o gráfico é um controle deslizante pelos instantes: o leitor de tela anuncia o ponto escolhido
  const selectedText = $derived(
    hoverIndex == null ? 'Use as setas para ver os valores'
    : `${clock(times[hoverIndex])}: ` + series.map((line) =>
        `${line.name} ${line.values[hoverIndex] == null ? 'sem dado' : format(line.values[hoverIndex])}`).join(', '),
  );

  // o quadro fica do lado com mais espaço
  const tooltipOnLeft = $derived(hoverIndex != null && xAt(hoverIndex) > width / 2);
  const tooltipX = $derived(hoverIndex == null ? 0 : tooltipOnLeft ? xAt(hoverIndex) - 8 : xAt(hoverIndex) + 8);
</script>

<div class="chart" bind:clientWidth={width}>
  <svg {width} {height} role="slider" aria-label={label} tabindex="0"
    aria-valuemin={0} aria-valuemax={Math.max(0, times.length - 1)} aria-valuenow={hoverIndex ?? 0} aria-valuetext={selectedText}
    onpointermove={onPointerMove} onpointerleave={() => (hoverIndex = null)} onkeydown={onKeyDown} onblur={() => (hoverIndex = null)}>
    {#each yTicks as tick, index (index)}
      <line class="grid" class:base={index === 0} x1={padding.left} x2={padding.left + plotWidth} y1={yAt(tick)} y2={yAt(tick)} />
      <text class="axis" x={padding.left - 6} y={yAt(tick)} dy="0.32em" text-anchor="end">{format(tick, true)}</text>
    {/each}
    {#each xLabelIndexes as timeIndex, position (position)}
      <text class="axis" x={xAt(timeIndex)} y={height - 4} text-anchor={position === 0 ? 'start' : position === 2 ? 'end' : 'middle'}>{clock(times[timeIndex])}</text>
    {/each}
    {#each series as line (line.id)}
      <path d={linePath(line.values)} stroke={line.color} />
    {/each}
    {#if hoverIndex != null}
      <line class="crosshair" x1={xAt(hoverIndex)} x2={xAt(hoverIndex)} y1={padding.top} y2={padding.top + plotHeight} />
      {#each series as line (line.id)}
        {#if line.values[hoverIndex] != null}
          <circle cx={xAt(hoverIndex)} cy={yAt(line.values[hoverIndex])} r="4" fill={line.color} />
        {/if}
      {/each}
    {/if}
  </svg>

  {#if hoverIndex != null}
    <div class="tooltip" class:left={tooltipOnLeft} style="left: {tooltipX}px">
      <span class="when">{clock(times[hoverIndex])}</span>
      {#each series as line (line.id)}
        <span class="row">
          <span class="key" style="background: {line.color}"></span>
          <strong class="num">{line.values[hoverIndex] == null ? '—' : format(line.values[hoverIndex])}</strong>
          <span class="name">{line.name}</span>
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
  .crosshair { stroke: var(--ink-4); stroke-width: 1; }
  circle { stroke: var(--page); stroke-width: 2; }
  .tooltip {
    position: absolute; top: 0; z-index: 2; pointer-events: none;
    display: grid; gap: 3px; padding: 7px 9px; min-width: 120px;
    background: var(--surface-2); border: 1px solid var(--surface-3); border-radius: var(--radius-s);
    box-shadow: var(--shadow-pop); font-size: 12px;
  }
  .tooltip.left { transform: translateX(-100%); }
  .when { color: var(--ink-3); font-size: 11px; }
  .row { display: flex; align-items: center; gap: 6px; white-space: nowrap; }
  .key { width: 10px; height: 2px; border-radius: 1px; flex: none; }
  .row strong { color: var(--ink); font-weight: 600; }
  .name { color: var(--ink-2); }
</style>
