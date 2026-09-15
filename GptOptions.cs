using System.Text.Json.Serialization;

namespace GPT.CLI;

public record GptOptions 
{
    // Secrets are global app settings and must never be persisted in per-channel state JSON.
    [JsonIgnore]
    public string ApiKey { get; set; }

    public string BaseDomain { get; set; }

    public string Prompt { get; set; }

    public string Model { get; set; } = "gpt-5.6-sol";

    public string VisionModel { get; set; } = "gpt-5.6-sol";

    public int? MaxTokens { get; set; } = 64000;

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? N { get; set; } = 1;

    public bool? Stream { get; set; } = true;

    public string Stop { get; set; }

    public double? PresencePenalty { get; set; }

    public double? FrequencyPenalty { get; set; }

    public string LogitBias { get; set; }

    public string User { get; set; }

    public string[] EmbedFilenames { get; set; }

    public int ChunkSize { get; set; } = 2048;

    public int ClosestMatchLimit { get; set; } = 3;

    public string[] EmbedDirectoryNames { get; set; }

    // Secrets are global app settings and must never be persisted in per-channel state JSON.
    [JsonIgnore]
    public string BotToken { get; set; }

    public uint MaxChatHistoryLength { get; set; } = 4096;

    public ulong? DiscordGuildId { get; set; }

    public string LearningPersonalityPrompt { get; set; }

    public ParameterMapping.Mode Mode { get; set; } = ParameterMapping.Mode.Completion;
}
