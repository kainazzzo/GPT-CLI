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
    public void StartEncounter_opens_party_round()
    {
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: new FixedDiceRoller(new[] { 10, 5 }), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        var res = r.StartEncounter();
        Assert.True(res.Ok);
        Assert.Equal(DndEncounterPhase.InCombat, res.State.Phase);
        Assert.Equal(string.Empty, res.State.CurrentActorId);
        Assert.Empty(res.PendingRolls);
        Assert.Contains("p1", res.State.ParticipatingActorIds);
    }

    [Fact]
    public void Open_floor_allows_party_member_to_act()
    {
        var r = ReadyCombat();
        Assert.Equal(string.Empty, r.GetState().CurrentActorId);
        var declared = r.DeclareAttack("p1", "b1");
        Assert.True(declared.Ok);
        Assert.Equal("p1", declared.State.CurrentActorId);
    }

    [Fact]
    public void DeclareAttack_requires_no_pending_rolls()
    {
        var r = ReadyCombat();
        var declared = r.DeclareAttack("p1", "b1");
        Assert.True(declared.Ok);
        var err = r.DeclareAttack("p1", "b1");
        Assert.False(err.Ok);
        Assert.Contains("Pending rolls", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attack_miss_stays_in_party_round()
    {
        var dice = new FixedDiceRoller(new[] { 1 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        var declared = r.DeclareAttack("p1", "b1");
        Assert.True(declared.Ok);
        Assert.Single(declared.PendingRolls.Where(p => p.Kind == DndPendingRollKind.AttackToHit));

        var resolved = r.RollAll();
        Assert.True(resolved.Ok);
        Assert.Equal(string.Empty, resolved.State.CurrentActorId);
        Assert.Contains("p1", resolved.State.ActedThisRoundActorIds);
        Assert.DoesNotContain(resolved.NewLedgerEntries, e => e.Message.Contains("Boss attacks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved.NewLedgerEntries, e => e.Message.Contains("MISS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Attack_hit_applies_damage_and_logs_hp_delta()
    {
        var dice = new FixedDiceRoller(new[] { 20, 4, 5 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();
        Assert.True(res.Ok);

        var boss = res.State.Actors["b1"];
        Assert.True(boss.Hp < boss.MaxHp);
        Assert.Contains(res.NewLedgerEntries, e => e.Kind == DndLedgerKind.DamageApplied);
        Assert.False(res.State.IsCompleted);
    }

    [Fact]
    public void CastSpell_spends_mp_and_errors_when_insufficient()
    {
        var enc = BasicEncounter();
        var r = new DndEncounterRunner(
            enc,
            diceRoller: new FixedDiceRoller(new[] { 1, 1, 1, 1, 1, 1 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        var first = r.DeclareCastSpell("p1", "b1");
        Assert.True(first.Ok);
        Assert.Contains(first.NewLedgerEntries, e => e.Kind == DndLedgerKind.MpSpent);
        r.RollAll();
        r.Ready("p1");

        Assert.True(r.DeclareCastSpell("p1", "b1").Ok);
        r.RollAll();
        r.Ready("p1");

        Assert.True(r.DeclareCastSpell("p1", "b1").Ok);
        r.RollAll();
        r.Ready("p1");

        var fail = r.DeclareCastSpell("p1", "b1");
        Assert.False(fail.Ok);
        Assert.Contains("MP", fail.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enemy_turns_run_after_ready_quorum()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 6, 3 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        r.DeclareAttack("p1", "b1");
        r.RollAll();
        var res = r.Ready("p1");
        Assert.True(res.Ok);

        Assert.Contains(res.NewLedgerEntries, e => e.Message.Contains("Boss attacks", StringComparison.OrdinalIgnoreCase));
        Assert.True(res.State.Actors["p1"].Hp < res.State.Actors["p1"].MaxHp);
        Assert.Equal(2, res.State.RoundNumber);
    }

    [Fact]
    public void Victory_locks_engine_and_rejects_further_actions()
    {
        var dice = new FixedDiceRoller(new[] { 20, 8, 8 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        r.DeclareAttack("p1", "b1");
        var res = r.RollAll();
        Assert.True(res.Ok);
        Assert.True(res.State.IsCompleted);
        Assert.Equal("victory", res.State.CompletionReason);

        var fail = r.DeclareAttack("p1", "b1");
        Assert.False(fail.Ok);
    }

    [Fact]
    public void Pass_marks_acted_and_does_not_run_enemies()
    {
        var dice = new FixedDiceRoller(new[] { 1 });
        var r = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();

        var res = r.Pass("p1");
        Assert.True(res.Ok);
        Assert.Contains(res.NewLedgerEntries, e => e.Message.Contains("passes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(string.Empty, res.State.CurrentActorId);
        Assert.Contains("p1", res.State.ActedThisRoundActorIds);
        Assert.Equal(20, res.State.Actors["p1"].Hp);
        Assert.DoesNotContain(res.NewLedgerEntries, e => e.Message.Contains("Boss attacks", StringComparison.OrdinalIgnoreCase));
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
    public void DeclareAttack_enemy_cannot_act()
    {
        var r = ReadyCombat();
        var err = r.DeclareAttack("b1", "p1");
        Assert.False(err.Ok);
        Assert.Contains("party", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Second_action_same_round_is_rejected()
    {
        var r = ReadyCombat(new[] { 1 });
        r.Pass("p1");
        var err = r.DeclareAttack("p1", "b1");
        Assert.False(err.Ok);
        Assert.Contains("Already acted", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ready_without_acting_fails()
    {
        var r = ReadyCombat();
        var err = r.Ready("p1");
        Assert.False(err.Ok);
        Assert.Contains("Act first", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Late_joiner_is_participating_and_can_act()
    {
        var r = ReadyCombat();
        var joined = r.AddPartyActor(new DndActorDefinition(
            ActorId: "p2",
            Name: "Rookie",
            Side: DndSide.Party,
            IsBoss: false,
            Stats: new DndStats(10, 10, 10, 10, 10),
            MaxHp: 16,
            MaxMp: 6,
            StartingHp: 16,
            StartingMp: 6));
        Assert.True(joined.Ok);
        Assert.Contains("p2", joined.State.ParticipatingActorIds);

        var pass = r.Pass("p2");
        Assert.True(pass.Ok);
        Assert.Contains("p2", pass.State.ActedThisRoundActorIds);
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
    public void Adds_are_included_in_the_encounter()
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
            diceRoller: new FixedDiceRoller(new[] { 1 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));

        var started = r.StartEncounter();
        Assert.True(started.Ok);
        Assert.Empty(started.PendingRolls);
        Assert.True(started.State.Actors.ContainsKey("a1"));
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
        var dice = new FixedDiceRoller(new[] { 1, 20, 8, 8 });
        var r = new DndEncounterRunner(enc, diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.DeclareAttack("p1", "b1");
        r.RollAll();
        var res = r.Ready("p1");

        Assert.True(res.Ok);
        Assert.True(res.State.IsCompleted);
        Assert.Equal("defeat", res.State.CompletionReason);
        Assert.False(res.State.Actors["p1"].IsAlive);
    }

    [Fact]
    public void Two_pc_quorum_one_done_vote_skips_the_other()
    {
        var p2 = new DndActorDefinition(
            ActorId: "p2",
            Name: "Rookie",
            Side: DndSide.Party,
            IsBoss: false,
            Stats: new DndStats(10, 10, 10, 10, 10),
            MaxHp: 16,
            MaxMp: 6,
            StartingHp: 16,
            StartingMp: 6);
        var enc = new DndEncounterDefinition(
            new[] { BasicEncounter().Party[0], p2 },
            BasicEncounter().Boss,
            Array.Empty<DndActorDefinition>());
        var r = new DndEncounterRunner(enc, diceRoller: new FixedDiceRoller(new[] { 1, 1 }), clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        r.Pass("p1");
        var res = r.Ready("p1");
        Assert.True(res.Ok);
        Assert.Contains("p2", res.State.SittingOutActorIds);
        Assert.DoesNotContain("p2", res.State.ParticipatingActorIds ?? Array.Empty<string>());
    }

    private static DndEncounterRunner ReadyCombat(int[] dice = null)
    {
        var r = new DndEncounterRunner(
            BasicEncounter(),
            diceRoller: new FixedDiceRoller(dice ?? new[] { 1 }),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        r.StartEncounter();
        return r;
    }
}
