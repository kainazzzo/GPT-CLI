#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
APPSETTINGS="${DEPLOY_DIR}/appsettings.json"

if [[ ! -f "${APPSETTINGS}" ]]; then
  echo "Missing ${APPSETTINGS}. Add your appsettings.json in deploy/."
  exit 1
fi

case "${1:-}" in
  build)
    docker compose build
    ;;
  up|"")
    docker compose up -d
    ;;
  restart)
    docker compose build
    docker compose up -d
    ;;
  logs)
    docker compose logs -f discord
    ;;
  stop)
    docker compose down
    ;;
  *)
    echo "Usage: $0 {build|up|restart|logs|stop}"
    exit 1
    ;;
esac
