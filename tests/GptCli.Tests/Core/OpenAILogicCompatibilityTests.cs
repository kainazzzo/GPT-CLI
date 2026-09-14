using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class OpenAILogicCompatibilityTests
{
    [Theory]
    [InlineData("gpt-6-astra")]
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
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGpt6Family_detects_prefix(string model, bool expected)
    {
        Assert.Equal(expected, OpenAILogic.IsGpt6Family(model));
    }

    [Fact]
    public void RequiresResponsesForTools_only_for_gpt6_with_tools()
    {
        var withTools = SampleRequest("gpt-6-astra");
        withTools.Tools = new List<ToolDefinition> { new() { Type = "function" } };
        Assert.True(OpenAILogic.RequiresResponsesForTools(withTools));

        var noTools = SampleRequest("gpt-6-astra");
        Assert.False(OpenAILogic.RequiresResponsesForTools(noTools));

        var oldModel = SampleRequest("gpt-4o");
        oldModel.Tools = withTools.Tools;
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
