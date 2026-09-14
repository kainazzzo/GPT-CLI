namespace GPT.CLI.Chat.Dnd;

// Serializable engine state for persistence. These DTOs intentionally use settable properties
// so callers (e.g., Discord modules) can reconcile/merge state before restoring runners.

public sealed class DndEncounterRunnerState
{
    public int SequenceCounter { get; set; }
    public int RoundNumber { get; set; }
    public int TurnIndex { get; set; }
    public DndEncounterPhase Phase { get; set; }
    public bool Completed { get; set; }
    public string CompletionReason { get; set; } = string.Empty;

    public List<string> TurnOrder { get; set; } = new();
    public List<DndEncounterActorState> Actors { get; set; } = new();
    public Dictionary<string, int> InitiativeByActorId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DndPendingRoll> PendingRolls { get; set; } = new();
    public List<DndEncounterActionState> Actions { get; set; } = new();
    public List<DndLedgerEntry> Ledger { get; set; } = new();
}

public sealed class DndEncounterActorState
{
    public DndActorDefinition Definition { get; set; }
    public int Hp { get; set; }
    public int Mp { get; set; }
    public int? InitiativeTotal { get; set; }
}

public sealed class DndEncounterActionState
{
    public string ActionId { get; set; } = string.Empty;
    public DndActionType ActionType { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public bool Hit { get; set; }
    public bool Critical { get; set; }
}

public sealed class DndCampaignRunnerState
{
    public List<DndCampaignPartyMember> Party { get; set; } = new();
    public List<DndEncounterTemplate> Templates { get; set; } = new();
    public List<DndCampaignLedgerEntry> Ledger { get; set; } = new();
    public int LedgerSeq { get; set; }
    public int EncounterSeq { get; set; }

    public bool Failed { get; set; }
    public string FailureReason { get; set; } = string.Empty;

    public string ActiveEncounterId { get; set; } = string.Empty;
    public string ActiveEncounterName { get; set; } = string.Empty;
    public string ActiveTemplateId { get; set; } = string.Empty;

    public DndEncounterRunnerState ActiveEncounter { get; set; }
    public DndSessionRunnerState Session { get; set; }
}

public sealed class DndSessionRunnerState
{
    public bool Started { get; set; }
    public DndGamePhase Phase { get; set; }
    public string CurrentSceneId { get; set; } = string.Empty;
    public DndGamePhase PreviousPhase { get; set; }
    public DndPendingCheck PendingCheck { get; set; }
    public bool LastCheckSuccess { get; set; }
    public string LastCheckSummary { get; set; } = string.Empty;
    public List<DndSceneDefinition> Scenes { get; set; } = new();
}

