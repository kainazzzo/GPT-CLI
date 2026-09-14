using DndModuleExample;
using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Modules.Tests.Dnd;

public sealed class DndGameMasterModuleTests
{
    private static DndEncounterSnapshot Encounter()
    {
        var actors = new Dictionary<string, DndActorSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["p1"] = new("p1", "Hero", DndSide.Party, false, new DndStats(10, 10, 10, 10, 10), 20, 20, 0, 0, true, 15),
            ["b1"] = new("b1", "Goblin Boss", DndSide.Enemy, true, new DndStats(10, 10, 10, 10, 10), 12, 12, 0, 0, true, 10)
        };
        return new DndEncounterSnapshot(
            DndEncounterPhase.InCombat,
            1,
            "p1",
            new[] { "p1", "b1" },
            actors,
            false,
            string.Empty);
    }

    [Fact]
    public void ParseNaturalGameIntent_maps_combat_verbs()
    {
        var enc = Encounter();
        Assert.Equal(DndGameMasterModule.NaturalGameActionKind.None, DndGameMasterModule.ParseNaturalGameIntent("nice weather", enc).Action);
        Assert.Equal(DndGameMasterModule.NaturalGameActionKind.Pass, DndGameMasterModule.ParseNaturalGameIntent("I'll pass", enc).Action);
        Assert.Equal(DndGameMasterModule.NaturalGameActionKind.RollAll, DndGameMasterModule.ParseNaturalGameIntent("roll all", enc).Action);
        Assert.Equal(DndGameMasterModule.NaturalGameActionKind.Attack, DndGameMasterModule.ParseNaturalGameIntent("attack the goblin boss", enc).Action);
        Assert.Equal(DndGameMasterModule.NaturalGameActionKind.Cast, DndGameMasterModule.ParseNaturalGameIntent("cast firebolt at goblin", enc).Action);
    }

    [Fact]
    public void Campaign_and_party_intent_helpers()
    {
        Assert.True(DndGameMasterModule.TryDetectCampaignCreateIntent("create a campaign called \"Wilds\" and overwrite it", out var name, out var overwrite));
        Assert.True(overwrite);
        Assert.Equal("Wilds", name);
        Assert.True(DndGameMasterModule.LooksLikeDraftUpdateIntent("update the campaign to add a forest"));
        Assert.False(DndGameMasterModule.LooksLikeDraftUpdateIntent("create a campaign"));
        Assert.True(DndGameMasterModule.LooksLikePartyAdd("add bob to the party"));
        Assert.True(DndGameMasterModule.LooksLikePartyRemove("remove bob"));
        Assert.True(DndGameMasterModule.LooksLikeModeChangeIntent("/gptcli dnd mode game"));
        Assert.True(DndGameMasterModule.LooksLikeGameStartIntent("start the encounter"));
    }

    [Fact]
    public void Pass_timeout_confirm_and_sanitize()
    {
        Assert.True(DndGameMasterModule.TryExtractPassTimeoutArgs("set pass timeout to 30 seconds", out var seconds, out var minutes));
        Assert.Equal(30, seconds);
        Assert.Null(minutes);
        Assert.True(DndGameMasterModule.TryExtractPassTimeoutArgs("pass timeout 2 minutes", out seconds, out minutes));
        Assert.Equal(2, minutes);

        Assert.True(DndGameMasterModule.TryParseDraftConfirmationResponse("yes", out var confirm, out var cancel));
        Assert.True(confirm);
        Assert.False(cancel);
        Assert.True(DndGameMasterModule.TryParseDraftConfirmationResponse("cancel", out confirm, out cancel));
        Assert.True(cancel);
        Assert.False(DndGameMasterModule.TryParseDraftConfirmationResponse("maybe", out _, out _));

        var cleaned = DndGameMasterModule.SanitizeDraftToolNarration("Calling tool\ngptcli_dnd_attack {}\nThe goblin staggers.");
        Assert.Equal("The goblin staggers.", cleaned);
    }

    [Fact]
    public void IsDndToolAllowedForMode_respects_off_draft_game()
    {
        Assert.True(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_mode", "off"));
        Assert.False(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_attack", "off"));
        Assert.True(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_campaigncreate", "draft"));
        Assert.False(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_attack", "draft"));
        Assert.True(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_attack", "game"));
        Assert.False(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_campaigncreate", "game"));
    }
}
