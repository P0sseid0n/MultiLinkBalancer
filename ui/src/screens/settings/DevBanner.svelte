<script>
  // Faixa do modo de desenvolvimento (.exe ao lado do build.ps1): fica presa no topo enquanto os ajustes rolam.
  import { app, fire } from '../../state/app.svelte.js';
  import { since } from '../../lib/format.js';

  const dev = $derived(app.state.dev);
</script>

<div class="banner" role="status">
  <span class="dot" aria-hidden="true"></span>
  <p><strong>Modo de desenvolvimento</strong> · v{app.state.update.current} · compilado {since(dev.builtAt)}</p>
  <button class="btn" onclick={() => fire('dev.rebuild')} disabled={dev.building}>{dev.building ? 'Recompilando…' : 'Recompilar'}</button>
</div>

<style>
  .banner {
    position: sticky; top: 0; z-index: var(--z-tooltip);
    display: flex; align-items: center; gap: 10px;
    margin-bottom: -12px; padding: 8px 8px 8px 12px;
    background: color-mix(in srgb, var(--warn) 9%, var(--page));
    border: 1px solid var(--warn-ring); border-radius: var(--radius-m);
    box-shadow: 0 6px 16px -8px rgb(4 8 14 / .6);
  }
  .dot { flex: none; width: 7px; height: 7px; border-radius: 50%; background: var(--warn); }
  p { flex: 1; min-width: 0; font-size: 12.5px; color: var(--ink-2); }
  strong { color: var(--warn-ink); font-weight: 600; }
  .btn { flex: none; }
</style>
