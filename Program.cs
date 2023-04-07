using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.GPT3.Extensions;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;

namespace GPT.CLI;

class Program
{
    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("api-key", "Your OpenAI API key");
        var baseUrlOption = new Option<string>("base-domain", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("prompt", "The prompt for text generation") { IsRequired = true };
        var inputOption = new Option<string>("input", "The input text for processing. Combine this with a prompt to trigger edit mode. Also works with stdin for piping commands.");
        var configOption = new Option<string>("config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("model", () => "gpt-3.5-turbo", "The model ID to use.");
        var maxTokensOption = new Option<int>("max-tokens", () => 50, "The maximum number of tokens to generate in the completion.");
        var temperatureOption = new Option<double>("temperature", "The sampling temperature to use, between 0 and 2");
        var topPOption = new Option<double>("top-p", "The value for nucleus sampling");
        var nOption = new Option<int>("n", () => 1, "The number of completions to generate for each prompt.");
        var streamOption = new Option<bool>("stream", "Whether to stream back partial progress");
        var stopOption = new Option<string>("stop", "Up to 4 sequences where the API will stop generating further tokens");
        var presencePenaltyOption = new Option<double>("presence-penalty", "Penalty for new tokens based on their presence in the text so far");
        var frequencyPenaltyOption = new Option<double>("frequency-penalty", "Penalty for new tokens based on their frequency in the text so far");
        var logitBiasOption = new Option<string>("logit-bias", "Modify the likelihood of specified tokens appearing in the completion");
        var userOption = new Option<string>("user", "A unique identifier representing your end-user");

        // Create a command and add the options
        var rootCommand = new RootCommand("GPT Console Application")
        {
            apiKeyOption, baseUrlOption, promptOption, inputOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption, userOption
        };
        var binder = new GPTParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, inputOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption, userOption);

        // Set the handler for the rootCommand
        rootCommand.SetHandler(_ => { 
        }, binder);

        // Invoke the command
        var retValue = await new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build()
            .InvokeAsync(args);

        if (retValue == 0 && binder.GPTParameters != null && !string.IsNullOrEmpty(binder.GPTParameters.Prompt))
        {
            // Set up dependency injection
            var services = new ServiceCollection();
            await ConfigureServices(services, binder);

            await using var serviceProvider = services.BuildServiceProvider();

            // get a OpenAILogic instance
            var openAILogic = serviceProvider.GetService<OpenAILogic>();
            var chatRequest = !string.IsNullOrWhiteSpace(binder.GPTParameters.Input)
                ? MapChatEdit(binder.GPTParameters)
                : MapChatCreate(binder.GPTParameters);

            var response = await openAILogic.CreateChatCompletionAsync(chatRequest);

       
            if (response.Successful)
            {
                foreach (var choice in response.Choices)
                {
                    await Console.Out.WriteAsync(choice.Message.Content.Trim());
                }
            }
            else
            {
                await Console.Error.WriteAsync(response.Error?.Message?.Trim());
            }
        }
    }

    private static async Task ConfigureServices(IServiceCollection services, GPTParametersBinder gptParametersBinder)
    {
        var gptParameters = gptParametersBinder.GPTParameters;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile(gptParameters.Config ?? "appSettings.json", optional: true, reloadOnChange: false)
            .Build();

        gptParameters.ApiKey ??= configuration["OpenAI:api-key"];
        gptParameters.BaseDomain ??= configuration["OpenAI:base-domain"];

        if (Console.IsInputRedirected) {
            using var streamReader = new StreamReader(Console.OpenStandardInput());
            gptParameters.Input = await streamReader.ReadToEndAsync();
        }

        // Add the configuration object to the services
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOpenAIService(settings =>
        {
            settings.ApiKey = gptParameters.ApiKey;
            settings.BaseDomain = gptParameters.BaseDomain;
        });
        services.AddScoped<OpenAILogic>();
    }

    private static ChatCompletionCreateRequest MapChatEdit(GPTParameters parameters)
    {
        return new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>() { 
                new (StaticValues.ChatMessageRoles.System,"My next message will be text. My message after that will be a prompt describing how you should proceed. I want you to read through the text I give you, understand it, and then apply the prompt in the message after using the text I give you first as a starting point. Your final message after the prompt should only be the result of the prompt applied to the input text, and no more."),
                new (StaticValues.ChatMessageRoles.Assistant, "Sure. I will read through the next message, understand it, and then wait for the next message containing a prompt. After I combine them, my final response will be only the text edited with the prompt for a guide."),
                new (StaticValues.ChatMessageRoles.User, parameters.Input),
                new (StaticValues.ChatMessageRoles.Assistant, "Thank you. Now I will wait for the prompt and then apply it in context."),
                new (StaticValues.ChatMessageRoles.User, parameters.Prompt)

            },
            Model = parameters.Model,
            MaxTokens = parameters.MaxTokens,
            N = parameters.N,
            Temperature = (float?)parameters.Temperature,
            TopP = (float?)parameters.TopP,
            Stream = parameters.Stream,
            Stop = parameters.Stop,
            PresencePenalty = (float?)parameters.PresencePenalty,
            FrequencyPenalty = (float?)parameters.FrequencyPenalty,
            LogitBias = parameters.LogitBias == null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias),
            User = parameters.User
        };
    }

    private static ChatCompletionCreateRequest MapChatCreate(GPTParameters parameters)
    {
        return new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage> () {new(StaticValues.ChatMessageRoles.System, parameters.Prompt) },
            Model = parameters.Model,
            MaxTokens = parameters.MaxTokens,
            N = parameters.N,
            Temperature = (float?)parameters.Temperature,
            TopP = (float?)parameters.TopP,
            Stream = parameters.Stream,
            Stop = parameters.Stop,
            PresencePenalty = (float?)parameters.PresencePenalty,
            FrequencyPenalty = (float?)parameters.FrequencyPenalty,
            LogitBias = parameters.LogitBias == null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias),
            User = parameters.User
        };
    }
}
