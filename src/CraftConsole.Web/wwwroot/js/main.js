// App shell: header (brand, profile, vitals, issues bell, server controls),
// icon rail, hash router, EULA banner.
import { h, icon, toast } from './ui.js';
import { api } from './api.js';
import { connectSse, on } from './bus.js';
import { state, initStore, isAdmin } from './store.js';
import { createServerControls } from './components/server-controls.js';
import { createVitals } from './components/vitals.js';

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

// Rail groups. Route ids stay as they always were (dashboard/scheduler/editor);
// only the titles/labels shown to the user changed (Health/Tasks/Files).
const RAIL_GROUPS = [
  { kicker: 'Operate', ids: ['console', 'dashboard', 'players', 'issues'] },
  { kicker: 'Configure', ids: ['server', 'plugins', 'editor'] },
  { kicker: 'Automate', ids: ['backups', 'scheduler', 'settings'] },
];

// Phone bottom tab bar. The handoff's fixed five (Console, Health, Players,
// Server, Settings) assumes an admin — Server and Settings 403 for an
// Operator, so an Operator gets the next-most-useful non-gated destinations
// instead (Issues, Backups) rather than two dead tabs.
const PHONE_TABS_ADMIN = ['console', 'dashboard', 'players', 'server', 'settings'];
const PHONE_TABS_OPERATOR = ['console', 'dashboard', 'players', 'issues', 'backups'];

let activeCleanup = null;
let railIssueBadge = null;
let bellIssueBadge = null;
let phoneIssueBadge = null;

// ── Icon rail ───────────────────────────────────────────────────────────
function buildRail() {
  const rail = document.getElementById('rail');
  rail.innerHTML = '';
  const admin = isAdmin();

  for (const group of RAIL_GROUPS) {
    const routesInGroup = group.ids
      .map(id => ROUTES.find(r => r.id === id))
      .filter(route => route && (!ADMIN_ONLY_VIEWS.has(route.id) || admin));

    if (!routesInGroup.length) continue; // e.g. Configure is entirely admin-only

    rail.append(h('div', { class: 'rail-kicker' }, group.kicker));

    for (const route of routesInGroup) {
      const item = h('button', {
        class: 'rail-item',
        id: `rail-${route.id}`,
        onclick: () => { location.hash = `#/${route.id}`; },
      },
        icon(route.icon),
        h('span', { class: 'rail-label' }, route.title));

      if (route.id === 'issues') {
        railIssueBadge = h('span', { class: 'count-badge', style: { display: 'none' } }, '0');
        item.append(railIssueBadge);
      }
      rail.append(h('div', { class: 'rail-item-wrap' }, item));
    }
  }
}

function updateIssueBadge() {
  const count = state.issues.length;
  const text = count > 99 ? '99+' : String(count);
  for (const badge of [railIssueBadge, bellIssueBadge, phoneIssueBadge]) {
    if (!badge) continue;
    badge.textContent = text;
    badge.style.display = count > 0 ? '' : 'none';
  }
}

// ── Phone tab bar ───────────────────────────────────────────────────────
function buildPhoneTabs() {
  const nav = document.getElementById('phone-tabs');
  nav.innerHTML = '';
  phoneIssueBadge = null;
  const ids = isAdmin() ? PHONE_TABS_ADMIN : PHONE_TABS_OPERATOR;

  for (const id of ids) {
    const route = ROUTES.find(r => r.id === id);
    if (!route) continue;
    const item = h('button', {
      class: 'phone-tab',
      id: `phone-tab-${route.id}`,
      onclick: () => { location.hash = `#/${route.id}`; },
    },
      icon(route.icon),
      h('span', { class: 'rail-label' }, route.title));

    if (route.id === 'issues') {
      phoneIssueBadge = h('span', { class: 'count-badge', style: { display: 'none' } }, '0');
      item.append(phoneIssueBadge);
    }
    nav.append(item);
  }
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

  document.querySelectorAll('.rail-item, .phone-tab').forEach(el => el.classList.remove('active'));
  document.getElementById(`rail-${view.id}`)?.classList.add('active');
  document.getElementById(`phone-tab-${view.id}`)?.classList.add('active');

  document.getElementById('page-title').textContent = view.title;
  syncSubtitle(view);

  const outlet = document.getElementById('view');
  outlet.innerHTML = '';
  activeCleanup = view.render(outlet) ?? null;

  // Retrigger the entrance animation on every route change — removing and
  // re-adding the class (with a reflow between) restarts it even when the
  // class name didn't change from the previous route.
  outlet.classList.remove('view-enter');
  void outlet.offsetWidth;
  outlet.classList.add('view-enter');
}

