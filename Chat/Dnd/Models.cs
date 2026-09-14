using System.Collections.ObjectModel;

namespace GPT.CLI.Chat.Dnd;

public enum DndSide
{
    Party = 0,
    Enemy = 1
}

public enum DndEncounterPhase
{
    NotStarted = 0,
    NeedInitiative = 1,
    InCombat = 2,
    Completed = 3
}

public enum DndGamePhase
{
    NotStarted = 0,
    SessionStart = 1,
    Exploration = 2,
    Social = 3,
    Travel = 4,
    Check = 5,
    Rest = 6,
    Combat = 7,
    Aftermath = 8,
    Failed = 9,
    Complete = 10
}

public enum DndSceneKind
{
    Intro = 0,
    Exploration = 1,
    Social = 2,
    Travel = 3,
    Combat = 4,
    Aftermath = 5,
    Finale = 6
}

public enum DndCheckStat
{
    Str = 0,
    Def = 1,
    Dex = 2,
    SpellPower = 3,
    Luck = 4
}

public enum DndNextRequestKind
{
    NeedInitiativeRolls = 0,
    NeedAction = 1,
    NeedRolls = 2,
    Completed = 3
}

public sealed record DndStats(int Str, int Def, int Dex, int SpellPower, int Luck);

public sealed record DndActorDefinition(
    string ActorId,
    string Name,
    DndSide Side,
    bool IsBoss,
    DndStats Stats,
    int MaxHp,
    int MaxMp,
    int StartingHp,
    int StartingMp);

public sealed record DndEncounterDefinition(
    IReadOnlyList<DndActorDefinition> Party,
    DndActorDefinition Boss,
    IReadOnlyList<DndActorDefinition> Adds);

public sealed record DndActorSnapshot(
    string ActorId,
    string Name,
    DndSide Side,
    bool IsBoss,
    DndStats Stats,
    int MaxHp,
    int Hp,
    int MaxMp,
    int Mp,
    bool IsAlive,
    int? InitiativeTotal);

public sealed record DndEncounterSnapshot(
    DndEncounterPhase Phase,
    int RoundNumber,
    string CurrentActorId,
    IReadOnlyList<string> TurnOrder,
    IReadOnlyDictionary<string, DndActorSnapshot> Actors,
    bool IsCompleted,
    string CompletionReason);

public enum DndPendingRollKind
{
    Initiative = 0,
    AttackToHit = 1,
    AttackDamage = 2,
    SpellToHit = 3,
    SpellDamage = 4
}

public sealed record DndPendingRoll(
    string RollId,
    DndPendingRollKind Kind,
    string ActorId,
    string TargetId,
    string ActionId,
    int DiceCount,
    int DiceSides,
    int Modifier);

public enum DndActionType
{
    Unknown = 0,
    Attack = 1,
    CastSpell = 2,
    Pass = 3
}

public enum DndLedgerKind
{
    EncounterStarted = 0,
    InitiativeRolled = 1,
    TurnAdvanced = 2,
    ActionDeclared = 3,
    ActionResolved = 4,
    DamageApplied = 5,
    MpSpent = 6,
    EncounterCompleted = 7
}

public sealed record DndLedgerDetails(
    string ActorId,
    string TargetId,
    string ActionId,
    DndActionType ActionType,
    string RollId,
    DndPendingRollKind? RollKind,
    int DiceCount,
    int DiceSides,
    IReadOnlyList<int> Rolls,
    int Modifier,
    int Total,
    bool Hit,
    bool Critical,
    int ActorHpDelta,
    int ActorMpDelta,
    int TargetHpDelta,
    int TargetMpDelta);

public sealed record DndLedgerEntry(
    int Sequence,
    DateTimeOffset OccurredUtc,
    DndLedgerKind Kind,
    string Message,
    DndLedgerDetails Details);

public sealed record DndNextRequest(
    DndNextRequestKind Kind,
    string CurrentActorId,
    IReadOnlyList<DndPendingRoll> RequiredRolls);

public sealed record DndTurnResult(
    bool Ok,
    string Error,
    DndEncounterSnapshot State,
    IReadOnlyList<DndLedgerEntry> NewLedgerEntries,
    IReadOnlyList<DndPendingRoll> PendingRolls,
    DndNextRequest NextRequest)
{
    public static DndTurnResult ErrorResult(string message, DndEncounterSnapshot state, IReadOnlyList<DndPendingRoll> pendingRolls)
    {
        return new DndTurnResult(
            Ok: false,
            Error: message ?? "error",
            State: state,
            NewLedgerEntries: Array.Empty<DndLedgerEntry>(),
            PendingRolls: pendingRolls ?? Array.Empty<DndPendingRoll>(),
            NextRequest: BuildNextRequest(state, pendingRolls));
    }

    public static DndNextRequest BuildNextRequest(DndEncounterSnapshot state, IReadOnlyList<DndPendingRoll> pendingRolls)
    {
        if (state == null)
        {
            return new DndNextRequest(DndNextRequestKind.Completed, string.Empty, Array.Empty<DndPendingRoll>());
        }

        if (state.IsCompleted || state.Phase == DndEncounterPhase.Completed)
        {
            return new DndNextRequest(DndNextRequestKind.Completed, state.CurrentActorId ?? string.Empty, Array.Empty<DndPendingRoll>());
        }

        if (pendingRolls != null && pendingRolls.Count > 0)
        {
            var needInit = pendingRolls.Any(r => r.Kind == DndPendingRollKind.Initiative);
            return new DndNextRequest(
                needInit ? DndNextRequestKind.NeedInitiativeRolls : DndNextRequestKind.NeedRolls,
                state.CurrentActorId ?? string.Empty,
                pendingRolls.ToList().AsReadOnly());
        }

        return new DndNextRequest(DndNextRequestKind.NeedAction, state.CurrentActorId ?? string.Empty, Array.Empty<DndPendingRoll>());
    }
}

internal static class DndModelHelpers
{
    public static IReadOnlyDictionary<string, T> AsReadOnlyCopy<T>(Dictionary<string, T> values)
    {
        if (values == null || values.Count == 0)
        {
            return new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase));
        }

        return new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(values, StringComparer.OrdinalIgnoreCase));
    }
}
