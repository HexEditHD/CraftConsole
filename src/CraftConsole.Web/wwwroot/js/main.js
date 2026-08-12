// App shell for CYANOTYPE: ruled sidebar, drawing title block carrying
// the readings and server controls in its own cells, hash router.
import { h, icon, toast } from './ui.js';
import { api } from './api.js';
import { connectSse, on } from './bus.js';
import { state, initStore, isAdmin } from './store.js';
import { createServerControls } from './components/server-controls.js';
import { createVitals } from './components/vitals.js';
import { createServerSwitch } from './components/server-switch.js';

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

// Route ids stay as they always were (dashboard/scheduler/editor); only the
// labels shown to the user differ (Health/Tasks/Files).
const NAV_GROUPS = [
  { label: 'Operate',   ids: ['console', 'dashboard', 'players', 'issues'] },
  { label: 'Configure', ids: ['server', 'plugins', 'editor'] },
  { label: 'Automate',  ids: ['backups', 'scheduler', 'settings'] },
];

// The phone bar can't hold ten destinations. Admins get the canonical five;
// an Operator would have two dead admin-only tabs there, so they get the
// next-most-useful non-gated destinations instead.
const PHONE_ADMIN    = ['console', 'dashboard', 'players', 'server', 'settings'];
const PHONE_OPERATOR = ['console', 'dashboard', 'players', 'issues', 'backups'];

let activeCleanup = null;
let navBadges = [];
let vitals, controls, serverSwitch;

// ── Sidebar ─────────────────────────────────────────────────────────────
function buildNav() {
  const nav = document.getElementById('nav');
  nav.innerHTML = '';
  navBadges = [];
  const admin = isAdmin();

  for (const group of NAV_GROUPS) {
    const routes = group.ids
      .map(id => ROUTES.find(r => r.id === id))
      .filter(r => r && (!ADMIN_ONLY_VIEWS.has(r.id) || admin));

    if (!routes.length) continue; // Configure is entirely admin-only

    nav.append(h('div', { class: 'nav-group-label' }, group.label));

    for (const route of routes) {
      const item = h('button', {
        class: 'nav-item', id: `nav-${route.id}`,
        onclick: () => { location.hash = `#/${route.id}`; },
      }, icon(route.icon), h('span', {}, route.title));

      if (route.id === 'issues') {
        const badge = h('span', { class: 'count', style: { display: 'none' } }, '0');
        navBadges.push(badge);
        item.append(badge);
      }
      nav.append(item);
    }
  }
}

function buildPhoneNav() {
  const nav = document.getElementById('mobile-nav');
  nav.innerHTML = '';
  const ids = isAdmin() ? PHONE_ADMIN : PHONE_OPERATOR;

  for (const id of ids) {
    const route = ROUTES.find(r => r.id === id);
    if (!route) continue;
    const item = h('button', {
      class: 'mnav-item', id: `mnav-${route.id}`,
      onclick: () => { location.hash = `#/${route.id}`; },
    }, icon(route.icon), h('span', { class: 'lbl' }, route.title));

    if (route.id === 'issues') {
      const badge = h('span', { class: 'count', style: { display: 'none' } }, '0');
      navBadges.push(badge);
      item.append(badge);
    }
    nav.append(item);
  }
}

function syncIssueBadges() {
  const count = state.issues.length;
  const text = count > 99 ? '99+' : String(count);
  for (const b of navBadges) {
    b.textContent = text;
    b.style.display = count > 0 ? '' : 'none';
  }
}

