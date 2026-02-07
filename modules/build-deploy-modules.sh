#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODULES_DIR="${SCRIPT_DIR}"
EXAMPLES_DIR="${MODULES_DIR}/examples"
TARGET_DIR="${1:-/usr/local/discord/modules}"
TARGET_DIR="${TARGET_DIR%/}"

if [[ ! -d "${EXAMPLES_DIR}" ]]; then
  echo "Examples directory not found: ${EXAMPLES_DIR}" >&2
  exit 1
fi

echo "Building module projects..."
csproj_files=()
while IFS= read -r -d '' csproj; do
  csproj_files+=("${csproj}")
done < <(find "${EXAMPLES_DIR}" -name "*.csproj" -print0)

if [[ ${#csproj_files[@]} -eq 0 ]]; then
  echo "No module projects found under ${EXAMPLES_DIR}." >&2
  exit 1
fi

echo "Restoring module projects..."
for csproj in "${csproj_files[@]}"; do
  echo "-> dotnet restore ${csproj}"
  dotnet restore "${csproj}"
done

for csproj in "${csproj_files[@]}"; do
  echo "-> dotnet build ${csproj}"
  dotnet build "${csproj}" -c Release --no-restore /p:GptSkipModuleDeploy=true /p:GenerateAssemblyInfo=false /p:GenerateTargetFrameworkAttribute=false
done

echo "Deploying module DLLs to ${TARGET_DIR}..."
if ! mkdir -p "${TARGET_DIR}" 2>/dev/null; then
  echo "Warning: cannot create target dir ${TARGET_DIR} (permission denied). Skipping deploy." >&2
  exit 0
fi
if [[ ! -w "${TARGET_DIR}" ]]; then
  echo "Warning: target dir not writable ${TARGET_DIR}. Skipping deploy." >&2
  exit 0
fi

target_uid="$(stat -c '%u' "${TARGET_DIR}" 2>/dev/null || echo '')"
target_gid="$(stat -c '%g' "${TARGET_DIR}" 2>/dev/null || echo '')"

for csproj in "${csproj_files[@]}"; do
  project_dir="$(cd "$(dirname "${csproj}")" && pwd)"
  project_name="$(basename "${csproj}" .csproj)"
  dll_path=""

  while IFS= read -r -d '' candidate; do
    dll_path="${candidate}"
    break
  done < <(find "${project_dir}/bin/Release" -name "${project_name}.dll" -print0)

  if [[ -z "${dll_path}" ]]; then
    echo "Warning: build output not found for ${project_name}." >&2
    continue
  fi

  echo "-> ${dll_path}"
  dest_path="${TARGET_DIR}/$(basename "${dll_path}")"

  # If a previous deploy ran as root inside a container, the existing file may be root-owned and not writable.
  # Removing the destination first avoids cp failing with "Permission denied" on overwrite.
  rm -f "${dest_path}" 2>/dev/null || true
  cp "${dll_path}" "${dest_path}"

  # Best-effort: align ownership with the target directory so future non-root deploys can overwrite.
  if [[ -n "${target_uid}" && -n "${target_gid}" ]]; then
    chown "${target_uid}:${target_gid}" "${dest_path}" 2>/dev/null || true
  fi
done

echo "Done."
