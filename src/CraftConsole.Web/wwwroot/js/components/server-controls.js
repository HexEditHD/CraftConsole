// Start/Restart/Stop controls for the active server, with capability gating.
// Shared by the topbar and the active profile's row in the Server view so the
// gating logic and post-start navigation only need to live in one place.
import { h, icon, toast, confirmDialog } from '../ui.js';
import { api } from '../api.js';
import { state } from '../store.js';

export function createServerControls() {
  const startLabel = h('span', { class: 'label' }, 'Start');
  const btnStart = h('button', { class: 'btn primary sm', onclick: start }, icon('play'), startLabel);
  const btnRestart = h('button', { class: 'btn sm icon-only', title: 'Restart', onclick: restart }, icon('refresh'));
  const btnStop = h('button', { class: 'btn danger sm', onclick: stop }, icon('stop'), 'Stop');

  const el = h('div', { style: { display: 'flex', gap: '6px', alignItems: 'center' } }, btnStart, btnRestart, btnStop);

  async function start() {
    btnStart.disabled = true;
    try {
      await api.post('/api/server/start', {});
      toast('Server starting…');
      location.hash = '#/console';
    } catch (err) {
      toast(err.message, 'err');
      sync();
    }
  }

  async function stop() {
    if (!await confirmDialog('Stop server', 'Stop the Minecraft server? Connected players will be disconnected.', { danger: true, okLabel: 'Stop server' }))
      return;
    try {
      await api.post('/api/server/stop');
      toast('Stopping server…');
    } catch (err) { toast(err.message, 'err'); }
  }

  async function restart() {
    if (!await confirmDialog('Restart server', 'Restart the Minecraft server now?', { okLabel: 'Restart' }))
      return;
    try {
      toast('Restarting server…');
      await api.post('/api/server/restart');
    } catch (err) { toast(err.message, 'err'); }
  }

  function sync() {
    const s = state.status;
    const status = s?.status ?? 'Stopped';
    // Defaults match a managed server, so the buttons behave exactly as before
    // until the first /api/status response actually carries capabilities.
    const caps = s?.capabilities ?? { canStart: true, canStop: true, canRestart: true };
    const running = status === 'Running';
    const busy = status === 'Starting' || status === 'Stopping';
    const isRcon = s?.profile?.mode === 'Rcon';

    btnStart.disabled = running || busy || !caps.canStart;
    startLabel.textContent = isRcon ? 'Connect' : 'Start';
    btnStop.disabled = !running || !caps.canStop;
    btnRestart.disabled = !running || !caps.canRestart;
    btnRestart.title = caps.canRestart
      ? 'Restart'
      : 'This server is connected over RCON and can’t be restarted from here';
  }

  sync();
  return { el, sync };
}
