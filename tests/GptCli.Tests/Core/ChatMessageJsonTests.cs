using System.Text.Json;
using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class ChatMessageJsonTests
{
    [Fact]
    public void ChatMessage_roundtrips_betalgo_channel_payload()
    {
        const string json = """
            {
              "role": "system",
              "reasoning_content": null,
              "content": "hello world",
              "name": null,
              "tool_call_id": null,
              "function_call": null,
              "tool_calls": null
            }
            """;

        var message = JsonSerializer.Deserialize<ChatMessage>(json);
        Assert.NotNull(message);
        Assert.Equal(ChatCompletionRole.System, message.Role);
        Assert.Equal("hello world", message.Content);

        var written = JsonSerializer.Serialize(message);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("system", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal("hello world", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void ChatMessage_roundtrips_tool_calls()
    {
        var message = new ChatMessage(ChatCompletionRole.Assistant, "reply")
        {
            ToolCalls = new List<ToolCall>
            {
                new()
                {
                    Id = "c1",
                    Type = "function",
                    FunctionCall = new FunctionCall { Name = "ping", Arguments = "{\"x\":1}" }
                }
            }
        };

        var json = JsonSerializer.Serialize(message);
        var restored = JsonSerializer.Deserialize<ChatMessage>(json);
        Assert.Equal(ChatCompletionRole.Assistant, restored.Role);
        Assert.Equal("reply", restored.Content);
        Assert.Equal("ping", restored.ToolCalls[0].FunctionCall.Name);
        Assert.Contains("\"x\":1", restored.ToolCalls[0].FunctionCall.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyDefinition_serializes_json_schema_names()
    {
        var prop = new PropertyDefinition
        {
            Type = "object",
            AdditionalProperties = false,
            Properties = new Dictionary<string, PropertyDefinition>
            {
                ["term"] = new PropertyDefinition { Type = "string", Description = "The term" }
            },
            Required = new List<string> { "term" }
        };

        var json = JsonSerializer.Serialize(prop);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", doc.RootElement.GetProperty("properties").GetProperty("term").GetProperty("type").GetString());
        Assert.Equal("term", doc.RootElement.GetProperty("required")[0].GetString());
    }
}
