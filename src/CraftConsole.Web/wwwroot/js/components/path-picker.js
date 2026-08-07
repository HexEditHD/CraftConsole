// In-app browser for every field that takes a path on the server host.
//
// This deliberately does NOT open the OS file dialog, for two reasons that
// both make a native dialog the wrong tool here:
//
//   1. A browser will not hand JavaScript an absolute filesystem path.
//      <input type="file"> reports "C:\fakepath\name.jar", and the File
//      System Access API returns opaque handles with no path at all, by
//      design.
//   2. More fundamentally, the dialog would run on whichever machine the
//      browser is on. Every path here — jarPath, workingDirectory, backup
//      sources and destinations — is opened by the server process. Picking
//      with your laptop's Explorer while the panel runs on a headless box
//      would produce paths the server cannot resolve.
//
// So this walks the *server's* filesystem over /api/setup/browse and renders
// the result in the page. It needs nothing the Admin role does not already
// have: these fields accept a typed path today.
import { h, icon, modal, toast, fmtSize } from '../ui.js';
import { api } from '../api.js';
import { state } from '../store.js';
import { sep } from '../platform.js';

/** Containing folder of a path, using the host's separator. '' at a root. */
function parentOf(p) {
  const parts = p.replace(/[\\/]+$/, '').split(/[\\/]/);
  parts.pop();
  // A bare Windows drive ("C:") is not a usable path — restore its separator.
  if (parts.length === 1 && /^[A-Za-z]:$/.test(parts[0])) return parts[0] + sep();
  return parts.join(sep());
}

/**
 * Pairs a path input with a Browse button that writes the choice back into it.
 * Returns the wrapper to drop into a .field.
 *
 * Multi-select appends to what is already in a textarea rather than replacing
 * it, so a sources list can be built up over several visits. The change event
 * is dispatched because callers hang behaviour off it — the profile editor
 * derives the working directory from the JAR that was just chosen.
 */
export function withBrowse(input, opts = {}) {
  const isTextarea = input.tagName === 'TEXTAREA';
  const label = opts.browseLabel
    ?? (opts.multiple ? 'Browse for files and folders'
      : opts.mode === 'file' ? 'Browse for a file' : 'Browse for a folder');

  const btn = h('button', {
    class: 'btn sm icon-only', type: 'button', title: label, 'aria-label': label,
    onclick: async () => {
      const first = isTextarea
        ? (input.value.split('\n').map(s => s.trim()).find(Boolean) ?? '')
        : input.value.trim();
      // A list reopens in the folder its first entry lives in, not inside that
      // entry: you are almost always adding a sibling of what is already
      // listed, and opening inside an already-chosen folder shows you nothing
      // useful (often nothing at all, if it has no subfolders).
      const start = isTextarea && first ? parentOf(first) : first;
      const picked = await openPathPicker({ ...opts, startPath: start });
      if (picked == null) return;

      if (Array.isArray(picked)) {
        if (!picked.length) return;
        const existing = input.value.split('\n').map(s => s.trim()).filter(Boolean);
        input.value = [...new Set([...existing, ...picked])].join('\n');
      } else {
        input.value = picked;
      }
      input.dispatchEvent(new Event('change', { bubbles: true }));
    },
  }, icon('folder'));

  return h('div', { class: 'path-field' }, input, btn);
}

/**
 * Opens a path picker over the server's filesystem.
 *
 * @param startPath   path to open on. Falsy falls back to defaultPath, then home.
 * @param defaultPath what "Default" jumps to, and the fallback start. Callers
 *   pass the root that suits their field — a backup destination wants
 *   defaultBackupRoot, a JAR folder wants defaultServerRoot.
 * @param mode        'folder' (default) or 'file'.
 * @param ext         file-mode extension allow-list, e.g. '.jar'. Empty shows all.
 * @param multiple    allow several selections; resolves an array instead of a string.
 * @param title       dialog title override.
 * @returns the chosen absolute path, an array of them when multiple, or null.
 */
