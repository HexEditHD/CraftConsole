// App shell: navigation, hash router, topbar status cluster, EULA banner.
import { h, icon, toast, confirmDialog } from './ui.js';
import { api } from './api.js';
import { connectSse, on } from './bus.js';
import { state, initStore } from './store.js';

import dashboard from './views/dashboard.js';
import consoleView from './views/console.js';
import players from './views/players.js';
import issues from './views/issues.js';
import server from './views/server.js';
import plugins from './views/plugins.js';
import editor from './views/editor.js';
import backups from './views/backups.js';
import scheduler from './views/scheduler.js';
import settings from './views/settings.js';

const ROUTES = [dashboard, consoleView, players, issues, server, plugins, editor, backups, scheduler, settings];

let activeCleanup = null;
let issueBadge = null;

// ── Navigation ──────────────────────────────────────────────────────────
function buildNav() {
  const nav = document.getElementById('nav');
  for (const route of ROUTES) {
    const item = h('button', {
      class: 'nav-item',
      id: `nav-${route.id}`,
      onclick: () => { location.hash = `#/${route.id}`; },
    },
      icon(route.icon),
      h('span', { class: 'label' }, route.title));

    if (route.id === 'issues') {
      issueBadge = h('span', { class: 'nav-badge', style: { display: 'none' } }, '0');
      item.append(issueBadge);
    }
    nav.append(item);
  }
}

function updateIssueBadge() {
  if (!issueBadge) return;
  const count = state.issues.length;
  issueBadge.textContent = count > 99 ? '99+' : String(count);
  issueBadge.style.display = count > 0 ? '' : 'none';
}

// ── Router ──────────────────────────────────────────────────────────────
function route() {
  const id = location.hash.replace(/^#\//, '') || 'dashboard';
  const view = ROUTES.find(r => r.id === id) ?? ROUTES[0];

  activeCleanup?.();
  activeCleanup = null;

  document.querySelectorAll('.nav-item').forEach(el => el.classList.remove('active'));
  document.getElementById(`nav-${view.id}`)?.classList.add('active');
  document.getElementById('page-title').textContent = view.title;

  const outlet = document.getElementById('view');
  outlet.innerHTML = '';
  activeCleanup = view.render(outlet) ?? null;
}

// ── Topbar cluster ──────────────────────────────────────────────────────
let pill, playersChip, profileChip, btnStart, btnStop, btnRestart;

function buildTopbar() {
  const cluster = document.getElementById('topbar-cluster');

  profileChip = h('button', {
    class: 'profile-chip',
    title: 'Manage server profiles',
    onclick: () => { location.hash = '#/server'; },
  }, icon('server'), h('span', {}, 'No profile'));

  pill = h('span', { class: 'status-pill stopped' }, 'Stopped');

  playersChip = h('span', { class: 'badge', title: 'Players online' }, '0 online');

  btnStart = h('button', { class: 'btn primary sm', onclick: startServer }, icon('play'), 'Start');
  btnRestart = h('button', { class: 'btn sm icon-only', title: 'Restart', onclick: restartServer }, icon('refresh'));
  btnStop = h('button', { class: 'btn danger sm', onclick: stopServer }, icon('stop'), 'Stop');

  cluster.append(profileChip, pill, playersChip, btnStart, btnRestart, btnStop);
  syncTopbar();
}

function syncTopbar() {
  const s = state.status;
  const status = s?.status ?? 'Stopped';

  pill.className = `status-pill ${status.toLowerCase()}`;
  pill.textContent = status;

  playersChip.textContent = `${state.players.length} online`;

  profileChip.querySelector('span').textContent = s?.profile?.name ?? 'No profile';

  const running = status === 'Running';
  const busy = status === 'Starting' || status === 'Stopping';
  btnStart.disabled = running || busy;
  btnStop.disabled = !running;
  btnRestart.disabled = !running;

  syncEulaBanner(s?.eulaRequired === true);
}

async function startServer() {
  btnStart.disabled = true;
  try {
    await api.post('/api/server/start', {});
    toast('Server starting…');
  } catch (err) {
    toast(err.message, 'err');
    syncTopbar();
  }
}

async function stopServer() {
  if (!await confirmDialog('Stop server', 'Stop the Minecraft server? Connected players will be disconnected.', { danger: true, okLabel: 'Stop server' }))
    return;
  try {
    await api.post('/api/server/stop');
    toast('Stopping server…');
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function restartServer() {
  if (!await confirmDialog('Restart server', 'Restart the Minecraft server now?', { okLabel: 'Restart' }))
    return;
  try {
    toast('Restarting server…');
    await api.post('/api/server/restart');
  } catch (err) {
    toast(err.message, 'err');
  }
}

// ── EULA banner ─────────────────────────────────────────────────────────
function syncEulaBanner(required) {
  const slot = document.getElementById('banner-slot');
  const existing = slot.querySelector('.banner');
  if (!required) { existing?.remove(); return; }
  if (existing) return;

  slot.append(h('div', { class: 'banner' },
    icon('alert'),
    h('span', {}, 'Mojang requires accepting the Minecraft EULA before the server can run.'),
    h('span', { class: 'spacer' }),
    h('button', {
      class: 'btn sm primary',
      onclick: async function () {
        this.disabled = true;
        try {
          await api.post('/api/server/eula/accept');
          toast('EULA accepted — start the server again.');
        } catch (err) { toast(err.message, 'err'); this.disabled = false; }
      },
    }, 'Accept EULA')));
}

// ── Connection indicator ────────────────────────────────────────────────
function syncConn() {
  const dot = document.getElementById('conn-dot');
  const label = document.getElementById('conn-label');
  dot.className = `conn-dot ${state.connected ? 'ok' : 'bad'}`;
  dot.title = state.connected ? 'Live connection to CraftConsole' : 'Reconnecting…';
  label.textContent = state.connected ? 'Live' : 'Reconnecting…';
}

// ── Logout ──────────────────────────────────────────────────────────────
function buildLogout() {
  document.querySelector('.sidebar-foot').append(
    h('button', {
      class: 'btn ghost sm icon-only', title: 'Sign out',
      style: { marginLeft: 'auto' },
      onclick: async () => {
        try { await api.post('/api/auth/logout'); } catch { /* clearing the cookie server-side is best-effort */ }
        location.reload();
      },
    }, icon('power')));
}

// ── Boot ────────────────────────────────────────────────────────────────
(async function boot() {
  buildNav();
  await initStore();
  buildTopbar();
  buildLogout();
  updateIssueBadge();

  on('store:status', syncTopbar);
  on('store:players', syncTopbar);
  on('store:issues', updateIssueBadge);
  on('store:conn', syncConn);

  connectSse();

  window.addEventListener('hashchange', route);
  route();
})();
