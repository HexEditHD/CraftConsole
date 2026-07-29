// Players: online roster with moderation actions, banned players, banned IPs.
import { h, icon, toast, promptReason, confirmDialog, timeAgo } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';

export default {
  id: 'players',
  title: 'Players',
  icon: 'users',

  render(el) {
    let tab = 'online';
    let banned = [];
    let bannedIps = [];

    const tabs = h('div', { class: 'tabs' });
    const body = h('div');
    el.append(tabs, body);

    const TABS = [
      ['online', 'Online'],
      ['banned', 'Banned players'],
      ['banned-ips', 'Banned IPs'],
    ];

    function buildTabs() {
      tabs.innerHTML = '';
      for (const [id, label] of TABS) {
        const count = id === 'online' ? state.players.length : id === 'banned' ? banned.length : bannedIps.length;
        tabs.append(h('button', {
          class: `tab${tab === id ? ' active' : ''}`,
          onclick: () => { tab = id; buildTabs(); buildBody(); },
        }, `${label}${count ? ` (${count})` : ''}`));
      }
    }

    async function loadBanLists() {
      try {
        [banned, bannedIps] = await Promise.all([
          api.get('/api/players/banned'),
          api.get('/api/players/banned-ips'),
        ]);
      } catch { banned = []; bannedIps = []; }
      buildTabs();
      if (tab !== 'online') buildBody();
    }

    function avatarCell(username, colorHex) {
      const avatar = h('span', { class: 'avatar', style: { background: colorHex } }, username[0]?.toUpperCase() ?? '?');
      avatar.prepend(h('img', {
        src: `https://mc-heads.net/avatar/${encodeURIComponent(username)}/28`,
        alt: '', loading: 'lazy',
        onerror: function () { this.remove(); },
      }));
      return h('span', { class: 'player-cell' }, avatar, username);
    }

    async function act(verb, target, withReason, confirmText) {
      let reason = '';
      if (withReason) {
        reason = await promptReason(confirmText);
        if (reason === null) return;
      } else if (confirmText && !await confirmDialog(confirmText, `${confirmText} “${target}”?`)) {
        return;
      }
      try {
        await api.post(`/api/players/${verb}`, { target, reason: reason || null });
        toast(`Sent: ${verb} ${target}`);
        setTimeout(loadBanLists, 700); // give the server a moment to rewrite its JSON
      } catch (err) { toast(err.message, 'err'); }
    }

    function buildBody() {
      body.innerHTML = '';

      if (tab === 'online') {
        if (!state.players.length) {
          body.append(emptyState('users', 'Nobody online',
            state.status?.status === 'Running'
              ? 'Players will appear here the moment they join.'
              : 'Start the server and players will appear here as they join.'));
          return;
        }
        body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'Player'), h('th', {}, 'IP address'), h('th', {}, 'Location'),
            h('th', {}, 'Joined'), h('th', {}))),
          h('tbody', {}, state.players.map(p => h('tr', {},
            h('td', {}, avatarCell(p.username, p.colorHex)),
            h('td', { class: 'mono' }, p.ipAddress ?? '—'),
            h('td', {}, p.location ?? '—'),
            h('td', { class: 'muted' }, timeAgo(p.joinedAt)),
            h('td', {}, h('div', { class: 'actions' },
              h('button', { class: 'btn sm', onclick: () => act('kick', p.username, true, 'Kick reason (optional)') }, icon('userX'), 'Kick'),
              h('button', { class: 'btn sm danger', onclick: () => act('ban', p.username, true, 'Ban reason (optional)') }, icon('ban'), 'Ban'),
              h('button', { class: 'btn sm danger', title: 'Ban this player’s IP address', onclick: () => act('ban-ip', p.ipAddress ?? p.username, true, 'Ban-IP reason (optional)') }, 'Ban IP')))))))));
        return;
      }

      if (tab === 'banned') {
        if (!banned.length) {
          body.append(emptyState('check', 'No banned players', 'Bans issued from the console or this panel show up here.'));
          return;
        }
        body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'Player'), h('th', {}, 'Reason'), h('th', {}, 'Source'),
            h('th', {}, 'Created'), h('th', {}))),
          h('tbody', {}, banned.map(b => h('tr', {},
            h('td', {}, avatarCell(b.name, 'var(--surface-3)')),
            h('td', { class: 'text-2' }, b.reason || '—'),
            h('td', { class: 'muted' }, b.source || '—'),
            h('td', { class: 'muted small' }, b.created || '—'),
            h('td', {}, h('div', { class: 'actions' },
              h('button', { class: 'btn sm', onclick: () => act('pardon', b.name, false, 'Pardon') }, 'Pardon')))))))));
        return;
      }

      // banned-ips
      if (!bannedIps.length) {
        body.append(emptyState('check', 'No banned IPs', 'IP bans show up here.'));
        return;
      }
      body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, 'IP address'), h('th', {}, 'Reason'), h('th', {}, 'Source'),
          h('th', {}, 'Created'), h('th', {}))),
        h('tbody', {}, bannedIps.map(b => h('tr', {},
          h('td', { class: 'mono' }, b.ip),
          h('td', { class: 'text-2' }, b.reason || '—'),
          h('td', { class: 'muted' }, b.source || '—'),
          h('td', { class: 'muted small' }, b.created || '—'),
          h('td', {}, h('div', { class: 'actions' },
            h('button', { class: 'btn sm', onclick: () => act('pardon-ip', b.ip, false, 'Pardon IP') }, 'Pardon')))))))));
    }

    buildTabs();
    buildBody();
    loadBanLists();

    const off = on('store:players', () => { buildTabs(); if (tab === 'online') buildBody(); });
    return () => off();
  },
};

function emptyState(iconName, title, sub) {
  return h('div', { class: 'card' }, h('div', { class: 'empty' },
    icon(iconName), h('div', { class: 'empty-title' }, title), h('div', { class: 'empty-sub' }, sub)));
}
