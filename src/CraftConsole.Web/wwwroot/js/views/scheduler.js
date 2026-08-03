// Scheduler: recurring / event-driven tasks (commands, broadcasts, restarts).
import { h, icon, toast, confirmDialog, modal } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';

const TRIGGERS = [
  ['Interval', 'Every N seconds', 'Seconds between runs, e.g. 1800'],
  ['TimeCron', 'Daily at time', '24h time, e.g. 04:30'],
  ['PlayerJoin', 'When a player joins', ''],
  ['ServerReady', 'When the server is ready', ''],
];

const ACTIONS = [
  ['SendCommand', 'Run command', 'Console command, e.g. save-all'],
  ['BroadcastMessage', 'Broadcast message', 'Message shown to all players'],
  ['RestartServer', 'Restart server', ''],
];

function triggerLabel(task) {
  switch (task.triggerType) {
    case 'Interval': return `every ${task.triggerValue}s`;
    case 'TimeCron': return `daily at ${task.triggerValue}`;
    case 'PlayerJoin': return 'on player join';
    case 'ServerReady': return 'on server ready';
    default: return task.triggerType;
  }
}

function actionLabel(task) {
  switch (task.actionType) {
    case 'SendCommand': return `/${task.actionValue}`;
    case 'BroadcastMessage': return `say “${task.actionValue}”`;
    case 'RestartServer': return 'restart server';
    default: return task.actionType;
  }
}

/** Why a trigger/action can't fire against the active server, or null if it's fine. */
function unsupportedReason(value, kind) {
  const caps = state.status?.capabilities;
  if (!caps) return null;
  // PlayerJoin still fires over RCON — it's polled and synthesized (see
  // RconMinecraftServer) — but ServerReady needs the literal "Done (…)!" line
  // from a real log stream, which RCON never has.
  if (kind === 'trigger' && value === 'ServerReady' && !caps.hasConsoleStream)
    return 'Needs a console stream — not available over RCON.';
  if (kind === 'action' && value === 'RestartServer' && !caps.canRestart)
    return 'This server is connected over RCON and can’t be restarted from here.';
  return null;
}

