// Live console — the hero screen. Streamed log with a segmented level filter
// and search, command input with history + autocomplete, online-players rail
// with quick commands.
import { h, icon, toast, fmtClock, timeAgo } from '../ui.js';
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

const QUICK_COMMANDS = ['/list', '/save-all', '/whitelist on', '/weather clear', '/time set day'];

const CHAT_RE = /^<(\w+)> /;
const MAX_DOM_LINES = 3000;

const FILTERS = [
  ['all', 'All'],
  ['info', 'Info'],
  ['warn', 'Warn'],
  ['error', 'Error'],
  ['chat', 'Chat'],
];

function isChat(entry) { return CHAT_RE.test(entry.message); }

/** Which of the five segmented-control buckets an entry belongs to. */
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
  icon: 'terminalWindow',

  render(el) {
    let levelFilter = 'all';
    let searchText = '';
    let follow = state.settings?.autoScrollConsole ?? true;
    let history = JSON.parse(localStorage.getItem('cc-cmd-history') ?? '[]');
    let historyIndex = -1;
    let selSuggestion = -1;
    let pendingLines = [];
    let rafQueued = false;

    applyColorVars(el);

    // ── Toolbar ──────────────────────────────────────────────────────────
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
            rebuildLog();
          },
        }, label, countEl);
        segItems[id] = { btn, countEl };
        return btn;
      }));

    const searchInput = h('input', {
      type: 'search', placeholder: 'Filter output',
      oninput: () => { searchText = searchInput.value.toLowerCase(); rebuildLog(); },
    });
    const searchField = h('div', { class: 'search-field' }, icon('magnifyingGlass'), searchInput);

    const followDot = h('span', { class: 'dot' });
    const followBtn = h('button', {
      class: `follow-toggle${follow ? ' on' : ''}`,
      title: 'Auto-scroll to newest output',
      onclick: () => {
        follow = !follow;
        followBtn.classList.toggle('on', follow);
        if (follow) scrollToBottom();
      },
    }, followDot, 'Follow');

    const clearBtn = h('button', {
      class: 'toolbar-icon-btn', title: 'Clear console', 'aria-label': 'Clear console',
      onclick: async () => { await api.del('/api/console'); },
    }, icon('eraser'));

    // ── Log area ─────────────────────────────────────────────────────────
    const log = h('div', { class: 'console-log' });
    log.addEventListener('scroll', () => {
      const nearBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 60;
      if (nearBottom) hideJump();
    });

    const jumpBtn = h('button', {
      class: 'btn sm console-jump', style: { display: 'none' },
      onclick: () => { scrollToBottom(); hideJump(); },
    }, icon('arrowDown'), 'New output');

    // RCON has no log stream — only command replies and synthesized player
    // join/leave — so this view is a transcript there, not a live tail.
    const rconNotice = h('div', { class: 'hint', style: { padding: '2px 2px 10px', display: 'none' } },
      'RCON transcript — command replies and player join/leave only, not the full server log.');

    const scrollToBottom = () => { log.scrollTop = log.scrollHeight; };
    const isNearBottom = () => log.scrollHeight - log.scrollTop - log.clientHeight < 60;
    const hideJump = () => { jumpBtn.style.display = 'none'; };

    // ── Composer ─────────────────────────────────────────────────────────
    const suggestBox = h('div', {
      class: 'suggestions', style: { display: 'none' },
      onmousedown: e => { if (e.target === suggestBox) e.preventDefault(); },
    });

    const input = h('input', {
      class: 'input',
      placeholder: 'Run a command — / to autocomplete, ↑ for history',
      onkeydown: onInputKey,
      oninput: refreshSuggestions,
    });

    const sendBtn = h('button', { class: 'btn primary send-btn', title: 'Send', 'aria-label': 'Send command', onclick: send }, icon('paperPlaneRight'));

    // ── Players rail ─────────────────────────────────────────────────────
    const railList = h('div', { style: { display: 'flex', flexDirection: 'column', gap: 'var(--space-2)' } });
    const railHeader = h('div', { class: 'rail-title' });
    const rail = h('div', { class: 'console-rail' }, railHeader, railList, quickCommandsPanel());

    function quickCommandsPanel() {
      return h('div', { class: 'quick-commands' },
        h('div', { class: 'rail-title' }, 'Quick commands'),
        h('div', { class: 'quick-commands-chips' },
          QUICK_COMMANDS.map(cmd => h('button', {
            class: 'chip-cmd',
            onclick: () => { input.value = cmd + ' '; input.focus(); },
          }, cmd))));
    }

    const layout = h('div', { class: 'console-layout' },
      h('div', { class: 'console-main' },
        h('div', { class: 'console-toolbar' },
          seg,
          searchField,
          followBtn,
          clearBtn),
        h('hr', { class: 'rule-fade' }),
        h('div', { style: { position: 'relative', flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' } },
          rconNotice,
          log,
          jumpBtn),
        h('hr', { class: 'rule-fade' }),
        h('div', { class: 'console-input-row' },
          suggestBox,
          h('span', { class: 'prompt' }, '>'),
          input,
          sendBtn)),
      rail);

    el.append(layout);
    el.style.height = '100%';

    // ── Rendering ────────────────────────────────────────────────────────
    function lineNode(entry) {
      const rawLvl = (entry.level ?? 'Unknown').toLowerCase();
      const chat = isChat(entry);
      const cls = chat ? 'chat' : rawLvl;
      const row = h('div', { class: `console-line lvl-${cls}` });

      if (state.settings?.showTimestamp ?? true)
        row.append(h('span', { class: 't' }, fmtClock(entry.timestamp, { date: state.settings?.showDate })));

      row.append(h('span', { class: 'lvl' }, cls === 'input' ? '>' : cls.toUpperCase()));

      const msg = h('span', { class: 'msg' });
      const chatMatch = CHAT_RE.exec(entry.message);
      if (chatMatch) {
        msg.append(
          h('span', { class: 'chat-name', style: { color: usernameColor(chatMatch[1]) } }, `<${chatMatch[1]}>`),
          entry.message.slice(chatMatch[0].length - 1));
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
      for (const entry of state.consoleEntries) {
        const b = bucketOf(entry);
        if (b) counts[b]++;
      }
      for (const [id, { countEl }] of Object.entries(segItems)) countEl.textContent = String(counts[id] ?? 0);
    }

    function rebuildLog() {
      log.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const entry of state.consoleEntries)
        if (matches(entry)) frag.append(lineNode(entry));
      log.append(frag);
      syncCounts();
      if (frag.childElementCount === 0 && log.childElementCount === 0 && !state.consoleEntries.length) {
        const hasStream = state.status?.capabilities?.hasConsoleStream ?? true;
        log.append(h('div', { class: 'empty' },
          icon('terminalWindow'),
          h('div', { class: 'empty-title' }, hasStream ? 'Console is quiet' : 'No activity yet'),
          h('div', { class: 'empty-sub' }, hasStream
            ? 'Start the server to see live output here.'
            : 'Connect to this server and send a command — RCON shows replies and player activity here, not the full server log.')));
      }
      scrollToBottom();
    }

    function syncCapabilities() {
      rconNotice.style.display = (state.status?.capabilities?.hasConsoleStream ?? true) ? 'none' : '';
    }

    function flushPending() {
      rafQueued = false;
      if (!pendingLines.length) return;
      const stick = follow && isNearBottom();
      const frag = document.createDocumentFragment();
      for (const entry of pendingLines) {
        if (!matches(entry)) continue;
        const node = lineNode(entry);
        node.classList.add('new'); // only newly-arrived lines animate — never the backlog
        frag.append(node);
      }
      pendingLines = [];
      syncCounts();
      if (!frag.childElementCount) return;

      log.querySelector('.empty')?.remove();
      log.append(frag);
      while (log.childElementCount > MAX_DOM_LINES) log.firstElementChild.remove();

      if (stick) scrollToBottom();
      else jumpBtn.style.display = '';
    }

    // ── Command input behaviors ──────────────────────────────────────────
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
      hideSuggestions();
      try { await api.post('/api/server/command', { command: cmd }); }
      catch (err) { toast(err.message, 'err'); }
      input.focus();
    }

    function onInputKey(e) {
      const visible = suggestBox.style.display !== 'none';
      if (visible && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
        e.preventDefault();
        const items = [...suggestBox.children];
        selSuggestion = e.key === 'ArrowDown'
          ? Math.min(selSuggestion + 1, items.length - 1)
          : Math.max(selSuggestion - 1, 0);
        items.forEach((it, i) => it.classList.toggle('sel', i === selSuggestion));
        items[selSuggestion]?.scrollIntoView({ block: 'nearest' });
        return;
      }
      if (visible && e.key === 'Tab') {
        e.preventDefault();
        acceptSuggestion(suggestBox.children[Math.max(selSuggestion, 0)]?.textContent);
        return;
      }
      if (visible && e.key === 'Enter' && selSuggestion >= 0) {
        e.preventDefault();
        acceptSuggestion(suggestBox.children[selSuggestion]?.textContent);
        return;
      }
      if (e.key === 'Escape') { hideSuggestions(); return; }
      if (e.key === 'Enter') { send(); return; }

      if (e.key === 'ArrowUp' && !visible) {
        e.preventDefault();
        if (!history.length) return;
        historyIndex = historyIndex === -1 ? history.length - 1 : Math.max(historyIndex - 1, 0);
        input.value = history[historyIndex];
      } else if (e.key === 'ArrowDown' && !visible) {
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

    function refreshSuggestions() {
      const value = input.value;
      if (!value.startsWith('/')) { hideSuggestions(); return; }
      const found = COMMANDS.filter(c => c.startsWith(value.toLowerCase()));
      if (!found.length || (found.length === 1 && found[0] === value)) { hideSuggestions(); return; }
      suggestBox.innerHTML = '';
      selSuggestion = -1;
      for (const cmd of found)
        suggestBox.append(h('div', {
          class: 'suggestion',
          onmousedown: e => { e.preventDefault(); acceptSuggestion(cmd); },
        }, cmd));
      suggestBox.scrollTop = 0;
      suggestBox.style.display = '';
    }

    function acceptSuggestion(cmd) {
      if (!cmd) return;
      input.value = cmd + ' ';
      hideSuggestions();
      input.focus();
    }

    function hideSuggestions() {
      suggestBox.style.display = 'none';
      selSuggestion = -1;
    }

    // ── Players rail ─────────────────────────────────────────────────────
    function syncRail() {
      railList.innerHTML = '';
      if (!state.players.length) { rail.style.display = 'none'; return; }
      rail.style.display = '';
      railHeader.replaceChildren('Online', h('span', { class: 'badge accent' }, String(state.players.length)));
      for (const p of state.players) {
        const avatar = h('span', { class: 'avatar', style: { background: p.colorHex } }, p.username[0].toUpperCase());
        railList.append(h('div', { class: 'rail-player' },
          avatar,
          h('div', { class: 'rp-info' },
            h('div', { class: 'rp-name' }, p.username),
            h('div', { class: 'rp-meta' }, `${timeAgo(p.joinedAt)} · ${p.ipAddress ?? '—'}`))));
      }
    }

    // ── Boot & subscriptions ─────────────────────────────────────────────
    rebuildLog();
    syncRail();
    syncCapabilities();
    input.focus();

    const offs = [
      on('store:console', entry => {
        pendingLines.push(entry);
        if (!rafQueued) { rafQueued = true; requestAnimationFrame(flushPending); }
      }),
      on('store:console-cleared', rebuildLog),
      on('store:players', syncRail),
      on('store:status', syncCapabilities),
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
  el.style.setProperty('--lvl-info', s.colorInfo);
  el.style.setProperty('--lvl-warn', s.colorWarn);
  el.style.setProperty('--lvl-err', s.colorError);
}
