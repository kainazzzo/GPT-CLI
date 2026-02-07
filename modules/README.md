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
- Setup/control (slash): `/gptcli dnd status`, `/gptcli dnd mode`, `/gptcli dnd campaigncreate`,
  `/gptcli dnd campaignrefine`, `/gptcli dnd campaignoverwrite`, `/gptcli dnd charactercreate`,
  `/gptcli dnd charactershow`, `/gptcli dnd npccreate`, `/gptcli dnd npclist`, `/gptcli dnd npcshow`,
  `/gptcli dnd npcremove`, `/gptcli dnd ledger`, `/gptcli dnd campaignhistory`
- Live action tags: `!roll`, `!check`, `!save`, `!attack`, `!initiative`, `!endturn`
- Persisted docs per campaign:
  - `campaign.json` (campaign details + generation trigger + prompt tweak chain + revision chain)
  - `ledger.json` (official actions/rolls/outcomes referencing campaign document revision)

## Docker

If you use docker-compose, map a host directory to `/app/modules` so modules persist:

```
- /usr/local/discord/modules:/app/modules
```
