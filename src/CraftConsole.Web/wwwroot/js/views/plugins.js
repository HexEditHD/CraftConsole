// Plugins: Installed (scan the active server's plugins folder, disable jars,
// remove anything installed via Modrinth/CurseForge) and Browse (search
// either provider and install a plugin/mod, with a prompt for required
// dependencies).
import { h, icon, toast, confirmDialog, debounce } from '../ui.js';
import { api } from '../api.js';
import { state } from '../store.js';

export default {
  id: 'plugins',
  title: 'Plugins',
  subtitle: 'Installed jars, Modrinth and CurseForge search',
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

// ── Providers ────────────────────────────────────────────────────────────
// Search/install differ enough between the two (Modrinth installs by a
// single versionId it resolves itself; CurseForge needs a modId *and* a
// fileId, and gates everything behind an API key) that each gets its own
// adapter rather than forcing a shared shape. Everything that reads a
// dependency/install name checks both projectTitle (Modrinth) and modName
// (CurseForge) — see depName/installName below.
const PROVIDERS = {
  modrinth: {
    label: 'Modrinth',
    placeholder: 'Search Modrinth…',
    available: () => true,
    unavailableReason: '',
    search: query => api.get(`/api/modrinth/search?query=${encodeURIComponent(query)}&limit=24`),
    normalizeHit: h => ({ key: h.projectId, title: h.title, author: h.author, downloads: h.downloads, iconUrl: h.iconUrl, description: h.description }),
    resolveTarget: async hit => {
      const versions = await api.get(`/api/modrinth/versions?projectId=${encodeURIComponent(hit.key)}`);
      return versions.length ? { versionId: versions[0].id } : null;
    },
    install: (target, includeDependencies) => api.post('/api/modrinth/install', { versionId: target.versionId, includeDependencies }),
  },
  curseforge: {
    label: 'CurseForge',
    placeholder: 'Search CurseForge…',
    available: () => !!state.settings?.hasCurseForgeApiKey,
    unavailableReason: 'Add a CurseForge API key in Settings → Integrations & about to browse CurseForge.',
    search: query => api.get(`/api/curseforge/search?query=${encodeURIComponent(query)}&limit=24`),
    normalizeHit: h => ({ key: h.modId, title: h.name, author: h.author, downloads: h.downloads, iconUrl: h.iconUrl, description: h.summary }),
    resolveTarget: async hit => {
      const files = await api.get(`/api/curseforge/files?modId=${encodeURIComponent(hit.key)}`);
      return files.length ? { modId: hit.key, fileId: files[0].id } : null;
    },
    install: (target, includeDependencies) => api.post('/api/curseforge/install', { modId: target.modId, fileId: target.fileId, includeDependencies }),
  },
};

const depName = d => d.projectTitle ?? d.modName;
const installName = i => i.projectTitle ?? i.modName;

// ── Installed ────────────────────────────────────────────────────────────
function renderInstalledTab(container) {
  const modrinthList = h('div');
  const curseForgeList = h('div');
  container.append(
    h('div', { class: 'card' }, h('div', { class: 'card-title' }, 'Installed via Modrinth'), modrinthList),
    h('div', { class: 'card' }, h('div', { class: 'card-title' }, 'Installed via CurseForge'), curseForgeList));

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

  container.append(scanSection);

  function trackedTable(items, { nameKey, idKey, idParam, apiPrefix, emptyMessage }) {
    if (!items.length) return h('div', { class: 'empty-sub' }, emptyMessage);

    return h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'File'), h('th', {}))),
      h('tbody', {}, items.map(i => h('tr', {},
        h('td', { style: { fontWeight: 600 } }, i[nameKey]),
        h('td', { class: 'dim small mono' }, i.fileName),
        h('td', {}, h('button', {
          class: 'btn sm danger', title: 'Delete the file and forget this install',
          onclick: async () => {
            if (!await confirmDialog('Remove', `Delete "${i.fileName}" and forget this install?`, { danger: true, okLabel: 'Remove' })) return;
            try {
              await api.del(`/api/${apiPrefix}/${encodeURIComponent(i[idKey])}`);
              toast(`${i[nameKey]} removed`);
              loadModrinthList();
              loadCurseForgeList();
            } catch (err) { toast(err.message, 'err'); }
          },
        }, icon('trash'))))))));
  }

  async function loadModrinthList() {
    modrinthList.innerHTML = '';
    modrinthList.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
    let items;
    try { items = await api.get('/api/modrinth/installed'); }
    catch { items = []; }
    modrinthList.innerHTML = '';
    modrinthList.append(trackedTable(items, {
      nameKey: 'projectTitle', idKey: 'projectId', apiPrefix: 'modrinth',
      emptyMessage: 'Nothing installed via Modrinth yet — try the Browse tab.',
    }));
  }

  async function loadCurseForgeList() {
    curseForgeList.innerHTML = '';
    curseForgeList.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
    let items;
    try { items = await api.get('/api/curseforge/installed'); }
    catch { items = []; }
    curseForgeList.innerHTML = '';
    curseForgeList.append(trackedTable(items, {
      nameKey: 'modName', idKey: 'modId', apiPrefix: 'curseforge',
      emptyMessage: 'Nothing installed via CurseForge yet — try the Browse tab.',
    }));
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
  loadCurseForgeList();
  loadScan();
}

