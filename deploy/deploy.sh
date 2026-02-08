#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
APPSETTINGS="${DEPLOY_DIR}/appsettings.json"

export DOCKER_BUILDKIT=1
export COMPOSE_DOCKER_CLI_BUILD=1

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
    docker compose run --rm builder
    docker compose build
    ;;
  modules)
    # Build and deploy example modules to the host-mounted modules directory.
    # This is intentionally not part of `up`/`restart` to keep deploy cycles fast.
    docker compose run --rm builder bash -lc "bash modules/build-deploy-modules.sh"
    ;;
  up|"")
    docker compose run --rm builder
    docker compose up -d
    ;;
  watch)
    docker compose run --rm builder
    docker compose build
    docker compose up
    ;;
  restart)
    docker compose run --rm builder
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
    echo "Usage: $0 {build|modules|up|watch|restart|logs|stop|completion}"
    exit 1
    ;;
esac
