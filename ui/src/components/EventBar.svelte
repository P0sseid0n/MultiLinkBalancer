<script>
  // Faixa do topo: o último evento numa linha; ao clicar, os anteriores.
  import { fly } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import { app } from '../lib/store.svelte.js';
  import { dur } from '../lib/motion.js';

  let open = $state(false);
  const events = $derived([...app.log].reverse().slice(0, 8));
  const last = $derived(events[0]);
  const hhmm = (t) => (t ? t.slice(0, 5) : '');
</script>

{#if last}
  <div class="bar">
    <button aria-expanded={open} onclick={() => (open = !open)}
      aria-label={open ? 'Recolher eventos' : `Último evento: ${last.text}. Ver eventos anteriores`}>
      <span class="dot {last.kind}" aria-hidden="true"></span>
      <!-- evento novo: o anterior sobe e sai, o novo sobe e entra -->
      <span class="ticker">
        {#key last.seq}
          <span class="text" in:fly={{ y: 14, duration: dur(280), easing: cubicOut }} out:fly={{ y: -14, duration: dur(220), easing: cubicOut }}>{last.text}</span>
        {/key}
      </span>
      <time class="num">{hhmm(last.time)}</time>
      <svg class="chev" class:open width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg>
    </button>
    {#if open}
      <ol>
        {#each events.slice(1) as e (e.seq)}
          <li>
            <span class="dot {e.kind}" aria-hidden="true"></span>
            <span class="t">{e.text}</span>
            <time class="num">{hhmm(e.time)}</time>
          </li>
        {:else}
          <li class="none">Nenhum evento anterior.</li>
        {/each}
      </ol>
    {/if}
  </div>
{/if}

<style>
  /* de borda a borda: compensa o padding da janela (24 à esquerda, 16 à direita) */
  .bar {
    margin: 4px -16px 0 -24px;
    background: var(--band);
    border-top: 1px solid var(--line-soft);
    border-bottom: 1px solid var(--line-soft);
  }
  button {
    width: 100%; min-height: 44px;
    display: flex; align-items: center; gap: 10px;
    padding: 0 16px 0 24px;
    border: 0; background: transparent; color: var(--ink-soft);
    text-align: left; cursor: pointer;
    transition: color .15s, background-color .15s;
  }
  button:hover { color: var(--ink); background: rgb(255 255 255 / .015); }
  button:focus-visible { outline-offset: -2px; }
  .ticker { flex: 1; min-width: 0; display: grid; overflow: hidden; }
  .text { grid-area: 1 / 1; min-width: 0; font-size: 12.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  time { font-size: 12px; color: var(--ink-3); }
  .chev { flex: none; color: var(--ink-3); transition: transform .25s var(--ease-out); }
  .chev.open { transform: rotate(180deg); }

  .dot { width: 6px; height: 6px; border-radius: 3px; flex: none; background: var(--dot-neutral); }
  .dot.down, .dot.error { background: var(--critical); }
  .dot.up { background: var(--on); }

  ol { list-style: none; padding: 0 16px 6px 24px; animation: rise .25s var(--ease-out) both; }
  li { display: flex; align-items: flex-start; gap: 10px; padding: 9px 0; border-top: 1px solid var(--line-soft); }
  li .dot { margin-top: 6px; }
  li .t { flex: 1; min-width: 0; font-size: 12.5px; line-height: 1.45; color: var(--ink-2); }
  li time { line-height: 1.45; }
  .none { color: var(--ink-3); font-size: 12.5px; }
</style>