function syncSubtitle(view) {
  const el = document.getElementById('page-subtitle');
  if (!view) return;
  el.textContent = typeof view.subtitle === 'function' ? view.subtitle() : (view.subtitle ?? '');
}

function currentView() {
  const id = location.hash.replace(/^#\//, '') || 'dashboard';
  return ROUTES.find(r => r.id === id) ?? ROUTES[0];
}

// ── Header ──────────────────────────────────────────────────────────────
let vitals, controls;

function buildHeader() {
  document.getElementById('brand-icon').append(icon('cube'));
  document.getElementById('profile-caret').append(icon('caretDown'));
  document.getElementById('profile-btn').addEventListener('click', () => { location.hash = '#/server'; });

  document.getElementById('bell-icon').append(icon('bell'));
  bellIssueBadge = document.getElementById('issues-badge');
  document.getElementById('issues-bell').addEventListener('click', () => { location.hash = '#/issues'; });

  vitals = createVitals();
  document.getElementById('vitals-slot').replaceWith(vitals.el);
  vitals.el.id = 'vitals-slot';

  controls = createServerControls();
  document.getElementById('server-toggle-slot').append(controls.el);

  syncHeader();
}

function syncHeader() {
  const s = state.status;
  const status = s?.status ?? 'Stopped';

  const dot = document.getElementById('profile-dot');
  dot.className = `profile-dot${status === 'Running' ? ' running' : ''}`;

  document.getElementById('profile-name').textContent = s?.profile?.name ?? 'No profile';

  const meta = document.getElementById('profile-meta');
  meta.textContent = s?.profile
    ? (s.profile.mode === 'Rcon' ? 'RCON' : [s.profile.type, s.profile.minecraftVersion].filter(Boolean).join(' '))
    : '';

  vitals?.sync();
  controls?.sync();
  updateIssueBadge();
  syncEulaBanner(s?.eulaRequired === true);
  syncSubtitle(currentView());
}

// ── EULA banner ─────────────────────────────────────────────────────────
let eulaDismissed = false;

function syncEulaBanner(required) {
  const slot = document.getElementById('banner-slot');
  const existing = slot.querySelector('.banner');
  if (!required) { eulaDismissed = false; existing?.remove(); return; }
  if (existing || eulaDismissed) return;

  slot.append(h('div', { class: 'banner' },
    icon('warningCircle'),
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

// ── Connection indicator ────────────────────────────────────────────────
function syncConn() {
  const dot = document.getElementById('conn-dot');
  dot.className = `conn-dot ${state.connected ? 'ok' : 'bad'}`;
  dot.title = state.connected ? 'Live connection to CraftConsole' : 'Reconnecting…';
}

// ── Sign out ────────────────────────────────────────────────────────────
function buildSignOut() {
  const btn = document.getElementById('sign-out-btn');
  btn.append(icon('power'));
  btn.setAttribute('aria-label', 'Sign out');
  if (state.session) btn.title = `Signed in as ${state.session.username} (${state.session.role}) — click to sign out`;
  btn.addEventListener('click', async () => {
    try { await api.post('/api/auth/logout'); } catch { /* clearing the cookie server-side is best-effort */ }
    location.reload();
  });
}

// ── Boot ────────────────────────────────────────────────────────────────
(async function boot() {
  await initStore();
  buildRail();
  buildPhoneTabs();
  buildHeader();
  buildSignOut();
  updateIssueBadge();

  on('store:status', syncHeader);
  on('store:players', syncHeader);
  on('store:metrics', () => vitals?.sync());
  on('store:issues', updateIssueBadge);
  on('store:conn', syncConn);

  connectSse();

  window.addEventListener('hashchange', route);
  route();
})();
