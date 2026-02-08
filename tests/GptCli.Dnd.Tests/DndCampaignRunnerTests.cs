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
        Assert.Equal(DndEncounterPhase.NeedInitiative, res.EncounterResult.State.Phase);
        Assert.Equal(7, res.EncounterResult.State.Actors["p1"].Hp);
        Assert.Equal(2, res.EncounterResult.State.Actors["p1"].Mp);
    }

    [Fact]
    public void Encounter_auto_enemy_turn_reconciles_into_campaign_party_state()
    {
        // Dice sequence:
        // initiative: p1=20, b1=1
        // player attack to-hit: 1 (miss)
        // enemy to-hit: 20 (crit hit)
        // enemy damage dice: 3,3 (2d8+1 => 7)
        var dice = new FixedDiceRoller(new[] { 20, 1, 1, 20, 3, 3 });
        var camp = MakeCampaign(startingHp: 20, startingMp: 10, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");
        camp.RollAll(); // resolve initiative

        camp.Attack("p1", "b1");
        var res = camp.RollAll(); // resolves miss, then enemy attacks/damages
        Assert.True(res.Ok);
        Assert.Equal(13, res.Campaign.Party["p1"].Hp);
    }

    [Fact]
    public void Party_wipe_marks_campaign_failed_and_blocks_actions_until_cleared()
    {
        // Start low HP and force enemy hit+damage.
        // initiative: p1=20, b1=1
        // player attack: 1 miss
        // enemy to-hit: 20 crit hit
        // enemy damage: 8,8 => 2d8+1 => 17 kills startingHp=3
        var dice = new FixedDiceRoller(new[] { 20, 1, 1, 20, 8, 8 });
        var camp = MakeCampaign(startingHp: 3, startingMp: 0, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartEncounter("t1");
        camp.RollAll();

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
}
