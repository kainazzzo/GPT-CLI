#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

cd "${SCRIPT_DIR}"

dotnet build DndModuleExample.slnx -c Release /p:GptSkipModuleDeploy=true

cp "${SCRIPT_DIR}/DndModuleExample/bin/Release/net10.0/DndModuleExample.dll" "${OUTPUT_DIR}/"

echo "Module copied to ${OUTPUT_DIR}/DndModuleExample.dll"
