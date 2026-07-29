// Dashboard: status hero, live stat tiles with sparklines, machine gauges,
// recent issues.
import { h, icon, fmtUptime, timeAgo } from '../ui.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { sparkline, gauge, thresholdColor } from '../charts.js';

/** Placeholder for a metric this platform cannot report (e.g. machine CPU off Windows/Linux). */
function unavailableGauge(label) {
  return h('div', { class: 'gauge-box' },
    h('div', {
      style: {
        width: '118px', height: '118px', borderRadius: '50%',
        border: '2px dashed var(--border-strong)',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: 'var(--text-3)', fontSize: '11.5px', textAlign: 'center', padding: '0 14px',
      },
    }, 'Not available'),
    h('div', { class: 'gauge-caption' }, label));
}

export default {
  id: 'dashboard',
  title: 'Dashboard',
  icon: 'gauge',

  render(el) {
    let els = {};

    const build = () => {
      el.innerHTML = '';
      els = {};

      // ── Hero stat tiles ──────────────────────────────────────────────
      els.statusValue = h('div', { class: 'stat-value' }, '—');
      els.statusSub = h('div', { class: 'stat-sub' }, '');
      els.playersValue = h('div', { class: 'stat-value' }, '0');
      els.playersSub = h('div', { class: 'stat-sub' }, '');
      els.cpuValue = h('div', { class: 'stat-value' }, '—');
      els.cpuSpark = h('div', { class: 'stat-spark' });
      els.ramValue = h('div', { class: 'stat-value' }, '—');
      els.ramSpark = h('div', { class: 'stat-spark' });
      els.ramMeter = h('div', { class: 'meter' }, h('div'));

      const hero = h('div', { class: 'dash-hero' },
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('server'), 'Server'),
          els.statusValue, els.statusSub),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('users'), 'Players online'),
          els.playersValue, els.playersSub),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('zap'), 'Server CPU'),
          els.cpuValue, els.cpuSpark),
        h('div', { class: 'card stat-tile' },
          h('div', { class: 'stat-label' }, icon('box'), 'Server memory'),
          els.ramValue, els.ramMeter, els.ramSpark));

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
      els.statusValue.textContent = status;
      els.statusValue.style.color =
        status === 'Running' ? 'var(--accent)'
        : status === 'Crashed' ? 'var(--danger)'
        : status === 'Stopped' ? 'var(--text-2)' : 'var(--warn)';

      const bits = [];
      if (s?.version) bits.push(`v${s.version}`);
      // RCON has no real "since when" to report — the panel only knows when it
      // connected, not when the remote server actually started.
      if (status === 'Running' && (s?.capabilities?.hasUptime ?? true)) bits.push(`up ${fmtUptime(s.uptimeSeconds)}`);
      if (s?.profile?.name) bits.push(s.profile.name);
      els.statusSub.textContent = bits.join(' · ') || 'No profile started yet';

      els.playersValue.innerHTML = '';
      els.playersValue.append(
        String(state.players.length),
        h('span', { class: 'unit' }, `/ ${s?.maxPlayers ?? 20}`));
      els.playersSub.textContent = state.players.slice(0, 4).map(p => p.username).join(', ')
        || (status === 'Running' ? 'Nobody online' : '');
    };

    const syncMetrics = () => {
      const m = state.metrics;
      const hist = state.metricsHistory;

      // Server process tiles. null means no local process to sample — the
      // server hasn't started yet, or this is an RCON connection, which never
      // has one. Show that plainly rather than drawing an idle 0% server.
      const cpu = m?.serverCpuPercent;
      els.cpuValue.innerHTML = '';
      if (cpu == null) {
        els.cpuValue.title = 'No server process running.';
        els.cpuValue.append('—');
      } else {
        els.cpuValue.title = '';
        els.cpuValue.append(cpu.toFixed(0), h('span', { class: 'unit' }, '%'));
      }
      els.cpuSpark.innerHTML = '';
      if (cpu != null) els.cpuSpark.append(sparkline(hist.map(x => x.serverCpuPercent ?? 0), { max: 100 }));

      const ram = m?.serverRamMb;
      const ramMax = m?.serverRamMaxMb || 0;
      els.ramValue.innerHTML = '';
      els.ramMeter.style.display = ram == null ? 'none' : '';
      if (ram == null) {
        els.ramValue.title = 'No server process running.';
        els.ramValue.append('—');
      } else {
        els.ramValue.title = '';
        els.ramValue.append(
          ram >= 1024 ? (ram / 1024).toFixed(1) : String(Math.round(ram)),
          h('span', { class: 'unit' }, ram >= 1024 ? 'GB' : 'MB'));
        if (ramMax > 0) {
          const pct = Math.min(ram / ramMax * 100, 100);
          els.ramMeter.className = `meter ${pct >= 90 ? 'danger' : pct >= 70 ? 'warn' : ''}`;
          els.ramMeter.firstChild.style.width = `${pct}%`;
          els.ramValue.append(h('span', { class: 'unit' }, ` / ${(ramMax / 1024).toFixed(1)} GB`));
        }
      }
      els.ramSpark.innerHTML = '';
      if (ram != null) els.ramSpark.append(sparkline(hist.map(x => x.serverRamMb ?? 0), { max: ramMax || null }));

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
      const recent = state.issues.slice(-8).reverse();
      if (!recent.length) {
        els.issueList.append(h('div', { class: 'empty', style: { padding: '22px' } },
          icon('check'),
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
