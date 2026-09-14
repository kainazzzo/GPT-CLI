# Pinboard module (`pinboard`)

Id `pinboard`. Source: `PinboardModuleExample/PinboardModule.cs`. Slash group name is `pin`. State: `ChannelState.Pinboard`.

## Behavior

Save message snippets to a per-channel list, list them, remove by index. Slash: `/gptcli pin …`. Bang: `!pin`, `!pin list`, `!pin remove 1`. Parser: `TryParseMessageCommand`. No LLM required for mechanics.

## Tests

`tests/GptCli.Modules.Tests/Pinboard/` — parse + add/remove validation (optional fake `IMessageChannel`).

## Build

`build-module.sh` → `modules/PinboardModuleExample.dll`.
