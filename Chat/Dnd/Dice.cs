namespace GPT.CLI.Chat.Dnd;

public interface IDiceRoller
{
    int RollDie(int sides);
}

public sealed class RandomDiceRoller : IDiceRoller
{
    private readonly Random _random;

    public RandomDiceRoller(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public int RollDie(int sides)
    {
        if (sides <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sides), "sides must be >= 2");
        }

        // Inclusive 1..sides
        return _random.Next(1, sides + 1);
    }
}

