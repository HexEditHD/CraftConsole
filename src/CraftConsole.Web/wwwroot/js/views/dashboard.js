// Health — stat tiles, machine gauges, recent issues.
import { h, icon, fmtUptime, timeAgo, emptyBlock } from '../ui.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { gauge, thresholdColor } from '../charts.js';

const PEAK_WINDOW = 20; // ~40s at the sampler's ~2s interval

/** Placeholder for a metric this platform cannot report (e.g. machine CPU off Windows/Linux). */
function unavailableGauge(label) {
  return h('div', { class: 'gauge-box' },
    h('div', { class: 'gauge-na' }, 'Not available'),
    h('div', { class: 'gauge-cap' }, label));
}

function tile(iconName, label) {
  const value = h('div', { class: 'tile-value' }, '—');
  const sub = h('div', { class: 'tile-sub' }, '');
  const fill = h('div', { style: { width: '0%' } });
  const el = h('div', { class: 'card tile' },
    h('div', { class: 'tile-label' }, icon(iconName), label),
    value, sub,
    h('div', { class: 'bar' }, fill));
  return { el, value, sub, fill };
}

export default {
  id: 'dashboard',
  title: 'Health',
  subtitle: 'Process and machine vitals',
  icon: 'pulse',

  render(el) {
    const status  = tile('drives', 'Status');
    const players = tile('users', 'Players');
    const cpu     = tile('bolt', 'Server CPU');
    const mem     = tile('memory', 'Server memory');

    const gauges = h('div', { class: 'gauges' });
    const issues = h('div', {});

    el.append(h('div', { class: 'stack' },
      h('div', { class: 'tiles' }, status.el, players.el, cpu.el, mem.el),
      h('div', { class: 'pair' },
        h('div', { class: 'card' }, h('div', { class: 'card-title' }, 'This machine'), gauges),
        h('div', { class: 'card' },
          h('div', { class: 'card-title' },
            'Recent issues',
            h('span', { class: 'spacer' }),
            h('a', { href: '#/issues', class: 'small' }, 'View all')),
          issues))));

    function syncStatus() {
      const s = state.status;
      const st = s?.status ?? 'Stopped';
      const colour =
        st === 'Running' ? 'var(--good)'
        : st === 'Crashed' ? 'var(--bad)'
        : st === 'Stopped' ? 'var(--fg-mid)' : 'var(--warn)';
      status.value.textContent = st;
      status.value.style.color = colour;
      status.fill.style.width = '100%';
      status.fill.style.background = colour;

      const bits = [];
      if (s?.version) bits.push(`v${s.version}`);
      // RCON has no real "since when" — the panel only knows when it connected.
      if (st === 'Running' && (s?.capabilities?.hasUptime ?? true)) bits.push(`up ${fmtUptime(s.uptimeSeconds)}`);
      if (s?.profile?.name) bits.push(s.profile.name);
      status.sub.textContent = bits.join(' · ') || 'No profile started yet';

      const max = s?.maxPlayers ?? 20;
      players.value.replaceChildren(String(state.players.length), h('span', { class: 'unit' }, `/ ${max}`));
      players.sub.textContent = state.players.slice(0, 4).map(p => p.username).join(', ')
        || (st === 'Running' ? 'Nobody online' : '');
      players.fill.style.width = `${max > 0 ? Math.min(state.players.length / max * 100, 100) : 0}%`;
      players.fill.style.background = 'var(--amber)';
    }

    function syncMetrics() {
      const m = state.metrics;
      const hist = state.metricsHistory;

      // null means no local process to sample — not started, or RCON, which
      // never has one. Show that plainly rather than an idle-looking 0.
      const c = m?.serverCpuPercent;
      cpu.fill.style.width = '0%';
      if (c == null) {
        cpu.value.replaceChildren('—');
        cpu.value.style.color = '';
        cpu.value.title = 'No server process running.';
        cpu.sub.textContent = '';
      } else {
        cpu.value.replaceChildren(c.toFixed(0), h('span', { class: 'unit' }, '%'));
        cpu.value.style.color = thresholdColor(c);
        cpu.value.title = '';
        const peak = Math.max(c, ...hist.slice(-PEAK_WINDOW).map(x => x.serverCpuPercent ?? 0));
        cpu.sub.textContent = `peak ${peak.toFixed(0)}% in last 40s`;
        cpu.fill.style.width = `${Math.min(c, 100)}%`;
        cpu.fill.style.background = thresholdColor(c);
      }

      const ram = m?.serverRamMb;
      const ramMax = m?.serverRamMaxMb || 0;
      mem.fill.style.width = '0%';
      if (ram == null) {
        mem.value.replaceChildren('—');
        mem.value.style.color = '';
        mem.value.title = 'No server process running.';
        mem.sub.textContent = '';
      } else {
        mem.value.title = '';
        const shown = (ram / 1024).toFixed(1);
        if (ramMax > 0) {
          const pct = Math.min(ram / ramMax * 100, 100);
          mem.value.replaceChildren(shown, h('span', { class: 'unit' }, `/ ${(ramMax / 1024).toFixed(1)} GB`));
          mem.value.style.color = thresholdColor(pct);
          mem.fill.style.width = `${pct}%`;
          mem.fill.style.background = thresholdColor(pct);
        } else {
          mem.value.replaceChildren(shown, h('span', { class: 'unit' }, 'GB'));
          mem.value.style.color = '';
        }
        mem.sub.textContent = `${Math.round(ram)} MB resident`;
      }

      // null means the platform can't report the figure — show it as
      // unavailable rather than a gauge pinned at zero, which reads as idle.
      gauges.innerHTML = '';
      const cpuPct = m?.machineCpuPercent;
      const ramPct = m?.machineRamPercent;

      gauges.append(
        cpuPct == null
          ? unavailableGauge('CPU')
          : h('div', { class: 'gauge-box' },
              gauge(cpuPct, { label: 'CPU' }),
              h('div', { class: 'gauge-cap', style: { color: thresholdColor(cpuPct) } }, `${cpuPct.toFixed(0)}% in use`)),
        ramPct == null
          ? unavailableGauge('Memory')
          : h('div', { class: 'gauge-box' },
              gauge(ramPct, { label: 'Memory' }),
              h('div', { class: 'gauge-cap' },
                m.machineRamUsedGb != null && m.machineRamTotalGb != null
                  ? `${m.machineRamUsedGb.toFixed(1)} / ${m.machineRamTotalGb.toFixed(1)} GB`
                  : '—')));
    }

    function syncIssues() {
      issues.innerHTML = '';
      const recent = state.issues.slice(-3).reverse();
      if (!recent.length) {
        issues.append(emptyBlock('check', null, 'No warnings or errors detected.'));
        return;
      }
      for (const i of recent) {
        issues.append(h('div', { class: 'issue-row' },
          h('span', { class: `tag ${i.type === 'Severe' ? 'bad' : 'warn'}` }, i.type),
          h('span', { class: 'msg', title: i.message }, i.message),
          h('time', {}, timeAgo(i.timestamp))));
      }
    }

    syncStatus();
    syncMetrics();
    syncIssues();

    const offs = [
      on('store:status', syncStatus),
      on('store:players', syncStatus),
      on('store:metrics', syncMetrics),
      on('store:issues', syncIssues),
    ];
    return () => offs.forEach(off => off());
  },
};
