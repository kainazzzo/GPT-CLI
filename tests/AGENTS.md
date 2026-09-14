# Tests

Three xUnit projects. Always `dotnet test ./GPT-CLI.sln -c Release` before claiming a change is done. **No live OpenAI or Discord.** No real API keys required.

| Project | Covers | Fakes |
|---|---|---|
| `GptCli.Tests` | Embeddings, `GptOptions`, `ParameterMapping`, OpenAI HTTP mapping, `InstructionChatBot`, Discord host helpers, Infobot, module pipeline | `FakeOpenAIService`, `StubHttpMessageHandler`, `FakeFeatureModule` |
| `GptCli.Dnd.Tests` | `Chat/Dnd` engine: combat, campaign, **session FSM**, scene synthesis, rest/checks, persistence | `FixedDiceRoller`, `FakeClock` |
| `GptCli.Modules.Tests` | Example modules: parsers, allow-lists, casino math, poll lifecycle, pinboard, welcome, DnD intents | Seeded `Random`, in-memory `ChannelState` |

Per-project notes: `GptCli.Dnd.Tests/README.md`, `GptCli.Modules.Tests/README.md`, `GptCli.Tests/README.md`.

## InternalsVisibleTo

- Host `gpt.csproj` → `GptCli.Tests`, `GptCli.Dnd.Tests`
- Each example module → `GptCli.Modules.Tests`

When adding `internal` helpers for tests, keep those attributes.

## DnD test expectations

Session tests live in `DndSessionRunnerTests.cs`. Combat tests must still pass **without** `StartSession()` (`StartEncounter` remains valid standalone). If you change legal option ids, update both engine tests and `DndGameMasterModuleTests.TryMatchSessionOption_*`.
