#!/usr/bin/env bash
#
# rewrite-strm.sh
# ----------------
# Rewrites NZBDav .strm files so they point at the Jellyfin NZBDav Proxy plugin
# instead of the internal Docker hostname, so remote players (Infuse, etc.) can
# reach them.
#
#   FROM:  http://nzbdav:3000/content/Movies/Example/Example.mkv
#   TO:    https://tv.jaydinleeman.com/NZBDavProxy/Stream/content/Movies/Example/Example.mkv?api_key=YOURKEY
#
# It rewrites the scheme+host portion only and appends your Jellyfin API key as a
# query parameter (Jellyfin accepts ?api_key=... for authentication, which is how
# a static .strm URL authenticates the proxy request).
#
# Usage:
#   ./rewrite-strm.sh [MEDIA_DIR]
#
# Configure the three variables below (or pass MEDIA_DIR as the first argument).
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

# A Jellyfin API key. Create one under:
#   Dashboard -> Advanced -> API Keys -> +
# You can also export it instead of editing the file:  export JELLYFIN_API_KEY=xxxx
JELLYFIN_API_KEY="${JELLYFIN_API_KEY:-REPLACE_WITH_YOUR_JELLYFIN_API_KEY}"

# Directory containing your .strm files. Defaults to first arg, then current dir.
MEDIA_DIR="${1:-${MEDIA_DIR:-.}}"

# ----------------------------------------------------------------------------
# Derived values
# ----------------------------------------------------------------------------

# The proxy path prefix. Must match the controller route: /NZBDavProxy/Stream/
PROXY_PREFIX="${JELLYFIN_URL%/}/NZBDavProxy/Stream"

if [[ "$JELLYFIN_API_KEY" == "REPLACE_WITH_YOUR_JELLYFIN_API_KEY" ]]; then
    echo "ERROR: Set JELLYFIN_API_KEY (edit the script or 'export JELLYFIN_API_KEY=...')." >&2
    exit 1
fi

if [[ ! -d "$MEDIA_DIR" ]]; then
    echo "ERROR: Media directory not found: $MEDIA_DIR" >&2
    exit 1
fi

DRY_RUN="${DRY_RUN:-0}"

echo "Media dir     : $MEDIA_DIR"
echo "Replacing     : ${NZBDAV_ORIGIN}/..."
echo "With          : ${PROXY_PREFIX}/...?api_key=<hidden>"
[[ "$DRY_RUN" == "1" ]] && echo "Mode          : DRY RUN (no files will be changed)"
echo

# Escape characters that are special to sed (in the search and replacement text).
sed_escape() { printf '%s' "$1" | sed -e 's/[\/&|]/\\&/g'; }

SEARCH_ESC="$(sed_escape "$NZBDAV_ORIGIN")"
PREFIX_ESC="$(sed_escape "$PROXY_PREFIX")"
KEY_ESC="$(sed_escape "$JELLYFIN_API_KEY")"

changed=0
scanned=0

# -print0 / read -d '' safely handles spaces and unicode in filenames.
while IFS= read -r -d '' file; do
    scanned=$((scanned + 1))

    # Only touch files that still reference the internal origin.
    if ! grep -q "$NZBDAV_ORIGIN" "$file"; then
        continue
    fi

    # Build the replacement with awk so we can correctly choose ? vs & for the
    # api_key depending on whether the original URL already had a query string.
    new_content="$(
        awk -v origin="$NZBDAV_ORIGIN" \
            -v prefix="$PROXY_PREFIX" \
            -v apikey="$JELLYFIN_API_KEY" '
        {
            line = $0
            idx = index(line, origin)
            if (idx > 0) {
                # path = everything after the origin on this line
                path = substr(line, idx + length(origin))
                sep = (index(path, "?") > 0) ? "&" : "?"
                line = substr(line, 1, idx - 1) prefix path sep "api_key=" apikey
            }
            print line
        }' "$file"
    )"

    if [[ "$DRY_RUN" == "1" ]]; then
        echo "WOULD UPDATE: $file"
        echo "    -> $(printf '%s' "$new_content" | head -n1 | sed "s/${KEY_ESC}/<api_key>/")"
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
