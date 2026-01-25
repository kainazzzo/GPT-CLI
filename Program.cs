using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat;
using OpenAI.Extensions;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;

namespace GPT.CLI;

class Program
{
    static async Task Main(string[] args)
    {
        var (modeOverride, remainingArgs) = ParseModeOverride(args);

        using var host = Host.CreateDefaultBuilder(remainingArgs)
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services, context.Configuration, modeOverride);
            })
            .Build();

        var gptParameters = host.Services.GetRequiredService<GptOptions>();

        if (string.IsNullOrWhiteSpace(gptParameters.ApiKey))
        {
            await Console.Error.WriteLineAsync("Missing OpenAI ApiKey. Set OpenAI:ApiKey or GPT:ApiKey in configuration.");
            return;
        }

        if (gptParameters.Mode == ParameterMapping.Mode.Discord)
        {
            if (string.IsNullOrWhiteSpace(gptParameters.BotToken))
            {
                await Console.Error.WriteLineAsync("Missing Discord bot token. Set GPT:BotToken in configuration.");
                return;
            }

            await host.RunAsync();
            return;
        }

        var openAILogic = host.Services.GetRequiredService<OpenAILogic>();

        switch (gptParameters.Mode)
        {
            case ParameterMapping.Mode.Chat:
                {
                    await HandleChatMode(openAILogic, gptParameters);
                    break;
                }
            case ParameterMapping.Mode.Embed:
                {
                    await HandleEmbedMode(openAILogic, gptParameters);
                    break;
                }
            case ParameterMapping.Mode.Completion:
            default:
                {
                    if (string.IsNullOrWhiteSpace(gptParameters.Prompt))
                    {
                        await Console.Error.WriteLineAsync("Missing GPT prompt. Set GPT:Prompt in configuration.");
                        return;
                    }

                    await HandleCompletionMode(openAILogic, gptParameters);
                    break;
                }
        }
    }

    private static async Task HandleEmbedMode(OpenAILogic openAILogic, GptOptions gptParameters)
    {
        // Create and output embedding
        var documents = await Document.ChunkToDocumentsAsync(Console.OpenStandardInput(), gptParameters.ChunkSize);

        await openAILogic.CreateEmbeddings(documents);

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(documents));
    }

    private static async Task HandleCompletionMode(OpenAILogic openAILogic, GptOptions gptParameters)
    {
        var chatRequest = Console.IsInputRedirected
            ? await ParameterMapping.MapChatEdit(gptParameters, openAILogic, Console.OpenStandardInput())
            : await ParameterMapping.MapChatCreate(gptParameters, openAILogic);

        var responses = gptParameters.Stream == true
            ? openAILogic.CreateChatCompletionAsyncEnumerable(chatRequest)
            : (await openAILogic.CreateChatCompletionAsync(chatRequest)).ToAsyncEnumerable();
        await foreach (var response in responses)
        {
            await OutputChatResponse(response);
        }
    }

    private static readonly Random Random = new();
    private static int _lastLogo = 0;
    private static readonly List<(string Logo, int Width)> Logos = new()
    {
        new(@"
   ________          __  __________  ______   ________    ____
  / ____/ /_  ____ _/ /_/ ____/ __ \/_  __/  / ____/ /   /  _/
 / /   / __ \/ __ `/ __/ / __/ /_/ / / /    / /   / /    / /  
/ /___/ / / / /_/ / /_/ /_/ / ____/ / /    / /___/ /____/ /   
\____/_/ /_/\__,_/\__/\____/_/     /_/     \____/_____/___/", 63),
        new(@"
╔═╗┬ ┬┌─┐┌┬┐╔═╗╔═╗╔╦╗  ╔═╗╦  ╦
║  ├─┤├─┤ │ ║ ╦╠═╝ ║   ║  ║  ║
╚═╝┴ ┴┴ ┴ ┴ ╚═╝╩   ╩   ╚═╝╩═╝╩
",31),
        new (@"
   ___ _           _     ___   ___  _____     ___   __   _____ 
  / __\ |__   __ _| |_  / _ \ / _ \/__   \   / __\ / /   \_   \
 / /  | '_ \ / _` | __|/ /_\// /_)/  / /\/  / /   / /     / /\/
/ /___| | | | (_| | |_/ /_\\/ ___/  / /    / /___/ /___/\/ /_  
\____/|_| |_|\__,_|\__\____/\/      \/     \____/\____/\____/", 64),
        new(@"
  _              __  _ ___    _    ___ 
 /  |_   _. _|_ /__ |_) |    /  |   |  
 \_ | | (_|  |_ \_| |   |    \_ |_ _|_
                                       ", 38),
        new(@"
    __  __ __   ____  ______   ____  ____  ______         __  _      ____ 
   /  ]|  |  | /    ||      | /    ||    \|      |       /  ]| |    |    |
  /  / |  |  ||  o  ||      ||   __||  o  )      |      /  / | |     |  | 
 /  /  |  _  ||     ||_|  |_||  |  ||   _/|_|  |_|     /  /  | |___  |  | 
/   \_ |  |  ||  _  |  |  |  |  |_ ||  |    |  |      /   \_ |     | |  | 
\     ||  |  ||  |  |  |  |  |     ||  |    |  |      \     ||     | |  | 
 \____||__|__||__|__|  |__|  |___,_||__|    |__|       \____||_____||____|", 75),
        new(@"
 ,-----.,--.               ,--.   ,----.   ,------. ,--------.     ,-----.,--.   ,--. 
'  .--./|  ,---.  ,--,--.,-'  '-.'  .-./   |  .--. ''--.  .--'    '  .--./|  |   |  | 
|  |    |  .-.  |' ,-.  |'-.  .-'|  | .---.|  '--' |   |  |       |  |    |  |   |  | 
'  '--'\|  | |  |\ '-'  |  |  |  '  '--'  ||  | --'    |  |       '  '--'\|  '--.|  | 
 `-----'`--' `--' `--`--'  `--'   `------' `--'        `--'        `-----'`-----'`--'", 86),
        new(@"
 ______     __  __     ______     ______   ______     ______   ______      ______     __         __    
/\  ___\   /\ \_\ \   /\  __ \   /\__  _\ /\  ___\   /\  == \ /\__  _\    /\  ___\   /\ \       /\ \   
\ \ \____  \ \  __ \  \ \  __ \  \/_/\ \/ \ \ \__ \  \ \  _-/ \/_/\ \/    \ \ \____  \ \ \____  \ \ \  
 \ \_____\  \ \_\ \_\  \ \_\ \_\    \ \_\  \ \_____\  \ \_\      \ \_\     \ \_____\  \ \_____\  \ \_\ 
  \/_____/   \/_/\/_/   \/_/\/_/     \/_/   \/_____/   \/_/       \/_/      \/_____/   \/_____/   \/_/", 103)
    };

    private static async Task HandleChatMode(OpenAILogic openAILogic, GptOptions gptParameters)
    {
        var initialRequest = await ParameterMapping.MapCommon(gptParameters, openAILogic, new ChatCompletionCreateRequest()
        {
            Messages = new List<ChatMessage>(50)
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are ChatGPT CLI, the helpful assistant, but you're running on a command line.")
            }
        }, ParameterMapping.Mode.Chat);

        async Task PrintLogo()
        {
            var newLogo = GetRandomLogo();
            while (newLogo == _lastLogo)
            {
                newLogo = GetRandomLogo();
            }

            _lastLogo = newLogo;
            await Console.Out.WriteLineAsync(Logos[newLogo].Logo);
        }

        await PrintLogo();

        var sb = new StringBuilder();
        var documents = await ReadEmbedFilesAsync(gptParameters);
        documents.AddRange(await ReadEmbedDirectoriesAsync(gptParameters));

        var prompts = new List<string>();
        var promptResponses = new List<string>();

        var chatBot = new InstructionChatBot(openAILogic, gptParameters);

        do
        {
            await Console.Out.WriteAsync("\r\n? ");
            var chatInput = await Console.In.ReadLineAsync();

            if (string.IsNullOrWhiteSpace(chatInput))
            {
                await PrintLogo();
                continue;
            }

            if ("exit".Equals(chatInput, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (chatInput.StartsWith("!instruction "))
            {
                // add instruction:
                chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.System,
                    chatInput.Substring(13)));
                await Console.Out.WriteLineAsync($"Instructions added: {chatInput.Substring(13)}");
                continue;
            }
            if (chatInput.StartsWith("!instructions"))
            {
                await Console.Out.WriteLineAsync($"My instructions are:");
                await Console.Out.WriteLineAsync(chatBot.InstructionStr);
                continue;
            }

            if (chatInput.StartsWith("!embed"))
            {
                await Console.Out.WriteLineAsync($"Paste in text to embed. CTRL-Z -> Enter when finished: ");
                // get stream for Console.In and read until EOF
                var input = await Console.In.ReadToEndAsync();
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(input));

                var embedding = await Document.ChunkToDocumentsAsync(ms, 2048);
                Directory.CreateDirectory("embeds");
                Directory.CreateDirectory("embeds/console/");

                _ = await openAILogic.CreateEmbeddings(embedding);
                var filePath = $"embeds/console/{Guid.NewGuid()}.json";
                await using var outputStream = File.Create(filePath);
                await JsonSerializer.SerializeAsync(outputStream, embedding);
                await Console.Out.WriteLineAsync($"Embedding saved to {filePath}");

                documents.AddRange(embedding);
                continue;
            }

            for (int i = 0; i < prompts.Count; i++)
            {
                chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User, prompts[i]));
                chatBot.AddMessage(new(StaticValues.ChatMessageRoles.Assistant, promptResponses[i]));
            }

            // If there's embedded context provided, inject it after the existing chat history, and before the new prompt
            if (documents.Count > 0)
            {
                // Search for the closest few documents and add those if they aren't used yet
                var closestDocuments =
                    Document.FindMostSimilarDocuments(documents, await openAILogic.GetEmbeddingForPrompt(chatInput), gptParameters.ClosestMatchLimit).ToList();
                chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User,
                    $"Embedding context for the next {closestDocuments.Count} message(s). Please use this information to answer the next prompt"));
                foreach (var closestDocument in closestDocuments)
                {
                    chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User,
                        $"---context---\r\n{closestDocument.Document.Text}\r\n--end context---"));
                }
            }

            prompts.Add(chatInput);
            chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User, chatInput));

            // Get the new response:
            var responses = chatBot.GetResponseAsync();
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

        } while (true);
    }

    private static int GetRandomLogo()
    {
        var fits = Logos.Where(l => l.Width >= Console.LargestWindowWidth).ToList();
        return Random.Next(Logos.Count);
    }

    public static async Task<List<Document>> ReadEmbedFilesAsync(GptOptions parameters)
    {
        List<Document> documents = new();
        if (parameters.EmbedFilenames is { Length: > 0 })
        {
            foreach (var embedFile in parameters.EmbedFilenames)
            {
                await using var fileStream = File.OpenRead(embedFile);
                var loaded = Document.LoadEmbeddings(fileStream);
                documents.AddRange(loaded);

                await Console.Out.WriteLineAsync($"Loaded {loaded.Count} embeddings from {embedFile}");
            }
        }

        return documents;
    }

    public static async Task<List<Document>> ReadEmbedDirectoriesAsync(GptOptions parameters)
    {
        List<Document> documents = new();

        if (parameters.EmbedDirectoryNames is { Length: > 0 })
        {
            foreach (var embedDirectory in parameters.EmbedDirectoryNames)
            {
                if (!Directory.Exists(embedDirectory))
                {
                    continue;
                }

                var files = Directory.EnumerateFiles(embedDirectory, "*.json", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    await using var fileStream = File.OpenRead(file);
                    var loaded = Document.LoadEmbeddings(fileStream);
                    documents.AddRange(loaded);
                    await Console.Out.WriteLineAsync($"Loaded {loaded.Count} embeddings from {embedDirectory}{Path.DirectorySeparatorChar}{file}");
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


    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration, ParameterMapping.Mode? modeOverride)
    {
        var gptParameters = BindParameters(configuration, modeOverride);

        // Add the configuration object to the services
        services.AddSingleton(configuration);
        services.AddOpenAIService(settings =>
        {
            settings.ApiKey = gptParameters.ApiKey;
            if (!string.IsNullOrWhiteSpace(gptParameters.BaseDomain))
            {
                settings.BaseDomain = gptParameters.BaseDomain;
            }
        });
        services.AddSingleton<OpenAILogic>();

        if (gptParameters.Mode == ParameterMapping.Mode.Discord)
        {
            services.AddSingleton(_ => new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers |
                                 GatewayIntents.MessageContent | GatewayIntents.DirectMessages |
                                 GatewayIntents.DirectMessageReactions |
                                 GatewayIntents.GuildMessageReactions | GatewayIntents.GuildEmojis,
                MessageCacheSize = 10
            });

            services.AddScoped<DiscordSocketClient>();
            services.AddHostedService<InstructionGPT>();
        }

        services.AddSingleton(gptParameters);
    }

    private static GptOptions BindParameters(IConfiguration configuration, ParameterMapping.Mode? modeOverride)
    {
        var gptParameters = configuration.GetSection("GPT").Get<GptOptions>() ?? new GptOptions();

        gptParameters.ApiKey ??= configuration["OpenAI:ApiKey"];
        gptParameters.BaseDomain ??= configuration["OpenAI:BaseDomain"];

        if (modeOverride.HasValue)
        {
            gptParameters.Mode = modeOverride.Value;
        }
        else
        {
            var modeRaw = configuration["GPT:Mode"];
            if (!string.IsNullOrWhiteSpace(modeRaw) &&
                Enum.TryParse<ParameterMapping.Mode>(modeRaw, true, out var parsedMode))
            {
                gptParameters.Mode = parsedMode;
            }
        }

        return gptParameters;
    }

    private static (ParameterMapping.Mode? ModeOverride, string[] RemainingArgs) ParseModeOverride(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "chat", StringComparison.OrdinalIgnoreCase))
        {
            return (ParameterMapping.Mode.Chat, args.Skip(1).ToArray());
        }

        return (null, args);
    }
}