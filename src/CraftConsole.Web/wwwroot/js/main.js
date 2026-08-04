// App shell: navigation, hash router, topbar status cluster, EULA banner.
import { h, icon, toast } from './ui.js';
import { api } from './api.js';
import { connectSse, on } from './bus.js';
import { state, initStore, isAdmin } from './store.js';
import { createServerControls } from './components/server-controls.js';

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

// These views are entirely gated on Admin-only endpoints server-side — every
// action on them would 403 for an Operator, so there's nothing useful to show.
const ADMIN_ONLY_VIEWS = new Set(['server', 'plugins', 'editor', 'scheduler', 'settings']);

let activeCleanup = null;
let issueBadge = null;

// ── Navigation ──────────────────────────────────────────────────────────
function buildNav() {
  const nav = document.getElementById('nav');
  nav.innerHTML = '';
  const admin = isAdmin();
  for (const route of ROUTES) {
    if (ADMIN_ONLY_VIEWS.has(route.id) && !admin) continue;

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
  let view = ROUTES.find(r => r.id === id) ?? ROUTES[0];

  if (ADMIN_ONLY_VIEWS.has(view.id) && !isAdmin()) {
    toast('That page needs an admin account.', 'err');
    location.hash = '#/dashboard';
    view = dashboard;
  }

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
let pill, playersChip, profileChip, controls;

function buildTopbar() {
  const cluster = document.getElementById('topbar-cluster');

  profileChip = h('button', {
    class: 'profile-chip',
    title: 'Manage server profiles',
    onclick: () => { location.hash = '#/server'; },
  }, icon('server'), h('span', { class: 'name' }, 'No profile'));

  pill = h('span', { class: 'status-pill stopped' }, 'Stopped');

  playersChip = h('span', { class: 'badge', title: 'Players online' }, '0 online');

  controls = createServerControls();

  cluster.append(profileChip, pill, playersChip, controls.el);
  syncTopbar();
}

function syncTopbar() {
  const s = state.status;
  const status = s?.status ?? 'Stopped';

  pill.className = `status-pill ${status.toLowerCase()}`;
  pill.textContent = status;

  playersChip.textContent = `${state.players.length} online`;

  profileChip.querySelector('span.name').textContent = s?.profile?.name ?? 'No profile';

  controls.sync();

  syncEulaBanner(s?.eulaRequired === true);
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
  const foot = document.querySelector('.sidebar-foot');

  if (state.session) {
    foot.append(h('span', {
      class: 'whoami', style: { marginLeft: 'auto' },
      title: `Signed in as ${state.session.username} (${state.session.role})`,
    }, `${state.session.username} · ${state.session.role}`));
  }

  foot.append(
    h('button', {
      class: 'btn ghost sm icon-only', title: 'Sign out',
      style: { marginLeft: state.session ? '' : 'auto' },
      onclick: async () => {
        try { await api.post('/api/auth/logout'); } catch { /* clearing the cookie server-side is best-effort */ }
        location.reload();
      },
    }, icon('power')));
}

// ── Boot ────────────────────────────────────────────────────────────────
(async function boot() {
  await initStore();
  buildNav();
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
