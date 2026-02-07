#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

cd "${SCRIPT_DIR}"

dotnet build PollModuleExample.slnx -c Release

cp "${SCRIPT_DIR}/PollModuleExample/bin/Release/net10.0/PollModuleExample.dll" "${OUTPUT_DIR}/"

echo "Module copied to ${OUTPUT_DIR}/PollModuleExample.dll"
