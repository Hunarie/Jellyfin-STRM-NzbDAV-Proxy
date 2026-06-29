#!/usr/bin/env bash
#
# rewrite-strm.sh
# ----------------
# Rewrites NZBDav .strm files so they point at the Jellyfin NZBDav Proxy plugin
# instead of the internal Docker hostname, so remote players (Infuse, etc.) can
# reach them.
#
#   FROM:  http://nzbdav:3000/view/.ids/.../uuid?downloadKey=...&extension=mkv
#   TO:    https://tv.jaydinleeman.com/NZBDavProxy/Stream/view/.ids/.../uuid?downloadKey=...&extension=mkv
#
# It replaces the origin (scheme+host) only; the path and all existing query
# parameters (downloadKey, extension, etc.) are preserved exactly as-is.
# No API key is appended — the proxy endpoint is anonymous; access is gated by
# NZBDav's per-file downloadKey already present in the URL.
#
# Usage:
#   ./rewrite-strm.sh [MEDIA_DIR]
#
# Configure the two variables below (or pass MEDIA_DIR as the first argument).
# Run with DRY_RUN=1 first to preview changes without touching any files:
#   DRY_RUN=1 ./rewrite-strm.sh /path/to/media
#
set -euo pipefail

# ----------------------------------------------------------------------------
# CONFIG — edit these
# ----------------------------------------------------------------------------

# Your public Jellyfin base URL (no trailing slash). PLACEHOLDER — swap as needed.
JELLYFIN_URL="${JELLYFIN_URL:-https://tv.jaydinleeman.com}"

# The internal NZBDav origin currently embedded in your .strm files (no trailing slash).
NZBDAV_ORIGIN="${NZBDAV_ORIGIN:-http://nzbdav:3000}"

# Directory containing your .strm files. Defaults to first arg, then current dir.
MEDIA_DIR="${1:-${MEDIA_DIR:-.}}"

# ----------------------------------------------------------------------------
# Derived values
# ----------------------------------------------------------------------------

# The proxy path prefix. Must match the controller route: /NZBDavProxy/Stream/
PROXY_PREFIX="${JELLYFIN_URL%/}/NZBDavProxy/Stream"

if [[ ! -d "$MEDIA_DIR" ]]; then
    echo "ERROR: Media directory not found: $MEDIA_DIR" >&2
    exit 1
fi

DRY_RUN="${DRY_RUN:-0}"

echo "Media dir     : $MEDIA_DIR"
echo "Replacing     : ${NZBDAV_ORIGIN}/..."
echo "With          : ${PROXY_PREFIX}/..."
[[ "$DRY_RUN" == "1" ]] && echo "Mode          : DRY RUN (no files will be changed)"
echo

changed=0
scanned=0

# -print0 / read -d '' safely handles spaces and unicode in filenames.
while IFS= read -r -d '' file; do
    scanned=$((scanned + 1))

    # Only touch files that still reference the internal origin.
    if ! grep -q "$NZBDAV_ORIGIN" "$file"; then
        continue
    fi

    new_content="$(
        awk -v origin="$NZBDAV_ORIGIN" \
            -v prefix="$PROXY_PREFIX" '
        {
            line = $0
            idx = index(line, origin)
            if (idx > 0) {
                path = substr(line, idx + length(origin))
                line = substr(line, 1, idx - 1) prefix path
            }
            print line
        }' "$file"
    )"

    if [[ "$DRY_RUN" == "1" ]]; then
        echo "WOULD UPDATE: $file"
        echo "    -> $(printf '%s' "$new_content" | head -n1)"
    else
        # Preserve a .bak the first time, then write the new content atomically.
        cp -p -- "$file" "${file}.bak"
        printf '%s\n' "$new_content" > "$file"
        echo "UPDATED: $file"
    fi
    changed=$((changed + 1))
done < <(find "$MEDIA_DIR" -type f -name '*.strm' -print0)

echo
echo "Scanned $scanned .strm file(s); ${changed} contained the internal origin."
if [[ "$DRY_RUN" != "1" && "$changed" -gt 0 ]]; then
    echo "Backups written next to each file as *.strm.bak"
    echo "Once you've confirmed playback works, remove them with:"
    echo "    find \"$MEDIA_DIR\" -name '*.strm.bak' -delete"
fi
