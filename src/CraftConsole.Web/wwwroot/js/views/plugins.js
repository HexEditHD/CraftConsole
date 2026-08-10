// Plugins: Installed (scan the active server's plugins folder, disable jars,
// remove anything installed via Modrinth/CurseForge) and Browse (search
// either provider and install a plugin/mod, with a prompt for required
// dependencies).
import { h, icon, toast, confirmDialog, debounce, modal, timeAgo } from '../ui.js';
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
    // Already loader/game-version filtered and newest-first — listOptions just
    // reshapes each version into what pickVersion needs to render an option
    // and what installTarget needs to install it.
    listOptions: async key => {
      const versions = await api.get(`/api/modrinth/versions?projectId=${encodeURIComponent(key)}`);
      return versions.map(v => ({
        label: v.versionNumber, channel: v.versionType, published: v.datePublished,
        target: { versionId: v.id },
      }));
    },
    install: (target, includeDependencies) => api.post('/api/modrinth/install', { versionId: target.versionId, includeDependencies }),
    updates: () => api.get('/api/modrinth/updates'),
    updateTarget: (installed, update) => ({ versionId: update.latestVersionId }),
    updateLabel: update => update.latestVersionNumber,
    currentLabel: installed => installed.versionNumber,
  },
  curseforge: {
    label: 'CurseForge',
    placeholder: 'Search CurseForge…',
    available: () => !!state.settings?.hasCurseForgeApiKey,
    unavailableReason: 'Add a CurseForge API key in Settings → Integrations & about to browse CurseForge.',
    search: query => api.get(`/api/curseforge/search?query=${encodeURIComponent(query)}&limit=24`),
    normalizeHit: h => ({ key: h.modId, title: h.name, author: h.author, downloads: h.downloads, iconUrl: h.iconUrl, description: h.summary }),
    listOptions: async key => {
      const files = await api.get(`/api/curseforge/files?modId=${encodeURIComponent(key)}`);
      return files.map(f => ({
        label: f.displayName || f.fileName, channel: f.releaseType, published: f.fileDate,
        target: { modId: key, fileId: f.id },
      }));
    },
    install: (target, includeDependencies) => api.post('/api/curseforge/install', { modId: target.modId, fileId: target.fileId, includeDependencies }),
    updates: () => api.get('/api/curseforge/updates'),
    updateTarget: (installed, update) => ({ modId: installed.modId, fileId: update.latestFileId }),
    updateLabel: update => update.latestDisplayName,
    currentLabel: installed => installed.displayName || installed.fileName,
  },
};

const depName = d => d.projectTitle ?? d.modName;
const installName = i => i.projectTitle ?? i.modName;

// ── Version picker ───────────────────────────────────────────────────────
// Module scope, not renderBrowseTab-local: the Installed tab's Update action
// (Check-for-updates) reuses installTarget too, and neither this nor
// pickVersion closes over anything from that view.
function optionLabel(o) {
  const channel = o.channel ? `${o.channel}, ` : '';
  return `${o.label}  —  ${channel}${timeAgo(o.published)}`;
}

/** Resolves to the chosen {versionId} / {modId, fileId} target, or null if cancelled. */
async function pickVersion(provider, key, title) {
  let options;
  try { options = await provider.listOptions(key); }
  catch (err) { toast(err.message, 'err'); return null; }
  if (!options.length) { toast(`No file of "${title}" is compatible with this server.`, 'err'); return null; }

  const running = ['Running', 'Starting'].includes(state.status?.status ?? 'Stopped');

  return new Promise(resolve => {
    const select = h('select', { class: 'select' },
      options.map((o, i) => h('option', { value: String(i) }, optionLabel(o))));
    const channelTag = h('span', { class: 'tag warn', style: { display: 'none' } });

    const syncChannelTag = () => {
      const channel = options[Number(select.value)].channel;
      if (channel && channel !== 'release') {
        channelTag.textContent = channel;
        channelTag.style.display = '';
      } else {
        channelTag.style.display = 'none';
      }
    };
    select.addEventListener('change', syncChannelTag);
    syncChannelTag();

    let picked = null;
    modal({
      title: `Install "${title}"`,
      body: h('div', {},
        running
          ? h('div', { class: 'banner warn inline', style: { margin: '0 0 14px' } },
              icon('alert'),
              h('span', {},
                'The server is running. The new file loads on the next restart, and on Windows the old one may be locked until then.'))
          : null,
        h('div', { class: 'field' },
          h('label', {}, 'Version'),
          h('div', { style: { display: 'flex', gap: '6px', alignItems: 'center' } }, select, channelTag),
          h('span', { class: 'hint' },
            "Newest first. Only versions matching this server's loader and Minecraft version are listed."))),
      onClose: () => resolve(picked),
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        { label: 'Install', kind: 'primary', onClick: () => { picked = options[Number(select.value)].target; } },
      ],
    });
  });
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

  const running = ['Running', 'Starting'].includes(state.status?.status ?? 'Stopped');
  toast(`Installed ${result.installed.map(installName).join(', ')}${running ? ' — restart the server to load it' : ''}`);
  for (const warning of result.warnings ?? []) toast(warning, 'err', 6000);
}

