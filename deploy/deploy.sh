#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
APPSETTINGS="${DEPLOY_DIR}/appsettings.json"

if [[ "${1:-}" == "completion" ]]; then
  cat "${DEPLOY_DIR}/deploy-completion.bash"
  exit 0
fi

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
  completion)
    cat "${DEPLOY_DIR}/deploy-completion.bash"
    ;;
  *)
    echo "Usage: $0 {build|up|restart|logs|stop|completion}"
    exit 1
    ;;
esac
