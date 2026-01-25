namespace GPT.CLI;

public record GptOptions
{
    public string ApiKey { get; set; }

    public string BaseDomain { get; set; }

    public string Prompt { get; set; }

    public string Model { get; set; } = "gpt-4o";

    public int? MaxTokens { get; set; } = 3584;

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

    public string BotToken { get; set; }

    public uint MaxChatHistoryLength { get; set; } = 4096;

    public ParameterMapping.Mode Mode { get; set; } = ParameterMapping.Mode.Completion;
}
