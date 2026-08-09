// Plugins: Installed (scan the active server's plugins folder, disable jars,
// remove anything installed via Modrinth) and Browse (search Modrinth and
// install a plugin/mod, with a prompt for required dependencies).
import { h, icon, toast, confirmDialog, debounce } from '../ui.js';
import { api } from '../api.js';

export default {
  id: 'plugins',
  title: 'Plugins',
  subtitle: 'Installed jars and Modrinth search',
  icon: 'puzzle',

  render(el) {
    let activeTab = 'installed';
    const TABS = [['installed', 'Installed'], ['browse', 'Browse']];
    const tabsEl = h('div', { class: 'seg', style: { width: 'fit-content', marginBottom: 'var(--s3)' } });
    const body = h('div');

    function buildTabs() {
      tabsEl.innerHTML = '';
      for (const [id, label] of TABS) {
        tabsEl.append(h('button', {
          class: `seg-item${activeTab === id ? ' active' : ''}`,
          onclick: () => {
            if (activeTab === id) return;
            activeTab = id;
            buildTabs();
            buildBody();
          },
        }, label));
      }
    }

    function buildBody() {
      body.innerHTML = '';
      if (activeTab === 'installed') renderInstalledTab(body);
      else renderBrowseTab(body);
    }

    buildTabs();
    buildBody();
    el.append(tabsEl, body);
  },
};

// ── Installed ────────────────────────────────────────────────────────────
function renderInstalledTab(container) {
  const modrinthList = h('div');
  const modrinthCard = h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'Installed via Modrinth'),
    modrinthList);

  const folderLabel = h('span', { class: 'sub mono ellipsis', style: { maxWidth: '440px' } }, '');
  const scanBody = h('div');
  const scanSection = h('div', {},
    h('div', { class: 'view-head' },
      folderLabel,
      h('button', {
        class: 'btn sm ghost icon-only', title: 'Copy folder path', 'aria-label': 'Copy folder path',
        onclick: () => { navigator.clipboard.writeText(folderLabel.textContent).then(() => toast('Path copied')); },
      }, icon('copy')),
      h('span', { class: 'spacer' }),
      h('button', { class: 'btn sm', onclick: loadScan }, icon('refresh'), 'Re-scan')),
    scanBody);

  container.append(modrinthCard, scanSection);

  async function loadModrinthList() {
    modrinthList.innerHTML = '';
    modrinthList.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
    let items;
    try { items = await api.get('/api/modrinth/installed'); }
    catch { items = []; }
    modrinthList.innerHTML = '';

    if (!items.length) {
      modrinthList.append(h('div', { class: 'empty-sub' }, 'Nothing installed via Modrinth yet — try the Browse tab.'));
      return;
    }

    modrinthList.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, 'Project'), h('th', {}, 'Version'), h('th', {}, 'File'), h('th', {}))),
      h('tbody', {}, items.map(i => h('tr', {},
        h('td', { style: { fontWeight: 600 } }, i.projectTitle),
        h('td', {}, h('span', { class: 'tag' }, i.versionNumber)),
        h('td', { class: 'dim small mono' }, i.fileName),
        h('td', {}, h('button', {
          class: 'btn sm danger', title: 'Delete the file and forget this install',
          onclick: async () => {
            if (!await confirmDialog('Remove', `Delete "${i.fileName}" and forget this install?`, { danger: true, okLabel: 'Remove' })) return;
            try {
              await api.del(`/api/modrinth/${encodeURIComponent(i.projectId)}`);
              toast(`${i.projectTitle} removed`);
              loadModrinthList();
            } catch (err) { toast(err.message, 'err'); }
          },
        }, icon('trash')))))))));
  }

  async function loadScan() {
    scanBody.innerHTML = '';
    scanBody.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Scanning plugins…'));

    let data;
    try { data = await api.get('/api/plugins'); }
    catch (err) { scanBody.innerHTML = ''; toast(err.message, 'err'); return; }

    folderLabel.textContent = data.folder ?? '';
    scanBody.innerHTML = '';

    if (!data.available) {
      scanBody.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
        icon('info'),
        h('div', { class: 'empty-title' }, 'Not available'),
        h('div', { class: 'empty-sub' }, data.reason))));
      return;
    }

    if (!data.plugins?.length) {
      scanBody.append(h('div', { class: 'card' }, h('div', { class: 'empty' },
        icon('box'),
        h('div', { class: 'empty-title' }, 'No plugins found'),
        h('div', { class: 'empty-sub' }, 'Drop plugin .jar files into the plugins folder, install one from Browse, and re-scan.'))));
      return;
    }

    scanBody.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
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
                loadScan();
              } catch (err) { toast(err.message, 'err'); }
            },
          }, 'Disable')))))))));
  }

  loadModrinthList();
  loadScan();
}

