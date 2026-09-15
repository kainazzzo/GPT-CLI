using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class OpenAILogicCompatibilityTests
{
    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("GPT-5")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public void ApplyModelCompatibility_strips_sampling_for_restricted_models(string model)
    {
        var request = SampleRequest(model);
        OpenAILogic.ApplyModelCompatibility(request);
        Assert.Null(request.Temperature);
        Assert.Null(request.TopP);
        Assert.Null(request.LogProbs);
        Assert.Null(request.TopLogprobs);
        Assert.Null(request.LogitBias);
        Assert.Null(request.PresencePenalty);
        Assert.Null(request.FrequencyPenalty);
    }

    [Fact]
    public void ApplyModelCompatibility_leaves_sampling_for_older_models()
    {
        var request = SampleRequest("gpt-4o");
        OpenAILogic.ApplyModelCompatibility(request);
        Assert.Equal(0.7f, request.Temperature);
        Assert.Equal(0.9f, request.TopP);
        Assert.True(request.LogProbs);
        Assert.Equal(2, request.TopLogprobs);
        Assert.NotNull(request.LogitBias);
        Assert.Equal(0.1f, request.PresencePenalty);
        Assert.Equal(0.2f, request.FrequencyPenalty);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", false)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGpt6Family_detects_prefix(string model, bool expected)
    {
        Assert.Equal(expected, OpenAILogic.IsGpt6Family(model));
    }

    [Theory]
    [InlineData(null, "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-5.2", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-5.2-nano", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("GPT-5", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-6-sol", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt-4o", "gpt-5.6-sol", "gpt-4o")]
    public void ResolveCurrentTextModel_replaces_stale_gpt5(string requested, string fallback, string expected)
    {
        Assert.Equal(expected, OpenAILogic.ResolveCurrentTextModel(requested, fallback));
    }

    [Theory]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("GPT-5.6-sol", true)]
    [InlineData("gpt-5.2", false)]
    [InlineData("gpt-6-astra", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGpt56Family_detects_prefix(string model, bool expected)
    {
        Assert.Equal(expected, OpenAILogic.IsGpt56Family(model));
    }

    [Fact]
    public void RequiresResponsesForTools_for_gpt6_and_gpt56_with_tools()
    {
        var gpt6Tools = SampleRequest("gpt-6-astra");
        gpt6Tools.Tools = new List<ToolDefinition> { new() { Type = "function" } };
        Assert.True(OpenAILogic.RequiresResponsesForTools(gpt6Tools));

        var gpt56Tools = SampleRequest("gpt-5.6-sol");
        gpt56Tools.Tools = gpt6Tools.Tools;
        Assert.True(OpenAILogic.RequiresResponsesForTools(gpt56Tools));

        var gpt56NoTools = SampleRequest("gpt-5.6-sol");
        Assert.True(OpenAILogic.RequiresResponsesApi(gpt56NoTools));

        var gpt52Tools = SampleRequest("gpt-5.2");
        gpt52Tools.Tools = gpt6Tools.Tools;
        Assert.False(OpenAILogic.RequiresResponsesForTools(gpt52Tools));

        var oldModel = SampleRequest("gpt-4o");
        oldModel.Tools = gpt6Tools.Tools;
        Assert.False(OpenAILogic.RequiresResponsesForTools(oldModel));
    }

    private static ChatCompletionCreateRequest SampleRequest(string model)
    {
        return new ChatCompletionCreateRequest
        {
            Model = model,
            Temperature = 0.7f,
            TopP = 0.9f,
            LogProbs = true,
            TopLogprobs = 2,
            LogitBias = new Dictionary<string, double> { ["1"] = 1 },
            PresencePenalty = 0.1f,
            FrequencyPenalty = 0.2f
        };
    }
}
