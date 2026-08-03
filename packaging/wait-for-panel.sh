#!/usr/bin/env bash
#
# Polls the panel's health endpoint until it responds or times out. Shared by
# build.yml and release.yml so their .deb verification steps cannot drift out
# of sync with how the panel actually binds -- the v1.1.0 release failed
# because release.yml kept its own copy of this check hardcoded to plain
# http:// after the panel switched to serving HTTPS by default.
#
#   packaging/wait-for-panel.sh [url] [timeout-seconds]
#
set -euo pipefail

URL="${1:-https://127.0.0.1:5178/api/auth/status}"
TIMEOUT="${2:-30}"

for i in $(seq 1 "$TIMEOUT"); do
  # -k: the panel serves a self-signed certificate by default (see TlsCertificateProvider).
  if curl -fsSk -o /dev/null "$URL"; then
    echo "Panel responded after ${i}s"
    exit 0
  fi
  sleep 1
done

echo "Panel did not respond within ${TIMEOUT}s"
exit 1
