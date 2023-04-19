using System.Text.Json.Serialization;
using Mapster;
using Newtonsoft.Json;

namespace GPT.CLI;

public record GPTParameters
{
    [JsonPropertyName("api-key")]
    public string ApiKey { get; set; }

    [JsonPropertyName("base-domain")]
    public string BaseDomain { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; }

    [JsonPropertyName("config")]
    public string Config { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; }

    [JsonPropertyName("max-tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top-p")]
    public double? TopP { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("stop")]
    public string Stop { get; set; }

    [JsonPropertyName("presence-penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency-penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("logit-bias")]
    public string LogitBias { get; set; }

    [JsonPropertyName("user")]
    public string User { get; set; }

    [JsonPropertyName("embed-filenames")]
    public string[] EmbedFilenames { get; set; }

    [JsonPropertyName("chunk-size")]
    public int ChunkSize { get; set; }

    [JsonPropertyName("closest-match-limit")]
    public int ClosestMatchLimit { get; set; }

    [JsonPropertyName("embed-directory-names")]
    public string[] EmbedDirectoryNames { get; set; }

    [JsonPropertyName("bot-token")]
    public string BotToken { get; set; }

    [JsonPropertyName("max-chat-history-length")]
    public uint MaxChatHistoryLength { get; set; }
}