export default {
  id: 'scheduler',
  title: 'Scheduler',
  icon: 'clock',

  render(el) {
    let tasks = [];

    const list = h('div');
    el.append(
      h('div', { class: 'view-head' },
        h('span', { class: 'sub' }, 'Tasks fire while the panel is running; command actions need a running server.'),
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm primary', onclick: () => openEditor(null) }, icon('plus'), 'New task')),
      list);

    async function load() {
      try { tasks = (await api.get('/api/tasks')).tasks ?? []; }
      catch { tasks = []; }
      build();
    }

    function build() {
      list.innerHTML = '';
      if (!tasks.length) {
        list.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
          icon('clock'),
          h('div', { class: 'empty-title' }, 'No scheduled tasks'),
          h('div', { class: 'empty-sub' }, 'Automate saves, broadcasts, and restarts — on an interval, at a set time, or on game events.'))));
        return;
      }

      list.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', { style: { width: '46px' } }, 'On'),
          h('th', {}, 'Task'), h('th', {}, 'Trigger'), h('th', {}, 'Action'), h('th', {}))),
        h('tbody', {}, tasks.map(task => {
          const triggerWarn = unsupportedReason(task.triggerType, 'trigger');
          const actionWarn = unsupportedReason(task.actionType, 'action');
          return h('tr', { style: task.isEnabled ? null : { opacity: .55 } },
            h('td', {},
              h('label', { class: 'switch', title: task.isEnabled ? 'Enabled' : 'Disabled' },
                h('input', {
                  type: 'checkbox', checked: task.isEnabled,
                  'aria-label': `${task.isEnabled ? 'Disable' : 'Enable'} “${task.name}”`,
                  onchange: e => toggle(task, e.target.checked),
                }),
                h('span', { class: 'track' }))),
            h('td', { style: { fontWeight: 600 } }, task.name,
              task.isEnabled ? null : h('span', { class: 'badge warn', style: { marginLeft: '6px' } }, 'Disabled')),
            h('td', {}, h('span', { class: 'badge info' }, triggerLabel(task)),
              triggerWarn ? h('span', { class: 'badge warn', style: { marginLeft: '6px' }, title: triggerWarn }, '!') : null),
            h('td', { class: 'mono small text-2' }, actionLabel(task),
              actionWarn ? h('span', { class: 'badge warn', style: { marginLeft: '6px' }, title: actionWarn }, '!') : null),
            h('td', {}, h('div', { class: 'actions' },
              h('button', {
                class: 'btn sm', title: 'Run once now',
                onclick: async () => {
                  try { await api.post(`/api/tasks/${task.id}/run`); toast(`Ran “${task.name}”`); }
                  catch (err) { toast(err.message, 'err'); }
                },
              }, icon('play'), 'Run'),
              h('button', { class: 'btn sm icon-only', title: 'Edit', onclick: () => openEditor(task) }, icon('pencil')),
              h('button', {
                class: 'btn sm icon-only danger', title: 'Delete',
                onclick: async () => {
                  if (!await confirmDialog('Delete task', `Delete “${task.name}”?`, { danger: true, okLabel: 'Delete' })) return;
                  await api.del(`/api/tasks/${task.id}`);
                },
              }, icon('trash')))));
        })))));
    }

    async function toggle(task, enabled) {
      try { await api.post(`/api/tasks/${task.id}/enabled`, { enabled }); }
      catch (err) { toast(err.message, 'err'); build(); }
    }

    function openEditor(task) {
      const isNew = !task;
      const f = {
        name: h('input', { class: 'input', value: task?.name ?? '' , placeholder: 'e.g. Autosave'}),
        triggerType: h('select', { class: 'select' },
          TRIGGERS.map(([v, label]) => h('option', {
            value: v, selected: (task?.triggerType ?? 'Interval') === v, disabled: !!unsupportedReason(v, 'trigger'),
          }, unsupportedReason(v, 'trigger') ? `${label} (unavailable)` : label))),
        triggerValue: h('input', { class: 'input', value: task?.triggerValue ?? '' }),
        actionType: h('select', { class: 'select' },
          ACTIONS.map(([v, label]) => h('option', {
            value: v, selected: (task?.actionType ?? 'SendCommand') === v, disabled: !!unsupportedReason(v, 'action'),
          }, unsupportedReason(v, 'action') ? `${label} (unavailable)` : label))),
        actionValue: h('input', { class: 'input', value: task?.actionValue ?? '' }),
      };
      const triggerHint = h('span', { class: 'hint' });
      const actionHint = h('span', { class: 'hint' });

      const syncHints = () => {
        const trig = TRIGGERS.find(t => t[0] === f.triggerType.value);
        const act = ACTIONS.find(a => a[0] === f.actionType.value);
        triggerHint.textContent = unsupportedReason(f.triggerType.value, 'trigger') ?? trig?.[2] ?? '';
        f.triggerValue.style.display = trig?.[2] ? '' : 'none';
        actionHint.textContent = unsupportedReason(f.actionType.value, 'action') ?? act?.[2] ?? '';
        f.actionValue.style.display = act?.[2] ? '' : 'none';
      };
      f.triggerType.addEventListener('change', syncHints);
      f.actionType.addEventListener('change', syncHints);
      syncHints();

      modal({
        title: isNew ? 'New scheduled task' : `Edit “${task.name}”`,
        body: h('div', {},
          h('div', { class: 'field' }, h('label', {}, 'Name'), f.name),
          h('div', { class: 'field' }, h('label', {}, 'Trigger'), f.triggerType, f.triggerValue, triggerHint),
          h('div', { class: 'field' }, h('label', {}, 'Action'), f.actionType, f.actionValue, actionHint)),
        actions: [
          { label: 'Cancel', kind: 'ghost' },
          {
            label: isNew ? 'Create task' : 'Save changes',
            kind: 'primary',
            onClick: () => {
              if (!f.name.value.trim()) { toast('A name is required.', 'err'); return false; }
              if (f.triggerType.value === 'Interval' && !(parseInt(f.triggerValue.value, 10) > 0)) {
                toast('Interval must be a positive number of seconds.', 'err');
                return false;
              }
              const body = {
                name: f.name.value.trim(),
                triggerType: f.triggerType.value,
                triggerValue: f.triggerValue.value.trim(),
                actionType: f.actionType.value,
                actionValue: f.actionValue.value.trim(),
                isEnabled: task?.isEnabled ?? true,
              };
              const req = isNew ? api.post('/api/tasks', body) : api.put(`/api/tasks/${task.id}`, body);
              req.then(() => toast(isNew ? 'Task created' : 'Task saved'))
                 .catch(err => toast(err.message, 'err'));
            },
          },
        ],
      });
    }

    const offs = [
      on('tasks', data => { tasks = data.tasks ?? tasks; build(); }),
      on('task-ran', evt => toast(`Task “${evt.name}” executed`)),
      on('task-failed', evt => toast(`Task “${evt.name}” failed: ${evt.message}`, 'err')),
      on('store:status', () => build()),
    ];

    load();
    return () => offs.forEach(off => off());
  },
};
