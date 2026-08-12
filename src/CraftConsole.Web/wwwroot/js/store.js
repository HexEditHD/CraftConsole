// Central client state, hydrated over REST and kept fresh by SSE events.
//
// Multi-server: the backend now runs one supervisor per profile ever started
// (see ServerRegistry), but this store still tracks exactly one "current"
// server's live state (status/consoleEntries/players/issues/metrics) — the
// one the switcher has selected. servers[] is the switcher's own list, kept
// live for every server regardless of which one is current.
//
// The legacy unscoped endpoints (/api/status, /api/console, ...) resolve
// server-side to whichever profile is marked active, so switching context
// here calls /api/profiles/{id}/activate first, then re-hydrates through
// those same endpoints — no separate "scoped hydration" path needed. SSE
// payloads carry their own serverId (added by EventBroker's scoped Publish
// overload); this store filters on it.
import { api } from './api.js';
import { on, emit } from './bus.js';

export const state = {
  status: null,        // /api/status snapshot — the current server
  servers: [],          // /api/servers — every profile, with live status merged in, for the switcher
  currentServerId: null,
  settings: null,       // AppSettings
  system: null,         // /api/system/info snapshot (platform, path separator, default paths)
  session: null,        // /api/auth/me snapshot ({username, role})
  metrics: null,        // latest metrics sample, machine + current server merged
  metricsHistory: [],   // last N samples for sparklines — current server only
  players: [],
  consoleEntries: [],
  issues: [],
  connected: false,
};

export const isAdmin = () => state.session?.role === 'Admin';

const HISTORY_LIMIT = 120;

function capConsole() {
  const max = Math.max(100, state.settings?.maxConsoleLines ?? 2000);
  if (state.consoleEntries.length > max)
    state.consoleEntries.splice(0, state.consoleEntries.length - max);
}

/**
 * (Re)loads everything scoped to "the current server" from the legacy
 * endpoints. Trusts state.currentServerId is already correct — set by
 * hydrateServerList() or switchServer(), never derived here. It can't be
 * derived from this response: /api/status's Profile field is null for any
 * server never started (ServerSupervisor.ActiveProfile is only set once
 * StartAsync has actually run), which is the ordinary case for most
 * profiles, not an edge case.
 */
async function hydrateCurrentServer() {
  const [status, consoleEntries, players, issues, metrics] = await Promise.allSettled([
    api.get('/api/status'),
    api.get('/api/console'),
    api.get('/api/players'),
    api.get('/api/issues'),
    api.get('/api/metrics'),
  ]).then(results => results.map(r => (r.status === 'fulfilled' ? r.value : null)));

  state.status = status;
  state.consoleEntries = consoleEntries ?? [];
  state.players = players?.players ?? [];
  state.issues = issues?.issues ?? [];
  state.metrics = metrics && Object.keys(metrics).length ? metrics : null;
  state.metricsHistory = [];
  capConsole();
}

/**
 * The one place state.currentServerId is set from the server's own notion of
 * "active". Exported so any view that creates, edits, or deletes a profile
 * can refresh servers[] — port/working-directory conflict flags only exist
 * on this response, not on /api/profiles, so without a refresh here they'd
 * stay stale until the next switchServer() or a page reload.
 */
export async function hydrateServerList() {
  try {
    const data = await api.get('/api/servers');
    state.servers = data.servers ?? [];
    state.currentServerId = data.activeProfileId ?? state.servers[0]?.id ?? null;
  } catch {
    state.servers = [];
  }
}

