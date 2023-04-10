using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using GPT.CLI.Embeddings;
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
        Completion,
        Chat,
        Embed
    }

    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("--api-key", "Your OpenAI API key") ;
        var baseUrlOption = new Option<string>("--base-domain", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("--prompt", "The prompt for text generation. Optional for most commands.") {IsRequired = true};

        var configOption = new Option<string>("--config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("--model", () => Models.ChatGpt3_5Turbo, "The model ID to use.");
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
        var embedCommand = new Command("embed", "Create an embedding for data redirected via STDIN.");
        var embedFileOption = new Option<string[]>("--file", "Name of a file from which to load previously saved embeddings. Multiple files allowed.")
            { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore};

        embedCommand.AddValidator(result =>
        {
            if (!Console.IsInputRedirected)
            {
                result.ErrorMessage = "Input required for embedding";
            }
        });

        embedCommand.AddOption(embedFileOption);

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
        rootCommand.AddOption(nOption);
        rootCommand.AddGlobalOption(streamOption);
        rootCommand.AddGlobalOption(stopOption);
        rootCommand.AddGlobalOption(presencePenaltyOption);
        rootCommand.AddGlobalOption(frequencyPenaltyOption);
        rootCommand.AddGlobalOption(logitBiasOption);
        rootCommand.AddGlobalOption(userOption);
        rootCommand.AddGlobalOption(embedFileOption);

        rootCommand.AddCommand(chatCommand);
        rootCommand.AddCommand(embedCommand);


        var binder = new GPTParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption, userOption, embedFileOption);

        Mode mode = Mode.Completion;

        // Set the handler for the rootCommand
        rootCommand.SetHandler(_ => {}, binder);
        chatCommand.SetHandler(_ => mode = Mode.Chat, binder);
        embedCommand.SetHandler(_ => mode = Mode.Embed, binder);

        // Invoke the command
        var retValue = await new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build()
            .InvokeAsync(args);

        if (retValue == 0 && binder.GPTParameters != null)
        {
            // Set up dependency injection
            var services = new ServiceCollection(); 
            
            ConfigureServices(services, binder);

            await using var serviceProvider = services.BuildServiceProvider();

            // get a OpenAILogic instance
            var openAILogic = serviceProvider.GetService<OpenAILogic>();

            switch (mode)
            {
                case Mode.Chat:
                {
                    await HandleChatMode(openAILogic, binder);
                    break;
                }
                case Mode.Embed:
                    await HandleEmbedMode(openAILogic);
                    break;
                case Mode.Completion:
                default:
                {
                    await HandleCompletionMode(openAILogic, binder);

                    break;
                }
            }
        }
    }

    private static async Task HandleEmbedMode(OpenAILogic openAILogic)
    {
        // Create and output embedding
        var documents = await Document.ChunkStreamToDocumentsAsync(Console.OpenStandardInput());

        await openAILogic.CreateEmbeddings(documents);

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(documents));
    }

    private static async Task HandleCompletionMode(OpenAILogic openAILogic, GPTParametersBinder binder)
    {
        var chatRequest = Console.IsInputRedirected
            ? await MapChatEdit(binder.GPTParameters, openAILogic)
            : await MapChatCreate(binder.GPTParameters, openAILogic);

        var responses = binder.GPTParameters.Stream == true
            ? openAILogic.CreateChatCompletionAsyncEnumerable(chatRequest)
            : (await openAILogic.CreateChatCompletionAsync(chatRequest)).ToAsyncEnumerable();

        await foreach (var response in responses)
        {
            await OutputChatResponse(response);
        }
    }

    private static async Task HandleChatMode(OpenAILogic openAILogic, GPTParametersBinder binder)
    {
        var initialRequest = await MapCommon(binder.GPTParameters, openAILogic, new ChatCompletionCreateRequest()
        {
            Messages = new List<ChatMessage>(50)
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are ChatGPT CLI, the helpful assistant, but you're running on a command line.")
            }
        });



        await Console.Out.WriteLineAsync(@"
 #####                       #####  ######  #######     #####  #       ### 
#     # #    #   ##   ##### #     # #     #    #       #     # #        #  
#       #    #  #  #    #   #       #     #    #       #       #        #  
#       ###### #    #   #   #  #### ######     #       #       #        #  
#       #    # ######   #   #     # #          #       #       #        #  
#     # #    # #    #   #   #     # #          #       #     # #        #  
 #####  #    # #    #   #    #####  #          #        #####  ####### ###");
        var sb = new StringBuilder();
        var documents = await ReadEmbedFilesAsync(binder.GPTParameters);
        var prompts = new List<string>();
        var promptResponses = new List<string>();

        do
        {
            // Keeping track of all the prompts and responses means we can rebuild the chat message history
            // without including the old context every time, saving on tokens
            var chatGpt = new ChatGPTLogic(openAILogic, initialRequest);

            await Console.Out.WriteAsync("\r\n? ");
            var chatInput = await Console.In.ReadLineAsync();

            if (!string.IsNullOrWhiteSpace(chatInput))
            {
                if ("exit".Equals(chatInput, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }


                for (int i = 0; i < prompts.Count; i++)
                {
                    chatGpt.AppendMessage(new(StaticValues.ChatMessageRoles.User, prompts[i]));
                    chatGpt.AppendMessage(new(StaticValues.ChatMessageRoles.Assistant, promptResponses[i]));
                }

                
                // If there's embedded context provided, inject it after the existing chat history, and before the new prompt
                if (documents != null)
                {
                    // Search for the closest few documents and add those if they aren't used yet
                    var closestDocuments =
                        Document.FindMostSimilarDocuments(documents, await openAILogic.GetEmbeddingForPrompt(chatInput));
                    if (closestDocuments != null)
                    {
                        foreach (var closestDocument in closestDocuments)
                        {
                            chatGpt.AppendMessage(new(StaticValues.ChatMessageRoles.User, 
                                    $"Embedding context for the next prompt: {closestDocument.Text}"));
                        }
                    }
                }

                prompts.Add(chatInput);
                chatGpt.AppendMessage(new(StaticValues.ChatMessageRoles.User, chatInput));

                // Get the new response:
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

                // Store the streamed response for the chat history
                promptResponses.Add(sb.ToString());

                // Output the 
                await Console.Out.WriteLineAsync();
            }
        } while (true);
    }

    private static async Task<List<Document>> ReadEmbedFilesAsync(GPTParameters parameters)
    {
        List<Document> documents = null;
        if (parameters.EmbedFilenames is { Length: > 0 })
        {
            foreach (var embedFile in parameters.EmbedFilenames)
            {
                documents ??= new(parameters.EmbedFilenames.Length);

                await using var fileStream = File.OpenRead(embedFile);

                documents.AddRange(Document.LoadEmbeddings(fileStream));
            }
        }

        return documents;
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


    private static void ConfigureServices(IServiceCollection services, GPTParametersBinder gptParametersBinder)
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
            gptParameters.Input = Console.OpenStandardInput();
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

    private static async Task<ChatCompletionCreateRequest> MapChatEdit(GPTParameters parameters, OpenAILogic openAILogic)
    {
        using var streamReader = new StreamReader(parameters.Input);
        var input = await streamReader.ReadToEndAsync();
        await parameters.Input.DisposeAsync();

        var request = await MapCommon(parameters, openAILogic, new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>()
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You will receive two messages from the user. The first message will be text for you to parse and understand. The next message will be a prompt describing how you should proceed. You will read through the text or code in the first message, understand it, and then apply the prompt in the second message, with the first message as your main context. Your final message after the prompt should only be the result of the prompt applied to the input text, and no more."),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Sure. I will read through the first message and understand it. Then I'll wait for another message containing the prompt. After I apply the prompt to the original text, my final response will be the result of applying the prompt to my understanding of the input text."),
                new(StaticValues.ChatMessageRoles.User, input),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Thank you. Now I will wait for the prompt and then apply it in context.")
            }
        });

        // This is placed here so the MapCommon method can add contextual embeddings before the prompt
        request.Messages.Add(new(StaticValues.ChatMessageRoles.User, parameters.Prompt));

        return request;

    }

    private static async Task<ChatCompletionCreateRequest> MapCommon(GPTParameters parameters, OpenAILogic openAILogic, ChatCompletionCreateRequest request)
    {
        // This won't do anything for chat mode, unless a starting prompt is specified, I suppose
        // TODO: Test this prompt with chat mode:
        if (parameters.Prompt != null)
        {
            var documents = await ReadEmbedFilesAsync(parameters);

            if (documents != null)
            {
                // Search for the closest few documents and add those if they aren't used yet
                var closestDocuments =
                    Document.FindMostSimilarDocuments(documents,
                        await openAILogic.GetEmbeddingForPrompt(parameters.Prompt));
                if (closestDocuments != null)
                {
                    foreach (var closestDocument in closestDocuments)
                    {
                        request.Messages.Add(new(StaticValues.ChatMessageRoles.User,
                            $"Here is some context provided for you to learn from for the next prompt: {closestDocument.Text}"));
                    }
                }
            }
        }

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

    private static async Task<ChatCompletionCreateRequest> MapChatCreate(GPTParameters parameters,
        OpenAILogic openAILogic)
    {
        var request = await MapCommon(parameters, openAILogic,new ChatCompletionCreateRequest
        {
            Messages = new List<ChatMessage>()
        });

        request.Messages.Add(new(StaticValues.ChatMessageRoles.System, parameters.Prompt));

        return request;
    }
}
