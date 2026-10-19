<script>
  // Botão I/O da tela inicial com o estado embaixo. Ligado: anel verde que se desenha em volta,
  // uma onda única ao ligar e algumas respirações lentas logo depois.
  let { on, busy, subtitle, warning = false, onToggle } = $props();

  let waves = $state(0);
  let wasOn; // indefinido na primeira vez: abrir a tela com o MultiLink já ligado não dispara a onda
  $effect(() => {
    if (on && wasOn === false) waves++;
    wasOn = on;
  });
</script>

<section class="power">
  <button class="io" class:on aria-pressed={on} aria-label={on ? 'Desligar o MultiLink' : 'Ligar o MultiLink'} disabled={busy} onclick={onToggle}>
    <svg width="52" height="52" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="M12 3v8" /><path d="M6.4 6.6a8 8 0 1 0 11.2 0" /></svg>
    <svg class="ring" viewBox="0 0 152 152" aria-hidden="true"><circle cx="76" cy="76" r="75" pathLength="100" /></svg>
    {#key waves}{#if waves}<span class="wave" aria-hidden="true"></span>{/if}{/key}
  </button>
  <div class="status">
    <span class="label" class:on>{on ? 'Ligado' : 'Desligado'}</span>
    <span class="subtitle" class:warning>{subtitle}</span>
  </div>
</section>

<style>
  .power { display: flex; flex-direction: column; align-items: center; gap: 18px; padding-top: 22px; }
  /* superfície levemente convexa (luz de cima), anel verde quando ligado */
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
    color: var(--on);
    background: radial-gradient(circle at 50% 28%, color-mix(in srgb, var(--on) 10%, transparent), var(--page) 78%);
    box-shadow: 0 0 0 10px var(--on-halo), inset 0 1px 0 rgb(255 255 255 / .05), 0 14px 34px -10px var(--on-glow);
  }
  .io.on:hover:not(:disabled) { color: var(--on); }
  /* anel verde: se desenha em volta ao ligar e se desfaz no sentido contrário ao desligar */
  .ring {
    position: absolute; inset: -2px; width: calc(100% + 4px); height: calc(100% + 4px);
    transform: rotate(-90deg); pointer-events: none; overflow: visible;
  }
  .ring circle {
    fill: none; stroke: var(--on); stroke-width: 2; stroke-linecap: round;
    stroke-dasharray: 100; stroke-dashoffset: 100;
    transition: stroke-dashoffset .5s cubic-bezier(.55, 0, .7, .4);
  }
  .io.on .ring circle { stroke-dashoffset: 0; transition: stroke-dashoffset .8s var(--ease-out) .05s; }
  /* onda que sai do botão uma vez ao ligar */
  .wave {
    position: absolute; inset: -2px; border-radius: 50%;
    border: 2px solid var(--on); opacity: 0;
    animation: wave .9s var(--ease-out) both;
    pointer-events: none;
  }
  @keyframes wave { 0% { opacity: .6; transform: scale(1); } 100% { opacity: 0; transform: scale(1.5); } }
  /* respiração lenta do anel depois que ele termina de se desenhar; poucas vezes e para: sem GPU,
     uma animação infinita redesenha o painel a 60 quadros por segundo enquanto ele fica aberto */
  .io.on::after {
    content: ""; position: absolute; inset: -2px; border-radius: 50%;
    border: 2px solid var(--on); opacity: 0;
    animation: breathe 3.2s var(--ease-out) .9s 3;
    pointer-events: none;
  }
  @keyframes breathe { 0% { opacity: .35; transform: scale(1); } 100% { opacity: 0; transform: scale(1.22); } }
  .io:disabled { cursor: progress; }
  .io:disabled svg { opacity: .6; }
  .io:focus-visible { outline-offset: 6px; }

  .status { display: flex; flex-direction: column; align-items: center; gap: 4px; }
  .label { font-size: 15px; font-weight: 600; color: var(--ink-2); transition: color .2s; }
  .label.on { color: var(--ink); }
  .subtitle { font-size: 12px; color: var(--ink-3); }
  .subtitle.warning { color: var(--critical-ink); }
</style>
