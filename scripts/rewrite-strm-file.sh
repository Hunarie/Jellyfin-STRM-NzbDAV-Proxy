#!/usr/bin/env bash
#
# rewrite-strm-file.sh
# --------------------
# Rewrites a SINGLE NZBDav .strm file in place so it points at the Jellyfin
# NZBDav Proxy plugin instead of the internal Docker hostname.
#
# This is the "hook handler": call it with one .strm path. It is idempotent and
# safe to run repeatedly (it only touches files that still contain the internal
# NZBDav origin), so it can be used from:
#   - the watcher daemon (watch-strm.sh),
#   - a Sonarr/Radarr "Custom Script" connection (pass the imported file path),
#   - or any NZBDav post-processing that can run a command with the file path.
#
# Config is read from environment variables, optionally seeded from a config
# file (default /boot/config/nzbdav-proxy.conf on Unraid, override with
# NZBDAV_PROXY_CONF). Required: JELLYFIN_API_KEY.
#
# Usage:
#   rewrite-strm-file.sh /path/to/Movie.strm
#
set -euo pipefail

CONF="${NZBDAV_PROXY_CONF:-/boot/config/nzbdav-proxy.conf}"
# shellcheck disable=SC1090
[ -f "$CONF" ] && . "$CONF"

JELLYFIN_URL="${JELLYFIN_URL:-https://tv.jaydinleeman.com}"
NZBDAV_ORIGIN="${NZBDAV_ORIGIN:-http://nzbdav:3000}"
JELLYFIN_API_KEY="${JELLYFIN_API_KEY:-}"
PROXY_PREFIX="${JELLYFIN_URL%/}/NZBDavProxy/Stream"

file="${1:-}"
[ -n "$file" ] || { echo "usage: $0 <file.strm>" >&2; exit 2; }
case "$file" in
    *.strm) ;;
    *) exit 0 ;;   # silently ignore anything that isn't a .strm
esac
[ -f "$file" ] || { echo "rewrite-strm-file: no such file: $file" >&2; exit 2; }

if [ -z "$JELLYFIN_API_KEY" ]; then
    echo "rewrite-strm-file: JELLYFIN_API_KEY is not set (env or $CONF)" >&2
    exit 2
fi

# Nothing to do if it no longer references the internal origin (already rewritten).
grep -q "$NZBDAV_ORIGIN" "$file" 2>/dev/null || exit 0

new="$(
    awk -v origin="$NZBDAV_ORIGIN" \
        -v prefix="$PROXY_PREFIX" \
        -v apikey="$JELLYFIN_API_KEY" '
    {
        line = $0
        idx = index(line, origin)
        if (idx > 0) {
            path = substr(line, idx + length(origin))
            # If the original URL already had a query string, append with &.
            sep = (index(path, "?") > 0) ? "&" : "?"
            line = substr(line, 1, idx - 1) prefix path sep "api_key=" apikey
        }
        print line
    }' "$file"
)"

# Write atomically so a media player never reads a half-written file.
tmp="$(mktemp "${file}.XXXXXX")"
printf '%s\n' "$new" > "$tmp"
chmod --reference="$file" "$tmp" 2>/dev/null || true
mv -f "$tmp" "$file"
echo "rewrite-strm-file: rewrote $file"
