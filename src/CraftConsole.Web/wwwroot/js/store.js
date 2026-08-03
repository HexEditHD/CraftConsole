// Central client state, hydrated over REST and kept fresh by SSE events.
import { api } from './api.js';
import { on, emit } from './bus.js';

export const state = {
  status: null,        // /api/status snapshot
  settings: null,      // AppSettings
  system: null,        // /api/system/info snapshot (platform, path separator, default paths)
  metrics: null,       // latest metrics sample
  metricsHistory: [],  // last N samples for sparklines
  players: [],
  consoleEntries: [],
  issues: [],
  connected: false,
};

const HISTORY_LIMIT = 120;

function capConsole() {
  const max = Math.max(100, state.settings?.maxConsoleLines ?? 2000);
  if (state.consoleEntries.length > max)
    state.consoleEntries.splice(0, state.consoleEntries.length - max);
}

export async function initStore() {
  // Hydrate everything in parallel; individual failures shouldn't kill boot.
  const [status, settings, system, consoleEntries, players, issues, metrics] = await Promise.allSettled([
    api.get('/api/status'),
    api.get('/api/settings'),
    api.get('/api/system/info'),
    api.get('/api/console'),
    api.get('/api/players'),
    api.get('/api/issues'),
    api.get('/api/metrics'),
  ]).then(results => results.map(r => (r.status === 'fulfilled' ? r.value : null)));

  state.status = status;
  state.settings = settings;
  state.system = system;
  state.consoleEntries = consoleEntries ?? [];
  state.players = players?.players ?? [];
  state.issues = issues?.issues ?? [];
  state.metrics = metrics && Object.keys(metrics).length ? metrics : null;
  capConsole();

  // ── SSE → state ──────────────────────────────────────────────────────
  on('status', s => { state.status = s; emit('store:status'); });

  on('console', entry => {
    state.consoleEntries.push(entry);
    capConsole();
    emit('store:console', entry);
  });

  on('console-cleared', () => {
    state.consoleEntries = [];
    emit('store:console-cleared');
  });

  on('players', p => { state.players = p.players ?? []; emit('store:players'); });

  on('issue', issue => {
    state.issues.push(issue);
    if (state.issues.length > 500) state.issues.shift();
    emit('store:issues');
  });

  on('issues-cleared', () => { state.issues = []; emit('store:issues'); });

  on('metrics', m => {
    state.metrics = m;
    state.metricsHistory.push({ t: Date.now(), ...m });
    if (state.metricsHistory.length > HISTORY_LIMIT) state.metricsHistory.shift();
    emit('store:metrics');
  });

  on('sse', ({ connected }) => { state.connected = connected; emit('store:conn'); });
}
