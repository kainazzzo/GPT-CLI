# Changelog

Notable user-facing changes. Entries are ordered newest-first.

## 2026-02-11

### DnD module: prep generation moved to Responses API agent loop

- Migrated draft/prep generation from chat-completions to a Responses API tool-calling loop for:
- `campaigncreate` (now a two-stage build: story/encounters, then party sheets/unassigned PCs).
- `draftupdate` (campaign rewrite now uses tool-driven draft output).
- `charactercreate` and `npccreate` sheet generation.
- Added strict bounded retries and explicit failure reasons when the agent loop cannot complete valid output.
- Added detailed Responses pipeline logs (rounds, tool calls, response ids, and timing).
- Updated package reference from `Betalgo.OpenAI` to `Betalgo.Ranul.OpenAI` (floating to latest).

Files:
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`
- `gpt.csproj`

## 2026-02-10

### DnD module: draft/game improvements, party removal fixes, and longer pass timeout

- Added chat-history context to DnD draft/game LLM calls (so followups like "no I meant X" work), and record module replies into channel history.
- Fixed natural-language NPC party removal loops by including party roster (actor ids + names) in the LLM context and adding `partyremovenpc`.
- Game mode is less verbose and more in-character.
- Increased default auto-pass timeout to 30 minutes and added `/gptcli dnd passtimeout` (also works via natural language).
- In game mode, switching back to draft/off is slash-only (natural language/mention tool routing cannot change mode).
- Reduced campaign bootstrap token budget and tightened generation prompt constraints to avoid long/empty responses.
- Campaign bootstrap: enable JSON response format and correctly read structured content parts (`ContentCalculated`) to avoid false "empty content" failures (uses `max_completion_tokens`, not `max_tokens`, for gpt-5.* models).
- Campaign bootstrap: force a `dnd_campaign_package` tool call and parse JSON from tool arguments to avoid "reasoning-only" empty assistant content.
- Added verbose logging of the exact LLM prompt messages used by DnD auto-routing.
- Persist module enable/disable state per channel and harden Discord slash command descriptions (length limits) and DnD slash tree sizing.

Files:
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`
- `Chat/Discord/InstructionGPT.cs`
- `Chat/Discord/Commands/GptCliFunction.cs`
- `README.md`
