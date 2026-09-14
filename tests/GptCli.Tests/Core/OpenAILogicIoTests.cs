using System.Net;
using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using GPT.CLI;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class OpenAILogicIoTests
{
    [Fact]
    public async Task CreateChatCompletionAsync_delegates_to_service_for_non_gpt6_tools()
    {
        var fake = new FakeOpenAIService();
        var logic = new OpenAILogic(fake.Service, new GptOptions { Model = "gpt-4o", ApiKey = "sk-test" });
        var request = new ChatCompletionCreateRequest
        {
            Model = "gpt-4o",
            Temperature = 0.5f,
            Messages = new List<ChatMessage> { new(ChatCompletionRole.User, "hi") },
            Tools = new List<ToolDefinition> { new() { Type = "function" } }
        };

        var response = await logic.CreateChatCompletionAsync(request);
        Assert.Equal(1, fake.ChatCalls);
        Assert.Equal("stub-reply", response.Choices[0].Message.Content);
        Assert.Equal(0.5f, request.Temperature);
    }

    [Fact]
    public async Task CreateChatCompletionAsync_gpt6_without_tools_still_uses_chat_completions()
    {
        var fake = new FakeOpenAIService();
        var logic = new OpenAILogic(fake.Service, new GptOptions { Model = "gpt-6-astra", ApiKey = "sk-test" });
        var request = new ChatCompletionCreateRequest
        {
            Model = "gpt-6-astra",
            Temperature = 0.5f,
            Messages = new List<ChatMessage> { new(ChatCompletionRole.User, "hi") }
        };

        await logic.CreateChatCompletionAsync(request);
        Assert.Equal(1, fake.ChatCalls);
        Assert.Null(request.Temperature);
    }

    [Fact]
    public async Task CreateChatCompletionAsync_gpt6_tools_without_api_key_fails_without_http()
    {
        var handler = new StubHttpMessageHandler();
        var logic = new OpenAILogic(
            new FakeOpenAIService().Service,
            new GptOptions { Model = "gpt-6-astra" },
            new HttpClient(handler));
        var request = Gpt6ToolsRequest();

        var response = await logic.CreateChatCompletionAsync(request);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(HttpStatusCode.Unauthorized, response.HttpStatusCode);
        Assert.Contains("API key", response.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateChatCompletionAsync_gpt6_tools_with_empty_messages_is_bad_request()
    {
        var handler = new StubHttpMessageHandler();
        var logic = new OpenAILogic(
            new FakeOpenAIService().Service,
            new GptOptions { Model = "gpt-6-astra", ApiKey = "sk-test" },
            new HttpClient(handler));
        var request = Gpt6ToolsRequest();
        request.Messages = new List<ChatMessage>();

        var response = await logic.CreateChatCompletionAsync(request);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(HttpStatusCode.BadRequest, response.HttpStatusCode);
    }

    [Fact]
    public async Task CreateEmbeddings_writes_vectors_onto_documents()
    {
        var fake = new FakeOpenAIService { Embedding = new List<double> { 0.2, 0.8 } };
        var logic = new OpenAILogic(fake.Service, new GptOptions());
        var docs = new List<GPT.CLI.Embeddings.Document>
        {
            new() { Text = "a" },
            new() { Text = "b" }
        };

        await logic.CreateEmbeddings(docs);
        Assert.Equal(new[] { 0.2, 0.8 }, docs[0].Embedding);
        Assert.Equal(new[] { 0.2, 0.8 }, docs[1].Embedding);
        Assert.Equal(2, fake.LastEmbedRequest.InputAsList.Count);
    }

    [Fact]
    public async Task GetEmbeddingForPrompt_returns_fake_vector()
    {
        var fake = new FakeOpenAIService { Embedding = new List<double> { 9, 8, 7 } };
        var logic = new OpenAILogic(fake.Service, new GptOptions());
        var vector = await logic.GetEmbeddingForPrompt("hello");
        Assert.Equal(new[] { 9.0, 8.0, 7.0 }, vector);
    }

    [Fact]
    public async Task CountTokensAsync_is_positive_for_gpt4()
    {
        var count = await OpenAILogic.CountTokensAsync("hello world", "gpt-4");
        Assert.True(count > 0);
    }

    [Fact]
    public void ConvertMessages_skips_null_and_unknown_roles()
    {
        var converted = OpenAILogic.ConvertMessages(new List<ChatMessage>
        {
            null,
            new(ChatCompletionRole.User, "hi"),
            new(ChatCompletionRole.Assistant, ""),
            new(ChatCompletionRole.System, "sys")
        });
        Assert.Contains(converted, m => string.Equals(m["role"]?.ToString(), "user", StringComparison.Ordinal) && (string)m["content"] == "hi");
        Assert.Contains(converted, m => string.Equals(m["role"]?.ToString(), "assistant", StringComparison.Ordinal));
        Assert.Contains(converted, m => string.Equals(m["role"]?.ToString(), "system", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "https://api.openai.com/v1/responses")]
    [InlineData("", "https://api.openai.com/v1/responses")]
    [InlineData("https://example.com/v1", "https://example.com/v1/responses")]
    [InlineData("https://example.com/v1/responses", "https://example.com/v1/responses")]
    [InlineData("https://example.com", "https://example.com/v1/responses")]
    public void ResolveResponsesEndpoint_normalizes_base_domain(string input, string expected)
    {
        Assert.Equal(expected, OpenAILogic.ResolveResponsesEndpoint(input));
    }

    [Fact]
    public void ResolveResponsesMaxOutputTokens_floors_at_2048()
    {
        Assert.Equal(2048, OpenAILogic.ResolveResponsesMaxOutputTokens(null));
        Assert.Equal(2048, OpenAILogic.ResolveResponsesMaxOutputTokens(10));
        Assert.Equal(4000, OpenAILogic.ResolveResponsesMaxOutputTokens(4000));
    }

    private static ChatCompletionCreateRequest Gpt6ToolsRequest()
    {
        return new ChatCompletionCreateRequest
        {
            Model = "gpt-6-astra",
            Messages = new List<ChatMessage> { new(ChatCompletionRole.User, "call a tool") },
            Tools = new List<ToolDefinition>
            {
                new()
                {
                    Type = "function",
                    Function = new FunctionDefinition { Name = "ping", Description = "ping" }
                }
            }
        };
    }
}
