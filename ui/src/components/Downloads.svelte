<script>
  import { app, run } from '../lib/store.svelte.js';
  import { bytes, rate, rateText, percent, seriesVar } from '../lib/format.js';
  import SplitBar from './SplitBar.svelte';

  let url = $state('');
  let starting = $state(false);

  const s = $derived(app.state);
  const d = $derived(s.download);
  const active = $derived(d && (d.state === 'running' || d.state === 'probing'));
  const big = $derived(rate(d?.rate || 0));
  const colorOf = (id) => seriesVar(Math.max(0, s.links.findIndex((l) => l.id === id)));
  const parts = $derived(
    d ? d.perLink.map((p) => ({
      id: p.id, name: p.name, value: p.bytes, color: colorOf(p.id),
      detail: d.state === 'running' ? rateText(p.rate) : bytes(p.bytes),
    })) : [],
  );

  async function start(e) {
    e.preventDefault();
    starting = true;
    try { await run('download.start', { url }); url = ''; } catch {}
    starting = false;
  }
</script>

<section class="intro">
  <h2>Download acelerado</h2>
  <p>Cole o link de um arquivo grande. O MultiLink divide o arquivo em partes e baixa cada parte por um link diferente, somando a velocidade de todos num único download.</p>
</section>

<form class="panel bar" onsubmit={start}>
  <label class="sr-only" for="dl-url">Link do arquivo</label>
  <input id="dl-url" type="url" placeholder="https://exemplo.com/arquivo.iso" bind:value={url} disabled={active} autocomplete="off" />
  <button class="btn primary" type="submit" disabled={active || starting || !url.trim()}>
    {starting ? 'Verificando…' : 'Baixar com todos os links'}
  </button>
</form>

{#if d}
  <section class="panel progress" aria-live="polite">
    {#if d.state === 'probing'}
      <p class="muted">Verificando o arquivo…</p>
    {:else}
      <div class="top">
        <div>
          <p class="file" title={d.path}>{d.name}</p>
          <p class="muted num">
            {#if d.state === 'running'}{bytes(d.done)} de {bytes(d.total)}{:else if d.state === 'done'}{bytes(d.total)} salvos em {d.path}{:else}{d.message}{/if}
          </p>
        </div>
        {#if d.state === 'running'}
          <p class="speed"><span class="num">{big.value}</span> {big.unit}</p>
        {:else if d.state === 'done'}
          <p class="speed done">média de <span class="num">{d.avgMbps}</span> Mbps</p>
        {/if}
      </div>

      <SplitBar {parts} track={d.state === 'running' ? d.total : 0}
        label="Parte do arquivo baixada por cada link: {percent(d.done, d.total)}% concluído" />

      {#if d.message && d.state === 'running'}<p class="note">{d.message}</p>{/if}

      <div class="buttons">
        {#if d.state === 'running'}
          <span class="pct num">{percent(d.done, d.total)}% concluído</span>
          <button class="btn" onclick={() => run('download.cancel').catch(() => {})}>Cancelar download</button>
        {:else}
          {#if d.state === 'done'}<button class="btn" onclick={() => run('download.reveal').catch(() => {})}>Mostrar na pasta</button>{/if}
          <button class="btn quiet" onclick={() => run('download.clear').catch(() => {})}>Limpar</button>
        {/if}
      </div>
    {/if}
  </section>
{:else}
  <p class="empty">Funciona com servidores que aceitam download em partes, como a maioria dos sites de arquivos, ISOs e CDNs. Se o servidor não aceitar, o arquivo é baixado normalmente por um link só.</p>
{/if}

<style>
  .intro { margin-bottom: 12px; padding-bottom: 12px; border-bottom: 1px solid var(--line); }
  .intro h2 { font-size: 13px; color: var(--ink-2); }
  .intro p { color: var(--ink-3); margin-top: 12px; font-size: 12.5px; }
  .bar { display: flex; flex-direction: column; gap: 8px; padding: 0; border: 0; background: transparent; }
  .bar input { width: 100%; }
  .progress { margin-top: 12px; padding: 14px 14px 16px; animation: rise .3s var(--ease-out) both; }
  .top { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-end; gap: 8px 16px; margin-bottom: 14px; }
  .top > div { min-width: 0; }
  .file { font-weight: 600; font-size: 16px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .muted { color: var(--ink-3); }
  .speed { font-family: var(--font-display); font-size: 22px; font-weight: 600; letter-spacing: -0.02em; white-space: nowrap; }
  .speed.done { font-size: 16px; font-weight: 500; color: var(--ink-2); }
  .note { margin-top: 10px; color: var(--ink-2); }
  .buttons { display: flex; align-items: center; justify-content: flex-end; gap: 8px; margin-top: 16px; }
  .pct { margin-right: auto; color: var(--ink-2); }
  .empty { margin-top: 12px; color: var(--ink-3); font-size: 12.5px; }
</style>
