using GPT.CLI.Chat.Discord;
using PollModuleExample;
using Xunit;

namespace GptCli.Modules.Tests.Poll;

public sealed class PollModuleTests
{
    private static InstructionGPT.ChannelState Channel()
        => new() { Polls = new InstructionGPT.PollState() };

    [Fact]
    public void TryParseMessageCommand_parses_create_and_vote()
    {
        Assert.True(PollModule.TryParseMessageCommand("!poll create Lunch pizza, salad", out var create));
        Assert.Equal("create", create.Command);
        Assert.Equal(new[] { "Lunch", "pizza,", "salad" }, create.Args);

        Assert.True(PollModule.TryParseMessageCommand("!poll vote 1 2", out var vote));
        Assert.Equal("vote", vote.Command);
        Assert.Equal(new[] { "1", "2" }, vote.Args);

        Assert.False(PollModule.TryParseMessageCommand("hello", out _));
        Assert.True(PollModule.TryParseMessageCommand("!poll", out var list));
        Assert.Equal("list", list.Command);
    }

    [Fact]
    public void Create_vote_list_results_close_delete_lifecycle()
    {
        var channel = Channel();
        Assert.True(PollModule.TryParseMessageCommand("!poll create Lunch pizza,salad", out var createReq));
        var created = PollModule.ExecutePollAction(null, channel, 10, createReq);
        Assert.Contains("created", created.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, channel.Polls.Polls[0].Id);

        Assert.True(PollModule.TryParseMessageCommand("!poll vote 1 2", out var voteReq));
        var voted = PollModule.ExecutePollAction(null, channel, 10, voteReq);
        Assert.Contains("Vote recorded", voted.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, channel.Polls.Polls[0].Votes[10]);

        var listed = PollModule.ListPolls(channel);
        Assert.Contains("#1", listed.PlainText, StringComparison.Ordinal);
        Assert.Contains("[open]", listed.PlainText, StringComparison.Ordinal);

        var results = PollModule.ShowResults(channel, new[] { "1" });
        Assert.Contains("pizza", results.Details ?? results.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("salad", results.Details ?? results.PlainText, StringComparison.OrdinalIgnoreCase);

        var closed = PollModule.ClosePoll(channel, new[] { "1" });
        Assert.Contains("closed", closed.PlainText, StringComparison.OrdinalIgnoreCase);
        var voteClosed = PollModule.VotePoll(channel, 11, new[] { "1", "1" });
        Assert.Contains("closed", voteClosed.PlainText, StringComparison.OrdinalIgnoreCase);

        var deleted = PollModule.DeletePoll(channel, new[] { "1" });
        Assert.Contains("deleted", deleted.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(channel.Polls.Polls);
    }

    [Fact]
    public void CreatePoll_requires_two_options_and_unknown_command_fails()
    {
        var channel = Channel();
        var tooFew = PollModule.CreatePoll(channel, 1, new[] { "Q", "only-one" });
        Assert.Contains("two options", tooFew.PlainText, StringComparison.OrdinalIgnoreCase);

        Assert.True(PollModule.TryParseMessageCommand("!poll nope", out var unknown));
        var result = PollModule.ExecutePollAction(null, channel, 1, unknown);
        Assert.Contains("Unknown", result.PlainText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitOptions_trims_and_drops_empties()
    {
        Assert.Equal(new[] { "a", "b" }, PollModule.SplitOptions("a, b, ,"));
    }
}
