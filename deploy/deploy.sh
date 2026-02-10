#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
APPSETTINGS="${DEPLOY_DIR}/appsettings.json"

# Ensure all relative paths (modules/, docker-compose.yml, etc.) resolve from repo root.
cd "${ROOT_DIR}"

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

cmd="${1:-watch-modules}"
watch_pid_file="/tmp/gptcli-watch-modules.pid"
watch_log_file="/tmp/gptcli-watch-modules.log"
script_path="${DEPLOY_DIR}/deploy.sh"

watch_modules_loop() {
  local last_modules=""
  local last_core=""

  while true; do
    # Hash relevant module sources. If no files match, hash is empty.
    modules_hash="$(
      find modules/examples -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' \) -print0 2>/dev/null |
        sort -z |
        xargs -0 -r sha256sum 2>/dev/null |
        sha256sum 2>/dev/null |
        awk '{print $1}'
    )"

    # Hash core sources that should trigger a discord image rebuild.
    core_hash="$(
      find . \
          -path './bin' -prune -o \
          -path './obj' -prune -o \
          -path './deploy' -prune -o \
          -path './modules/examples' -prune -o \
          -type f \( -name '*.cs' -o -name '*.csproj' -o -name 'Dockerfile.discord' -o -name 'docker-compose.yml' \) -print0 2>/dev/null |
        sort -z |
        xargs -0 -r sha256sum 2>/dev/null |
        sha256sum 2>/dev/null |
        awk '{print $1}'
    )"

    if [[ -z "${last_modules}" ]]; then
      last_modules="${modules_hash}"
    fi
    if [[ -z "${last_core}" ]]; then
      last_core="${core_hash}"
    fi

    if [[ "${modules_hash}" != "${last_modules}" ]]; then
      echo "[watch-modules] change detected; deploying modules..."
      if docker compose run --rm -T builder bash -lc "bash modules/build-deploy-modules.sh"; then
        echo "[watch-modules] restart discord..."
        docker compose restart discord >/dev/null 2>&1 || true
      else
        echo "[watch-modules] module deploy failed; will retry on next change."
      fi
      last_modules="${modules_hash}"
    fi

    if [[ "${core_hash}" != "${last_core}" ]]; then
      echo "[watch-modules] change detected; rebuilding core..."
      if docker compose up -d --build --force-recreate discord; then
        echo "[watch-modules] restart discord..."
        docker compose restart discord >/dev/null 2>&1 || true
      else
        echo "[watch-modules] core build failed; will retry on next change."
      fi
      last_core="${core_hash}"
    fi

    sleep 2
  done
}

