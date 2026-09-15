using System.Net;
using System.Text.Json;
using GPT.CLI;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class OpenAILogicResponsesTests
{
    [Fact]
    public async Task Responses_path_posts_to_endpoint_with_bearer_token()
    {
        var handler = new StubHttpMessageHandler
        {
            ResponseBody = """{"id":"resp_1","output_text":"hello from responses"}"""
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = " sk-secret ", BaseDomain = "https://example.com/v1" },
            new HttpClient(handler));

        var response = await logic.CreateChatCompletionAsync(Gpt6ToolsRequest());
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("https://example.com/v1/responses", handler.LastRequest.RequestUri.ToString());
        Assert.Equal("sk-secret", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("hello from responses", response.Choices[0].Message.Content);
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
    }

    [Fact]
    public async Task Responses_path_maps_function_calls()
    {
        var handler = new StubHttpMessageHandler
        {
            ResponseBody = """
                {
                  "id":"resp_tools",
                  "output":[
                    {"type":"function_call","name":"ping","call_id":"c1","arguments":"{\"x\":1}"}
                  ]
                }
                """
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test" },
            new HttpClient(handler));

        var response = await logic.CreateChatCompletionAsync(Gpt6ToolsRequest());
        Assert.Equal("tool_calls", response.Choices[0].FinishReason);
        Assert.Equal("ping", response.Choices[0].Message.ToolCalls[0].FunctionCall.Name);
        Assert.Contains("\"x\":1", response.Choices[0].Message.ToolCalls[0].FunctionCall.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_path_non_success_maps_error()
    {
        var handler = new StubHttpMessageHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":{"message":"bad model"}}"""
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test" },
            new HttpClient(handler));

        var response = await logic.CreateChatCompletionAsync(Gpt6ToolsRequest());
        Assert.Equal(HttpStatusCode.BadRequest, response.HttpStatusCode);
        Assert.Contains("bad model", response.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Responses_path_invalid_json_is_bad_gateway()
    {
        var handler = new StubHttpMessageHandler { ResponseBody = "not-json" };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test" },
            new HttpClient(handler));

        var response = await logic.CreateChatCompletionAsync(Gpt6ToolsRequest());
        Assert.Equal(HttpStatusCode.BadGateway, response.HttpStatusCode);
    }

    [Fact]
    public async Task CreateChatCompletionAsyncEnumerable_gpt6_tools_yields_single_mapped_result()
    {
        var handler = new StubHttpMessageHandler
        {
            ResponseBody = """{"output_text":"streamed"}"""
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test" },
            new HttpClient(handler));

        var items = new List<string>();
        await foreach (var item in logic.CreateChatCompletionAsyncEnumerable(Gpt6ToolsRequest()))
        {
            items.Add(item.Choices[0].Message.Content);
        }

        Assert.Single(items);
        Assert.Equal("streamed", items[0]);
    }

    [Fact]
    public void MapResponsesToChatCompletion_reads_nested_message_text()
    {
        using var doc = JsonDocument.Parse("""
            {
              "output":[
                {"type":"message","content":[{"type":"output_text","text":"line one"},{"text":"line two"}]}
              ]
            }
            """);
        var mapped = OpenAILogic.MapResponsesToChatCompletion("gpt-5.6-sol", doc.RootElement);
        Assert.Contains("line one", mapped.Choices[0].Message.Content, StringComparison.Ordinal);
        Assert.Contains("line two", mapped.Choices[0].Message.Content, StringComparison.Ordinal);
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
