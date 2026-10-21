<script>
  // Tela de ajustes: download acelerado no topo e, abaixo, todos os ajustes.
  import PageHeader from '../../components/PageHeader.svelte';
  import { app } from '../../state/app.svelte.js';
  import DevBanner from './DevBanner.svelte';
  import DownloadPanel from './DownloadPanel.svelte';
  import SettingsForm from './SettingsForm.svelte';

  let { onBack } = $props();
</script>

<PageHeader title="Ajustes" {onBack} />

<div class="sections">
  {#if app.state.dev}<DevBanner />{/if}
  <DownloadPanel />
  <SettingsForm />
</div>

<style>
  .sections { display: grid; gap: 48px; }
</style>
