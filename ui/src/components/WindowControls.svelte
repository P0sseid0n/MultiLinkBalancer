<script>
  // Fechar o painel (ele volta para a bandeja; a barra de título do Windows foi removida).
  import { call } from '../lib/bridge.js';
</script>

<div class="wc">
  <button class="icon-btn close" aria-label="Fechar (o MultiLink continua na bandeja)" title="Fechar" onclick={() => call('window.close').catch(() => {})}>
    <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M2 2l8 8M10 2l-8 8" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" /></svg>
  </button>
</div>

<style>
  .wc { display: flex; }
  .close:hover { background: var(--close-hover); color: #ffffff; }
</style>
