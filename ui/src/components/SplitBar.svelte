<script>
  // Barra de divisão: quanto cada link contribui para o todo. Usada no painel (tráfego ao vivo)
  // e no download acelerado (bytes baixados por link).
  // parts: [{ id, name, value, color, detail? }]
  import { percent } from '../lib/format.js';

  let { parts, idle = false, label, track = 0 } = $props();
  // track > 0: a barra representa um total fixo (ex.: tamanho do arquivo) e o resto fica vazio
  const sum = $derived(parts.reduce((a, p) => a + p.value, 0));
  const whole = $derived(track > 0 ? track : sum);
</script>

<div class="split" class:idle>
  <div class="bar" role="img" aria-label={label}>
    {#each parts as p (p.id)}
      <span class="seg" style:flex-grow={whole ? p.value / whole : 0} style:background={p.color}></span>
    {/each}
    {#if track > 0}<span class="rest" style:flex-grow={Math.max(0, 1 - sum / whole)}></span>{/if}
  </div>
  <ul class="legend">
    {#each parts as p (p.id)}
      <li>
        <span class="sw" style:background={p.color}></span>
        <span class="name">{p.name}</span>
        <span class="pct num">{percent(p.value, sum)}%</span>
        {#if p.detail}<span class="detail num">{p.detail}</span>{/if}
      </li>
    {/each}
  </ul>
</div>

<style>
  .bar {
    display: flex; gap: 2px;
    height: 20px; padding: 0;
    border-radius: 5px; overflow: hidden;
    background: var(--surface-3);
  }
  .seg { flex-basis: 0; min-width: 3px; transition: flex-grow .45s var(--ease-out), opacity .3s; }
  .seg:first-child { border-radius: 5px 0 0 5px; }
  .rest { flex-basis: 0; background: transparent; }
  .idle .seg { opacity: .35; }

  .legend { display: flex; flex-wrap: wrap; gap: 6px 28px; margin: 10px 0 0; padding: 0; list-style: none; }
  li { display: flex; align-items: baseline; gap: 8px; }
  .sw { width: 10px; height: 10px; border-radius: 3px; align-self: center; }
  .name { font-weight: 600; }
  .pct { color: var(--ink); }
  .detail { color: var(--ink-3); }
</style>
