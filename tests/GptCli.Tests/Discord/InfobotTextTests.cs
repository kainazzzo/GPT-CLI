using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class InfobotTextTests
{
    [Theory]
    [InlineData("foo is bar", "foo", "bar")]
    [InlineData("cats are mammals", "cats", "mammals")]
    [InlineData("Bob was here", "Bob", "here")]
    [InlineData("they were friends", "they", "friends")]
    public void TryParseInfobotSet_splits_on_copula(string content, string term, string fact)
    {
        Assert.True(InfobotModule.TryParseInfobotSet(content, out var parsedTerm, out var parsedFact));
        Assert.Equal(term, parsedTerm);
        Assert.Equal(fact, parsedFact);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no separator here")]
    [InlineData(" is leading")]
    [InlineData("term is ")]
    public void TryParseInfobotSet_rejects_invalid(string content)
    {
        Assert.False(InfobotModule.TryParseInfobotSet(content, out _, out _));
    }

    [Theory]
    [InlineData("The Widget?", "widget")]
    [InlineData("an Apple", "apple")]
    [InlineData("da thing", "thing")]
    [InlineData("  ", null)]
    public void NormalizeTerm_strips_articles_and_question_marks(string input, string expected)
    {
        Assert.Equal(expected, InfobotModule.NormalizeTerm(input));
    }

    [Fact]
    public void PreprocessInfobotQuestion_strips_common_prefixes()
    {
        var cleaned = InfobotModule.PreprocessInfobotQuestion("who is the mayor");
        Assert.Equal("the mayor", cleaned);
    }

    [Fact]
    public void NormalizeInfobotQuery_collapses_noise_and_flags_question_mark()
    {
        var qmark = false;
        var query = InfobotModule.NormalizeInfobotQuery("do you know where the pub is???", ref qmark);
        Assert.True(qmark);
        Assert.DoesNotContain("???", query, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(query));
    }

    [Fact]
    public void SwitchPerson_rewrites_speaker_and_bot_pronouns()
    {
        var switched = InfobotModule.SwitchPerson("I am looking for you", who: "Ada", addressed: true, botName: "Grok");
        Assert.Contains("Ada", switched, StringComparison.Ordinal);
        Assert.DoesNotContain("you", switched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInfobotQueries_includes_original_and_cleaned_forms()
    {
        var queries = InfobotModule.BuildInfobotQueries("what is a widget?", who: "Ada", addressed: false, botName: "Grok");
        Assert.Contains("what is a widget?", queries);
        Assert.True(queries.Count >= 1);
        Assert.Contains(queries, q => q.Contains("widget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilterFactoidsForChannel_treats_zero_as_wildcard()
    {
        var factoids = new[]
        {
            new FactoidEntry { Term = "local", SourceGuildId = 1, SourceChannelId = 2 },
            new FactoidEntry { Term = "any-channel", SourceGuildId = 1, SourceChannelId = 0 },
            new FactoidEntry { Term = "global", SourceGuildId = 0, SourceChannelId = 0 },
            new FactoidEntry { Term = "other", SourceGuildId = 9, SourceChannelId = 9 }
        };

        var filtered = InfobotModule.FilterFactoidsForChannel(factoids, guildId: 1, channelId: 2);
        Assert.Equal(new[] { "local", "any-channel", "global" }, filtered.Select(f => f.Term));
        Assert.Empty(InfobotModule.FilterFactoidsForChannel(null, 1, 2));
    }

    [Fact]
    public void FindMostSimilarFactoids_applies_threshold_and_limit()
    {
        var query = new List<double> { 1, 0 };
        var factoids = new List<FactoidEntry>
        {
            new() { Term = "exact", Embedding = new List<double> { 1, 0 } },
            new() { Term = "close", Embedding = new List<double> { 0.9, 0.1 } },
            new() { Term = "far", Embedding = new List<double> { 0, 1 } },
            new() { Term = "empty", Embedding = new List<double>() }
        };

        var matches = InfobotModule.FindMostSimilarFactoids(factoids, query, limit: 1, threshold: 0.8);
        Assert.Single(matches);
        Assert.Equal("exact", matches[0].Term);
    }

    [Fact]
    public void BuildMatchStatsFromEntries_counts_and_tracks_last_ids()
    {
        var matches = new[]
        {
            new FactoidMatchEntry
            {
                Term = "Widget",
                Query = "what is widget",
                ResponseMessageId = 10,
                UserId = 5,
                MatchedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            },
            new FactoidMatchEntry
            {
                Term = "widget",
                Query = "widget?",
                ResponseMessageId = 11,
                UserId = 6,
                MatchedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z")
            }
        };

        var stats = InfobotModule.BuildMatchStatsFromEntries(matches);
        Assert.Equal(2, stats.TotalMatches);
        Assert.Equal(2, stats.TermCounts["widget"]);
        Assert.Equal((ulong)11, stats.LastResponseMessageIds["widget"]);
        Assert.Equal((ulong)6, stats.LastUserIds["widget"]);
    }
}
