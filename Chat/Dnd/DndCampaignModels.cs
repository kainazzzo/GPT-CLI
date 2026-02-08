using System.Collections.ObjectModel;

namespace GPT.CLI.Chat.Dnd;

public sealed record DndCampaignDefinition(IReadOnlyList<DndCampaignPartyMember> Party);

public sealed record DndCampaignPartyMember(
    string ActorId,
    string Name,
    DndStats Stats,
    int MaxHp,
    int Hp,
    int MaxMp,
    int Mp);

public sealed record DndEncounterTemplate(
    string TemplateId,
    string Name,
    DndActorDefinition Boss,
    IReadOnlyList<DndActorDefinition> Adds);

public sealed record DndCampaignSnapshot(
    bool IsFailed,
    string FailureReason,
    IReadOnlyDictionary<string, DndCampaignPartyMember> Party,
    string ActiveEncounterId,
    string ActiveEncounterName,
    DndEncounterSnapshot ActiveEncounterState);

public sealed record DndCampaignLedgerEntry(
    int Sequence,
    DateTimeOffset OccurredUtc,
    string EncounterId,
    string EncounterName,
    DndLedgerEntry EncounterEntry,
    string Message);

public sealed record DndCampaignResult(
    bool Ok,
    string Error,
    DndCampaignSnapshot Campaign,
    DndTurnResult EncounterResult,
    IReadOnlyList<DndCampaignLedgerEntry> NewCampaignLedgerEntries);

internal static class DndCampaignModelHelpers
{
    public static IReadOnlyDictionary<string, T> ToReadOnlyDictionary<T>(Dictionary<string, T> items)
    {
        if (items == null || items.Count == 0)
        {
            return new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase));
        }

        return new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(items, StringComparer.OrdinalIgnoreCase));
    }
}

