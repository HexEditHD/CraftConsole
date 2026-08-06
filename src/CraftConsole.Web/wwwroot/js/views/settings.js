// Settings: console display preferences and log-level colors.
import { h, icon, toast, debounce, modal, confirmDialog } from '../ui.js';
import { api } from '../api.js';
import { state } from '../store.js';

const SWATCHES = {
  colorInfo:   ['#60A5FA', '#93C5FD', '#3B82F6', '#94A3B8', '#38BDF8'],
  colorWarn:   ['#FB923C', '#F97316', '#FDBA74', '#FCD34D', '#C2410C'],
  colorError:  ['#F87171', '#EF4444', '#FCA5A5', '#F43F5E', '#B91C1C'],
  colorPlayer: ['#22C55E', '#16A34A', '#4ADE80', '#86EFAC', '#15803D'],
};

const DEFAULTS = { colorInfo: '#94A3B8', colorWarn: '#FB923C', colorError: '#F87171', colorPlayer: '#22C55E' };

function tlsCard() {
  const body = h('div', { class: 'tls-body' }, h('p', { class: 'dimmer small' }, 'Loading…'));
  const card = h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'TLS certificate'),
    body);

  const renderPinned = status => {
    body.replaceChildren(
      h('p', { class: 'dim small' },
        `Certificate pinned via --cert-path — expires ${new Date(status.expiry).toLocaleDateString()}.`),
      h('p', { class: 'dimmer small' }, 'Remove --cert-path to manage the certificate from here instead.'));
  };

  const renderManaged = status => {
    const sourceLabel = status.source === 'uploaded' ? 'Uploaded certificate' : 'Self-signed (auto-generated)';
    const certFile = h('input', { class: 'input', type: 'file', accept: '.pem,.crt,.cer,.txt' });
    const keyFile = h('input', { class: 'input', type: 'file', accept: '.pem,.key,.txt' });
    const submit = h('button', { class: 'btn primary sm', type: 'submit' }, 'Upload');

    const form = h('form', {
      onsubmit: async e => {
        e.preventDefault();
        const cert = certFile.files[0];
        const key = keyFile.files[0];
        if (!cert || !key) { toast('Choose both a certificate and a private key file.', 'err'); return; }

        const formData = new FormData();
        formData.append('certificate', cert);
        formData.append('key', key);

        submit.disabled = true;
        try {
          const result = await api.upload('/api/tls/certificate', formData);
          toast('Certificate updated — now serving it, no restart needed.');
          renderManaged(result);
        } catch (err) { toast(err.message, 'err'); }
        finally { submit.disabled = false; }
      },
    },
      h('div', { class: 'field-row' },
        h('div', { class: 'field' }, h('label', {}, 'Certificate (.pem/.crt — may include the full chain)'), certFile),
        h('div', { class: 'field' }, h('label', {}, 'Private key (.pem/.key)'), keyFile)),
      submit);

    body.replaceChildren(
      h('p', { class: 'dim small' },
        `${sourceLabel} — expires ${new Date(status.expiry).toLocaleDateString()}.`),
      h('p', { class: 'dimmer small' },
        'Self-signed certificates trigger a one-time browser warning; that’s expected. Upload your own certificate and key (e.g. from Let’s Encrypt or an internal CA) to replace it — it takes effect immediately, no restart.'),
      form);
  };

  (async () => {
    try {
      const status = await api.get('/api/tls/status');
      if (status.pinned) renderPinned(status);
      else renderManaged(status);
    } catch {
      body.replaceChildren(
        h('p', { class: 'dimmer small' },
          'Running in plain HTTP mode (started with --http). No TLS certificate is in use.'));
    }
  })();

  return card;
}

const ROLES = ['Operator', 'Admin'];

