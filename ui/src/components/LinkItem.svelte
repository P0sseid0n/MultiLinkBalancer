<script>
  // Um link: interruptor (liga/desliga o adaptador no Windows), estado, velocidade e,
  // ao clicar, os detalhes em um bloco de borda a borda.
  import { app, run, ask } from '../lib/store.svelte.js';
  import { rateText, since } from '../lib/format.js';
  import { slide } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import { dur } from '../lib/motion.js';

  let { link: l, color, share, idle, proxyOn, open, ontoggle } = $props();

  const disabled = $derived(l.health === 'disabled');
  const ok = $derived(l.health === 'ok');
  const down = $derived(!ok && !disabled);

  // interruptor: mostra o estado pedido enquanto o Windows aplica (pode pedir confirmação de administrador)
  let target = $state(null);
  const switchOn = $derived(target ?? !disabled);

  async function flip() {
    if (target !== null) return;
    const enable = disabled;
    if (!enable) {
      const others = app.state.links.filter((x) => x.id !== l.id && x.health === 'ok');
      if (!others.length) {
        const yes = await ask({
          title: `Desativar ${l.name}?`,
          body: 'É o único link com internet agora. O computador fica sem conexão até você ativá-lo de novo.',
          confirm: 'Desativar',
          danger: true,
        });
        if (!yes) return;
      }
    }
    target = enable;
    try { await run('link.toggleAdapter', { id: l.id }); } catch {}
    target = null;
  }

  const led = $derived(down ? 'fail' : disabled ? 'off' : proxyOn ? 'on' : 'idle');
  const right = $derived(
    disabled ? 'desativado'
    : down ? (l.downSince ? `fora ${since(l.downSince)}` : 'fora do ar')
    : !proxyOn ? '—'
    : rateText(l.rateDown),
  );
  const showBar = $derived(proxyOn && ok && l.enabled && l.weight > 0);

  // peso: edição local, enviada ao app com um pequeno atraso
  let weight = $state(0);
  let editing = false;
  let timer;
  $effect(() => { if (!editing) weight = l.weight; });
  function setWeight(v) {
    weight = Math.max(0, Math.min(1000, Math.round(Number(v) || 0)));
    editing = true;
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try { await run('link.set', { id: l.id, weight }); } catch {}
      editing = false;
    }, 400);
  }

  const cap = (t) => (t ? t.charAt(0).toUpperCase() + t.slice(1) : '');
  const dash = (v) => (disabled || v === '' || v == null ? '—' : v);
</script>

