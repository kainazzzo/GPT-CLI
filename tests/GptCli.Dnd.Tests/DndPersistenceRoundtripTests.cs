using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndPersistenceRoundtripTests
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
    public void EncounterRunner_ToState_FromState_preserves_pending_rolls_and_can_continue()
    {
        var dice = new FixedDiceRoller(new[] { 20, 4, 4, 1, 1, 1 });
        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z"));

        var r1 = new DndEncounterRunner(BasicEncounter(), diceRoller: dice, clock: clock);
        r1.StartEncounter();

        var declared = r1.DeclareAttack("p1", "b1");
        Assert.True(declared.Ok);
        Assert.Contains(declared.PendingRolls, pr => pr.Kind == DndPendingRollKind.AttackToHit);

        var pendingId = declared.PendingRolls.First(pr => pr.Kind == DndPendingRollKind.AttackToHit).RollId;
        var s1 = r1.ToState();

        var r2 = DndEncounterRunner.FromState(s1, diceRoller: dice, clock: clock);
        var snap1 = r1.GetState();
        var snap2 = r2.GetState();

        Assert.Equal(snap1.Phase, snap2.Phase);
        Assert.Equal(snap1.RoundNumber, snap2.RoundNumber);
        Assert.Equal(snap1.CurrentActorId, snap2.CurrentActorId);
        Assert.Equal(snap1.Actors["p1"].Hp, snap2.Actors["p1"].Hp);
        Assert.Equal(snap1.Actors["b1"].Hp, snap2.Actors["b1"].Hp);

        var p2 = r2.GetPendingRolls();
        Assert.Contains(p2, pr => string.Equals(pr.RollId, pendingId, StringComparison.OrdinalIgnoreCase));

        var progressed = r2.RollAll();
        Assert.True(progressed.Ok);
        Assert.True(progressed.NewLedgerEntries.Count > 0);
        Assert.True(progressed.State.Actors["b1"].Hp < progressed.State.Actors["b1"].MaxHp);
    }

    [Fact]
    public void CampaignRunner_ToState_FromState_preserves_active_encounter_and_reconciles_party()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 3, 3 });
        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z"));

        var def = new DndCampaignDefinition(new[]
        {
            new DndCampaignPartyMember(
                ActorId: "p1",
                Name: "Hero",
                Stats: new DndStats(Str: 14, Def: 12, Dex: 12, SpellPower: 10, Luck: 10),
                MaxHp: 20,
                Hp: 20,
                MaxMp: 10,
                Mp: 10)
        });

        var camp1 = new DndCampaignRunner(def, diceRoller: dice, clock: clock);
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
        camp1.RegisterEncounterTemplate(new DndEncounterTemplate("t1", "Test Encounter", boss, Array.Empty<DndActorDefinition>()));

        camp1.StartEncounter("t1");
        camp1.Attack("p1", "b1");

        var s1 = camp1.ToState();
        Assert.NotNull(s1.ActiveEncounter);

        var camp2 = DndCampaignRunner.FromState(s1, diceRoller: dice, clock: clock);
        var progressed = camp2.RollAll();
        Assert.True(progressed.Ok);
        Assert.Equal(13, progressed.Campaign.Party["p1"].Hp);
        Assert.NotNull(progressed.NewCampaignLedgerEntries);
    }
}
