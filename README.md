# CraftConsole

A web control panel for Minecraft servers. It runs on the machine hosting your server,
listens on localhost, and you drive it from a browser.

Start and stop the server, watch its console live, run commands, manage players and
plugins, edit configuration files, schedule tasks, and take and restore backups.

---

## Install

### Windows

Download `CraftConsole-<version>-win-x64.exe` from the
[latest release](https://github.com/HexEditHD/CraftConsole/releases) and run it.

It is a single self-contained file — no .NET runtime, no installer, nothing beside it.
Your browser opens at `http://127.0.0.1:5178`.

### Debian / Ubuntu

Download the `.deb` for your architecture (`amd64` or `arm64`), then:

```bash
sudo apt install ./craftconsole_<version>_amd64.deb
```

That installs a systemd service running as a dedicated `craftconsole` user, listening on
`http://127.0.0.1:5178`, with its data in `/var/lib/craftconsole`. The .NET runtime is
bundled, so Microsoft's apt repository is not required.

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

You will be asked to set a password before anything else is reachable. It is hashed with
PBKDF2-SHA256 and stored on the machine only — **there is no recovery**. To reset it,
delete `auth.json` from the data directory and restart.

Then open **Server** to create a profile: point it at a server JAR (or download one — Vanilla,
Paper and Purpur can be fetched directly), pick a Java runtime, set memory limits, and start.

If this is a brand new server, Mojang requires accepting the EULA. CraftConsole detects the
prompt and shows a banner with an Accept button; start the server again afterwards.

---

## Reaching it from another machine

The panel binds to loopback. On a headless server, forward the port over SSH:

```bash
ssh -L 5178:127.0.0.1:5178 you@your-server
```

Then browse to `http://localhost:5178` on your own machine.

You *can* bind it wider — a password is required for every request, so it is not unguarded:

```bash
sudo systemctl edit craftconsole    # Environment=ASPNETCORE_URLS=http://0.0.0.0:5178
```

> **There is no TLS.** The password and everything you do crosses the network in the clear.
> Only do this on a network you trust, or put a reverse proxy with HTTPS in front of it.

---

## Where things are kept

Profiles, scheduled tasks, backup definitions, settings, the password hash and logs:

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
| `--urls <url>` | Address to bind, e.g. `http://0.0.0.0:5178` |
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

- **No TLS.** See above.
- **No RCON.** CraftConsole can only manage a server it started itself; it cannot attach to
  an already-running one.
- Machine CPU and memory gauges work on Windows and Linux. Other platforms show them as
  unavailable rather than guessing.
- Player geolocation calls `ipinfo.io` without an API key. It is best-effort, rate-limited,
  and sends player IP addresses to a third party.
