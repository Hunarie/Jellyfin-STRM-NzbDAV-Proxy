#!/usr/bin/env bash
#
# watch-strm.sh
# -------------
# Watches a media tree and rewrites NZBDav .strm files to the Jellyfin proxy URL
# the moment they are created or modified. This is the "post-processing hook":
# run it once as a background service and every new download is fixed
# automatically.
#
# It uses inotifywait (inotify-tools) when available for instant, event-driven
# rewriting, and transparently falls back to a lightweight polling loop when it
# is not installed (e.g. a stock Unraid host without NerdTools).
#
# Config is read the same way as rewrite-strm-file.sh (env vars, optionally from
# NZBDAV_PROXY_CONF / /boot/config/nzbdav-proxy.conf). Required: JELLYFIN_API_KEY.
#
# Usage:
#   watch-strm.sh /mnt/user/media
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REWRITE="$HERE/rewrite-strm-file.sh"

CONF="${NZBDAV_PROXY_CONF:-/boot/config/nzbdav-proxy.conf}"
# shellcheck disable=SC1090
[ -f "$CONF" ] && . "$CONF"

MEDIA_DIR="${1:-${MEDIA_DIR:-}}"
if [ -z "$MEDIA_DIR" ] || [ ! -d "$MEDIA_DIR" ]; then
    echo "watch-strm: set MEDIA_DIR (arg 1 or env) to your media root; got '${MEDIA_DIR:-}'" >&2
    exit 2
fi
[ -x "$REWRITE" ] || chmod +x "$REWRITE" 2>/dev/null || true

echo "watch-strm: rewriting any existing .strm files under $MEDIA_DIR ..."
# Initial sweep so the current backlog is fixed before we start watching.
find "$MEDIA_DIR" -type f -name '*.strm' -print0 \
    | while IFS= read -r -d '' f; do "$REWRITE" "$f" || true; done

if command -v inotifywait >/dev/null 2>&1; then
    echo "watch-strm: watching $MEDIA_DIR with inotify (event-driven)."
    # close_write: file written in place and closed. moved_to: atomically moved in.
    inotifywait -m -r -e close_write -e moved_to --format '%w%f' "$MEDIA_DIR" \
        | while IFS= read -r f; do
            case "$f" in
                *.strm) "$REWRITE" "$f" || true ;;
            esac
        done
else
    echo "watch-strm: inotifywait not found; polling every 15s." >&2
    while true; do
        # Re-check files touched in the last ~30s; rewrite is idempotent so any
        # overlap or re-processing is harmless.
        find "$MEDIA_DIR" -type f -name '*.strm' -newermt '-30 seconds' 2>/dev/null \
            | while IFS= read -r f; do "$REWRITE" "$f" || true; done
        sleep 15
    done
fi