// ── Router ──────────────────────────────────────────────────────────────
function currentView() {
  const id = location.hash.replace(/^#\//, '') || 'dashboard';
  return ROUTES.find(r => r.id === id) ?? ROUTES[0];
}

function route() {
  let view = currentView();

  if (ADMIN_ONLY_VIEWS.has(view.id) && !isAdmin()) {
    toast('That page needs an admin account.', 'err');
    location.hash = '#/dashboard';
    view = dashboard;
  }

  activeCleanup?.();
  activeCleanup = null;

  document.querySelectorAll('.nav-item, .mnav-item').forEach(el => el.classList.remove('active'));
  document.getElementById(`nav-${view.id}`)?.classList.add('active');
  document.getElementById(`mnav-${view.id}`)?.classList.add('active');

  document.getElementById('page-title').textContent = view.title;
  syncSubtitle(view);

  const outlet = document.getElementById('view');
  outlet.innerHTML = '';
  activeCleanup = view.render(outlet) ?? null;

  // Restart the entrance animation on every route change — removing and
  // re-adding with a reflow between restarts it even when the class didn't
  // change from the previous route.
  outlet.classList.remove('view-in');
  void outlet.offsetWidth;
  outlet.classList.add('view-in');
}

function syncSubtitle(view) {
  const el = document.getElementById('page-sub');
  el.textContent = typeof view.subtitle === 'function' ? view.subtitle() : (view.subtitle ?? '');
}

// ── Masthead ────────────────────────────────────────────────────────────
function buildTopbar() {
  document.getElementById('brand-icon').append(icon('cube'));

  serverSwitch = createServerSwitch();
  document.getElementById('server-switch').replaceWith(serverSwitch.el);
  serverSwitch.el.id = 'server-switch';

  vitals = createVitals();
  document.getElementById('readings').replaceWith(vitals.el);
  vitals.el.id = 'readings';

  controls = createServerControls();
  document.getElementById('controls').replaceWith(controls.el);
  controls.el.id = 'controls';

  syncShell();
}

function syncShell() {
  serverSwitch?.sync();
  vitals?.sync();
  controls?.sync();
  syncIssueBadges();
  syncEulaBanner(state.status?.eulaRequired === true);
  syncSubtitle(currentView());
}

// ── Server context switch ──────────────────────────────────────────────
// store.js has already re-hydrated state for the newly current server by the
// time this fires; a full re-render is the simplest way to guarantee every
// view drops whatever it had for the old one rather than trying to patch
// each view's internal state individually.
function onServerSwitched() {
  route();
  syncShell();
}

// ── EULA banner ─────────────────────────────────────────────────────────
let eulaDismissed = false;

function syncEulaBanner(required) {
  const slot = document.getElementById('banner-slot');
  const existing = slot.querySelector('.banner');
  if (!required) { eulaDismissed = false; existing?.remove(); return; }
  if (existing || eulaDismissed) return;

  slot.append(h('div', { class: 'banner' },
    icon('warning'),
    h('span', {}, 'Mojang requires accepting the Minecraft EULA before this server can run.'),
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
    }, 'Accept EULA'),
    h('button', {
      class: 'btn sm ghost',
      onclick: () => { eulaDismissed = true; slot.querySelector('.banner')?.remove(); },
    }, 'Later')));
}

// ── Connection ──────────────────────────────────────────────────────────
function syncConn() {
  const dot = document.getElementById('conn');
  const label = document.getElementById('conn-label');
  dot.className = `conn ${state.connected ? 'ok' : 'bad'}`;
  dot.title = state.connected ? 'Live connection to CraftConsole' : 'Reconnecting…';
  label.textContent = state.connected ? 'Live' : 'Reconnecting…';
}

// ── Sign out ────────────────────────────────────────────────────────────
function buildSignOut() {
  const btn = document.getElementById('signout');
  btn.append(icon('power'));
  if (state.session) btn.title = `Signed in as ${state.session.username} (${state.session.role}) — click to sign out`;
  btn.addEventListener('click', async () => {
    try { await api.post('/api/auth/logout'); } catch { /* clearing the cookie server-side is best-effort */ }
    location.reload();
  });
}

// ── Boot ────────────────────────────────────────────────────────────────
(async function boot() {
  await initStore();
  buildNav();
  buildPhoneNav();
  buildTopbar();
  buildSignOut();
  syncIssueBadges();
  syncConn();

  on('store:status', syncShell);
  on('store:players', syncShell);
  on('store:metrics', () => vitals?.sync());
  on('store:issues', syncIssueBadges);
  on('store:conn', syncConn);
  on('store:servers', () => serverSwitch?.sync());
  on('store:switched', onServerSwitched);

  connectSse();

  window.addEventListener('hashchange', route);
  route();
})();
