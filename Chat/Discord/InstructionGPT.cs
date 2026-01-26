using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using Mapster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord;

public class InstructionGPT : DiscordBotBase, IHostedService
{
    public InstructionGPT (DiscordSocketClient client, IConfiguration configuration, OpenAILogic openAILogic,
        GptOptions defaultParameters) : base(client, configuration, openAILogic, defaultParameters)
    {
    }
    public record ChannelState
    {
        [JsonPropertyName("guild-id")]
        public ulong GuildId { get; set; }

        [JsonPropertyName("guild-name")]
        public string GuildName { get; set; }

        [JsonPropertyName("channel-id")]
        public ulong ChannelId { get; set; }

        [JsonPropertyName("channel-name")]
        public string ChannelName { get; set; }

        [JsonPropertyName("chat-state")]
        public InstructionChatBot InstructionChat { get; set; }

        [JsonPropertyName("options")]
        public ChannelOptions Options { get; set; }
    }

    public record ChannelOptions
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("muted")]
        public bool Muted { get; set; }
    }






    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use configuration settings
        string token = DefaultParameters.BotToken
            ?? Configuration["GPT:BotToken"]
            ?? Configuration["Discord:BotToken"];


        // Login and start
        await Client.LoginAsync(TokenType.Bot, token);


        // Load embeddings
        await LoadEmbeddings();

        await Client.StartAsync();

        Client.Log += LogAsync;

        Client.Ready += async () =>
        {
            var response = await CreateGlobalCommand();
            await Console.Out.WriteLineAsync("Loading state");
            // Load state from discordState.json
            await LoadState();

            // Save state immediately since some new properties might have been added with default values
            await SaveState();

            // Message receiver is going to run in parallel
#pragma warning disable CS4014
            Client.MessageReceived += (message) =>
            {
                if (message?.Content != null)
                {
                    HandleMessageReceivedAsync(message);

                    SaveCachedChannelState(message.Channel.Id);
                }

                return Task.CompletedTask;
            };
#pragma warning restore CS4014

            await Console.Out.WriteLineAsync("Ready!");
        };


        // This is required for slash commands to work
        Client.InteractionCreated += HandleInteractionAsync;
        



        Client.MessageUpdated += async (oldMessage, newMessage, channel) =>
        {
            if (newMessage.Content != null && oldMessage.Value?.Content != newMessage.Content)
            {
                await HandleMessageReceivedAsync(newMessage);
            }
        };

        // Handle emoji reactions.
        Client.ReactionAdded += HandleReactionAsync;




        Client.MessageCommandExecuted += async (command) =>
        {
            await Console.Out.WriteLineAsync($"Command {command.CommandName} executed with result {command.Data.Message.Content}");
        };
    }

    private async Task<RestGlobalCommand> CreateGlobalCommand()
    {
        var command = new SlashCommandBuilder()
            .WithName("gptcli")
            .WithDescription("GPT-CLI commands")
            .AddOptions(new()
            {
                Name = "clear",
                Description = "Clear the instructions or messages, or all",
                Type = ApplicationCommandOptionType.SubCommand,
                Options = new()
                {
                    new SlashCommandOptionBuilder().WithName("messages").WithDescription("Clear messages")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("messages", "messages"),
                    new SlashCommandOptionBuilder().WithName("instructions").WithDescription("Clear instructions")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("instructions", "instructions"),
                    new SlashCommandOptionBuilder().WithName("all").WithDescription("Clear all")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("all", "all"),
                }
            }, new()
            {
                Name = "set",
                Description = "Settings",
                Type = ApplicationCommandOptionType.SubCommand,
                Options = new()
                {

                    new SlashCommandOptionBuilder().WithName("enabled").WithDescription("Enable or disable the chat bot")
                        .WithType(ApplicationCommandOptionType.Boolean),
                    new SlashCommandOptionBuilder().WithName("mute").WithDescription("Mute or unmute the chat bot")
                        .WithType(ApplicationCommandOptionType.Boolean),
                    new SlashCommandOptionBuilder().WithName("response-mode").WithDescription("Set the response mode")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("All", "All").AddChoice("Matches", "Matches"),
                    new SlashCommandOptionBuilder().WithName("embed-mode").WithDescription("Set the embed mode")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("Explicit", "Explicit").AddChoice("All", "All"),
                    new SlashCommandOptionBuilder().WithName("max-chat-history-length").WithDescription("Set the maximum chat history length")
                        .WithType(ApplicationCommandOptionType.Integer).WithMinValue(100),
                    new SlashCommandOptionBuilder().WithName("max-tokens").WithDescription("Set the maximum tokens")
                        .WithType(ApplicationCommandOptionType.Integer).WithMinValue(50),
                    new SlashCommandOptionBuilder().WithName("model").WithDescription("Set the model")
                        .WithType(ApplicationCommandOptionType.String).AddChoice("gpt-5.2", "gpt-5.2").AddChoice("gpt-5.2", "gpt-5.2").AddChoice("gpt-5.2", "gpt-5.2")
                }
            });

        var response = await Client.Rest.CreateGlobalCommand(command.Build());
        return response;
    }

    private async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> userMessage, Cacheable<IMessageChannel, ulong> messageChannel, SocketReaction reaction)
    {
        if (reaction.UserId == Client.CurrentUser.Id)
        {
            return;
        }

        var message = await userMessage.GetOrDownloadAsync();
        var channel = await messageChannel.GetOrDownloadAsync();


        switch (reaction.Emote.Name)
        {
            case "📌":
            {
                var channelState = ChannelBots[channel.Id];
                if (channelState.Options.Enabled)
                {
                    var chatBot = channelState.InstructionChat;
                    chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.System, message.Content));


                    using var typingState = channel.EnterTypingState();
                    await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                    await message.ReplyAsync("Instruction added.");
                    await SaveCachedChannelState(channel.Id);
                }

                break;
            }
            case "🔄":
            {
                // If the message is from the bot, ignore it. These aren't prompts.
                if (message.Author.Id == Client.CurrentUser.Id)
                {
                    return;
                }

                // Replay the message as a new message
                await HandleMessageReceivedAsync(message as SocketMessage);
                break;
            }
            case "💾":
            {
                if (message.Author.Id == Client.CurrentUser.Id)
                {
                        return;
                }

                await SaveEmbed(message);
                break;
            }

        }
    }


    private async Task LoadState()
    {
        await Console.Out.WriteLineAsync("Loading state...");
        Directory.CreateDirectory("channels");
        var files = Directory.GetFiles("channels", "*.state.json", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (!TryGetChannelIdFromStatePath(file, out var channelId))
            {
                continue;
            }

            await Console.Out.WriteLineAsync($"Loading state for channel {channelId}");
            await using var stream = File.OpenRead(file);
            var channelState = await ReadAsync(channelId, stream);
            if (channelState == null)
            {
                continue;
            }

            channelState.ChannelId = channelId;

            // Always read the channel and guild name in case they change
            var channel = await Client.GetChannelAsync(channelId) as IGuildChannel;
            if (channel != null)
            {
                EnsureChannelStateMetadata(channelState, channel);
            }
        
            channelState.InstructionChat ??= new(OpenAILogic, DefaultParameters);
            channelState.InstructionChat.ChatBotState ??= new() { PrimeDirectives = PrimeDirective.ToList() };
            channelState.InstructionChat.OpenAILogic = OpenAILogic;
        }
    }

    private async Task SaveState()
    {
        Directory.CreateDirectory("channels");
        foreach (var channelId in ChannelBots.Keys)
        {
            await SaveCachedChannelState(channelId);
        }
    }

    private async Task SaveCachedChannelState(ulong channelId)
    {
        if (!ChannelBots.TryGetValue(channelId, out var channelState))
        {
            channelState = InitializeChannel(channelId);
        }

        if (channelState.ChannelId == 0 || channelState.GuildId == 0)
        {
            var guildChannel = Client.GetChannel(channelId) as IGuildChannel;
            if (guildChannel != null)
            {
                EnsureChannelStateMetadata(channelState, guildChannel);
            }
            else
            {
                channelState.ChannelId = channelId;
            }
        }

        var channelDirectory = GetChannelDirectory(channelState);
        Directory.CreateDirectory(channelDirectory);
        var stateFileName = $"{GetChannelFileName(channelState)}.state.json";

        await using var stream = File.Create(Path.Combine(channelDirectory, stateFileName));
        await WriteAsync(channelId, stream);
    }

    // Method to write state to a Stream in JSON format
    private async Task WriteAsync(ulong channelId, Stream stream)
    {
        if (ChannelBots.TryGetValue(channelId, out var channelState))
        {
            // prepare serializer options
            // Serialize channelState to stream
            await JsonSerializer.SerializeAsync(stream, channelState,
                new JsonSerializerOptions { WriteIndented = true });

            ChannelBots[channelId] = channelState;
        }
    }

    // Method to read state from a Stream in JSON format
    private async Task<ChannelState> ReadAsync(ulong channelId, Stream stream)
    {
        try
        {
            var str = await new StreamReader(stream).ReadToEndAsync();
            // Deserialize channelState from stream
            var channelState = JsonSerializer.Deserialize<ChannelState>(str);

            if (channelState != null)
            {
                ChannelBots[channelId] = channelState;
            }

            return channelState;
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync(ex.ToString());
            return null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Save state to discordState.json
        await SaveState();
        await Client.StopAsync();
    }

    private async Task LogAsync(LogMessage log)
    {
        await Console.Out.WriteLineAsync(log.ToString());
    }

    private async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        if (message == null || message.Author.Id == Client.CurrentUser.Id)
            return;

        // Handle the received message here
        // ...
        var channel = ChannelBots.GetOrAdd(message.Channel.Id, InitializeChannel);
       
        if (channel.InstructionChat.ChatBotState.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.InstructionChat.ChatBotState.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.InstructionChat.ChatBotState.PrimeDirectives = PrimeDirective.ToList();
        }

        if (!channel.Options.Enabled)
            return;

        if (message.Content.StartsWith("!ignore"))
            return;

        if (message.Attachments is {Count: > 0})
        {
            await message.AddReactionAsync(new Emoji("💾"));
        }


        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        

        if (Documents.GetOrAdd(message.Channel.Id, new List<Document>()) is {Count: > 0} documents)
        {
            // Search for the closest few documents and add those if they aren't used yet
            var closestDocuments =
                Document.FindMostSimilarDocuments(documents, await OpenAILogic.GetEmbeddingForPrompt(message.Content), channel.InstructionChat.ChatBotState.Parameters.ClosestMatchLimit).ToList();
            if (closestDocuments.Any(cd => cd.Similarity > 0.80))
            {
                channel.InstructionChat.AddMessage(new(StaticValues.ChatMessageRoles.System,
                    $"Context for the next {closestDocuments.Count} message(s). Use this to answer:"));
                foreach (var closestDocument in closestDocuments)
                {
                    channel.InstructionChat.AddMessage(new(StaticValues.ChatMessageRoles.System,
                        $"---context---\r\n{closestDocument.Document.Text}\r\n--end context---"));
                }
            }
        }

        // Add this message as a chat log
        channel.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, $"<{message.Author.Username}> {message.Content}"));

        if (!channel.Options.Muted)
        {
            using var typingState = message.Channel.EnterTypingState();
            // Get the response from the bot
            var responses = channel.InstructionChat.GetResponseAsync();
            // Add the response as a chat 
            
            var sb = new StringBuilder();
            // Send the response to the channel
            await foreach (var response in responses)
            {
                if (response.Successful)
                {
                    var content = response.Choices.FirstOrDefault()?.Message.Content;
                    if (content is not null)
                    {
                        sb.Append(content);
                    }
                }
                else
                {
                    await Console.Out.WriteLineAsync(
                        $"Error code {response.Error?.Code}: {response.Error?.Message}");
                }
            }

            int chunkSize = 2000;
            int currentPosition = 0;

            while (currentPosition < sb.Length)
            {
                var size = Math.Min(chunkSize, sb.Length - currentPosition);
                var chunk = sb.ToString(currentPosition, size);
                currentPosition += size;

                var responseMessage = new ChatMessage(StaticValues.ChatMessageRoles.Assistant, chunk);

                channel.InstructionChat.AddMessage(responseMessage);
                // Convert message to SocketMessage

                if (message is IUserMessage userMessage)
                {
                    _ = await userMessage.ReplyAsync(responseMessage.Content, components: BuildStandardComponents());
                }
                else
                {
                    _ = await message.Channel.SendMessageAsync(responseMessage.Content, components: BuildStandardComponents());
                }
            }
        }

        await SaveCachedChannelState(message.Channel.Id);
    }

    private MessageComponent BuildStandardComponents()
    {
        // pushpin unicode escape: \U0001F4CC
        var builder = new ComponentBuilder()
            .WithButton("📌Instruct", "instruction")
            .WithButton("💾Embed", "embed");
        return builder.Build();
    }

    private readonly List<ChatMessage> _defaultPrimeDirective = new(1)
    {
        new
        (StaticValues.ChatMessageRoles.System,
            "This is the Prime Directive: This is a chat bot running in [GPT-CLI](https://github.com/kainazzzo/GPT-CLI). Answer questions and" +
            " provide responses in Discord message formatting. Encourage users to add instructions with /gptcli or by using the :up_arrow:" +
            " emoji reaction on any message. Instructions are like 'sticky' chat messages that provide upfront context to the bot. The the 📌 emoji reaction is for pinning a message to instructions. The 🔄 emoji reaction is for replaying a message as a new prompt.")
    };

    private IEnumerable<ChatMessage> PrimeDirective => _defaultPrimeDirective;

    private ChannelState InitializeChannel(ulong channelId)
    {
        var discordChannel = Client.GetChannel(channelId) as IGuildChannel;
        if (discordChannel is null)
        {
            throw new Exception($"Channel {channelId} not found");
        }
        ChannelState channel = new()
        {
            GuildId = discordChannel.GuildId,
            ChannelName = discordChannel.Name,
            GuildName = discordChannel.Guild.Name,
            ChannelId = discordChannel.Id,
            InstructionChat = new(OpenAILogic, DefaultParameters.Adapt<GptOptions>())
            {
                ChatBotState = new()
                {
                    PrimeDirectives = PrimeDirective.ToList(),
                    Parameters = DefaultParameters.Adapt<GptOptions>()
                }
            },
            Options = new(),
        };
        
        ChannelBots[channelId] = channel;

        return channel;
    }

    private async Task HandleInteractionAsync(SocketInteraction arg)
    {
        if (arg is SocketSlashCommand { CommandName: "gptcli" } socketSlashCommand)
        {
            await HandleGptCliCommand(socketSlashCommand);
        }
        else if (arg is SocketMessageComponent command)
        {
            switch (command.Data.CustomId)
            {
                case "instruction":
                    await HandleInstructionCommand(command);
                    break;
                case "embed":
                    await HandleEmbedCommand(command);
                    break;
            }

            await Console.Out.WriteLineAsync(
                $"Interaction: {arg.Type} {arg.Id} {arg} {arg.Channel.Id} {arg.Channel.Name} {arg.User.Id} {arg.User.Username}");
        }
    }

    private async Task HandleEmbedCommand(SocketMessageComponent command)
    {
        await SaveEmbed(command.Message);
    }

    private async Task SaveEmbed(IMessage message)
    {
        if (message == null)
        {
            return;
        }

        var channelState = ChannelBots.GetOrAdd(message.Channel.Id, InitializeChannel);
        if (!channelState.Options.Enabled)
        {
            return;
        }

        if (message.Channel is IGuildChannel guildChannel)
        {
            EnsureChannelStateMetadata(channelState, guildChannel);
        }

        var channelDirectory = GetChannelDirectory(channelState);
        var embedDirectory = Path.Combine(channelDirectory, "embeds");
        Directory.CreateDirectory(embedDirectory);

        var channelDocs = Documents.GetOrAdd(message.Channel.Id, new List<Document>());

        if (await CreateAndSaveEmbedding(message.Content, Path.Combine(embedDirectory, $"{message.Id}.embed.json"), channelState.InstructionChat.ChatBotState.Parameters.ChunkSize) is { Count: > 0 } newDocs)
        {
            channelDocs.AddRange(newDocs);


            using var embedStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(embedStream, newDocs);

            await message.Channel.SendFileAsync(embedStream, $"{message.Id}.embed.json",
                $"Message saved as {newDocs.Count} documents.",
                messageReference: message.Reference);
        }

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync(attachment.Url);
                if (response.IsSuccessStatusCode)
                {
                    await using var responseStream = await response.Content.ReadAsStreamAsync();
                    using var streamReader = new StreamReader(responseStream);
                    

                    if (await CreateAndSaveEmbedding(await streamReader.ReadToEndAsync(), Path.Combine(embedDirectory, $"{message.Id}.{attachment.Id}.embed.json"), channelState.InstructionChat.ChatBotState.Parameters.ChunkSize) is { Count: > 0 } attachmentDocs)
                    {
                        channelDocs.AddRange(attachmentDocs);

                        using var embedStream = new MemoryStream();
                        await JsonSerializer.SerializeAsync(embedStream, attachmentDocs);

                        await message.Channel.SendFileAsync(embedStream, $"{attachment.Filename}.embed.json",
                            $"Attachment {attachment.Filename} saved as {attachmentDocs.Count} documents.", 
                            messageReference: message.Reference);
                    }
                }
            }
        }
    }

    private async Task<List<Document>> CreateAndSaveEmbedding(string strToEmbed, string filename, int chunkSize)
    {
        var documents =
            await Document.ChunkToDocumentsAsync(strToEmbed, chunkSize);
        if (documents.Count > 0)
        {
            var newEmbeds = await OpenAILogic.CreateEmbeddings(documents);
            if (newEmbeds.Successful)
            {
                await using var file = File.Create(filename);
                await JsonSerializer.SerializeAsync(file, documents);
            }
        }

        return documents;
    }

    private async Task LoadEmbeddings()
    {
        Directory.CreateDirectory("channels");
        var files = Directory.GetFiles("channels", "*.embed.json", SearchOption.AllDirectories).ToList();
        
        foreach (var file in files)
        {
            await using var fileStream = File.OpenRead(file);
            var documents = await JsonSerializer.DeserializeAsync<List<Document>>(fileStream);
            if (!TryGetChannelIdFromEmbedPath(file, out var channelId))
            {
                continue;
            }
            var channelDocs = Documents.GetOrAdd(channelId, _ => new List<Document>());
            channelDocs.AddRange(documents);
        }
    
    }

    private static void EnsureChannelStateMetadata(ChannelState channelState, IGuildChannel guildChannel)
    {
        channelState.GuildId = guildChannel.GuildId;
        channelState.GuildName = guildChannel.Guild.Name;
        channelState.ChannelId = guildChannel.Id;
        channelState.ChannelName = guildChannel.Name;
    }

    private static string GetChannelDirectory(ChannelState channelState)
    {
        var guildFolder = $"{SanitizeName(channelState.GuildName ?? "guild")}_{channelState.GuildId}";
        var channelFolder = $"{SanitizeName(channelState.ChannelName ?? "channel")}_{channelState.ChannelId}";
        return Path.Combine("channels", guildFolder, channelFolder);
    }

    private static string GetChannelFileName(ChannelState channelState)
    {
        return $"{SanitizeName(channelState.ChannelName ?? "channel")}_{channelState.ChannelId}";
    }

    private static bool TryGetChannelIdFromStatePath(string file, out ulong channelId)
    {
        channelId = 0;
        var channelDirectory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(channelDirectory))
        {
            return false;
        }

        var channelFolder = new DirectoryInfo(channelDirectory).Name;
        return TryParseIdFromName(channelFolder, out channelId);
    }

    private static bool TryGetChannelIdFromEmbedPath(string file, out ulong channelId)
    {
        channelId = 0;
        var embedDirectory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(embedDirectory))
        {
            return false;
        }

        var channelDirectory = Directory.GetParent(embedDirectory);
        if (channelDirectory == null)
        {
            return false;
        }

        return TryParseIdFromName(channelDirectory.Name, out channelId);
    }

    private static bool TryParseIdFromName(string name, out ulong id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var underscoreIndex = name.LastIndexOf('_');
        if (underscoreIndex >= 0 && underscoreIndex < name.Length - 1)
        {
            if (ulong.TryParse(name[(underscoreIndex + 1)..], out id))
            {
                return true;
            }
        }

        return ulong.TryParse(name, out id);
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        var lastWasDash = false;

        foreach (var ch in name)
        {
            var isInvalid = invalidChars.Contains(ch);
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (char.IsWhiteSpace(ch) || isInvalid)
            {
                if (!lastWasDash)
                {
                    builder.Append('-');
                    lastWasDash = true;
                }
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private async Task HandleInstructionCommand(SocketMessageComponent command)
    {
        var channelState = ChannelBots.GetOrAdd(command.Channel.Id, InitializeChannel);
        if (channelState.Options.Enabled)
        {
            var chatBot = channelState.InstructionChat;
            chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.System, command.Message.Content));
            await SaveCachedChannelState(command.Channel.Id);

            using var typingState = command.Channel.EnterTypingState();
            await command.RespondAsync("Instruction added");
        }
        
    }

    private async Task HandleGptCliCommand(SocketSlashCommand command)
    {
        var channel = command.Channel;
        if (!ChannelBots.TryGetValue(channel.Id, out var chatBot))
        {
            chatBot = InitializeChannel(channel.Id);
        }


        var options = command.Data.Options;
        foreach (var option in options)
        {
            var subOption = option.Options.FirstOrDefault();
            switch (option.Name)
            {
                case "clear":
                    
                    switch (subOption.Name)
                    {
                        case "messages":
                            chatBot.InstructionChat.ClearMessages();
                            
                            break;
                        case "instructions":
                            chatBot.InstructionChat.ClearInstructions();
                            break;
                        case "all":
                            chatBot.InstructionChat.ClearMessages();
                            chatBot.InstructionChat.ClearInstructions();
                            break;
                    }

                    var clearOptionValue = subOption.Name;

                    try
                    {
                        await command.RespondAsync(
                            $"{char.ToUpper(clearOptionValue[0])}{clearOptionValue.Substring(1)} cleared.");
                    }
                    catch (Exception ex)
                    {
                        await Console.Out.WriteLineAsync(ex.Message);
                    }
                
                    

                    break;
                case "instruction":
                    var instruction = option.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(instruction))
                    {
                        chatBot.InstructionChat.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));
                        await SaveCachedChannelState(channel.Id);
                        try
                        {
                            await command.RespondAsync("Instruction received!");
                        }
                        catch (Exception ex)
                        {
                            await Console.Out.WriteLineAsync(ex.Message);
                        }
                    }
                    break;
                case "instructions":
                    try
                    {
                        await command.RespondAsync($"Instructions: {chatBot.InstructionChat.InstructionStr}");
                    }
                    catch (Exception ex)
                    {
                        await Console.Out.WriteLineAsync(ex.Message);
                    }
                    break;
                case "set":
                    switch (subOption.Name)
                    {
                        case "enabled":
                            if (subOption.Value is bool enabled)
                            {
                                chatBot.Options.Enabled = enabled;
                                try
                                {
                                    await command.RespondAsync($"InstructionChat bot {(enabled ? "enabled" : "disabled")}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "mute":
                            if (subOption.Value is bool muted)
                            {
                                chatBot.Options.Muted = muted;

                                try
                                {
                                    await command.RespondAsync($"InstructionChat bot {(muted ? "muted" : "un-muted")}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "max-tokens":
                            if (subOption.Value is long maxTokens)
                            {
                                chatBot.InstructionChat.ChatBotState.Parameters.MaxTokens = (int?)maxTokens;
                                try
                                {
                                    await command.RespondAsync($"Max tokens set to {maxTokens}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "max-chat-history-length":
                            if (subOption.Value is long maxChatHistoryLength)
                            {
                                chatBot.InstructionChat.ChatBotState.Parameters.MaxChatHistoryLength = (uint)maxChatHistoryLength;
                                try
                                {
                                    await command.RespondAsync(
                                        $"Max chat history length set to {maxChatHistoryLength}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "model":
                            if (subOption.Value is string model)
                            {
                                chatBot.InstructionChat.ChatBotState.Parameters.Model = model;
                                try
                                {
                                    await command.RespondAsync($"Model set to {model}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "embed-mode":
                            if (subOption.Value is string embedMode)
                            {
                                chatBot.InstructionChat.ChatBotState.EmbedMode = Enum.Parse<InstructionChatBot.EmbedMode>(embedMode);
                                try
                                {
                                    await command.RespondAsync($"Embed mode set to {embedMode}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;
                        case "response-mode":
                            if (subOption.Value is string responseMode)
                            {
                                chatBot.InstructionChat.ChatBotState.ResponseMode = Enum.Parse<InstructionChatBot.ResponseMode>(responseMode);
                                try
                                {
                                    await command.RespondAsync($"Response mode set to {responseMode}.");
                                }
                                catch (Exception ex)
                                {
                                    await Console.Out.WriteLineAsync(ex.Message);
                                }
                            }
                            break;

                    }
                    break;
            }
        }

        await SaveCachedChannelState(channel.Id);
    }
}
