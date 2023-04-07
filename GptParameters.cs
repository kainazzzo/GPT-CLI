namespace GPT.CLI;

public class GPTParameters
{
    public string ApiKey { get; set; }
    public string BaseDomain { get; set; }
    public string Prompt { get; set; }
    public string Input { get; set; }
    public string Config { get; set; }
    public string Model { get; set; }
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? N { get; set; }
    public bool? Stream { get; set; }
    public string Stop { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public string LogitBias { get; set; }
    public string User { get; set; }
}