using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndEncounterRunnerTests
{
    private static DndEncounterDefinition BasicEncounter()
    {
        var party = new[]
        {
            new DndActorDefinition(
                ActorId: "p1",
                Name: "Hero",
                Side: DndSide.Party,
                IsBoss: false,
                Stats: new DndStats(Str: 14, Def: 12, Dex: 12, SpellPower: 10, Luck: 10),
                MaxHp: 20,
                MaxMp: 10,
                StartingHp: 20,
                StartingMp: 10)
        };

        var boss = new DndActorDefinition(
            ActorId: "b1",
            Name: "Boss",
            Side: DndSide.Enemy,
            IsBoss: true,
            Stats: new DndStats(Str: 12, Def: 10, Dex: 10, SpellPower: 10, Luck: 10),
            MaxHp: 12,
            MaxMp: 0,
            StartingHp: 12,
            StartingMp: 0);

        return new DndEncounterDefinition(party, boss, Adds: Array.Empty<DndActorDefinition>());
    }

    [Fact]
    public void StartEncounter_enqueues_initiative_for_all_actors()
    {
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: new FixedDiceRoller(new[] { 10, 5 }), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        var res = r.StartEncounter();
        Assert.True(res.Ok);
        Assert.Equal(DndEncounterPhase.NeedInitiative, res.State.Phase);
        Assert.Equal(2, res.PendingRolls.Count);
        Assert.All(res.PendingRolls, pr => Assert.Equal(DndPendingRollKind.Initiative, pr.Kind));
    }

    [Fact]
    public void Initiative_resolution_sets_turn_order_and_current_actor()
    {
        // p1 rolls 15, b1 rolls 5.
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: new FixedDiceRoller(new[] { 15, 5 }), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        var res = r.RollAll();
        Assert.True(res.Ok);
        Assert.Equal(DndEncounterPhase.InCombat, res.State.Phase);
        Assert.Equal("p1", res.State.CurrentActorId);
        Assert.Equal(new[] { "p1", "b1" }, res.State.TurnOrder);
    }

    [Fact]
    public void DeclareAttack_requires_actors_turn_and_no_pending_rolls()
    {
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: new FixedDiceRoller(new[] { 10, 5 }), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        // Still in initiative phase => should fail.
        var err = r.DeclareAttack("p1", "b1");
        Assert.False(err.Ok);
        Assert.Contains("Initiative", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attack_miss_advances_turn_and_logs_resolution()
    {
        // Initiative: p1=15, b1=5. Attack roll: p1 rolls 1 (auto miss vs DEF 10).
        var dice = new FixedDiceRoller(new[] { 15, 5, 1, 10, 4 }); // includes enemy follow-up rolls
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll(); // initiative -> p1's turn

        var declared = r.DeclareAttack("p1", "b1");
        Assert.True(declared.Ok);
        Assert.Single(declared.PendingRolls.Where(p => p.Kind == DndPendingRollKind.AttackToHit));

        var resolved = r.RollAll();
        Assert.True(resolved.Ok);

        // After miss, enemies should auto-run and we should be back to p1 turn.
        Assert.Equal("p1", resolved.State.CurrentActorId);
        Assert.Contains(resolved.NewLedgerEntries, e => e.Message.Contains("MISS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Attack_hit_applies_damage_and_logs_hp_delta()
    {
        // Initiative: p1=15, b1=5.
        // Attack roll: 20 (crit) -> damage rolls: 4, 5 (2 dice due to crit) + StrMod(14)=+2
        // Provide one extra die so the boss can auto-take a turn (miss).
        var dice = new FixedDiceRoller(new[] { 15, 5, 20, 4, 5, 1 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();

        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();
        Assert.True(res.Ok);

        var boss = res.State.Actors["b1"];
        Assert.True(boss.Hp < boss.MaxHp);
        Assert.Contains(res.NewLedgerEntries, e => e.Kind == DndLedgerKind.DamageApplied);
    }

    [Fact]
    public void CastSpell_spends_mp_and_errors_when_insufficient()
    {
        var enc = BasicEncounter();
        // Use seeded RNG here since RollAll() will also simulate enemy turns.
        var r = new DndEncounterRunner(enc, diceRoller: new RandomDiceRoller(seed: 123), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll(); // into combat

        // Spend MP repeatedly by declaring spells; we don't need to resolve to-hit for this test.
        for (var i = 0; i < 3; i++)
        {
            var s = r.DeclareCastSpell("p1", "b1");
            Assert.True(s.Ok);
            Assert.Contains(s.NewLedgerEntries, e => e.Kind == DndLedgerKind.MpSpent);
            r.RollAll(); // clear pending rolls quickly (will resolve to-hit/dmg with random; fine)
        }

        // Now MP should be low enough that next cast fails (MaxMp=10, cost=3 => after 3 casts: 1 MP left).
        var fail = r.DeclareCastSpell("p1", "b1");
        Assert.False(fail.Ok);
        Assert.Contains("MP", fail.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enemy_turns_autorun_after_player_action_and_are_logged()
    {
        // Initiative p1 first; p1 attack miss; enemy attack hit + damage.
        var dice = new FixedDiceRoller(new[] { 15, 5, 1, 20, 6, 3 }); // enemy to-hit 20, damage 6
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();

        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();
        Assert.True(res.Ok);

        Assert.Contains(res.NewLedgerEntries, e => e.Message.Contains("Boss attacks", StringComparison.OrdinalIgnoreCase));
        Assert.True(res.State.Actors["p1"].Hp < res.State.Actors["p1"].MaxHp);
    }

    [Fact]
    public void Victory_locks_engine_and_rejects_further_actions()
    {
        // Make sure we can kill boss in one crit: damage big enough.
        // Attack crit: 20; damage dice: 8, 8 (+2) => 18, boss HP=12 => dead.
        var dice = new FixedDiceRoller(new[] { 15, 5, 20, 8, 8 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();

        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();
        Assert.True(res.Ok);
        Assert.True(res.State.IsCompleted);
        Assert.Equal("victory", res.State.CompletionReason);

        var fail = r.DeclareAttack("p1", "b1");
        Assert.False(fail.Ok);
    }

    [Fact]
    public void Pass_advances_turn_and_autoruns_enemy()
    {
        // Initiative: p1=15, b1=5. Enemy follow-up miss (to-hit 1).
        var dice = new FixedDiceRoller(new[] { 15, 5, 1 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();

        var res = r.Pass("p1");
        Assert.True(res.Ok);
        Assert.Contains(res.NewLedgerEntries, e => e.Message.Contains("passes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("p1", res.State.CurrentActorId);
        Assert.Equal(20, res.State.Actors["p1"].Hp);
    }

    [Fact]
    public void DeclareAttack_unknown_actor_fails()
    {
        var r = ReadyCombat();
        var err = r.DeclareAttack("nope", "b1");
        Assert.False(err.Ok);
        Assert.Contains("Actor", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclareAttack_not_actors_turn_fails()
    {
        var r = ReadyCombat();
        var err = r.DeclareAttack("b1", "p1");
        Assert.False(err.Ok);
        Assert.Contains("turn", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclareAttack_unknown_target_fails()
    {
        var r = ReadyCombat();
        var err = r.DeclareAttack("p1", "ghost");
        Assert.False(err.Ok);
        Assert.Contains("Target", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Starting_hp_mp_of_zero_or_negative_default_to_max()
    {
        var party = new[]
        {
            new DndActorDefinition(
                ActorId: "p1",
                Name: "Hero",
                Side: DndSide.Party,
                IsBoss: false,
                Stats: new DndStats(Str: 14, Def: 12, Dex: 12, SpellPower: 10, Luck: 10),
                MaxHp: 20,
                MaxMp: 10,
                StartingHp: 0,
                StartingMp: -1)
        };

        var boss = new DndActorDefinition(
            ActorId: "b1",
            Name: "Boss",
            Side: DndSide.Enemy,
            IsBoss: true,
            Stats: new DndStats(Str: 12, Def: 10, Dex: 10, SpellPower: 10, Luck: 10),
            MaxHp: 12,
            MaxMp: 0,
            StartingHp: 12,
            StartingMp: 0);

        var r = new DndEncounterRunner(
            new DndEncounterDefinition(party, boss, Adds: Array.Empty<DndActorDefinition>()),
            diceRoller: new FixedDiceRoller(new[] { 10, 5 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));

        var res = r.StartEncounter();
        Assert.True(res.Ok);
        Assert.Equal(20, res.State.Actors["p1"].Hp);
        Assert.Equal(10, res.State.Actors["p1"].Mp);
    }

    [Fact]
    public void Adds_are_included_in_initiative()
    {
        var add = new DndActorDefinition(
            ActorId: "a1",
            Name: "Goblin",
            Side: DndSide.Enemy,
            IsBoss: false,
            Stats: new DndStats(Str: 10, Def: 10, Dex: 10, SpellPower: 10, Luck: 10),
            MaxHp: 6,
            MaxMp: 0,
            StartingHp: 6,
            StartingMp: 0);

        var enc = new DndEncounterDefinition(
            BasicEncounter().Party,
            BasicEncounter().Boss,
            Adds: new[] { add });

        var r = new DndEncounterRunner(
            enc,
            diceRoller: new FixedDiceRoller(new[] { 15, 5, 8 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));

        var started = r.StartEncounter();
        Assert.True(started.Ok);
        Assert.Equal(3, started.PendingRolls.Count);

        var res = r.RollAll();
        Assert.True(res.Ok);
        Assert.Contains("a1", res.State.TurnOrder);
        Assert.True(res.State.Actors.ContainsKey("a1"));
    }

    [Fact]
    public void Party_wipe_completes_encounter_as_defeat()
    {
        var party = new[]
        {
            new DndActorDefinition(
                ActorId: "p1",
                Name: "Hero",
                Side: DndSide.Party,
                IsBoss: false,
                Stats: new DndStats(Str: 14, Def: 12, Dex: 12, SpellPower: 10, Luck: 10),
                MaxHp: 20,
                MaxMp: 10,
                StartingHp: 1,
                StartingMp: 10)
        };

        var enc = new DndEncounterDefinition(party, BasicEncounter().Boss, Adds: Array.Empty<DndActorDefinition>());
        // Initiative p1 first, player miss, enemy crit + damage.
        var dice = new FixedDiceRoller(new[] { 15, 5, 1, 20, 8, 8 });
        var r = new DndEncounterRunner(enc, diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();
        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();

        Assert.True(res.Ok);
        Assert.True(res.State.IsCompleted);
        Assert.Equal("defeat", res.State.CompletionReason);
        Assert.False(res.State.Actors["p1"].IsAlive);
    }

    private static DndEncounterRunner ReadyCombat()
    {
        var r = new DndEncounterRunner(
            BasicEncounter(),
            diceRoller: new FixedDiceRoller(new[] { 15, 5 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.RollAll();
        return r;
    }
}
