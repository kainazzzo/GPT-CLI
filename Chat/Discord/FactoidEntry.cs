using System.Text.Json.Serialization;

namespace GPT.CLI.Chat.Discord;

public record FactoidEntry
{
    [JsonPropertyName("term")]
    public string Term { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("embedding")]
    public List<double> Embedding { get; set; }

    [JsonPropertyName("source-guild-id")]
    public ulong SourceGuildId { get; set; }

    [JsonPropertyName("source-channel-id")]
    public ulong SourceChannelId { get; set; }

    [JsonPropertyName("source-message-id")]
    public ulong SourceMessageId { get; set; }

    [JsonPropertyName("source-user-id")]
    public ulong SourceUserId { get; set; }

    [JsonPropertyName("source-username")]
    public string SourceUsername { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset CreatedAt { get; set; }
}
