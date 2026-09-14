using Betalgo.Ranul.OpenAI.Interfaces;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using GPT.CLI.Embeddings;
using NSubstitute;

namespace GptCli.Tests.TestDoubles;

internal sealed class FakeOpenAIService
{
    public IOpenAIService Service { get; }
    public ChatCompletionCreateRequest LastChatRequest { get; private set; }
    public EmbeddingCreateRequest LastEmbedRequest { get; private set; }
    public ChatCompletionCreateResponse ChatResponse { get; set; }
    public List<double> Embedding { get; set; } = new() { 1.0, 0.0 };
    public int ChatCalls { get; private set; }
    public int EmbedCalls { get; private set; }

    public FakeOpenAIService()
    {
        ChatResponse = new ChatCompletionCreateResponse
        {
            Id = "chat-1",
            Model = "gpt-4o",
            Choices = new List<ChatChoiceResponse>
            {
                new()
                {
                    Index = 0,
                    FinishReason = "stop",
                    Message = new ChatMessage("assistant", "stub-reply")
                }
            }
        };

        var chat = Substitute.For<IChatCompletionService>();
        chat.CreateCompletion(Arg.Any<ChatCompletionCreateRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ChatCalls++;
                LastChatRequest = ci.Arg<ChatCompletionCreateRequest>();
                return ChatResponse;
            });
        chat.CreateCompletionAsStream(Arg.Any<ChatCompletionCreateRequest>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ChatCalls++;
                LastChatRequest = ci.Arg<ChatCompletionCreateRequest>();
                return SingleAsync(ChatResponse);
            });

        var embeddings = Substitute.For<IEmbeddingService>();
        embeddings.CreateEmbedding(Arg.Any<EmbeddingCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                EmbedCalls++;
                LastEmbedRequest = ci.Arg<EmbeddingCreateRequest>();
                var count = 1;
                if (LastEmbedRequest?.InputAsList != null && LastEmbedRequest.InputAsList.Count > 0)
                {
                    count = LastEmbedRequest.InputAsList.Count;
                }

                return new EmbeddingCreateResponse
                {
                    Data = Enumerable.Range(0, count)
                        .Select(i => new EmbeddingResponse
                        {
                            Index = i,
                            Embedding = new List<double>(Embedding)
                        })
                        .ToList()
                };
            });

        Service = Substitute.For<IOpenAIService>();
        Service.ChatCompletion.Returns(chat);
        Service.Embeddings.Returns(embeddings);
    }

    private static async IAsyncEnumerable<ChatCompletionCreateResponse> SingleAsync(ChatCompletionCreateResponse response)
    {
        yield return response;
        await Task.CompletedTask;
    }
}
