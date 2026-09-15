using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPT.CLI;

[JsonConverter(typeof(ChatCompletionRoleJsonConverter))]
public readonly struct ChatCompletionRole : IEquatable<ChatCompletionRole>
{
    public static readonly ChatCompletionRole System = new("system");
    public static readonly ChatCompletionRole User = new("user");
    public static readonly ChatCompletionRole Assistant = new("assistant");
    public static readonly ChatCompletionRole Developer = new("developer");
    public static readonly ChatCompletionRole Tool = new("tool");
    public static readonly ChatCompletionRole Function = new("function");

    private readonly string _value;

    public ChatCompletionRole(string value)
    {
        _value = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    public override string ToString() => _value ?? string.Empty;

    public bool Equals(ChatCompletionRole other) =>
        string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object obj) => obj is ChatCompletionRole other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_value ?? string.Empty);

    public static bool operator ==(ChatCompletionRole left, ChatCompletionRole right) => left.Equals(right);

    public static bool operator !=(ChatCompletionRole left, ChatCompletionRole right) => !left.Equals(right);

    public static implicit operator ChatCompletionRole(string value) => new(value);
}

internal sealed class ChatCompletionRoleJsonConverter : JsonConverter<ChatCompletionRole>
{
    public override ChatCompletionRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.String
            ? new ChatCompletionRole(reader.GetString())
            : default;
    }

    public override void Write(Utf8JsonWriter writer, ChatCompletionRole value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public sealed class ChatMessage
{
    public ChatMessage()
    {
    }

    public ChatMessage(ChatCompletionRole role, string content, string name = null, IList<ToolCall> toolCalls = null, string toolCallId = null)
    {
        Role = role;
        Content = content;
        Name = name;
        ToolCalls = toolCalls;
        ToolCallId = toolCallId;
    }

    public ChatMessage(string role, string content)
        : this(new ChatCompletionRole(role), content)
    {
    }

    public ChatMessage(ChatCompletionRole role, IList<MessageContent> contents, string name = null, IList<ToolCall> toolCalls = null, string toolCallId = null)
    {
        Role = role;
        Contents = contents;
        Name = name;
        ToolCalls = toolCalls;
        ToolCallId = toolCallId;
    }

    [JsonPropertyName("role")]
    public ChatCompletionRole? Role { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string ToolCallId { get; set; }

    [JsonPropertyName("function_call")]
    public FunctionCall FunctionCall { get; set; }

    [JsonPropertyName("tool_calls")]
    public IList<ToolCall> ToolCalls { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string ReasoningContent { get; set; }

    [JsonIgnore]
    public string Content { get; set; }

    [JsonIgnore]
    public IList<MessageContent> Contents { get; set; }

    [JsonIgnore]
    public object ContentCalculated => Contents is { Count: > 0 } ? Contents : Content;

    [JsonPropertyName("content")]
    public object ContentPayload
    {
        get
        {
            if (Contents is { Count: > 0 })
            {
                return Contents;
            }

            return Content;
        }
        set
        {
            switch (value)
            {
                case null:
                    Content = null;
                    Contents = null;
                    break;
                case string text:
                    Content = text;
                    Contents = null;
                    break;
                case JsonElement element when element.ValueKind == JsonValueKind.String:
                    Content = element.GetString();
                    Contents = null;
                    break;
                case JsonElement element when element.ValueKind == JsonValueKind.Array:
                    Contents = JsonSerializer.Deserialize<List<MessageContent>>(element.GetRawText());
                    Content = null;
                    break;
                case JsonElement:
                    Content = value.ToString();
                    Contents = null;
                    break;
                case IList<MessageContent> parts:
                    Contents = parts;
                    Content = null;
                    break;
                default:
                    Content = value.ToString();
                    Contents = null;
                    break;
            }
        }
    }
}

public sealed class MessageContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("image_url")]
    public MessageImageUrl ImageUrl { get; set; }

    [JsonIgnore]
    public byte[] ImageBytes { get; set; }

    [JsonIgnore]
    public string MediaType { get; set; }

    public static MessageContent TextContent(string text) => new()
    {
        Type = "text",
        Text = text
    };

    public static MessageContent ImageBinaryContent(byte[] bytes, string mediaType, string detail = "auto")
    {
        var mime = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType.Trim();
        return new MessageContent
        {
            Type = "image_url",
            ImageBytes = bytes,
            MediaType = mime,
            ImageUrl = new MessageImageUrl
            {
                Url = $"data:{mime};base64,{Convert.ToBase64String(bytes ?? Array.Empty<byte>())}",
                Detail = string.IsNullOrWhiteSpace(detail) ? "auto" : detail
            }
        };
    }
}

public sealed class MessageImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; }
}

public sealed class ChatCompletionCreateRequest
{
    public string Model { get; set; }
    public IList<ChatMessage> Messages { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? N { get; set; }
    public bool? Stream { get; set; }
    public string Stop { get; set; }
    public float? PresencePenalty { get; set; }
    public float? FrequencyPenalty { get; set; }
    public Dictionary<string, double> LogitBias { get; set; }
    public string User { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public IList<ToolDefinition> Tools { get; set; }
    public ToolChoice ToolChoice { get; set; }
    public bool? ParallelToolCalls { get; set; }
    public bool? LogProbs { get; set; }
    public int? TopLogprobs { get; set; }
}

public sealed class ChatCompletionCreateResponse
{
    public string Id { get; set; }
    public string Model { get; set; }
    public string ObjectTypeName { get; set; }
    public System.Net.HttpStatusCode HttpStatusCode { get; set; }
    public List<ChatChoiceResponse> Choices { get; set; }
    public Error Error { get; set; }
    public bool Successful => Error == null;
}

public sealed class ChatChoiceResponse
{
    public int Index { get; set; }
    public string FinishReason { get; set; }
    public ChatMessage Message { get; set; }
}

public sealed class Error
{
    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonIgnore]
    public string MessageObject
    {
        get => Message;
        set => Message = value;
    }
}

public sealed class ToolDefinition
{
    public string Type { get; set; }
    public FunctionDefinition Function { get; set; }
}

public sealed class FunctionDefinition
{
    public string Name { get; set; }
    public string Description { get; set; }
    public bool? Strict { get; set; }
    public PropertyDefinition Parameters { get; set; }
}

public sealed class ToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("function")]
    public FunctionCall FunctionCall { get; set; }
}

public sealed class FunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; }
}

public sealed class ToolChoice
{
    public string Type { get; set; }
}

public sealed class PropertyDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("enum")]
    public IList<string> Enum { get; set; }

    [JsonPropertyName("properties")]
    public IDictionary<string, PropertyDefinition> Properties { get; set; }

    [JsonPropertyName("required")]
    public IList<string> Required { get; set; }

    [JsonPropertyName("additionalProperties")]
    public bool? AdditionalProperties { get; set; }
}

public sealed class EmbeddingCreateRequest
{
    public string Model { get; set; }
    public string Input { get; set; }
    public IList<string> InputAsList { get; set; }
}

public sealed class EmbeddingCreateResponse
{
    public List<EmbeddingResponse> Data { get; set; }
    public Error Error { get; set; }
    public System.Net.HttpStatusCode HttpStatusCode { get; set; }
    public bool Successful => Error == null;
}

public sealed class EmbeddingResponse
{
    public int Index { get; set; }
    public List<double> Embedding { get; set; }
}
