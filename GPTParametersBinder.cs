using GptConsoleApp;
using System.CommandLine.Binding;
using System.CommandLine;

public class GptParametersBinder : BinderBase<GptParameters>
{
    private readonly Option<string> _apiKeyOption;
    private readonly Option<string> _baseUrlOption;
    private readonly Option<string> _promptOption;
    private readonly Option<string> _inputOption;
    private readonly Option<string> _configOption;
    private readonly Option<string> _modelOption;
    private readonly Option<string> _suffixOption;
    private readonly Option<int> _maxTokensOption;
    private readonly Option<double> _temperatureOption;
    private readonly Option<double> _topPOption;
    private readonly Option<int> _nOption;
    private readonly Option<bool> _streamOption;
    private readonly Option<int> _logprobsOption;
    private readonly Option<bool> _echoOption;
    private readonly Option<string> _stopOption;
    private readonly Option<double> _presencePenaltyOption;
    private readonly Option<double> _frequencyPenaltyOption;
    private readonly Option<int> _bestOfOption;
    private readonly Option<string> _logitBiasOption;
    private readonly Option<string> _userOption;

    public GptParametersBinder(
        Option<string> apiKeyOption,
        Option<string> baseUrlOption,
        Option<string> promptOption,
        Option<string> inputOption,
        Option<string> configOption,
        Option<string> modelOption,
        Option<string> suffixOption,
        Option<int> maxTokensOption,
        Option<double> temperatureOption,
        Option<double> topPOption,
        Option<int> nOption,
        Option<bool> streamOption,
        Option<int> logprobsOption,
        Option<bool> echoOption,
        Option<string> stopOption,
        Option<double> presencePenaltyOption,
        Option<double> frequencyPenaltyOption,
        Option<int> bestOfOption,
        Option<string> logitBiasOption,
        Option<string> userOption)
    {
        _apiKeyOption = apiKeyOption;
        _baseUrlOption = baseUrlOption;
        _promptOption = promptOption;
        _inputOption = inputOption;
        _configOption = configOption;
        _modelOption = modelOption;
        _suffixOption = suffixOption;
        _maxTokensOption = maxTokensOption;
        _temperatureOption = temperatureOption;
        _topPOption = topPOption;
        _nOption = nOption;
        _streamOption = streamOption;
        _logprobsOption = logprobsOption;
        _echoOption = echoOption;
        _stopOption = stopOption;
        _presencePenaltyOption = presencePenaltyOption;
        _frequencyPenaltyOption = frequencyPenaltyOption;
        _bestOfOption = bestOfOption;
        _logitBiasOption = logitBiasOption;
        _userOption = userOption;
    }

    protected override GptParameters GetBoundValue(BindingContext bindingContext)
    {
        GptParameters = new GptParameters
        {
            ApiKey = bindingContext.ParseResult.GetValueForOption(_apiKeyOption),
            BaseDomain = bindingContext.ParseResult.GetValueForOption(_baseUrlOption),
            Prompt = bindingContext.ParseResult.GetValueForOption(_promptOption),
            Input = bindingContext.ParseResult.GetValueForOption(_inputOption),
            Config = bindingContext.ParseResult.GetValueForOption(_configOption),
            Model = bindingContext.ParseResult.GetValueForOption(_modelOption),
            Suffix = bindingContext.ParseResult.GetValueForOption(_suffixOption),
            MaxTokens = bindingContext.ParseResult.GetValueForOption(_maxTokensOption),
            Temperature = bindingContext.ParseResult.GetValueForOption(_temperatureOption),
            TopP = bindingContext.ParseResult.GetValueForOption(_topPOption),
            N = bindingContext.ParseResult.GetValueForOption(_nOption),
            Stream = bindingContext.ParseResult.GetValueForOption(_streamOption),
            Logprobs = bindingContext.ParseResult.GetValueForOption(_logprobsOption),
            Echo = bindingContext.ParseResult.GetValueForOption(_echoOption),
            Stop = bindingContext.ParseResult.GetValueForOption(_stopOption),
            PresencePenalty = bindingContext.ParseResult.GetValueForOption(_presencePenaltyOption),
            FrequencyPenalty = bindingContext.ParseResult.GetValueForOption(_frequencyPenaltyOption),
            BestOf = bindingContext.ParseResult.GetValueForOption(_bestOfOption),
            LogitBias = bindingContext.ParseResult.GetValueForOption(_logitBiasOption),
            User = bindingContext.ParseResult.GetValueForOption(_userOption)
        };
 

        return GptParameters;
    }

    public GptParameters GptParameters { get; private set; }
}
