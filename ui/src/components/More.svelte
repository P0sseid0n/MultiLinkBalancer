<script>
  import Downloads from './Downloads.svelte';
  import Settings from './Settings.svelte';
  import WindowControls from './WindowControls.svelte';

  let { onBack } = $props();
</script>

<header>
  <button class="icon-btn back" aria-label="Voltar para a tela inicial" title="Voltar" onclick={onBack}>
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
  </button>
  <h1>Ajustes</h1>
  <div class="wc"><WindowControls /></div>
</header>

<div class="sections">
  <Downloads />
  <Settings />
</div>

<style>
  header { display: flex; align-items: center; gap: 4px; height: 40px; margin: 0 -8px 12px 0; app-region: drag; }
  .back { margin-left: -12px; color: var(--ink-2); }
  .wc { margin-left: auto; }
  h1 { font-size: 15px; font-weight: 600; }
  .sections { display: grid; gap: 28px; }
</style>
