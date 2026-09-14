using Discord;
using GPT.CLI.Chat.Discord;
using NSubstitute;
using PinboardModuleExample;
using Xunit;

namespace GptCli.Modules.Tests.Pinboard;

public sealed class PinboardModuleTests
{
    private static InstructionGPT.ChannelState Channel()
        => new()
        {
            GuildId = 1,
            ChannelId = 22,
            Pinboard = new InstructionGPT.PinboardState()
        };

    [Fact]
    public void TryParseMessageCommand_parses_pin_verbs()
    {
        Assert.True(PinboardModule.TryParseMessageCommand("!pin", out var list));
        Assert.Equal("list", list.Command);
        Assert.True(PinboardModule.TryParseMessageCommand("!pin list", out var list2));
        Assert.Equal("list", list2.Command);
        Assert.True(PinboardModule.TryParseMessageCommand("!pin remove 1", out var remove));
        Assert.Equal("remove", remove.Command);
        Assert.Equal(new[] { "1" }, remove.Args);
        Assert.False(PinboardModule.TryParseMessageCommand("pin it", out _));
    }

    [Fact]
    public void RemovePin_list_search_on_seeded_state()
    {
        var channel = Channel();
        channel.Pinboard.Pins.Add(new InstructionGPT.PinboardEntry
        {
            Id = 1,
            ChannelId = 22,
            MessageId = 100,
            Snippet = "alpha note",
            Note = "keep",
            CreatedUtc = DateTime.UtcNow
        });
        channel.Pinboard.Pins.Add(new InstructionGPT.PinboardEntry
        {
            Id = 2,
            ChannelId = 22,
            MessageId = 101,
            Snippet = "beta other",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
        });

        Assert.Contains("removed", PinboardModule.RemovePin(channel, new[] { "2" }), StringComparison.OrdinalIgnoreCase);
        Assert.Single(channel.Pinboard.Pins);
        Assert.Contains("not found", PinboardModule.RemovePin(channel, new[] { "9" }), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutePinActionAsync_add_validates_args_and_channel()
    {
        var channelState = Channel();
        var discordChannel = Substitute.For<IMessageChannel>();
        discordChannel.Id.Returns(22ul);

        var missing = await PinboardModule.ExecutePinActionAsync(null, channelState, discordChannel, 5, new PinboardModule.PinRequest("add", Array.Empty<string>()));
        Assert.Contains("Provide a message", missing, StringComparison.OrdinalIgnoreCase);

        var foreign = await PinboardModule.ExecutePinActionAsync(
            null,
            channelState,
            discordChannel,
            5,
            new PinboardModule.PinRequest("add", new[] { "https://discord.com/channels/1/99/123" }));
        Assert.Contains("this channel", foreign, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutePinActionAsync_add_inserts_when_message_exists()
    {
        var channelState = Channel();
        var author = Substitute.For<IUser>();
        author.Id.Returns(9ul);
        var message = Substitute.For<IUserMessage>();
        message.Author.Returns(author);
        message.Content.Returns("hello pin");

        var discordChannel = Substitute.For<IMessageChannel>();
        discordChannel.Id.Returns(22ul);
        discordChannel.GetMessageAsync(33ul, Arg.Any<CacheMode>(), Arg.Any<RequestOptions>()).Returns(message);

        var result = await PinboardModule.ExecutePinActionAsync(
            null,
            channelState,
            discordChannel,
            5,
            new PinboardModule.PinRequest("add", new[] { "https://discord.com/channels/1/22/33", "saved" }));
        Assert.Contains("Pinned", result, StringComparison.OrdinalIgnoreCase);
        Assert.Single(channelState.Pinboard.Pins);
        Assert.Equal("hello pin", channelState.Pinboard.Pins[0].Snippet);
        Assert.Equal("saved", channelState.Pinboard.Pins[0].Note);
    }

    [Fact]
    public void TryParseMessageId_accepts_raw_id_and_link()
    {
        Assert.True(PinboardModule.TryParseMessageId("123", out var id, out var channelId));
        Assert.Equal(123ul, id);
        Assert.Equal(0ul, channelId);

        Assert.True(PinboardModule.TryParseMessageId("https://discord.com/channels/1/22/33", out var mid, out var cid));
        Assert.Equal(33ul, mid);
        Assert.Equal(22ul, cid);
        Assert.False(PinboardModule.TryParseMessageId("nope", out _, out _));
    }
}
