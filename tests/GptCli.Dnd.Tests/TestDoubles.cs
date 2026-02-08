using GPT.CLI.Chat.Dnd;

namespace GptCli.Dnd.Tests;

internal sealed class FixedDiceRoller : IDiceRoller
{
    private readonly Queue<int> _rolls = new();

    public FixedDiceRoller(IEnumerable<int> rolls)
    {
        foreach (var r in rolls ?? Array.Empty<int>())
        {
            _rolls.Enqueue(r);
        }
    }

    public int RollDie(int sides)
    {
        if (_rolls.Count == 0)
        {
            throw new InvalidOperationException("No more fixed dice values.");
        }

        var v = _rolls.Dequeue();
        if (v < 1)
        {
            v = 1;
        }

        if (v > sides)
        {
            v = sides;
        }

        return v;
    }
}

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }
}

