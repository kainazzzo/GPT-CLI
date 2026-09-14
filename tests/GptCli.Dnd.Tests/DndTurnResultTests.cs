using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndTurnResultTests
{
    private static DndEncounterSnapshot Snapshot(
        DndEncounterPhase phase = DndEncounterPhase.InCombat,
        bool completed = false,
        string currentActorId = "p1")
    {
        return new DndEncounterSnapshot(
            Phase: phase,
            RoundNumber: 1,
            CurrentActorId: currentActorId,
            TurnOrder: new[] { "p1", "b1" },
            Actors: new Dictionary<string, DndActorSnapshot>(StringComparer.OrdinalIgnoreCase),
            IsCompleted: completed,
            CompletionReason: completed ? "victory" : string.Empty);
    }

    private static DndPendingRoll Roll(DndPendingRollKind kind, string rollId = "r1")
        => new(rollId, kind, ActorId: "p1", TargetId: "b1", ActionId: "a1", DiceCount: 1, DiceSides: 20, Modifier: 0);

    [Fact]
    public void BuildNextRequest_null_state_is_completed()
    {
        var next = DndTurnResult.BuildNextRequest(null, Array.Empty<DndPendingRoll>());
        Assert.Equal(DndNextRequestKind.Completed, next.Kind);
        Assert.Empty(next.RequiredRolls);
    }

    [Fact]
    public void BuildNextRequest_completed_snapshot_is_completed()
    {
        var next = DndTurnResult.BuildNextRequest(
            Snapshot(phase: DndEncounterPhase.Completed, completed: true),
            new[] { Roll(DndPendingRollKind.AttackToHit) });
        Assert.Equal(DndNextRequestKind.Completed, next.Kind);
        Assert.Empty(next.RequiredRolls);
    }

    [Fact]
    public void BuildNextRequest_pending_initiative_asks_for_initiative()
    {
        var pending = new[] { Roll(DndPendingRollKind.Initiative), Roll(DndPendingRollKind.AttackToHit, "r2") };
        var next = DndTurnResult.BuildNextRequest(Snapshot(phase: DndEncounterPhase.NeedInitiative), pending);
        Assert.Equal(DndNextRequestKind.NeedInitiativeRolls, next.Kind);
        Assert.Equal(2, next.RequiredRolls.Count);
    }

    [Fact]
    public void BuildNextRequest_pending_non_initiative_asks_for_rolls()
    {
        var pending = new[] { Roll(DndPendingRollKind.AttackDamage) };
        var next = DndTurnResult.BuildNextRequest(Snapshot(), pending);
        Assert.Equal(DndNextRequestKind.NeedRolls, next.Kind);
        Assert.Single(next.RequiredRolls);
    }

    [Fact]
    public void BuildNextRequest_no_pending_asks_for_action()
    {
        var next = DndTurnResult.BuildNextRequest(Snapshot(), Array.Empty<DndPendingRoll>());
        Assert.Equal(DndNextRequestKind.NeedAction, next.Kind);
        Assert.Equal("p1", next.CurrentActorId);
        Assert.Empty(next.RequiredRolls);
    }
}
