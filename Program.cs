using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI.GPT3.Extensions;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat;

namespace GPT.CLI;

class Program
{
    static async Task Main(string[] args)
    {
        // Define command line parameters
        var apiKeyOption = new Option<string>("--api-key", "Your OpenAI API key");
        var baseUrlOption = new Option<string>("--base-domain", "The base URL for the OpenAI API");
        var promptOption = new Option<string>("--prompt", "The prompt for text generation. Optional for most commands.") { IsRequired = true };

        var configOption = new Option<string>("--config", () => "appSettings.json", "The path to the appSettings.json config file");

        // Add the rest of the available fields as command line parameters
        var modelOption = new Option<string>("--model", () => Models.ChatGpt3_5Turbo, "The model ID to use.");
        var maxTokensOption = new Option<int>("--max-tokens", () => 1500, "The maximum number of tokens to generate in the completion.");
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
        var discordCommand = new Command("discord", "Starts the CLI as a Discord bot that receives messages from all channels on your server.");
        var botTokenOption = new Option<string>("--bot-token", "The token for your Discord bot.");
        var maxChatHistoryLengthOption = new Option<uint>("--max-chat-history-length", () => 2048, "The maximum message length to keep in chat history (chat & discord modes).");

        var chunkSizeOption = new Option<int>("--chunk-size", () => 1024,
            "The size to chunk down text into embeddable documents.");
        var embedFileOption = new Option<string[]>("--file", "Name of a file from which to load previously saved embeddings. Multiple files allowed.")
        { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore };
        var embedDirectoryOption = new Option<string[]>("--directory", () => new[] { "embeds" },
            "Name of a directory from which to load previously saved embeddings. Multiple directories allowed.")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore
        };

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
        chatCommand.AddOption(chunkSizeOption);

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
        rootCommand.AddCommand(discordCommand);
        rootCommand.AddOption(botTokenOption);
        chatCommand.AddOption(maxChatHistoryLengthOption);
        discordCommand.AddOption(maxChatHistoryLengthOption);


        var binder = new GPTParametersBinder(
            apiKeyOption, baseUrlOption, promptOption, configOption,
            modelOption, maxTokensOption, temperatureOption, topPOption,
            nOption, streamOption, stopOption,
            presencePenaltyOption, frequencyPenaltyOption, logitBiasOption,
            userOption, embedFileOption, embedDirectoryOption, chunkSizeOption, matchLimitOption, botTokenOption, maxChatHistoryLengthOption);

        ParameterMapping.Mode mode = ParameterMapping.Mode.Completion;

