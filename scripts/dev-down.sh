#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Pass -v to also delete named volumes (destroys all local data).
if [ "${1:-}" = "-v" ]; then
  docker compose down -v
else
  docker compose down
fi
