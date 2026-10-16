<script>
  import { app, run, ask } from '../lib/store.svelte.js';
  import Toggle from './Toggle.svelte';

  const s = $derived(app.state);
  let port = $state(1080);
  let portFocused = false;
  $effect(() => { if (!portFocused) port = s.proxy.port; });

  const modes = [
    [0, 'Proporcional aos pesos', 'Cada nova conexão vai para um link, na proporção dos pesos.'],
    [1, 'Menos conexões', 'Prefere o link com menos conexões abertas, levando o peso em conta.'],
    [2, 'Só um link, com reserva', 'Usa apenas o link de maior peso. Os outros entram só se ele cair.'],
    [3, 'Menor latência', 'Usa o link com o menor ping no momento. Bom para jogos e chamadas; não soma a velocidade dos links.'],
  ];

  const set = (key, value) => run('settings.set', { key, value }).catch(() => {});

  // ajustes que mexem na rede do Windows: sem administrador, oferece reiniciar elevado
  async function setAdmin(key, on, why) {
    try {
      const r = await run('settings.set', { key, value: on });
      if (r && r.needsAdmin) {
        const yes = await ask({
          title: 'Reiniciar como administrador?',
          body: `${why} O app fecha e abre de novo; o Windows vai pedir confirmação.`,
          confirm: 'Reiniciar como administrador',
        });
        if (yes) run('app.restartElevated').catch(() => {});
      }
    } catch {}
  }
  const setAutoRoute = (on) => setAdmin('autoRoute', on,
    'Para tirar um link caído do roteamento, o MultiLink precisa mudar as rotas do Windows, o que exige permissão de administrador.');
  const setCapture = (on) => setAdmin('capture', on,
    'Para capturar todos os programas, o MultiLink cria um adaptador de rede virtual, o que exige permissão de administrador.');

  const c = $derived(s.capture);
  const captureStatus = $derived(
    !c.on ? '' : !s.admin ? '' : !s.proxy.running ? 'Começa quando o MultiLink for ligado.'
    : c.starting ? 'Criando o adaptador virtual…'
    : c.active ? `Ativa: todo o tráfego ${c.ipv6 ? 'IPv4 e IPv6' : 'IPv4'} passa pelo MultiLink.`
    : c.error ? `Não funcionou: ${c.error}` : '',
  );

  async function exitApp() {
    const yes = await ask({
      title: 'Sair do MultiLink?',
      body: 'O balanceamento e o monitoramento de quedas param. O proxy e as rotas do Windows voltam à configuração anterior.',
      confirm: 'Sair',
    });
    if (yes) run('app.exit').catch(() => {});
  }
</script>

