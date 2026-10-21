<script>
  // Ajustes, em seções: ao ligar, distribuição, quedas, avisos, atualizações (ou desenvolvimento) e sistema.
  import { app, run, fire, ask } from '../../state/app.svelte.js';
  import Toggle from '../../components/Toggle.svelte';
  import { since, clock } from '../../lib/format.js';

  const MODES = [ // [valor, título, descrição]; o valor é o BalanceMode do app
    [0, 'Proporcional aos pesos', 'Cada nova conexão vai para um link, na proporção dos pesos.'],
    [1, 'Menos conexões', 'Prefere o link com menos conexões abertas, levando o peso em conta.'],
    [2, 'Só um link, com reserva', 'Usa apenas o link de maior peso. Os outros entram só se ele cair.'],
    [3, 'Menor latência', 'Usa o link com o menor ping no momento. Bom para jogos e chamadas; não soma a velocidade dos links.'],
  ];

  const appState = $derived(app.state);
  const capture = $derived(appState.capture);
  const update = $derived(appState.update);
  const dev = $derived(appState.dev); // null fora do modo de desenvolvimento
  const sumTest = $derived(appState.sumTest);
  const sumResult = $derived(sumTest.result); // null até o primeiro teste da soma
  const measurePausedUntil = $derived(appState.measurePausedUntil); // ms; null se o servidor de medição não bloqueou há pouco
  const busyMeasuring = $derived(appState.measuring || sumTest.running || measurePausedUntil != null);

  // porta: edição local enquanto o campo tem foco
  let port = $state(1080);
  let editingPort = false;
  $effect(() => { if (!editingPort) port = appState.proxy.port; });
  function savePort() {
    editingPort = false;
    if (port !== appState.proxy.port) setSetting('port', port);
  }

  const setSetting = (key, value) => fire('settings.set', { key, value });
  const restartAsAdmin = () => fire('app.restartElevated');

  // ajustes que mexem na rede do Windows: sem administrador, oferece reiniciar elevado
  async function setAdminSetting(key, enabled, reason) {
    try {
      const result = await run('settings.set', { key, value: enabled });
      if (!result?.needsAdmin) return;
      const confirmed = await ask({
        title: 'Reiniciar como administrador?',
        body: `${reason} O app fecha e abre de novo; o Windows vai pedir confirmação.`,
        confirm: 'Reiniciar como administrador',
      });
      if (confirmed) restartAsAdmin();
    } catch {}
  }
  const setAutoRoute = (enabled) => setAdminSetting('autoRoute', enabled,
    'Para tirar um link caído do roteamento, o MultiLink precisa mudar as rotas do Windows, o que exige permissão de administrador.');
  const setCapture = (enabled) => setAdminSetting('capture', enabled,
    'Para capturar todos os programas, o MultiLink cria um adaptador de rede virtual, o que exige permissão de administrador.');

  const captureStatus = $derived(
    !capture.on || !appState.admin ? ''
    : !appState.proxy.running ? 'Começa quando o MultiLink for ligado.'
    : capture.starting ? 'Criando o adaptador virtual…'
    : capture.active ? `Ativa: todo o tráfego ${capture.ipv6 ? 'IPv4 e IPv6' : 'IPv4'} passa pelo MultiLink.`
    : capture.error ? `Não funcionou: ${capture.error}` : '',
  );
  const captureFailed = $derived(capture.error && !capture.active && !capture.starting);

  const updateStatus = $derived(
    update.state === 'checking' ? 'Procurando…'
    : update.state === 'uptodate' ? `Você está na versão mais recente (${update.current}).`
    : update.state === 'available' ? `Versão ${update.latest} disponível. Você tem a ${update.current}.`
    : update.state === 'downloading' ? `Baixando a versão ${update.latest}… ${update.progress}%`
    : update.state === 'error' ? `Não foi possível procurar: ${update.error}`
    : `Versão ${update.current}.`,
  );

  async function installUpdate() {
    const confirmed = await ask({
      title: `Atualizar para a versão ${update.latest}?`,
      body: 'O MultiLink baixa a versão nova, confere o arquivo e reinicia sozinho. As conexões abertas caem por alguns segundos.',
      confirm: 'Atualizar e reiniciar',
    });
    if (confirmed) fire('update.install');
  }

  async function exitApp() {
    const confirmed = await ask({
      title: 'Sair do MultiLink?',
      body: 'O balanceamento e o monitoramento de quedas param. O proxy e as rotas do Windows voltam à configuração anterior.',
      confirm: 'Sair',
    });
    if (confirmed) fire('app.exit');
  }
</script>

