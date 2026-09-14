# Discord host

The bot class is `InstructionGPT` (`InstructionGPT.cs`), built on `DiscordBotBase`. It owns per-channel state, `/gptcli`, mention-based tool routing, and the module pipeline.

## Pipeline

`Modules/DiscordModulePipeline.cs` loads `IFeatureModule` implementations:

- Always: `InfobotModule` from this assembly (id `infobot`).
- Plus every `*.dll` in `Discord:ModulesPath` (default `./modules`) that implements `IFeatureModule`.

Modules are ordered by `DependsOn`. Disabled modules are not dispatched `OnMessageReceived` for that channel (`InstructionGPT.IsModuleEnabled`). Optional `IModuleEnablementHooks` runs when `/gptcli modules enable|disable` flips a module (DnD uses this to stop tick loops and force mode `off`).

Contracts:

- `IFeatureModule` / `FeatureModuleBase` — implement this.
- `GetGptCliFunctions` — one list drives both slash commands and LLM tools (`Commands/GptCliFunction.cs`).
- `GetAdditionalMessageContextAsync` — extra system preambles for the channel LLM.
- `IDiscordModuleHost` — channel state, save, guild match.

## Slash vs tools vs messages

- Slash: `/gptcli …` built from module `GptCliFunction` slash bindings plus host commands (`help`, `set`, `modules`, …).
- Mention tools: user pings the bot; host offers allowed `gptcli_*` tools. DnD filters tools by draft/game/off in **two** places (`InstructionGPT.IsDndToolAllowedForMode` and `DndGameMasterModule.IsDndToolAllowedForMode`) — keep them in sync when adding tools.
- Untagged messages: only modules that opt in (DnD draft/game, Infobot questions, `!poll` / `!pin` / casino bangs).

While DnD is in **game** mode, the module mutes the core bot (`ChannelState.Options.Muted`) so the GM adapter owns the channel. Leaving game restores the previous mute flag. Switching game → draft/off is **slash-only**.

## Channel state

`InstructionGPT.ChannelState` is persisted under the channel directory. Modules may hang extra documents next to it (DnD uses `dnd-lite/`). Do not write secrets into channel JSON.

## GPT-6

Default model `gpt-6-astra`. Chat Completions: omit `temperature` / `top_p` / `logprobs` when the model rejects them. Tool loops for GPT-6 use the Responses API (`OpenAILogic`). DnD draft generation already uses a Responses tool loop.

## Tests

`tests/GptCli.Tests/Discord/` and `tests/GptCli.Tests/Chat/`. No Discord login. Hosted-bot tests use in-memory channel state and fakes.
