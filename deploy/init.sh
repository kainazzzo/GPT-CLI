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

set -a
# shellcheck disable=SC1090
source "${ENV_FILE}"
set +a

cat > "${APPSETTINGS}" <<EOF
{
  "OpenAI": {
    "ApiKey": "${OPENAI_API_KEY}"
  },
  "GPT": {
    "Mode": "Discord",
    "BotToken": "${DISCORD_BOT_TOKEN}",
    "Model": "${GPT_MODEL}",
    "MaxTokens": ${GPT_MAX_TOKENS},
    "ChunkSize": ${GPT_CHUNK_SIZE},
    "MaxChatHistoryLength": ${GPT_MAX_CHAT_HISTORY_LENGTH}
  }
}
EOF

echo "Generated ${APPSETTINGS}"