export function openPathPicker({
  startPath = '',
  defaultPath = '',
  mode = 'folder',
  ext = '',
  multiple = false,
  title = '',
} = {}) {
  return new Promise(resolve => {
    const wantFiles = mode === 'file' || multiple;
    let currentPath = '';
    let parentPath = null;
    let resolved = false;
    /** @type {Map<string, 'file'|'folder'>} chosen absolute path → kind */
    const picked = new Map();

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
    const selectionBar = h('div', { class: 'browse-selection', style: { display: 'none' } });
    const newName = h('input', { class: 'input', placeholder: 'New folder name (optional)' });
    const newFolderField = h('div', { class: 'field', style: { marginTop: 'var(--s3)', marginBottom: 0 } },
      h('label', {}, 'New folder name (optional)'),
      newName,
      h('span', { class: 'hint' }, 'Leave blank to select the folder shown above.'));

    /** Same path, ignoring a trailing separator and Windows' case rules. */
    function samePath(a, b) {
      const trim = p => (p ?? '').replace(/[\\/]+$/, '');
      return isWindows ? trim(a).toLowerCase() === trim(b).toLowerCase() : trim(a) === trim(b);
    }

    function syncSelection() {
      if (!multiple && mode !== 'file') return;
      selectionBar.innerHTML = '';
      if (!picked.size) { selectionBar.style.display = 'none'; syncPrimary(); return; }
      selectionBar.style.display = '';
      selectionBar.append(h('span', { class: 'cap' }, `Selected (${picked.size})`));
      for (const [p, kind] of picked) {
        selectionBar.append(h('span', { class: 'browse-chip', title: p },
          icon(kind === 'folder' ? 'folder' : 'file'),
          h('span', { class: 'ellipsis' }, p.split(/[\\/]/).pop() || p),
          h('button', {
            class: 'chip-x', 'aria-label': `Remove ${p}`,
            onclick: () => { picked.delete(p); syncSelection(); markRows(); },
          }, icon('x'))));
      }
      syncPrimary();
    }

    function toggle(path, kind) {
      if (picked.has(path)) picked.delete(path);
      else {
        if (!multiple) picked.clear();   // single file mode holds one at a time
        picked.set(path, kind);
      }
      syncSelection();
      markRows();
    }

    function markRows() {
      for (const row of list.querySelectorAll('.browse-row[data-path]'))
        row.classList.toggle('picked', picked.has(row.dataset.path));
    }

    /**
     * @param retreatToHome only set when opening on a *default* rather than a
     *   path the user typed. The browse API walks up to the nearest folder that
     *   exists, so a configured-but-never-created default (Windows ships no
     *   C:\MinecraftServers) would otherwise strand the picker on the drive
     *   root among $Recycle.Bin and Program Files. Home always exists.
     */
    async function load(path, { retreatToHome = false } = {}) {
      list.innerHTML = '';
      list.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));

      const q = new URLSearchParams({ path: path ?? '' });
      if (wantFiles) { q.set('files', 'true'); if (ext) q.set('ext', ext); }

      let data;
      try { data = await api.get(`/api/setup/browse?${q}`); }
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
      syncPrimary();

      const dirs = data.directories ?? [];
      const files = data.files ?? [];
      list.innerHTML = '';

      if (!dirs.length && !files.length) {
        list.append(h('div', { class: 'empty', style: { padding: '26px 10px' } },
          icon('folder'),
          h('div', { class: 'empty-sub' },
            wantFiles
              ? (ext ? `Nothing here matching ${ext}.` : 'This folder is empty.')
              : 'No subfolders here.')));
        return;
      }

      for (const dir of dirs) {
        const row = h('div', { class: 'browse-row', dataset: { path: dir.path } },
          // Only multi-select needs a folder to be both openable and choosable,
          // so the add control only exists there.
          multiple
            ? h('button', {
                class: 'browse-pick', 'aria-label': `Select folder ${dir.name}`, title: 'Add this folder',
                onclick: e => { e.stopPropagation(); toggle(dir.path, 'folder'); },
              }, icon('plus'))
            : null,
          h('button', {
            class: 'browse-open', title: dir.path,
            onclick: () => load(dir.path),
          }, icon('folder'), h('span', { class: 'name' }, dir.name)));
        list.append(row);
      }

      for (const file of files) {
        const row = h('div', { class: 'browse-row is-file', dataset: { path: file.path } },
          h('button', {
            class: 'browse-open', title: file.path,
            onclick: () => toggle(file.path, 'file'),
          },
            icon('file'),
            h('span', { class: 'name' }, file.name),
            h('span', { class: 'size' }, fmtSize(file.size ?? 0))));
        list.append(row);
      }
      markRows();
    }

    // ── Dialog ──────────────────────────────────────────────────────────
    const primaryLabel = multiple ? 'Add selected'
      : mode === 'file' ? 'Select this file'
      : 'Select this folder';

    let primaryBtn = null;
    function syncPrimary() {
      if (!primaryBtn) return;
      // A file must actually be chosen; a folder is implied by where you are.
      primaryBtn.disabled = (mode === 'file' || multiple) ? picked.size === 0 : false;
    }

    const m = modal({
      title: title || (multiple ? 'Choose files and folders' : mode === 'file' ? 'Choose a file' : 'Choose a folder'),
      wide: true,
      onClose: () => { if (!resolved) resolve(null); },
      body: h('div', { class: 'browse-picker' },
        h('div', { class: 'browse-toolbar' }, upBtn, rootBtn, homeBtn, defaultBtn, pathLabel),
        list,
        selectionBar,
        mode === 'folder' && !multiple ? newFolderField : null),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        {
          label: primaryLabel,
          kind: 'primary',
          onClick: () => {
            resolved = true;
            if (multiple) return void resolve([...picked.keys()]);
            if (mode === 'file') return void resolve([...picked.keys()][0] ?? null);
            const extra = newName.value.trim();
            // The host reports its own separator (/api/system/info) — no need to
            // infer one from the shape of the path we happen to be sitting on.
            const s = sep();
            resolve(extra ? `${currentPath}${currentPath.endsWith(s) ? '' : s}${extra}` : currentPath);
          },
        },
      ],
    });

    primaryBtn = [...document.querySelectorAll('.modal-foot .btn.primary')].pop() ?? null;
    syncPrimary();

    // Open on the field's current value if it has one, else this field's
    // default, else home. retreatToHome only when falling back to a default:
    // if the user typed a path, the nearest existing ancestor of what they
    // typed is genuinely where they meant to be.
    load(startPath || fallback || home, { retreatToHome: !startPath });
  });
}
