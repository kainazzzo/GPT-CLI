# Changelog

Notable user-facing changes. Entries are ordered newest-first.

## 2026-09-14

### OpenAI I/O: official SDK only

- Removed `Betalgo.Ranul.OpenAI`. Chat Completions and embeddings now use the official OpenAI .NET SDK (`ChatClient` / `EmbeddingClient`).
- Discord modules and persisted chat history use first-party `ChatMessage` / request DTOs instead of Betalgo types.
- GPT-5.6 chat and GPT-6 tools still use the existing Responses API mapper; strict `json_schema` still uses `ResponsesClient`.

### Draft proposals: official OpenAI structured outputs

- Additive draft NPC/monster/location proposals now request strict `json_schema` through the official OpenAI .NET SDK Responses client instead of prompt-only JSON on Betalgo Chat Completions.
- Proposal Discord cards no longer clip encounter scenes to 160 characters; the reply can chunk, and the structured-output token cap is higher.
- Draft monster/NPC/location proposals honor “give me 3” / “more than 2”; they no longer always invent two encounters or treat “more than 2” as picking item 2.
- Draft and game untagged chat now ask the GPT model to parse intent from conversation (including pending numbered proposals) before keyword/regex handlers.
- Draft location proposals salvage truncated JSON (Club Calor-style cutoffs). `mapMarkdown` stays in the strict schema (empty string allowed) so OpenAI accepts the format.

### Default model: GPT-5.6 Sol

- Updated the default text and vision model to `gpt-5.6-sol`.
- Persisted `gpt-5` / `gpt-5.2` / `gpt-6-astra` channel overrides, and the misnamed `gpt-6-sol` leftover, now resolve to the current default (`gpt-5.6*` is kept).

## 2026-09-09

### Default model: GPT-6 Astra

- Updated the default text and vision model to OpenAI's current flagship, `gpt-6-astra`.
- Chat Completions requests now omit sampling parameters (`temperature`, `top_p`, `logprobs`) that GPT-6 Astra rejects.
- Tool-calling requests for GPT-6 models go through the Responses API, which GPT-6 Astra requires for tools.

Files:
- `GptOptions.cs`
- `OpenAILogic.cs`
- `appsettings.json`
- `deploy/appsettings.json`
- `Properties/launchSettings.json`
- `Dockerfile.discord`
- `README.md`
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`

## 2026-02-13

### Discord integration and module-flow expansion

- Expanded Discord module integration plumbing and command flow across core/chat modules.
- Updated module contracts/pipeline wiring (`IFeatureModule`, `FeatureModuleBase`, `DiscordModulePipeline`) and command mapping behavior.
- Refined Discord chat/runtime state handling and OpenAI parameter mapping touchpoints used by modules.
- Updated example modules (DND/Casino/Poll) to align with the expanded module flow.

Files:
- `Chat/Discord/Commands/GptCliFunction.cs`
- `Chat/Discord/InstructionGPT.cs`
- `Chat/Discord/Modules/DiscordModulePipeline.cs`
- `Chat/Discord/Modules/FeatureModuleBase.cs`
- `Chat/Discord/Modules/IFeatureModule.cs`
- `Chat/Discord/Modules/InfobotModule.cs`
- `Chat/InstructionChatBot.cs`
- `OpenAILogic.cs`
- `ParameterMapping.cs`
- `Program.cs`
- `modules/examples/CasinoModuleExample/CasinoModuleExample/CasinoGameModule.cs`
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`
- `modules/examples/PollModuleExample/PollModuleExample/PollModule.cs`

### DnD draft mode: deterministic state handling + leaner LLM usage

- Added a stricter delineation between state management and LLM generation in draft mode:
  - deterministic handlers now process state edits first (party changes, confirmations, pass-timeout parsing, and clarifications),
  - LLM chat fallback is conversation-only (no tool calls) when applicable.
- Added pending draft action execution path for confirmations (`confirm`/`cancel`) across campaign create overwrite, draft updates, and risky party edits.
- Reduced draft chat token pressure:
  - compact chat-only context profile (smaller history window and reduced prompt size),
  - smaller campaign excerpt inclusion for draft auto-route context,
  - token-limit failure fallback to a deterministic conversational reply.
- Stopped leaking tool narration/JSON into user-facing draft replies by sanitizing model narration in tool-response paths.
- Kept Discord progress updates sparse and conversational (phase-based, throttled status updates).
- Reduced sheet-generation campaign context payload so character/NPC generation does not require large draft context.

Files:
- `modules/examples/DndModuleExample/DndModuleExample/DndGameMasterModule.cs`

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
