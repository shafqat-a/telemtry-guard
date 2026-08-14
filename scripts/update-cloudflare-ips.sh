#!/usr/bin/env bash
set -euo pipefail
out="$(dirname "$0")/../TelemetryGuard.Api/Edge/cloudflare-ips.txt"
{
  echo "# https://www.cloudflare.com/ips-v4  (refresh: scripts/update-cloudflare-ips.sh)"
  curl -fsS https://www.cloudflare.com/ips-v4
  echo "# https://www.cloudflare.com/ips-v6"
  curl -fsS https://www.cloudflare.com/ips-v6
} > "$out"
echo "updated $out"
