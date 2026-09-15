# GptCli.Tests

xUnit suite for deterministic GPT-CLI helpers outside the DnD engine.

## How To Run

From the repo root:

```bash
dotnet test ./tests/GptCli.Tests/GptCli.Tests.csproj -c Release
```

Or run the whole solution:

```bash
dotnet test ./GPT-CLI.sln -c Release
```

No OpenAI API key or Discord token is required.

## What's Being Tested

- Embeddings: cosine similarity, document JSON load/chunk/rank.
- Core: `GptOptions` secret JSON ignore, `ParameterMapping` request mapping, GPT-5.6/GPT-6 sampling/tool compatibility, embed file/directory IO, async enumerable helpers.
- OpenAI I/O: stub HTTP for Chat Completions, embeddings, and Responses API mapping, token counting.
- Chat: `InstructionChatBot` history trimming, instruction edits, and fake-completion `GetResponseAsync`.
- Hosted bot: channel state create/persist (no login), debounce, mention stripping, guild match, Discord typing, Infobot factoid file write, pipeline discovery of built-in Infobot.
- Discord helpers: module enablement/aliases, function availability, message links, channel directories, `GptCliFunction` JSON/tool schema, infobot query/term parsing, module pipeline ordering and isolation.

Test doubles live in `TestDoubles/`.
