<script>
  import { app, answer } from '../lib/store.svelte.js';

  let dialog;
  $effect(() => {
    if (!dialog) return;
    if (app.confirm && !dialog.open) dialog.showModal();
    if (!app.confirm && dialog.open) dialog.close();
  });
</script>

<dialog bind:this={dialog} onclose={() => answer(false)} aria-labelledby="cd-title">
  {#if app.confirm}
    <h2 id="cd-title">{app.confirm.title}</h2>
    <p>{app.confirm.body}</p>
    <div class="buttons">
      <button class="btn" onclick={() => answer(false)}>Cancelar</button>
      <button class="btn primary" class:danger={app.confirm.danger} onclick={() => answer(true)}>{app.confirm.confirm}</button>
    </div>
  {/if}
</dialog>

<style>
  dialog {
    width: min(440px, calc(100vw - 32px)); padding: 22px 22px 18px;
    border: 1px solid var(--line); border-radius: var(--radius-l);
    background: var(--surface); color: var(--ink);
    box-shadow: var(--shadow-pop);
  }
  dialog[open] { animation: pop .22s var(--ease-out) both; }
  dialog::backdrop { background: rgb(8 12 18 / .55); backdrop-filter: blur(2px); }
  dialog[open]::backdrop { animation: fade .2s ease both; }
  @keyframes pop { from { opacity: 0; transform: translateY(8px) scale(.98); } }
  @keyframes fade { from { opacity: 0; } }
  h2 { font-size: 17px; letter-spacing: -0.01em; margin-bottom: 8px; text-wrap: balance; }
  p { color: var(--ink-2); }
  .buttons { display: flex; justify-content: flex-end; gap: 8px; margin-top: 20px; }
  .btn.primary.danger { background: var(--critical); border-color: var(--critical); color: #fff; }
  .btn.primary.danger:hover:not(:disabled) { background: var(--critical-hover); border-color: var(--critical-hover); }
</style>
