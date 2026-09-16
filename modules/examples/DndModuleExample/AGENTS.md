# DnD Discord module (`dnd`)

Adapter between Discord and the engine in `Chat/Dnd/` (read that `AGENTS.md` first). Source: `DndModuleExample/DndGameMasterModule.cs` (large). Id `dnd`. Implements `IModuleEnablementHooks`.

**Do not** grow combat or session rules in this file. Call `DndCampaignRunner`. Add parser/UX/tools here; add HP/transitions/checks in `Chat/Dnd/`.

## Modes (channel, not campaign phase)

| Mode | Behavior |
|---|---|
| `off` | Enabled but ignores messages. Only `/gptcli dnd mode` to leave. |
| `draft` | GM prep. The GPT intent router parses conversation (including pending proposals) and dispatches to draft handlers. Does **not** write the catalog. |
| `game` | Live play. Finalizes draft on entry. Starts session FSM. Core bot is muted. Leave via slash only. Natural language is parsed by the GPT auto-router onto listed options/tools first. |

Natural language cannot switch away from game (in-character safety).

## Game-mode contract

On `/gptcli dnd mode value:game`:

1. Finalize draft → catalog if needed (`TryFinalizeDraftOnGameSwitchAsync`).
2. `EnsureGameSessionAsync` → `DndCampaignRunner.StartSession()` if needed (empty party is allowed; session starts in `PartyFormation`).
3. Reply with `RenderSessionPrompt`: **State**, scene summary, **Party** HP/MP, **Options**, table-round footer.

Every later game reply should keep that footer (`RenderTurnResult` / `WithSessionFooter`).

Player text mapping, in order:

1. Bang commands (`!state`, `!attack`, …) in game only.
2. Table control (`I'll join` / `I'll play` / `ready` / `done`) before the LLM — `HandleJoinOrReadyAsync`. Do not let auto-route map join onto `gptcli_dnd_ready`.
3. LLM auto-route with conversation + **listed options in context**. Prefer `gptcli_dnd_choose`. Sit-down paraphrases use `gptcli_dnd_join`. `gptcli_dnd_encounterstart` is excluded from auto-route (slash override still exists).
4. Deterministic fallback: session start / natural combat verbs / `TryMatchSessionOption`.
5. Short fallback if nothing handled.

LLM may narrate 1–4 sentences and pick a listed option or check. LLM must not invent HP, hit/miss, damage, or extra options.

Combat rolls auto-resolve (`AutoResolvePendingRolls`) according to liveconfig `autoroll_policy` (`npc-only` default, `all`, `never`). Enemy turns are engine-side.

## Session options the parser knows

`TryMatchSessionOption` (tested): numbers, exact id/label, then keywords:

- search/investigate → `check:search`
- talk/speak → `social`
- travel → `travel`
- rest / short rest / long rest
- fight/combat/ambush → `combat:{templateId}`
- continue / begin / recap / roll / cancel / return / end session

## Tools

Draft (subset): `campaigncreate`, `draftupdate`, `campaignfinalize`, character/NPC sheets, `campaignlist`/`start`, `encounterlist`, `passtimeout`, `status`, `mode`. No draft party roster — seating is game-mode `I'll join`.

Game (subset): `choose`, `rest`, `attack`, `cast`, `pass`, `join`, `ready`, `rollall`, encounter status/end, `ledger`, `liveconfig`, `passtimeout`, `status`, party show, sheets. **Not** `campaigncreate`.

Allow-lists are duplicated:

- `DndGameMasterModule.IsDndToolAllowedForMode`
- `InstructionGPT.IsDndToolAllowedForMode`

Update both. Mention-routing also refuses `gptcli_dnd_mode` while in game.

## Persistence (per channel)

Under the channel state dir:

- Draft: `dnd-lite/drafts/<slug>/draft.json`
- Catalog: `dnd-lite/campaigns/<slug>/campaign.json`
- Run: `dnd-lite/runs/<slug>/…` including `DndCampaignRunnerState` (party, templates, active encounter, **session**)
- Sheets: PC/NPC profiles beside the campaign (actor ids `u:{discordUserId}`, `npc:<slug>`). Live party starts empty; players sit down with `I'll join` / `I'll play`. Draft `party.json` is not copied into the run.

`BuildRunnerTemplates` must copy `Scene` and `Rewards` onto `DndEncounterTemplate` so synthesis has flavor text.

## Draft notes

GPT intent router first (conversation + pending-action context). It dispatches to draft handlers (`dnd_route_*`). Regex pending-pick / campaign-create paths are fallback only if the router does not handle the message. Risky edits use `PendingDraftAction`. Campaign/sheet generation uses a bounded Responses API tool loop, not freeform JSON in assistant content. Do not leak tool JSON into user replies (`SanitizeDraftToolNarration`).

**Additive generate (do not rewrite the story):** if the user asks to generate NPCs/monsters/locations/maps, or “examples that fit the campaign”, without `name` + `concept`, **propose** numbered examples from the draft markdown (`dnd_route_propose_content`). The proposal LLM call uses official OpenAI SDK structured outputs (`json_schema` + `strict: true`) via `OpenAILogic.CreateStructuredJsonCompletionAsync`. Never reply with the canned “Include a `concept`” form. Persist only what they pick (`all`, `1 and 3`, `cancel`).

- NPCs → existing `npccreate` sheets after confirm.
- Monsters → append encounter templates (boss/adds + stats). Does not rewrite `CampaignMarkdown`.
- Locations/maps → `DndLiteLocationDocument` list on the draft catalog (`Id`, `Name`, `Summary`, optional ASCII `MapMarkdown`). Copied through finalize.

After a draft lock-in (proposal pick, campaign create, sheet, story update), the reply appends a short **Next:** prompt from current draft inventory (story / locations / encounters) so the GM is not left to ask “what’s next?”. Game replies already include session **Options** / next-request via `RenderSessionPrompt`.

**Finish the rest:** `dnd_route_finish_draft` fills remaining locations/encounters/NPCs, missing roster sheets, and an additive endgame/finale appendix. It should leave **What's left** empty except optional tweaks. Does not rewrite existing story prose.

**Status / what’s left:** `dnd_route_draft_status` renders Discord markdown inventory (`**In place**` / `**What's left**` / `**How to edit**`) instead of a prose paragraph. After each lock-in, the reply includes done + left. After `finish the rest`, it includes a full snapshot plus natural-language edit examples.

Draft chat replies (including `dnd_route_chat_reply`) should use Discord markdown: **bold** headings and `- ` bullets for any list of 2+ items.

`LooksLikeDraftUpdateIntent` must stay false for “no rewrite / just generate / locations / monsters / examples”. Sheet-create with an explicit name and concept still creates immediately.

## Live tick

Game mode runs a per-channel tick loop (`liveconfig`: tick seconds, player-turn timeout default 30 minutes, encounter timeout, npc autoplay). Disable stops the loop.

## Tests

- Engine: `tests/GptCli.Dnd.Tests` (session, combat, persistence).
- Adapter: `tests/GptCli.Modules.Tests/Dnd/DndGameMasterModuleTests.cs` — intent parsers, confirmations, sanitizer, tool allow-list, `TryMatchSessionOption`, `LooksLikeBeginIntent`.

No live OpenAI/Discord.

## Build

Local: `modules/examples/DndModuleExample/build-module.sh` → `modules/DndModuleExample.dll`, then restart.

Docker: `docker compose build` (or `up --build`) compiles this module into the image at `/app/modules`. The discord service does not mount host module DLLs.