{#snippet needsAdmin()}
  <div class="needs-admin">
    <p>Só funciona com o MultiLink aberto como administrador.</p>
    <button class="btn" onclick={restartAsAdmin}>Reiniciar como administrador</button>
  </div>
{/snippet}

<section>
  <h2>Ao ligar</h2>
  <div class="body">
    <Toggle checked={appState.proxy.system} onchange={(value) => setSetting('system', value)}
      label="Passar o Windows pelo MultiLink"
      description="Navegadores e apps do sistema usam o balanceamento enquanto o MultiLink está ligado. Ao desligar ou sair, a configuração anterior do Windows volta." />
    <Toggle checked={capture.on} onchange={setCapture}
      label="Capturar todos os programas"
      description="Também passa pelo MultiLink o que ignora o proxy do Windows: jogos, chamadas, apps de chat e linha de comando, inclusive UDP. Cria um adaptador de rede virtual enquanto está ligado." />
    {#if capture.on && !appState.admin}
      {@render needsAdmin()}
    {:else if captureStatus}
      <p class="status" class:bad={captureFailed}>{captureStatus}</p>
    {/if}
    <Toggle checked={appState.proxy.lan} onchange={(value) => setSetting('lan', value)}
      label="Aceitar outros aparelhos da rede"
      description="Celulares e outros computadores podem usar este PC como proxy, na porta {appState.proxy.port}." />
    <div class="field">
      <label for="port">Porta do proxy</label>
      <input id="port" class="num" type="number" min="1" max="65535" bind:value={port} disabled={appState.proxy.running}
        onfocus={() => (editingPort = true)} onblur={savePort} />
      <span class="help">{appState.proxy.running ? 'Desligue o MultiLink para mudar a porta.' : 'SOCKS4, SOCKS5 e HTTP na mesma porta.'}</span>
    </div>
  </div>
</section>

<section>
  <h2>Distribuição</h2>
  <div class="body">
    <fieldset class="modes">
      <legend class="sr-only">Modo de distribuição</legend>
      {#each MODES as [value, title, description]}
        <label class="mode" class:checked={appState.settings.mode === value}>
          <input type="radio" name="mode" {value} checked={appState.settings.mode === value} onchange={() => setSetting('mode', value)} />
          <span class="mode-title">{title}</span>
          <span class="mode-description">{description}</span>
        </label>
      {/each}
    </fieldset>
    <Toggle checked={appState.settings.quality} onchange={(value) => setSetting('quality', value)}
      label="Considerar perda de pacotes e jitter"
      description="Um link que está perdendo pacotes recebe menos conexões até melhorar. No modo Menor latência, perda e jitter também contam, não só o ping." />
    <Toggle checked={appState.settings.affinity} onchange={(value) => setSetting('affinity', value)}
      label="Manter cada site no mesmo link"
      description="Use se algum site (banco, login) desconectar ao trocar de IP. Com isso ligado, downloads de um mesmo site não somam os links." />
    {#if measurePausedUntil != null}
      <p class="paused" role="status">O servidor de medição (speed.cloudflare.com) bloqueou temporariamente os pedidos deste PC, depois de muitos testes seguidos. Dá para medir de novo às <span class="num">{clock(measurePausedUntil)}</span>.</p>
    {/if}
    <div class="field">
      <button class="btn" onclick={() => fire('measure')} disabled={busyMeasuring}>{appState.measuring ? 'Medindo…' : 'Medir velocidade e ajustar pesos'}</button>
      <span class="help">Mede cada link por cerca de 8 segundos e define o peso proporcional à velocidade.</span>
    </div>
    <div class="field">
      <button class="btn" onclick={() => fire('measure.sum')} disabled={busyMeasuring}>{sumTest.running ? 'Testando a soma…' : 'Testar a soma dos links'}</button>
      <span class="help">Baixa por todos os links ao mesmo tempo e depois só pelo melhor deles, cerca de 8 segundos cada, para ver quanto a soma rende a mais.</span>
      {#if sumResult}
        <div class="sum" aria-live="polite">
          <p><strong class="num">Soma: {sumResult.totalMbps} Mbps</strong> · melhor link sozinho ({sumResult.bestName}): <span class="num">{sumResult.bestAloneMbps > 0 ? `${sumResult.bestAloneMbps} Mbps` : 'não mediu'}</span></p>
          {#if sumResult.gainPct != null}
            <p class="gain num" class:bad={sumResult.gainPct < 5}>{sumResult.gainPct >= 0 ? '+' : ''}{sumResult.gainPct}% com a soma</p>
          {/if}
          <p class="help">Juntos: {sumResult.perLink.map((link) => `${link.name} ${link.mbps} Mbps`).join(' · ')}. Testado {since(sumResult.at)}.</p>
        </div>
      {/if}
    </div>
  </div>
</section>

<section>
  <h2>Quedas</h2>
  <div class="body">
    <Toggle checked={appState.settings.autoRoute} onchange={setAutoRoute}
      label="Tirar link caído do roteamento do Windows"
      description="Quando um link cai, o computador inteiro passa a usar os outros, e ele volta sozinho quando a internet retornar." />
    {#if appState.settings.autoRoute && !appState.admin}
      {@render needsAdmin()}
    {/if}
    <div class="row">
      <button class="btn" onclick={() => fire('adapters.refresh')}>Atualizar adaptadores</button>
      <button class="btn quiet" onclick={() => fire('app.openNetworkConnections')}>Conexões de Rede</button>
    </div>
  </div>
</section>

<section>
  <h2>Avisos</h2>
  <div class="body">
    <Toggle checked={appState.settings.notify} onchange={(value) => setSetting('notify', value)}
      label="Avisar quando um link cair ou voltar"
      description="Notificação do Windows, mesmo com o painel fechado. Também avisa quando um download acelerado termina." />
    <Toggle checked={appState.settings.notifySound} onchange={(value) => setSetting('notifySound', value)} disabled={!appState.settings.notify}
      label="Tocar som quando um link cair" />
    <Toggle checked={appState.settings.hotkeys} onchange={(value) => setSetting('hotkeys', value)}
      label="Atalhos de teclado"
      description="Ctrl+Alt+M liga e desliga o MultiLink. Ctrl+Alt+N abre e fecha este painel. Funcionam de qualquer programa." />
  </div>
</section>

{#if dev}
<section>
  <h2>Desenvolvimento</h2>
  <div class="body">
    <div class="field">
      <p class="update">Versão {update.current} compilada {since(dev.builtAt)} a partir desta pasta. As atualizações do GitHub ficam desligadas para não trocar este build pelo da release.</p>
      <button class="btn primary" onclick={() => fire('dev.rebuild')} disabled={dev.building}>{dev.building ? 'Recompilando…' : 'Recompilar e reiniciar'}</button>
      <span class="help">Roda o build.ps1 -Restart numa janela do PowerShell. Se falhar, a janela mostra o erro e o app continua aberto.</span>
    </div>
    <div class="row">
      <button class="btn" onclick={() => fire('dev.devTools')}>DevTools (F12)</button>
      <button class="btn" onclick={() => fire('dev.reload')}>Recarregar interface (F5)</button>
    </div>
    <div class="row">
      <button class="btn quiet" onclick={() => fire('dev.openFolder', { folder: 'logs' })}>Pasta de logs</button>
      <button class="btn quiet" onclick={() => fire('dev.openFolder', { folder: 'settings' })}>Pasta de ajustes</button>
    </div>
  </div>
</section>
{:else}
<section>
  <h2>Atualizações</h2>
  <div class="body">
    <Toggle checked={appState.settings.autoUpdate} onchange={(value) => setSetting('autoUpdate', value)}
      label="Procurar atualizações automaticamente"
      description="Uma vez por dia, nas versões publicadas no GitHub. Você escolhe quando instalar." />
    <div class="field">
      <p class="update" class:bad={update.state === 'error'} aria-live="polite">{updateStatus}</p>
      {#if update.state === 'available' && update.error}<p class="update bad">Última tentativa falhou: {update.error}</p>{/if}
      {#if update.state === 'available'}
        <button class="btn primary" onclick={installUpdate}>Atualizar para {update.latest}</button>
      {:else}
        <button class="btn" onclick={() => fire('update.check')} disabled={update.state === 'checking' || update.state === 'downloading'}>Procurar agora</button>
      {/if}
    </div>
  </div>
</section>
{/if}

<section>
  <h2>Sistema</h2>
  <div class="body">
    <Toggle checked={appState.settings.startup} onchange={(value) => setSetting('startup', value)}
      label="Iniciar com o Windows"
      description={appState.admin ? 'Abre na bandeja ao entrar no Windows, já como administrador.' : 'Abre na bandeja ao entrar no Windows.'} />
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
    transition: background-color .18s, border-color .18s, box-shadow .18s, transform .12s var(--ease-out);
  }
  .mode:active { transform: scale(.99); }
  .mode:hover { background: var(--band); border-color: var(--hover-line); }
  .mode.checked, .mode.checked:hover { border-color: var(--on); box-shadow: inset 0 0 0 1px var(--on); background: var(--on-wash); }
  .mode input { position: absolute; opacity: 0; pointer-events: none; }
  .mode:has(input:focus-visible) { outline: 2px solid var(--focus); outline-offset: 2px; }
  .mode-title { font-weight: 600; font-size: 13.5px; }
  .mode-description { color: var(--ink-3); font-size: 12.5px; }

  .paused { margin: 0; padding: 10px 12px; background: var(--band); border-radius: var(--radius-m); font-size: 13px; color: var(--critical-ink); }
  .sum { display: grid; gap: 4px; padding: 10px 12px; background: var(--band); border-radius: var(--radius-m); font-size: 13px; }
  .gain { font-weight: 600; color: var(--good-ink); }
  .gain.bad { color: var(--ink-2); }

  .update { font-size: 13px; color: var(--ink-2); }
  .update.bad { color: var(--critical-ink); }
  .status { margin-top: -10px; padding-left: 52px; color: var(--ink-3); font-size: 12.5px; }
  .status.bad { color: var(--critical-ink); }
  .needs-admin { display: grid; gap: 10px; justify-items: start; margin-top: -6px; padding: 12px; background: var(--band); border-radius: var(--radius-m); font-size: 13px; }
</style>