function usersCard() {
  const body = h('div', {}, h('p', { class: 'dimmer small' }, 'Loading…'));
  const card = h('div', { class: 'card' },
    h('div', { class: 'card-title' },
      'Users',
      h('span', { class: 'spacer' }),
      h('button', { class: 'btn sm primary', onclick: () => openEditor() }, icon('plus'), 'New user')),
    body);

  async function load() {
    try { build((await api.get('/api/users')).users ?? []); }
    catch (err) { body.replaceChildren(h('p', { class: 'dimmer small' }, err.message)); }
  }

  function build(users) {
    body.replaceChildren(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, 'Username'), h('th', {}, 'Role'), h('th', {}, 'Status'), h('th', {}))),
      h('tbody', {}, users.map(u => h('tr', { style: u.enabled ? null : { opacity: .55 } },
        h('td', { style: { fontWeight: 600 } }, u.username),
        h('td', {}, h('select', {
          class: 'select',
          onchange: e => {
            api.put(`/api/users/${u.id}/role`, { role: e.target.value })
              .then(() => { toast('Role updated'); load(); })
              .catch(err => { toast(err.message, 'err'); e.target.value = u.role; });
          },
        }, ROLES.map(r => h('option', { value: r, selected: u.role === r }, r)))),
        h('td', {}, u.enabled
          ? h('span', { class: 'tag ok' }, 'Enabled')
          : h('span', { class: 'tag warn' }, 'Disabled')),
        h('td', {}, h('div', { class: 'actions' },
          h('button', {
            class: 'btn sm',
            onclick: () => {
              api.put(`/api/users/${u.id}/enabled`, { enabled: !u.enabled })
                .then(load)
                .catch(err => toast(err.message, 'err'));
            },
          }, u.enabled ? 'Disable' : 'Enable'),
          h('button', { class: 'btn sm', onclick: () => openPasswordReset(u) }, 'Reset password'),
          h('button', {
            class: 'btn sm icon-only danger', title: 'Delete', 'aria-label': `Delete “${u.username}”`,
            onclick: async () => {
              if (!await confirmDialog('Delete user', `Delete “${u.username}”?`, { danger: true, okLabel: 'Delete' })) return;
              api.del(`/api/users/${u.id}`).then(load).catch(err => toast(err.message, 'err'));
            },
          }, icon('trash'))))))))));
  }

  function openEditor() {
    const username = h('input', { class: 'input', placeholder: 'Username', autocomplete: 'off' });
    const password = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', placeholder: 'Password (min. 8 characters)' });
    const role = h('select', { class: 'select' }, ROLES.map(r => h('option', { value: r }, r)));

    modal({
      title: 'New user',
      body: h('div', {},
        h('div', { class: 'field' }, h('label', {}, 'Username'), username),
        h('div', { class: 'field' }, h('label', {}, 'Password'), password),
        h('div', { class: 'field' }, h('label', {}, 'Role'), role)),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        {
          label: 'Create user',
          kind: 'primary',
          onClick: () => {
            if (!username.value.trim()) { toast('A username is required.', 'err'); return false; }
            if (password.value.length < 8) { toast('Password must be at least 8 characters.', 'err'); return false; }
            api.post('/api/users', { username: username.value.trim(), password: password.value, role: role.value })
              .then(() => { toast('User created'); load(); })
              .catch(err => toast(err.message, 'err'));
          },
        },
      ],
    });
  }

  function openPasswordReset(u) {
    const password = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', placeholder: 'New password (min. 8 characters)' });

    modal({
      title: `Reset “${u.username}”’s password`,
      body: h('div', { class: 'field' }, h('label', {}, 'New password'), password),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        {
          label: 'Reset password',
          kind: 'primary',
          onClick: () => {
            if (password.value.length < 8) { toast('Password must be at least 8 characters.', 'err'); return false; }
            api.put(`/api/users/${u.id}/password`, { newPassword: password.value })
              .then(() => toast('Password reset'))
              .catch(err => toast(err.message, 'err'));
          },
        },
      ],
    });
  }

  load();
  return card;
}

function securityCard() {
  const current = h('input', { class: 'input', type: 'password', autocomplete: 'current-password', placeholder: 'Current password' });
  const next = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', placeholder: 'New password (min. 8 characters)' });
  const confirm = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', placeholder: 'Confirm new password' });
  const submit = h('button', { class: 'btn primary sm', type: 'submit' }, 'Update password');

  const form = h('form', {
    onsubmit: async e => {
      e.preventDefault();
      if (next.value !== confirm.value) { toast('New passwords do not match.', 'err'); return; }
      submit.disabled = true;
      try {
        await api.post('/api/auth/change-password', { currentPassword: current.value, newPassword: next.value });
        toast('Password updated — you’ll need it next time you sign in.');
        current.value = next.value = confirm.value = '';
      } catch (err) { toast(err.message, 'err'); }
      finally { submit.disabled = false; }
    },
  },
    h('div', { class: 'field' }, h('label', {}, 'Current password'), current),
    h('div', { class: 'field-row' },
      h('div', { class: 'field' }, h('label', {}, 'New password'), next),
      h('div', { class: 'field' }, h('label', {}, 'Confirm'), confirm)),
    submit);

  return h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'Security'),
    form);
}

