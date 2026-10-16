<script>
  // Estado vazio: ícone, título, explicação curta e, se houver, a ação que resolve.
  // icon: traços de um SVG 24×24 (mesmo estilo dos ícones do topo)
  let { title, icon, action, children } = $props();
</script>

<div class="empty">
  <span class="badge" aria-hidden="true">
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">{@render icon()}</svg>
  </span>
  <h2>{title}</h2>
  <p>{@render children()}</p>
  {#if action}<div class="action">{@render action()}</div>{/if}
</div>

<style>
  .empty { display: grid; justify-items: center; gap: 6px; padding: 32px 8px 36px; text-align: center; }
  .badge {
    display: grid; place-items: center; width: 48px; height: 48px; margin-bottom: 6px;
    border-radius: var(--radius-l); background: var(--surface); color: var(--ink-3);
    box-shadow: inset 0 1px 0 rgb(255 255 255 / .04);
  }
  h2 { font-size: 14px; font-weight: 600; text-wrap: balance; }
  p { max-width: 36ch; color: var(--ink-3); font-size: 12.5px; }
  .action { margin-top: 10px; }
</style>
