// Header vitals strip: CPU, Memory, Players, Uptime. Reads the ring buffer
// store.js already keeps (state.metricsHistory) — no new state plumbing.
//
// The design handoff specified a 4th tile of "TPS", but nothing in this
// backend reports TPS (/api/metrics has no such field, and adding a poller
// would mean changing the API contract this refresh is not allowed to
// touch). Players is the same shape — numeric, no sparkline — and the data
// is already in state.players / state.status.maxPlayers.
import { h } from '../ui.js';
import { state } from '../store.js';
import { sparkline, thresholdColor } from '../charts.js';

const SPARK_SAMPLES = 20;

function uptimeParts(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  if (s >= 3600) return { value: `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}`, unit: 'm' };
  if (s >= 60) return { value: String(Math.floor(s / 60)), unit: 'm' };
  return { value: String(s), unit: 's' };
}

function tile(label) {
  const value = h('span', { class: 'vt-value' }, '—');
  const unit = h('span', { class: 'vt-unit' }, '');
  const spark = h('div', { class: 'vt-spark' });
  const el = h('div', { class: 'vitals-tile' },
    h('div', { class: 'vt-labelvalue' },
      h('div', { class: 'vt-label' }, label),
      h('div', {}, value, unit)),
    spark);
  return { el, value, unit, spark };
}

export function createVitals() {
  const cpu = tile('CPU');
  const mem = tile('Memory');
  const players = tile('Players');
  const uptime = tile('Uptime');
  players.spark.remove();
  uptime.spark.remove();

  const el = h('div', { class: 'vitals-strip' }, cpu.el, mem.el, players.el, uptime.el);

  function sync() {
    const m = state.metrics;
    const hist = state.metricsHistory;
    const s = state.status;

    // CPU — null means no local process (not started, or RCON never has one).
    const cpuPct = m?.serverCpuPercent;
    if (cpuPct == null) {
      cpu.value.textContent = '—'; cpu.value.style.color = ''; cpu.unit.textContent = '';
      cpu.spark.innerHTML = '';
    } else {
      cpu.value.textContent = cpuPct.toFixed(0);
      cpu.value.style.color = thresholdColor(cpuPct);
      cpu.unit.textContent = '%';
      cpu.spark.innerHTML = '';
      cpu.spark.append(sparkline(
        hist.slice(-SPARK_SAMPLES).map(x => x.serverCpuPercent ?? 0),
        { width: 40, height: 20, max: 100, color: thresholdColor(cpuPct) }));
    }

    // Memory — same null convention as CPU. Uses the real configured ceiling,
    // never the prototype's fixed 6144 MB.
    const ram = m?.serverRamMb;
    const ramMax = m?.serverRamMaxMb || 0;
    if (ram == null) {
      mem.value.textContent = '—'; mem.value.style.color = ''; mem.unit.textContent = '';
      mem.spark.innerHTML = '';
    } else {
      const ramPct = ramMax > 0 ? Math.min(ram / ramMax * 100, 100) : null;
      mem.value.textContent = ram >= 1024 ? (ram / 1024).toFixed(1) : String(Math.round(ram));
      mem.value.style.color = ramPct != null ? thresholdColor(ramPct) : '';
      mem.unit.textContent = ram >= 1024 ? 'GB' : 'MB';
      mem.spark.innerHTML = '';
      mem.spark.append(sparkline(
        hist.slice(-SPARK_SAMPLES).map(x => x.serverRamMb ?? 0),
        { width: 40, height: 20, max: ramMax || null, color: ramPct != null ? thresholdColor(ramPct) : undefined }));
    }

    // Players — always known, even at 0/0 before a profile is configured.
    players.value.textContent = String(state.players.length);
    players.unit.textContent = `/ ${s?.maxPlayers ?? 20}`;

    // Uptime — gated by capabilities.hasUptime (RCON has no real "since when").
    const hasUptime = s?.capabilities?.hasUptime ?? true;
    if (s?.status !== 'Running' || !hasUptime || s?.uptimeSeconds == null) {
      uptime.value.textContent = '—';
      uptime.unit.textContent = '';
    } else {
      const parts = uptimeParts(s.uptimeSeconds);
      uptime.value.textContent = parts.value;
      uptime.unit.textContent = parts.unit;
    }
  }

  sync();
  return { el, sync };
}
