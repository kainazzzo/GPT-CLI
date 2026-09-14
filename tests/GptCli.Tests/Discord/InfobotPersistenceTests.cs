using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Discord;

[Collection("fs")]
public sealed class InfobotPersistenceTests
{
    [Fact]
    public async Task SaveFactoidsAsync_writes_json_for_channel()
    {
        using var cwd = new TempCwd();
        var channel = new InstructionGPT.ChannelState
        {
            GuildId = 1,
            GuildName = "g",
            ChannelId = 2,
            ChannelName = "c"
        };
        var entries = new List<FactoidEntry>
        {
            new() { Term = "widget", Text = "a thing", SourceGuildId = 1, SourceChannelId = 2 }
        };

        await InfobotModule.SaveFactoidsAsync(context: null, channel, entries);
        var path = InstructionGPT.GetChannelFactoidFile(channel);
        Assert.True(File.Exists(path));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("widget", json, StringComparison.Ordinal);
        Assert.Contains("a thing", json, StringComparison.Ordinal);

        var filtered = InfobotModule.FilterFactoidsForChannel(entries, 1, 2);
        Assert.Single(filtered);
    }
}
