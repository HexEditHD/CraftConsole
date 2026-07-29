// Backups: job definitions + on-demand runs (zips sources to a destination).
import { h, icon, toast, confirmDialog, modal, timeAgo } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';

export default {
  id: 'backups',
  title: 'Backups',
  icon: 'archive',

  render(el) {
    let jobs = [];
    const running = new Set();

    const list = h('div', { class: 'grid' });
    el.append(
      h('div', { class: 'view-head' },
        h('span', { class: 'sub' }, 'Each run creates a timestamped .zip of the job’s sources.'),
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm primary', onclick: () => openEditor(null) }, icon('plus'), 'New backup job')),
      list);

    async function load() {
      try { jobs = (await api.get('/api/backups')).jobs ?? []; }
      catch { jobs = []; }
      build();
    }

    function build() {
      list.innerHTML = '';
      if (!jobs.length) {
        list.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
          icon('archive'),
          h('div', { class: 'empty-title' }, 'No backup jobs'),
          h('div', { class: 'empty-sub' }, 'Create a job that zips your world folder (or the whole server directory) to a safe place.'))));
        return;
      }
      for (const job of jobs) {
        const isRunning = running.has(job.id);
        list.append(h('div', { class: 'card profile-card' },
          h('div', { class: 'info' },
            h('div', { class: 'name' },
              job.name,
              h('span', { class: 'badge' }, job.compression)),
            h('div', { class: 'meta' },
              `${job.sourcePaths.length} source${job.sourcePaths.length === 1 ? '' : 's'} → ${job.destinationPath}`),
            h('div', { class: 'meta muted' },
              job.lastRun ? `Last run ${timeAgo(job.lastRun)}` : 'Never run')),
          h('div', { class: 'actions' },
            h('button', {
              class: 'btn sm primary', disabled: isRunning,
              onclick: () => run(job),
            }, isRunning ? h('span', { class: 'spinner' }) : icon('play'), isRunning ? 'Running…' : 'Run now'),
            h('button', { class: 'btn sm icon-only', title: 'Edit', onclick: () => openEditor(job) }, icon('pencil')),
            h('button', {
              class: 'btn sm icon-only danger', title: 'Delete',
              onclick: async () => {
                if (!await confirmDialog('Delete backup job', `Delete “${job.name}”? Existing archives are kept.`, { danger: true, okLabel: 'Delete' })) return;
                await api.del(`/api/backups/${job.id}`);
                load();
              },
            }, icon('trash')))));
      }
    }

    async function run(job) {
      running.add(job.id);
      build();
      try { await api.post(`/api/backups/${job.id}/run`); }
      catch (err) {
        toast(err.message, 'err');
        running.delete(job.id);
        build();
      }
    }

    function openEditor(job) {
      const isNew = !job;
      const workingDir = state.status?.profile?.workingDirectory ?? '';
      const f = {
        name: h('input', { class: 'input', value: job?.name ?? 'World backup' }),
        sources: h('textarea', { class: 'textarea', placeholder: 'One path per line', value: (job?.sourcePaths ?? (workingDir ? [workingDir + '\\world'] : [])).join('\n') }),
        dest: h('input', { class: 'input', value: job?.destinationPath ?? '', placeholder: 'C:\\Backups\\minecraft' }),
        compression: h('select', { class: 'select' },
          ['Optimal', 'Fastest', 'NoCompression'].map(c =>
            h('option', { value: c, selected: (job?.compression ?? 'Optimal') === c }, c))),
      };

      modal({
        title: isNew ? 'New backup job' : `Edit “${job.name}”`,
        body: h('div', {},
          h('div', { class: 'field' }, h('label', {}, 'Name'), f.name),
          h('div', { class: 'field' }, h('label', {}, 'Source files / folders'), f.sources,
            h('span', { class: 'hint' }, 'Folders are zipped recursively.')),
          h('div', { class: 'field' }, h('label', {}, 'Destination folder'), f.dest),
          h('div', { class: 'field' }, h('label', {}, 'Compression'), f.compression)),
        actions: [
          { label: 'Cancel', kind: 'ghost' },
          {
            label: isNew ? 'Create job' : 'Save changes',
            kind: 'primary',
            onClick: () => {
              const sources = f.sources.value.split('\n').map(s => s.trim()).filter(Boolean);
              if (!f.name.value.trim() || !sources.length || !f.dest.value.trim()) {
                toast('Name, at least one source, and a destination are required.', 'err');
                return false;
              }
              const body = {
                name: f.name.value.trim(),
                sourcePaths: sources,
                destinationPath: f.dest.value.trim(),
                compression: f.compression.value,
              };
              const req = isNew ? api.post('/api/backups', body) : api.put(`/api/backups/${job.id}`, body);
              req.then(() => { toast(isNew ? 'Job created' : 'Job saved'); load(); })
                 .catch(err => toast(err.message, 'err'));
            },
          },
        ],
      });
    }

    const offs = [
      on('backups', data => { jobs = data.jobs ?? jobs; build(); }),
      on('backup-run', evt => {
        if (evt.phase === 'done') {
          running.delete(evt.id);
          toast(`Backup “${evt.name}” finished`);
          load();
        } else if (evt.phase === 'error') {
          running.delete(evt.id);
          toast(`Backup “${evt.name}” failed: ${evt.message}`, 'err');
          build();
        }
      }),
    ];

    load();
    return () => offs.forEach(off => off());
  },
};
