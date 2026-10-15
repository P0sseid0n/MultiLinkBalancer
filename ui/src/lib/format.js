// Formatação compartilhada. O app envia taxas em bytes/s e totais em bytes.

export function rate(bytesPerSec) {
  const bits = (bytesPerSec || 0) * 8;
  if (bits < 1e6) return { value: bits < 1e3 ? '0' : Math.round(bits / 1e3).toString(), unit: 'kbps' };
  if (bits < 1e9) {
    const mbps = bits / 1e6;
    return { value: mbps < 100 ? mbps.toFixed(1).replace('.', ',') : Math.round(mbps).toString(), unit: 'Mbps' };
  }
  return { value: (bits / 1e9).toFixed(2).replace('.', ','), unit: 'Gbps' };
}

export function rateText(bytesPerSec) {
  const r = rate(bytesPerSec);
  return `${r.value} ${r.unit}`;
}

export function bytes(b) {
  if (b == null || b < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  let v = b;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${i === 0 ? v : v.toFixed(v < 10 ? 1 : 0).replace('.', ',')} ${units[i]}`;
}

export function since(epochMs) {
  if (!epochMs) return '';
  const s = Math.max(0, Math.round((Date.now() - epochMs) / 1000));
  if (s < 5) return 'agora';
  if (s < 60) return `há ${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `há ${m} min`;
  return `há ${Math.floor(m / 60)} h ${m % 60} min`;
}

export function percent(part, whole) {
  if (!whole) return 0;
  return Math.round((part / whole) * 100);
}

// Cor de identidade de cada link: segue o link (pela posição na lista do app), nunca o ranking.
// Ordem validada para daltonismo (ver docs do projeto): azul, laranja, verde-água, amarelo...
export function seriesVar(index) {
  return `var(--series-${(index % 8) + 1})`;
}