case "${cmd}" in
  build)
    docker compose run --rm -T builder
    docker compose build
    ;;
  modules)
    # Build and deploy example modules to the host-mounted modules directory.
    # This is intentionally not part of `up`/`restart` to keep deploy cycles fast.
    docker compose run --rm -T builder bash -lc "bash modules/build-deploy-modules.sh"
    ;;
  watch-modules)
    # Default dev loop: bootstrap core+modules, start a daemon watcher, then stream discord logs.
    if [[ -f "${watch_pid_file}" ]]; then
      old_pid="$(cat "${watch_pid_file}" 2>/dev/null || true)"
      if [[ -n "${old_pid}" ]] && kill -0 "${old_pid}" >/dev/null 2>&1; then
        echo "watch-modules already running (pid=${old_pid}); restarting..."
        "${script_path}" watch-modules-stop >/dev/null 2>&1 || true
      fi
    fi

    echo "Bootstrapping core + modules..."
    docker compose run --rm -T builder
    docker compose run --rm -T builder bash -lc "bash modules/build-deploy-modules.sh"
    docker compose up -d --build --force-recreate discord

    echo "Starting watch-modules daemon in background..."
    echo "Watcher logs: ${watch_log_file}"
    if command -v setsid >/dev/null 2>&1; then
      nohup setsid "${script_path}" watch-modules-daemon >"${watch_log_file}" 2>&1 &
    else
      nohup "${script_path}" watch-modules-daemon >"${watch_log_file}" 2>&1 &
    fi
    echo $! >"${watch_pid_file}"
    echo "watch-modules started (pid=$(cat "${watch_pid_file}"))."
    echo "Streaming discord logs (Ctrl-C to stop viewing logs; watcher keeps running)..."
    echo "Stop watcher with: ${script_path} watch-modules-stop"
    echo "View watcher output with: tail -f ${watch_log_file}"
    docker compose --ansi always logs -f discord
    ;;
  watch-modules-daemon)
    echo "[watch-modules] daemon started (pid=$$)."
    echo "[watch-modules] watching for module/core changes..."
    watch_modules_loop
    ;;
  watch-modules-fg)
    # Build+deploy core + modules once, start the bot, then watch sources for changes and redeploy/restart on each change.
    docker compose run --rm -T builder
    echo "Building+deploying modules..."
    docker compose run --rm -T builder bash -lc "bash modules/build-deploy-modules.sh"

    echo "Starting discord service (build if needed)..."
    docker compose up -d --build --force-recreate discord

    echo "Watching module sources under modules/examples for changes..."
    echo "Watching core sources for changes..."
    echo "On module change: rebuild+deploy modules -> restart discord service"
    echo "On core change: rebuild core -> restart discord service"

    # Watcher runs in the background while logs stream in the foreground.
    ( watch_modules_loop ) &
    watcher_pid="$!"

    cleanup() {
      if [[ -n "${watcher_pid:-}" ]]; then
        kill "${watcher_pid}" >/dev/null 2>&1 || true
      fi
    }
    trap cleanup EXIT INT TERM

    docker compose --ansi always logs -f discord
    ;;
  watch-modules-stop)
    if [[ ! -f "${watch_pid_file}" ]]; then
      echo "watch-modules is not running (no pid file at ${watch_pid_file})."
      exit 0
    fi

    pid="$(cat "${watch_pid_file}" 2>/dev/null || true)"
    if [[ -z "${pid}" ]]; then
      rm -f "${watch_pid_file}" >/dev/null 2>&1 || true
      echo "watch-modules pid file was empty; removed."
      exit 0
    fi

    if kill -0 "${pid}" >/dev/null 2>&1; then
      echo "Stopping watch-modules (pid=${pid})..."
      # Prefer killing the process group so child processes (e.g. `docker compose logs -f`) don't linger.
      kill -TERM -- -"${pid}" >/dev/null 2>&1 || kill -TERM "${pid}" >/dev/null 2>&1 || true
      # Give it a moment to run traps and exit.
      for _ in {1..20}; do
        if kill -0 "${pid}" >/dev/null 2>&1; then
          sleep 0.2
        else
          break
        fi
      done
      if kill -0 "${pid}" >/dev/null 2>&1; then
        kill -KILL -- -"${pid}" >/dev/null 2>&1 || kill -KILL "${pid}" >/dev/null 2>&1 || true
      fi
    else
      echo "watch-modules pid=${pid} not running."
    fi

    rm -f "${watch_pid_file}" >/dev/null 2>&1 || true
    echo "Stopped."
    ;;
  watch-modules-status)
    if [[ -f "${watch_pid_file}" ]]; then
      pid="$(cat "${watch_pid_file}" 2>/dev/null || true)"
      if [[ -n "${pid}" ]] && kill -0 "${pid}" >/dev/null 2>&1; then
        echo "watch-modules running (pid=${pid}). Logs: ${watch_log_file}"
        exit 0
      fi
    fi
    echo "watch-modules not running. Logs (if any): ${watch_log_file}"
    ;;
  up)
    docker compose run --rm -T builder
    docker compose up -d
    ;;
  watch)
    docker compose run --rm -T builder
    docker compose build
    docker compose up -d
    docker compose logs -f discord
    ;;
  restart)
    docker compose run --rm -T builder
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
    echo "Usage: $0 [build|modules|watch-modules|watch-modules-daemon|watch-modules-fg|watch-modules-stop|watch-modules-status|up|watch|restart|logs|stop|completion]"
    echo
    echo "Default (no args): watch-modules"
    exit 1
    ;;
esac
