using System.Text.Json;
using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class GptOptionsTests
{
    [Fact]
    public void Json_serialize_omits_api_key_and_bot_token()
    {
        var options = new GptOptions
        {
            ApiKey = "sk-secret",
            BotToken = "bot-secret",
            Prompt = "hello",
            Model = "gpt-6-astra"
        };

        var json = JsonSerializer.Serialize(options);
        Assert.DoesNotContain("sk-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bot-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BotToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello", json, StringComparison.Ordinal);
        Assert.Contains("gpt-6-astra", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_deserialize_binds_non_secret_fields()
    {
        var json = """{"Prompt":"hi","Model":"gpt-4o","ClosestMatchLimit":7}""";
        var options = JsonSerializer.Deserialize<GptOptions>(json);
        Assert.NotNull(options);
        Assert.Equal("hi", options.Prompt);
        Assert.Equal("gpt-4o", options.Model);
        Assert.Equal(7, options.ClosestMatchLimit);
        Assert.Null(options.ApiKey);
        Assert.Null(options.BotToken);
    }
}
