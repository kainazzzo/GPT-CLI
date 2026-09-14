using GPT.CLI.Chat.Discord;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class BuildDiscordMessageLinkTests
{
    [Fact]
    public void Happy_path_builds_discord_url()
    {
        var channel = new InstructionGPT.ChannelState { GuildId = 11, ChannelId = 22 };
        Assert.Equal("https://discord.com/channels/11/22/33", InstructionGPT.BuildDiscordMessageLink(channel, 33));
    }

    [Theory]
    [InlineData(0, 22, 33)]
    [InlineData(11, 0, 33)]
    [InlineData(11, 22, 0)]
    public void Zero_ids_return_null(ulong guildId, ulong channelId, ulong messageId)
    {
        var channel = new InstructionGPT.ChannelState { GuildId = guildId, ChannelId = channelId };
        Assert.Null(InstructionGPT.BuildDiscordMessageLink(channel, messageId));
    }

    [Fact]
    public void Null_channel_returns_null()
    {
        Assert.Null(InstructionGPT.BuildDiscordMessageLink(null, 1));
    }
}
