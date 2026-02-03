#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

cd "${SCRIPT_DIR}"

dotnet build CasinoModuleExample.slnx -c Release

cp "${SCRIPT_DIR}/CasinoModuleExample/bin/Release/net10.0/CasinoModuleExample.dll" "${OUTPUT_DIR}/"

echo "Module copied to ${OUTPUT_DIR}/CasinoModuleExample.dll"
