using Discord;
using GPT.CLI.Chat.Discord;
using NSubstitute;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class DiscordTypingTests
{
    [Fact]
    public void Begin_null_channel_is_noop()
    {
        var typing = DiscordTyping.Begin(null);
        typing.Dispose();
    }

    [Fact]
    public void Begin_fake_channel_disposes_safely()
    {
        var channel = Substitute.For<IMessageChannel>();
        channel.TriggerTypingAsync(Arg.Any<RequestOptions>()).Returns(Task.CompletedTask);
        var typing = DiscordTyping.Begin(channel);
        typing.Dispose();
    }
}
