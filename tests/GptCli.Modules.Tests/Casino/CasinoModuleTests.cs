using CasinoModuleExample;
using GPT.CLI.Chat.Discord;
using Xunit;

namespace GptCli.Modules.Tests.Casino;

public sealed class CasinoModuleTests : IDisposable
{
    public CasinoModuleTests()
    {
        CasinoGameModule.Rng = new Random(1);
    }

    public void Dispose()
    {
        CasinoGameModule.Rng = Random.Shared;
    }

    private static InstructionGPT.ChannelState Channel()
        => new()
        {
            Options = new InstructionGPT.ChannelOptions { CasinoEnabled = true },
            CasinoBalances = new Dictionary<ulong, decimal>()
        };

    [Fact]
    public void TryParseMessageCommand_recognizes_games_and_help()
    {
        Assert.True(CasinoGameModule.TryParseMessageCommand("!coinflip heads 5", out var coinflip));
        Assert.Equal(CasinoGameModule.GameType.Coinflip, coinflip.Game);
        Assert.Equal(new[] { "heads", "5" }, coinflip.Args);

        Assert.True(CasinoGameModule.TryParseMessageCommand("!casino help", out var help));
        Assert.Equal(CasinoGameModule.GameType.Help, help.Game);

        Assert.True(CasinoGameModule.TryParseMessageCommand("!dice", out var dice));
        Assert.Equal(CasinoGameModule.GameType.Dice, dice.Game);

        Assert.True(CasinoGameModule.TryParseMessageCommand("!foo", out var unknown));
        Assert.Equal(CasinoGameModule.GameType.Help, unknown.Game);

        Assert.False(CasinoGameModule.TryParseMessageCommand("coinflip", out _));
    }

    [Fact]
    public void Purchase_wallet_and_insufficient_funds()
    {
        var channel = Channel();
        var purchased = CasinoGameModule.ExecuteGame(
            null,
            channel,
            7,
            new CasinoGameModule.GameRequest(CasinoGameModule.GameType.Purchase, new[] { "20" }));
        Assert.Equal(20m, CasinoGameModule.GetBalance(channel, 7));
        Assert.Contains("20", purchased.Details ?? purchased.Outcome, StringComparison.Ordinal);

        var broke = CasinoGameModule.ExecuteGame(
            null,
            channel,
            8,
            new CasinoGameModule.GameRequest(CasinoGameModule.GameType.Coinflip, new[] { "heads", "5" }));
        Assert.Contains("Insufficient", broke.Outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seeded_coinflip_updates_balance()
    {
        var channel = Channel();
        CasinoGameModule.SetBalance(channel, 3, 10m);
        var result = CasinoGameModule.ExecuteGame(
            null,
            channel,
            3,
            new CasinoGameModule.GameRequest(CasinoGameModule.GameType.Coinflip, new[] { "heads", "2" }));
        Assert.Equal("Coinflip", result.Game);
        Assert.NotNull(result.Bet);
        Assert.NotNull(result.Net);
        Assert.Equal(10m + result.Net.Value, CasinoGameModule.GetBalance(channel, 3));
    }

    [Fact]
    public void Help_and_status_do_not_need_a_wallet()
    {
        var channel = Channel();
        var help = CasinoGameModule.ExecuteGame(null, channel, 1, new CasinoGameModule.GameRequest(CasinoGameModule.GameType.Help, Array.Empty<string>()));
        Assert.Contains("help", help.Game, StringComparison.OrdinalIgnoreCase);
        var status = CasinoGameModule.ExecuteGame(null, channel, 1, new CasinoGameModule.GameRequest(CasinoGameModule.GameType.Status, Array.Empty<string>()));
        Assert.Contains("enabled", status.Outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CardValue_HandValue_ComputeNet_CountMatches()
    {
        Assert.Equal(11, CasinoGameModule.CardValue("A"));
        Assert.Equal(10, CasinoGameModule.CardValue("K"));
        Assert.Equal(7, CasinoGameModule.CardValue("7"));

        var hand = new List<CasinoGameModule.Card>
        {
            new("A", "♠️", 11),
            new("K", "♥️", 10)
        };
        Assert.Equal(21, CasinoGameModule.HandValue(hand));
        hand.Add(new CasinoGameModule.Card("5", "♦️", 5));
        Assert.Equal(16, CasinoGameModule.HandValue(hand));

        Assert.Equal(5m, CasinoGameModule.ComputeNet(true, true, 5, 5));
        Assert.Equal(-5m, CasinoGameModule.ComputeNet(true, false, 5, 5));
        Assert.Equal(0m, CasinoGameModule.ComputeNet(false, true, 5, 5));

        Assert.Equal(3, CasinoGameModule.CountMatches("🍒", "🍒", "🍒"));
        Assert.Equal(2, CasinoGameModule.CountMatches("🍒", "🍒", "🍋"));
        Assert.Equal(0, CasinoGameModule.CountMatches("🍒", "🍋", "🔔"));
        Assert.Equal("heads", CasinoGameModule.NormalizeHeadsTails("head"));
        Assert.Equal("tails", CasinoGameModule.NormalizeHeadsTails("tails"));
        Assert.Equal(2.5m, CasinoGameModule.ParseBet("2.5"));
    }
}
