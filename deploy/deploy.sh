#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
ENV_FILE="${DEPLOY_DIR}/.env"
APPSETTINGS="${ROOT_DIR}/appsettings.json"

if [[ ! -f "${ENV_FILE}" ]]; then
  echo "Missing ${ENV_FILE}. Copy deploy/.env.example to deploy/.env and fill in secrets."
  exit 1
fi

"${DEPLOY_DIR}/init.sh"

case "${1:-}" in
  build)
    docker compose build
    ;;
  up|"")
    docker compose up -d
    ;;
  logs)
    docker compose logs -f discord
    ;;
  stop)
    docker compose down
    ;;
  *)
    echo "Usage: $0 {build|up|logs|stop}"
    exit 1
    ;;
esac