// ── Installed ────────────────────────────────────────────────────────────
function renderInstalledTab(container) {
  // Keyed by projectId / modId — populated by checkUpdates(), read by
  // trackedTable() when it (re)renders. Absent means "never checked", not
  // "up to date", so a fresh install shows no status until Check for
  // updates has actually run.
  const modrinthUpdates = new Map();
  const curseForgeUpdates = new Map();

  const modrinthList = h('div');
  const curseForgeList = h('div');

  const checkBtnLabel = h('span', {}, 'Check for updates');
  const checkBtn = h('button', { class: 'btn sm', onclick: checkUpdates }, icon('refresh'), checkBtnLabel);

  container.append(
    h('div', { class: 'view-head' },
      h('span', { class: 'sub' }, 'Mods and plugins CraftConsole installed for this server.'),
      h('span', { class: 'spacer' }),
      checkBtn),
    h('div', { class: 'card' }, h('div', { class: 'card-title' }, 'Installed via Modrinth'), modrinthList),
    h('div', { class: 'card' }, h('div', { class: 'card-title' }, 'Installed via CurseForge'), curseForgeList));

  async function checkUpdates() {
    checkBtn.disabled = true;
    checkBtn.firstChild.replaceWith(h('span', { class: 'spinner' }));
    checkBtnLabel.textContent = 'Checking…';

    const [m, c] = await Promise.allSettled([PROVIDERS.modrinth.updates(), PROVIDERS.curseforge.updates()]);

    modrinthUpdates.clear();
    curseForgeUpdates.clear();
    let available = 0;
    if (m.status === 'fulfilled') {
      for (const u of m.value) { modrinthUpdates.set(u.projectId, u); if (u.updateAvailable) available++; }
    } else toast(m.reason.message, 'err');
    if (c.status === 'fulfilled') {
      for (const u of c.value) { curseForgeUpdates.set(u.modId, u); if (u.updateAvailable) available++; }
    } else toast(c.reason.message, 'err');

    checkBtn.disabled = false;
    checkBtn.firstChild.replaceWith(icon('refresh'));
    checkBtnLabel.textContent = 'Check for updates';

    toast(available === 0 ? 'Everything is up to date' : `${available} update${available === 1 ? '' : 's'} available`);
    loadModrinthList();
    loadCurseForgeList();
  }

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

  function trackedTable(items, { nameKey, idKey, apiPrefix, providerKey, currentLabel, updates, emptyMessage }) {
    if (!items.length) return h('div', { class: 'empty-sub' }, emptyMessage);
    const provider = PROVIDERS[providerKey];

    function statusTag(status) {
      if (!status) return null; // never checked — no noise before Check for updates runs
      if (status.unavailable) return h('span', { class: 'tag bad', title: status.unavailable }, 'Check failed');
      // "Newer:", never "Out of date" — the version picker makes pinning an
      // older version deliberate, so nagging about a downgrade would be wrong.
      if (status.updateAvailable) return h('span', { class: 'tag warn' }, `Newer: ${provider.updateLabel(status)}`);
      return h('span', { class: 'tag ok' }, 'Up to date');
    }

    return h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, 'Name'), h('th', {}, 'Installed'), h('th', {}, 'File'), h('th', {}, 'Newest'), h('th', {}))),
      h('tbody', {}, items.map(i => {
        const status = updates.get(i[idKey]);
        return h('tr', {},
          h('td', { style: { fontWeight: 600 } }, i[nameKey]),
          h('td', {}, h('span', { class: 'tag' }, currentLabel(i))),
          h('td', { class: 'dim small mono' }, i.fileName),
          h('td', {}, statusTag(status)),
          h('td', {}, h('div', { class: 'actions' },
            status?.updateAvailable ? h('button', {
              class: 'btn sm primary', title: `Install ${provider.updateLabel(status)}`,
              onclick: async () => {
                if (!await confirmDialog('Update', `Update ${i[nameKey]} from ${currentLabel(i)} to ${provider.updateLabel(status)}?`, { okLabel: 'Update' })) return;
                try {
                  await installTarget(provider, provider.updateTarget(i, status), i[nameKey]);
                  // The check result now describes a version that's no longer
                  // installed — clear it rather than leave a stale "Newer:"
                  // claim about the file just installed. Back to blank until
                  // the next explicit check, same as a fresh install.
                  updates.delete(i[idKey]);
                  loadModrinthList();
                  loadCurseForgeList();
                } catch (err) { toast(err.message, 'err'); }
              },
            }, icon('arrowUp'), 'Update') : null,
            h('button', {
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
            }, icon('trash')))));
      }))));
  }

  async function loadModrinthList() {
    modrinthList.innerHTML = '';
    modrinthList.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Loading…'));
    let items;
    try { items = await api.get('/api/modrinth/installed'); }
    catch { items = []; }
    modrinthList.innerHTML = '';
    modrinthList.append(trackedTable(items, {
      nameKey: 'projectTitle', idKey: 'projectId', apiPrefix: 'modrinth', providerKey: 'modrinth',
      currentLabel: PROVIDERS.modrinth.currentLabel, updates: modrinthUpdates,
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
      nameKey: 'modName', idKey: 'modId', apiPrefix: 'curseforge', providerKey: 'curseforge',
      currentLabel: PROVIDERS.curseforge.currentLabel, updates: curseForgeUpdates,
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
    const installBtn = h('button', { class: 'btn sm primary', onclick: () => install(provider, hit, installBtn) }, icon('download'), 'Install…');
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
      const target = await pickVersion(provider, hit.key, hit.title);
      if (!target) return;
      await installTarget(provider, target, hit.title);
    } catch (err) { toast(err.message, 'err'); }
    finally { btn.disabled = false; }
  }

  buildProviderSeg();
  syncForProvider();
}
