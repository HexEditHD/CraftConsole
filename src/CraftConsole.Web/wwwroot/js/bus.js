// Client event bus + the Server-Sent-Events connection.
const target = new EventTarget();

export function emit(type, detail) {
  target.dispatchEvent(new CustomEvent(type, { detail }));
}

/** Subscribe; returns an unsubscribe function. */
export function on(type, handler) {
  const wrapped = e => handler(e.detail);
  target.addEventListener(type, wrapped);
  return () => target.removeEventListener(type, wrapped);
}

const SSE_EVENTS = [
  'console', 'console-cleared', 'status', 'players', 'issue', 'issues-cleared',
  'metrics', 'tasks', 'task-ran', 'backups', 'backup-run', 'setup',
];

export function connectSse() {
  const es = new EventSource('/api/events');

  for (const type of SSE_EVENTS) {
    es.addEventListener(type, e => {
      try { emit(type, JSON.parse(e.data)); }
      catch { /* malformed frame — skip */ }
    });
  }

  es.onopen = () => emit('sse', { connected: true });
  es.onerror = () => emit('sse', { connected: false }); // EventSource auto-reconnects
  return es;
}
