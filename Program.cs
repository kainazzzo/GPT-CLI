using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.GPT3.Extensions;
using Microsoft.Extensions.Configuration;

namespace GptConsoleApp;

class Program
{
    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("api-key", "Your OpenAI API key");
        var baseUrlOption = new Option<string>("base-domain", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("prompt", "The prompt for text generation") { IsRequired = true };
        var inputOption = new Option<string>("input", "The input file or text for processing");
        var configOption = new Option<string>("config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("model", () => "text-davinci-003", "The model ID to use. Defaults to text-davinci-003");
        var suffixOption = new Option<string>("suffix", "The suffix that comes after a completion of inserted text");
        var maxTokensOption = new Option<int>("max-tokens", () => 50, "The maximum number of tokens to generate in the completion. Defaults to 50.");
        var temperatureOption = new Option<double>("temperature", "The sampling temperature to use, between 0 and 2");
        var topPOption = new Option<double>("top-p", "The value for nucleus sampling");
        var nOption = new Option<int>("n", () => 1, "The number of completions to generate for each prompt. Defaults to 1");
        var streamOption = new Option<bool>("stream", "Whether to stream back partial progress");
        var logprobsOption = new Option<int>("logprobs", "Include the log probabilities on the most likely tokens");
        var echoOption = new Option<bool>("echo", "Echo back the prompt in addition to the completion");
        var stopOption = new Option<string>("stop", "Up to 4 sequences where the API will stop generating further tokens");
        var presencePenaltyOption = new Option<double>("presence-penalty", "Penalty for new tokens based on their presence in the text so far");
        var frequencyPenaltyOption = new Option<double>("frequency-penalty", "Penalty for new tokens based on their frequency in the text so far");
        var bestOfOption = new Option<int>("best-of", () => 1, "Generates best_of completions server-side and returns the best one. Defaults to 1.");
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
        rootCommand.SetHandler((gptParameters) => { 
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

        // get a OpenAILogic instance
        var openAILogic = serviceProvider.GetService<OpenAILogic>();

        var response = await openAILogic.CreateCompletionAsync(binder.GptParameters);

        if (response.Successful == true)
        {
            foreach (var choice in response.Choices)
            {
                await Console.Out.WriteAsync(choice.Text.Trim());
            }
        }
        else
        {
            await Console.Out.WriteAsync(response.Error.Message.Trim());
        }
    }

    private static void ConfigureServices(IServiceCollection services, GptParametersBinder gptParametersBinder)
    {
        var gptParameters = gptParametersBinder.GptParameters;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile(gptParameters.Config ?? "appSettings.json", optional: true, reloadOnChange: false)
            .Build();

        gptParameters.ApiKey ??= configuration["OpenAI:api-key"];
        gptParameters.BaseDomain ??= configuration["OpenAI:base-domain"];

        // Add the configuration object to the services
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOpenAIService(settings =>
        {
            settings.ApiKey = gptParameters.ApiKey;
            settings.BaseDomain = gptParameters.BaseDomain;
        });
        services.AddScoped<OpenAILogic>();

    }

}
