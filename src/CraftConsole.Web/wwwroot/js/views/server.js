// Server: profile management plus setup helpers (server JAR + Java downloads).
import { h, icon, toast, confirmDialog, modal } from '../ui.js';
import { api } from '../api.js';
import { on } from '../bus.js';
import { state } from '../store.js';
import { joinPath } from '../platform.js';
import { openPathPicker, withBrowse } from '../components/path-picker.js';

export default {
  id: 'server',
  title: 'Server',
  subtitle: 'Profiles, JARs and Java runtimes',
  icon: 'drives',

  render(el) {
    let profiles = [];
    let activeId = null;
    let javaInstalls = [];
    let serverTypes = [];
    let javaVersions = [];
    let selectedType = null;
    let downloadedJar = null; // {jarPath, version, type}
    let installedJavaPath = null;

    // ── Profiles card ────────────────────────────────────────────────────
    const profileList = h('div', { class: 'grid' });
    const profilesCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' },
        'Server profiles',
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm primary', onclick: () => openProfileEditor(null) }, icon('plus'), 'New profile')),
      profileList);

    // ── Server download card ────────────────────────────────────────────
    const typeGrid = h('div', { class: 'type-grid' });
    const versionSelect = h('select', { class: 'select' }, h('option', { value: '' }, 'Latest'));
    const dirInput = h('input', {
      class: 'input',
      placeholder: joinPath(state.system?.defaultServerRoot ?? 'C:\\MinecraftServers', 'my-server'),
    });
    const dirBrowseBtn = h('button', {
      class: 'btn sm icon-only', title: 'Browse for a folder', 'aria-label': 'Browse for a folder',
      onclick: async () => {
        // The default goes in as defaultPath, not as the start path: the picker
        // treats a start path as something the user chose and honours it, but
        // will step a non-existent default aside in favour of home.
        const picked = await openPathPicker({ startPath: dirInput.value.trim(), defaultPath: state.system?.defaultServerRoot });
        if (picked) dirInput.value = picked;
      },
    }, icon('folder'));
    const dlBtn = h('button', { class: 'btn primary', onclick: startServerDownload }, icon('download'), 'Download JAR');
    const dlCancel = h('button', { class: 'btn sm ghost', style: { display: 'none' }, onclick: () => api.post('/api/setup/cancel/server') }, 'Cancel');
    const dlBar = h('div', { class: 'progress', style: { display: 'none' } }, h('div'));
    const dlMsg = h('span', {}, '');
    const dlStatus = h('div', { class: 'dl-status' }, dlMsg, dlBar, dlCancel);

    const manualCommands = h('pre', {
      class: 'mono', style: {
        whiteSpace: 'pre-wrap', wordBreak: 'break-all', background: 'var(--sheet-3)',
        border: '1px solid var(--rule-firm)', borderRadius: 'var(--r)', padding: 'var(--s2) var(--s3)',
        fontSize: 'var(--t-sm)', marginTop: 'var(--s2)', marginBottom: 'var(--s2)',
      },
    });
    const manualLink = h('a', { target: '_blank', rel: 'noopener noreferrer' }, 'Official website');
    const manualBox = h('div', { style: { display: 'none', marginTop: '12px' } },
      h('div', { class: 'switch-desc' }, 'Automated download not available — get it from the ', manualLink, ':'),
      manualCommands,
      h('button', {
        class: 'btn sm ghost',
        onclick: () => { navigator.clipboard.writeText(manualCommands.textContent); toast('Commands copied'); },
      }, icon('copy'), 'Copy'));

    const downloadCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' }, 'Download a server JAR'),
      typeGrid,
      h('div', { class: 'field-row', style: { marginTop: '14px' } },
        h('div', { class: 'field' }, h('label', {}, 'Version'), versionSelect),
        h('div', { class: 'field', style: { flex: 2 } },
          h('label', {}, 'Destination folder'),
          h('div', { style: { display: 'flex', gap: '6px' } }, dirInput, dirBrowseBtn))),
      h('div', { style: { display: 'flex', gap: '8px', alignItems: 'center' } }, dlBtn),
      dlStatus,
      manualBox);

    // ── Java card ────────────────────────────────────────────────────────
    const javaList = h('div');
    const javaSelect = h('select', { class: 'select', style: { maxWidth: '220px' }, onchange: syncJavaLinuxHint });
    const javaDlBtn = h('button', { class: 'btn', onclick: startJavaDownload }, icon('download'), 'Download');
    const javaCancel = h('button', { class: 'btn sm ghost', style: { display: 'none' }, onclick: () => api.post('/api/setup/cancel/java') }, 'Cancel');
    const javaBar = h('div', { class: 'progress', style: { display: 'none' } }, h('div'));
    const javaMsg = h('span', {}, '');
    const javaStatus = h('div', { class: 'dl-status' }, javaMsg, javaBar, javaCancel);

    const javaLinuxCommands = h('pre', {
      class: 'mono', style: {
        whiteSpace: 'pre-wrap', wordBreak: 'break-all', background: 'var(--sheet-3)',
        border: '1px solid var(--rule-firm)', borderRadius: 'var(--r)', padding: 'var(--s2) var(--s3)',
        fontSize: 'var(--t-sm)', marginTop: 'var(--s2)', marginBottom: 'var(--s2)',
      },
    });
    const javaLinuxHint = h('div', { style: { display: 'none', marginTop: '12px' } },
      h('div', { class: 'switch-desc' }, 'Or install it system-wide instead of downloading it here:'),
      javaLinuxCommands,
      h('button', {
        class: 'btn sm ghost',
        onclick: () => { navigator.clipboard.writeText(javaLinuxCommands.textContent); toast('Commands copied'); },
      }, icon('copy'), 'Copy'));

    const javaCard = h('div', { class: 'card' },
      h('div', { class: 'card-title' },
        'Java runtimes',
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm ghost', onclick: detectJava }, icon('refresh'), 'Re-scan')),
      javaList,
      h('div', { style: { display: 'flex', gap: '8px', marginTop: '12px', alignItems: 'center' } },
        javaSelect, javaDlBtn),
      javaStatus,
      javaLinuxHint);

    el.append(h('div', { class: 'setup-split' },
      profilesCard,
      h('div', { class: 'grid' }, downloadCard, javaCard)));

    // ── Profiles ─────────────────────────────────────────────────────────
    async function loadProfiles() {
      try {
        const data = await api.get('/api/profiles');
        profiles = data.profiles ?? [];
        activeId = data.activeProfileId;
      } catch { profiles = []; }
      buildProfileList();
    }

    function buildProfileList() {
      profileList.innerHTML = '';
      if (!profiles.length) {
        profileList.append(h('div', { class: 'empty' },
          icon('hardDrives'),
          h('div', { class: 'empty-title' }, 'No profiles yet'),
          h('div', { class: 'empty-sub' }, 'A profile bundles a server JAR, its folder, Java, and memory settings. Create one, or download a JAR below to get started.')));
        return;
      }
      for (const p of profiles) {
        const isActive = activeId === p.id;
        const isRcon = p.mode === 'Rcon';
        const busy = ['Running', 'Starting', 'Stopping'].includes(state.status?.status);
        const status = state.status?.status ?? 'Stopped';
        profileList.append(h('div', { class: `card profile-card${isActive ? ' active' : ''}`, style: { padding: '13px 16px' } },
          h('div', { class: 'info' },
            h('div', { class: 'name' },
              p.name,
              isRcon ? h('span', { class: 'tag' }, 'RCON') : h('span', { class: 'tag' }, p.type),
              !isRcon && p.minecraftVersion ? h('span', { class: 'tag' }, p.minecraftVersion) : null,
              isActive ? h('span', { class: 'tag live' }, 'ACTIVE') : null),
            h('div', { class: 'meta' }, isRcon
              ? `${p.rconHost}:${p.rconPort}`
              : `${p.minRamMb}–${p.maxRamMb} MB · ${p.workingDirectory}`)),
          h('div', { class: 'actions' },
            !isActive ? h('button', {
              class: 'btn sm', title: 'Make this the active profile without starting it',
              onclick: () => activate(p.id),
            }, 'Set active') : null,
            isActive
              ? h('span', { class: `status-pill ${status.toLowerCase()}` }, status)
              : h('button', {
                  class: 'btn sm primary',
                  title: busy
                    ? 'Another server is running — stop it before switching.'
                    : (isRcon ? 'Switch to this profile and connect' : 'Switch to this profile and start it'),
                  onclick: () => start(p.id),
                  disabled: busy,
                }, icon('play'), isRcon ? 'Switch & connect' : 'Switch & start'),
            h('button', { class: 'btn sm icon-only', title: 'Edit', 'aria-label': `Edit “${p.name}”`, onclick: () => openProfileEditor(p) }, icon('pencilSimple')),
            h('button', {
              class: 'btn sm icon-only danger', title: 'Delete', 'aria-label': `Delete “${p.name}”`,
              onclick: async () => {
                if (!await confirmDialog('Delete profile', `Delete profile “${p.name}”? Server files on disk are not touched.`, { danger: true, okLabel: 'Delete' })) return;
                await api.del(`/api/profiles/${p.id}`);
                loadProfiles();
              },
            }, icon('trash')))));
      }
    }

    async function activate(id) {
      await api.post(`/api/profiles/${id}/activate`);
      toast('Active profile changed');
      loadProfiles();
    }

    async function start(id) {
      try {
        await api.post('/api/server/start', { profileId: id });
        toast('Server starting…');
        location.hash = '#/console';
      } catch (err) { toast(err.message, 'err'); }
    }

    function openProfileEditor(profile) {
      const isNew = !profile;
      const seed = profile ?? {
        mode: 'Managed',
        name: 'My Server',
        jarPath: downloadedJar?.jarPath ?? '',
        workingDirectory: downloadedJar ? dirname(downloadedJar.jarPath) : '',
        javaPath: installedJavaPath ?? javaInstalls[0]?.executablePath ?? 'java',
        minRamMb: 512, maxRamMb: 2048,
        minecraftVersion: downloadedJar?.version ?? '',
        jvmArguments: '',
        type: downloadedJar?.type ?? 'Paper',
        rconHost: '', rconPort: 25575,
      };

      const f = {
        mode: h('select', { class: 'select' },
          h('option', { value: 'Managed', selected: seed.mode !== 'Rcon' }, 'Managed — the panel launches the server'),
          h('option', { value: 'Rcon', selected: seed.mode === 'Rcon' }, 'Remote — attach via RCON')),
        name: h('input', { class: 'input', value: seed.name }),
        jar: h('input', { class: 'input', value: seed.jarPath, placeholder: joinPath('…', 'paper-1.21.4.jar') }),
        dir: h('input', { class: 'input', value: seed.workingDirectory, placeholder: 'Defaults to the JAR’s folder' }),
        java: h('select', { class: 'select' },
          javaInstalls.map(j => h('option', { value: j.executablePath, selected: j.executablePath === seed.javaPath }, j.label)),
          h('option', { value: '__custom', selected: !javaInstalls.some(j => j.executablePath === seed.javaPath) }, 'Custom path…')),
        javaCustom: h('input', { class: 'input', value: seed.javaPath, placeholder: 'java', style: { marginTop: '6px' } }),
        minRam: h('input', { class: 'input', type: 'number', min: 256, step: 256, value: seed.minRamMb }),
        maxRam: h('input', { class: 'input', type: 'number', min: 512, step: 256, value: seed.maxRamMb }),
        version: h('input', { class: 'input', value: seed.minecraftVersion, placeholder: 'e.g. 1.21.4' }),
        jvm: h('input', { class: 'input', value: seed.jvmArguments, placeholder: '-XX:+UseG1GC …' }),
        type: h('select', { class: 'select' },
          serverTypes.map(t => h('option', { value: t.type, selected: t.type === seed.type }, t.displayName))),
        rconHost: h('input', { class: 'input', value: seed.rconHost ?? '', placeholder: '127.0.0.1' }),
        rconPort: h('input', { class: 'input', type: 'number', min: 1, max: 65535, value: seed.rconPort ?? 25575 }),
        rconPassword: h('input', {
          class: 'input', type: 'password', autocomplete: 'new-password',
          placeholder: !isNew && profile.hasRconPassword ? 'Unchanged — leave blank to keep it' : 'Password',
        }),
      };

      // The custom-path row is the input *and* its Browse button, so the whole
      // row has to hide together — toggling the input alone would strand the
      // button next to the Java dropdown.
      // No extension filter on it: the launcher is java.exe on Windows and an
      // extensionless binary on Linux, so there is nothing common to match on.
      const javaCustomRow = withBrowse(f.javaCustom, { mode: 'file', browseLabel: 'Browse for a Java executable' });
      javaCustomRow.style.marginTop = '6px';

      const syncCustom = () => { javaCustomRow.style.display = f.java.value === '__custom' ? '' : 'none'; };
      f.java.addEventListener('change', syncCustom);
      syncCustom();

      f.jar.addEventListener('change', () => {
        if (!f.dir.value && f.jar.value) f.dir.value = dirname(f.jar.value);
      });

      const managedFields = h('div', {},
        h('div', { class: 'field-row' },
          h('div', { class: 'field', style: { flex: 2 } }, h('label', {}, 'Server JAR path'),
            withBrowse(f.jar, { mode: 'file', ext: '.jar', defaultPath: state.system?.defaultServerRoot })),
          h('div', { class: 'field' }, h('label', {}, 'Type'), f.type)),
        h('div', { class: 'field' }, h('label', {}, 'Working directory'),
          withBrowse(f.dir, { defaultPath: state.system?.defaultServerRoot })),
        h('div', { class: 'field' }, h('label', {}, 'Java runtime'), f.java, javaCustomRow),
        h('div', { class: 'field-row' },
          h('div', { class: 'field' }, h('label', {}, 'Min RAM (MB)'), f.minRam),
          h('div', { class: 'field' }, h('label', {}, 'Max RAM (MB)'), f.maxRam),
          h('div', { class: 'field' }, h('label', {}, 'Minecraft version'), f.version)),
        h('div', { class: 'field' }, h('label', {}, 'Extra JVM arguments'), f.jvm));

      const rconFields = h('div', {},
        h('div', { class: 'field-row' },
          h('div', { class: 'field', style: { flex: 2 } }, h('label', {}, 'Host or IP address'), f.rconHost),
          h('div', { class: 'field' }, h('label', {}, 'RCON port'), f.rconPort)),
        h('div', { class: 'field' },
          h('label', {}, 'RCON password'), f.rconPassword,
          h('span', { class: 'hint' }, !isNew && profile.hasRconPassword
            ? 'A password is already set on the server — leave this blank to keep it.'
            : 'Must match rcon.password in the remote server’s server.properties.')),
        h('div', { class: 'field' },
          h('span', { class: 'hint' },
            'The remote server needs enable-rcon=true and a matching rcon.port. RCON sends its password in plain text, so only connect over a trusted network.')));

      const syncMode = () => {
        const isRcon = f.mode.value === 'Rcon';
        managedFields.style.display = isRcon ? 'none' : '';
        rconFields.style.display = isRcon ? '' : 'none';
      };
      f.mode.addEventListener('change', syncMode);
      syncMode();

      modal({
        title: isNew ? 'New server profile' : `Edit “${profile.name}”`,
        wide: true,
        body: h('div', {},
          h('div', { class: 'field-row' },
            h('div', { class: 'field', style: { flex: 2 } }, h('label', {}, 'Name'), f.name),
            h('div', { class: 'field' }, h('label', {}, 'Connection'), f.mode)),
          managedFields,
          rconFields),
        actions: [
          { label: 'Cancel', kind: 'ghost' },
          {
            label: isNew ? 'Create profile' : 'Save changes',
            kind: 'primary',
            onClick: () => {
              const mode = f.mode.value;
              const body = {
                mode,
                name: f.name.value.trim() || 'My Server',
                jarPath: f.jar.value.trim(),
                workingDirectory: f.dir.value.trim() || dirname(f.jar.value.trim()),
                javaPath: (f.java.value === '__custom' ? f.javaCustom.value.trim() : f.java.value) || 'java',
                minRamMb: parseInt(f.minRam.value, 10) || 512,
                maxRamMb: parseInt(f.maxRam.value, 10) || 2048,
                minecraftVersion: f.version.value.trim(),
                jvmArguments: f.jvm.value.trim(),
                type: f.type.value,
                rconHost: f.rconHost.value.trim(),
                rconPort: parseInt(f.rconPort.value, 10) || 25575,
              };
              if (mode === 'Managed' && !body.jarPath) { toast('A server JAR path is required.', 'err'); return false; }
              if (mode === 'Rcon' && !body.rconHost) { toast('A host or IP address is required.', 'err'); return false; }

              const password = f.rconPassword.value;
              const req = isNew
                ? api.post('/api/profiles', body)
                : api.put(`/api/profiles/${profile.id}`, body);
              // Returned so modal() awaits the whole chain — the RCON password
              // is a second request that only runs after the profile itself is
              // saved, and without the return the dialog closed before it had
              // even been sent, taking the typed password with it.
              return req.then(async created => {
                if (mode === 'Rcon' && password) {
                  const id = isNew ? created.id : profile.id;
                  await api.put(`/api/profiles/${id}/rcon-password`, { password });
                }
                toast(isNew ? 'Profile created' : 'Profile saved');
                loadProfiles();
              }).catch(err => { toast(err.message, 'err'); return false; });
            },
          },
        ],
      });
    }

    // ── Server download ──────────────────────────────────────────────────
    async function loadTypes() {
      try { serverTypes = await api.get('/api/setup/server/types'); }
      catch { serverTypes = []; }
      selectedType = serverTypes.find(t => t.type === 'Paper') ?? serverTypes[0] ?? null;
      buildTypeGrid();
      loadVersions();
    }

    function buildTypeGrid() {
      typeGrid.innerHTML = '';
      for (const t of serverTypes) {
        typeGrid.append(h('button', {
          class: `type-card${selectedType?.type === t.type ? ' selected' : ''}`,
          onclick: () => { selectedType = t; buildTypeGrid(); loadVersions(); },
        },
          h('div', { class: 'type-name' },
            t.displayName,
            h('span', { class: `tag ${t.tag === 'RECOMMENDED' ? 'live' : ''}` }, t.tag)),
          h('div', { class: 'type-desc' }, t.description)));
      }
      const manual = selectedType && !selectedType.hasAutoDownload;
      dlBtn.disabled = manual;
      dlMsg.textContent = '';
      if (manual && selectedType.downloadUrl) {
        manualLink.href = selectedType.downloadUrl;
        manualCommands.textContent = selectedType.manualInstructions ?? '';
        manualBox.style.display = '';
      } else {
        manualBox.style.display = 'none';
        dlMsg.textContent = manual
          ? 'Automated download not available for this type — get it from the official website.'
          : '';
      }
    }

    async function loadVersions() {
      versionSelect.innerHTML = '';
      versionSelect.append(h('option', { value: '' }, 'Latest'));
      if (!selectedType?.hasAutoDownload) return;
      try {
        const versions = await api.get(`/api/setup/server/versions?type=${selectedType.type}`);
        for (const v of versions) versionSelect.append(h('option', { value: v }, v));
      } catch (err) { toast(err.message, 'err'); }
    }

    async function startServerDownload() {
      if (!selectedType) return;
      const directory = dirInput.value.trim();
      if (!directory) { toast('Choose a destination folder first.', 'err'); return; }
      try {
        await api.post('/api/setup/server/download', {
          type: selectedType.type,
          version: versionSelect.value || null,
          directory,
        });
        dlBtn.disabled = true;
      } catch (err) { toast(err.message, 'err'); }
    }

    // ── Java ─────────────────────────────────────────────────────────────
    async function detectJava() {
      javaList.innerHTML = '';
      javaList.append(h('div', { class: 'dl-status' }, h('span', { class: 'spinner' }), 'Scanning for Java installs…'));
      try { javaInstalls = await api.get('/api/setup/java/detect'); }
      catch { javaInstalls = []; }
      javaList.innerHTML = '';
      if (!javaInstalls.length) {
        javaList.append(h('div', { class: 'empty-sub', style: { color: 'var(--ink-low)', fontSize: 'var(--t-sm)' } },
          'No Java runtimes found. Download one below — Minecraft 1.21+ needs Java 21.'));
        return;
      }
      for (const j of javaInstalls) {
        javaList.append(h('div', { class: 'switch-row' },
          h('div', {},
            h('div', { class: 'switch-label' }, j.label),
            h('div', { class: 'switch-desc mono' }, j.executablePath)),
          h('span', { class: 'tag live' }, `Java ${j.majorVersion}`)));
      }
    }

    async function loadJavaVersions() {
      try {
        const data = await api.get('/api/setup/java/versions');
        javaVersions = data.versions ?? [];
      } catch { javaVersions = []; }
      javaSelect.innerHTML = '';
      for (const v of javaVersions)
        javaSelect.append(h('option', { value: v.major }, v.displayName));
      syncJavaLinuxHint();
    }

    // Debian/Ubuntu only — matches what this project actually packages and ships.
    function syncJavaLinuxHint() {
      const major = parseInt(javaSelect.value, 10) || javaVersions[0]?.major;
      if (state.system?.platform !== 'linux' || !major) { javaLinuxHint.style.display = 'none'; return; }
      javaLinuxHint.style.display = '';
      javaLinuxCommands.textContent =
        'sudo apt-get install -y wget apt-transport-https gpg\n' +
        'wget -qO - https://packages.adoptium.net/artifactory/api/gpg/key/public | sudo gpg --dearmor -o /etc/apt/trusted.gpg.d/adoptium.gpg\n' +
        'echo "deb https://packages.adoptium.net/artifactory/deb $(awk -F= \'/^VERSION_CODENAME/{print$2}\' /etc/os-release) main" | sudo tee /etc/apt/sources.list.d/adoptium.list\n' +
        'sudo apt-get update\n' +
        `sudo apt-get install -y temurin-${major}-jdk\n\n` +
        "Not on Debian/Ubuntu? See Adoptium's install docs for your distro.";
    }

    async function startJavaDownload() {
      const major = parseInt(javaSelect.value, 10);
      if (!major) return;
      try {
        await api.post('/api/setup/java/download', { major });
        javaDlBtn.disabled = true;
      } catch (err) { toast(err.message, 'err'); }
    }

    // ── SSE: download progress ───────────────────────────────────────────
    const offSetup = on('setup', p => {
      const isServer = p.kind === 'server';
      const msg = isServer ? dlMsg : javaMsg;
      const bar = isServer ? dlBar : javaBar;
      const btn = isServer ? dlBtn : javaDlBtn;
      const cancel = isServer ? dlCancel : javaCancel;

      msg.textContent = p.message;
      if (p.phase === 'downloading' || p.phase === 'resolving' || p.phase === 'installing') {
        bar.style.display = '';
        bar.firstChild.style.width = `${Math.round(p.progress * 100)}%`;
        cancel.style.display = p.phase === 'installing' ? 'none' : ''; // already elevating/installing — too late to cancel
        btn.disabled = true;
      } else {
        bar.style.display = 'none';
        cancel.style.display = 'none';
        btn.disabled = isServer && !selectedType?.hasAutoDownload;

        if (p.phase === 'done') {
          toast(p.message);
          if (isServer && p.extra?.jarPath) {
            downloadedJar = { jarPath: p.extra.jarPath, version: p.extra.version, type: selectedType?.type };
            msg.textContent = '';
            dlStatus.prepend(h('button', {
              class: 'btn sm primary',
              onclick: function () { this.remove(); openProfileEditor(null); },
            }, icon('plus'), `Create profile from ${p.extra.version}`));
          }
          if (!isServer) {
            if (p.extra?.javaPath) {
              installedJavaPath = p.extra.javaPath;
              msg.textContent = '';
              javaStatus.prepend(h('button', {
                class: 'btn sm primary',
                onclick: function () { this.remove(); openProfileEditor(null); },
              }, icon('plus'), 'Use this Java for a profile'));
            }
            detectJava();
          }
        }
        if (p.phase === 'error') toast(p.message, 'err');
      }
    });

    const offStatus = on('store:status', buildProfileList);

    loadProfiles();
    loadTypes();
    detectJava();
    loadJavaVersions();

    return () => { offSetup(); offStatus(); };
  },
};

function dirname(path) {
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return i > 0 ? path.slice(0, i) : '';
}
