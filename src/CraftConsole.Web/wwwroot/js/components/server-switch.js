// Title-block server switcher: which server the rest of the shell — console,
// players, issues, metrics — is currently showing. Which *screen* is
// #page-title's job; which *server* is this one's.
import { h, icon } from '../ui.js';
import { state, switchServer } from '../store.js';

function statusClass(status) {
  switch (status) {
    case 'Running': return 'ok';
    case 'Crashed': return 'bad';
    case 'Starting':
    case 'Stopping': return 'warn';
    default: return '';
  }
}

export function createServerSwitch() {
  const dot = h('span', { class: 'server-dot' });
  const label = h('span', { class: 'name' }, '—');
  const trigger = h('button', {
    class: 'server-switch-trigger',
    'aria-haspopup': 'true', 'aria-expanded': 'false',
    onclick: () => (open ? close() : openMenu()),
  }, dot, label, icon('arrowDown'));

  const menu = h('div', { class: 'server-switch-menu', style: { display: 'none' } });
  const el = h('div', { class: 'server-switch' }, trigger, menu);

  let open = false;

  function onOutsideClick(e) { if (!el.contains(e.target)) close(); }
  function onKey(e) { if (e.key === 'Escape') close(); }

  function openMenu() {
    if (trigger.disabled) return;
    open = true;
    trigger.setAttribute('aria-expanded', 'true');
    menu.style.display = '';
    // Capture phase so this fires before a row's own click handler, which
    // would otherwise immediately re-open via the outside-click check below
    // seeing the click as "outside" (it isn't) then racing the row's switch.
    document.addEventListener('click', onOutsideClick, { capture: true });
    document.addEventListener('keydown', onKey);
  }

  function close() {
    if (!open) return;
    open = false;
    trigger.setAttribute('aria-expanded', 'false');
    menu.style.display = 'none';
    document.removeEventListener('click', onOutsideClick, { capture: true });
    document.removeEventListener('keydown', onKey);
  }

  function sync() {
    const current = state.servers.find(s => s.id === state.currentServerId);
    label.textContent = current?.name ?? (state.servers.length ? 'Select a server' : 'No servers yet');
    dot.className = `server-dot ${statusClass(current?.status)}`;

    // A switcher needs something to switch to — one or zero servers just
    // shows the name as plain text, no menu affordance.
    trigger.disabled = state.servers.length < 2;
    trigger.classList.toggle('has-menu', state.servers.length >= 2);
    if (trigger.disabled) close();

    menu.innerHTML = '';
    for (const s of state.servers) {
      const isCurrent = s.id === state.currentServerId;
      const row = h('button', {
        class: `server-switch-item${isCurrent ? ' current' : ''}`,
        onclick: async () => {
          close();
          if (isCurrent) return;
          row.disabled = true;
          try { await switchServer(s.id); }
          finally { row.disabled = false; }
        },
      },
        h('span', { class: `server-dot ${statusClass(s.status)}` }),
        h('span', { class: 'name' }, s.name),
        s.mode === 'Rcon' ? h('span', { class: 'tag' }, 'RCON') : null,
        // Real text, not an icon-only tag with a title — a title-only tooltip
        // gives a screen reader nothing, and doesn't show on touch at all.
        s.portConflict
          ? h('span', {
              class: 'tag warn',
              title: 'Another Managed profile is configured with the same server-port.',
            }, 'Port conflict')
          : null,
        s.workingDirectoryConflict
          ? h('span', {
              class: 'tag warn',
              title: 'Another Managed profile points at the same working directory.',
            }, 'Same folder')
          : null,
        h('span', { class: 'spacer' }),
        h('span', { class: 'count' }, String(s.playerCount)));
      menu.append(row);
    }
  }

  sync();
  return { el, sync };
}