<section>
  <h2>Ao ligar</h2>
  <div class="body">
    <Toggle checked={s.proxy.system} onchange={(v) => set('system', v)}
      label="Passar o Windows pelo MultiLink"
      description="Navegadores e apps do sistema usam o balanceamento enquanto o MultiLink está ligado. Ao desligar ou sair, a configuração anterior do Windows volta." />
    <Toggle checked={c.on} onchange={setCapture}
      label="Capturar todos os programas"
      description="Também passa pelo MultiLink o que ignora o proxy do Windows: jogos, chamadas, apps de chat e linha de comando, inclusive UDP. Cria um adaptador de rede virtual enquanto está ligado." />
    {#if c.on && !s.admin}
      <div class="needs-admin">
        <p>Só funciona com o MultiLink aberto como administrador.</p>
        <button class="btn" onclick={() => run('app.restartElevated').catch(() => {})}>Reiniciar como administrador</button>
      </div>
    {:else if captureStatus}
      <p class="status" class:bad={c.error && !c.active && !c.starting}>{captureStatus}</p>
    {/if}
    <Toggle checked={s.proxy.lan} onchange={(v) => set('lan', v)}
      label="Aceitar outros aparelhos da rede"
      description="Celulares e outros computadores podem usar este PC como proxy, na porta {s.proxy.port}." />
    <div class="field">
      <label for="port">Porta do proxy</label>
      <input id="port" class="num" type="number" min="1" max="65535" bind:value={port} disabled={s.proxy.running}
        onfocus={() => (portFocused = true)} onblur={() => { portFocused = false; if (port !== s.proxy.port) set('port', port); }} />
      <span class="help">{s.proxy.running ? 'Desligue o MultiLink para mudar a porta.' : 'SOCKS4, SOCKS5 e HTTP na mesma porta.'}</span>
    </div>
  </div>
</section>

<section>
  <h2>Distribuição</h2>
  <div class="body">
    <fieldset class="modes">
      <legend class="sr-only">Modo de distribuição</legend>
      {#each modes as [value, title, desc]}
        <label class="mode" class:checked={s.settings.mode === value}>
          <input type="radio" name="mode" {value} checked={s.settings.mode === value} onchange={() => set('mode', value)} />
          <span class="t">{title}</span>
          <span class="d">{desc}</span>
        </label>
      {/each}
    </fieldset>
    <Toggle checked={s.settings.quality} onchange={(v) => set('quality', v)}
      label="Considerar perda de pacotes e jitter"
      description="Um link que está perdendo pacotes recebe menos conexões até melhorar. No modo Menor latência, perda e jitter também contam, não só o ping." />
    <Toggle checked={s.settings.affinity} onchange={(v) => set('affinity', v)}
      label="Manter cada site no mesmo link"
      description="Use se algum site (banco, login) desconectar ao trocar de IP. Com isso ligado, downloads de um mesmo site não somam os links." />
    <div class="field">
      <button class="btn" onclick={() => run('measure').catch(() => {})} disabled={s.measuring}>{s.measuring ? 'Medindo…' : 'Medir velocidade e ajustar pesos'}</button>
      <span class="help">Mede cada link por cerca de 8 segundos e define o peso proporcional à velocidade.</span>
    </div>
  </div>
</section>

<section>
  <h2>Quedas</h2>
  <div class="body">
    <Toggle checked={s.settings.autoRoute} onchange={setAutoRoute}
      label="Tirar link caído do roteamento do Windows"
      description="Quando um link cai, o computador inteiro passa a usar os outros, e ele volta sozinho quando a internet retornar." />
    {#if s.settings.autoRoute && !s.admin}
      <div class="needs-admin">
        <p>Só funciona com o MultiLink aberto como administrador.</p>
        <button class="btn" onclick={() => run('app.restartElevated').catch(() => {})}>Reiniciar como administrador</button>
      </div>
    {/if}
    <div class="row">
      <button class="btn" onclick={() => run('adapters.refresh').catch(() => {})}>Atualizar adaptadores</button>
      <button class="btn quiet" onclick={() => run('app.openNetworkConnections').catch(() => {})}>Conexões de Rede</button>
    </div>
  </div>
</section>

<section>
  <h2>Avisos</h2>
  <div class="body">
    <Toggle checked={s.settings.notify} onchange={(v) => set('notify', v)}
      label="Avisar quando um link cair ou voltar"
      description="Notificação do Windows, mesmo com o painel fechado. Também avisa quando um download acelerado termina." />
    <Toggle checked={s.settings.notifySound} onchange={(v) => set('notifySound', v)} disabled={!s.settings.notify}
      label="Tocar som quando um link cair" />
    <Toggle checked={s.settings.hotkeys} onchange={(v) => set('hotkeys', v)}
      label="Atalhos de teclado"
      description="Ctrl+Alt+M liga e desliga o MultiLink. Ctrl+Alt+N abre e fecha este painel. Funcionam de qualquer programa." />
  </div>
</section>

<section>
  <h2>Sistema</h2>
  <div class="body">
    <Toggle checked={s.settings.startup} onchange={(v) => set('startup', v)}
      label="Iniciar com o Windows"
      description={s.admin ? 'Abre na bandeja ao entrar no Windows, já como administrador.' : 'Abre na bandeja ao entrar no Windows.'} />
    <div class="field">
      <button class="btn danger" onclick={exitApp}>Sair do MultiLink</button>
      <span class="help">Fechar a janela só esconde o app na bandeja.</span>
    </div>
  </div>
</section>

<style>
  section { display: grid; gap: 12px; }
  h2 { font-size: 13px; font-weight: 600; color: var(--ink-2); }
  .body { display: grid; gap: 18px; padding-top: 12px; border-top: 1px solid var(--line); }
  .field { display: grid; gap: 6px; justify-items: start; }
  .field > label { font-weight: 500; }
  .field input { width: 120px; }
  .help { color: var(--ink-3); font-size: 12.5px; }
  .row { display: flex; flex-wrap: wrap; gap: 8px; }

  .modes { display: grid; gap: 8px; margin: 0; padding: 0; border: 0; }
  .mode {
    position: relative; display: grid; gap: 2px; padding: 10px 12px;
    border: 1px solid var(--surface-3); border-radius: var(--radius-m);
    cursor: pointer;
    transition: background-color .18s, border-color .18s, box-shadow .18s;
  }
  .mode:hover { background: var(--band); border-color: var(--hover-line); }
  .mode.checked, .mode.checked:hover { border-color: var(--on); box-shadow: inset 0 0 0 1px var(--on); background: rgb(76 195 138 / .04); }
  .mode input { position: absolute; opacity: 0; pointer-events: none; }
  .mode:has(input:focus-visible) { outline: 2px solid var(--focus); outline-offset: 2px; }
  .t { font-weight: 600; font-size: 13.5px; }
  .d { color: var(--ink-3); font-size: 12.5px; }

  .status { margin-top: -10px; padding-left: 52px; color: var(--ink-3); font-size: 12.5px; }
  .status.bad { color: var(--critical-ink); }
  .needs-admin { display: grid; gap: 10px; justify-items: start; margin-top: -6px; padding: 12px; background: var(--band); border-radius: var(--radius-m); font-size: 13px; }
</style>
