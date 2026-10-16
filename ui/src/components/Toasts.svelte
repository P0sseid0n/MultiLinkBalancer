<script>
  import { flip } from 'svelte/animate';
  import { fly } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import { app, dismissToast } from '../lib/store.svelte.js';
  import { dur } from '../lib/motion.js';
</script>

<div class="toasts" aria-live="polite">
  {#each app.toasts as t (t.id)}
    <!-- sai deslizando para a direita; os outros avisos se reacomodam -->
    <div class="toast" class:error={t.kind === 'error'} animate:flip={{ duration: dur(240), easing: cubicOut }}
      out:fly={{ x: 40, duration: dur(200), easing: cubicOut }} role={t.kind === 'error' ? 'alert' : 'status'}>
      <p>{t.text}</p>
      <button class="btn quiet" aria-label="Fechar aviso" onclick={() => dismissToast(t.id)}>
        <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M2 2l8 8M10 2l-8 8" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" /></svg>
      </button>
    </div>
  {/each}
</div>

<style>
  .toasts { position: fixed; right: 16px; bottom: 16px; z-index: 20; display: grid; gap: 8px; width: min(420px, calc(100vw - 40px)); }
  .toast {
    display: flex; align-items: flex-start; gap: 8px;
    padding: 10px 6px 10px 14px;
    background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius-m);
    box-shadow: var(--shadow-pop);
    animation: rise .28s var(--ease-out) both;
  }
  .toast p { flex: 1; padding-top: 5px; }
  .toast.error { border-left: 3px solid var(--critical); background: linear-gradient(90deg, var(--critical-wash), var(--surface) 55%); }
  .btn { width: 28px; min-height: 28px; height: 28px; padding: 0; }
</style>
