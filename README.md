# CraftConsole

A web control panel for Minecraft servers. It runs on the machine hosting your server,
listens on localhost, and you drive it from a browser.

Launch a server or attach to one already running, then manage it end to end — console,
players, files, plugins, backups and scheduled tasks — all from one password-protected panel.

---

## Features

- **Server control** — profiles in Managed or Remote (RCON) mode, start/stop/restart, a
  guided server-JAR download (Vanilla, Paper, Purpur, Fabric and NeoForge auto-download with a
  version picker; Spigot and classic Forge get a direct link and copy-pasteable install
  instructions instead, since neither can be redistributed/automated), and Java runtime
  detection with a real install — one click and a UAC prompt on Windows, copy-pasteable `apt`
  commands on Linux.
- **Live console** — streamed output with level filtering and search, coloured chat names, and
  a command bar with `/`-autocomplete and history.
- **Players and moderation** — an online roster with IP, geolocation and join time; kick, ban
  and ban-IP with an optional reason; banned-player and banned-IP lists with pardon; a
  whitelist with an enforcement toggle.
- **Files and plugins** — a config editor jailed to the server's own directory (allowlisted
  extensions, 2 MB cap), and a plugin browser that reads each jar's `plugin.yml` and can
  disable one without deleting it.
- **Automation** — backup jobs with on-demand or scheduled runs and restore into a chosen
  directory, and a scheduler with interval/daily/player-join/server-ready triggers and
  command/broadcast/restart/run-backup actions. Both backup jobs and tasks can be disabled
  without deleting them.
- **Diagnostics** — a live dashboard (machine and process CPU/RAM, uptime, player count) and an
  issues feed that distills warnings and errors out of the console automatically.
- **Security** — multi-user accounts with Admin/Operator roles (PBKDF2-SHA256, server-held
  sessions), a per-IP lockout after repeated failures, loopback-only first-run setup, and RCON
  passwords encrypted at rest.

