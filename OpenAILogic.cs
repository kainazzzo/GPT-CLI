using GPT.CLI.Embeddings;
using OpenAI.GPT3.Interfaces;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;
using Microsoft.DeepDev;

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

    public async Task<EmbeddingCreateResponse> CreateEmbedding(EmbeddingCreateRequest request)
    {
        return await _openAIService.Embeddings.CreateEmbedding(request);
    }

    public async Task CreateEmbeddings(List<Document> documents)
    {
        var embeddings = await _openAIService.Embeddings.CreateEmbedding(new EmbeddingCreateRequest()
        {
            Model = Models.TextEmbeddingAdaV2,
            InputAsList = documents.Select(d => d.Text).ToList()
        });

        for (int i = 0; i < embeddings.Data.Count; i++)
        {
            documents[i].Embedding = embeddings.Data[i].Embedding;
        }
    }

    public async Task<List<double>> GetEmbeddingForPrompt(string prompt)
    {
        return (await _openAIService.Embeddings.CreateEmbedding(new() { Input = prompt, Model = Models.TextEmbeddingAdaV2 }))
            .Data.First().Embedding;
    }

    public static int CountTokens(string prompt, string modelName)
    {
        var tokenizer = TokenizerBuilder.CreateByModelName(modelName);
        var encoded = tokenizer.Encode(prompt, new List<string>());
        return encoded.Count;
    }
}
