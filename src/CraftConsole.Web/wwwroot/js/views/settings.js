// Settings: console display preferences and log-level colors.
import { h, toast, debounce } from '../ui.js';
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
  const body = h('div', { class: 'tls-body' }, h('p', { class: 'muted small' }, 'Loading…'));
  const card = h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'TLS certificate'),
    body);

  const renderPinned = status => {
    body.replaceChildren(
      h('p', { class: 'text-2 small' },
        `Certificate pinned via --cert-path — expires ${new Date(status.expiry).toLocaleDateString()}.`),
      h('p', { class: 'muted small' }, 'Remove --cert-path to manage the certificate from here instead.'));
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
      h('p', { class: 'text-2 small' },
        `${sourceLabel} — expires ${new Date(status.expiry).toLocaleDateString()}.`),
      h('p', { class: 'muted small' },
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
        h('p', { class: 'muted small' },
          'Running in plain HTTP mode (started with --http). No TLS certificate is in use.'));
    }
  })();

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

    const switchRow = (label, desc, key) =>
      h('div', { class: 'switch-row' },
        h('div', {},
          h('div', { class: 'switch-label' }, label),
          h('div', { class: 'switch-desc' }, desc)),
        h('label', { class: 'switch' },
          h('input', {
            type: 'checkbox', checked: !!s[key],
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
      const preview = h('span', { class: 'color-preview', style: { background: s[key] } });
      const custom = h('input', {
        type: 'color', value: s[key],
        style: { width: '26px', height: '26px', padding: 0, border: 'none', background: 'none', cursor: 'pointer' },
        oninput: e => { s[key] = e.target.value; preview.style.background = s[key]; save(); },
      });
      return h('div', { class: 'switch-row' },
        h('div', { class: 'switch-label' }, label),
        h('div', { class: 'swatch-row' },
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
      h('p', { class: 'text-2 small' },
        'CraftConsole — a local web panel for managing Minecraft servers. ',
        'Settings, profiles, tasks, and backups persist in %APPDATA%\\CraftConsole.'),
      h('p', { class: 'muted small', style: { marginTop: '8px' } },
        'The panel binds to localhost by default. A password is required for every request, so exposing it further (e.g. --urls) is reasonable — do it over a trusted network or an SSH tunnel.'));

    el.append(h('div', { class: 'grid', style: { maxWidth: '660px' } },
      consoleCard, colorsCard, securityCard(), tlsCard(), aboutCard));
  },
};
