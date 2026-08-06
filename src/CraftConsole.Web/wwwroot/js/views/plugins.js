// Plugins: scan the active server's plugins folder, disable jars.
import { h, icon, toast, confirmDialog } from '../ui.js';
import { api } from '../api.js';

export default {
  id: 'plugins',
  title: 'Plugins',
  subtitle: 'Installed jars and their plugin.yml',
  icon: 'puzzle',

  render(el) {
    const folderLabel = h('span', { class: 'sub mono ellipsis', style: { maxWidth: '440px' } }, '');
    const body = h('div');

    el.append(
      h('div', { class: 'view-head' },
        folderLabel,
        h('button', {
          class: 'btn sm ghost icon-only', title: 'Copy folder path', 'aria-label': 'Copy folder path',
          onclick: () => {
            navigator.clipboard.writeText(folderLabel.textContent).then(() => toast('Path copied'));
          },
        }, icon('copy')),
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm', onclick: load }, icon('refresh'), 'Re-scan')),
      body);

    async function load() {
      body.innerHTML = '';
      body.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Scanning plugins…'));

      let data;
      try { data = await api.get('/api/plugins'); }
      catch (err) { body.innerHTML = ''; toast(err.message, 'err'); return; }

      folderLabel.textContent = data.folder ?? '';
      body.innerHTML = '';

      if (!data.available) {
        body.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
          icon('info'),
          h('div', { class: 'empty-title' }, 'Not available'),
          h('div', { class: 'empty-sub' }, data.reason))));
        return;
      }

      if (!data.plugins?.length) {
        body.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
          icon('box'),
          h('div', { class: 'empty-title' }, 'No plugins found'),
          h('div', { class: 'empty-sub' }, 'Drop plugin .jar files into the plugins folder and re-scan.'))));
        return;
      }

      body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, 'Plugin'), h('th', {}, 'Version'), h('th', {}, 'Author'),
          h('th', {}, 'Description'), h('th', {}))),
        h('tbody', {}, data.plugins.map(p => h('tr', {},
          h('td', {},
            h('div', { style: { fontWeight: 600 } }, p.name),
            h('div', { class: 'dimmer small mono' }, p.fileName)),
          h('td', {}, p.version ? h('span', { class: 'tag' }, p.version) : h('span', { class: 'dimmer' }, '—')),
          h('td', { class: 'dim' }, p.author || '—'),
          h('td', { class: 'dim small', style: { maxWidth: '340px' } },
            h('div', { class: 'ellipsis', title: p.description }, p.description || '—')),
          h('td', {}, h('div', { class: 'actions' },
            h('button', {
              class: 'btn sm danger',
              title: 'Move to plugins/disabled (takes effect after restart)',
              onclick: async () => {
                if (!await confirmDialog('Disable plugin', `Move “${p.fileName}” to plugins/disabled? Takes effect after a server restart.`, { danger: true, okLabel: 'Disable' })) return;
                try {
                  await api.post(`/api/plugins/${encodeURIComponent(p.fileName)}/disable`);
                  toast(`${p.name} disabled`);
                  load();
                } catch (err) { toast(err.message, 'err'); }
              },
            }, 'Disable')))))))));
    }

    load();
  },
};
