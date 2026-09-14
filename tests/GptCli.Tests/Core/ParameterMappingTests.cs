using System.Text;
using Betalgo.Ranul.OpenAI.Contracts.Enums;
using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class ParameterMappingTests
{
    private static GptOptions Options() => new()
    {
        Prompt = "write a haiku",
        Model = "gpt-6-astra",
        MaxTokens = 1234,
        Temperature = 0.4,
        N = 1,
        Stream = false
    };

    [Fact]
    public async Task MapChatCreate_sets_system_prompt_and_max_completion_tokens()
    {
        var request = await ParameterMapping.MapChatCreate(Options(), openAILogic: null);
        Assert.Equal("gpt-6-astra", request.Model);
        Assert.Equal(1234, request.MaxCompletionTokens);
        Assert.Null(request.MaxTokens);
        Assert.Contains(request.Messages, m =>
            m.Role == ChatCompletionRole.System &&
            string.Equals(m.Content, "write a haiku", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MapChatEdit_wraps_input_then_appends_prompt()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("original text"));
        var request = await ParameterMapping.MapChatEdit(Options(), openAILogic: null, input);
        Assert.Equal("gpt-6-astra", request.Model);
        Assert.Equal(1234, request.MaxCompletionTokens);
        Assert.Contains(request.Messages, m =>
            m.Role == ChatCompletionRole.User &&
            string.Equals(m.Content, "original text", StringComparison.Ordinal));
        Assert.Equal("write a haiku", request.Messages.Last().Content);
        Assert.Equal(ChatCompletionRole.User, request.Messages.Last().Role);
    }
}