export default {
  id: 'settings',
  title: 'Settings',
  subtitle: 'Panel, users and TLS certificate',
  icon: 'sliders',

  render(el) {
    const s = { ...state.settings };

    const save = debounce(async () => {
      try {
        state.settings = await api.put('/api/settings', {
          showTimestamp: s.showTimestamp,
          showDate: s.showDate,
          autoScrollConsole: s.autoScrollConsole,
          maxConsoleLines: s.maxConsoleLines,
          colorInfo: s.colorInfo,
          colorWarn: s.colorWarn,
          colorError: s.colorError,
          colorPlayer: s.colorPlayer,
        });
        toast('Settings saved');
      } catch (err) { toast(err.message, 'err'); }
    }, 500);

    // sep: fade the row's bottom edge, for rows that aren't last in their card.
    const switchRow = (label, desc, key, sep = true) =>
      h('div', { class: `switch-row${sep ? ' rule-fade-bottom' : ''}` },
        h('div', {},
          h('div', { class: 'switch-label' }, label),
          h('div', { class: 'switch-desc' }, desc)),
        h('label', { class: 'switch' },
          h('input', {
            type: 'checkbox', checked: !!s[key],
            role: 'switch', 'aria-checked': String(!!s[key]), 'aria-label': label,
            onchange: e => { s[key] = e.target.checked; save(); },
          }),
          h('span', { class: 'track' })));

    const maxLines = h('input', {
      class: 'input', type: 'number', min: 100, max: 20000, step: 100,
      value: s.maxConsoleLines ?? 2000,
      style: { maxWidth: '140px' },
      onchange: () => { s.maxConsoleLines = parseInt(maxLines.value, 10) || 2000; save(); },
    });

    const consoleCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' }, 'Console display'),
      switchRow('Show timestamps', 'Prefix each console line with the time', 'showTimestamp'),
      switchRow('Show date', 'Include the date in timestamps', 'showDate'),
      switchRow('Auto-scroll', 'Follow new output as it arrives', 'autoScrollConsole'),
      h('div', { class: 'switch-row' },
        h('div', {},
          h('div', { class: 'switch-label' }, 'Console history'),
          h('div', { class: 'switch-desc' }, 'Maximum lines kept in the buffer')),
        maxLines));

    const colorRow = (label, key) => {
      const preview = h('span', { class: 'swatch-preview', style: { background: s[key] } });
      const custom = h('input', {
        type: 'color', value: s[key],
        style: { width: '26px', height: '26px', padding: 0, border: 'none', background: 'none', cursor: 'pointer' },
        oninput: e => { s[key] = e.target.value; preview.style.background = s[key]; save(); },
      });
      return h('div', { class: 'switch-row' },
        h('div', { class: 'switch-label' }, label),
        h('div', { class: 'swatches' },
          preview,
          SWATCHES[key].map(hex => h('button', {
            class: 'swatch', title: hex, style: { background: hex },
            onclick: () => { s[key] = hex; preview.style.background = hex; custom.value = hex; save(); },
          })),
          custom));
    };

    const colorsCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' },
        'Log level colors',
        h('span', { class: 'spacer' }),
        h('button', {
          class: 'btn sm ghost',
          onclick: () => {
            Object.assign(s, DEFAULTS);
            save();
            buildColors();
          },
        }, 'Reset to defaults')),
      h('div', { id: 'color-rows' }));

    const buildColors = () => {
      const rows = colorsCard.querySelector('#color-rows');
      rows.innerHTML = '';
      rows.append(
        colorRow('Info messages', 'colorInfo'),
        colorRow('Warnings', 'colorWarn'),
        colorRow('Errors', 'colorError'),
        colorRow('Player events', 'colorPlayer'));
    };
    buildColors();

    const aboutCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' }, 'About'),
      h('p', { class: 'dim small' },
        'CraftConsole — a local web panel for managing Minecraft servers. ',
        `Settings, profiles, tasks, and backups persist in ${state.system?.dataDirectory ?? 'the app data directory'}.`),
      h('p', { class: 'dimmer small', style: { marginTop: '8px' } },
        'The panel binds to localhost by default. A password is required for every request, so exposing it further (e.g. --urls) is reasonable — do it over a trusted network or an SSH tunnel.'));

    // usersCard()/tlsCard() self-load over the API on construction — build
    // them (and securityCard(), for consistency) only once their tab is
    // actually visited, and cache the result so revisiting doesn't re-fetch.
    let securityEl = null, usersEl = null, tlsEl = null;
    const getSecurity = () => securityEl ??= securityCard();
    const getUsers = () => usersEl ??= usersCard();
    const getTls = () => tlsEl ??= tlsCard();

    const TABS = [
      ['console', 'Console'],
      ['security', 'Security'],
      ['users', 'Users'],
      ['tls', 'TLS & about'],
    ];
    let activeTab = 'console';
    const tabsEl = h('div', { class: 'seg', style: { width: 'fit-content', marginBottom: 'var(--s4)' } });
    const body = h('div', { class: 'settings-col' });

    function buildTabs() {
      tabsEl.innerHTML = '';
      for (const [id, label] of TABS) {
        tabsEl.append(h('button', {
          class: `seg-item${activeTab === id ? ' active' : ''}`,
          onclick: () => { activeTab = id; buildTabs(); buildBody(); },
        }, label));
      }
    }

    function buildBody() {
      body.innerHTML = '';
      if (activeTab === 'console') body.append(consoleCard, colorsCard);
      else if (activeTab === 'security') body.append(getSecurity());
      else if (activeTab === 'users') body.append(getUsers());
      else body.append(getTls(), aboutCard);
    }

    buildTabs();
    buildBody();
    el.append(tabsEl, body);
  },
};
