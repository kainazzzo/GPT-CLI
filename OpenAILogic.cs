using OpenAI.GPT3.Interfaces;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;
using System.Text.Json;

namespace GptConsoleApp;

public class OpenAILogic
{
    private readonly IOpenAIService _openAIService;

    public OpenAILogic(IOpenAIService openAIService)
    {
        _openAIService = openAIService;
    }

    public async Task<CompletionCreateResponse> CreateCompletionAsync(GptParameters parameters)
    {
        return await _openAIService.Completions.CreateCompletion(new CompletionCreateRequest()
        {
            Prompt = parameters.Prompt,
            Model = parameters.Model,
            BestOf = parameters.BestOf,
            MaxTokens = parameters.MaxTokens,
            N = parameters.N,
            Suffix = parameters.Suffix,
            Temperature = (float?)parameters.Temperature,
            TopP = (float?)parameters.TopP,
            Stream = parameters.Stream,
            LogProbs = parameters.Logprobs,
            Echo = parameters.Echo,
            Stop = parameters.Stop,
            PresencePenalty = (float?)parameters.PresencePenalty,
            FrequencyPenalty = (float?)parameters.FrequencyPenalty,
            LogitBias = parameters.LogitBias == null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias)
        });
    }
}
