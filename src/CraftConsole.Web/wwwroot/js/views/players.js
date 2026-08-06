// Players: online roster with moderation actions, banned players, banned IPs.
import { h, icon, toast, promptReason, confirmDialog, timeAgo, emptyState } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { usernameColor } from '../usercolor.js';

export default {
  id: 'players',
  title: 'Players',
  subtitle: () => `${state.players.length} online of a max ${state.status?.maxPlayers ?? 20}`,
  icon: 'users',

  render(el) {
    let tab = 'online';
    let banned = { available: true, reason: null, entries: [] };
    let bannedIps = { available: true, reason: null, entries: [] };
    let whitelist = { available: true, reason: null, entries: [], enabled: false };

    const tabs = h('div', { class: 'seg', style: { width: 'fit-content', marginBottom: 'var(--s3)' } });
    const body = h('div');
    el.append(tabs, body);

    const TABS = [
      ['online', 'Online'],
      ['whitelist', 'Whitelist'],
      ['banned', 'Banned players'],
      ['banned-ips', 'Banned IPs'],
    ];

    const tabCount = id => ({
      online: state.players.length,
      whitelist: whitelist.entries.length,
      banned: banned.entries.length,
      'banned-ips': bannedIps.entries.length,
    })[id] ?? 0;

    function buildTabs() {
      tabs.innerHTML = '';
      for (const [id, label] of TABS) {
        const count = tabCount(id);
        tabs.append(h('button', {
          class: `seg-item${tab === id ? ' active' : ''}`,
          onclick: () => { tab = id; buildTabs(); buildBody(); },
        }, `${label}${count ? ` (${count})` : ''}`));
      }
    }

    async function loadBanLists() {
      const [b, ips, wl] = await Promise.allSettled([
        api.get('/api/players/banned'),
        api.get('/api/players/banned-ips'),
        api.get('/api/players/whitelist'),
      ]);
      banned = b.status === 'fulfilled' ? b.value : { available: true, reason: null, entries: [] };
      bannedIps = ips.status === 'fulfilled' ? ips.value : { available: true, reason: null, entries: [] };
      whitelist = wl.status === 'fulfilled' ? wl.value : { available: true, reason: null, entries: [], enabled: false };
      buildTabs();
      if (tab !== 'online') buildBody();
    }

    async function whitelistAction(path, body, successMessage) {
      try {
        await api.post(`/api/players/whitelist/${path}`, body ?? {});
        toast(successMessage);
        // The server rewrites whitelist.json; give it a moment before re-reading.
        setTimeout(loadBanLists, 700);
      } catch (err) { toast(err.message, 'err'); }
    }

    function avatarCell(username, colorHex) {
      const avatar = h('span', { class: 'avatar sm', style: { background: colorHex } }, username[0]?.toUpperCase() ?? '?');
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
            h('td', { class: 'dim' }, timeAgo(p.joinedAt)),
            h('td', {}, h('div', { class: 'actions' },
              h('button', { class: 'btn sm', onclick: () => act('kick', p.username, true, 'Kick reason (optional)') }, icon('userMinus'), 'Kick'),
              h('button', { class: 'btn sm danger', onclick: () => act('ban', p.username, true, 'Ban reason (optional)') }, icon('ban'), 'Ban'),
              h('button', {
                class: 'btn sm danger',
                title: p.ipAddress ? 'Ban this player’s IP address' : 'IP address unknown for this connection',
                disabled: !p.ipAddress,
                onclick: () => act('ban-ip', p.ipAddress, true, 'Ban-IP reason (optional)'),
              }, 'Ban IP')))))))));
        return;
      }

      if (tab === 'whitelist') {
        const serverUp = ['Running', 'Starting'].includes(state.status?.status ?? 'Stopped');

        const nameInput = h('input', {
          class: 'input', placeholder: 'Player name', style: { maxWidth: '220px' },
          onkeydown: e => { if (e.key === 'Enter') addPlayer(); },
        });

        const addPlayer = () => {
          const name = nameInput.value.trim();
          if (!name) return;
          nameInput.value = '';
          whitelistAction('add', { target: name }, `Whitelisted ${name}`);
        };

        body.append(h('div', { class: 'card', style: { marginBottom: 'var(--s3)' } },
          h('div', { class: 'switch-row', style: { paddingTop: 0 } },
            h('div', {},
              h('div', { class: 'switch-label' }, 'Whitelist enforcement'),
              h('div', { class: 'switch-desc' },
                whitelist.enabled
                  ? 'Only whitelisted players can join.'
                  : 'Anyone can join. The list below is kept but not enforced.')),
            h('label', { class: 'switch', title: serverUp ? '' : 'The server must be running' },
              h('input', {
                type: 'checkbox', checked: whitelist.enabled, disabled: !serverUp,
                role: 'switch', 'aria-checked': String(whitelist.enabled), 'aria-label': 'Whitelist enforcement',
                onchange: e => whitelistAction(
                  e.target.checked ? 'on' : 'off', null,
                  e.target.checked ? 'Whitelist enabled' : 'Whitelist disabled'),
              }),
              h('span', { class: 'track' }))),
          h('div', { style: { display: 'flex', gap: '8px', alignItems: 'center', marginTop: '12px' } },
            nameInput,
            h('button', { class: 'btn sm primary', disabled: !serverUp, onclick: addPlayer },
              icon('plus'), 'Add player'),
            h('button', {
              class: 'btn sm ghost', disabled: !serverUp,
              title: 'Re-read whitelist.json on the server',
              onclick: () => whitelistAction('reload', null, 'Whitelist reloaded'),
            }, icon('restart'), 'Reload')),
          !serverUp
            ? h('div', { class: 'hint', style: { marginTop: '10px' } },
                'Start the server to change the whitelist — changes go through it so whitelist.json stays in sync.')
            : null));

        if (!whitelist.available) {
          body.append(emptyState('info', 'List not shown', whitelist.reason));
          return;
        }

        if (!whitelist.entries.length) {
          body.append(emptyState('users', 'Whitelist is empty',
            'Add players above. With enforcement on and an empty list, nobody can join.'));
          return;
        }

        body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'Player'), h('th', {}, 'UUID'), h('th', {}))),
          h('tbody', {}, whitelist.entries.map(entry => h('tr', {},
            h('td', {}, avatarCell(entry.name, usernameColor(entry.name))),
            h('td', { class: 'mono dimmer small' }, entry.uuid || '—'),
            h('td', {}, h('div', { class: 'actions' },
              h('button', {
                class: 'btn sm danger', disabled: !serverUp,
                onclick: async () => {
                  if (!await confirmDialog('Remove from whitelist',
                    `Remove “${entry.name}” from the whitelist?`,
                    { danger: true, okLabel: 'Remove' })) return;
                  whitelistAction('remove', { target: entry.name }, `Removed ${entry.name}`);
                },
              }, 'Remove')))))))));
        return;
      }

      if (tab === 'banned') {
        if (!banned.available) {
          body.append(emptyState('info', 'Not available', banned.reason));
          return;
        }
        if (!banned.entries.length) {
          body.append(emptyState('check', 'No banned players', 'Bans issued from the console or this panel show up here.'));
          return;
        }
        body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'Player'), h('th', {}, 'Reason'), h('th', {}, 'Source'),
            h('th', {}, 'Created'), h('th', {}))),
          h('tbody', {}, banned.entries.map(b => h('tr', {},
            h('td', {}, avatarCell(b.name, 'var(--surface-3)')),
            h('td', { class: 'dim' }, b.reason || '—'),
            h('td', { class: 'dim' }, b.source || '—'),
            h('td', { class: 'dimmer small' }, b.created || '—'),
            h('td', {}, h('div', { class: 'actions' },
              h('button', { class: 'btn sm', onclick: () => act('pardon', b.name, false, 'Pardon') }, 'Pardon')))))))));
        return;
      }

      // banned-ips
      if (!bannedIps.available) {
        body.append(emptyState('info', 'Not available', bannedIps.reason));
        return;
      }
      if (!bannedIps.entries.length) {
        body.append(emptyState('check', 'No banned IPs', 'IP bans show up here.'));
        return;
      }
      body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, 'IP address'), h('th', {}, 'Reason'), h('th', {}, 'Source'),
          h('th', {}, 'Created'), h('th', {}))),
        h('tbody', {}, bannedIps.entries.map(b => h('tr', {},
          h('td', { class: 'mono' }, b.ip),
          h('td', { class: 'dim' }, b.reason || '—'),
          h('td', { class: 'dim' }, b.source || '—'),
          h('td', { class: 'dimmer small' }, b.created || '—'),
          h('td', {}, h('div', { class: 'actions' },
            h('button', { class: 'btn sm', onclick: () => act('pardon-ip', b.ip, false, 'Pardon IP') }, 'Pardon')))))))));
    }

    buildTabs();
    buildBody();
    loadBanLists();

    const offs = [
      on('store:players', () => { buildTabs(); if (tab === 'online') buildBody(); }),
      // Enable/disable and the "server must be running" hints depend on status.
      on('store:status', () => { if (tab === 'whitelist') buildBody(); }),
    ];
    return () => offs.forEach(off => off());
  },
};
