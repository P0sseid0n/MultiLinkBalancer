<script>
  // Topo das telas secundárias: voltar, título e fechar. É também a área de arrastar da janela.
  import WindowControls from './WindowControls.svelte';

  let { title, onBack } = $props();
</script>

<header>
  <button class="icon-btn back" aria-label="Voltar para a tela inicial" title="Voltar" onclick={onBack}>
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
  </button>
  <h1>{title}</h1>
  <div class="window-controls"><WindowControls /></div>
</header>

<style>
  header { display: flex; align-items: center; gap: 4px; height: 40px; margin: 0 -8px 12px 0; app-region: drag; }
  .back { margin-left: -12px; color: var(--ink-2); }
  .window-controls { margin-left: auto; }
  h1 { font-size: 15px; font-weight: 600; }
</style>
