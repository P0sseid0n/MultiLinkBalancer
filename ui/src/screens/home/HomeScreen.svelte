<script>
  // Tela inicial: eventos, botão I/O, total recebido agora e a lista de links.
  import { Tween } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import { app, run, fire } from '../../state/app.svelte.js';
  import { rate, seriesColor } from '../../lib/format.js';
  import { isDown, isUsable } from '../../lib/health.js';
  import { dur } from '../../lib/motion.js';
  import WindowControls from '../../components/WindowControls.svelte';
  import EventBar from './EventBar.svelte';
  import PowerButton from './PowerButton.svelte';
  import LinkItem from './LinkItem.svelte';
  import EmptyState from '../../components/EmptyState.svelte';

  let { onSettings, onHistory } = $props();

  const IDLE_BELOW = 12500; // bytes/s (100 kbps): abaixo disso a divisão mostrada é a prevista pelos pesos

  const on = $derived(app.state.proxy.running);
  const links = $derived(app.state.links);
  const downLinks = $derived(links.filter(isDown));
  const usableLinks = $derived(links.filter(isUsable));
  const totalRate = $derived(links.reduce((sum, link) => sum + link.rateDown, 0));
  // o total desliza até o valor novo a cada segundo, em vez de pular
  const shownRate = Tween.of(() => (on ? totalRate : 0), { duration: () => dur(700), easing: cubicOut });
  const total = $derived(on ? rate(shownRate.current) : { value: '0', unit: 'Mbps' });

  // participação de cada link: tráfego real; sem tráfego, a divisão prevista pelos pesos
  const idle = $derived(!on || totalRate < IDLE_BELOW);
  const shares = $derived.by(() => {
    const amounts = links.map((link) => (isUsable(link) ? (idle ? link.weight : link.rateDown) : 0));
    const sum = amounts.reduce((total, amount) => total + amount, 0);
    return amounts.map((amount) => (sum ? Math.round((amount / sum) * 100) : 0));
  });

  const subtitle = $derived(
    !on ? 'Toque para dividir as conexões'
    : downLinks.length ? `${downLinks.length} de ${links.length} links fora do ar`
    : usableLinks.length === 0 ? 'Nenhum link disponível'
    : usableLinks.length === 1 ? 'Usando 1 link'
    : `Dividindo conexões entre ${usableLinks.length} links`,
  );

  let switching = $state(false);
  async function togglePower() {
    switching = true;
    try { await run(on ? 'proxy.stop' : 'proxy.start'); } catch {}
    switching = false;
  }

  let expandedId = $state(null);
</script>

<header>
  <span class="brand">MultiLink</span>
  <div class="tools">
    <button class="icon-btn" aria-label="Histórico de velocidade e quedas" title="Histórico" onclick={onHistory}>
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 3v18h18" /><path d="m7 15 4-5 3 3 5-7" /></svg>
    </button>
    <button class="icon-btn" aria-label="Ajustes e download" title="Ajustes" onclick={onSettings}>
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" /></svg>
    </button>
    <WindowControls />
  </div>
</header>

<EventBar />

<PowerButton {on} busy={switching} {subtitle} warning={on && downLinks.length > 0} onToggle={togglePower} />

<p class="total" class:off={!on} aria-label="Recebendo agora: {total.value} {total.unit}">
  <span class="value num">{total.value}</span><span class="unit">{total.unit}</span>
</p>

<div class="links">
  {#each links as link, index (link.id)}
    <LinkItem {link} color={seriesColor(index)} share={shares[index]} {idle} proxyOn={on}
      open={expandedId === link.id} ontoggle={() => (expandedId = expandedId === link.id ? null : link.id)} />
  {:else}
    <EmptyState title="Nenhuma conexão encontrada">
      {#snippet icon()}<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0zM12 18v4" />{/snippet}
      Conecte um cabo de rede ou ative um adaptador. O MultiLink usa cada conexão com internet como um link.
      {#snippet action()}<button class="btn" onclick={() => fire('adapters.refresh')}>Procurar de novo</button>{/snippet}
    </EmptyState>
  {/each}
</div>

<style>
  /* o topo é a "barra de título": arrasta a janela; os botões não */
  header { display: flex; align-items: center; justify-content: space-between; height: 40px; margin-right: -8px; app-region: drag; }
  .brand { font-size: 13px; font-weight: 600; letter-spacing: 0.01em; color: var(--ink-2); }
  .tools { display: flex; align-items: center; gap: 2px; }

  .total {
    display: flex; align-items: baseline; justify-content: center; gap: 6px;
    padding: 24px 0 20px;
    font-family: var(--font-display);
  }
  .total .value { font-size: 44px; font-weight: 600; letter-spacing: -0.03em; line-height: 1; transition: color .3s; }
  .total .unit { font-size: 15px; font-weight: 500; color: var(--ink-2); }
  .total.off .value { color: var(--ink-4); }

  .links { border-top: 1px solid var(--line); }
</style>
