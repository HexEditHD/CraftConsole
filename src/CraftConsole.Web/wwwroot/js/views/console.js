// Console — the hero screen. Streamed log with a level filter and search,
// command input with history + autocomplete, online-players panel.
import { h, icon, toast, fmtClock, timeAgo, emptyBlock } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { usernameColor } from '../usercolor.js';

const COMMANDS = [
  '/advancement', '/attribute', '/ban', '/ban-ip', '/banlist', '/bossbar',
  '/clear', '/clone', '/damage', '/data', '/datapack', '/debug',
  '/defaultgamemode', '/deop', '/difficulty', '/effect', '/enchant',
  '/execute', '/experience', '/fill', '/fillbiome', '/forceload',
  '/function', '/gamemode', '/gamerule', '/give', '/help', '/item',
  '/kick', '/kill', '/list', '/locate', '/loot', '/me', '/msg',
  '/op', '/pardon', '/pardon-ip', '/particle', '/perf', '/place',
  '/playsound', '/recipe', '/reload', '/return', '/ride',
  '/save-all', '/save-off', '/save-on', '/say', '/schedule',
  '/scoreboard', '/seed', '/setblock', '/setworldspawn',
  '/spawnpoint', '/spreadplayers', '/stop', '/stopsound',
  '/summon', '/tag', '/team', '/teleport', '/tell', '/tellraw',
  '/time', '/title', '/tp', '/trigger', '/weather',
  '/whitelist', '/worldborder', '/xp',
];

const QUICK = ['/list', '/save-all', '/whitelist on', '/weather clear', '/time set day'];

const CHAT_RE = /^<(\w+)> /;
const MAX_DOM_LINES = 3000;

const FILTERS = [['all', 'All'], ['info', 'Info'], ['warn', 'Warn'], ['error', 'Error'], ['chat', 'Chat']];

const isChat = entry => CHAT_RE.test(entry.message);

/** Which of the five filter buckets an entry belongs to. */
function bucketOf(entry) {
  if (isChat(entry)) return 'chat';
  const lvl = (entry.level ?? 'Unknown').toLowerCase();
  if (lvl === 'warn') return 'warn';
  if (lvl === 'error') return 'error';
  if (lvl === 'info' || lvl === 'input') return 'info';
  return null; // debug/unknown — only reachable under "all"
}

