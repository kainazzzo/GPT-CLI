using OpenAI.GPT3.Interfaces;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI;

public class OpenAILogic
{
    private readonly IOpenAIService _openAIService;

    public OpenAILogic(IOpenAIService openAIService)
    {
        _openAIService = openAIService;
    }

    public async Task<ChatCompletionCreateResponse> CreateChatCompletionAsync(ChatCompletionCreateRequest request)
    {
        return await _openAIService.ChatCompletion.CreateCompletion(request);
    }

    public IAsyncEnumerable<ChatCompletionCreateResponse> CreateChatCompletionAsyncEnumerable(ChatCompletionCreateRequest request)
    {
        return _openAIService.ChatCompletion.CreateCompletionAsStream(request);
    }
}
