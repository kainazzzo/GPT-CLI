namespace GPT.CLI.Chat.Dnd;

public static class DndSceneCatalog
{
    public const string IntroSceneId = "intro";
    public const string FinaleSceneId = "finale";

    public static string ApproachSceneId(string templateId) => $"{Slug(templateId)}-approach";
    public static string CombatSceneId(string templateId) => $"{Slug(templateId)}-combat";
    public static string AftermathSceneId(string templateId) => $"{Slug(templateId)}-aftermath";

    public static IReadOnlyList<DndSceneDefinition> Synthesize(IReadOnlyList<DndEncounterTemplate> templates)
    {
        var list = new List<DndSceneDefinition>();
        var ordered = (templates ?? Array.Empty<DndEncounterTemplate>())
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
            .GroupBy(t => t.TemplateId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (ordered.Count == 0)
        {
            list.Add(new DndSceneDefinition(
                SceneId: IntroSceneId,
                Title: "Session start",
                Kind: DndSceneKind.Intro,
                Summary: "The party gathers. There are no prepared encounters yet.",
                LinkedEncounterTemplateId: string.Empty,
                NextSceneId: FinaleSceneId));
            list.Add(new DndSceneDefinition(
                SceneId: FinaleSceneId,
                Title: "The end",
                Kind: DndSceneKind.Finale,
                Summary: "The campaign has no remaining scenes.",
                LinkedEncounterTemplateId: string.Empty,
                NextSceneId: string.Empty));
            return list.AsReadOnly();
        }

        var firstApproach = ApproachSceneId(ordered[0].TemplateId);
        list.Add(new DndSceneDefinition(
            SceneId: IntroSceneId,
            Title: "Session start",
            Kind: DndSceneKind.Intro,
            Summary: "Recap the hook, present the party, and begin the first scene.",
            LinkedEncounterTemplateId: string.Empty,
            NextSceneId: firstApproach));

        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            var id = t.TemplateId.Trim();
            var title = string.IsNullOrWhiteSpace(t.Name) ? id : t.Name.Trim();
            var sceneText = string.IsNullOrWhiteSpace(t.Scene) ? $"The party approaches {title}." : t.Scene.Trim();
            var rewardsText = string.IsNullOrWhiteSpace(t.Rewards) ? "Catch your breath and take whatever the fight left behind." : t.Rewards.Trim();
            var nextApproach = i + 1 < ordered.Count ? ApproachSceneId(ordered[i + 1].TemplateId) : FinaleSceneId;

            var approachId = ApproachSceneId(id);
            var combatId = CombatSceneId(id);
            var aftermathId = AftermathSceneId(id);

            list.Add(new DndSceneDefinition(
                SceneId: approachId,
                Title: title,
                Kind: DndSceneKind.Exploration,
                Summary: sceneText,
                LinkedEncounterTemplateId: id,
                NextSceneId: combatId));
            list.Add(new DndSceneDefinition(
                SceneId: combatId,
                Title: $"{title} — combat",
                Kind: DndSceneKind.Combat,
                Summary: $"Fight: {title}.",
                LinkedEncounterTemplateId: id,
                NextSceneId: aftermathId));
            list.Add(new DndSceneDefinition(
                SceneId: aftermathId,
                Title: $"{title} — aftermath",
                Kind: DndSceneKind.Aftermath,
                Summary: rewardsText,
                LinkedEncounterTemplateId: id,
                NextSceneId: nextApproach));
        }

        list.Add(new DndSceneDefinition(
            SceneId: FinaleSceneId,
            Title: "The end",
            Kind: DndSceneKind.Finale,
            Summary: "The campaign's prepared scenes are complete.",
            LinkedEncounterTemplateId: string.Empty,
            NextSceneId: string.Empty));

        return list.AsReadOnly();
    }

    private static string Slug(string value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(s) ? "scene" : s;
    }
}
