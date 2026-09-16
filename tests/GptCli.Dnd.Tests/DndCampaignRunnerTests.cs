using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndCampaignRunnerTests
{
    private static DndCampaignRunner MakeCampaign(
        int startingHp = 20,
        int startingMp = 10,
        IDiceRoller dice = null)
    {
        var def = new DndCampaignDefinition(new[]
        {
            new DndCampaignPartyMember(
                ActorId: "p1",
                Name: "Hero",
                Stats: new DndStats(Str: 14, Def: 12, Dex: 12, SpellPower: 10, Luck: 10),
                MaxHp: 20,
                Hp: startingHp,
                MaxMp: 10,
                Mp: startingMp)
        });

        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z"));
        return new DndCampaignRunner(def, diceRoller: dice ?? new FixedDiceRoller(new[] { 20, 1 }), clock: clock);
    }

    private static DndEncounterTemplate BasicTemplate()
    {
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

        return new DndEncounterTemplate(
            TemplateId: "t1",
            Name: "Test Encounter",
            Boss: boss,
            Adds: Array.Empty<DndActorDefinition>());
    }

    [Fact]
    public void StartEncounter_uses_campaign_party_current_hp_mp_as_starting_values()
    {
        var camp = MakeCampaign(startingHp: 7, startingMp: 2, dice: new FixedDiceRoller(new[] { 20, 1 }));
        camp.RegisterEncounterTemplate(BasicTemplate());

        var res = camp.StartEncounter("t1");
        Assert.True(res.Ok);
        Assert.Equal(DndEncounterPhase.InCombat, res.EncounterResult.State.Phase);
        Assert.Equal(7, res.EncounterResult.State.Actors["p1"].Hp);
        Assert.Equal(2, res.EncounterResult.State.Actors["p1"].Mp);
    }

    [Fact]
    public void Encounter_enemy_turn_after_ready_reconciles_into_campaign_party_state()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 3, 3 });
        var camp = MakeCampaign(startingHp: 20, startingMp: 10, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");

        camp.Attack("p1", "b1");
        var res = camp.RollAll();
        Assert.True(res.Ok);
        Assert.Equal(13, res.Campaign.Party["p1"].Hp);
    }

    [Fact]
    public void Party_wipe_marks_campaign_failed_and_blocks_actions_until_cleared()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 8, 8 });
        var camp = MakeCampaign(startingHp: 3, startingMp: 0, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");

        camp.Attack("p1", "b1");
        var res = camp.RollAll();
        Assert.True(res.Campaign.IsFailed);
        Assert.Equal("defeat", res.Campaign.FailureReason);

        var blocked = camp.StartEncounter("t1");
        Assert.False(blocked.Ok);

        var cleared = camp.LongRest(clearFailure: true);
        Assert.True(cleared.Ok);
        Assert.False(cleared.Campaign.IsFailed);
        Assert.Equal(20, cleared.Campaign.Party["p1"].Hp);
    }

    [Fact]
    public void StartEncounter_unknown_template_fails()
    {
        var camp = MakeCampaign();
        var res = camp.StartEncounter("missing");
        Assert.False(res.Ok);
        Assert.Contains("template", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongRest_without_clearFailure_restores_hp_but_keeps_failed()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 8, 8 });
        var camp = MakeCampaign(startingHp: 3, startingMp: 0, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");
        camp.Attack("p1", "b1");
        camp.RollAll();

        Assert.True(camp.GetState().IsFailed);

        var rested = camp.LongRest(clearFailure: false);
        Assert.True(rested.Ok);
        Assert.True(rested.Campaign.IsFailed);
        Assert.Equal("defeat", rested.Campaign.FailureReason);
        Assert.Equal(20, rested.Campaign.Party["p1"].Hp);

        var blocked = camp.StartEncounter("t1");
        Assert.False(blocked.Ok);
    }

    [Fact]
    public void Pass_reconciles_party_hp_mp_when_enemy_misses()
    {
        var dice = new FixedDiceRoller(new[] { 1 });
        var camp = MakeCampaign(startingHp: 20, startingMp: 10, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");

        var res = camp.Pass("p1");
        Assert.True(res.Ok);
        Assert.Equal(20, res.Campaign.Party["p1"].Hp);
        Assert.Equal(10, res.Campaign.Party["p1"].Mp);
    }
}