// ── Browse ───────────────────────────────────────────────────────────────
function renderBrowseTab(container) {
  let providerKey = 'modrinth';

  const providerSeg = h('div', { class: 'seg', style: { marginBottom: 'var(--s3)' } });
  const searchInput = h('input', { type: 'search' });
  const searchBox = h('div', { class: 'console-search', style: { maxWidth: '420px' } }, icon('search'), searchInput);
  const status = h('div', { class: 'dl-status' });
  const results = h('div', { class: 'grid modrinth-results' });

  container.append(h('div', { class: 'card' },
    h('div', { class: 'card-title' }, 'Browse'),
    providerSeg,
    searchBox, status, results));

  function buildProviderSeg() {
    providerSeg.innerHTML = '';
    for (const [key, provider] of Object.entries(PROVIDERS)) {
      providerSeg.append(h('button', {
        class: `seg-item${providerKey === key ? ' active' : ''}`,
        onclick: () => {
          if (providerKey === key) return;
          providerKey = key;
          buildProviderSeg();
          syncForProvider();
        },
      }, provider.label));
    }
  }

  function syncForProvider() {
    const provider = PROVIDERS[providerKey];
    searchInput.placeholder = provider.placeholder;
    if (!provider.available()) {
      searchBox.style.display = 'none';
      status.innerHTML = '';
      results.innerHTML = '';
      results.append(h('div', { class: 'empty', style: { padding: '26px 10px', gridColumn: '1 / -1' } },
        icon('info'),
        h('div', { class: 'empty-title' }, 'Not available'),
        h('div', { class: 'empty-sub' }, provider.unavailableReason)));
      return;
    }
    searchBox.style.display = '';
    runSearch();
  }

  searchInput.addEventListener('input', debounce(runSearch, 350));

  async function runSearch() {
    const provider = PROVIDERS[providerKey];
    status.innerHTML = '';
    status.append(h('span', { class: 'spinner' }), 'Searching…');
    results.innerHTML = '';

    let data;
    try { data = await provider.search(searchInput.value.trim()); }
    catch (err) { status.innerHTML = ''; toast(err.message, 'err'); return; }
    status.innerHTML = '';

    const hits = (data.hits ?? []).map(provider.normalizeHit);
    if (!hits.length) {
      results.append(h('div', { class: 'empty', style: { padding: '26px 10px', gridColumn: '1 / -1' } },
        icon('puzzle'),
        h('div', { class: 'empty-title' }, 'No results'),
        h('div', { class: 'empty-sub' }, 'Try a different search — or this server type has no plugin or mod ecosystem.')));
      return;
    }
    for (const hit of hits) results.append(hitCard(provider, hit));
  }

  function hitCard(provider, hit) {
    const installBtn = h('button', { class: 'btn sm primary', onclick: () => install(provider, hit, installBtn) }, icon('download'), 'Install');
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

  async function install(provider, hit, btn) {
    btn.disabled = true;
    try {
      const target = await provider.resolveTarget(hit);
      if (!target) { toast(`No file of "${hit.title}" is compatible with this server.`, 'err'); return; }
      await installTarget(provider, target, hit.title);
    } catch (err) { toast(err.message, 'err'); }
    finally { btn.disabled = false; }
  }

  async function installTarget(provider, target, title, includeDependencies = false) {
    const result = await provider.install(target, includeDependencies);

    if (result.needsDependencyConfirmation) {
      const names = result.requiredDependencies.map(depName).join(', ');
      if (await confirmDialog('Required dependencies', `"${title}" requires: ${names}. Install them too?`, { okLabel: 'Install all' })) {
        await installTarget(provider, target, title, true);
      } else {
        toast(`"${title}" needs its required dependencies — not installed.`, 'err');
      }
      return;
    }

    toast(`Installed ${result.installed.map(installName).join(', ')}`);
  }

  buildProviderSeg();
  syncForProvider();
}
