# DnD-lite engine

Deterministic rules in `GPT.CLI.Chat.Dnd`. No Discord, no LLM. The Discord module is an adapter only (`modules/examples/DndModuleExample/AGENTS.md`).

**Invariant:** the engine mutates HP, MP, stats, checks, rest, initiative, damage, and legal scene transitions. Callers (including an LLM) may only choose listed options or declare combat actions. Never invent hit/miss/damage in the adapter.

## Layers

```
DndCampaignRunner          session FSM + party HP/MP + encounter wrapper
  ├── DndSceneCatalog      linear scenes from encounter templates
  └── DndEncounterRunner   combat sub-FSM
        └── DndRuleset     mods, AC, to-hit, check DC clamp
```

Dice: `IDiceRoller` / `RandomDiceRoller`. Time: `IClock` / `SystemClock`. Tests inject `FixedDiceRoller` and `FakeClock`.

## Session FSM (`DndGamePhase`)

Started with `StartSession()` (idempotent). Scenes are synthesized if empty.

| Phase | Meaning | Typical options (ids) |
|---|---|---|
| `NotStarted` | No session | — |
| `PartyFormation` | Empty table / everyone sat out | `party` only; sit down via `JoinParty`, start via `Ready` |
| `SessionStart` | Intro/recap | `begin`, `recap`, `party` |
| `Exploration` | Location play | `check:search`, `social`, `travel`, `rest`, `combat:{templateId}`, `continue` |
| `Social` | Overlay talk (free to enter) | `check:persuade`, `return`, combat, `continue` |
| `Travel` | Overlay travel | `continue`, `check:navigate`, `rest`, combat, `return` |
| `Check` | Pending ability check | `roll`, `cancel` |
| `Rest` | Camp | `rest:short`, `rest:long`, `return` |
| `Combat` | Delegates to encounter runner | empty scene options; use Attack/Cast/Pass |
| `Aftermath` | After victory | `continue`, `rest`, `travel` |
| `Failed` | Party wipe | `rest:long` (revive), `end` |
| `Complete` | Finale | none |

Overlays (`Social`, `Travel`, `Rest`, `Check`) remember `PreviousPhase` and do not change `CurrentSceneId`. `ChooseOption` accepts option id, 1-based index, unique label, or unique substring.

Session table rounds (exploration/social/travel): `begin` / `continue` / `travel` / `rest` / `combat` consume one action. `social`, `recap`, `party`, and `return` are free. Failed options release the current-actor lock. Questions are adapter-side and must not call `ChooseOption`.

`begin` / `continue` call `MoveToScene(NextSceneId)`. Entering a `Combat` scene starts that template via `StartEncounter`. Victory → aftermath scene. Defeat → `Failed` (and campaign `IsFailed`).

`StartEncounter(templateId)` still works **without** a session (existing tests / slash override). If a session is running, it sets phase `Combat` and binds the combat scene.

## Scene synthesis (`DndSceneCatalog`)

From encounter templates in **registration order** (not alphabetical):

1. `intro`
2. For each template `{id}`: `{id}-approach` (Exploration, `Scene` text, linked encounter), `{id}-combat`, `{id}-aftermath` (`Rewards` text)
3. `finale`

No templates → `intro` + `finale` only.

`DndEncounterTemplate` now has optional `Scene` and `Rewards`. The Discord adapter copies those from `DndLiteEncounterTemplateDocument` in `BuildRunnerTemplates`.

## Checks and rest

- `RequestCheck(actorId, stat, dc, reason)` from Exploration/Social/Travel. DC clamped 8–18 (`DndRuleset.DefaultCheckDc` is 12).
- Roll: `d20 + GetCheckModifier(stats, DndCheckStat)` vs DC. Then return to `PreviousPhase`. Checks deal no damage.
- Stats: `Str`, `Def`, `Dex`, `SpellPower`, `Luck`. Same `GetStatMod` as combat (`floor((stat-10)/2)`).
- Short rest: half of missing HP/MP (integer divide). Blocked in combat, while failed, or in Check/Complete.
- Long rest: full HP/MP. Blocked in an active encounter. `LongRest(clearFailure: true)` from `Failed` revives, clears failure, and returns to that encounter's approach scene.

## Combat (`DndEncounterRunner`)

Actors have `DndStats`, `MaxHp`/`Hp`, `MaxMp`/`Mp`. Sides: Party vs Enemy (one boss + adds).

Phases: `NotStarted → InCombat → Completed` (initiative is no longer an action gate).

- Start: open **party round**. `CurrentActorId` empty means anyone who has not acted may declare.
- Enemies do **not** act after each PC. `Ready(actorId)` (must have acted) or `EndPartyRound()` / `TimeoutIdle()` closes the round: unacted PCs sit out, then all enemies act vs **participating** PCs (acted or joined this round).
- Done quorum is `max(1, ceil(activePcCount / 3))` (1 of 2). `npc:` actors do not vote.
- `AddPartyActor` / campaign `JoinParty` seats a late joiner mid-fight (targetable this enemy phase).
- To-hit d20 + Str or SpellPower vs `BaseDefense + Def mod + Dex mod` (default base 10). Natural 20 is a crit (extra damage die).
- Damage: 1d8+Str (attack) or 1d10+SpellPower (spell). Applied to target HP; 0 HP = down.
- Completion: no living enemies → `victory`; no living party → `defeat`. Empty table is formation, not a wipe.

`DndTurnResult.NextRequest` tells the adapter what is needed: `NeedInitiativeRolls`, `NeedAction`, `NeedRolls`, `Completed`.

Campaign runner copies party HP/MP into the encounter and reconciles after every step. Party wipe marks the campaign failed until a clearing long rest.

## Persistence

Serializable DTOs in `RunnerStateModels.cs` (`DndCampaignRunnerState`, nested `DndEncounterRunnerState`, `DndSessionRunnerState`). `ToState` / `FromState` must round-trip session phase, scene id, pending check, and active encounter.

## Public session API (`DndCampaignRunner`)

- `StartSession`, `ChooseOption(input, actorId)`, `JoinParty`, `Ready`, `BeginPlayFromFormation`, `EndPartyRound`, `TimeoutIdle`, `RequestCheck`, `ResolveCheck`, `CancelCheck`, `ShortRest`, `LongRest`
- Empty party is legal. `StartSession` with no seated PC enters `PartyFormation`.
- `GetSessionSnapshot` / `Campaign.Session` on `DndCampaignSnapshot`
- `FormatOptionList` — numbered labels for Discord
- Combat wrappers unchanged: `StartEncounter`, `Attack`, `CastSpell`, `Pass`, `RollAll`, …

## Tests

`tests/GptCli.Dnd.Tests/` — see `tests/AGENTS.md`. Add engine tests here, not in the Discord module suite, unless the change is parser/tool allow-list UX.
