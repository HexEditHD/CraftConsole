// In-app folder browser for destination-path fields (server JAR downloads,
// backup jobs). Browsers can't hand JavaScript an absolute filesystem path
// from a native file dialog, so this walks /api/setup/browse instead —
// works the same on Windows and Linux, and needs nothing the Admin role
// doesn't already have (they can type any path into these forms today).
import { h, icon, modal, toast } from '../ui.js';
import { api } from '../api.js';

/**
 * Opens a folder-picker modal. Resolves with the chosen absolute path, or
 * null if cancelled.
 * @param startPath initial folder to browse into (falsy → drive list on
 *   Windows, "/" on Linux)
 */
export function openFolderPicker(startPath) {
  return new Promise(resolve => {
    let currentPath = '';
    let parentPath = null;
    let resolved = false;

    const pathLabel = h('div', { class: 'browse-path mono ellipsis' }, 'Loading…');
    const upBtn = h('button', {
      class: 'btn sm ghost', title: 'Up a level', disabled: true,
      onclick: () => load(parentPath),
    }, icon('arrowUp'), 'Up');
    const list = h('div', { class: 'browse-list' });
    const newName = h('input', { class: 'input', placeholder: 'New folder name (optional)' });

    async function load(path) {
      list.innerHTML = '';
      list.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
      let data;
      try { data = await api.get(`/api/setup/browse?path=${encodeURIComponent(path ?? '')}`); }
      catch (err) {
        toast(err.message, 'err');
        list.innerHTML = '';
        list.append(h('div', { class: 'empty', style: { padding: '26px 10px' } },
          icon('warningCircle'),
          h('div', { class: 'empty-sub' }, err.message)));
        // Leave pathLabel/upBtn showing wherever we were before this failed
        // navigation, rather than stuck on "Loading…" with no way back.
        if (currentPath || parentPath !== null) { pathLabel.textContent = currentPath || 'This PC'; upBtn.disabled = parentPath === null; }
        return;
      }

      currentPath = data.path ?? '';
      parentPath = data.parent;
      pathLabel.textContent = currentPath || 'This PC';
      upBtn.disabled = parentPath === null;

      list.innerHTML = '';
      if (!data.directories?.length) {
        list.append(h('div', { class: 'empty', style: { padding: '26px 10px' } },
          icon('folder'),
          h('div', { class: 'empty-sub' }, 'No subfolders here.')));
        return;
      }
      for (const dir of data.directories) {
        list.append(h('button', {
          class: 'browse-row',
          onclick: () => load(dir.path),
        }, icon('folder'), h('span', { class: 'name' }, dir.name)));
      }
    }

    const m = modal({
      title: 'Choose a folder',
      wide: true,
      onClose: () => { if (!resolved) resolve(null); },
      body: h('div', { class: 'browse-picker' },
        h('div', { class: 'browse-toolbar' }, upBtn, pathLabel),
        list,
        h('div', { class: 'field', style: { marginTop: 'var(--space-3)', marginBottom: 0 } },
          h('label', {}, 'New folder name (optional)'),
          newName,
          h('span', { class: 'hint' }, 'Leave blank to select the folder shown above.'))),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        {
          label: 'Select this folder',
          kind: 'primary',
          onClick: () => {
            const extra = newName.value.trim();
            const sep = currentPath.includes('/') && !currentPath.includes('\\') ? '/' : '\\';
            resolved = true;
            resolve(extra ? `${currentPath}${currentPath.endsWith(sep) ? '' : sep}${extra}` : currentPath);
          },
        },
      ],
    });

    load(startPath);
  });
}
