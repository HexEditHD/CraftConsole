// Issues: warnings and errors distilled from console output.
import { h, icon, fmtClock } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';

export default {
  id: 'issues',
  title: 'Issues',
  subtitle: 'Warnings and errors distilled from the log',
  icon: 'warning',

  render(el) {
    let filter = 'all';

    const segItems = ['all', 'warning', 'severe'].map(f =>
      h('button', {
        class: `seg-item${f === 'all' ? ' active' : ''}`,
        onclick: function () {
          filter = f;
          el.querySelectorAll('.seg-item').forEach(c => c.classList.remove('active'));
          this.classList.add('active');
          build();
        },
      }, f[0].toUpperCase() + f.slice(1)));

    const head = h('div', { class: 'view-head' },
      h('div', { class: 'seg' }, segItems),
      h('span', { class: 'spacer' }),
      h('button', {
        class: 'btn sm ghost',
        onclick: async () => { await api.del('/api/issues'); },
      }, icon('eraser'), 'Clear all'));

    const body = h('div');
    el.append(head, body);

    function build() {
      body.innerHTML = '';
      const items = state.issues
        .filter(i => filter === 'all' || i.type.toLowerCase() === filter)
        .slice()
        .reverse();

      if (!items.length) {
        body.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
          icon('checkCircle'),
          h('div', { class: 'empty-title' }, 'All clear'),
          h('div', { class: 'empty-sub' }, 'Warnings and errors from the server console will be collected here automatically.'))));
        return;
      }

      body.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', { style: { width: '90px' } }, 'Severity'),
          h('th', {}, 'Message'),
          h('th', { style: { width: '110px' } }, 'Time'))),
        h('tbody', {}, items.map(issue => h('tr', {},
          h('td', {}, h('span', { class: `badge ${issue.type === 'Severe' ? 'danger' : 'warn'}` }, issue.type)),
          h('td', { class: 'text-2', style: { wordBreak: 'break-word' } }, issue.message),
          h('td', { class: 'muted small nowrap' }, fmtClock(issue.timestamp))))))));
    }

    build();
    const off = on('store:issues', build);
    return () => off();
  },
};
