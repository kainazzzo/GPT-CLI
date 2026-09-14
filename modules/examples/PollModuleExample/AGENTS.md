# Poll module (`poll`)

Id `poll`. Source: `PollModuleExample/PollModule.cs`. State: `ChannelState.Polls`.

## Behavior

Create / vote / list / results / close / delete. Slash: `/gptcli poll …`. Bang: `!poll create Lunch pizza, salad`, `!poll vote 1 2`, `!poll` (list). Parser: `TryParseMessageCommand`.

LLM may summarize stored poll data; it must not invent options, ids, or vote counts (`PollSystemPrompt`).

## Tests

`tests/GptCli.Modules.Tests/Poll/` — parse + full lifecycle on in-memory `PollState`.

## Build

`build-module.sh` → `modules/PollModuleExample.dll`.
