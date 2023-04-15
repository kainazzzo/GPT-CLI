using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Discord.WebSocket;
using Discord;
using GPT.CLI.Embeddings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;
using OpenAI.GPT3.Extensions;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;
using TokenType = Discord.TokenType;
using System.Security.Cryptography;
using GPT.CLI.Discord;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace GPT.CLI;

class Program
{
    enum Mode
    {
        Completion,
        Chat,
        Embed,
        Http,
        Discord
    }

    private static DiscordSocketClient _discordClient;

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
        var httpCommand = new Command("http", "Starts an HTTP server to listen for requests.");
        var discordCommand = new Command("discord", "Starts the CLI as a Discord bot that receives messages from all channels on your server.");
        var botTokenOption = new Option<string>("--bot-token", "The token for your Discord bot.");

        var chunkSizeOption = new Option<int>("--chunk-size", () => 1024,
            "The size to chunk down text into embeddable documents.");
        var embedFileOption = new Option<string[]>("--file", "Name of a file from which to load previously saved embeddings. Multiple files allowed.")
            { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore};
        var embedDirectoryOption = new Option<string[]>("--directory",
            "Name of a directory from which to load previously saved embeddings. Multiple directories allowed.")
        {
            AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore
        };

        var httpPortOption = new Option<int>("--port", () => 5000, "The port to listen on for HTTP requests.");
        var sslPortOption = new Option<int>("--ssl-port", () => 5001, "The port to listen on for HTTPS requests.");

        var matchLimitOption = new Option<int>("--match-limit", () => 3,
            "Limits the number of embedding chunks to use when applying context.");

        embedCommand.AddValidator(result =>
        {
            if (!Console.IsInputRedirected)
            {
                result.ErrorMessage = "Input required for embedding";
            }
        });

        embedCommand.AddOption(chunkSizeOption);

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
        rootCommand.AddGlobalOption(embedDirectoryOption);

        rootCommand.AddOption(matchLimitOption);

        rootCommand.AddCommand(chatCommand);
        rootCommand.AddCommand(embedCommand);
        // We'll add this later when I have an api idea.
        //rootCommand.AddCommand(httpCommand);
        rootCommand.AddCommand(discordCommand);
        rootCommand.AddOption(botTokenOption);
        

