// Config editor: file tree of the server directory, tabbed editing with
// dirty tracking and Ctrl+S save.
import { h, icon, toast, confirmDialog, fmtSize } from '../ui.js';
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

    el.append(h('div', { class: 'editor-layout' }, tree, editorMain));
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
          h('div', { class: 'empty-sub' }, 'No editable files (.yml, .json, .properties, .txt, .log) found yet.')));
        return;
      }
      for (const node of data.nodes) tree.append(nodeEl(node));
    }

    function nodeEl(node) {
      if (!node.isDirectory) {
        const row = h('div', {
          class: 'tree-row', dataset: { path: node.path },
          onclick: () => open(node),
        },
          icon('file'), h('span', { class: 'name' }, node.name),
          h('span', { class: 'size' }, fmtSize(node.size)));
        return h('div', { class: 'tree-node' }, row);
      }

      const wrap = h('div', { class: 'tree-node' });
      const chevron = icon('chevronDown');
      const row = h('div', {
        class: 'tree-row',
        onclick: () => {
          wrap.classList.toggle('collapsed');
          chevron.style.transform = wrap.classList.contains('collapsed') ? 'rotate(-90deg)' : '';
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
