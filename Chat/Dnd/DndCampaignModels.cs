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
    IReadOnlyList<DndActorDefinition> Adds,
    string Scene = "",
    string Rewards = "");

public sealed record DndPendingCheck(
    string ActorId,
    string ActorName,
    DndCheckStat Stat,
    int Dc,
    string Reason);

public sealed record DndSceneDefinition(
    string SceneId,
    string Title,
    DndSceneKind Kind,
    string Summary,
    string LinkedEncounterTemplateId,
    string NextSceneId);

public sealed record DndSceneOption(
    string Id,
    string Label,
    DndGamePhase TargetPhase,
    string EncounterTemplateId = "",
    string NextSceneId = "",
    DndCheckStat? CheckStat = null,
    int? CheckDc = null,
    string CheckReason = "");

public sealed record DndSessionSnapshot(
    bool Started,
    DndGamePhase Phase,
    string CurrentSceneId,
    string CurrentSceneTitle,
    string CurrentSceneSummary,
    DndGamePhase PreviousPhase,
    DndPendingCheck PendingCheck,
    bool LastCheckSuccess,
    string LastCheckSummary,
    IReadOnlyList<DndSceneDefinition> Scenes,
    IReadOnlyList<DndSceneOption> Options,
    IReadOnlyList<string> SeatedActorIds = null,
    IReadOnlyList<string> SittingOutActorIds = null,
    IReadOnlyList<string> ActedThisRoundActorIds = null,
    IReadOnlyList<string> ReadyActorIds = null,
    string TableCurrentActorId = "",
    int TableRoundNumber = 1,
    int ReadyQuorumNeeded = 0,
    int DoneVoteCount = 0);

public sealed record DndCampaignSnapshot(
    bool IsFailed,
    string FailureReason,
    IReadOnlyDictionary<string, DndCampaignPartyMember> Party,
    string ActiveEncounterId,
    string ActiveEncounterName,
    DndEncounterSnapshot ActiveEncounterState,
    DndSessionSnapshot Session = null);

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

