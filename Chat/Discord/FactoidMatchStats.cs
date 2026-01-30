using System.Text.Json.Serialization;

namespace GPT.CLI.Chat.Discord;

public record FactoidMatchStats
{
    [JsonPropertyName("total")]
    public int TotalMatches { get; set; }

    [JsonPropertyName("term-counts")]
    public Dictionary<string, int> TermCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("term-display-names")]
    public Dictionary<string, string> TermDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("last-response-message-ids")]
    public Dictionary<string, ulong> LastResponseMessageIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("last-user-ids")]
    public Dictionary<string, ulong> LastUserIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
