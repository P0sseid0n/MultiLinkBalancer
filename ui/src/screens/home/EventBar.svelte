<script>
  // Faixa do topo: o último evento numa linha; ao clicar, os anteriores num painel que flutua sobre a tela.
  import { fly } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import { app } from '../../state/app.svelte.js';
  import { dur } from '../../lib/motion.js';

  const SHOWN = 50; // com rolagem própria, dá para mostrar bem mais que cabe na tela

  let open = $state(false);
  let bar = $state();
  const events = $derived([...app.log].reverse().slice(0, SHOWN));
  const last = $derived(events[0]);
  const hoursMinutes = (time) => (time ? time.slice(0, 5) : ''); // "14:05:33" -> "14:05"

  // aberto, fecha ao clicar fora do painel ou com Esc
  function closeOutside(event) { if (open && !bar?.contains(event.target)) open = false; }
  function closeOnEscape(event) { if (open && event.key === 'Escape') open = false; }
</script>

<svelte:window onpointerdown={closeOutside} onkeydown={closeOnEscape} />

{#if last}
  <div class="bar" class:open bind:this={bar}>
    <div class="sheet">
      <!-- fechado, o texto cortado aparece inteiro ao passar o mouse; aberto, ele quebra linha -->
      <button aria-expanded={open} onclick={() => (open = !open)} title={open ? undefined : last.text}
        aria-label={open ? 'Recolher eventos' : `Último evento: ${last.text}. Ver eventos anteriores`}>
        <span class="dot {last.kind}" aria-hidden="true"></span>
        <!-- evento novo: o anterior sobe e sai, o novo sobe e entra -->
        <span class="ticker">
          {#key last.seq}
            <span class="text" in:fly={{ y: 14, duration: dur(280), easing: cubicOut }} out:fly={{ y: -14, duration: dur(220), easing: cubicOut }}>{last.text}</span>
          {/key}
        </span>
        <time class="num">{hoursMinutes(last.time)}</time>
        <svg class="chev" class:open width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg>
      </button>
      {#if open}
        <ol>
          {#each events.slice(1) as entry (entry.seq)}
            <li>
              <span class="dot {entry.kind}" aria-hidden="true"></span>
              <span class="entry-text">{entry.text}</span>
              <time class="num">{hoursMinutes(entry.time)}</time>
            </li>
          {:else}
            <li class="none">Nenhum evento anterior.</li>
          {/each}
        </ol>
      {/if}
    </div>
  </div>
{/if}

<style>
  /* de borda a borda: compensa o padding da janela (24 à esquerda, 16 à direita).
     A faixa guarda sempre a altura de uma linha; aberto, o painel flutua por cima do botão I/O */
  .bar { position: relative; height: 46px; margin: 4px -16px 0 -24px; }
  .sheet {
    position: absolute; top: 0; left: 0; right: 0; z-index: var(--z-tooltip);
    display: flex; flex-direction: column;
    background: var(--band);
    border-top: 1px solid var(--line-soft);
    border-bottom: 1px solid var(--line-soft);
    transition: box-shadow .2s;
  }
  .bar.open .sheet { max-height: min(360px, calc(100vh - 72px)); border-bottom-color: var(--line); box-shadow: var(--shadow-pop); }
  button {
    flex: none; width: 100%; min-height: 44px;
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
  /* aberto: o último evento quebra linha como os anteriores; ponto, hora e seta ficam na primeira linha */
  .bar.open button { align-items: flex-start; padding-top: 13px; padding-bottom: 13px; }
  .bar.open .text { white-space: normal; line-height: 1.45; }
  .bar.open button .dot { margin-top: 6px; }
  .bar.open button time { line-height: 1.45; }
  .bar.open .chev { margin-top: 2px; }

  .dot { width: 6px; height: 6px; border-radius: 3px; flex: none; background: var(--dot-neutral); }
  .dot.down, .dot.error { background: var(--critical); }
  .dot.up { background: var(--on); }

  /* só a lista rola, com a barra fina do app; o último evento fica fixo no topo do painel */
  ol {
    min-height: 0; overflow-y: auto; overscroll-behavior: contain;
    list-style: none; margin: 0; padding: 0 16px 6px 24px;
    scrollbar-width: thin; scrollbar-color: var(--scroll-thumb) transparent;
    animation: rise .25s var(--ease-out) both;
  }
  li { display: flex; align-items: flex-start; gap: 10px; padding: 9px 0; border-top: 1px solid var(--line-soft); }
  li .dot { margin-top: 6px; }
  li .entry-text { flex: 1; min-width: 0; font-size: 12.5px; line-height: 1.45; color: var(--ink-2); }
  li time { line-height: 1.45; }
  .none { color: var(--ink-3); font-size: 12.5px; }
</style>
