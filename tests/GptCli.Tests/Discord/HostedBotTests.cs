using Discord;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using GptCli.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace GptCli.Tests.Discord;

[Collection("fs")]
public sealed class HostedBotTests
{
    [Fact]
    public void GetOrCreateChannelState_creates_fallback_state()
    {
        var bot = BotFactory.Create();
        var host = (IDiscordModuleHost)bot;
        var state = host.GetOrCreateChannelState(42);
        Assert.Equal(42ul, state.ChannelId);
        Assert.Equal("unknown", state.GuildName);
        Assert.Equal("unknown", state.ChannelName);
        Assert.NotNull(state.Options);
        Assert.NotNull(state.InstructionChat);
        Assert.Equal("be nice", state.Options.LearningPersonalityPrompt);
        Assert.Null(state.InstructionChat.ChatBotState.Parameters.ApiKey);
        Assert.Null(state.InstructionChat.ChatBotState.Parameters.BotToken);
    }

    [Fact]
    public void TryAcquirePromptDebounceSlot_blocks_immediate_repeat()
    {
        var bot = BotFactory.Create();
        Assert.True(bot.TryAcquirePromptDebounceSlot(1, 2, 5, out var firstWait));
        Assert.Equal(TimeSpan.Zero, firstWait);
        Assert.False(bot.TryAcquirePromptDebounceSlot(1, 2, 5, out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.True(bot.TryAcquirePromptDebounceSlot(1, 2, 0, out _));
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(500, 300)]
    public void NormalizePromptDebounceSeconds_clamps(int input, int expected)
    {
        Assert.Equal(expected, InstructionGPT.NormalizePromptDebounceSeconds(input));
    }

    [Fact]
    public void ResolveConfiguredPromptDebounceSeconds_reads_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["GPT:PromptDebounceSeconds"] = "12" })
            .Build();
        Assert.Equal(12, InstructionGPT.ResolveConfiguredPromptDebounceSeconds(config));
        Assert.Equal(5, InstructionGPT.ResolveConfiguredPromptDebounceSeconds(null));
    }

    [Fact]
    public void StripBotMentions_removes_mention_tokens()
    {
        Assert.Equal("hello", InstructionGPT.StripBotMentions("<@99> hello", 99));
        Assert.Equal("hello", InstructionGPT.StripBotMentions("<@!99> hello", 99));
        Assert.Equal(string.Empty, InstructionGPT.StripBotMentions("  ", 1));
    }

    [Fact]
    public void IsChannelGuildMatch_compares_guild_ids()
    {
        var bot = BotFactory.Create();
        var state = bot.CreateBaseChannelState(7);
        state.GuildId = 11;

        var matching = Substitute.For<IMessageChannel, IGuildChannel>();
        matching.Id.Returns(7ul);
        ((IGuildChannel)matching).GuildId.Returns(11ul);
        Assert.True(bot.IsChannelGuildMatch(state, matching, "test"));

        var mismatch = Substitute.For<IMessageChannel, IGuildChannel>();
        mismatch.Id.Returns(7ul);
        ((IGuildChannel)mismatch).GuildId.Returns(99ul);
        Assert.False(bot.IsChannelGuildMatch(state, mismatch, "test"));

        Assert.False(bot.IsChannelGuildMatch(null, matching, "test"));
        Assert.False(bot.IsChannelGuildMatch(state, null, "test"));
    }
}
