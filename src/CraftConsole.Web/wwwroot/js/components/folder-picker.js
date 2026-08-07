// In-app folder browser for destination-path fields (server JAR downloads,
// backup jobs). Browsers can't hand JavaScript an absolute filesystem path
// from a native file dialog, so this walks /api/setup/browse instead —
// works the same on Windows and Linux, and needs nothing the Admin role
// doesn't already have (they can type any path into these forms today).
import { h, icon, modal, toast } from '../ui.js';
import { api } from '../api.js';
import { state } from '../store.js';
import { sep } from '../platform.js';

/**
 * Opens a folder-picker modal. Resolves with the chosen absolute path, or
 * null if cancelled.
 *
 * @param startPath  folder to open on. Falsy falls back to defaultPath, then
 *   to the host's default server root.
 * @param defaultPath what the "Default" shortcut jumps to, and the fallback
 *   start. Callers pass the root that suits their field — the backup
 *   destination wants defaultBackupRoot, a JAR directory wants
 *   defaultServerRoot. Falsy uses defaultServerRoot.
 */
export function openFolderPicker(startPath, { defaultPath } = {}) {
  return new Promise(resolve => {
    let currentPath = '';
    let parentPath = null;
    let resolved = false;

    const sys = state.system ?? {};
    const isWindows = sys.platform === 'windows';
    const home = sys.homeDirectory || '';
    const fallback = defaultPath || sys.defaultServerRoot || '';
    // An empty path from the API only ever means the Windows drive list —
    // Unix resolves the same request to "/", which is a real path.
    const rootLabel = isWindows ? 'This PC' : '/';

    const pathLabel = h('div', { class: 'browse-path mono ellipsis' }, 'Loading…');
    const upBtn = h('button', {
      class: 'btn sm ghost', title: 'Up a level', disabled: true,
      onclick: () => load(parentPath),
    }, icon('arrowUp'), 'Up');

    // OS-appropriate jumps. Windows has a drive list above the roots and no
    // single filesystem root; Unix has "/" and no drive concept, so the same
    // button means different things and is labelled accordingly.
    const rootBtn = h('button', {
      class: 'btn sm ghost',
      title: isWindows ? 'Show all drives' : 'Go to the filesystem root',
      onclick: () => load(isWindows ? '' : '/'),
    }, icon(isWindows ? 'drive' : 'folder'), isWindows ? 'This PC' : '/');

    const homeBtn = home
      ? h('button', { class: 'btn sm ghost', title: home, onclick: () => load(home) }, icon('house'), 'Home')
      : null;

    const defaultBtn = fallback
      ? h('button', { class: 'btn sm ghost', title: fallback, onclick: () => load(fallback) }, icon('folder'), 'Default')
      : null;

    const list = h('div', { class: 'browse-list' });
    const newName = h('input', { class: 'input', placeholder: 'New folder name (optional)' });

    /** Same folder, ignoring a trailing separator and Windows' case rules. */
    function samePath(a, b) {
      const trim = p => (p ?? '').replace(/[\\/]+$/, '');
      return isWindows
        ? trim(a).toLowerCase() === trim(b).toLowerCase()
        : trim(a) === trim(b);
    }

    /**
     * @param retreatToHome only set when opening on a *default* rather than a
     *   path the user typed. The browse API walks up to the nearest folder
     *   that exists, so a configured-but-never-created default (Windows ships
     *   none of C:\MinecraftServers) silently strands the picker on the drive
     *   root among $Recycle.Bin and Program Files. Home always exists.
     */
    async function load(path, { retreatToHome = false } = {}) {
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
        if (currentPath || parentPath !== null) { pathLabel.textContent = currentPath || rootLabel; upBtn.disabled = parentPath === null; }
        return;
      }

      currentPath = data.path ?? '';

      // The API resolved somewhere other than what was asked for, so the
      // default does not exist. Start from home instead of the drive root.
      if (retreatToHome && home && !samePath(currentPath, path) && !samePath(home, path))
        return load(home);

      parentPath = data.parent;
      pathLabel.textContent = currentPath || rootLabel;
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
        h('div', { class: 'browse-toolbar' }, upBtn, rootBtn, homeBtn, defaultBtn, pathLabel),
        list,
        h('div', { class: 'field', style: { marginTop: 'var(--s3)', marginBottom: 0 } },
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
            // The host tells us its separator (/api/system/info) — no need to
            // infer one from the shape of the path we happen to be sitting on.
            const s = sep();
            resolved = true;
            resolve(extra ? `${currentPath}${currentPath.endsWith(s) ? '' : s}${extra}` : currentPath);
          },
        },
      ],
    });

    // Open on the field's current value if it has one, else this field's
    // default, else home. retreatToHome only when falling back to a default:
    // if the user typed a path, the nearest existing ancestor of what they
    // typed is genuinely where they meant to be.
    load(startPath || fallback || home, { retreatToHome: !startPath });
  });
}
