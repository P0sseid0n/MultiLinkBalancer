<script>
  import { Tween } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import { app, run } from '../lib/store.svelte.js';
  import { rate, seriesVar } from '../lib/format.js';
  import { dur } from '../lib/motion.js';
  import EventBar from './EventBar.svelte';
  import LinkItem from './LinkItem.svelte';
  import WindowControls from './WindowControls.svelte';

  let { onSettings, onHistory } = $props();

  const s = $derived(app.state);
  const on = $derived(s.proxy.running);
  const links = $derived(s.links);
  const down = $derived(links.filter((l) => l.health !== 'ok' && l.health !== 'disabled'));
  const usable = $derived(links.filter((l) => l.health === 'ok' && l.enabled && l.weight > 0));
  const total = $derived(links.reduce((a, l) => a + l.rateDown, 0));
  // o total desliza até o valor novo a cada segundo, em vez de pular
  const shown = Tween.of(() => (on ? total : 0), { duration: () => dur(700), easing: cubicOut });
  const big = $derived(on ? rate(shown.current) : { value: '0', unit: 'Mbps' });

  // participação de cada link: tráfego real; sem tráfego, a divisão prevista pelos pesos
  const idle = $derived(!on || total < 12500);
  const shares = $derived.by(() => {
    const base = links.map((l) => (l.health === 'ok' && l.enabled && l.weight > 0 ? (idle ? l.weight : l.rateDown) : 0));
    const sum = base.reduce((a, b) => a + b, 0);
    return base.map((v) => (sum ? Math.round((v / sum) * 100) : 0));
  });

  const sub = $derived(
    !on ? 'Toque para dividir as conexões'
    : down.length ? `${down.length} de ${links.length} links fora do ar`
    : usable.length === 0 ? 'Nenhum link disponível'
    : usable.length === 1 ? 'Usando 1 link'
    : `Dividindo conexões entre ${usable.length} links`,
  );

  let busy = $state(false);
  async function toggle() {
    busy = true;
    try { await run(on ? 'proxy.stop' : 'proxy.start'); } catch {}
    busy = false;
  }

  let open = $state(null);
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

<section class="power">
  <button class="io" class:on aria-pressed={on} aria-label={on ? 'Desligar o MultiLink' : 'Ligar o MultiLink'} disabled={busy} onclick={toggle}>
    <svg width="52" height="52" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="M12 3v8" /><path d="M6.4 6.6a8 8 0 1 0 11.2 0" /></svg>
  </button>
  <div class="state">
    <span class="label" class:on>{on ? 'Ligado' : 'Desligado'}</span>
    <span class="sub" class:bad={on && down.length}>{sub}</span>
  </div>
</section>

<p class="total" class:off={!on} aria-label="Recebendo agora: {big.value} {big.unit}">
  <span class="value num">{big.value}</span><span class="unit">{big.unit}</span>
</p>

<div class="links">
  {#each links as l, i (l.id)}
    <LinkItem link={l} color={seriesVar(i)} share={shares[i]} {idle} proxyOn={on}
      open={open === l.id} ontoggle={() => (open = open === l.id ? null : l.id)} />
  {:else}
    <p class="empty">Nenhuma conexão com internet encontrada. Conecte um cabo de rede.</p>
  {/each}
</div>

<style>
  /* o topo é a "barra de título": arrasta a janela; os botões não */
  header { display: flex; align-items: center; justify-content: space-between; height: 40px; margin-right: -8px; app-region: drag; }
  .brand { font-size: 13px; font-weight: 600; letter-spacing: 0.01em; color: var(--ink-2); }
  .tools { display: flex; align-items: center; gap: 2px; }

  .power { display: flex; flex-direction: column; align-items: center; gap: 18px; padding-top: 22px; }
  /* botão principal: superfície levemente convexa (luz de cima), anel verde quando ligado */
  .io {
    position: relative;
    width: 152px; height: 152px; border-radius: 50%;
    display: flex; align-items: center; justify-content: center;
    background: radial-gradient(circle at 50% 28%, var(--surface-2), var(--page) 78%);
    color: var(--ink-3);
    border: 2px solid var(--surface-3);
    box-shadow: 0 0 0 10px transparent, inset 0 1px 0 rgb(255 255 255 / .05), 0 14px 30px -12px rgb(4 8 14 / .7);
    cursor: pointer;
    transition: border-color .25s, box-shadow .35s var(--ease-out), color .25s, transform .15s var(--ease-out);
  }
  .io:hover:not(:disabled) { border-color: var(--hover-line); color: var(--ink-2); }
  .io:active:not(:disabled) { transform: scale(.96); }
  .io.on {
    color: var(--on); border-color: var(--on);
    background: radial-gradient(circle at 50% 28%, rgb(76 195 138 / .10), var(--page) 78%);
    box-shadow: 0 0 0 10px var(--on-halo), inset 0 1px 0 rgb(255 255 255 / .05), 0 14px 34px -10px rgb(76 195 138 / .28);
  }
  .io.on:hover:not(:disabled) { border-color: var(--on); color: var(--on); }
  /* respiração lenta do anel enquanto ligado */
  .io.on::after {
    content: ""; position: absolute; inset: -2px; border-radius: 50%;
    border: 2px solid var(--on); opacity: 0;
    animation: breathe 3.2s var(--ease-out) infinite;
    pointer-events: none;
  }
  @keyframes breathe { 0% { opacity: .35; transform: scale(1); } 100% { opacity: 0; transform: scale(1.22); } }
  .io:disabled { cursor: progress; }
  .io:disabled svg { opacity: .6; }
  .io:focus-visible { outline-offset: 6px; }
  .state { display: flex; flex-direction: column; align-items: center; gap: 4px; }
  .label { font-size: 15px; font-weight: 600; color: var(--ink-2); transition: color .2s; }
  .label.on { color: var(--ink); }
  .sub { font-size: 12px; color: var(--ink-3); }
  .sub.bad { color: var(--critical-ink); }

  .total {
    display: flex; align-items: baseline; justify-content: center; gap: 6px;
    padding: 24px 0 20px;
    font-family: var(--font-display);
  }
  .total .value { font-size: 44px; font-weight: 600; letter-spacing: -0.03em; line-height: 1; transition: color .3s; }
  .total .unit { font-size: 15px; font-weight: 500; color: var(--ink-2); }
  .total.off .value { color: var(--ink-4); }

  .links { border-top: 1px solid var(--line); }
  .empty { padding: 20px 0; color: var(--ink-3); font-size: 13px; text-align: center; }
</style>
