using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class RandomDiceRollerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-4)]
    public void RollDie_throws_when_sides_less_than_two(int sides)
    {
        var roller = new RandomDiceRoller(seed: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => roller.RollDie(sides));
    }

    [Fact]
    public void RollDie_stays_in_inclusive_range()
    {
        var roller = new RandomDiceRoller(seed: 42);
        for (var i = 0; i < 200; i++)
        {
            var roll = roller.RollDie(6);
            Assert.InRange(roll, 1, 6);
        }
    }

    [Fact]
    public void Same_seed_produces_the_same_sequence()
    {
        var a = new RandomDiceRoller(seed: 7);
        var b = new RandomDiceRoller(seed: 7);
        var rollsA = Enumerable.Range(0, 20).Select(_ => a.RollDie(20)).ToArray();
        var rollsB = Enumerable.Range(0, 20).Select(_ => b.RollDie(20)).ToArray();
        Assert.Equal(rollsA, rollsB);
    }
}
