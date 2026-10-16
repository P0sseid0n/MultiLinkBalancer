<!-- esqueleto com a forma da tela inicial enquanto o app responde -->
<div class="skeleton" role="status" aria-label="Conectando ao MultiLink">
  <span class="bone brand"></span>
  <span class="bone band"></span>
  <span class="bone ring"></span>
  <span class="bone line-medium"></span>
  <span class="bone line-large"></span>
  {#each [0, 1, 2] as row (row)}<span class="bone row"></span>{/each}
</div>

<style>
  .skeleton { display: flex; flex-direction: column; align-items: center; gap: 18px; padding-top: 13px; }
  .brand { align-self: flex-start; width: 72px; height: 14px; }
  .band { align-self: stretch; height: 44px; border-radius: 0; margin: 0 -16px 0 -24px; }
  .ring { width: 152px; height: 152px; border-radius: 50%; margin-top: 4px; }
  .line-medium { width: 90px; height: 14px; }
  .line-large { width: 150px; height: 40px; margin-bottom: 8px; }
  .row { align-self: stretch; height: 44px; }
</style>
