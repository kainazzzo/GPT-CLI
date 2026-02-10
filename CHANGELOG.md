# Changelog

Notable user-facing changes. Entries are ordered newest-first.

## 2026-02-10

### DnD module: draft/game improvements, party removal fixes, and longer pass timeout

- Added chat-history context to DnD draft/game LLM calls (so followups like "no I meant X" work), and record module replies into channel history.
- Fixed natural-language NPC party removal loops by including party roster (actor ids + names) in the LLM context and adding `partyremovenpc`.
- Game mode is less verbose and more in-character.
- Increased default auto-pass timeout to 30 minutes and added `/gptcli dnd passtimeout` (also works via natural language).
- In game mode, switching back to draft/off is slash-only (natural language/mention tool routing cannot change mode).
- Reduced campaign bootstrap token budget and tightened generation prompt constraints to avoid long/empty responses.
- Added verbose logging of the exact LLM prompt messages used by DnD auto-routing.
- Persist module enable/disable state per channel and harden Discord slash command descriptions (length limits) and DnD slash tree sizing.

Files:
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`
- `Chat/Discord/InstructionGPT.cs`
- `Chat/Discord/Commands/GptCliFunction.cs`
- `README.md`
