// Tiny DOM + UI helpers: element builder, toasts, modals, formatters.
import { icon } from './icons.js';

/** h('div', {class:'x', onclick:fn, dataset:{id:1}}, child1, 'text', …) */
export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value == null) continue;
    if (key === 'class') el.className = value;
    else if (key === 'dataset') Object.assign(el.dataset, value);
    else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (key in el && key !== 'list' && key !== 'type') el[key] = value;
    else el.setAttribute(key, value);
  }
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    el.append(child.nodeType ? child : document.createTextNode(child));
  }
  return el;
}

// ── Toasts ──────────────────────────────────────────────────────────────
export function toast(message, kind = 'ok', ms = 3200) {
  const root = document.getElementById('toasts');
  const el = h('div', { class: `toast ${kind}` },
    icon(kind === 'err' ? 'alert' : 'check'),
    h('span', {}, message));
  root.append(el);
  setTimeout(() => {
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 220);
  }, ms);
}

// ── Modals ──────────────────────────────────────────────────────────────
export function modal({ title, body, actions = [], wide = false, onClose }) {
  const root = document.getElementById('modal-root');

  const close = () => {
    backdrop.remove();
    document.removeEventListener('keydown', onKey);
    onClose?.();
  };

  const onKey = e => { if (e.key === 'Escape') close(); };
  document.addEventListener('keydown', onKey);

  const backdrop = h('div', {
    class: 'modal-backdrop',
    onclick: e => { if (e.target === backdrop) close(); },
  },
    h('div', { class: `modal${wide ? ' wide' : ''}`, role: 'dialog', 'aria-label': title },
      h('div', { class: 'modal-head' },
        h('h2', {}, title),
        h('button', { class: 'btn ghost sm icon-only', onclick: close, 'aria-label': 'Close' }, icon('x'))),
      h('div', { class: 'modal-body' }, body),
      actions.length
        ? h('div', { class: 'modal-foot' },
            ...actions.map(a => h('button', {
              class: `btn ${a.kind ?? ''}`,
              // Awaited so an async handler can keep the dialog open by
              // returning false. Without this an async handler returns a
              // Promise, which is never === false, and the dialog closed
              // before the work it started had finished or failed.
              onclick: async () => {
                const result = await a.onClick?.(close);
                if (result !== false && a.autoClose !== false) close();
              },
            }, a.label)))
        : null));

  root.append(backdrop);
  backdrop.querySelector('input, textarea, select, button.primary')?.focus();
  return { close };
}

export function confirmDialog(title, message, { danger = false, okLabel = 'Confirm' } = {}) {
  return new Promise(resolve => {
    modal({
      title,
      body: h('p', { class: 'text-2' }, message),
      onClose: () => resolve(false),
      actions: [
        { label: 'Cancel', kind: 'ghost', onClick: () => resolve(false) },
        { label: okLabel, kind: danger ? 'danger' : 'primary', onClick: () => resolve(true) },
      ],
    });
  });
}

/** Prompt for an optional reason. Resolves the string ('' allowed) or null on cancel. */
export function promptReason(title, placeholder = 'Reason (optional)') {
  return new Promise(resolve => {
    let value = null;
    const input = h('input', { class: 'input', placeholder, onkeydown: e => {
      if (e.key === 'Enter') { value = input.value; m.close(); }
    }});
    const m = modal({
      title,
      body: input,
      onClose: () => resolve(value),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        { label: 'OK', kind: 'primary', onClick: () => { value = input.value; } },
      ],
    });
  });
}

// ── Formatters ──────────────────────────────────────────────────────────
export function fmtUptime(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
  if (s >= 60) return `${Math.floor(s / 60)}m ${s % 60}s`;
  return `${s}s`;
}

export function fmtSize(bytes) {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function timeAgo(iso) {
  if (!iso) return '—';
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 10) return 'just now';
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

export function fmtClock(iso, { date = false } = {}) {
  const d = new Date(iso);
  const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
  return date ? `${d.toLocaleDateString()} ${time}` : time;
}

export function debounce(fn, ms) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}

export { icon };
