namespace GPT.CLI.Chat.Dnd;

internal sealed class DndTableRound
{
    public int RoundNumber { get; set; } = 1;
    public string CurrentActorId { get; set; } = string.Empty;

    public HashSet<string> ActedThisRound { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ReadyThisRound { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SittingOut { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Participating { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> JoinedThisRound { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static int QuorumNeeded(int activePcCount)
        => Math.Max(1, (int)Math.Ceiling(Math.Max(0, activePcCount) / 3.0));

    public static bool IsNpcActorId(string actorId)
        => !string.IsNullOrWhiteSpace(actorId) &&
           actorId.StartsWith("npc:", StringComparison.OrdinalIgnoreCase);

    public void ClearRoundFlags()
    {
        CurrentActorId = string.Empty;
        ActedThisRound.Clear();
        ReadyThisRound.Clear();
        JoinedThisRound.Clear();
    }

    public void AdvanceRound()
    {
        ClearRoundFlags();
        Participating.Clear();
        RoundNumber++;
    }

    public void Seat(string actorId, bool joinedThisRound = false)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return;
        }

        SittingOut.Remove(actorId);
        Participating.Add(actorId);
        if (joinedThisRound)
        {
            JoinedThisRound.Add(actorId);
        }
    }

    public void MarkActed(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return;
        }

        Seat(actorId);
        ActedThisRound.Add(actorId);
        CurrentActorId = string.Empty;
    }

    public DndTableRoundState ToState()
        => new()
        {
            RoundNumber = RoundNumber,
            CurrentActorId = CurrentActorId ?? string.Empty,
            ActedThisRoundActorIds = ActedThisRound.ToList(),
            ReadyActorIds = ReadyThisRound.ToList(),
            SittingOutActorIds = SittingOut.ToList(),
            ParticipatingActorIds = Participating.ToList(),
            JoinedThisRoundActorIds = JoinedThisRound.ToList()
        };

    public void LoadFrom(DndTableRoundState state)
    {
        if (state == null)
        {
            return;
        }

        RoundNumber = state.RoundNumber <= 0 ? 1 : state.RoundNumber;
        CurrentActorId = state.CurrentActorId ?? string.Empty;
        ReplaceSet(ActedThisRound, state.ActedThisRoundActorIds);
        ReplaceSet(ReadyThisRound, state.ReadyActorIds);
        ReplaceSet(SittingOut, state.SittingOutActorIds);
        ReplaceSet(Participating, state.ParticipatingActorIds);
        ReplaceSet(JoinedThisRound, state.JoinedThisRoundActorIds);
    }

    public static IReadOnlyList<string> Sorted(IEnumerable<string> values)
        => (values ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

    private static void ReplaceSet(HashSet<string> target, List<string> source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var id in source)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                target.Add(id);
            }
        }
    }
}
