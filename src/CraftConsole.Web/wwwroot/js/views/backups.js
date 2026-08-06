// Backups: job definitions + on-demand runs (zips sources to a destination).
import { h, icon, toast, confirmDialog, modal, timeAgo, fmtSize } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state, isAdmin } from '../store.js';
import { joinPath } from '../platform.js';

export default {
  id: 'backups',
  title: 'Backups',
  subtitle: 'Jobs, on-demand runs and restores',
  icon: 'archive',

  render(el) {
    let jobs = [];
    const running = new Set();

    const list = h('div', { class: 'grid' });
    el.append(
      h('div', { class: 'view-head' },
        h('span', { class: 'sub' }, 'Each run creates a timestamped .zip of the job’s sources.'),
        h('span', { class: 'spacer' }),
        isAdmin() ? h('button', { class: 'btn sm primary', onclick: () => openEditor(null) }, icon('plus'), 'New backup job') : null),
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
        const enabled = job.isEnabled ?? true;
        const admin = isAdmin();
        list.append(h('div', { class: 'card profile-card', style: enabled ? null : { opacity: .55 } },
          admin
            ? h('label', { class: 'switch', title: enabled ? 'Enabled' : 'Disabled' },
                h('input', {
                  type: 'checkbox', checked: enabled,
                  role: 'switch', 'aria-checked': String(enabled), 'aria-label': `${enabled ? 'Disable' : 'Enable'} “${job.name}”`,
                  onchange: e => toggle(job, e.target.checked),
                }),
                h('span', { class: 'track' }))
            : null,
          h('div', { class: 'info' },
            h('div', { class: 'name' },
              job.name,
              h('span', { class: 'tag' }, job.compression),
              enabled ? null : h('span', { class: 'tag warn' }, 'Disabled')),
            h('div', { class: 'meta' },
              `${job.sourcePaths.length} source${job.sourcePaths.length === 1 ? '' : 's'} → ${job.destinationPath}`),
            h('div', { class: 'meta dimmer' },
              job.lastRun ? `Last run ${timeAgo(job.lastRun)}` : 'Never run')),
          h('div', { class: 'actions' },
            h('button', {
              class: 'btn sm primary', disabled: isRunning || !enabled,
              title: enabled ? '' : 'This job is disabled — enable it first.',
              onclick: () => run(job),
            }, isRunning ? h('span', { class: 'spinner' }) : icon('play'), isRunning ? 'Running…' : 'Run now'),
            admin ? h('button', {
              class: 'btn sm', title: 'Restore an archive from this job',
              onclick: () => openRestore(job),
            }, icon('archive'), 'Restore') : null,
            admin ? h('button', { class: 'btn sm icon-only', title: 'Edit', 'aria-label': `Edit “${job.name}”`, onclick: () => openEditor(job) }, icon('pencil')) : null,
            admin ? h('button', {
              class: 'btn sm icon-only danger', title: 'Delete', 'aria-label': `Delete “${job.name}”`,
              onclick: async () => {
                if (!await confirmDialog('Delete backup job', `Delete “${job.name}”? Existing archives are kept.`, { danger: true, okLabel: 'Delete' })) return;
                await api.del(`/api/backups/${job.id}`);
                load();
              },
            }, icon('trash')) : null)));
      }
    }

    async function toggle(job, enabled) {
      try { await api.put(`/api/backups/${job.id}`, { ...job, isEnabled: enabled }); }
      catch (err) { toast(err.message, 'err'); build(); }
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

    async function openRestore(job) {
      const serverStopped = ['Stopped', 'Crashed'].includes(state.status?.status ?? 'Stopped');

      let archives = [];
      try { archives = (await api.get(`/api/backups/${job.id}/archives`)).archives ?? []; }
      catch (err) { toast(err.message, 'err'); return; }

      if (!archives.length) {
        modal({
          title: `Restore from “${job.name}”`,
          body: h('div', { class: 'empty' },
            icon('archive'),
            h('div', { class: 'empty-title' }, 'No archives yet'),
            h('div', { class: 'empty-sub' },
              `Nothing found in ${job.destinationPath}. Run the job first to create one.`)),
          actions: [{ label: 'Close', kind: 'ghost' }],
        });
        return;
      }

      const select = h('select', { class: 'select' },
        archives.map(a => h('option', { value: a.fileName },
          `${a.fileName}  —  ${fmtSize(a.sizeBytes)}, ${timeAgo(a.createdAt)}`)));

      const target = h('input', {
        class: 'input',
        value: state.status?.profile?.workingDirectory ?? '',
        placeholder: 'Directory to extract into',
      });

      modal({
        title: `Restore from “${job.name}”`,
        wide: true,
        body: h('div', {},
          !serverStopped
            ? h('div', {
                class: 'banner warn inline',
                style: { margin: '0 0 14px' },
              },
                icon('alert'),
                h('span', {}, 'Stop the server first. Restoring over a running world would be overwritten by the next autosave.'))
            : null,
          h('div', { class: 'field' }, h('label', {}, 'Archive'), select),
          h('div', { class: 'field' },
            h('label', {}, 'Restore into'), target,
            h('span', { class: 'hint' },
              'Files in the archive overwrite files of the same name. Anything else in the directory is left untouched.')),
          h('p', { class: 'dimmer small prose' },
            'Consider running this job once before restoring, so the current state is recoverable.')),
        actions: [
          { label: 'Cancel', kind: 'ghost' },
          {
            label: 'Restore',
            kind: 'danger',
            onClick: async () => {
              if (!serverStopped) { toast('Stop the server before restoring.', 'err'); return false; }
              if (!target.value.trim()) { toast('A target directory is required.', 'err'); return false; }

              const archive = select.value;
              if (!await confirmDialog(
                'Confirm restore',
                `Extract “${archive}” into ${target.value.trim()}? Files with matching names will be overwritten.`,
                { danger: true, okLabel: 'Restore' })) return false;

              try {
                await api.post(`/api/backups/${job.id}/restore`, {
                  archive,
                  targetDirectory: target.value.trim(),
                });
                toast(`Restored ${archive}`);
              } catch (err) {
                toast(err.message, 'err');
                return false; // keep the dialog open so the choice isn't lost
              }
            },
          },
        ],
      });
    }

    function openEditor(job) {
      const isNew = !job;
      const workingDir = state.status?.profile?.workingDirectory ?? '';
      const f = {
        name: h('input', { class: 'input', value: job?.name ?? 'World backup' }),
        sources: h('textarea', { class: 'textarea', placeholder: 'One path per line', value: (job?.sourcePaths ?? (workingDir ? [joinPath(workingDir, 'world')] : [])).join('\n') }),
        dest: h('input', { class: 'input', value: job?.destinationPath ?? '', placeholder: state.system?.defaultBackupRoot ?? 'C:\\Backups\\minecraft' }),
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
              return req.then(() => { toast(isNew ? 'Job created' : 'Job saved'); load(); })
                 .catch(err => { toast(err.message, 'err'); return false; });
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
