#!/usr/bin/env bash
# Fetches iplegence's merged IP-intelligence database (Superior-IP.mmdb) into the geo
# data directory. The API memory-maps it and hot-swaps readers when the file changes
# (IpEnrichment:Provider=Iplegence, spec D24) — no restart, no downtime.
#
# Run daily; iplegence publishes a vYYYY.MM.DD release at 01:00 UTC.
#
# Sources, in the order tried (first one that is usable wins):
#   1. $IPLEGENCE_MMDB_URL   — any HTTPS URL (release asset, Azure Blob SAS, CDN…)
#   2. $IPLEGENCE_DIST_DIR   — a local iplegence checkout's dist/ (dev machines)
#   3. GitHub release        — gh CLI, repo $IPLEGENCE_REPO (private: needs gh auth)
#
# Usage: ./scripts/update-iplegence.sh [DATA_DIR]
set -euo pipefail

DATA_DIR="${1:-./data/geo}"
REPO="${IPLEGENCE_REPO:-shafqat-a/iplegence}"
ASSET="Superior-IP.mmdb"
DEST="$DATA_DIR/$ASSET"

mkdir -p "$DATA_DIR"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

fetch() {
  if [ -n "${IPLEGENCE_MMDB_URL:-}" ]; then
    echo "Downloading $ASSET from \$IPLEGENCE_MMDB_URL"
    curl -fsSL "$IPLEGENCE_MMDB_URL" -o "$TMP/$ASSET"
    # A sibling .sha256 is optional for URL sources.
    curl -fsSL "${IPLEGENCE_MMDB_URL}.sha256" -o "$TMP/$ASSET.sha256" 2>/dev/null || true
    return
  fi

  if [ -n "${IPLEGENCE_DIST_DIR:-}" ]; then
    echo "Copying $ASSET from $IPLEGENCE_DIST_DIR"
    cp "$IPLEGENCE_DIST_DIR/$ASSET" "$TMP/$ASSET"
    [ -f "$IPLEGENCE_DIST_DIR/$ASSET.sha256" ] && cp "$IPLEGENCE_DIST_DIR/$ASSET.sha256" "$TMP/$ASSET.sha256"
    return
  fi

  command -v gh >/dev/null 2>&1 || {
    echo "error: no IPLEGENCE_MMDB_URL, no IPLEGENCE_DIST_DIR, and the gh CLI is not installed." >&2
    echo "       $REPO is private — install gh and 'gh auth login', or set one of the above." >&2
    exit 1
  }
  echo "Downloading $ASSET from the latest $REPO release"
  gh release download --repo "$REPO" --pattern "$ASSET" --pattern "$ASSET.sha256" --dir "$TMP" --clobber
}

fetch

[ -s "$TMP/$ASSET" ] || { echo "error: downloaded $ASSET is empty" >&2; exit 1; }

if [ -f "$TMP/$ASSET.sha256" ]; then
  ( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null ) \
    && echo "sha256 verified" \
    || { echo "error: sha256 mismatch — refusing to install $ASSET" >&2; exit 1; }
else
  echo "warning: no $ASSET.sha256 alongside the download — skipping integrity check" >&2
fi

# Atomic within one filesystem: the API never sees a half-written database.
mv "$TMP/$ASSET" "$DEST.tmp" && mv "$DEST.tmp" "$DEST"
[ -f "$TMP/$ASSET.sha256" ] && mv "$TMP/$ASSET.sha256" "$DEST.sha256"

# Attribution ships with every build and must travel with the data (source licenses).
if [ -n "${IPLEGENCE_DIST_DIR:-}" ] && [ -f "$IPLEGENCE_DIST_DIR/ATTRIBUTION.md" ]; then
  cp "$IPLEGENCE_DIST_DIR/ATTRIBUTION.md" "$DATA_DIR/IPLEGENCE-ATTRIBUTION.md"
elif command -v gh >/dev/null 2>&1 && [ -z "${IPLEGENCE_MMDB_URL:-}" ]; then
  gh release download --repo "$REPO" --pattern ATTRIBUTION.md --dir "$TMP" --clobber 2>/dev/null \
    && mv "$TMP/ATTRIBUTION.md" "$DATA_DIR/IPLEGENCE-ATTRIBUTION.md" || true
fi

echo "iplegence data updated: $DEST ($(du -h "$DEST" | cut -f1))"

# The Apple Private Relay egress list is still used as a supplement to the dataset's
# is_relay trait (and is the only source of that flag when no database is present).
if [ ! -f "$DATA_DIR/apple-private-relay.csv" ]; then
  curl -fsSL "https://mask-api.icloud.com/egress-ip-ranges.csv" -o "$DATA_DIR/apple-private-relay.csv.tmp" \
    && mv "$DATA_DIR/apple-private-relay.csv.tmp" "$DATA_DIR/apple-private-relay.csv" \
    && echo "apple-private-relay.csv seeded"
fi
