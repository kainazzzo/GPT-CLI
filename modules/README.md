# Discord Modules

This folder is scanned at startup for `*.dll` assemblies containing `IFeatureModule` implementations.
The bot loads every module it finds, orders them by declared dependencies, and registers any
`/gptcli` subcommands the modules contribute.

## How it works

- Default path is `./modules` (relative to the working directory).
- Override with config: `Discord:ModulesPath` or env var `DISCORD__MODULESPATH`.
- Modules are discovered by scanning the folder for `*.dll` and loading them into the default
  AssemblyLoadContext.
- Any module that implements `GPT.CLI.Chat.Discord.Modules.IFeatureModule` is instantiated via DI.
- Slash command contributions are merged into `/gptcli` and conflicts are logged + skipped.

## Add a module

1) Build a class library that targets `net10.0`.
2) Reference `gpt.csproj` (or the published GPT-CLI assembly) so you can implement `IFeatureModule`.
3) Copy the compiled `.dll` into this `modules/` folder.
4) Restart the bot.

Example (local build):

```bash
dotnet build path/to/MyModule/MyModule.slnx -c Release
cp path/to/MyModule/bin/Release/net10.0/MyModule.dll ./modules/
```

## Module conventions

- Use a unique top-level subcommand name (e.g., `/gptcli casino`).
- If extending an existing group (e.g., `set`), your subcommand name must be unique there too.
- Handle your own persistence as needed.
- Prefer short, stable module IDs for dependency chains.

## Example module

A working module example lives here:

- Source: `modules/examples/CasinoModuleExample`
- Build: `modules/examples/CasinoModuleExample/build-module.sh` or `build-module.bat`
- Output: `modules/CasinoModuleExample.dll`

Another example:

- Source: `modules/examples/DndModuleExample`
- Build: `modules/examples/DndModuleExample/build-module.sh`
- Output: `modules/DndModuleExample.dll`
- Modes:
  - `off`: module ignores messages
  - `draft`: natural language campaign drafting (does not write the campaign catalog)
  - `game`: gameplay/encounters (finalizes the active draft into the catalog/run on entry)
- Primary workflow (natural language):
  - Enable module in your channel: `/gptcli modules enable module:dnd`
  - Switch to `draft`, then describe your campaign in plain English (name + theme).
  - Keep iterating in `draft` by asking for story changes ("rewrite the hook...", "add a rival faction...").
  - Switch to `game` to finalize and start playing. Players sit down with a sheet and `I'll join`.
- Slash commands (draft):
  - `/gptcli dnd status`, `/gptcli dnd mode value:off|draft|game`
  - `/gptcli dnd campaigncreate` (build/overwrite the active draft)
  - `/gptcli dnd draftupdate` (apply a modification prompt to the existing draft story)
  - `/gptcli dnd partyshow` (live seating in game; no draft roster)
  - `/gptcli dnd charactercreate`, `/gptcli dnd charactershow`
  - `/gptcli dnd npccreate`, `/gptcli dnd npclist`, `/gptcli dnd npcshow`, `/gptcli dnd npcremove`
  - `/gptcli dnd campaignlist`, `/gptcli dnd campaignstart`, `/gptcli dnd encounterlist`
  - `/gptcli dnd campaignfinalize` (optional; game mode also finalizes)
- Slash commands (game):
  - `/gptcli dnd encounterstart`, `/gptcli dnd encounterstatus`, `/gptcli dnd encounterend`
  - `/gptcli dnd ledger`
  - `/gptcli dnd liveconfig` (game-mode ticking/timeouts)
- Game-mode actions are typically driven by natural language (auto-routing) or by `!` tags:
  - `!state`, `!targets`, `!attack <target>`, `!cast <target>`, `!pass`, `!rollall`, `!ledger [n]`
- Persistence (per Discord channel state dir):
  - Drafts: `dnd-lite/drafts/<campaign-slug>/draft.json`
  - Final: `dnd-lite/campaigns/<campaign-slug>/campaign.json` and `dnd-lite/runs/<campaign-slug>/...`

## Docker

`docker compose build` compiles the example modules into the discord image at `/app/modules`. Do not mount a host modules directory over that path unless you also rebuild those DLLs; a stale mount is how an old plugin can keep running after a compose rebuild.
