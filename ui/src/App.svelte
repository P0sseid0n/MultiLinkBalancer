<script>
  // Raiz da interface: troca entre as telas e mostra os avisos e confirmações globais.
  import { app, initStore } from './state/app.svelte.js';
  import { onMessage } from './api/bridge.js';
  import HomeScreen from './screens/home/HomeScreen.svelte';
  import SettingsScreen from './screens/settings/SettingsScreen.svelte';
  import HistoryScreen from './screens/history/HistoryScreen.svelte';
  import LoadingSkeleton from './components/LoadingSkeleton.svelte';
  import Toasts from './components/Toasts.svelte';
  import ConfirmDialog from './components/ConfirmDialog.svelte';

  initStore();

  let screen = $state('home'); // 'home' | 'settings' | 'history'
  const goHome = () => (screen = 'home');
  let scroller;

  // painel reaberto com a interface que ficou escondida na bandeja: começa da tela inicial, como numa abertura nova
  onMessage((message) => {
    if (message.type !== 'panel.opened') return;
    screen = 'home';
    if (scroller) scroller.scrollTop = 0;
  });
</script>

<div class="scroll" bind:this={scroller}>
  <div class="frame">
    {#if !app.state}
      <LoadingSkeleton />
    {:else}
      {#key screen}
        <div class="enter">
          {#if screen === 'home'}
            <HomeScreen onSettings={() => (screen = 'settings')} onHistory={() => (screen = 'history')} />
          {:else if screen === 'history'}
            <HistoryScreen onBack={goHome} />
          {:else}
            <SettingsScreen onBack={goHome} />
          {/if}
        </div>
      {/key}
    {/if}
  </div>
</div>

<ConfirmDialog />
<Toasts />

<style>
  /* padding direito menor: o espaço da barra de rolagem fica reservado ao lado */
  .frame { min-height: 100%; max-width: 560px; margin: 0 auto; padding: 6px 16px 20px 24px; }
</style>
