using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndSessionRunnerTests
{
    private static DndCampaignRunner MakeCampaign(int startingHp = 20, int startingMp = 10, IDiceRoller dice = null)
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

    private static DndEncounterTemplate BasicTemplate(string id = "t1", string name = "Test Encounter")
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
            TemplateId: id,
            Name: name,
            Boss: boss,
            Adds: Array.Empty<DndActorDefinition>(),
            Scene: "A ruined watchtower looms ahead.",
            Rewards: "A battered lantern and a scrap of map.");
    }

    [Fact]
    public void Synthesize_from_n_templates_is_intro_plus_three_per_template_plus_finale()
    {
        var scenes = DndSceneCatalog.Synthesize(new[] { BasicTemplate("t1"), BasicTemplate("t2", "Second") });
        Assert.Equal(8, scenes.Count);
        Assert.Equal("intro", scenes[0].SceneId);
        Assert.Equal(DndSceneKind.Intro, scenes[0].Kind);
        Assert.Equal("t1-approach", scenes[1].SceneId);
        Assert.Equal(DndSceneKind.Exploration, scenes[1].Kind);
        Assert.Equal("t1", scenes[1].LinkedEncounterTemplateId);
        Assert.Equal("t1-combat", scenes[2].SceneId);
        Assert.Equal(DndSceneKind.Combat, scenes[2].Kind);
        Assert.Equal("t1-aftermath", scenes[3].SceneId);
        Assert.Equal("A battered lantern and a scrap of map.", scenes[3].Summary);
        Assert.Equal("t2-approach", scenes[4].SceneId);
        Assert.Equal("finale", scenes[^1].SceneId);
        Assert.Equal(DndSceneKind.Finale, scenes[^1].Kind);
    }

    [Fact]
    public void StartSession_begins_in_session_start_with_options()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());

        var res = camp.StartSession();
        Assert.True(res.Ok);
        var session = res.Campaign.Session;
        Assert.True(session.Started);
        Assert.Equal(DndGamePhase.SessionStart, session.Phase);
        Assert.Equal("intro", session.CurrentSceneId);
        Assert.Contains(session.Options, o => o.Id == "begin");
        Assert.Contains(session.Options, o => o.Id == "recap");
        Assert.Contains(session.Options, o => o.Id == "party");
        Assert.Equal(5, session.Scenes.Count);
    }

    [Fact]
    public void StartSession_synthesizes_five_scenes_for_one_template()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        Assert.Equal(5, camp.ListScenes().Count);
    }

    [Fact]
    public void Illegal_option_is_rejected_and_phase_stays()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();

        var res = camp.ChooseOption("cast fireball");
        Assert.False(res.Ok);
        Assert.Contains("Unknown option", res.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DndGamePhase.SessionStart, camp.GetSessionSnapshot().Phase);
    }

    [Fact]
    public void Begin_enters_first_exploration_scene()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();

        var res = camp.ChooseOption("begin");
        Assert.True(res.Ok);
        camp.Ready("p1");
        var session = camp.GetSessionSnapshot();
        Assert.Equal(DndGamePhase.Exploration, session.Phase);
        Assert.Equal("t1-approach", session.CurrentSceneId);
        Assert.Contains(session.Options, o => o.Id == "check:search");
        Assert.Contains(session.Options, o => o.Id == "social");
        Assert.Contains(session.Options, o => o.Id.StartsWith("combat:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChooseOption_accepts_numeric_index()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();

        var res = camp.ChooseOption("1");
        Assert.True(res.Ok);
        Assert.Equal(DndGamePhase.Exploration, res.Campaign.Session.Phase);
    }

    [Fact]
    public void Social_does_not_consume_the_table_action()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin", "p1");

        var talked = camp.ChooseOption("social", "p1");
        Assert.True(talked.Ok);
        Assert.Equal(DndGamePhase.Social, talked.Campaign.Session.Phase);

        camp.ChooseOption("return", "p1");
        var blocked = camp.ChooseOption("check:search", "p1");
        Assert.False(blocked.Ok);
        Assert.Contains("Already acted", blocked.Error, StringComparison.OrdinalIgnoreCase);

        camp.Ready("p1");
        var searched = camp.ChooseOption("check:search", "p1");
        Assert.True(searched.Ok);
        Assert.Equal(DndGamePhase.Check, searched.Campaign.Session.Phase);
    }

    [Fact]
    public void Exploration_check_rolls_and_returns()
    {
        var dice = new FixedDiceRoller(new[] { 11 });
        var camp = MakeCampaign(dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");

        var pending = camp.ChooseOption("check:search");
        Assert.True(pending.Ok);
        Assert.Equal(DndGamePhase.Check, pending.Campaign.Session.Phase);
        Assert.NotNull(pending.Campaign.Session.PendingCheck);
        Assert.Equal(DndCheckStat.Dex, pending.Campaign.Session.PendingCheck.Stat);
        Assert.Equal(12, pending.Campaign.Session.PendingCheck.Dc);

        var resolved = camp.ChooseOption("roll");
        Assert.True(resolved.Ok);
        Assert.Equal(DndGamePhase.Exploration, resolved.Campaign.Session.Phase);
        Assert.True(resolved.Campaign.Session.LastCheckSuccess);
        Assert.Contains("success", resolved.Campaign.Session.LastCheckSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_failure_returns_to_previous_phase()
    {
        var dice = new FixedDiceRoller(new[] { 10 });
        var camp = MakeCampaign(dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("check:search");

        var resolved = camp.ResolveCheck();
        Assert.True(resolved.Ok);
        Assert.False(resolved.Campaign.Session.LastCheckSuccess);
        Assert.Equal(DndGamePhase.Exploration, resolved.Campaign.Session.Phase);
    }

    [Fact]
    public void Cancel_check_does_not_roll()
    {
        var camp = MakeCampaign(dice: new FixedDiceRoller(Array.Empty<int>()));
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("search the area");

        var cancelled = camp.ChooseOption("cancel");
        Assert.True(cancelled.Ok);
        Assert.Equal(DndGamePhase.Exploration, cancelled.Campaign.Session.Phase);
        Assert.Null(cancelled.Campaign.Session.PendingCheck);
    }

    [Fact]
    public void Listed_combat_option_starts_encounter_unlisted_template_rejected()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");

        var started = camp.ChooseOption("start the fight");
        Assert.True(started.Ok);
        Assert.Equal(DndGamePhase.Combat, started.Campaign.Session.Phase);
        Assert.NotNull(started.Campaign.ActiveEncounterState);
        Assert.Equal(DndEncounterPhase.InCombat, started.Campaign.ActiveEncounterState.Phase);
        Assert.Equal(20, started.Campaign.Party["p1"].Hp);

        var blocked = camp.StartEncounter("missing");
        Assert.False(blocked.Ok);
    }

    [Fact]
    public void Combat_victory_moves_to_aftermath()
    {
        var dice = new FixedDiceRoller(new[] { 20, 8, 8 });
        var camp = MakeCampaign(dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("combat:t1");

        var res = camp.Attack("p1", "b1");
        res = camp.RollAll();
        Assert.True(res.Ok);
        Assert.Equal("victory", res.EncounterResult.State.CompletionReason);
        Assert.Equal(DndGamePhase.Aftermath, res.Campaign.Session.Phase);
        Assert.Equal("t1-aftermath", res.Campaign.Session.CurrentSceneId);
        Assert.Contains(res.Campaign.Session.Options, o => o.Id == "continue");
        Assert.Contains(res.Campaign.Session.Options, o => o.Id == "rest");
    }

    [Fact]
    public void Party_wipe_moves_session_to_failed_and_long_rest_revives()
    {
        var dice = new FixedDiceRoller(new[] { 1, 20, 8, 8 });
        var camp = MakeCampaign(startingHp: 3, startingMp: 0, dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("combat:t1");
        camp.Attack("p1", "b1");
        camp.RollAll();
        var res = camp.Ready("p1");

        Assert.True(res.Campaign.IsFailed);
        Assert.Equal(DndGamePhase.Failed, res.Campaign.Session.Phase);
        Assert.Contains(res.Campaign.Session.Options, o => o.Id == "rest:long");

        var revived = camp.ChooseOption("rest:long");
        Assert.True(revived.Ok);
        Assert.False(revived.Campaign.IsFailed);
        Assert.Equal(20, revived.Campaign.Party["p1"].Hp);
        Assert.Equal(DndGamePhase.Exploration, revived.Campaign.Session.Phase);
        Assert.Equal("t1-approach", revived.Campaign.Session.CurrentSceneId);
    }

    [Fact]
    public void Short_rest_heals_half_missing_and_is_blocked_in_combat()
    {
        var camp = MakeCampaign(startingHp: 10, startingMp: 2);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");

        var rested = camp.ChooseOption("rest");
        Assert.Equal(DndGamePhase.Rest, rested.Campaign.Session.Phase);

        var shortRest = camp.ShortRest();
        Assert.True(shortRest.Ok);
        Assert.Equal(15, shortRest.Campaign.Party["p1"].Hp);
        Assert.Equal(6, shortRest.Campaign.Party["p1"].Mp);

        camp.ChooseOption("return");
        camp.Ready("p1");
        camp.ChooseOption("combat:t1");
        var blocked = camp.ShortRest();
        Assert.False(blocked.Ok);
        Assert.Contains("encounter", blocked.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Long_rest_blocked_during_active_encounter()
    {
        var camp = MakeCampaign();
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("combat:t1");

        var blocked = camp.LongRest();
        Assert.False(blocked.Ok);
    }

    [Fact]
    public void Aftermath_continue_reaches_finale_when_no_more_encounters()
    {
        var dice = new FixedDiceRoller(new[] { 20, 8, 8 });
        var camp = MakeCampaign(dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.ChooseOption("combat:t1");
        camp.Attack("p1", "b1");
        camp.RollAll();

        var done = camp.ChooseOption("continue");
        Assert.True(done.Ok);
        Assert.Equal(DndGamePhase.Complete, done.Campaign.Session.Phase);
        Assert.Empty(done.Campaign.Session.Options);
    }

    [Fact]
    public void Session_roundtrip_preserves_phase_scene_and_pending_check()
    {
        var dice = new FixedDiceRoller(new[] { 11 });
        var camp = MakeCampaign(dice: dice);
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();
        camp.ChooseOption("begin");
        camp.Ready("p1");
        camp.RequestCheck("p1", DndCheckStat.Dex, 12, "Search the rubble");

        var state = camp.ToState();
        Assert.True(state.Session.Started);
        Assert.Equal(DndGamePhase.Check, state.Session.Phase);
        Assert.Equal("t1-approach", state.Session.CurrentSceneId);
        Assert.NotNull(state.Session.PendingCheck);

        var restored = DndCampaignRunner.FromState(state, diceRoller: dice, clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        var snap = restored.GetSessionSnapshot();
        Assert.True(snap.Started);
        Assert.Equal(DndGamePhase.Check, snap.Phase);
        Assert.Equal("t1-approach", snap.CurrentSceneId);
        Assert.Equal("p1", snap.PendingCheck.ActorId);
        Assert.Equal(DndCheckStat.Dex, snap.PendingCheck.Stat);

        var resolved = restored.ResolveCheck();
        Assert.True(resolved.Campaign.Session.LastCheckSuccess);
        Assert.Equal(DndGamePhase.Exploration, resolved.Campaign.Session.Phase);
    }

    [Fact]
    public void Empty_party_starts_in_formation()
    {
        var camp = new DndCampaignRunner(
            new DndCampaignDefinition(Array.Empty<DndCampaignPartyMember>()),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        camp.RegisterEncounterTemplate(BasicTemplate());
        var started = camp.StartSession();
        Assert.True(started.Ok);
        Assert.Equal(DndGamePhase.PartyFormation, started.Campaign.Session.Phase);

        var blocked = camp.ChooseOption("begin");
        Assert.False(blocked.Ok);
        Assert.Contains("forming", blocked.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Join_then_ready_leaves_formation()
    {
        var camp = new DndCampaignRunner(
            new DndCampaignDefinition(Array.Empty<DndCampaignPartyMember>()),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        camp.RegisterEncounterTemplate(BasicTemplate());
        camp.StartSession();

        var joined = camp.JoinParty(new DndCampaignPartyMember(
            ActorId: "p1",
            Name: "Hero",
            Stats: new DndStats(14, 12, 12, 10, 10),
            MaxHp: 20,
            Hp: 20,
            MaxMp: 10,
            Mp: 10));
        Assert.True(joined.Ok);
        Assert.Equal(DndGamePhase.PartyFormation, joined.Campaign.Session.Phase);

        var ready = camp.Ready("p1");
        Assert.True(ready.Ok);
        Assert.Equal(DndGamePhase.SessionStart, ready.Campaign.Session.Phase);
        Assert.Contains(ready.Campaign.Session.Options, o => o.Id == "begin");
    }

    [Fact]
    public void Ready_in_formation_without_joining_fails()
    {
        var camp = new DndCampaignRunner(
            new DndCampaignDefinition(Array.Empty<DndCampaignPartyMember>()),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        camp.StartSession();
        var ready = camp.Ready("p1");
        Assert.False(ready.Ok);
    }

    [Fact]
    public void StartEncounter_with_empty_party_fails()
    {
        var camp = new DndCampaignRunner(
            new DndCampaignDefinition(Array.Empty<DndCampaignPartyMember>()),
            clock: new FakeClock(DateTimeOffset.Parse("2026-02-08T00:00:00Z")));
        camp.RegisterEncounterTemplate(BasicTemplate());
        var res = camp.StartEncounter("t1");
        Assert.False(res.Ok);
    }
}