// ── Browse ───────────────────────────────────────────────────────────────
function renderBrowseTab(container) {
  const searchInput = h('input', { type: 'search', placeholder: 'Search Modrinth…' });
  const searchBox = h('div', { class: 'console-search', style: { maxWidth: '420px' } }, icon('search'), searchInput);
  const status = h('div', { class: 'dl-status' });
  const results = h('div', { class: 'grid modrinth-results' });

  container.append(h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'Browse Modrinth'),
    searchBox, status, results));

  searchInput.addEventListener('input', debounce(runSearch, 350));

  async function runSearch() {
    status.innerHTML = '';
    status.append(h('span', { class: 'spinner' }), 'Searching…');
    results.innerHTML = '';

    let data;
    try { data = await api.get(`/api/modrinth/search?query=${encodeURIComponent(searchInput.value.trim())}&limit=24`); }
    catch (err) { status.innerHTML = ''; toast(err.message, 'err'); return; }
    status.innerHTML = '';

    if (!data.hits?.length) {
      results.append(h('div', { class: 'empty', style: { padding: '26px 10px', gridColumn: '1 / -1' } },
        icon('puzzle'),
        h('div', { class: 'empty-title' }, 'No results'),
        h('div', { class: 'empty-sub' }, 'Try a different search — or this server type has no plugin or mod ecosystem.')));
      return;
    }
    for (const hit of data.hits) results.append(hitCard(hit));
  }

  function hitCard(hit) {
    const installBtn = h('button', { class: 'btn sm primary', onclick: () => install(hit, installBtn) }, icon('download'), 'Install');
    return h('div', { class: 'card', style: { padding: '12px 14px' } },
      h('div', { style: { display: 'flex', gap: '10px', alignItems: 'flex-start' } },
        hit.iconUrl
          ? h('img', { class: 'modrinth-card-icon', src: hit.iconUrl, alt: '' })
          : h('div', { class: 'modrinth-card-icon placeholder' }, icon('puzzle')),
        h('div', { style: { flex: '1', minWidth: '0' } },
          h('div', { style: { fontWeight: 600 } }, hit.title),
          h('div', { class: 'dimmer small' }, `${hit.author} · ${hit.downloads.toLocaleString()} downloads`),
          h('div', { class: 'dim small', style: { marginTop: '4px' } }, hit.description))),
      h('div', { style: { marginTop: '10px', display: 'flex', justifyContent: 'flex-end' } }, installBtn));
  }

  async function install(hit, btn) {
    btn.disabled = true;
    try {
      const versions = await api.get(`/api/modrinth/versions?projectId=${encodeURIComponent(hit.projectId)}`);
      if (!versions.length) { toast(`No version of "${hit.title}" is compatible with this server.`, 'err'); return; }
      await installVersion(versions[0].id, hit.title);
    } catch (err) { toast(err.message, 'err'); }
    finally { btn.disabled = false; }
  }

  async function installVersion(versionId, title, includeDependencies = false) {
    const result = await api.post('/api/modrinth/install', { versionId, includeDependencies });

    if (result.needsDependencyConfirmation) {
      const names = result.requiredDependencies.map(d => d.projectTitle).join(', ');
      if (await confirmDialog('Required dependencies', `"${title}" requires: ${names}. Install them too?`, { okLabel: 'Install all' })) {
        await installVersion(versionId, title, true);
      } else {
        toast(`"${title}" needs its required dependencies — not installed.`, 'err');
      }
      return;
    }

    toast(`Installed ${result.installed.map(i => i.projectTitle).join(', ')}`);
  }

  runSearch();
}
