<script>
  let { checked = false, label, description = '', hideLabel = false, disabled = false, onchange } = $props();
  const id = 'tg' + Math.random().toString(36).slice(2, 8);
</script>

<div class="toggle" class:compact={hideLabel}>
  <button id={id} role="switch" aria-checked={checked} aria-label={hideLabel ? label : undefined}
    aria-describedby={description ? id + 'd' : undefined} {disabled}
    onclick={() => onchange && onchange(!checked)}>
    <span class="knob"></span>
  </button>
  {#if !hideLabel}
    <label for={id}>
      <span class="label">{label}</span>
      {#if description}<span class="desc" id={id + 'd'}>{description}</span>{/if}
    </label>
  {/if}
</div>

<style>
  .toggle { display: flex; gap: 14px; align-items: flex-start; }
  button {
    flex: none; position: relative;
    width: 38px; height: 22px; margin-top: 1px; padding: 0;
    border: 1px solid var(--line); border-radius: 11px;
    background: var(--surface-3); cursor: pointer;
    transition: background-color .18s, border-color .18s;
  }
  button:hover:not(:disabled):not([aria-checked="true"]) { border-color: var(--hover-line); }
  button[aria-checked="true"] { background: var(--on); border-color: var(--on); }
  button:disabled { opacity: .45; cursor: default; }
  .knob {
    position: absolute; top: 2px; left: 2px;
    width: 16px; height: 16px; border-radius: 50%;
    background: var(--ink-2);
    transition: transform .22s var(--ease-out), width .12s;
  }
  button:active:not(:disabled) .knob { width: 19px; }
  button[aria-checked="true"]:active:not(:disabled) .knob { transform: translateX(13px); }
  button[aria-checked="true"] .knob { transform: translateX(16px); background: var(--page); }
  label { cursor: pointer; display: grid; gap: 4px; }
  .label { font-weight: 500; }
  .desc { color: var(--ink-3); font-size: 12.5px; max-width: 70ch; }
</style>
