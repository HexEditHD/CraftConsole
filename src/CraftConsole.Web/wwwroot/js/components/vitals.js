// Readings — figures in their own ruled cells of the drawing's title
// block. Reads the ring buffer store.js already keeps
// (state.metricsHistory).
//
// The fourth reading is Players, not TPS: nothing in this backend reports
// TPS (/api/metrics has no such field), and adding a poller would mean
// changing an API contract this work is not allowed to touch.
import { h } from '../ui.js';
import { state } from '../store.js';
import { thresholdColor } from '../charts.js';

function uptimeParts(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  if (s >= 3600) return { value: `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}`, unit: 'm' };
  if (s >= 60) return { value: String(Math.floor(s / 60)), unit: 'm' };
  return { value: String(s), unit: 's' };
}

function reading(key) {
  const v = h('span', { class: 'v' }, '—');
  const u = h('span', { class: 'u' }, '');
  const el = h('div', { class: 'reading' }, h('span', { class: 'k' }, key), h('span', {}, v, u));
  return { el, v, u };
}

export function createVitals() {
  const cpu = reading('CPU');
  const mem = reading('Memory');
  const players = reading('Players');
  const uptime = reading('Uptime');

  const el = h('div', { class: 'readings' }, cpu.el, mem.el, players.el, uptime.el);

  function sync() {
    const m = state.metrics;
    const s = state.status;

    // null means no local process to sample — not started, or RCON, which
    // never has one. Show that plainly rather than an idle-looking 0.
    const c = m?.serverCpuPercent;
    if (c == null) {
      cpu.v.textContent = '—'; cpu.v.style.color = ''; cpu.u.textContent = '';
    } else {
      cpu.v.textContent = c.toFixed(0);
      cpu.v.style.color = thresholdColor(c);
      cpu.u.textContent = '%';
    }

    const ram = m?.serverRamMb;
    const ramMax = m?.serverRamMaxMb || 0;
    if (ram == null) {
      mem.v.textContent = '—'; mem.v.style.color = ''; mem.u.textContent = '';
    } else {
      const pct = ramMax > 0 ? Math.min(ram / ramMax * 100, 100) : null;
      mem.v.textContent = ram >= 1024 ? (ram / 1024).toFixed(1) : String(Math.round(ram));
      mem.v.style.color = pct != null ? thresholdColor(pct) : '';
      mem.u.textContent = ram >= 1024 ? 'GB' : 'MB';
    }

    players.v.textContent = String(state.players.length);
    players.u.textContent = `/${s?.maxPlayers ?? 20}`;

    // hasUptime is false over RCON — the panel only knows when it connected,
    // not when the remote server actually started.
    const hasUptime = s?.capabilities?.hasUptime ?? true;
    if (s?.status !== 'Running' || !hasUptime || s?.uptimeSeconds == null) {
      uptime.v.textContent = '—';
      uptime.u.textContent = '';
    } else {
      const p = uptimeParts(s.uptimeSeconds);
      uptime.v.textContent = p.value;
      uptime.u.textContent = p.unit;
    }
  }

  sync();
  return { el, sync };
}
