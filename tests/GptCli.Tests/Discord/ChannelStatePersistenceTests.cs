using System.Text.Json;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Discord;

[Collection("fs")]
public sealed class ChannelStatePersistenceTests
{
    [Fact]
    public async Task WriteAsync_ReadAsync_roundtrips_channel_state_without_secrets()
    {
        using var cwd = new TempCwd();
        var bot = BotFactory.Create();
        var host = (IDiscordModuleHost)bot;
        var state = host.GetOrCreateChannelState(99);
        state.GuildId = 5;
        state.GuildName = "Guild";
        state.ChannelName = "general";
        state.Options.Enabled = true;
        state.InstructionChat.ChatBotState.Parameters.ApiKey = "sk-should-not-persist";
        state.InstructionChat.ChatBotState.Parameters.BotToken = "bot-should-not-persist";

        using var stream = new MemoryStream();
        await bot.WriteAsync(99, stream);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("sk-should-not-persist", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bot-should-not-persist", json, StringComparison.Ordinal);

        var bot2 = BotFactory.Create();
        stream.Position = 0;
        var restored = await bot2.ReadAsync(99, stream);
        Assert.NotNull(restored);
        Assert.Equal(5ul, restored.GuildId);
        Assert.True(restored.Options.Enabled);
        Assert.Null(restored.InstructionChat.ChatBotState.Parameters.ApiKey);
        Assert.Null(restored.InstructionChat.ChatBotState.Parameters.BotToken);
    }

    [Fact]
    public async Task SaveCachedChannelState_writes_tokenized_file()
    {
        using var cwd = new TempCwd();
        var bot = BotFactory.Create();
        var host = (IDiscordModuleHost)bot;
        var state = host.GetOrCreateChannelState(12);
        state.GuildId = 34;
        state.GuildName = "Guild";
        state.ChannelName = "voice and text";

        await bot.SaveCachedChannelState(12);
        var dir = InstructionGPT.GetChannelDirectory(state);
        Assert.True(Directory.Exists(dir));
        var files = Directory.GetFiles(dir, "*.state.json");
        Assert.Single(files);
        var json = await File.ReadAllTextAsync(files[0]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(12ul, doc.RootElement.GetProperty("channel-id").GetUInt64());
    }
}
