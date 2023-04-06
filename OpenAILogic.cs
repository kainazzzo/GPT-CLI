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

    public async Task<CompletionCreateResponse> CreateCompletionAsync(CompletionCreateRequest request)
    {
        return await _openAIService.Completions.CreateCompletion(request);
    }

    public async Task<EditCreateResponse> CreateEditAsync(EditCreateRequest request)
    {
        return await _openAIService.Edit.CreateEdit(request);
    }

}
