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
        Assert.False(DndGameMasterModule.LooksLikeDraftUpdateIntent(
            "no rewrite.. just generate npcs/monsters, create locations/maps"));
        Assert.False(DndGameMasterModule.LooksLikeDraftUpdateIntent("can you come up with some examples that fit the campaign?"));
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
        Assert.True(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_choose", "game"));
        Assert.True(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_rest", "game"));
        Assert.False(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_choose", "draft"));
        Assert.False(DndGameMasterModule.IsDndToolAllowedForMode("gptcli_dnd_campaigncreate", "game"));
    }

    [Fact]
    public void TryMatchSessionOption_maps_numbers_labels_and_keywords()
    {
        var options = new[]
        {
            new DndSceneOption("begin", "Begin the adventure", DndGamePhase.Exploration),
            new DndSceneOption("recap", "Recap the hook", DndGamePhase.SessionStart),
            new DndSceneOption("party", "Show the party", DndGamePhase.SessionStart)
        };

        Assert.Equal("begin", DndGameMasterModule.TryMatchSessionOption("1", options));
        Assert.Equal("begin", DndGameMasterModule.TryMatchSessionOption("begin", options));
        Assert.Equal("begin", DndGameMasterModule.TryMatchSessionOption("let's play", options));
        Assert.Equal("recap", DndGameMasterModule.TryMatchSessionOption("recap", options));
        Assert.Null(DndGameMasterModule.TryMatchSessionOption("cast fireball", options));

        var explore = new[]
        {
            new DndSceneOption("check:search", "Search the area", DndGamePhase.Check),
            new DndSceneOption("social", "Talk to someone", DndGamePhase.Social),
            new DndSceneOption("rest", "Make camp", DndGamePhase.Rest),
            new DndSceneOption("combat:t1", "Start the fight", DndGamePhase.Combat, EncounterTemplateId: "t1"),
            new DndSceneOption("continue", "Continue to the next scene", DndGamePhase.Combat)
        };

        Assert.Equal("check:search", DndGameMasterModule.TryMatchSessionOption("I search the rubble", explore));
        Assert.Equal("social", DndGameMasterModule.TryMatchSessionOption("talk to the survivor", explore));
        Assert.Equal("combat:t1", DndGameMasterModule.TryMatchSessionOption("fight the goblins", explore));
        Assert.Equal("rest", DndGameMasterModule.TryMatchSessionOption("let's rest", explore));
        Assert.Equal("continue", DndGameMasterModule.TryMatchSessionOption("continue", explore));
    }

    [Fact]
    public void LooksLikeBeginIntent_ignores_fight_requests()
    {
        Assert.True(DndGameMasterModule.LooksLikeBeginIntent("let's play"));
        Assert.True(DndGameMasterModule.LooksLikeBeginIntent("begin"));
        Assert.False(DndGameMasterModule.LooksLikeBeginIntent("start the encounter"));
        Assert.False(DndGameMasterModule.LooksLikeBeginIntent("start the fight"));
        Assert.True(DndGameMasterModule.LooksLikeGameStartIntent("start the encounter"));
    }

    [Fact]
    public void Draft_propose_intents_cover_generate_and_examples()
    {
        var mixed = DndGameMasterModule.DetectDraftProposeKinds(
            "no rewrite.. just generate npcs/monsters, create locations/maps");
        Assert.True(mixed.Npcs);
        Assert.True(mixed.Encounters);
        Assert.True(mixed.Locations);

        var examples = DndGameMasterModule.DetectDraftProposeKinds(
            "can you come up with some examples that fit the campaign?");
        Assert.True(examples.Npcs);
        Assert.False(examples.Encounters);
        Assert.False(examples.Locations);

        Assert.True(DndGameMasterModule.LooksLikeNpcCreateIntentFromText("generate npcs for the town"));
        Assert.True(DndGameMasterModule.LooksLikeBrainstormExamplesIntent("come up with some examples"));
        Assert.True(DndGameMasterModule.LooksLikeNoRewriteIntent("no rewrite, just generate npcs"));
    }

    [Fact]
    public void TryParseProposalSelection_accepts_all_and_index_lists()
    {
        Assert.True(DndGameMasterModule.TryParseProposalSelection("all", 3, out var all));
        Assert.Equal(new[] { 1, 2, 3 }, all);

        Assert.True(DndGameMasterModule.TryParseProposalSelection("1 and 3", 4, out var picks));
        Assert.Equal(new[] { 1, 3 }, picks);

        Assert.True(DndGameMasterModule.TryParseProposalSelection("create 2", 3, out var two));
        Assert.Equal(new[] { 2 }, two);

        Assert.False(DndGameMasterModule.TryParseProposalSelection("maybe later", 3, out _));
        Assert.False(DndGameMasterModule.TryParseProposalSelection("cancel", 3, out _));
        Assert.False(DndGameMasterModule.TryParseProposalSelection("give me 3", 2, out _));
        Assert.False(DndGameMasterModule.TryParseProposalSelection("no I want more monsters than 2", 2, out _));
        Assert.False(DndGameMasterModule.TryParseProposalSelection("more than two monsters", 2, out _));
        Assert.True(DndGameMasterModule.LooksLikeProposalRevisionRequest("give me 3"));
        Assert.True(DndGameMasterModule.LooksLikeProposalRevisionRequest("more than two monsters!!"));
        Assert.False(DndGameMasterModule.LooksLikeProposalRevisionRequest("1 and 2"));
        Assert.False(DndGameMasterModule.LooksLikeProposalRevisionRequest("create 2"));

        Assert.True(DndGameMasterModule.TryParseRequestedProposalCount("give me 3", out var three));
        Assert.Equal(3, three);
        Assert.True(DndGameMasterModule.TryParseRequestedProposalCount("more than 2", out var moreThanTwo));
        Assert.Equal(3, moreThanTwo);
        Assert.True(DndGameMasterModule.TryParseRequestedProposalCount("more than two", out var moreThanTwoWord));
        Assert.Equal(3, moreThanTwoWord);
        Assert.True(DndGameMasterModule.TryParseRequestedProposalCount("I want more monsters than 2", out var thanTwo));
        Assert.Equal(3, thanTwo);
        Assert.True(DndGameMasterModule.TryParseRequestedProposalCount("5 monsters", out var five));
        Assert.Equal(5, five);

        var kinds = DndGameMasterModule.DetectDraftProposeKinds("give me 3 more monsters");
        Assert.True(kinds.Encounters);
        Assert.Equal(3, kinds.Count);
    }

    [Fact]
    public void DraftProposalJsonSchema_is_strict_object_with_required_arrays()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(DndGameMasterModule.DraftProposalJsonSchema);
        var root = doc.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("npcs", required);
        Assert.Contains("encounters", required);
        Assert.Contains("locations", required);
        Assert.True(root.GetProperty("$defs").GetProperty("actor").TryGetProperty("additionalProperties", out var actorAdditional));
        Assert.False(actorAdditional.GetBoolean());
        var locationItems = root.GetProperty("properties").GetProperty("locations").GetProperty("items");
        var locationRequired = locationItems.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        var locationProps = locationItems.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(locationProps, locationRequired);
    }

    [Fact]
    public void TryParseProposalBundleFromModelText_reads_json_and_location_roundtrip()
    {
        var json = """
            {
              "npcs": [{"name":"Mira","concept":"harbor witch","role":"ally"}],
              "encounters": [{"templateId":"dock-ambush","name":"Dock Ambush","scene":"Fog on the pier.","boss":{"name":"Reef Ghoul"}}],
              "locations": [{"id":"old-pier","name":"Old Pier","summary":"Rotting boards over black water.","mapMarkdown":"+--+\n|  |\n+--+"}]
            }
            """;
        var bundle = DndGameMasterModule.TryParseProposalBundleFromModelText("```json\n" + json + "\n```");
        Assert.NotNull(bundle);
        Assert.Single(bundle.Npcs);
        Assert.Equal("Mira", bundle.Npcs[0].Name);
        Assert.Single(bundle.Locations);
        Assert.Equal("Old Pier", bundle.Locations[0].Name);

        var rendered = DndGameMasterModule.RenderDraftProposalBundle(bundle);
        Assert.Contains("Mira", rendered, StringComparison.Ordinal);
        Assert.Contains("Old Pier", rendered, StringComparison.Ordinal);
        Assert.Contains("**1.**", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Include a `concept`", rendered, StringComparison.OrdinalIgnoreCase);

        var loc = new DndGameMasterModule.DndLiteLocationDocument
        {
            Id = "old-pier",
            Name = "Old Pier",
            Summary = "Rotting boards over black water.",
            MapMarkdown = "+--+\n|  |\n+--+"
        };
        var serialized = System.Text.Json.JsonSerializer.Serialize(loc);
        var copy = System.Text.Json.JsonSerializer.Deserialize<DndGameMasterModule.DndLiteLocationDocument>(serialized);
        Assert.Equal("old-pier", copy.Id);
        Assert.Equal("Old Pier", copy.Name);
        Assert.Contains("+--+", copy.MapMarkdown);
    }

    [Fact]
    public void TryExtractTruncatedJsonString_salvages_cut_off_chat_reply()
    {
        var truncated =
            "{\"reply\":\"Here are example NPCs that fit Funky Cold Medina.\\n1) Mira Vell — informant.\\n2) DJ Rime —";
        Assert.False(DndGameMasterModule.TryGetDraftRouterReplyText("not json", out _));
        Assert.True(DndGameMasterModule.TryGetDraftRouterReplyText(truncated, out var reply));
        Assert.Contains("Mira Vell", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("playable draft", reply, StringComparison.OrdinalIgnoreCase);

        var valid = "{\"reply\":\"Short ok.\"}";
        Assert.True(DndGameMasterModule.TryGetDraftRouterReplyText(valid, out var ok));
        Assert.Equal("Short ok.", ok);

        Assert.True(DndGameMasterModule.LooksLikeShortDraftTopic("premise"));
        Assert.False(DndGameMasterModule.LooksLikeShortDraftTopic("try again.. give me some examples"));

        var longTruncated =
            "{\"reply\":\"Here are some examples that fit Funky Cold Medina.\\n\\n## NPCs\\n### 1) DJ Cryo-Sermon\\n- Hook: Preaches abstinence.\\n### 2) Detective Juno Kest\\n- Tell: Always clicks a";
        Assert.True(DndGameMasterModule.TryGetDraftRouterReplyText(longTruncated, out var longReply));
        Assert.Contains("DJ Cryo-Sermon", longReply, StringComparison.Ordinal);
        Assert.Contains("Detective Juno Kest", longReply, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseProposalBundle_accepts_truncated_encounter_json()
    {
        var truncated =
            "{\"npcs\":[],\"encounters\":[{\"templateId\":\"medina-freezeout\",\"name\":\"Cold Snap at the Neon Turntable\",\"scene\":\"A basement dance hall pulses with a cursed rhythm as Medina vapors crystallize across the floor. Revelers stagger through euphoric t";
        var bundle = DndGameMasterModule.TryParseProposalBundleFromModelText(truncated);
        Assert.NotNull(bundle);
        Assert.Single(bundle.Encounters);
        Assert.Equal("medina-freezeout", bundle.Encounters[0].TemplateId);
        Assert.Equal("Cold Snap at the Neon Turntable", bundle.Encounters[0].Name);
    }

    [Fact]
    public void TryParseProposalBundle_salvages_truncated_location_json()
    {
        var truncated =
            "{\"npcs\":[],\"encounters\":[],\"locations\":[{\"id\":\"loc-neon-needle\",\"name\":\"The Neon Needle\",\"summary\":\"A packed nightclub where the campaign can open amid laser haze, mirrored walls, and bass-heavy enchanted music. A hidden mixing booth siphon";
        var bundle = DndGameMasterModule.TryParseProposalBundleFromModelText(truncated);
        Assert.NotNull(bundle);
        Assert.Single(bundle.Locations);
        Assert.Equal("The Neon Needle", bundle.Locations[0].Name);
        Assert.Contains("packed nightclub", bundle.Locations[0].Summary, StringComparison.Ordinal);

        var withNewlines =
            "{\"npcs\":[],\"encounters\":[],\"locations\":[{\"id\":\"club\",\"name\":\"Club Calor\",\"summary\":\"Hot dance floor.\",\"mapMarkdown\":\"+--+\n|DJ|\n+--+\"}]}";
        var mapped = DndGameMasterModule.TryParseProposalBundleFromModelText(withNewlines);
        Assert.NotNull(mapped);
        Assert.Equal("Club Calor", mapped.Locations[0].Name);
    }

    [Fact]
    public void RenderDraftProposalBundle_keeps_long_encounter_scenes()
    {
        var scene =
            "A Medina-fueled street party erupts into panic when the cursed rhythm triggers a flash-freeze surge. " +
            "The party must subdue the euphoric revelers, disrupt the enchanted turntables, and stop the beat " +
            "before the bazaar ices over completely.";
        Assert.True(scene.Length > 160);
        var bundle = new DndGameMasterModule.DraftProposalBundle
        {
            Npcs = new List<DndGameMasterModule.NpcProposalDto>(),
            Encounters =
            {
                new DndGameMasterModule.EncounterTemplateDto
                {
                    TemplateId = "encounter_medina_cold_snap",
                    Name = "Cold Snap at the Neon Bazaar",
                    Scene = scene
                },
                new DndGameMasterModule.EncounterTemplateDto
                {
                    TemplateId = "encounter_cryophonic_loop",
                    Name = "The Cryophonic Feedback Loop",
                    Scene = "Inside a looted arcane laboratory, stolen research apparatus has fused with the cursed beat."
                }
            }
        };

        var rendered = DndGameMasterModule.RenderDraftProposalBundle(bundle);
        Assert.Contains("disrupt the enchanted turntables", rendered, StringComparison.Ordinal);
        Assert.Contains("The Cryophonic Feedback Loop", rendered, StringComparison.Ordinal);
        Assert.Contains("`1 and 2`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("`1 and 3`", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftNextStepsPrompt_after_location_asks_for_fight_or_npcs()
    {
        var next = DndGameMasterModule.BuildDraftNextStepsPrompt(
            "location",
            "Club Calor",
            hasStory: true,
            locationCount: 1,
            encounterCount: 0,
            partyMemberCount: 2);
        Assert.Contains("Club Calor", next, StringComparison.Ordinal);
        Assert.Contains("fight", next, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**Next**", next, StringComparison.Ordinal);
        Assert.Contains("\n- ", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftNextStepsPrompt_playable_draft_offers_game_mode()
    {
        var next = DndGameMasterModule.BuildDraftNextStepsPrompt(
            "mixed",
            null,
            hasStory: true,
            locationCount: 2,
            encounterCount: 2,
            partyMemberCount: 3);
        Assert.Contains("mode value:game", next, StringComparison.Ordinal);
    }

    [Fact]
    public void LooksLikeFinishDraftIntent_matches_finish_the_rest()
    {
        Assert.True(DndGameMasterModule.LooksLikeFinishDraftIntent("finish out the rest for me"));
        Assert.True(DndGameMasterModule.LooksLikeFinishDraftIntent("fill it out"));
        Assert.True(DndGameMasterModule.LooksLikeFinishDraftIntent("complete the draft"));
        Assert.False(DndGameMasterModule.LooksLikeFinishDraftIntent("give me 3 monsters"));
        Assert.False(DndGameMasterModule.LooksLikeFinishDraftIntent("all"));
    }

    [Fact]
    public void LooksLikeDraftStatusIntent_and_status_markdown_use_bullets()
    {
        Assert.True(DndGameMasterModule.LooksLikeDraftStatusIntent("what's left"));
        Assert.True(DndGameMasterModule.LooksLikeDraftStatusIntent("whats missing"));
        Assert.True(DndGameMasterModule.LooksLikeDraftStatusIntent("what else is left to be tweaked?"));
        Assert.False(DndGameMasterModule.LooksLikeDraftStatusIntent("finish the rest"));

        Assert.Empty(DndGameMasterModule.BuildDraftRemainingGaps(
            hasStory: true,
            locationCount: 3,
            encounterCount: 9,
            partyPcs: 1,
            partyNpcs: 5,
            missingSheets: 0,
            hasFinale: true));
        Assert.True(DndGameMasterModule.CampaignMarkdownHasFinale("## Finale\nStop the drop."));
        Assert.False(DndGameMasterModule.CampaignMarkdownHasFinale("Just a premise about Medina."));

        var markdown = DndGameMasterModule.BuildDraftStatusMarkdown(
            "funky cold medina",
            hasStory: true,
            locations: new[] { "The Icebreaker" },
            encounters: Array.Empty<string>(),
            partyPcs: 1,
            partyNpcs: 1,
            remaining: new[] { "2–3 investigation or combat encounters", "Key districts or hideouts" });
        Assert.Contains("**Draft status**", markdown, StringComparison.Ordinal);
        Assert.Contains("**What's left**", markdown, StringComparison.Ordinal);
        Assert.Contains("\n- 2–3 investigation", markdown, StringComparison.Ordinal);
        Assert.Contains("The Icebreaker", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("The core is in place:", markdown, StringComparison.Ordinal);

        var complete = DndGameMasterModule.BuildDraftStatusMarkdown(
            "funky cold medina",
            hasStory: true,
            locations: new[] { "The Icebreaker" },
            encounters: new[] { "Medina Mile Freeze-Out" },
            partyPcs: 1,
            partyNpcs: 5,
            remaining: Array.Empty<string>(),
            hasFinale: true,
            includeNext: true,
            includeEditHelp: true);
        Assert.Contains("Nothing required", complete, StringComparison.Ordinal);
        Assert.Contains("**How to edit**", complete, StringComparison.Ordinal);
        Assert.Contains("natural language", complete, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`finish the rest` to fill remaining gaps", complete, StringComparison.Ordinal);
        Assert.Contains(DndGameMasterModule.BuildDraftEditInstructions(complete: true), complete, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseProposalBundle_accepts_full_encounter_with_stats()
    {
        var json = """
            {
              "npcs": [],
              "encounters": [
                {
                  "templateId": "medina-freezeout",
                  "name": "Cold Snap at the Neon Turntable",
                  "scene": "A basement dance hall pulses with a cursed rhythm.",
                  "rewards": "A cracked vinyl ward.",
                  "boss": {
                    "name": "DJ Cryo-Sermon",
                    "description": "Club prophet",
                    "maxHp": 32,
                    "maxMp": 6,
                    "stats": { "str": 12, "def": 11, "dex": 14, "spellPower": 16, "luck": 10 }
                  },
                  "adds": [
                    { "name": "Frost Reveler", "maxHp": 12, "stats": { "STR": 10, "DEF": 10, "DEX": 12, "spell_power": 8, "luck": "9" } }
                  ]
                }
              ],
              "locations": []
            }
            """;
        var bundle = DndGameMasterModule.TryParseProposalBundleFromModelText(json);
        Assert.NotNull(bundle);
        Assert.Single(bundle.Encounters);
        Assert.Equal("DJ Cryo-Sermon", bundle.Encounters[0].Boss.Name);
        Assert.Equal(32, bundle.Encounters[0].Boss.MaxHp);
    }
}
