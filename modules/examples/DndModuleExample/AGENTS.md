# DnD Discord module (`dnd`)

Adapter between Discord and the engine in `Chat/Dnd/` (read that `AGENTS.md` first). Source: `DndModuleExample/DndGameMasterModule.cs` (large). Id `dnd`. Implements `IModuleEnablementHooks`.

**Do not** grow combat or session rules in this file. Call `DndCampaignRunner`. Add parser/UX/tools here; add HP/transitions/checks in `Chat/Dnd/`.

## Modes (channel, not campaign phase)

| Mode | Behavior |
|---|---|
| `off` | Enabled but ignores messages. Only `/gptcli dnd mode` to leave. |
| `draft` | GM prep. Deterministic handlers first; LLM is conversation + optional tools for persisted edits. Does **not** write the catalog. |
| `game` | Live play. Finalizes draft on entry. Starts session FSM. Core bot is muted. Leave via slash only. |

Natural language cannot switch away from game (in-character safety).

## Game-mode contract

On `/gptcli dnd mode value:game`:

1. Finalize draft → catalog if needed (`TryFinalizeDraftOnGameSwitchAsync`).
2. `EnsureGameSessionAsync` → `DndCampaignRunner.StartSession()` if needed.
3. Reply with `RenderSessionPrompt`: **State**, scene summary, **Party** HP/MP, **Options**.

Every later game reply should keep that footer (`RenderTurnResult` / `WithSessionFooter`).

Player text mapping, in order:

1. Bang commands (`!state`, `!attack`, …) in game only.
2. `TryHandleDeterministicGameStartAsync` — ensure session; if `SessionStart` and `LooksLikeBeginIntent` (start/begin/play **without** fight/combat), `ChooseOption("begin")`. Does **not** auto-start the first combat template.
3. `TryHandleNaturalGameActionAsync` — if an encounter is live, combat verbs; else `TryMatchSessionOption` + `ChooseOption`.
4. LLM auto-route with **listed options in context**. Prefer `gptcli_dnd_choose`. `gptcli_dnd_encounterstart` is excluded from auto-route (slash override still exists).
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

Draft (subset): `campaigncreate`, `draftupdate`, `campaignfinalize`, party add/remove, character/NPC sheets, `campaignlist`/`start`, `encounterlist`, `passtimeout`, `status`, `mode`.

Game (subset): `choose`, `rest`, `attack`, `cast`, `pass`, `rollall`, encounter status/end, `ledger`, `liveconfig`, `passtimeout`, `status`, party show, sheets. **Not** `campaigncreate`.

Allow-lists are duplicated:

- `DndGameMasterModule.IsDndToolAllowedForMode`
- `InstructionGPT.IsDndToolAllowedForMode`

Update both. Mention-routing also refuses `gptcli_dnd_mode` while in game.

## Persistence (per channel)

Under the channel state dir:

- Draft: `dnd-lite/drafts/<slug>/draft.json` + `party.json`
- Catalog: `dnd-lite/campaigns/<slug>/campaign.json`
- Run: `dnd-lite/runs/<slug>/…` including `DndCampaignRunnerState` (party, templates, active encounter, **session**)
- Sheets: PC/NPC profiles beside the campaign (actor ids `pc:<discordUserId>`, `npc:<slug>`)

`BuildRunnerTemplates` must copy `Scene` and `Rewards` onto `DndEncounterTemplate` so synthesis has flavor text.

## Draft notes

Deterministic first: pending `confirm`/`cancel`, party add/remove, campaign create overwrite, pass-timeout parse. Risky edits use `PendingDraftAction`. Campaign/sheet generation uses a bounded Responses API tool loop, not freeform JSON in assistant content. Do not leak tool JSON into user replies (`SanitizeDraftToolNarration`).

## Live tick

Game mode runs a per-channel tick loop (`liveconfig`: tick seconds, player-turn timeout default 30 minutes, encounter timeout, npc autoplay). Disable stops the loop.

## Tests

- Engine: `tests/GptCli.Dnd.Tests` (session, combat, persistence).
- Adapter: `tests/GptCli.Modules.Tests/Dnd/DndGameMasterModuleTests.cs` — intent parsers, confirmations, sanitizer, tool allow-list, `TryMatchSessionOption`, `LooksLikeBeginIntent`.

No live OpenAI/Discord.

## Build

`modules/examples/DndModuleExample/build-module.sh` → `modules/DndModuleExample.dll`. Restart the bot.
