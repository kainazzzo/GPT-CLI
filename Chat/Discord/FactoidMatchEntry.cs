using System.Text.Json.Serialization;

namespace GPT.CLI.Chat.Discord;

public record FactoidMatchEntry
{
    [JsonPropertyName("term")]
    public string Term { get; set; }

    [JsonPropertyName("query")]
    public string Query { get; set; }

    [JsonPropertyName("query-message-id")]
    public ulong QueryMessageId { get; set; }

    [JsonPropertyName("response-message-id")]
    public ulong ResponseMessageId { get; set; }

    [JsonPropertyName("user-id")]
    public ulong UserId { get; set; }

    [JsonPropertyName("matched-at")]
    public DateTimeOffset MatchedAt { get; set; }
}
