using System.Net;
using System.Text.Json;
using GPT.CLI;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class OpenAILogicStructuredOutputTests
{
    [Fact]
    public void ResolveOpenAiV1Endpoint_normalizes_base_domain()
    {
        Assert.Equal("https://api.openai.com/v1", OpenAILogic.ResolveOpenAiV1Endpoint(null));
        Assert.Equal("https://example.com/v1", OpenAILogic.ResolveOpenAiV1Endpoint("https://example.com/v1"));
        Assert.Equal("https://example.com/v1", OpenAILogic.ResolveOpenAiV1Endpoint("https://example.com/v1/"));
        Assert.Equal("https://example.com/v1", OpenAILogic.ResolveOpenAiV1Endpoint("https://example.com/v1/responses"));
        Assert.Equal("https://example.com/v1", OpenAILogic.ResolveOpenAiV1Endpoint("https://example.com"));
    }

    [Fact]
    public async Task Structured_json_posts_strict_json_schema_to_responses()
    {
        var handler = new StubHttpMessageHandler
        {
            ResponseBody = StructuredJsonResponseBody("""{"npcs":[{"name":"Mira","concept":"witch","role":"ally"}],"encounters":[],"locations":[]}""")
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test", BaseDomain = "https://example.com/v1" },
            new HttpClient(handler));

        var response = await logic.CreateStructuredJsonCompletionAsync(
            StructuredRequest(),
            "dnd_draft_proposals",
            BinaryData.FromString("""{"type":"object","properties":{"npcs":{"type":"array","items":{"type":"string"}}},"required":["npcs"],"additionalProperties":false}"""));

        Assert.Equal(1, handler.SendCount);
        Assert.Equal("https://example.com/v1/responses", handler.LastRequest.RequestUri.ToString());
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("\"type\":\"json_schema\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"dnd_draft_proposals\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"json_object\"", handler.LastBody, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(handler.LastBody))
        {
            var format = doc.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        Assert.Contains("Mira", response.Choices[0].Message.Content, StringComparison.Ordinal);
        Assert.Equal("stop", response.Choices[0].FinishReason);
    }

    [Fact]
    public async Task Structured_json_maps_http_error()
    {
        var handler = new StubHttpMessageHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":{"message":"invalid schema"}}"""
        };
        var logic = new OpenAILogic(
            new GptOptions { ApiKey = "sk-test" },
            new HttpClient(handler));

        var response = await logic.CreateStructuredJsonCompletionAsync(
            StructuredRequest(),
            "dnd_draft_proposals",
            BinaryData.FromString("""{"type":"object","properties":{},"additionalProperties":false}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.HttpStatusCode);
        Assert.False(response.Successful);
    }

    [Fact]
    public async Task Structured_json_without_api_key_does_not_http()
    {
        var handler = new StubHttpMessageHandler();
        var logic = new OpenAILogic(
            new GptOptions(),
            new HttpClient(handler));

        var response = await logic.CreateStructuredJsonCompletionAsync(
            StructuredRequest(),
            "dnd_draft_proposals",
            BinaryData.FromString("{}"));

        Assert.Equal(0, handler.SendCount);
        Assert.Equal(HttpStatusCode.Unauthorized, response.HttpStatusCode);
    }

    private static ChatCompletionCreateRequest StructuredRequest()
    {
        return new ChatCompletionCreateRequest
        {
            Model = "gpt-5.6-sol",
            MaxCompletionTokens = 2048,
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System, "Return schema JSON."),
                new(ChatCompletionRole.User, "Invent NPCs.")
            }
        };
    }

    private static string StructuredJsonResponseBody(string outputText)
    {
        var encoded = JsonSerializer.Serialize(outputText);
        return $$"""
            {
              "id": "resp_1",
              "object": "response",
              "created_at": 1741476542,
              "status": "completed",
              "model": "gpt-5.6-sol",
              "output": [
                {
                  "type": "message",
                  "id": "msg_1",
                  "status": "completed",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "output_text",
                      "text": {{encoded}},
                      "annotations": []
                    }
                  ]
                }
              ],
              "parallel_tool_calls": false,
              "tools": [],
              "usage": {
                "input_tokens": 10,
                "output_tokens": 20,
                "total_tokens": 30,
                "input_tokens_details": { "cached_tokens": 0 },
                "output_tokens_details": { "reasoning_tokens": 0 }
              }
            }
            """;
    }
}
