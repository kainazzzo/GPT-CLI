# GPT-CLI

.NET 10 CLI and Discord bot around OpenAI-compatible chat, embeddings, and pluggable Discord feature modules. Target framework is `net10.0` (`gpt.csproj`). Example modules are **not** compiled into the CLI; they ship as separate DLLs loaded at Discord startup.

This file orients an agent to the whole repo. Nested `AGENTS.md` files cover a directory in more detail — read the closest one before editing that area.

## Nested agent files

| Path | When to read |
|---|---|
| `Chat/AGENTS.md` | Chat bot, Discord host, DnD engine |
| `Chat/Discord/AGENTS.md` | Discord host, `/gptcli`, module pipeline |
| `Chat/Dnd/AGENTS.md` | Deterministic DnD-lite rules engine (session + combat) |
| `modules/AGENTS.md` | How Discord feature modules load and should be written |
| `modules/examples/DndModuleExample/AGENTS.md` | DnD Discord adapter (draft/game, tools, persistence) |
| `modules/examples/CasinoModuleExample/AGENTS.md` | Casino module |
| `modules/examples/PollModuleExample/AGENTS.md` | Poll module |
| `modules/examples/PinboardModuleExample/AGENTS.md` | Pinboard module |
| `modules/examples/WelcomeModuleExample/AGENTS.md` | Welcome/onboarding module |
| `tests/AGENTS.md` | Test projects, fakes, what not to hit |

Built-in Discord module (compiled into the host, not a DLL): `Chat/Discord/Modules/InfobotModule.cs` (id `infobot`).

## What the app does

`Program.cs` reads `GptOptions` (`GPT` + `OpenAI` config) and runs one of:

- **Completion** — prompt in, completion out; optional embedding-file context via `ParameterMapping`.
- **Chat** — `InstructionChatBot` REPL.
- **Embed** — chunk files/directories (`Embeddings/Document.cs`) and rank with cosine similarity.
- **Discord** — `InstructionGPT` bot: per-channel history, `/gptcli` slash tree, mention tool-routing, and a module pipeline.

OpenAI I/O lives in `OpenAILogic.cs`. Default model is `gpt-6-astra`. GPT-6 requests omit sampling params the model rejects; tool calling for GPT-6 goes through the Responses API.

## Layout

- `Program.cs`, `OpenAILogic.cs`, `GptOptions.cs`, `ParameterMapping.cs` — entry, API, options.
- `Chat/InstructionChatBot.cs` — non-Discord chat loop.
- `Chat/Discord/` — Discord host, slash/tool commands, Infobot, module contracts.
- `Chat/Dnd/` — rules engine used by the DnD module (no Discord types).
- `Embeddings/` — `Document`, `CosineSimilarity`.
- `modules/examples/` — plugin sources. Built DLLs copy to `modules/`.
- `tests/` — xUnit; never live OpenAI or Discord.
- `deploy/` — install/deploy helpers. `bin/` and `obj/` are build output.

Per-channel Discord state is stored under `channels/<guild>_<id>/<channel>_<id>/`. Do not persist API keys or bot tokens in that JSON (`GptOptions.ApiKey` / `BotToken` are `[JsonIgnore]`).

## Commands

```bash
dotnet test ./GPT-CLI.sln -c Release
dotnet publish gpt.csproj -c Release -r linux-x64 -o $HOME/bin --self-contained true -p:PublishSingleFile=true
# Windows equivalent: localinstall.bat / win-x64 publish
# Version, commit, tag, push: release.bat <version>
```

`test.bat` is a Windows **publish** helper, not the unit-test runner.

After changing an example module, rebuild/deploy its DLL (`modules/examples/<Name>/build-module.sh` or `modules/build-deploy-modules.sh`) and restart the Discord process. Host `gpt.csproj` excludes `modules/**/*.cs`.

## Coding

- C# `net10.0`, PascalCase types/methods, camelCase locals/fields.
- Put new files next to related code (Discord host vs `Chat/Dnd` engine vs example module).
- No repo formatter; do not reflow unrelated code.
- Conventional Commits (`feat(discord): …`, `fix(dnd): …`).
- PRs: summary, commands run, config/env changes, user-facing before/after.

## Tests

- `GptCli.Tests` — core, OpenAI mapping, Discord host helpers.
- `GptCli.Dnd.Tests` — DnD engine (session + combat).
- `GptCli.Modules.Tests` — example modules.

No live OpenAI or Discord. Use `FixedDiceRoller`, `FakeClock`, `FakeFeatureModule`, `FakeOpenAIService`, `StubHttpMessageHandler`.

## Config

`appsettings.json` or env vars (`OPENAI__APIKEY`, `GPT__PROMPT`, `GPT__MODE`, `GPT__BOTTOKEN`, `DISCORD__MODULESPATH`). Never commit real keys. Placeholders only in docs.

## DnD in one paragraph

Mechanics are deterministic in `Chat/Dnd/` (HP, stats, damage, checks, rest, legal scene transitions). The Discord module in `modules/examples/DndModuleExample` is an adapter: draft mode is GM prep, game mode starts a session state machine and uses the LLM only for narration and mapping player text onto listed options. Read `Chat/Dnd/AGENTS.md` and `modules/examples/DndModuleExample/AGENTS.md` before changing either side.
