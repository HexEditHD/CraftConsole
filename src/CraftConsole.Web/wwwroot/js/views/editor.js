// Config editor: file tree of the server directory, tabbed editing with
// dirty tracking and Ctrl+S save, plus upload/download for everything else
// in that tree (worlds, jars, modpacks) that isn't a text file to edit.
import { h, icon, toast, confirmDialog, modal, fmtSize } from '../ui.js';
import { api } from '../api.js';

export default {
  id: 'editor',
  title: 'Files',
  subtitle: 'Config editor jailed to the server directory',
  icon: 'file',

  render(el) {
    let tabs = [];        // {path, name, content, savedContent, dirty}
    let active = null;

    const tree = h('div', { class: 'file-tree' });
    const uploadInput = h('input', {
      type: 'file', multiple: true, style: { display: 'none' },
      onchange: () => {
        if (uploadInput.files.length) uploadFiles([...uploadInput.files], '');
        uploadInput.value = '';
      },
    });
    const progressLabel = h('span', {}, '');
    const progressFill = h('div', {});
    const progressBar = h('div', { class: 'upload-progress', style: { display: 'none' } },
      progressLabel, h('div', { class: 'progress' }, progressFill));
    const treeHeader = h('div', { class: 'tree-header' },
      h('span', {}, 'Server files'),
      h('span', { class: 'spacer' }),
      h('button', { class: 'btn sm ghost', onclick: () => uploadInput.click() }, icon('upload'), 'Upload'),
      uploadInput);
    const treePane = h('div', { class: 'file-tree-pane' }, treeHeader, tree, progressBar);

    // Root-level drop target — a drop inside a folder row stops propagation
    // before it reaches here, so this only fires for drops on the tree
    // background itself (i.e. "put this at the top level").
    treePane.addEventListener('dragover', e => { e.preventDefault(); treePane.classList.add('drop-target'); });
    treePane.addEventListener('dragleave', e => { if (!treePane.contains(e.relatedTarget)) treePane.classList.remove('drop-target'); });
    treePane.addEventListener('drop', e => {
      e.preventDefault();
      treePane.classList.remove('drop-target');
      const files = [...(e.dataTransfer?.files ?? [])];
      if (files.length) uploadFiles(files, '');
    });

    const tabBar = h('div', { class: 'editor-tabs' });
    const textarea = h('textarea', {
      class: 'editor-textarea', spellcheck: false,
      oninput: () => {
        if (!active) return;
        active.content = textarea.value;
        const dirty = active.content !== active.savedContent;
        if (dirty !== active.dirty) { active.dirty = dirty; buildTabBar(); }
        syncStatus();
      },
      onkeydown: e => {
        if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save(); }
      },
    });
    const statusPath = h('span', { class: 'mono' }, '');
    const statusInfo = h('span', {}, '');
    const saveBtn = h('button', { class: 'btn sm', onclick: save, disabled: true }, icon('save'), 'Save');
    const syncSaveBtn = () => {
      const dirty = !!active?.dirty;
      saveBtn.className = `btn sm${dirty ? ' primary' : ''}`;
      saveBtn.disabled = !dirty;
    };

    const editorMain = h('div', { class: 'editor-main' },
      tabBar,
      h('div', { class: 'editor-body' }, textarea),
      h('div', { class: 'editor-status' },
        statusPath, h('span', { class: 'spacer' }), statusInfo, saveBtn));

    el.append(h('div', { class: 'editor-layout' }, treePane, editorMain));
    el.style.height = '100%';

    // ── Tree ─────────────────────────────────────────────────────────────
    async function loadTree() {
      tree.innerHTML = '';
      tree.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
      let data;
      try { data = await api.get('/api/files/tree'); }
      catch { data = { available: true, reason: null, nodes: [] }; }
      tree.innerHTML = '';

      if (!data.available) {
        tree.append(h('div', { class: 'empty', style: { padding: '26px 10px' } },
          icon('info'),
          h('div', { class: 'empty-sub' }, data.reason)));
        return;
      }

      if (!data.nodes?.length) {
        tree.append(h('div', { class: 'empty', style: { padding: '26px 10px' } },
          icon('folder'),
          h('div', { class: 'empty-sub' }, 'No files yet — drag files here or use Upload.')));
        return;
      }
      for (const node of data.nodes) tree.append(nodeEl(node));
    }

    function nodeEl(node) {
      if (!node.isDirectory) {
        const row = h('div', {
          class: `tree-row${node.isEditable ? '' : ' non-editable'}`, dataset: { path: node.path },
          onclick: node.isEditable ? () => open(node) : null,
        },
          icon('file'), h('span', { class: 'name' }, node.name),
          h('span', { class: 'size' }, fmtSize(node.size)),
          h('button', {
            class: 'btn ghost sm icon-only', title: `Download ${node.name}`, 'aria-label': `Download ${node.name}`,
            onclick: e => { e.stopPropagation(); downloadFile(node); },
          }, icon('download')));
        return h('div', { class: 'tree-node' }, row);
      }

      const wrap = h('div', { class: 'tree-node' });
      const chevron = icon('chevronDown');
      // A folder row is also a drop target — uploads land inside it rather
      // than at the tree root. stopPropagation keeps the drop from also
      // triggering treePane's own root-level handler above.
      const row = h('div', {
        class: 'tree-row',
        onclick: () => {
          wrap.classList.toggle('collapsed');
          chevron.style.transform = wrap.classList.contains('collapsed') ? 'rotate(-90deg)' : '';
        },
        ondragover: e => { e.preventDefault(); e.stopPropagation(); row.classList.add('drop-target'); },
        ondragleave: () => row.classList.remove('drop-target'),
        ondrop: e => {
          e.preventDefault();
          e.stopPropagation();
          row.classList.remove('drop-target');
          const files = [...(e.dataTransfer?.files ?? [])];
          if (files.length) uploadFiles(files, node.path);
        },
      }, chevron, icon('folder'), h('span', { class: 'name', style: { fontWeight: 600 } }, node.name));
      wrap.append(row, h('div', { class: 'tree-children' }, node.children.map(nodeEl)));
      return wrap;
    }

    function markOpenFiles() {
      tree.querySelectorAll('.tree-row.open-file').forEach(r => r.classList.remove('open-file'));
      if (active)
        tree.querySelector(`.tree-row[data-path="${CSS.escape(active.path)}"]`)?.classList.add('open-file');
    }

    // ── Download ─────────────────────────────────────────────────────────
    function downloadFile(node) {
      const a = h('a', { href: `/api/files/download?path=${encodeURIComponent(node.path)}`, download: node.name });
      document.body.append(a);
      a.click();
      a.remove();
    }

    // ── Upload ───────────────────────────────────────────────────────────
    function chooseZipAction(name) {
      return new Promise(resolve => {
        let choice = 'cancel';
        modal({
          title: 'Upload archive',
          body: h('p', { class: 'dim' },
            `"${name}" is a zip archive. Extract its contents into this folder, or upload it as a single file?`),
          onClose: () => resolve(choice),
          actions: [
            { label: 'Cancel', kind: 'ghost', onClick: () => { choice = 'cancel'; } },
            { label: 'Upload as file', onClick: () => { choice = 'file'; } },
            { label: 'Extract here', kind: 'primary', onClick: () => { choice = 'extract'; } },
          ],
        });
      });
    }

    function setProgress(label, frac) {
      progressBar.style.display = '';
      progressLabel.textContent = `${label} — ${Math.round(frac * 100)}%`;
      progressFill.style.width = `${Math.round(frac * 100)}%`;
    }
    function clearProgress() { progressBar.style.display = 'none'; }

    async function uploadOne(file, destPath, { overwrite = false } = {}) {
      let extract = false;
      if (/\.zip$/i.test(file.name)) {
        const choice = await chooseZipAction(file.name);
        if (choice === 'cancel') return;
        extract = choice === 'extract';
      }

      const formData = new FormData();
      formData.append('file', file);
      formData.append('path', destPath);
      if (extract) formData.append('extract', 'true');
      if (overwrite) formData.append('overwrite', 'true');

      setProgress(`Uploading ${file.name}`, 0);
      try {
        await api.uploadWithProgress('/api/files/upload', formData, frac => setProgress(`Uploading ${file.name}`, frac));
        toast(extract ? `Extracted ${file.name}` : `Uploaded ${file.name}`);
      } catch (err) {
        if (err.status === 409) {
          clearProgress();
          if (await confirmDialog('Replace file', `${err.message} Overwrite it?`, { danger: true, okLabel: 'Overwrite' }))
            return uploadOne(file, destPath, { overwrite: true });
          return;
        }
        toast(err.message, 'err');
      } finally {
        clearProgress();
      }
    }

    async function uploadFiles(files, destPath) {
      for (const file of files) await uploadOne(file, destPath);
      loadTree();
    }

    // ── Tabs ─────────────────────────────────────────────────────────────
    async function open(node) {
      const existing = tabs.find(t => t.path === node.path);
      if (existing) { setActive(existing); return; }

      let data;
      try { data = await api.get(`/api/files/content?path=${encodeURIComponent(node.path)}`); }
      catch (err) { toast(err.message, 'err'); return; }

      const tab = { path: node.path, name: node.name, content: data.content, savedContent: data.content, dirty: false };
      tabs.push(tab);
      setActive(tab);
    }

    function setActive(tab) {
      active = tab;
      textarea.value = tab?.content ?? '';
      textarea.disabled = !tab;
      buildTabBar();
      syncStatus();
      markOpenFiles();
      if (tab) textarea.focus();
    }

    async function closeTab(tab) {
      if (tab.dirty && !await confirmDialog('Discard changes', `“${tab.name}” has unsaved changes. Close anyway?`, { danger: true, okLabel: 'Discard' }))
        return;
      const idx = tabs.indexOf(tab);
      tabs = tabs.filter(t => t !== tab);
      if (active === tab) setActive(tabs[Math.max(0, idx - 1)] ?? null);
      else buildTabBar();
    }

    function buildTabBar() {
      tabBar.innerHTML = '';
      for (const tab of tabs) {
        tabBar.append(h('button', {
          class: `editor-tab${tab === active ? ' active' : ''}`,
          onclick: e => { if (!e.target.closest('.close')) setActive(tab); },
          onmousedown: e => { if (e.button === 1) e.preventDefault(); },
          onauxclick: e => { if (e.button === 1) closeTab(tab); },
        },
          tab.dirty ? h('span', { class: 'dirty-dot', title: 'Unsaved changes' }) : null,
          tab.name,
          h('span', {
            class: 'close', title: 'Close',
            onclick: e => { e.stopPropagation(); closeTab(tab); },
          }, icon('x', 'icon'))));
      }
      syncSaveBtn();
    }

    function syncStatus() {
      statusPath.textContent = active?.path ?? 'No file open';
      if (active) {
        const lines = active.content.split('\n').length;
        statusInfo.textContent = `${lines} lines${active.dirty ? ' · unsaved' : ''}`;
      } else statusInfo.textContent = '';
      syncSaveBtn();
    }

    async function save() {
      if (!active?.dirty) return;
      try {
        await api.put('/api/files/content', { path: active.path, content: active.content });
        active.savedContent = active.content;
        active.dirty = false;
        buildTabBar();
        syncStatus();
        toast(`Saved ${active.name}`);
      } catch (err) { toast(err.message, 'err'); }
    }

    setActive(null);
    loadTree();

    return () => { el.style.height = ''; };
  },
};
