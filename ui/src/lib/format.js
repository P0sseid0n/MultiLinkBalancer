// Formatação compartilhada. O app envia taxas em bytes/s e totais em bytes.

/** Taxa em bits/s com a unidade mais legível: { value, unit }. */
export function rate(bytesPerSec) {
  const bits = (bytesPerSec || 0) * 8;
  if (bits < 1e6) return { value: bits < 1e3 ? '0' : Math.round(bits / 1e3).toString(), unit: 'kbps' };
  if (bits < 1e9) {
    const mbps = bits / 1e6;
    return { value: mbps < 100 ? decimal(mbps, 1) : Math.round(mbps).toString(), unit: 'Mbps' };
  }
  return { value: decimal(bits / 1e9, 2), unit: 'Gbps' };
}

export function rateText(bytesPerSec) {
  const formatted = rate(bytesPerSec);
  return `${formatted.value} ${formatted.unit}`;
}

/** Megabits/s sem unidade, para os eixos dos gráficos. */
export function mbpsAxis(bytesPerSec) {
  const mbps = (bytesPerSec * 8) / 1e6;
  if (mbps === 0) return '0';
  return mbps >= 10 ? Math.round(mbps).toString() : decimal(mbps, 1);
}

export function bytes(count) {
  if (count == null || count < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let unit = 0;
  let value = count;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : decimal(value, value < 10 ? 1 : 0)} ${units[unit]}`;
}

/** Tempo desde um instante (ms desde 1970), como "há 5 min". */
export function since(epochMs) {
  if (!epochMs) return '';
  const seconds = Math.max(0, Math.round((Date.now() - epochMs) / 1000));
  if (seconds < 5) return 'agora';
  if (seconds < 60) return `há ${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `há ${minutes} min`;
  return `há ${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** Duração entre dois instantes (ms), como "3 min" ou "1 h 20 min". */
export function duration(startMs, endMs) {
  const seconds = Math.round((endMs - startMs) / 1000);
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** Hora e minuto ("14:05"). */
export function clock(epochMs) {
  return new Date(epochMs).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
}

/** Hoje: só a hora; outro dia: dia/mês e hora. */
export function dateTime(epochMs) {
  const date = new Date(epochMs);
  const today = new Date().toDateString() === date.toDateString();
  return today ? clock(epochMs) : date.toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

/** Dia no formato "aaaa-mm-dd" como "Hoje" ou "dd/mm". */
export function dayLabel(isoDay) {
  const [, month, day] = isoDay.split('-');
  const now = new Date();
  const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  return isoDay === today ? 'Hoje' : `${day}/${month}`;
}

export function percent(part, whole) {
  if (!whole) return 0;
  return Math.round((part / whole) * 100);
}

export function capitalize(text) {
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : '';
}

// Cor de identidade de cada link: segue o link (pela posição na lista do app), nunca o ranking.
// Ordem validada para daltonismo (ver app.css): azul, laranja, verde-água, amarelo...
export function seriesColor(index) {
  return `var(--series-${(index % 8) + 1})`;
}

function decimal(value, digits) {
  return value.toFixed(digits).replace('.', ',');
}

function pad(number) {
  return String(number).padStart(2, '0');
}
