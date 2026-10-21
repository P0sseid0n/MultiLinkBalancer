<script>
  // Download acelerado: o arquivo é dividido em partes baixadas por todos os links ao mesmo tempo.
  import { app, run, fire } from '../../state/app.svelte.js';
  import { bytes, rate, rateText, percent, seriesColor } from '../../lib/format.js';
  import SplitBar from '../../components/SplitBar.svelte';

  let url = $state('');
  let starting = $state(false);

  const download = $derived(app.state.download);
  const running = $derived(download?.state === 'running');
  const busy = $derived(running || download?.state === 'probing');
  const speed = $derived(rate(download?.rate || 0));
  const progress = $derived(download ? percent(download.done, download.total) : 0);
  const colorOf = (id) => seriesColor(Math.max(0, app.state.links.findIndex((link) => link.id === id)));
  const parts = $derived(
    download ? download.perLink.map((part) => ({
      id: part.id, name: part.name, value: part.bytes, color: colorOf(part.id),
      detail: running ? rateText(part.rate) : bytes(part.bytes),
    })) : [],
  );

  async function start(event) {
    event.preventDefault();
    starting = true;
    try { await run('download.start', { url }); url = ''; } catch {}
    starting = false;
  }
</script>

<div class="download">
  <section class="intro">
    <h2>Download acelerado</h2>
    <p>Cole o link de um arquivo grande. O MultiLink divide o arquivo em partes e baixa cada parte por um link diferente, somando a velocidade de todos num único download.</p>
  </section>

  <form class="panel url-form" onsubmit={start}>
    <label class="sr-only" for="download-url">Link do arquivo</label>
    <input id="download-url" type="url" placeholder="https://exemplo.com/arquivo.iso" bind:value={url} disabled={busy} autocomplete="off" />
    <button class="btn primary" type="submit" disabled={busy || starting || !url.trim()}>
      {starting ? 'Verificando…' : 'Baixar com todos os links'}
    </button>
  </form>

  {#if download}
    <section class="panel progress" aria-live="polite">
      {#if download.state === 'probing'}
        <p class="muted">Verificando o arquivo…</p>
      {:else}
        <div class="top">
          <div>
            <p class="file" title={download.path}>{download.name}</p>
            <p class="muted num">
              {#if running}{bytes(download.done)} de {bytes(download.total)}{:else if download.state === 'done'}{bytes(download.total)} salvos em {download.path}{:else}{download.message}{/if}
            </p>
          </div>
          {#if running}
            <p class="speed"><span class="num">{speed.value}</span> {speed.unit}</p>
          {:else if download.state === 'done'}
            <p class="speed done">média de <span class="num">{download.avgMbps}</span> Mbps</p>
          {/if}
        </div>

        <SplitBar {parts} track={running ? download.total : 0}
          label="Parte do arquivo baixada por cada link: {progress}% concluído" />

        {#if download.message && running}<p class="note">{download.message}</p>{/if}

        <div class="buttons">
          {#if running}
            <span class="pct num">{progress}% concluído</span>
            <button class="btn" onclick={() => fire('download.cancel')}>Cancelar download</button>
          {:else}
            {#if download.state === 'done'}<button class="btn" onclick={() => fire('download.reveal')}>Mostrar na pasta</button>{/if}
            <button class="btn quiet" onclick={() => fire('download.clear')}>Limpar</button>
          {/if}
        </div>
      {/if}
    </section>
  {:else}
    <p class="empty">Funciona com servidores que aceitam download em partes, como a maioria dos sites de arquivos, ISOs e CDNs. Se o servidor não aceitar, o arquivo é baixado normalmente por um link só.</p>
  {/if}
</div>

<style>
  .intro { margin-bottom: 18px; padding-bottom: 18px; border-bottom: 1px solid var(--line); }
  .intro h2 { font-size: 14px; color: var(--ink-soft); }
  .intro p { color: var(--ink-3); margin-top: 14px; font-size: 12.5px; }
  .url-form { display: flex; flex-direction: column; gap: 10px; padding: 0; border: 0; background: transparent; }
  .url-form input { width: 100%; }
  .progress { margin-top: 16px; padding: 14px 14px 16px; animation: rise .3s var(--ease-out) both; }
  .top { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-end; gap: 8px 16px; margin-bottom: 14px; }
  .top > div { min-width: 0; }
  .file { font-weight: 600; font-size: 16px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .muted { color: var(--ink-3); }
  .speed { font-family: var(--font-display); font-size: 22px; font-weight: 600; letter-spacing: -0.02em; white-space: nowrap; }
  .speed.done { font-size: 16px; font-weight: 500; color: var(--ink-2); }
  .note { margin-top: 10px; color: var(--ink-2); }
  .buttons { display: flex; align-items: center; justify-content: flex-end; gap: 8px; margin-top: 16px; }
  .pct { margin-right: auto; color: var(--ink-2); }
  .empty { margin-top: 16px; color: var(--ink-3); font-size: 12.5px; }
</style>
