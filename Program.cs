using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.GPT3.Extensions;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI;

class Program
{
    enum Mode
    {
        Normal,
        Chat,
        Embed
    }

    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("--api-key", "Your OpenAI API key");
        var baseUrlOption = new Option<string>("--base-domain", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("--prompt", "The prompt for text generation. Optional for most commands.") {IsRequired = true};

        var configOption = new Option<string>("--config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("--model", () => "gpt-3.5-turbo", "The model ID to use.");
        var maxTokensOption = new Option<int>("--max-tokens", () => 1000, "The maximum number of tokens to generate in the completion.");
        var temperatureOption = new Option<double>("--temperature", "The sampling temperature to use, between 0 and 2");
        var topPOption = new Option<double>("--top-p", "The value for nucleus sampling");
        var nOption = new Option<int>("--n", () => 1, "The number of completions to generate for each prompt.");
        var streamOption = new Option<bool>("--stream", () => true, "Whether to stream back partial progress");
        var stopOption = new Option<string>("--stop", "Up to 4 sequences where the API will stop generating further tokens");
        var presencePenaltyOption = new Option<double>("--presence-penalty", "Penalty for new tokens based on their presence in the text so far");
        var frequencyPenaltyOption = new Option<double>("--frequency-penalty", "Penalty for new tokens based on their frequency in the text so far");
        var logitBiasOption = new Option<string>("--logit-bias", "Modify the likelihood of specified tokens appearing in the completion");
        var userOption = new Option<string>("--user", "A unique identifier representing your end-user");

        var chatCommand = new Command("chat", "Starts listening in chat mode.");
        var embedCommand = new Command("embed", "Create an embedding for STDIN.");

        embedCommand.AddValidator(result =>
        {
            if (!Console.IsInputRedirected)
            {
                result.ErrorMessage = "Input required for embedding";
            }
        });


        // Create a command and add the options
        var rootCommand = new RootCommand("GPT Console Application");

        rootCommand.AddGlobalOption(apiKeyOption);
        rootCommand.AddGlobalOption(baseUrlOption);
        rootCommand.AddOption(promptOption);
        rootCommand.AddGlobalOption(configOption);
        rootCommand.AddGlobalOption(modelOption);
        rootCommand.AddGlobalOption(maxTokensOption);
        rootCommand.AddGlobalOption(temperatureOption);
        rootCommand.AddGlobalOption(topPOption);
        rootCommand.AddGlobalOption(nOption);
        rootCommand.AddGlobalOption(streamOption);
        rootCommand.AddGlobalOption(stopOption);
        rootCommand.AddGlobalOption(presencePenaltyOption);
        rootCommand.AddGlobalOption(frequencyPenaltyOption);
        rootCommand.AddGlobalOption(logitBiasOption);
        rootCommand.AddGlobalOption(userOption);

        rootCommand.AddCommand(chatCommand);
        rootCommand.AddCommand(embedCommand);

        var binder = new GPTParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption, userOption);

        Mode mode = Mode.Normal;

        // Set the handler for the rootCommand
        rootCommand.SetHandler(_ => {}, binder);
        chatCommand.SetHandler(_ => mode = Mode.Chat, binder);
        embedCommand.SetHandler(_ => mode = Mode.Embed, binder);

        // Invoke the command
        var retValue = await new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build()
            .InvokeAsync(args);

        if (retValue == 0 && binder.GPTParameters != null && (!string.IsNullOrEmpty(binder.GPTParameters.Prompt) || mode == Mode.Chat))
        {
            // Set up dependency injection
            var services = new ServiceCollection();
            await ConfigureServices(services, binder);

            await using var serviceProvider = services.BuildServiceProvider();

            // get a OpenAILogic instance
            var openAILogic = serviceProvider.GetService<OpenAILogic>();

            switch (mode)
            {
                case Mode.Chat:
                {
                    var chatGpt = new ChatGPTLogic(openAILogic, MapCommon(binder.GPTParameters, new ChatCompletionCreateRequest()
                    {
                        Messages = new List<ChatMessage>(50)
                        {
                            new(StaticValues.ChatMessageRoles.System, "You are ChatGPT CLI, the helpful assistant, but you're running on a command line.")
                        }
                    }
                    ));

                    await Console.Out.WriteLineAsync(@"
 #####                       #####  ######  #######     #####  #       ### 
#     # #    #   ##   ##### #     # #     #    #       #     # #        #  
#       #    #  #  #    #   #       #     #    #       #       #        #  
#       ###### #    #   #   #  #### ######     #       #       #        #  
#       #    # ######   #   #     # #          #       #       #        #  
#     # #    # #    #   #   #     # #          #       #     # #        #  
 #####  #    # #    #   #    #####  #          #        #####  ####### ###");
                    var sb = new StringBuilder();
                    do
                    {
                    
                        await Console.Out.WriteAsync("\r\n? ");
                        var chatInput = await Console.In.ReadLineAsync();
                    

                        if (!string.IsNullOrWhiteSpace(chatInput))
                        {
                            chatGpt.AppendMessage(new(StaticValues.ChatMessageRoles.User, chatInput));
                            var responses = chatGpt.SendMessages();
                            sb.Clear();
                            await foreach (var response in responses)
                            {
                                if (await OutputChatResponse(response))
                                {
                                    foreach (var choice in response.Choices)
                                    {
                                        sb.Append(choice.Message.Content);
                                    }
                                }
                            }

                            await Console.Out.WriteLineAsync();
                            chatGpt.AppendMessage(new (StaticValues.ChatMessageRoles.Assistant, sb.ToString()));
                        }
                        else
                        {
                            break;
                        }
                    } while (true);

                    break;
                }
                case Mode.Embed:
                    throw new NotImplementedException();
                    break;
                case Mode.Normal:
                default:
                {
                    var chatRequest = !string.IsNullOrWhiteSpace(binder.GPTParameters.Input)
                        ? MapChatEdit(binder.GPTParameters)
                        : MapChatCreate(binder.GPTParameters);

                    var responses = binder.GPTParameters.Stream == true
                        ? openAILogic.CreateChatCompletionAsyncEnumerable(chatRequest)
                        : (await openAILogic.CreateChatCompletionAsync(chatRequest)).ToAsyncEnumerable();

                    await foreach (var response in responses)
                    {
                        await OutputChatResponse(response);
                    }

                    break;
                }
            }
        }
    }

    private static async Task<bool> OutputChatResponse(ChatCompletionCreateResponse response)
    {
        if (response.Successful)
        {
            foreach (var choice in response.Choices)
            {
                await Console.Out.WriteAsync(choice.Message.Content);
            }
        }
        else
        {
            await Console.Error.WriteAsync(response.Error?.Message?.Trim());
        }

        return response.Successful;
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

        if (Console.IsInputRedirected)
        {
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
        return MapCommon(parameters, new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>()
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You will receive two messages from the user. The first message will be text for you to parse and understand. The next message will be a prompt describing how you should proceed. You will read through the text or code in the first message, understand it, and then apply the prompt in the second message, with the first message as your main context. Your final message after the prompt should only be the result of the prompt applied to the input text, and no more."),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Sure. I will read through the first message and understand it. Then I'll wait for another message containing the prompt. After I apply the prompt to the original text, my final response will be the result of applying the prompt to my understanding of the input text."),
                new(StaticValues.ChatMessageRoles.User, parameters.Input),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Thank you. Now I will wait for the prompt and then apply it in context."),
                new(StaticValues.ChatMessageRoles.User, parameters.Prompt)

            }
        });
    }

    private static ChatCompletionCreateRequest MapCommon(GPTParameters parameters, ChatCompletionCreateRequest request)
    {
        request.Model = parameters.Model;
        request.MaxTokens = parameters.MaxTokens;
        request.N = parameters.N;
        request.Temperature = (float?)parameters.Temperature;
        request.TopP = (float?)parameters.TopP;
        request.Stream = parameters.Stream;
        request.Stop = parameters.Stop;
        request.PresencePenalty = (float?)parameters.PresencePenalty;
        request.FrequencyPenalty = (float?)parameters.FrequencyPenalty;
        request.LogitBias = parameters.LogitBias == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias);
        request.User = parameters.User;

        return request;
    }

    private static ChatCompletionCreateRequest MapChatCreate(GPTParameters parameters)
    {
        return MapCommon(parameters, new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>() { new(StaticValues.ChatMessageRoles.System, parameters.Prompt) }
        });
    }
}
