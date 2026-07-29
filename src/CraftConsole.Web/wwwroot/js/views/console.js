// Live console: streamed log with level filter and search, command input
// with history + autocomplete, online-players rail.
import { h, icon, toast, fmtClock } from '../ui.js';
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

const CHAT_RE = /^<(\w+)> /;
const MAX_DOM_LINES = 3000;

export default {
  id: 'console',
  title: 'Console',
  icon: 'terminal',

  render(el) {
    let levelFilter = 'all';
    let searchText = '';
    let autoScroll = state.settings?.autoScrollConsole ?? true;
    let history = JSON.parse(localStorage.getItem('cc-cmd-history') ?? '[]');
    let historyIndex = -1;
    let selSuggestion = -1;
    let pendingLines = [];
    let rafQueued = false;

    applyColorVars(el);

    // ── Toolbar ──────────────────────────────────────────────────────────
    const chips = ['all', 'info', 'warn', 'error', 'debug'].map(lvl =>
      h('button', {
        class: `chip${lvl === 'all' ? ' active' : ''}`,
        onclick: function () {
          levelFilter = lvl;
          el.querySelectorAll('.chip').forEach(c => c.classList.remove('active'));
          this.classList.add('active');
          rebuildLog();
        },
      }, lvl === 'all' ? 'All' : lvl[0].toUpperCase() + lvl.slice(1)));

    const searchInput = h('input', {
      class: 'input search', placeholder: 'Filter…', type: 'search',
      oninput: () => { searchText = searchInput.value.toLowerCase(); rebuildLog(); },
    });

    const autoBtn = h('button', {
      class: `chip${autoScroll ? ' active' : ''}`,
      title: 'Auto-scroll to newest output',
      onclick: () => {
        autoScroll = !autoScroll;
        autoBtn.classList.toggle('active', autoScroll);
        if (autoScroll) scrollToBottom();
      },
    }, 'Auto-scroll');

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

    // ── Input row ────────────────────────────────────────────────────────
    const suggestBox = h('div', { class: 'suggestions', style: { display: 'none' } });

    const input = h('input', {
      class: 'input',
      placeholder: 'Type a command… ( / for autocomplete, ↑↓ for history )',
      onkeydown: onInputKey,
      oninput: refreshSuggestions,
    });

    const sendBtn = h('button', { class: 'btn primary icon-only', title: 'Send', onclick: send }, icon('send'));

    // ── Players rail ─────────────────────────────────────────────────────
    const rail = h('div', { class: 'console-rail' });

    const layout = h('div', { class: 'console-layout' },
      h('div', { class: 'console-main', style: { position: 'relative' } },
        h('div', { class: 'console-toolbar' },
          h('div', { class: 'chip-row' }, chips),
          h('span', { class: 'spacer' }),
          searchInput,
          autoBtn,
          h('button', {
            class: 'btn sm ghost', title: 'Clear console',
            onclick: async () => { await api.del('/api/console'); },
          }, icon('eraser'), 'Clear')),
        rconNotice,
        log,
        jumpBtn,
        h('div', { class: 'console-input-row' }, suggestBox, input, sendBtn)),
      rail);

    el.append(layout);
    el.style.height = '100%';

    // ── Rendering ────────────────────────────────────────────────────────
    function lineNode(entry) {
      const lvl = (entry.level ?? 'Unknown').toLowerCase();
      const row = h('div', { class: `console-line lvl-${lvl}` });

      if (state.settings?.showTimestamp ?? true)
        row.append(h('span', { class: 't' }, fmtClock(entry.timestamp, { date: state.settings?.showDate })));

      row.append(h('span', { class: 'lvl' }, lvl === 'input' ? '>' : lvl.toUpperCase()));

      const msg = h('span', { class: 'msg' });
      const chat = CHAT_RE.exec(entry.message);
      if (chat) {
        msg.append(
          h('span', { class: 'chat-name', style: { color: usernameColor(chat[1]) } }, `<${chat[1]}>`),
          entry.message.slice(chat[0].length - 1));
      } else {
        msg.textContent = entry.message;
      }
      row.append(msg);
      return row;
    }

    function matches(entry) {
      const lvl = (entry.level ?? 'Unknown').toLowerCase();
      if (levelFilter !== 'all' && lvl !== levelFilter && lvl !== 'input') return false;
      if (searchText && !entry.message.toLowerCase().includes(searchText)) return false;
      return true;
    }

    function rebuildLog() {
      log.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const entry of state.consoleEntries)
        if (matches(entry)) frag.append(lineNode(entry));
      log.append(frag);
      if (frag.childElementCount === 0 && log.childElementCount === 0 && !state.consoleEntries.length) {
        const hasStream = state.status?.capabilities?.hasConsoleStream ?? true;
        log.append(h('div', { class: 'empty' },
          icon('terminal'),
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
      const stick = autoScroll && isNearBottom();
      const frag = document.createDocumentFragment();
      for (const entry of pendingLines)
        if (matches(entry)) frag.append(lineNode(entry));
      pendingLines = [];
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
      const found = COMMANDS.filter(c => c.startsWith(value.toLowerCase())).slice(0, 8);
      if (!found.length || (found.length === 1 && found[0] === value)) { hideSuggestions(); return; }
      suggestBox.innerHTML = '';
      selSuggestion = -1;
      for (const cmd of found)
        suggestBox.append(h('div', {
          class: 'suggestion',
          onmousedown: e => { e.preventDefault(); acceptSuggestion(cmd); },
        }, cmd));
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
      rail.innerHTML = '';
      if (!state.players.length) { rail.style.display = 'none'; return; }
      rail.style.display = '';
      rail.append(h('div', { class: 'rail-title' }, `Online — ${state.players.length}`));
      for (const p of state.players) {
        const avatar = h('span', { class: 'avatar', style: { background: p.colorHex } }, p.username[0].toUpperCase());
        const img = h('img', {
          src: `https://mc-heads.net/avatar/${encodeURIComponent(p.username)}/28`,
          alt: '', loading: 'lazy',
          onerror: function () { this.remove(); },
        });
        avatar.prepend(img);
        rail.append(h('div', { class: 'rail-player' }, avatar, h('span', { class: 'ellipsis' }, p.username)));
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
  el.style.setProperty('--lvl-error', s.colorError);
}
