﻿using System.CommandLine;
using System.CommandLine.Binding;

namespace GPT.CLI;

public class GPTParametersBinder : BinderBase<GPTParameters>
{
    private readonly Option<string> _apiKeyOption;
    private readonly Option<string> _baseUrlOption;
    private readonly Option<string> _promptOption;
    private readonly Option<string> _configOption;
    private readonly Option<string> _modelOption;
    private readonly Option<int> _maxTokensOption;
    private readonly Option<double> _temperatureOption;
    private readonly Option<double> _topPOption;
    private readonly Option<int> _nOption;
    private readonly Option<bool> _streamOption;
    private readonly Option<string> _stopOption;
    private readonly Option<double> _presencePenaltyOption;
    private readonly Option<double> _frequencyPenaltyOption;
    private readonly Option<string> _logitBiasOption;
    private readonly Option<string> _userOption;
    private readonly Option<string[]> _embedFileOption;
    private readonly Option<string[]> _embedDirectoryOption;

    private readonly Option<int> _chunkSizeOption;
    private readonly Option<int> _matchLimitOption;
    private readonly Option<string> _botTokenOption;
    private readonly Option<uint> _maxChatHistoryLengthOption;

    public GPTParametersBinder(Option<string> apiKeyOption,
        Option<string> baseUrlOption,
        Option<string> promptOption,
        Option<string> configOption,
        Option<string> modelOption,
        Option<int> maxTokensOption,
        Option<double> temperatureOption,
        Option<double> topPOption,
        Option<int> nOption,
        Option<bool> streamOption,
        Option<string> stopOption,
        Option<double> presencePenaltyOption,
        Option<double> frequencyPenaltyOption,
        Option<string> logitBiasOption,
        Option<string> userOption,
        Option<string[]> embedFileOption,
        Option<string[]> embedDirectoryOption,
        Option<int> chunkSizeOption,
        Option<int> matchLimitOption,
        Option<string> botTokenOption,
        Option<uint> maxChatHistoryLengthOption)
    {
        _apiKeyOption = apiKeyOption;
        _baseUrlOption = baseUrlOption;
        _promptOption = promptOption;
        _configOption = configOption;
        _modelOption = modelOption;
        _maxTokensOption = maxTokensOption;
        _temperatureOption = temperatureOption;
        _topPOption = topPOption;
        _nOption = nOption;
        _streamOption = streamOption;
        _stopOption = stopOption;
        _presencePenaltyOption = presencePenaltyOption;
        _frequencyPenaltyOption = frequencyPenaltyOption;
        _logitBiasOption = logitBiasOption;
        _userOption = userOption;
        _embedFileOption = embedFileOption;
        _embedDirectoryOption = embedDirectoryOption;
        _chunkSizeOption = chunkSizeOption;
        _matchLimitOption = matchLimitOption;
        _botTokenOption = botTokenOption;
        _maxChatHistoryLengthOption = maxChatHistoryLengthOption;
    }

    protected override GPTParameters GetBoundValue(BindingContext bindingContext)
    {
        GPTParameters = new GPTParameters
        {
            ApiKey = bindingContext.ParseResult.GetValueForOption(_apiKeyOption),
            BaseDomain = bindingContext.ParseResult.GetValueForOption(_baseUrlOption),
            Prompt = bindingContext.ParseResult.GetValueForOption(_promptOption),
            Config = bindingContext.ParseResult.GetValueForOption(_configOption),
            Model = bindingContext.ParseResult.GetValueForOption(_modelOption),
            MaxTokens = bindingContext.ParseResult.GetValueForOption(_maxTokensOption),
            Temperature = bindingContext.ParseResult.GetValueForOption(_temperatureOption),
            TopP = bindingContext.ParseResult.GetValueForOption(_topPOption),
            N = bindingContext.ParseResult.GetValueForOption(_nOption),
            Stream = bindingContext.ParseResult.GetValueForOption(_streamOption),
            Stop = bindingContext.ParseResult.GetValueForOption(_stopOption),
            PresencePenalty = bindingContext.ParseResult.GetValueForOption(_presencePenaltyOption),
            FrequencyPenalty = bindingContext.ParseResult.GetValueForOption(_frequencyPenaltyOption),
            LogitBias = bindingContext.ParseResult.GetValueForOption(_logitBiasOption),
            User = bindingContext.ParseResult.GetValueForOption(_userOption),
            EmbedFilenames = bindingContext.ParseResult.GetValueForOption(_embedFileOption),
            EmbedDirectoryNames = bindingContext.ParseResult.GetValueForOption(_embedDirectoryOption),
            ChunkSize = bindingContext.ParseResult.GetValueForOption(_chunkSizeOption),
            ClosestMatchLimit = bindingContext.ParseResult.GetValueForOption(_matchLimitOption),
            BotToken = bindingContext.ParseResult.GetValueForOption(_botTokenOption),
            MaxChatHistoryLength = bindingContext.ParseResult.GetValueForOption(_maxChatHistoryLengthOption)
        };
 

        return GPTParameters;
    }

    public GPTParameters GPTParameters { get; private set; }
}