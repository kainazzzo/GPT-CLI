# Discord feature modules

Plugins implementing `GPT.CLI.Chat.Discord.Modules.IFeatureModule`. The host scans `./modules` (or `Discord:ModulesPath` / `DISCORD__MODULESPATH`) for `*.dll` and loads them into the default `AssemblyLoadContext`. Example **sources** live in `modules/examples/`; they are not compiled into `gpt.csproj`.

User-facing overview: `modules/README.md`.

## Nested module guides

- `examples/DndModuleExample/AGENTS.md`
- `examples/CasinoModuleExample/AGENTS.md`
- `examples/PollModuleExample/AGENTS.md`
- `examples/PinboardModuleExample/AGENTS.md`
- `examples/WelcomeModuleExample/AGENTS.md`

Built-in (host assembly, not this folder): Infobot (`infobot`).

## Authoring rules

1. Class library `net10.0`, reference `gpt.csproj`, extend `FeatureModuleBase`.
2. Stable unique `Id` (used for `/gptcli modules enable module:<id>`).
3. Unique `/gptcli` top-level group name (`dnd`, `casino`, `poll`, `pin`, `welcome`).
4. Prefer `GetGptCliFunctions` so slash and LLM tools stay one list. Older modules still handle slash in `OnInteractionAsync` + `GetSlashCommandContributions`.
5. Persist under the channel state directory; never store API keys.
6. Gate on `InstructionGPT.IsModuleEnabled(channel, Id)`.
7. After code changes: `build-module.sh` (or `modules/build-deploy-modules.sh`) and restart the bot.

## Enablement

Per-channel. Disabled modules should be invisible to the LLM (no preamble, no tools). `IModuleEnablementHooks` is optional cleanup on disable (DnD uses it).

## Tests

`tests/GptCli.Modules.Tests/<Module>/`. Domain helpers and parsers only — no Discord login, no OpenAI.
