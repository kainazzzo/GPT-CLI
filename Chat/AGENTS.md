# Chat

Two runtimes share this tree:

1. **CLI chat** — `InstructionChatBot.cs` (mode `Chat`).
2. **Discord bot** — `Discord/` (mode `Discord`), which hosts Infobot plus plugin modules.
3. **DnD-lite engine** — `Dnd/` (no Discord types). The DnD Discord module calls this engine.

## Where to edit

| Change | Go here |
|---|---|
| Slash tree, mention tools, channel state, module load | `Discord/` (`Chat/Discord/AGENTS.md`) |
| Combat, session FSM, rest, checks, persistence DTOs | `Dnd/` (`Chat/Dnd/AGENTS.md`) |
| Discord-facing draft/game UX, tools, file layout | `modules/examples/DndModuleExample/AGENTS.md` |
| Infobot factoids | `Discord/Modules/InfobotModule.cs` |

Do not put Discord `SocketMessage` / slash handling into `Chat/Dnd/`. Do not reimplement HP/dice in the Discord module — call `DndCampaignRunner`.

`InstructionChatBot` trims history by `MaxChatHistoryLength` and is covered in `tests/GptCli.Tests/Chat/`.