export default {
  id: 'console',
  title: 'Console',
  subtitle: () => `Live output from ${state.status?.profile?.name ?? 'the server'}`,
  icon: 'terminal',

  render(el) {
    let levelFilter = 'all';
    let searchText = '';
    let follow = state.settings?.autoScrollConsole ?? true;
    let history = JSON.parse(localStorage.getItem('cc-cmd-history') ?? '[]');
    let historyIndex = -1;
    let selSuggestion = -1;
    let pending = [];
    let rafQueued = false;

    applyColorVars(el);

    const segItems = {};
    const seg = h('div', { class: 'seg' },
      FILTERS.map(([id, label]) => {
        const countEl = h('span', { class: 'count' }, '0');
        const btn = h('button', {
          class: `seg-item${id === 'all' ? ' active' : ''}`,
          onclick: () => {
            levelFilter = id;
            seg.querySelectorAll('.seg-item').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            rebuild();
          },
        }, label, countEl);
        segItems[id] = countEl;
        return btn;
      }));

    const searchInput = h('input', {
      type: 'search', placeholder: 'Filter output',
      oninput: () => { searchText = searchInput.value.toLowerCase(); rebuild(); },
    });

    const followBtn = h('button', {
      class: `follow${follow ? ' on' : ''}`,
      title: 'Auto-scroll to newest output',
      onclick: () => {
        follow = !follow;
        followBtn.classList.toggle('on', follow);
        if (follow) toBottom();
      },
    }, h('span', { class: 'pip' }), 'Follow');

    const clearBtn = h('button', {
      class: 'btn ghost sm icon-only', title: 'Clear console', 'aria-label': 'Clear console',
      onclick: async () => { await api.del('/api/console'); },
    }, icon('eraser'));

    const log = h('div', { class: 'log' });
    log.addEventListener('scroll', () => { if (nearBottom()) jump.style.display = 'none'; });

    const jump = h('button', {
      class: 'btn sm jump', style: { display: 'none' },
      onclick: () => { toBottom(); jump.style.display = 'none'; },
    }, icon('arrowDown'), 'New output');

    // RCON has no log stream — only command replies and synthesized player
    // join/leave — so this view is a transcript there, not a live tail.
    const rconNote = h('div', {
      class: 'hint', style: { padding: '6px 10px', display: 'none', borderBottom: '1px solid var(--rule)' },
    }, 'RCON transcript — command replies and player join/leave only, not the full server log.');

    const toBottom = () => { log.scrollTop = log.scrollHeight; };
    const nearBottom = () => log.scrollHeight - log.scrollTop - log.clientHeight < 60;

    const suggest = h('div', {
      class: 'suggest', style: { display: 'none' },
      onmousedown: e => { if (e.target === suggest) e.preventDefault(); },
    });

    const input = h('input', {
      class: 'input',
      placeholder: 'Run a command — / to autocomplete, ↑ for history',
      onkeydown: onKey,
      oninput: refreshSuggest,
    });

    const sendBtn = h('button', {
      class: 'btn primary send', title: 'Send', 'aria-label': 'Send command', onclick: send,
    }, icon('send'));

    const railList = h('div', { style: { display: 'flex', flexDirection: 'column', gap: 'var(--s2)' } });
    const railHead = h('div', { class: 'rail-head' });
    const railPanel = h('div', { class: 'rail-panel' },
      railHead,
      railList,
      h('div', { class: 'quick' },
        h('div', { class: 'rail-head' }, 'Quick commands'),
        h('div', { class: 'quick-chips' },
          QUICK.map(cmd => h('button', {
            class: 'chip-cmd',
            onclick: () => { input.value = cmd + ' '; input.focus(); },
          }, cmd)))));

    el.append(h('div', { class: 'console-layout' },
      h('div', { class: 'console-main' },
        h('div', { class: 'console-bar' }, seg, h('div', { class: 'console-search' }, icon('search'), searchInput), followBtn, clearBtn),
        rconNote,
        h('div', { style: { position: 'relative', flex: '1', minHeight: '0', display: 'flex', flexDirection: 'column' } }, log, jump),
        h('div', { class: 'composer' }, suggest, h('span', { class: 'caret' }, '>'), input, sendBtn)),
      railPanel));
    el.style.height = '100%';

    function lineNode(entry) {
      const raw = (entry.level ?? 'Unknown').toLowerCase();
      const cls = isChat(entry) ? 'chat' : raw;
      const row = h('div', { class: `line lv-${cls}` });

      if (state.settings?.showTimestamp ?? true)
        row.append(h('span', { class: 't' }, fmtClock(entry.timestamp, { date: state.settings?.showDate })));

      row.append(h('span', { class: 'lv' }, cls === 'input' ? '>' : cls.toUpperCase()));

      const msg = h('span', { class: 'msg' });
      const chat = CHAT_RE.exec(entry.message);
      if (chat) {
        msg.append(
          h('span', { class: 'who', style: { color: usernameColor(chat[1]) } }, `<${chat[1]}>`),
          entry.message.slice(chat[0].length - 1));
      } else {
        msg.textContent = entry.message;
      }
      row.append(msg);
      return row;
    }

    function matches(entry) {
      if (levelFilter !== 'all' && bucketOf(entry) !== levelFilter) return false;
      if (searchText && !entry.message.toLowerCase().includes(searchText)) return false;
      return true;
    }

    function syncCounts() {
      const counts = { all: state.consoleEntries.length, info: 0, warn: 0, error: 0, chat: 0 };
      for (const e of state.consoleEntries) {
        const b = bucketOf(e);
        if (b) counts[b]++;
      }
      for (const [id, node] of Object.entries(segItems)) node.textContent = String(counts[id] ?? 0);
    }

    function rebuild() {
      log.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const entry of state.consoleEntries)
        if (matches(entry)) frag.append(lineNode(entry));
      log.append(frag);
      syncCounts();

      if (!frag.childElementCount && !log.childElementCount && !state.consoleEntries.length) {
        const hasStream = state.status?.capabilities?.hasConsoleStream ?? true;
        log.append(emptyBlock('terminal',
          hasStream ? 'Console is quiet' : 'No activity yet',
          hasStream
            ? 'Start the server to see live output here.'
            : 'Connect to this server and send a command — RCON shows replies and player activity here, not the full server log.'));
      }
      toBottom();
    }

    function syncCaps() {
      const hasStream = state.status?.capabilities?.hasConsoleStream ?? true;
      rconNote.style.display = hasStream ? 'none' : '';
    }

    function flush() {
      rafQueued = false;
      if (!pending.length) return;
      const stick = follow && nearBottom();
      const frag = document.createDocumentFragment();
      for (const entry of pending) {
        if (!matches(entry)) continue;
        const node = lineNode(entry);
        node.classList.add('new'); // only newly arrived lines animate — never the backlog
        frag.append(node);
      }
      pending = [];
      syncCounts();
      if (!frag.childElementCount) return;

      log.querySelector('.empty')?.remove();
      log.append(frag);
      while (log.childElementCount > MAX_DOM_LINES) log.firstElementChild.remove();

      if (stick) toBottom();
      else jump.style.display = '';
    }

    async function send() {
      const cmd = input.value.trim();
      if (!cmd) return;
      if (history[history.length - 1] !== cmd) {
        history.push(cmd);
        history = history.slice(-100);
        localStorage.setItem('cc-cmd-history', JSON.stringify(history));
      }
      historyIndex = -1;
      input.value = '';
      hideSuggest();
      try { await api.post('/api/server/command', { command: cmd }); }
      catch (err) { toast(err.message, 'err'); }
      input.focus();
    }

    function onKey(e) {
      const open = suggest.style.display !== 'none';
      if (open && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
        e.preventDefault();
        const items = [...suggest.children];
        selSuggestion = e.key === 'ArrowDown'
          ? Math.min(selSuggestion + 1, items.length - 1)
          : Math.max(selSuggestion - 1, 0);
        items.forEach((it, i) => it.classList.toggle('sel', i === selSuggestion));
        items[selSuggestion]?.scrollIntoView({ block: 'nearest' });
        return;
      }
      if (open && e.key === 'Tab') {
        e.preventDefault();
        accept(suggest.children[Math.max(selSuggestion, 0)]?.textContent);
        return;
      }
      if (open && e.key === 'Enter' && selSuggestion >= 0) {
        e.preventDefault();
        accept(suggest.children[selSuggestion]?.textContent);
        return;
      }
      if (e.key === 'Escape') { hideSuggest(); return; }
      if (e.key === 'Enter') { send(); return; }

      if (e.key === 'ArrowUp' && !open) {
        e.preventDefault();
        if (!history.length) return;
        historyIndex = historyIndex === -1 ? history.length - 1 : Math.max(historyIndex - 1, 0);
        input.value = history[historyIndex];
      } else if (e.key === 'ArrowDown' && !open) {
        e.preventDefault();
        if (historyIndex === -1) return;
        if (historyIndex < history.length - 1) {
          historyIndex++;
          input.value = history[historyIndex];
        } else {
          historyIndex = -1;
          input.value = '';
        }
      }
    }

    function refreshSuggest() {
      const value = input.value;
      if (!value.startsWith('/')) { hideSuggest(); return; }
      const found = COMMANDS.filter(c => c.startsWith(value.toLowerCase()));
      if (!found.length || (found.length === 1 && found[0] === value)) { hideSuggest(); return; }
      suggest.innerHTML = '';
      selSuggestion = -1;
      for (const cmd of found)
        suggest.append(h('div', {
          class: 'suggest-item',
          onmousedown: e => { e.preventDefault(); accept(cmd); },
        }, cmd));
      suggest.scrollTop = 0;
      suggest.style.display = '';
    }

    function accept(cmd) {
      if (!cmd) return;
      input.value = cmd + ' ';
      hideSuggest();
      input.focus();
    }

    function hideSuggest() {
      suggest.style.display = 'none';
      selSuggestion = -1;
    }

    function syncRail() {
      railList.innerHTML = '';
      if (!state.players.length) { railPanel.style.display = 'none'; return; }
      railPanel.style.display = '';
      railHead.replaceChildren('Online', h('span', { class: 'tag ok' }, String(state.players.length)));
      for (const p of state.players) {
        railList.append(h('div', { class: 'rail-player' },
          h('span', { class: 'avatar sm', style: { background: p.colorHex } }, p.username[0].toUpperCase()),
          h('div', { style: { minWidth: 0 } },
            h('div', { class: 'who ellipsis' }, p.username),
            h('div', { class: 'meta ellipsis' }, `${timeAgo(p.joinedAt)} · ${p.ipAddress ?? '—'}`))));
      }
    }

    rebuild();
    syncRail();
    syncCaps();
    input.focus();

    const offs = [
      on('store:console', entry => {
        pending.push(entry);
        if (!rafQueued) { rafQueued = true; requestAnimationFrame(flush); }
      }),
      on('store:console-cleared', rebuild),
      on('store:players', syncRail),
      on('store:status', syncCaps),
    ];
    return () => {
      offs.forEach(off => off());
      el.style.height = '';
    };
  },
};

function applyColorVars(el) {
  const s = state.settings;
  if (!s) return;
  el.style.setProperty('--lv-info', s.colorInfo);
  el.style.setProperty('--lv-warn', s.colorWarn);
  el.style.setProperty('--lv-err', s.colorError);
}
