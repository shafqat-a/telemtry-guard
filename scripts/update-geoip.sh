#!/usr/bin/env bash
# Downloads GeoLite2 City+ASN (requires MAXMIND_LICENSE_KEY), IP2Proxy LITE PX11
# (requires IP2LOCATION_TOKEN, optional), and Apple Private Relay egress ranges.
# Run weekly (cron/systemd timer); the app hot-swaps readers when files change.
set -euo pipefail
: "${MAXMIND_LICENSE_KEY:?Set MAXMIND_LICENSE_KEY (free GeoLite2 account)}"
DATA_DIR="${1:-./data/geo}"
mkdir -p "$DATA_DIR"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

for ED in GeoLite2-City GeoLite2-ASN; do
  curl -fsSL "https://download.maxmind.com/app/geoip_download?edition_id=${ED}&license_key=${MAXMIND_LICENSE_KEY}&suffix=tar.gz" -o "$TMP/${ED}.tar.gz"
  tar -xzf "$TMP/${ED}.tar.gz" -C "$TMP"
  MMDB="$(find "$TMP" -name "${ED}.mmdb" | head -1)"
  cp "$MMDB" "$DATA_DIR/${ED}.mmdb.tmp" && mv "$DATA_DIR/${ED}.mmdb.tmp" "$DATA_DIR/${ED}.mmdb"   # atomic within one fs
done

if [ -n "${IP2LOCATION_TOKEN:-}" ]; then
  curl -fsSL "https://www.ip2location.com/download/?token=${IP2LOCATION_TOKEN}&file=PX11LITEBIN" -o "$TMP/px.zip"
  unzip -o "$TMP/px.zip" -d "$TMP/px" >/dev/null
  BIN="$(find "$TMP/px" -name '*.BIN' | head -1)"
  cp "$BIN" "$DATA_DIR/IP2PROXY-LITE-PX11.BIN.tmp" && mv "$DATA_DIR/IP2PROXY-LITE-PX11.BIN.tmp" "$DATA_DIR/IP2PROXY-LITE-PX11.BIN"
fi

curl -fsSL "https://mask-api.icloud.com/egress-ip-ranges.csv" -o "$DATA_DIR/apple-private-relay.csv.tmp" \
  && mv "$DATA_DIR/apple-private-relay.csv.tmp" "$DATA_DIR/apple-private-relay.csv"
echo "geo data updated in $DATA_DIR"