        var binder = new GPTParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption, 
            userOption, embedFileOption, embedDirectoryOption, chunkSizeOption, matchLimitOption, botTokenOption);

        Mode mode = Mode.Completion;

        // Set the handler for the rootCommand
        rootCommand.SetHandler(_ => {}, binder);
        chatCommand.SetHandler(_ => mode = Mode.Chat, binder);
        embedCommand.SetHandler(_ => mode = Mode.Embed, binder);
        httpCommand.SetHandler(_ => mode = Mode.Http, binder);
        discordCommand.SetHandler(_ => mode = Mode.Discord, binder);

        // Invoke the command
        var retValue = await new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build()
            .InvokeAsync(args);

        if (retValue == 0 && binder.GPTParameters != null)
        {
            // Set up dependency injection
            var services = new ServiceCollection(); 
            
            ConfigureServices(services, binder, mode);

            await using var serviceProvider = services.BuildServiceProvider();

            // get a OpenAILogic instance
            var openAILogic = serviceProvider.GetService<OpenAILogic>();

            switch (mode)
            {
                case Mode.Chat:
                {
                    await HandleChatMode(openAILogic, binder.GPTParameters);
                    break;
                }
                case Mode.Embed:
                {
                    await HandleEmbedMode(openAILogic, binder.GPTParameters);
                    break;
                }
                case Mode.Discord:
                {
                    await HandleDiscordMode(openAILogic, binder.GPTParameters, services);
                    break;
                }
                case Mode.Completion:
                default:
                {
                    await HandleCompletionMode(openAILogic, binder.GPTParameters);

                    break;
                }
            }
        }
    }

    private static async Task HandleDiscordMode(GPTParameters gptParameters)
    {
        // You will need to provide your Discord bot token here
        string botToken = gptParameters.BotToken;

        var discordClient = new DiscordSocketClient();
        discordClient.Log += LogAsync;
        discordClient.MessageReceived += MessageReceivedAsync;

        await discordClient.LoginAsync(TokenType.Bot, botToken);
        await discordClient.StartAsync();

        // Block this task until the program is closed.
        await Task.Delay(-1);
    }

    private static Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log.ToString());
        return Task.CompletedTask;
    }

    private static async Task MessageReceivedAsync(SocketMessage message)
    {
        // Ignore messages from the bot itself
        if (message.Author.Id == _discordClient.CurrentUser.Id)
            return;

        // Process the message here, e.g., call your OpenAILogic methods
    }

    private static async Task HandleDiscordMode(OpenAILogic openAILogic, GPTParameters gptParameters, IServiceCollection services)
    {
        var hostBuilder = new HostBuilder().ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.ConfigureServices(servicesCollection =>
            {
                foreach (var service in services)
                {
                    servicesCollection.Add(service);
                }
            });

            webBuilder.Configure(app =>
            {
                app.UseHttpsRedirection(); // Add HTTPS redirection
                app.UseRouting();
                
                //app.UseAuthentication();
                //app.UseAuthorization();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapPost("/", async (context) =>
                    {
                        // Read the request body
                        var request  = context.Request;
                        var response = context.Response;
                        using var reader = new StreamReader(request.Body, Encoding.UTF8);
                        string requestBody = await reader.ReadToEndAsync();

                        // Get the signature and timestamp from headers
                        string signature = request.Headers["X-Signature-Ed25519"];
                        string timestamp = request.Headers["X-Signature-Timestamp"];

                        // Verify the signature
                        if (!VerifySignature("f35df98f5bfd306aed9f224c9dc8ee06840203ca6cd6c5f9369ae26f21d61032", signature, timestamp, requestBody))
                        {
                            response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }

                        // Parse the request body
                        var json = JsonDocument.Parse(requestBody);
                        var interactionType = json.RootElement.GetProperty("type").GetInt32();

                        // Handle PING (type 1) interaction
                        if (interactionType == 1)
                        {
                            response.ContentType = "application/json";
                            await response.WriteAsync("{\"type\": 1}");
                            return;
                        }

                        // Handle other interaction types as needed
                        // ...

                        response.StatusCode = StatusCodes.Status400BadRequest;

                    } );
                });
            });

            // Configure Kestrel to listen on port 80
            webBuilder.UseKestrel(options =>
            {
                options.ListenAnyIP(80);
                options.ListenAnyIP(443, configure => configure.UseHttps());
            });
        });

        hostBuilder.ConfigureServices((hostContext, services) =>
        {
            services.AddSingleton<DiscordSocketClient>();
            services.AddHostedService<DiscordBot>();
        });

        await hostBuilder.RunConsoleAsync();
    }

    private static async Task HandleEmbedMode(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        // Create and output embedding
        var documents = await Document.ChunkStreamToDocumentsAsync(Console.OpenStandardInput(), gptParameters.ChunkSize);

        await openAILogic.CreateEmbeddings(documents);

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(documents));
    }

    private static async Task HandleCompletionMode(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        var chatRequest = Console.IsInputRedirected
            ? await MapChatEdit(gptParameters, openAILogic)
            : await MapChatCreate(gptParameters, openAILogic);

        var responses = gptParameters.Stream == true
            ? openAILogic.CreateChatCompletionAsyncEnumerable(chatRequest)
            : (await openAILogic.CreateChatCompletionAsync(chatRequest)).ToAsyncEnumerable();

        await foreach (var response in responses)
        {
            await OutputChatResponse(response);
        }
    }

    private static async Task HandleChatMode(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        var initialRequest = await MapCommon(gptParameters, openAILogic, new ChatCompletionCreateRequest()
        {
            Messages = new List<ChatMessage>(50)
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are ChatGPT CLI, the helpful assistant, but you're running on a command line.")
            }
        }, Mode.Chat);



        await Console.Out.WriteLineAsync(@"
 #####                       #####  ######  #######     #####  #       ### 
#     # #    #   ##   ##### #     # #     #    #       #     # #        #  
#       #    #  #  #    #   #       #     #    #       #       #        #  
#       ###### #    #   #   #  #### ######     #       #       #        #  
#       #    # ######   #   #     # #          #       #       #        #  
#     # #    # #    #   #   #     # #          #       #     # #        #  
 #####  #    # #    #   #    #####  #          #        #####  ####### ###");
        var sb = new StringBuilder();
        var documents = await ReadEmbedFilesAsync(gptParameters);
        documents.AddRange(await ReadEmbedDirectoriesAsync(gptParameters));

        var prompts = new List<string>();
        var promptResponses = new List<string>();

        do
        {
            // Keeping track of all the prompts and responses means we can rebuild the chat message history
            // without including the old context every time, saving on tokens
            var chatGpt = new ChatGPTLogic(openAILogic, initialRequest);
            chatGpt.ClearMessages();

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
                if (documents.Count > 0)
                {
                    // Search for the closest few documents and add those if they aren't used yet
                    var closestDocuments =
                        Document.FindMostSimilarDocuments(documents, await openAILogic.GetEmbeddingForPrompt(chatInput), gptParameters.ClosestMatchLimit);
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
        List<Document> documents = new ();
        if (parameters.EmbedFilenames is { Length: > 0 })
        {
            foreach (var embedFile in parameters.EmbedFilenames)
            {
                await using var fileStream = File.OpenRead(embedFile);

                documents.AddRange(Document.LoadEmbeddings(fileStream));
            }
        }

        return documents;
    }

    private static async Task<List<Document>> ReadEmbedDirectoriesAsync(GPTParameters parameters)
    {
        List<Document> documents = new();

        if (parameters.EmbedDirectoryNames is { Length: > 0 })
        {
            foreach (var embedDirectory in parameters.EmbedDirectoryNames)
            {
                var files = Directory.EnumerateFiles(embedDirectory);
                foreach (var file in files)
                {
                    await using var fileStream = File.OpenRead(file);

                    documents.AddRange(Document.LoadEmbeddings(fileStream));
                }
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


    private static void ConfigureServices(IServiceCollection services, GPTParametersBinder gptParametersBinder, Mode mode)
    {
        var gptParameters = gptParametersBinder.GPTParameters;

        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile(gptParameters.Config ?? "appSettings.json", optional: true, reloadOnChange: false)
            .Build();

        gptParameters.ApiKey ??= Configuration["OpenAI:api-key"];
        gptParameters.BaseDomain ??= Configuration["OpenAI:base-domain"];

        if (Console.IsInputRedirected)
        {
            gptParameters.Input = Console.OpenStandardInput();
        }

        // Add the configuration object to the services
        services.AddSingleton<IConfiguration>(Configuration);
        services.AddOpenAIService(settings =>
        {
            settings.ApiKey = gptParameters.ApiKey;
            settings.BaseDomain = gptParameters.BaseDomain;
        });
        services.AddSingleton<OpenAILogic>();
        services.AddSingleton<DiscordSocketClient>();
        services.AddSingleton<DiscordBot>();



        if (mode == Mode.Http)
        {
            // Add OpenAPI/Swagger document generation
            //services.AddSwaggerGen(c =>
            //{
            //    c.SwaggerDoc("v1", new OpenApiInfo { Title = "GPT-CLI API", Version = "v1" });

            //    // Set the comments path for the Swagger JSON and UI
            //    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            //    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            //    c.IncludeXmlComments(xmlPath);

            //    // Include custom API descriptions
            //    var apiDescriptionsFile = "ApiDescriptions.xml";
            //    var apiDescriptionsPath = Path.Combine(AppContext.BaseDirectory, apiDescriptionsFile);
            //    c.IncludeXmlComments(apiDescriptionsPath);
            //});

            // Add Azure AD B2C authentication
            // Add authentication with Azure AD B2C
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(options =>
                {
                    Configuration.Bind("AzureAdB2C", options);
                    options.TokenValidationParameters.NameClaimType = "name";
                },options => Configuration.Bind("AzureAdB2C", options));

            
            // Add minimal API
            services.AddEndpointsApiExplorer();
            services.AddRouting();

            // Add Swagger
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
            });
        }
    }

    public static IConfigurationRoot Configuration { get; set; }

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
                    "You will receive two messages from the user. The first message will be text for you to parse and understand. The next message will be a prompt describing how you should proceed. You will read through the text or code in the first message, understand it, and then apply the prompt in the second message, with the first message as your main context. Your final message after the prompt should only be the result of the prompt applied to the input text with no preamble."),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Sure. I will read through the first message and understand it. Then I'll wait for another message containing the prompt. After I apply the prompt to the original text, my final response will be the result of applying the prompt to my understanding of the input text."),
                new(StaticValues.ChatMessageRoles.User, input),
                new(StaticValues.ChatMessageRoles.Assistant,
                    "Thank you. Now I will wait for the prompt and then apply it in context.")
            }
        }, Mode.Completion);

        // This is placed here so the MapCommon method can add contextual embeddings before the prompt
        request.Messages.Add(new(StaticValues.ChatMessageRoles.User, parameters.Prompt));

        return request;

    }

    private static async Task<ChatCompletionCreateRequest> MapCommon(GPTParameters parameters, OpenAILogic openAILogic, ChatCompletionCreateRequest request, Mode mode)
    {
        // It only makes sense to look for embeddings when in completion mode and when a prompt is provided
        if (mode == Mode.Completion && parameters.Prompt != null)
        {
            // Read the embeddings from the files and directories (empty list is returned if none are provided)
            var documents = await ReadEmbedFilesAsync(parameters);
            documents.AddRange(await ReadEmbedDirectoriesAsync(parameters));

            // If there were embeddings supplied either as files or directories:
            if (documents.Count > 0)
            {
                // Search for the closest few documents and add those if they aren't used yet
                var closestDocuments =
                    Document.FindMostSimilarDocuments(documents,
                        await openAILogic.GetEmbeddingForPrompt(parameters.Prompt), parameters.ClosestMatchLimit);

                // Add any closest documents to the request
                if (closestDocuments != null)
                {
                    foreach (var closestDocument in closestDocuments)
                    {
                        request.Messages.Add(new(StaticValues.ChatMessageRoles.User,
                            $"Context for the next message: {closestDocument.Text}"));
                    }
                }
            }
        }

        // Map the common parameters to the request
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
        }, Mode.Completion);

        request.Messages.Add(new(StaticValues.ChatMessageRoles.System, parameters.Prompt));

        return request;
    }

    private static bool VerifySignature(string publicKey, string signature, string timestamp, string body)
    {
        byte[] publicKeyBytes = StringToByteArray(publicKey);
        byte[] signatureBytes = StringToByteArray(signature);
        byte[] timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        try
        {
            var pubKeyParam = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
            var verifier = SignerUtilities.GetSigner("Ed25519");

            verifier.Init(false, pubKeyParam);

            byte[] combinedBytes = new byte[timestampBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(timestampBytes, 0, combinedBytes, 0, timestampBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, combinedBytes, timestampBytes.Length, bodyBytes.Length);

            verifier.BlockUpdate(combinedBytes, 0, combinedBytes.Length);

            return verifier.VerifySignature(signatureBytes);
        }
        catch (Exception ex)
        {
            return false;
        }

        
    }
    private static byte[] StringToByteArray(string hex)
    {
        int length = hex.Length;
        byte[] bytes = new byte[length / 2];
        for (int i = 0; i < length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }
    

    public class InteractionPayload
    {
        public string ApplicationId { get; set; }
        public string Id { get; set; }
        public string Token { get; set; }
        public int Type { get; set; }
        public InteractionUser User { get; set; }
        public int Version { get; set; }
    }

    public class InteractionUser
    {
        public string Avatar { get; set; }
        public object AvatarDecoration { get; set; }
        public string Discriminator { get; set; }
        public object DisplayName { get; set; }
        public object GlobalName { get; set; }
        public string Id { get; set; }
        public int PublicFlags { get; set; }
        public string Username { get; set; }
    }

}