export async function initStore() {
  // servers first: hydrateCurrentServer() depends on currentServerId being
  // already set. The rest hydrate in parallel; individual failures shouldn't
  // kill boot.
  await hydrateServerList();

  const [, settings, system, session] = await Promise.allSettled([
    hydrateCurrentServer(),
    api.get('/api/settings'),
    api.get('/api/system/info'),
    api.get('/api/auth/me'),
  ]).then(results => results.map(r => (r.status === 'fulfilled' ? r.value : null)));

  state.settings = settings;
  state.system = system;
  state.session = session;

  // ── SSE → state ──────────────────────────────────────────────────────
  on('status', s => {
    // Applies to every server, not just the current one, so the switcher's
    // status dots stay live for servers you aren't looking at.
    const idx = state.servers.findIndex(x => x.id === s.serverId);
    if (idx >= 0) {
      // The spread carries portConflict/workingDirectoryConflict forward
      // unchanged — this handler never recomputes them, only hydrateServerList()
      // does. A future rewrite that builds this object from scratch instead of
      // spreading would silently drop them.
      state.servers[idx] = { ...state.servers[idx], status: s.status, playerCount: s.playerCount };
      emit('store:servers');
    }
    if (s.serverId !== state.currentServerId) return;
    state.status = s;
    emit('store:status');
  });

  on('console', entry => {
    if (entry.serverId !== state.currentServerId) return;
    state.consoleEntries.push(entry);
    capConsole();
    emit('store:console', entry);
  });

  on('console-cleared', payload => {
    if (payload.serverId !== state.currentServerId) return;
    state.consoleEntries = [];
    emit('store:console-cleared');
  });

  on('players', p => {
    if (p.serverId !== state.currentServerId) return;
    state.players = p.players ?? [];
    emit('store:players');
  });

  on('issue', issue => {
    if (issue.serverId !== state.currentServerId) return;
    state.issues.push(issue);
    if (state.issues.length > 500) state.issues.shift();
    emit('store:issues');
  });

  on('issues-cleared', payload => {
    if (payload.serverId !== state.currentServerId) return;
    state.issues = [];
    emit('store:issues');
  });

  on('metrics', m => {
    // No serverId: the global, once-per-tick machine sample — applies
    // regardless of which server is current. Field names are unprefixed
    // (cpuPercent/ramUsedGb/...) on the wire; folded here into the
    // machine*-prefixed names the rest of the UI already reads, so this is
    // the only place that distinction matters.
    if (m.serverId == null) {
      state.metrics = {
        ...state.metrics,
        machineCpuPercent: m.cpuPercent,
        machineRamUsedGb: m.ramUsedGb,
        machineRamTotalGb: m.ramTotalGb,
        machineRamPercent: m.ramPercent,
      };
      emit('store:metrics');
      return;
    }

    // Also true for every server, same as 'status' — keeps the switcher's
    // player counts current without needing the full status payload.
    const idx = state.servers.findIndex(x => x.id === m.serverId);
    if (idx >= 0) {
      state.servers[idx] = { ...state.servers[idx], playerCount: m.playerCount };
      emit('store:servers');
    }
    if (m.serverId !== state.currentServerId) return;

    state.metrics = {
      ...state.metrics,
      serverCpuPercent: m.serverCpuPercent,
      serverRamMb: m.serverRamMb,
      serverRamMaxMb: m.serverRamMaxMb,
    };
    // Only the current server's own tick advances history — machine ticks
    // arrive on the same 2s cadence and would otherwise double the push
    // rate, quietly halving the time span state.metricsHistory represents
    // (dashboard.js's "peak in last 40s" label assumes one entry per tick).
    state.metricsHistory.push({ t: Date.now(), ...state.metrics });
    if (state.metricsHistory.length > HISTORY_LIMIT) state.metricsHistory.shift();
    emit('store:metrics');
  });

  on('sse', ({ connected }) => { state.connected = connected; emit('store:conn'); });
}

/**
 * Switches which server is "current": marks it active server-side (the same
 * /activate the Server screen's own "Set active" button already used, now
 * doing double duty), re-hydrates every current-server field from the legacy
 * endpoints, and refreshes the switcher's own list so its status/playerCount
 * reflect the switch immediately rather than waiting for the next SSE tick.
 * A no-op if it's already current.
 */
export async function switchServer(id) {
  if (id === state.currentServerId) return;
  await api.post(`/api/profiles/${id}/activate`);
  // Set directly rather than waiting for hydrateServerList() to report it
  // back — id is already known, and hydrateCurrentServer() needs
  // currentServerId correct *before* it runs, not after.
  state.currentServerId = id;
  await hydrateCurrentServer();
  // Refreshes servers[]' status/playerCount immediately rather than waiting
  // on the next SSE tick, and re-confirms currentServerId from the server —
  // harmless since it was just set to the same id above.
  await hydrateServerList();
  emit('store:switched');
}
