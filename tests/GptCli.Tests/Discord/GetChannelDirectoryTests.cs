using GPT.CLI.Chat.Discord;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class GetChannelDirectoryTests
{
    [Fact]
    public void Combines_sanitized_guild_and_channel_names()
    {
        var state = new InstructionGPT.ChannelState
        {
            GuildName = "Hello World",
            GuildId = 1,
            ChannelName = "general chat",
            ChannelId = 2
        };

        var expected = Path.Combine("channels", "Hello-World_1", "general-chat_2");
        Assert.Equal(expected, InstructionGPT.GetChannelDirectory(state));
    }

    [Fact]
    public void Missing_names_use_defaults()
    {
        var state = new InstructionGPT.ChannelState { GuildId = 9, ChannelId = 8 };
        var expected = Path.Combine("channels", "guild_9", "channel_8");
        Assert.Equal(expected, InstructionGPT.GetChannelDirectory(state));
    }
}
