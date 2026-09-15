# GptCli.Modules.Tests

xUnit suite for example Discord modules (`Poll`, `Casino`, `Pinboard`, `Welcome`, `DnD`).

## How To Run

From the repo root:

```bash
dotnet test ./tests/GptCli.Modules.Tests/GptCli.Modules.Tests.csproj -c Release
```

Or run the whole solution:

```bash
dotnet test ./GPT-CLI.sln -c Release
```

No OpenAI API key or Discord token is required. Tests call module domain helpers with fakes; they do not log into Discord.

## What's Being Tested

- Poll create/vote/list/results/close/delete and `!poll` parsing.
- Casino command parsing, purchase/wallet, seeded coinflip, card/hand math.
- Pinboard `!pin` parsing, remove, add validation, optional fake `IMessageChannel` add.
- Welcome validation application, nudge cooldown, rules text.
- DnD GM natural-language intents, confirmations, narration sanitizing, tool-vs-mode allowlist, draft propose/select parsing.
