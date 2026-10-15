<script>
  import { app, initStore } from './lib/store.svelte.js';
  import Home from './components/Home.svelte';
  import More from './components/More.svelte';
  import History from './components/History.svelte';
  import Toasts from './components/Toasts.svelte';
  import ConfirmDialog from './components/ConfirmDialog.svelte';

  initStore();

  let view = $state('home'); // 'home' | 'more' | 'history'
</script>

<div class="scroll">
  <div class="frame">
    {#if !app.state}
      <!-- esqueleto com a forma da tela inicial enquanto o app responde -->
      <div class="skeleton" role="status" aria-label="Conectando ao MultiLink">
        <span class="sk brand"></span>
        <span class="sk band"></span>
        <span class="sk ring"></span>
        <span class="sk line-m"></span>
        <span class="sk line-l"></span>
        {#each [0, 1, 2] as i (i)}<span class="sk row"></span>{/each}
      </div>
    {:else}
      {#key view}
        <div class="enter">
          {#if view === 'home'}
            <Home onSettings={() => (view = 'more')} onHistory={() => (view = 'history')} />
          {:else if view === 'history'}
            <History onBack={() => (view = 'home')} />
          {:else}
            <More onBack={() => (view = 'home')} />
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
  .skeleton { display: flex; flex-direction: column; align-items: center; gap: 18px; padding-top: 13px; }
  .sk {
    display: block; border-radius: var(--radius-s);
    background: linear-gradient(90deg, var(--surface) 0%, var(--surface-2) 50%, var(--surface) 100%) 0 0 / 200% 100%;
    animation: shimmer 1.4s linear infinite;
  }
  .brand { align-self: flex-start; width: 72px; height: 14px; }
  .band { align-self: stretch; height: 44px; border-radius: 0; margin: 0 -16px 0 -24px; }
  .ring { width: 152px; height: 152px; border-radius: 50%; margin-top: 4px; }
  .line-m { width: 90px; height: 14px; }
  .line-l { width: 150px; height: 40px; margin-bottom: 8px; }
  .row { align-self: stretch; height: 44px; }
  @keyframes shimmer { to { background-position: -200% 0; } }
</style>
