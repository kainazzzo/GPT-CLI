# GptCli.Dnd.Tests

xUnit test suite for the DnD combat/campaign engine in `GPT.CLI.Chat.Dnd`.

## How To Run

From the repo root:

```bash
dotnet test ./tests/GptCli.Dnd.Tests/GptCli.Dnd.Tests.csproj -c Release
```

(You can also run `dotnet test ./GPT-CLI.sln -c Release` to run everything in the solution.)

## What's Being Tested

These tests exercise:
- `Chat/Dnd/DndEncounterRunner.cs`: encounter state machine (initiative, turn order, actions, rolls, auto-running enemy turns, victory lock).
- `Chat/Dnd/DndCampaignRunner.cs`: campaign wrapper (party HP/MP carrying into encounters, reconciliation back into campaign, failure state).
- Persistence roundtrips for both runners (serialize to state and restore from state).

### Test Doubles

- `FixedDiceRoller` (`tests/GptCli.Dnd.Tests/TestDoubles.cs`)
  - Provides deterministic dice results by dequeuing a fixed sequence.
  - Values are clamped to `[1..sides]`.
- `FakeClock` (`tests/GptCli.Dnd.Tests/TestDoubles.cs`)
  - Provides a stable `UtcNow` used in ledger entries.

## Test Cases

### `DndEncounterRunnerTests` (`tests/GptCli.Dnd.Tests/DndEncounterRunnerTests.cs`)

- `StartEncounter_enqueues_initiative_for_all_actors`
  - Starting an encounter moves to `NeedInitiative` and enqueues initiative rolls for all alive actors.
- `Initiative_resolution_sets_turn_order_and_current_actor`
  - Resolving initiative transitions to `InCombat`, sets `TurnOrder`, and sets `CurrentActorId` to the highest initiative.
- `DeclareAttack_requires_actors_turn_and_no_pending_rolls`
  - Declaring an attack fails if the encounter is still in initiative / not ready for actions.
- `Attack_miss_advances_turn_and_logs_resolution`
  - A missed attack logs a MISS entry and the engine advances turns; enemy turns auto-run; control returns to the player.
- `Attack_hit_applies_damage_and_logs_hp_delta`
  - A successful hit applies damage (boss HP decreases) and emits a `DamageApplied` ledger entry.
- `CastSpell_spends_mp_and_errors_when_insufficient`
  - Casting a spell spends MP (ledger includes `MpSpent`), and casting fails once MP is insufficient.
- `Enemy_turns_autorun_after_player_action_and_are_logged`
  - After the player resolves an action, enemy turns are simulated automatically, logged, and can reduce party HP.
- `Victory_locks_engine_and_rejects_further_actions`
  - Killing the boss completes the encounter with `CompletionReason == "victory"` and further actions are rejected.

### `DndCampaignRunnerTests` (`tests/GptCli.Dnd.Tests/DndCampaignRunnerTests.cs`)

- `StartEncounter_uses_campaign_party_current_hp_mp_as_starting_values`
  - Starting an encounter uses the campaign party's current HP/MP as the encounter actor starting values.
- `Encounter_auto_enemy_turn_reconciles_into_campaign_party_state`
  - Encounter results (including auto-run enemy turns) reconcile back into the campaign party state (e.g., HP decreases).
- `Party_wipe_marks_campaign_failed_and_blocks_actions_until_cleared`
  - If the party is wiped, the campaign is marked failed (`FailureReason == "defeat"`), actions are blocked, and `LongRest(clearFailure: true)` clears failure and restores HP.

### `DndPersistenceRoundtripTests` (`tests/GptCli.Dnd.Tests/DndPersistenceRoundtripTests.cs`)

- `EncounterRunner_ToState_FromState_preserves_pending_rolls_and_can_continue`
  - `DndEncounterRunner.ToState()` + `FromState(...)` preserves encounter snapshot and pending rolls, and the restored runner can continue resolving via `RollAll()`.
- `CampaignRunner_ToState_FromState_preserves_active_encounter_and_reconciles_party`
  - `DndCampaignRunner.ToState()` + `FromState(...)` preserves an active encounter; continuing with `RollAll()` progresses the encounter and reconciles party HP correctly.

