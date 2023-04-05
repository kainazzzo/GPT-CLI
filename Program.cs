using OpenAI.GPT3.Managers;
using OpenAI.GPT3;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;


namespace GptConsoleApp;

class Program
{
    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("api-key", "Your OpenAI API key");
        var baseUrlOption = new Option<string>("base-url", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("prompt", "The prompt for text generation") { IsRequired = true };
        var inputOption = new Option<string>("input", "The input file or text for processing");
        var configOption = new Option<string>("config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("model", "The model ID to use");
        var suffixOption = new Option<string>("suffix", "The suffix that comes after a completion of inserted text");
        var maxTokensOption = new Option<int>("max-tokens", "The maximum number of tokens to generate in the completion");
        var temperatureOption = new Option<double>("temperature", "The sampling temperature to use, between 0 and 2");
        var topPOption = new Option<double>("top-p", "The value for nucleus sampling");
        var nOption = new Option<int>("n", "The number of completions to generate for each prompt");
        var streamOption = new Option<bool>("stream", "Whether to stream back partial progress");
        var logprobsOption = new Option<int>("logprobs", "Include the log probabilities on the most likely tokens");
        var echoOption = new Option<bool>("echo", "Echo back the prompt in addition to the completion");
        var stopOption = new Option<string>("stop", "Up to 4 sequences where the API will stop generating further tokens");
        var presencePenaltyOption = new Option<double>("presence-penalty", "Penalty for new tokens based on their presence in the text so far");
        var frequencyPenaltyOption = new Option<double>("frequency-penalty", "Penalty for new tokens based on their frequency in the text so far");
        var bestOfOption = new Option<int>("best-of", "Generates best_of completions server-side and returns the best one");
        var logitBiasOption = new Option<string>("logit-bias", "Modify the likelihood of specified tokens appearing in the completion");
        var userOption = new Option<string>("user", "A unique identifier representing your end-user");

        // Create a command and add the options
        var rootCommand = new RootCommand("GPT Console Application")
        {
            apiKeyOption, baseUrlOption, promptOption, inputOption, configOption,
            modelOption, suffixOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, logprobsOption, echoOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, bestOfOption, logitBiasOption, userOption
        };
        var binder = new GptParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, inputOption, configOption,
            modelOption, suffixOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, logprobsOption, echoOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, bestOfOption, logitBiasOption, userOption);

        // Set the handler for the rootCommand
        rootCommand.SetHandler(async (gptParameters) =>
        {
            await ProcessParameters(gptParameters);
        }, binder);

        // Invoke the command
        await new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build()
            .InvokeAsync(args);

        // Set up dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services, binder);

        using var serviceProvider = services.BuildServiceProvider();

        // get a OpenAILogic instance from services
        var openAILogic = serviceProvider.GetService<OpenAILogic>();
        var openAILogic2 = openAILogic;
    }

    static async Task ProcessParameters(GptParameters parameters)
    {
        // Process the parameters and call the OpenAI API

        // For now, just print the received parameters for verification
        await Console.Out.WriteLineAsync($"API Key: {parameters.ApiKey}");
        await Console.Out.WriteLineAsync($"Base URL: {parameters.BaseUrl}");
        await Console.Out.WriteLineAsync($"Prompt: {parameters.Prompt}");
        await Console.Out.WriteLineAsync($"Input: {parameters.Input}");
        await Console.Out.WriteLineAsync($"Config: {parameters.Config}");
        await Console.Out.WriteLineAsync($"Model: {parameters.Model}");
        await Console.Out.WriteLineAsync($"Suffix: {parameters.Suffix}");
        await Console.Out.WriteLineAsync($"Max Tokens: {parameters.MaxTokens}");
        await Console.Out.WriteLineAsync($"Temperature: {parameters.Temperature}");
        await Console.Out.WriteLineAsync($"Top P: {parameters.TopP}");
        await Console.Out.WriteLineAsync($"N: {parameters.N}");
        await Console.Out.WriteLineAsync($"Stream: {parameters.Stream}");
        await Console.Out.WriteLineAsync($"Logprobs: {parameters.Logprobs}");
        await Console.Out.WriteLineAsync($"Echo: {parameters.Echo}");
        await Console.Out.WriteLineAsync($"Stop: {parameters.Stop}");
        await Console.Out.WriteLineAsync($"Presence Penalty: {parameters.PresencePenalty}");
        await Console.Out.WriteLineAsync($"Frequency Penalty: {parameters.FrequencyPenalty}");
        await Console.Out.WriteLineAsync($"Best Of: {parameters.BestOf}");
        await Console.Out.WriteLineAsync($"Logit Bias: {parameters.LogitBias}");
        await Console.Out.WriteLineAsync($"User: {parameters.User}");
    }

    private static void ConfigureServices(IServiceCollection services, GptParametersBinder gptParametersBinder)
    {
        var gptParameters = gptParametersBinder.GptParameters;

        services.AddSingleton(new OpenAILogic(gptParameters.ApiKey, gptParameters.BaseUrl));
    }

}