        // Set the handler for the rootCommand
        rootCommand.SetHandler(_ => { }, binder);
        chatCommand.SetHandler(_ => mode = ParameterMapping.Mode.Chat, binder);
        embedCommand.SetHandler(_ => mode = ParameterMapping.Mode.Embed, binder);
        discordCommand.SetHandler(_ => mode = ParameterMapping.Mode.Discord, binder);

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
                case ParameterMapping.Mode.Chat:
                    {
                        await HandleChatMode(openAILogic, binder.GPTParameters);
                        break;
                    }
                case ParameterMapping.Mode.Embed:
                    {
                        await HandleEmbedMode(openAILogic, binder.GPTParameters);
                        break;
                    }
                case ParameterMapping.Mode.Discord:
                    {
                        await HandleDiscordMode(openAILogic, binder.GPTParameters, services);
                        break;
                    }
                case ParameterMapping.Mode.Completion:
                default:
                    {
                        await HandleCompletionMode(openAILogic, binder.GPTParameters);

                        break;
                    }
            }
        }
    }

    private static async Task HandleDiscordMode(OpenAILogic openAILogic, GPTParameters gptParameters, IServiceCollection services)
    {
        var hostBuilder = new HostBuilder().ConfigureServices(innerServices =>
        {
            foreach (var service in services)
            {
                innerServices.Add(service);
            }

            innerServices.AddHostedService<DiscordBot>();
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
                                                                           ,----,                      ,--,
                                                        ,-.----.         ,/   .`|                   ,---.'|
  ,----..    ,---,                   ___      ,----..   \    /  \      ,`   .'  :          ,----..  |   | :      ,---,
 /   /   \ ,--.' |                 ,--.'|_   /   /   \  |   :    \   ;    ;     /         /   /   \ :   : |   ,`--.' |
|   :     :|  |  :                 |  | :,' |   :     : |   |  .\ :.'___,/    ,'         |   :     :|   ' :   |   :  :
.   |  ;. /:  :  :                 :  : ' : .   |  ;. / .   :  |: ||    :     |          .   |  ;. /;   ; '   :   |  '
.   ; /--` :  |  |,--.  ,--.--.  .;__,'  /  .   ; /--`  |   |   \ :;    |.';  ;          .   ; /--` '   | |__ |   :  |
;   | ;    |  :  '   | /       \ |  |   |   ;   | ;  __ |   : .   /`----'  |  |          ;   | ;    |   | :.'|'   '  ;
|   : |    |  |   /' :.--.  .-. |:__,'| :   |   : |.' .';   | |`-'     '   :  ;          |   : |    '   :    ;|   |  |
.   | '___ '  :  | | | \__\/: . .  '  : |__ .   | '_.' :|   | ;        |   |  '          .   | '___ |   |  ./ '   :  ;
'   ; : .'||  |  ' | : ,' .--.; |  |  | '.'|'   ; : \  |:   ' |        '   :  |          '   ; : .'|;   : ;   |   |  '
'   | '/  :|  :  :_:,'/  /  ,.  |  ;  :    ;'   | '/  .':   : :        ;   |.'           '   | '/  :|   ,/    '   :  |
|   :    / |  | ,'   ;  :   .'   \ |  ,   / |   :    /  |   | :        '---'             |   :    / '---'     ;   |.'
 \   \ .'  `--''     |  ,     .-./  ---`-'   \   \ .'   `---'.|                           \   \ .'            '---'
  `---`               `--`---'                `---`       `---`                            `---`", 119),
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
  \/_____/   \/_/\/_/   \/_/\/_/     \/_/   \/_____/   \/_/       \/_/      \/_____/   \/_____/   \/_/", 103),
        new(@"
 ▄████████    ▄█    █▄       ▄████████     ███        ▄██████▄     ▄███████▄     ███           ▄████████  ▄█        ▄█
███    ███   ███    ███     ███    ███ ▀█████████▄   ███    ███   ███    ███ ▀█████████▄      ███    ███ ███       ███
███    █▀    ███    ███     ███    ███    ▀███▀▀██   ███    █▀    ███    ███    ▀███▀▀██      ███    █▀  ███       ███▌
███         ▄███▄▄▄▄███▄▄   ███    ███     ███   ▀  ▄███          ███    ███     ███   ▀      ███        ███       ███▌
███        ▀▀███▀▀▀▀███▀  ▀███████████     ███     ▀▀███ ████▄  ▀█████████▀      ███          ███        ███       ███▌
███    █▄    ███    ███     ███    ███     ███       ███    ███   ███            ███          ███    █▄  ███       ███
███    ███   ███    ███     ███    ███     ███       ███    ███   ███            ███          ███    ███ ███▌    ▄ ███
████████▀    ███    █▀      ███    █▀     ▄████▀     ████████▀   ▄████▀         ▄████▀        ████████▀  █████▄▄██ █▀", 120),
        new(@"
 ▄▄▄▄▄▄▄▄▄▄▄  ▄         ▄  ▄▄▄▄▄▄▄▄▄▄▄  ▄▄▄▄▄▄▄▄▄▄▄  ▄▄▄▄▄▄▄▄▄▄▄  ▄▄▄▄▄▄▄▄▄▄▄  ▄▄▄▄▄▄▄▄▄▄▄       ▄▄▄▄▄▄▄▄▄▄▄  ▄            ▄▄▄▄▄▄▄▄▄▄▄ 
▐░░░░░░░░░░░▌▐░▌       ▐░▌▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌     ▐░░░░░░░░░░░▌▐░▌          ▐░░░░░░░░░░░▌
▐░█▀▀▀▀▀▀▀▀▀ ▐░▌       ▐░▌▐░█▀▀▀▀▀▀▀█░▌ ▀▀▀▀█░█▀▀▀▀ ▐░█▀▀▀▀▀▀▀▀▀ ▐░█▀▀▀▀▀▀▀█░▌ ▀▀▀▀█░█▀▀▀▀      ▐░█▀▀▀▀▀▀▀▀▀ ▐░▌           ▀▀▀▀█░█▀▀▀▀ 
▐░▌          ▐░▌       ▐░▌▐░▌       ▐░▌     ▐░▌     ▐░▌          ▐░▌       ▐░▌     ▐░▌          ▐░▌          ▐░▌               ▐░▌     
▐░▌          ▐░█▄▄▄▄▄▄▄█░▌▐░█▄▄▄▄▄▄▄█░▌     ▐░▌     ▐░▌ ▄▄▄▄▄▄▄▄ ▐░█▄▄▄▄▄▄▄█░▌     ▐░▌          ▐░▌          ▐░▌               ▐░▌     
▐░▌          ▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌     ▐░▌     ▐░▌▐░░░░░░░░▌▐░░░░░░░░░░░▌     ▐░▌          ▐░▌          ▐░▌               ▐░▌     
▐░▌          ▐░█▀▀▀▀▀▀▀█░▌▐░█▀▀▀▀▀▀▀█░▌     ▐░▌     ▐░▌ ▀▀▀▀▀▀█░▌▐░█▀▀▀▀▀▀▀▀▀      ▐░▌          ▐░▌          ▐░▌               ▐░▌     
▐░▌          ▐░▌       ▐░▌▐░▌       ▐░▌     ▐░▌     ▐░▌       ▐░▌▐░▌               ▐░▌          ▐░▌          ▐░▌               ▐░▌     
▐░█▄▄▄▄▄▄▄▄▄ ▐░▌       ▐░▌▐░▌       ▐░▌     ▐░▌     ▐░█▄▄▄▄▄▄▄█░▌▐░▌               ▐░▌          ▐░█▄▄▄▄▄▄▄▄▄ ▐░█▄▄▄▄▄▄▄▄▄  ▄▄▄▄█░█▄▄▄▄ 
▐░░░░░░░░░░░▌▐░▌       ▐░▌▐░▌       ▐░▌     ▐░▌     ▐░░░░░░░░░░░▌▐░▌               ▐░▌          ▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌▐░░░░░░░░░░░▌
 ▀▀▀▀▀▀▀▀▀▀▀  ▀         ▀  ▀         ▀       ▀       ▀▀▀▀▀▀▀▀▀▀▀  ▀                 ▀            ▀▀▀▀▀▀▀▀▀▀▀  ▀▀▀▀▀▀▀▀▀▀▀  ▀▀▀▀▀▀▀▀▀▀▀", 136)

    };

    private static async Task HandleChatMode(OpenAILogic openAILogic, GPTParameters gptParameters)
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

        var chatBot = new ChatBot(openAILogic, gptParameters);

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
                chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.User,
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

                var embedding = await Document.ChunkStreamToDocumentsAsync(ms, 2048);
                Directory.CreateDirectory("embeds");
                Directory.CreateDirectory("embeds/console/");

                var embeddings = await openAILogic.CreateEmbeddings(embedding);
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
                    Document.FindMostSimilarDocuments(documents, await openAILogic.GetEmbeddingForPrompt(chatInput), gptParameters.ClosestMatchLimit);
                if (closestDocuments != null)
                {
                    chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User,
                        $"Embedding context for the next {closestDocuments.Count} message(s). Please use this information to answer the next prompt"));
                    foreach (var closestDocument in closestDocuments)
                    {
                        chatBot.AddMessage(new(StaticValues.ChatMessageRoles.User,
                                $"---context---\r\n{closestDocument.Text}\r\n--end context---"));
                    }
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

    public static async Task<List<Document>> ReadEmbedFilesAsync(GPTParameters parameters)
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

    public static async Task<List<Document>> ReadEmbedDirectoriesAsync(GPTParameters parameters)
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


    private static void ConfigureServices(IServiceCollection services, GPTParametersBinder gptParametersBinder, ParameterMapping.Mode mode)
    {
        var gptParameters = gptParametersBinder.GPTParameters;

        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile(gptParameters.Config ?? "appSettings.json", optional: true, reloadOnChange: false)
            .Build();

        gptParameters.ApiKey ??= Configuration["OpenAI:api-key"];
        gptParameters.BaseDomain ??= Configuration["OpenAI:base-domain"];

        // Add the configuration object to the services
        services.AddSingleton<IConfiguration>(Configuration);
        services.AddOpenAIService(settings =>
        {
            settings.ApiKey = gptParameters.ApiKey;
            settings.BaseDomain = gptParameters.BaseDomain;
        });
        services.AddSingleton<OpenAILogic>();

        services.AddSingleton(_ => new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers |
                             GatewayIntents.MessageContent | GatewayIntents.DirectMessages |
                             GatewayIntents.DirectMessageReactions |
                             GatewayIntents.GuildMessageReactions | GatewayIntents.GuildEmojis,
            MessageCacheSize = 10
        });

        services.AddScoped<DiscordSocketClient>();

        services.AddSingleton<DiscordBot>();
        services.AddSingleton(_ => gptParameters);
    }

    public static IConfigurationRoot Configuration { get; set; }
}