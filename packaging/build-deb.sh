#!/usr/bin/env bash
#
# Builds a self-contained CraftConsole .deb.
#
#   packaging/build-deb.sh <version> <rid> [output-dir]
#
#   version     upstream version, no leading "v" (e.g. 1.2.3 or 1.2.3~rc1)
#   rid         linux-x64 | linux-arm64
#   output-dir  defaults to ./dist
#
# Only dpkg-deb and the .NET SDK are required; both are present on Ubuntu runners.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> <rid> [output-dir]}"
RID="${2:?usage: build-deb.sh <version> <rid> [output-dir]}"
OUTPUT_DIR="${3:-dist}"

case "$RID" in
  linux-x64)   DEB_ARCH=amd64 ;;
  linux-arm64) DEB_ARCH=arm64 ;;
  *) echo "Unsupported runtime identifier: $RID" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "Building CraftConsole ${VERSION} for ${DEB_ARCH} (${RID})"

# ── Publish ───────────────────────────────────────────────────────────────
# CRAFTCONSOLE_BINARY reuses an existing publish instead of building one. Handy
# when iterating on packaging, or on a machine without the .NET SDK.
PUBLISH_DIR="$STAGE/publish"
if [ -n "${CRAFTCONSOLE_BINARY:-}" ]; then
  echo "Using pre-built binary: $CRAFTCONSOLE_BINARY"
  mkdir -p "$PUBLISH_DIR"
  cp "$CRAFTCONSOLE_BINARY" "$PUBLISH_DIR/CraftConsole"
else
  dotnet publish "$REPO_ROOT/src/CraftConsole.Web/CraftConsole.Web.csproj" \
    --configuration Release \
    --runtime "$RID" \
    -p:Version="$VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --output "$PUBLISH_DIR"
fi

# Assets are embedded in the assembly, so this must be the only file produced.
PUBLISHED_COUNT="$(find "$PUBLISH_DIR" -type f | wc -l)"
if [ "$PUBLISHED_COUNT" -ne 1 ]; then
  echo "Expected a single published file, found ${PUBLISHED_COUNT}:" >&2
  find "$PUBLISH_DIR" -type f >&2
  exit 1
fi

# ── Stage the package tree ────────────────────────────────────────────────
ROOT="$STAGE/root"
install -d -m 0755 "$ROOT/DEBIAN"
install -d -m 0755 "$ROOT/opt/craftconsole"
install -d -m 0755 "$ROOT/lib/systemd/system"
install -d -m 0755 "$ROOT/usr/share/doc/craftconsole"

install -m 0755 "$PUBLISH_DIR/CraftConsole" "$ROOT/opt/craftconsole/CraftConsole"
install -m 0644 "$REPO_ROOT/packaging/debian/craftconsole.service" \
                "$ROOT/lib/systemd/system/craftconsole.service"

for script in postinst prerm postrm; do
  install -m 0755 "$REPO_ROOT/packaging/debian/$script" "$ROOT/DEBIAN/$script"
done

# Installed-Size is in KiB and is what apt shows before installing.
INSTALLED_SIZE="$(du -sk "$ROOT" | cut -f1)"

# Continuation lines must start with a single space; a blank line must be " .".
cat > "$ROOT/DEBIAN/control" <<EOF
Package: craftconsole
Version: ${VERSION}
Architecture: ${DEB_ARCH}
Maintainer: HexEditHD <noreply@github.com>
Installed-Size: ${INSTALLED_SIZE}
Section: web
Priority: optional
Homepage: https://github.com/HexEditHD/CraftConsole
Depends: adduser, libc6
Description: Web control panel for Minecraft servers
 CraftConsole manages Minecraft servers from a browser: start and stop the
 server, watch its console live, run commands, manage players and plugins,
 edit configuration files, schedule tasks and take backups.
 .
 It runs as a service listening on localhost and is password protected. The
 .NET runtime is bundled, so no separate runtime package is required.
EOF

cat > "$ROOT/usr/share/doc/craftconsole/copyright" <<'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: CraftConsole
Source: https://github.com/HexEditHD/CraftConsole
EOF

# ── Build ─────────────────────────────────────────────────────────────────
mkdir -p "$OUTPUT_DIR"
DEB_PATH="$OUTPUT_DIR/craftconsole_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --root-owner-group --build "$ROOT" "$DEB_PATH"

echo ""
dpkg-deb --info "$DEB_PATH"
echo "Built $DEB_PATH"
