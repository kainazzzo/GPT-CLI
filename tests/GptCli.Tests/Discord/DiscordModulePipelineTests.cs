using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Discord;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Chat.Discord.Modules;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class DiscordModulePipelineTests
{
    [Fact]
    public void OrderModules_topological_order_and_skips_invalid()
    {
        var logs = new List<string>();
        var a = new FakeFeatureModule { Id = "a", Name = "A" };
        var b = new FakeFeatureModule { Id = "b", Name = "B", DependsOn = new[] { "a" } };
        var missing = new FakeFeatureModule { Id = "c", Name = "C", DependsOn = new[] { "nope" } };
        var cycle1 = new FakeFeatureModule { Id = "d", Name = "D", DependsOn = new[] { "e" } };
        var cycle2 = new FakeFeatureModule { Id = "e", Name = "E", DependsOn = new[] { "d" } };
        var dup = new FakeFeatureModule { Id = "a", Name = "A-dup" };
        var empty = new FakeFeatureModule { Id = " ", Name = "blank" };

        var ordered = DiscordModulePipeline.OrderModules(
            new IFeatureModule[] { b, a, missing, cycle1, cycle2, dup, empty },
            logs.Add);

        Assert.Equal(new[] { "a", "b" }, ordered.Select(m => m.Id).ToArray());
        Assert.Contains(logs, l => l.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Contains("empty id", StringComparison.OrdinalIgnoreCase) || l.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetGptCliFunctions_skips_duplicate_tool_names()
    {
        var logs = new List<string>();
        var first = new FakeFeatureModule
        {
            Id = "one",
            Functions = new[] { new GptCliFunction { ToolName = "shared", Description = "first" } }
        };
        var second = new FakeFeatureModule
        {
            Id = "two",
            Functions = new[]
            {
                new GptCliFunction { ToolName = "shared", Description = "second" },
                new GptCliFunction { ToolName = "unique", Description = "ok" }
            }
        };

        var pipeline = DiscordModulePipeline.CreateForTests(new IFeatureModule[] { first, second }, log: logs.Add);
        var fns = pipeline.GetGptCliFunctions();
        Assert.Equal(new[] { "shared", "unique" }, fns.Select(f => f.ToolName).ToArray());
        Assert.Equal("first", fns.First(f => f.ToolName == "shared").Description);
        Assert.Contains(logs, l => l.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSlashCommandContributions_swallows_module_exceptions()
    {
        var logs = new List<string>();
        var throwing = new FakeFeatureModule { Id = "boom", ThrowOnSlash = new InvalidOperationException("nope") };
        var ok = new FakeFeatureModule
        {
            Id = "ok",
            Slash = new[] { SlashCommandContribution.TopLevel(new SlashCommandOptionBuilder { Name = "ok", Description = "ok" }) }
        };

        var pipeline = DiscordModulePipeline.CreateForTests(new IFeatureModule[] { throwing, ok }, log: logs.Add);
        var contributions = pipeline.GetSlashCommandContributions();
        Assert.Single(contributions);
        Assert.Contains(logs, l => l.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAdditionalMessageContextAsync_concatenates_and_isolates_failures()
    {
        var logs = new List<string>();
        var a = new FakeFeatureModule
        {
            Id = "a",
            ExtraContext = new[] { new ChatMessage(ChatCompletionRole.System, "from-a") }
        };
        var boom = new FakeFeatureModule { Id = "boom", ThrowOnContext = new InvalidOperationException("fail") };
        var b = new FakeFeatureModule
        {
            Id = "b",
            ExtraContext = new[] { new ChatMessage(ChatCompletionRole.System, "from-b") }
        };

        var pipeline = DiscordModulePipeline.CreateForTests(new IFeatureModule[] { a, boom, b }, log: logs.Add);
        var messages = await pipeline.GetAdditionalMessageContextAsync(message: null, channel: null, cancellationToken: CancellationToken.None);
        Assert.Equal(2, messages.Count);
        Assert.Equal("from-a", messages[0].Content);
        Assert.Equal("from-b", messages[1].Content);
        Assert.Contains(logs, l => l.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnReadyAsync_does_not_throw_when_a_module_fails()
    {
        var logs = new List<string>();
        var boom = new FakeFeatureModule { Id = "boom", ThrowOnReady = new InvalidOperationException("ready-fail") };
        var pipeline = DiscordModulePipeline.CreateForTests(new IFeatureModule[] { boom }, log: logs.Add);
        await pipeline.OnReadyAsync(CancellationToken.None);
        Assert.Contains(logs, l => l.Contains("ready-fail", StringComparison.OrdinalIgnoreCase));
    }
}