<div class="item" class:open>
  <div class="head">
    <button class="switch" role="switch" aria-checked={switchOn}
      aria-label="Adaptador {l.name} {switchOn ? 'ativado' : 'desativado'} no Windows"
      disabled={target !== null} onclick={flip}>
      <span class="track" class:on={switchOn}><span class="knob"></span></span>
    </button>
    <button class="toggle" aria-expanded={open} onclick={ontoggle}
      aria-label={open ? `Recolher ${l.name}` : `Ver detalhes de ${l.name}`}>
      <span class="led {led}" aria-hidden="true"></span>
      <span class="name" class:dim={disabled}>{l.name}</span>
      <span class="right num" class:bad={down} class:muted={disabled || (!proxyOn && ok)}>
        {target === true ? 'ativando…' : target === false ? 'desativando…' : right}
      </span>
      <svg class="chev" class:open width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg>
    </button>
  </div>

  {#if showBar}
    <div class="share">
      <div class="track-bar"><div class="fill" class:idle style:width="{share}%" style:background={color}></div></div>
      <span class="pct num">{share}%</span>
    </div>
  {/if}

  {#if open}
    <div class="details" transition:slide={{ duration: dur(260), easing: cubicOut }}>
      {#if down}<p class="diag">{cap(l.diagnosis)}.</p>{/if}
      <dl>
        <div><dt>Endereço</dt><dd class="num">{dash(l.ip)}</dd></div>
        <div><dt>Gateway</dt><dd class="num">{dash(l.gateway)}</dd></div>
        <div><dt>Latência</dt><dd class="num">{l.latency >= 0 ? `${l.latency} ms` : '—'}</dd></div>
        <div><dt>Velocidade medida</dt><dd class="num">{l.measuredMbps > 0 ? `${l.measuredMbps} Mbps` : '—'}</dd></div>
        <div><dt>Conexões abertas</dt><dd class="num">{disabled ? '—' : l.active}</dd></div>
        <div><dt>Enviando</dt><dd class="num">{disabled || !proxyOn ? '—' : rateText(l.rateUp)}</dd></div>
      </dl>
      <div class="weight">
        <span id="w-{l.id}">Peso</span>
        <div class="stepper" role="group" aria-labelledby="w-{l.id}">
          <button aria-label="Diminuir peso" onclick={() => setWeight(weight - 1)} disabled={weight <= 0}>−</button>
          <input class="num" type="number" min="0" max="1000" aria-label="Peso de {l.name}" value={weight}
            onchange={(e) => setWeight(e.currentTarget.value)} />
          <button aria-label="Aumentar peso" onclick={() => setWeight(weight + 1)}>+</button>
        </div>
      </div>
      {#if down}<p class="hint">Desligue o interruptor para o Windows parar de usar este link até ele voltar.</p>{/if}
      {#if l.metricRaised}<p class="hint">O Windows já parou de usar este link enquanto ele está fora.</p>{/if}
    </div>
  {/if}
</div>

<style>
  .item { display: flex; flex-direction: column; padding: 6px 0 14px; border-bottom: 1px solid var(--line); transition: background-color .25s; }
  /* expandido: fundo próprio de borda a borda (compensa o padding da janela) */
  .item.open { background: var(--raised); margin: 0 -16px 0 -24px; padding: 6px 16px 18px 24px; }

  .head { display: flex; align-items: center; gap: 4px; }

  .switch { width: 48px; height: 44px; flex: none; display: flex; align-items: center; padding: 0; border: 0; background: transparent; cursor: pointer; }
  .switch:disabled { cursor: progress; }
  .track { position: relative; width: 36px; height: 20px; border-radius: 10px; background: var(--surface-3); transition: background .15s; }
  .track.on { background: var(--on); }
  .knob { position: absolute; top: 2px; left: 2px; width: 16px; height: 16px; border-radius: 8px; background: var(--ink-2); transition: transform .22s var(--ease-out), width .12s, background .15s; }
  .track.on .knob { transform: translateX(16px); background: var(--page); }
  .switch:disabled .track { opacity: .6; }
  .switch:active:not(:disabled) .knob { width: 19px; }
  .switch:active:not(:disabled) .track.on .knob { transform: translateX(13px); }

  .toggle {
    flex: 1; min-width: 0; min-height: 44px;
    display: flex; align-items: center; gap: 10px;
    padding: 0; border: 0; background: transparent; color: var(--ink);
    text-align: left; cursor: pointer;
  }
  .toggle:hover .chev { color: var(--ink); }
  .toggle:hover .name.dim { color: var(--ink-soft); }
  .led { width: 8px; height: 8px; border-radius: 4px; flex: none; background: var(--ink-4); transition: background-color .2s, box-shadow .2s; }
  .led.on { background: var(--on); box-shadow: 0 0 0 3px rgb(76 195 138 / .14); }
  .led.fail { background: var(--critical); box-shadow: 0 0 0 3px rgba(229, 83, 75, 0.2); }
  .led.off { background: transparent; border: 1.5px solid var(--ink-4); }
  .name { font-size: 14px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .name.dim { color: var(--ink-2); }
  .right { margin-left: auto; font-size: 14px; white-space: nowrap; }
  .right.bad { font-size: 13px; color: var(--critical-ink); }
  .right.muted { font-size: 13px; color: var(--ink-3); }
  .chev { flex: none; color: var(--ink-3); transition: transform .25s var(--ease-out), color .15s; }
  .chev.open { transform: rotate(180deg); }

  .share { display: flex; align-items: center; gap: 10px; padding: 0 26px 0 70px; }
  .track-bar { flex: 1; height: 4px; border-radius: 2px; background: var(--track); overflow: hidden; }
  .fill { height: 4px; border-radius: 2px; transition: width .45s var(--ease-out); }
  .fill.idle { opacity: .45; }
  .pct { width: 32px; text-align: right; font-size: 12px; color: var(--ink-3); }

  .details { display: flex; flex-direction: column; gap: 14px; margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--line-strong); }
  .diag { font-size: 13px; line-height: 1.45; }
  dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 16px; }
  dl div { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  dt { font-size: 11.5px; color: var(--ink-3); }
  dd { font-size: 13px; overflow-wrap: anywhere; }
  .weight { display: flex; align-items: center; justify-content: space-between; gap: 12px; font-size: 13px; color: var(--ink-2); }
  .stepper { display: flex; align-items: center; border: 1px solid var(--surface-3); border-radius: var(--radius-s); overflow: hidden; transition: border-color .18s; }
  .stepper:hover { border-color: var(--hover-line); }
  .stepper button { width: 36px; height: 32px; border: 0; background: transparent; color: var(--ink-2); font-size: 16px; cursor: pointer; transition: color .15s, background-color .15s; }
  .stepper button:hover:not(:disabled) { color: var(--ink); background: var(--surface-2); }
  .stepper button:active:not(:disabled) { background: var(--surface-3); }
  .stepper button:disabled { opacity: .35; cursor: default; }
  .stepper input { width: 44px; height: 32px; padding: 0; border: 0; border-radius: 0; background: transparent; text-align: center; color: var(--ink); -moz-appearance: textfield; }
  .stepper input::-webkit-inner-spin-button { -webkit-appearance: none; }
  .hint { font-size: 12px; line-height: 1.45; color: var(--ink-2); }
</style>
