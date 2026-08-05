// Health (was Dashboard): stat tiles with bottom-pinned progress bars,
// machine gauges, recent issues.
import { h, icon, fmtUptime, timeAgo } from '../ui.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { gauge, thresholdColor } from '../charts.js';

const PEAK_WINDOW = 20; // ~40s at the sampler's ~2s interval

/** Placeholder for a metric this platform cannot report (e.g. machine CPU off Windows/Linux). */
function unavailableGauge(label) {
  return h('div', { class: 'gauge-box' },
    h('div', {
      style: {
        width: '112px', height: '112px', borderRadius: '50%',
        border: '2px dashed var(--color-neutral-700)',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: 'var(--muted-2)', fontSize: '11.5px', textAlign: 'center', padding: '0 14px',
      },
    }, 'Not available'),
    h('div', { class: 'gauge-caption' }, label));
}

function statBar() {
  const fill = h('div', { style: { width: '0%' } });
  return { el: h('div', { class: 'stat-bar' }, fill), fill };
}

export default {
  id: 'dashboard',
  title: 'Health',
  subtitle: 'Process and machine vitals',
  icon: 'pulse',

  render(el) {
    let els = {};

    const build = () => {
      el.innerHTML = '';
      els = {};

      // ── Hero stat tiles ──────────────────────────────────────────────
      els.statusValue = h('div', { class: 'stat-value' }, '—');
      els.statusSub = h('div', { class: 'stat-sub' }, '');
      els.statusBar = statBar();
      els.playersValue = h('div', { class: 'stat-value' }, '0');
      els.playersSub = h('div', { class: 'stat-sub' }, '');
      els.playersBar = statBar();
      els.cpuValue = h('div', { class: 'stat-value' }, '—');
      els.cpuSub = h('div', { class: 'stat-sub' }, '');
      els.cpuBar = statBar();
      els.ramValue = h('div', { class: 'stat-value' }, '—');
      els.ramSub = h('div', { class: 'stat-sub' }, '');
      els.ramBar = statBar();

      const hero = h('div', { class: 'dash-hero' },
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('hardDrives'), 'Status'),
          els.statusValue, els.statusSub, els.statusBar.el),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('usersThree'), 'Players'),
          els.playersValue, els.playersSub, els.playersBar.el),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('lightning'), 'Server CPU'),
          els.cpuValue, els.cpuSub, els.cpuBar.el),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('memory'), 'Server memory'),
          els.ramValue, els.ramSub, els.ramBar.el));

      // ── Machine gauges + issues ──────────────────────────────────────
      els.gauges = h('div', { class: 'gauge-pair' });
      els.issueList = h('div');

      const row = h('div', { class: 'dash-row' },
        h('div', { class: 'card' },
          h('div', { class: 'card-title' }, 'This machine'),
          els.gauges),
        h('div', { class: 'card' },
          h('div', { class: 'card-title' },
            'Recent issues',
            h('span', { class: 'spacer' }),
            h('a', { href: '#/issues', class: 'small' }, 'View all')),
          els.issueList));

      el.append(h('div', { class: 'dash' }, hero, row));
      syncStatus();
      syncMetrics();
      syncIssues();
    };

    const syncStatus = () => {
      const s = state.status;
      const status = s?.status ?? 'Stopped';
      const statusColor =
        status === 'Running' ? 'var(--color-accent)'
        : status === 'Crashed' ? 'var(--lvl-err)'
        : status === 'Stopped' ? 'var(--muted)' : 'var(--lvl-warn)';
      els.statusValue.textContent = status;
      els.statusValue.style.color = statusColor;
      els.statusBar.fill.style.width = '100%';
      els.statusBar.fill.style.background = statusColor;

      const bits = [];
      if (s?.version) bits.push(`v${s.version}`);
      // RCON has no real "since when" to report — the panel only knows when it
      // connected, not when the remote server actually started.
      if (status === 'Running' && (s?.capabilities?.hasUptime ?? true)) bits.push(`up ${fmtUptime(s.uptimeSeconds)}`);
      if (s?.profile?.name) bits.push(s.profile.name);
      els.statusSub.textContent = bits.join(' · ') || 'No profile started yet';

      const max = s?.maxPlayers ?? 20;
      els.playersValue.innerHTML = '';
      els.playersValue.append(String(state.players.length), h('span', { class: 'unit' }, `/ ${max}`));
      els.playersSub.textContent = state.players.slice(0, 4).map(p => p.username).join(', ')
        || (status === 'Running' ? 'Nobody online' : '');
      const playersPct = max > 0 ? Math.min(state.players.length / max * 100, 100) : 0;
      els.playersBar.fill.style.width = `${playersPct}%`;
      els.playersBar.fill.style.background = 'var(--color-accent)';
    };

    const syncMetrics = () => {
      const m = state.metrics;
      const hist = state.metricsHistory;

      // Server process tiles. null means no local process to sample — the
      // server hasn't started yet, or this is an RCON connection, which never
      // has one. Show that plainly rather than drawing an idle 0% server.
      const cpu = m?.serverCpuPercent;
      els.cpuValue.innerHTML = '';
      els.cpuBar.fill.style.width = '0%';
      if (cpu == null) {
        els.cpuValue.title = 'No server process running.';
        els.cpuValue.append('—');
        els.cpuSub.textContent = '';
      } else {
        els.cpuValue.title = '';
        els.cpuValue.style.color = thresholdColor(cpu);
        els.cpuValue.append(cpu.toFixed(0), h('span', { class: 'unit' }, '%'));
        const peak = Math.max(cpu, ...hist.slice(-PEAK_WINDOW).map(x => x.serverCpuPercent ?? 0));
        els.cpuSub.textContent = `peak ${peak.toFixed(0)}% in last 40s`;
        els.cpuBar.fill.style.width = `${Math.min(cpu, 100)}%`;
        els.cpuBar.fill.style.background = thresholdColor(cpu);
      }

      const ram = m?.serverRamMb;
      const ramMax = m?.serverRamMaxMb || 0;
      els.ramValue.innerHTML = '';
      els.ramBar.fill.style.width = '0%';
      if (ram == null) {
        els.ramValue.title = 'No server process running.';
        els.ramValue.append('—');
        els.ramSub.textContent = '';
      } else {
        els.ramValue.title = '';
        els.ramValue.append((ram / 1024).toFixed(1));
        els.ramSub.textContent = `${Math.round(ram)} MB resident`;
        if (ramMax > 0) {
          const pct = Math.min(ram / ramMax * 100, 100);
          els.ramValue.append(h('span', { class: 'unit' }, `/ ${(ramMax / 1024).toFixed(1)} GB`));
          els.ramValue.style.color = thresholdColor(pct);
          els.ramBar.fill.style.width = `${pct}%`;
          els.ramBar.fill.style.background = thresholdColor(pct);
        } else {
          els.ramValue.append(h('span', { class: 'unit' }, 'GB'));
          els.ramValue.style.color = '';
        }
      }

      // Machine gauges. null means the platform can't report the figure — show it
      // as unavailable rather than as a gauge pinned at zero, which reads as idle.
      els.gauges.innerHTML = '';
      const cpuPct = m?.machineCpuPercent;
      const ramPct = m?.machineRamPercent;

      els.gauges.append(
        cpuPct == null
          ? unavailableGauge('CPU')
          : h('div', { class: 'gauge-box' },
              gauge(cpuPct, { label: 'CPU' }),
              h('div', { class: 'gauge-detail', style: { color: thresholdColor(cpuPct) } },
                `${cpuPct.toFixed(0)}% in use`)),
        ramPct == null
          ? unavailableGauge('Memory')
          : h('div', { class: 'gauge-box' },
              gauge(ramPct, { label: 'Memory' }),
              h('div', { class: 'gauge-detail' },
                m.machineRamUsedGb != null && m.machineRamTotalGb != null
                  ? `${m.machineRamUsedGb.toFixed(1)} / ${m.machineRamTotalGb.toFixed(1)} GB`
                  : '—')));
    };

    const syncIssues = () => {
      els.issueList.innerHTML = '';
      const recent = state.issues.slice(-3).reverse();
      if (!recent.length) {
        els.issueList.append(h('div', { class: 'empty', style: { padding: '22px' } },
          icon('checkCircle'),
          h('div', { class: 'empty-sub' }, 'No warnings or errors detected.')));
        return;
      }
      for (const issue of recent) {
        els.issueList.append(h('div', { class: 'issue-row' },
          h('span', { class: `badge ${issue.type === 'Severe' ? 'danger' : 'warn'}` }, issue.type),
          h('span', { class: 'issue-msg', title: issue.message }, issue.message),
          h('time', {}, timeAgo(issue.timestamp))));
      }
    };

    build();

    const offs = [
      on('store:status', syncStatus),
      on('store:players', syncStatus),
      on('store:metrics', syncMetrics),
      on('store:issues', syncIssues),
    ];
    return () => offs.forEach(off => off());
  },
};
