# CurseForge live-verification checklist

The CurseForge integration (search, install, update-checking) was built and unit-tested against
stubbed HTTP responses only — no CurseForge API key was available in the environment where it was
written. `CurseForgeClient` has been hardened against the documented assumptions below being
wrong (see the commit that added this file), but the assumptions themselves are still unconfirmed
against the real API. Run this checklist once a key is available, in order — each item names the
exact request and the exact thing to check. Expect about ten minutes.

Get a free key from <https://console.curseforge.com/> (Eternal API keys), then either export it as
an environment variable for use with `curl`/a REST client, or paste it into Settings → Integrations
& about in a running instance of the panel and use the Plugins screen directly.

## 1. gameVersion filtering for Bukkit plugins (highest suspicion)

CurseForge plugin authors often tag files loosely (e.g. "1.20" instead of "1.20.4"), unlike the
mod ecosystem which tends to tag precisely. If `gameVersion` filtering doesn't match loosely-tagged
plugin files, search and file-list calls for a Paper/Spigot/Purpur profile with a specific
Minecraft version could come back **empty even when compatible files exist** — which would present
to a user as "CurseForge finds nothing," indistinguishable from a genuinely empty result.

- Request: `GET https://api.curseforge.com/v1/mods/search?gameId=432&classId=5&gameVersion=1.21.4&searchFilter=luckperms`
  (classId 5 = plugins; use a Minecraft version you know a popular plugin like LuckPerms supports)
- Check: the response's `data` array is non-empty and actually contains LuckPerms (or your chosen
  plugin).
- If it comes back empty: try the same request **without** `gameVersion` and compare. If dropping
  the filter suddenly finds results, `gameVersion` filtering is unreliable for plugins specifically,
  and `CurseForgeService.SearchAsync`/`GetFilesAsync` need a fallback (e.g. don't filter by exact
  patch version for the Plugins class) — file a follow-up rather than trying to fix blind.

## 2. Class and loader ids

`CurseForgeService.cs`'s `ClassMods = 6`, `ClassPlugins = 5`, and the `modLoaderType` values
(Forge=1, Fabric=4, NeoForge=6) are recalled from memory of CurseForge's API, not confirmed.

- Request: `GET https://api.curseforge.com/v1/categories?gameId=432`
- Check: find the top-level categories named "Mods" and "Bukkit Plugins" (or similar) in the
  response and confirm their `id` fields are `6` and `5` respectively. If either differs, update
  `ClassMods`/`ClassPlugins` in `CurseForgeService.cs`.
- Request: `GET https://api.curseforge.com/v1/games/432/versions` or the CurseForge API docs' own
  mod-loader-type enum reference, and confirm `1=Forge, 4=Fabric, 6=NeoForge` — the values
  `LoaderInfo` in `CurseForgeService.cs` already assumes.

## 3. File-list page size and ordering

`CurseForgeClient.GetModFilesAsync` now requests `pageSize=50` explicitly and sorts the parsed
result by `fileDate` descending in code — it no longer trusts the API's own ordering. This item is
about confirming the assumptions that fix was built on, not about re-testing the fix itself (that's
covered by unit tests).

- Request: `GET https://api.curseforge.com/v1/mods/{modId}/files?pageSize=50` for a mod with more
  than 50 files (a large modpack dependency is a good candidate).
- Check: the response's `pagination.pageSize` and `pagination.resultCount` fields — confirm 50 is
  actually the max accepted (some APIs silently clamp a larger request; this confirms the panel
  isn't accidentally requesting less than it could).
- Not required, but useful: eyeball whether `data` was already newest-first before the code's own
  sort — if CurseForge's default order turns out to already be reliable, the defensive sort is
  still correct to keep (it's free insurance), just not load-bearing.

## 4. Rate limiting

`CurseForgeClient` now recognizes HTTP 429 and reports a clear "rate-limiting" error, using the
`Retry-After` header when present.

- Fire several dozen requests to `GET https://api.curseforge.com/v1/mods/{modId}` in a tight loop
  (a modpack with many tracked mods, or a manual script) until a 429 is returned.
- Check: the response actually includes a `Retry-After` header (confirm the panel's message reads
  a real wait time rather than always falling back to the generic "try again shortly").
- Check the panel itself: run "Check for updates" against a server with many CurseForge mods
  tracked and confirm a 429 partway through surfaces as a toast naming the wait time, not a raw
  exception message.

## 5. Distribution-disabled files (403 on /download-url)

`CurseForgeClient.ResolveDownloadUrlAsync` now treats a 403 or 404 from the
`/mods/{modId}/files/{fileId}/download-url` endpoint as "nothing to resolve" (returns null) rather
than throwing, on the assumption that a 403 there means the author disabled third-party downloads
rather than a bad key.

- Find a mod file with `isAvailable: false` or a known "no third-party distribution" flag (some
  popular packs deliberately disable this, e.g. via CurseForge's distribution toggle) and note its
  `modId`/`fileId`.
- Request: `GET https://api.curseforge.com/v1/mods/{modId}/files/{fileId}/download-url`
- Check: confirm the actual status code returned is 403 (not 404, not 200-with-null-data like the
  main file object uses) — if it's a different status, `ResolveDownloadUrlAsync`'s status check
  needs updating to match.
- In the panel: try installing that file via Browse and confirm you get the friendly "download it
  from the CurseForge website" message, not a raw error.

## 6. CDN download headers (informational — no code change is planned here)

`DownloadService.DownloadFileAsync` (used for all four download flows — Paper/Vanilla jars, Java
runtimes, Modrinth files, and CurseForge files) sends no explicit `User-Agent` on the actual file
download request, only on the CurseForge/Modrinth API calls themselves via `CurseForgeClient`
/`ModrinthClient`. This is shared infrastructure already exercised successfully by the
Paper/Vanilla/Java download flows, so it wasn't changed as part of this hardening pass — but
CurseForge's CDN (`edge.forgecdn.net` / `mediafilez.forgecdn.net`) is a different host than
Modrinth's or Mojang's, and hasn't been exercised yet.

- Download a real CurseForge file through the panel (Browse → Install) and confirm it completes
  without an error. If it fails with a 403 or similar from the CDN itself (not from CurseForge's
  API), that's evidence the CDN wants a User-Agent or rejects the shared HttpClient's defaults —
  worth a follow-up fix to `DownloadService` at that point, not before.
- If any resolved `downloadUrl` you observe during testing contains an unencoded space or other
  character that would need percent-encoding, note it — .NET's `Uri` class generally auto-escapes
  when constructing from a string, but confirm the actual download still succeeds rather than
  assuming.

## After running this

Update this file (or open a follow-up issue) for anything that came back different from what's
assumed above — especially item 1, since a wrong answer there means CurseForge search silently
under-delivers for the most common server type (Paper/Spigot/Purpur) rather than failing loudly.