A connection that can't do something — RCON has no filesystem or log stream, for example —
says so explicitly rather than failing silently or showing an empty list. See
[Attaching to a server via RCON](#attaching-to-a-server-via-rcon) below.

---

## Install

### Windows

Download `CraftConsole-<version>-win-x64.exe` from the
[latest release](https://github.com/HexEditHD/CraftConsole/releases) and run it.

It is a single self-contained file — no .NET runtime, no installer, nothing beside it.
Your browser opens at `https://127.0.0.1:5178` — the panel generates itself a self-signed
certificate on first run, so the browser will show a one-time warning; that's expected (see
[TLS](#tls) below).

### Debian / Ubuntu

Download the `.deb` for your architecture (`amd64` or `arm64`), then:

```bash
sudo apt install ./craftconsole_<version>_amd64.deb
```

That installs a systemd service running as a dedicated `craftconsole` user, listening on
`https://127.0.0.1:5178` (self-signed by default — see [TLS](#tls) below), with its data in
`/var/lib/craftconsole`. The .NET runtime is bundled, so Microsoft's apt repository is not
required.

```bash
sudo systemctl status craftconsole
sudo journalctl -u craftconsole -f
```

`apt remove` keeps your profiles, password and backup definitions; `apt purge` deletes them.

### From source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) version in `global.json`.

```bash
dotnet run --project src/CraftConsole.Web
```

---

## First run

You will be asked to set a password before anything else is reachable. It creates a single
`admin` account, hashed with PBKDF2-SHA256 and stored on the machine only — **there is no
recovery**. To reset it, delete `auth.json` from the data directory and restart.

Additional accounts can be added under **Settings → Users**, each with one of two roles:

| Role | Can do |
|---|---|
| **Admin** | Everything — profiles, downloads, settings, TLS, backup/task management, other users |
| **Operator** | Day-to-day operation — console, players, moderation, start/stop/restart, run existing backups |

At least one enabled Admin always exists; the panel refuses to delete, disable, or demote the
last one. Upgrading from a version before roles existed converts the existing password into that
first Admin account automatically — same password, username `admin`.

Then open **Server** to create a profile: point it at a server JAR (or download one — Vanilla,
Paper and Purpur can be fetched directly), pick a Java runtime (or get one — Windows installs it
for you after one UAC prompt; Linux gets a ready-to-run `apt` command instead, since the panel
runs unprivileged there by design), set memory limits, and start.

If this is a brand new server, Mojang requires accepting the EULA. CraftConsole detects the
prompt and shows a banner with an Accept button; start the server again afterwards.

---

## Attaching to a server via RCON

If the server is already running — on this machine or another — CraftConsole doesn't have to
have started it. Create a profile, switch its mode from **Managed** to **Remote**, and give it
the host, RCON port and password from the server's `server.properties`
(`enable-rcon=true`, `rcon.port`, `rcon.password`).

RCON gives commands and their replies, and nothing else — no log stream, no filesystem, no
process to restart. The panel is upfront about this: the console shows a transcript rather
than a live log, the config editor and plugin list explain why they're empty, and controls
that don't apply (Restart, the config editor, IP bans) are disabled with a reason rather than
just failing when clicked. The online player list, moderation, and the whitelist still work —
those are just commands.

> RCON has no encryption of its own — the password and everything sent over it crosses the
> network in the clear regardless of the panel's own TLS setting (see [TLS](#tls) below, which
> only covers the browser↔panel hop). Only point it at a server on a network you trust.

---

## TLS

The panel serves HTTPS by default — on first run it generates itself a self-signed certificate
(RSA 2048, valid for `localhost`, `127.0.0.1`, `::1` and the machine's hostname). Browsers warn
once about the untrusted issuer; that's expected for a self-signed cert and safe to accept for a
panel you host yourself, the same way router admin pages and tools like Proxmox or TrueNAS work.

To use your own certificate instead — from Let's Encrypt, an internal CA, wherever — open
**Settings → TLS certificate** and upload the certificate and private key as PEM files (the
certificate file can be a full chain: leaf followed by intermediates). It takes effect
immediately, no restart.

For headless/scripted deployments, pin a certificate via configuration instead, which also makes
the Settings upload read-only (so it can't be silently overridden by a restart):

```bash
--cert-path /path/to/cert.pfx --cert-password <password>      # or CRAFTCONSOLE_CERT_PATH / CRAFTCONSOLE_CERT_PASSWORD
```

To go back to plain HTTP — e.g. behind a reverse proxy that already terminates TLS — pass
`--http` or set `CRAFTCONSOLE_HTTPS=0`.

## Reaching it from another machine

The panel binds to loopback. On a headless server, the simplest option is still to forward the
port over SSH rather than exposing it at all:

```bash
ssh -L 5178:127.0.0.1:5178 you@your-server
```

Then browse to `https://localhost:5178` on your own machine.

You *can* bind it wider instead — a password is required for every request, and TLS is on by
default, so it is not unguarded:

```bash
sudo systemctl edit craftconsole    # Environment=ASPNETCORE_URLS=https://0.0.0.0:5178
```

A self-signed certificate will still trigger a browser warning at the new address too, since its
SAN list is fixed to loopback and the local hostname — upload a certificate that actually covers
the address you're using (see [TLS](#tls) above) if you don't want that.

---

## Where things are kept

Profiles, scheduled tasks, backup definitions, settings, user accounts and their password
hashes, and logs:

| Platform | Location |
|---|---|
| Windows | `%APPDATA%\CraftConsole` |
| Linux (`.deb`) | `/var/lib/craftconsole` |
| Linux / macOS (manual) | `~/.config/CraftConsole` |

Override with `--data-dir <path>` or the `CRAFTCONSOLE_DATA` environment variable.

Logs roll daily under `logs/`, keeping a week.

---

## Command line

| Flag | Effect |
|---|---|
| `--data-dir <path>` | Where to keep data (see above) |
| `--urls <url>` | Address to bind, e.g. `https://0.0.0.0:5178` |
| `--http` | Serve plain HTTP instead of the default self-signed HTTPS |
| `--cert-path <path>` | Pin a PFX certificate instead of the auto-generated one |
| `--cert-password <password>` | Password for `--cert-path`, if the PFX has one |
| `--no-browser` | Do not open a browser at startup |

---

## Development

```bash
dotnet build CraftConsole.slnx      # build everything
dotnet test CraftConsole.slnx       # run the tests
dotnet run --project src/CraftConsole.Web
```

The frontend is plain ES modules and CSS under `src/CraftConsole.Web/wwwroot` — no build
step, no npm. In Development they are served from disk, so a refresh picks up edits. In
published builds they are embedded in the assembly, which is what makes the executable a
single file.

### Layout

| Project | Contents |
|---|---|
| `CraftConsole.Core` | Domain: process management, console and event parsing, models |
| `CraftConsole.Infrastructure` | Persistence, HTTP downloads, logging |
| `CraftConsole.Web` | Host, REST API, SSE stream, frontend |
| `tests/CraftConsole.Tests` | Unit and end-to-end tests |
| `tests/CraftConsole.FakeServer` | Stand-in Minecraft server used by the tests |

### The fake server

Lifecycle tests do not need a JVM or a real server jar. `CraftConsole.FakeServer`
impersonates a Paper server — boot sequence, ready line, join/leave/chat, warnings and
errors — and the tests point a profile's Java path at it, so the real
`ServerProcessManager` launches it exactly as it would launch java.

`CRAFTCONSOLE_FAKE_MODE` selects the scenario: `normal`, `eula`, `crash`, `hang`
(ignores stop, to exercise the kill fallback), `exitcode`, `stderr`.

### Manual smoke test

The automated tests never touch a real Minecraft server. Before releasing, run through this
by hand at least once:

1. Download a Paper or Vanilla jar through **Server → Download**.
2. Create a profile from it and start the server.
3. Accept the EULA banner on first boot, then start again.
4. Confirm the console streams and reaches `Done (…)!`.
5. Join with a real client; check the player appears in **Players** and chat shows in the console.
6. Run a command (`say hello`) and see the reply.
7. Take a backup, stop the server, restore it, and start again.
8. Stop the server and confirm it shuts down cleanly rather than being terminated.
9. Enable RCON in that server's `server.properties`, start it *outside* the panel, and attach
   with a Remote profile. Confirm: authentication; a command and its reply; the player list
   appearing and updating as someone joins and leaves; a kick and a whitelist change taking
   effect; and that the config editor, plugins and Restart all explain themselves rather than
   appearing blank or silently failing.
10. Try a wrong RCON password — it should reject clearly and not appear anywhere in the panel's
    log file.
11. Restart the panel and reconnect the Remote profile without being asked for the password
    again.

---

## Packaging

```bash
packaging/build-deb.sh <version> <linux-x64|linux-arm64> [output-dir]
```

The version argument is a *Debian* version: a prerelease is `1.2.3~rc1`, not `1.2.3-rc1`,
because Debian sorts `~` before the release and `-` after it. The assembly version is
derived from it.

Releases are cut by pushing a tag (`v1.2.3`). CI tests on Windows and Linux first, then
builds the Windows executable and both `.deb`s, installs and purges the amd64 package on a
clean runner, and publishes with `SHA256SUMS`.

---

## Known limitations

- **TLS is self-signed by default.** Real trust needs your own certificate — see [TLS](#tls).
- **RCON connections are unencrypted**, regardless of the panel's own TLS setting — see above.
- **One server at a time.** CraftConsole manages or attaches to a single server; running
  several profiles simultaneously needs several instances of the panel.
- Machine CPU and memory gauges work on Windows and Linux. Other platforms show them as
  unavailable rather than guessing.
- Player geolocation calls `ipinfo.io` without an API key. It is best-effort, rate-limited,
  and sends player IP addresses to a third party.
- **`[Not Secure]` in the console isn't a CraftConsole or TLS warning.** It's the Minecraft
  server's own chat-signing system (since 1.19) prefixing unsigned messages — including `/say`
  and anything else sent from outside a signed client connection. Nothing in the panel produces
  that string; it's the server telling you a message's authenticity wasn't verified.
- **NeoForge downloads need a detected Java runtime first.** Unlike the other auto-download
  types, turning the download into an actually runnable server means running the real NeoForge
  installer in the background (via [ServerStarterJar](https://github.com/neoforged/ServerStarterJar)),
  which itself needs a JVM. Detect or download Java on the Server page before downloading
  NeoForge — the download fails with a clear message if none is found. Classic Forge stays a
  manual download; ServerStarterJar only supports Forge 1.17 and later, and reliably telling
  those versions apart from earlier ones in Forge's version list wasn't worth the risk of
  quietly offering a version that can't actually install.
