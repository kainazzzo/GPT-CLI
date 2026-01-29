using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ulong, FactoidResponseMetadata> _factoidResponseMap = new();
    private record FactoidResponseMetadata(ulong ChannelId, string Term);

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

        [JsonPropertyName("learning-enabled")]
        public bool LearningEnabled { get; set; } = true;

        [JsonPropertyName("learning-personality-prompt")]
        public string LearningPersonalityPrompt { get; set; }

        [JsonPropertyName("factoid-similarity-threshold")]
        public double FactoidSimilarityThreshold { get; set; } = 0.80;
    }






    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use configuration settings
        string token = DefaultParameters.BotToken
            ?? Configuration["GPT:BotToken"]
            ?? Configuration["Discord:BotToken"];


        // Login and start
        await Client.LoginAsync(TokenType.Bot, token);


        // Load embeddings and factoids
        await LoadEmbeddings();
        await LoadFactoids();

        await Client.StartAsync();

        Client.Log += LogAsync;
        Client.Disconnected += async ex =>
        {
            await Console.Out.WriteLineAsync(ex == null
                ? "Gateway disconnected."
                : $"Gateway disconnected: {ex.GetType().Name} - {ex.Message}");
        };
        Client.Connected += async () => { await Console.Out.WriteLineAsync("Gateway connected."); };

        Client.Ready += async () =>
        {
            await CreateGlobalCommand();
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

    private async Task CreateGlobalCommand()
    {
        var command = new SlashCommandBuilder()
            .WithName("gptcli")
            .WithDescription("GPT-CLI commands")
            .AddOptions(new()
            {
                Name = "help",
                Description = "Show help",
                Type = ApplicationCommandOptionType.SubCommand,
            }, new()
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
                        .WithType(ApplicationCommandOptionType.String).AddChoice("gpt-5.2", "gpt-5.2"),
                    new SlashCommandOptionBuilder().WithName("infobot").WithDescription("Enable or disable infobot learning")
                        .WithType(ApplicationCommandOptionType.Boolean)
                }
            }, new()
            {
                Name = "infobot",
                Description = "Infobot commands",
                Type = ApplicationCommandOptionType.SubCommandGroup,
                Options = new()
                {
                    new SlashCommandOptionBuilder().WithName("help").WithDescription("Show infobot help")
                        .WithType(ApplicationCommandOptionType.SubCommand),
                    new SlashCommandOptionBuilder().WithName("set").WithDescription("Set a factoid")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("term")
                            .WithDescription("The factoid term")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                        .AddOption(new SlashCommandOptionBuilder().WithName("text")
                            .WithDescription("The factoid text")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)),
                    new SlashCommandOptionBuilder().WithName("get").WithDescription("Get a factoid")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("term")
                            .WithDescription("The factoid term")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)),
                    new SlashCommandOptionBuilder().WithName("delete").WithDescription("Delete a factoid")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("term")
                            .WithDescription("The factoid term")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)),
                    new SlashCommandOptionBuilder().WithName("list").WithDescription("List factoids")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                    ,
                    new SlashCommandOptionBuilder().WithName("personality").WithDescription("Set the infobot personality prompt")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("prompt")
                            .WithDescription("The personality prompt")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                }
            });

        DiscordRestClient restClient = Client.Rest;

        var builtCommand = command.Build();

        if (DefaultParameters.DiscordGuildId.HasValue)
        {
            var guild = await restClient.GetGuildAsync(DefaultParameters.DiscordGuildId.Value);
            if (guild == null)
            {
                throw new Exception($"Guild {DefaultParameters.DiscordGuildId.Value} not found.");
            }

            await Console.Out.WriteLineAsync($"Overwriting guild commands for {DefaultParameters.DiscordGuildId.Value}...");
            // Ensure updates always replace existing guild commands.
            await guild.BulkOverwriteApplicationCommandsAsync(new[] { builtCommand });
            await DumpGuildCommands(guild);
            return;
        }

        await Console.Out.WriteLineAsync("Overwriting global commands...");
        // Ensure updates always replace existing global commands.
        await ((IDiscordClient)restClient).BulkOverwriteGlobalApplicationCommand(new[] { builtCommand });
        await DumpGlobalCommands((IDiscordClient)restClient);
    }

    private static async Task DumpGuildCommands(IGuild guild)
    {
        var commands = await guild.GetApplicationCommandsAsync();
        await Console.Out.WriteLineAsync($"Guild commands: {string.Join(", ", commands.Select(c => c.Name))}");
        foreach (var command in commands)
        {
            if (string.Equals(command.Name, "gptcli", StringComparison.OrdinalIgnoreCase))
            {
                var optionNames = command.Options?.Select(o => o.Name) ?? Enumerable.Empty<string>();
                await Console.Out.WriteLineAsync($"gptcli options: {string.Join(", ", optionNames)}");
            }
        }
    }

    private static async Task DumpGlobalCommands(IDiscordClient client)
    {
        var commands = await client.GetGlobalApplicationCommandsAsync();
        await Console.Out.WriteLineAsync($"Global commands: {string.Join(", ", commands.Select(c => c.Name))}");
        foreach (var command in commands)
        {
            if (string.Equals(command.Name, "gptcli", StringComparison.OrdinalIgnoreCase))
            {
                var optionNames = command.Options?.Select(o => o.Name) ?? Enumerable.Empty<string>();
                await Console.Out.WriteLineAsync($"gptcli options: {string.Join(", ", optionNames)}");
            }
        }
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
            case "🗑️":
            {
                if (!_factoidResponseMap.TryGetValue(message.Id, out var metadata) || metadata.ChannelId != channel.Id)
                {
                    return;
                }

                var channelState = ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
                var removed = RemoveFactoidByTerm(channelState, metadata.Term);

                await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                await message.ReplyAsync(removed
                    ? $"Factoid removed: {metadata.Term}."
                    : $"No factoid found for {metadata.Term}.");
                await SaveCachedChannelState(channel.Id);
                break;
            }
            case "🛑":
            {
                if (!_factoidResponseMap.TryGetValue(message.Id, out var metadata) || metadata.ChannelId != channel.Id)
                {
                    return;
                }

                var channelState = ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
                if (channelState.Options.LearningEnabled)
                {
                    channelState.Options.LearningEnabled = false;
                    await SaveCachedChannelState(channel.Id);
                }

                await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                await message.ReplyAsync("Infobot disabled for this channel.");
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
            channelState.Options ??= new ChannelOptions();
            channelState.Options.LearningPersonalityPrompt ??= DefaultParameters.LearningPersonalityPrompt;
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
        var channel = ChannelBots.GetOrAdd(message.Channel.Id, _ => InitializeChannel(message.Channel));
        var isTagged = message.MentionedUsers.Any(user => user.Id == Client.CurrentUser.Id);
        var isDirectMessage = message.Channel is IPrivateChannel;
        var shouldRespond = isTagged || isDirectMessage;
       
        if (channel.InstructionChat.ChatBotState.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.InstructionChat.ChatBotState.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.InstructionChat.ChatBotState.PrimeDirectives = PrimeDirective.ToList();
        }

        if (channel.Options.LearningEnabled && !string.IsNullOrWhiteSpace(message.Content))
        {
            await HandleInfobotMessageAsync(channel, message, shouldRespond);
        }

        if (!channel.Options.Enabled)
        {
            return;
        }

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

        // Always record the message for context, but only respond to @mentions.
        channel.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, $"<{message.Author.Username}> {message.Content}"));
        if (!shouldRespond)
        {
            await SaveCachedChannelState(message.Channel.Id);
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

        var factoidContextMessages = await BuildFactoidContextMessagesAsync(channel, message);

        if (!channel.Options.Muted)
        {
            using var typingState = message.Channel.EnterTypingState();
            // Get the response from the bot
            var responses = channel.InstructionChat.GetResponseAsync(factoidContextMessages);
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

    private async Task<List<ChatMessage>> BuildFactoidContextMessagesAsync(ChannelState channel, SocketMessage message)
    {
        var channelFactoids = ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
        if (channelFactoids.Count == 0)
        {
            return new List<ChatMessage>();
        }

        var embedding = await OpenAILogic.GetEmbeddingForPrompt(message.Content);
        var threshold = channel.Options.FactoidSimilarityThreshold;
        var closestChannel = FindMostSimilarFactoids(channelFactoids, embedding, channel.InstructionChat.ChatBotState.Parameters.ClosestMatchLimit, threshold);
        if (closestChannel.Count == 0)
        {
            return new List<ChatMessage>();
        }

        var messages = new List<ChatMessage>
        {
            new(StaticValues.ChatMessageRoles.System,
                "Factoid context for the next message. Use these facts if relevant:")
        };
        foreach (var factoid in closestChannel)
        {
            messages.Add(new ChatMessage(StaticValues.ChatMessageRoles.System,
                $"---factoid---\r\n{factoid.Text}\r\n--end factoid---"));
        }

        return messages;
    }

    private async Task HandleInfobotMessageAsync(ChannelState channel, SocketMessage message, bool isTagged)
    {
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var isListening = channel.Options.LearningEnabled;
        if (!isListening)
        {
            return;
        }

        var content = message.Content.Trim();
        if (isTagged)
        {
            content = content.Replace($"<@{Client.CurrentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
                             .Replace($"<@!{Client.CurrentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
                             .Trim();
        }

        if (TryParseInfobotQuery(content, out var queryTerm))
        {
            await RespondWithFactoidMatchAsync(channel, message, queryTerm);
            return;
        }

        if (TryParseInfobotSet(content, out var term, out var fact))
        {
            if (isListening)
            {
                await SaveFactoidAsync(channel, message, term, fact);
                if (isTagged)
                {
                    await SendFactoidAcknowledgementAsync(channel, message, term, fact, "Learned a new factoid.");
                }
            }
            return;
        }
    }

    private static bool TryParseInfobotSet(string content, out string term, out string fact)
    {
        term = null;
        fact = null;

        var isIndex = content.IndexOf(" is ", StringComparison.OrdinalIgnoreCase);
        var areIndex = content.IndexOf(" are ", StringComparison.OrdinalIgnoreCase);
        var separatorIndex = isIndex >= 0 && areIndex >= 0
            ? Math.Min(isIndex, areIndex)
            : Math.Max(isIndex, areIndex);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var separatorLength = separatorIndex == isIndex ? 4 : 5;
        term = content[..separatorIndex].Trim();
        fact = content[(separatorIndex + separatorLength)..].Trim();
        return !string.IsNullOrWhiteSpace(term) && !string.IsNullOrWhiteSpace(fact);
    }

    private static bool TryParseInfobotQuery(string content, out string term)
    {
        term = null;
        var trimmed = content.Trim().TrimEnd('?').Trim();
        var prefixes = new[]
        {
            "what is ", "what are ", "what was ", "what were ",
            "who is ", "who are ", "who was ", "who were ",
            "when is ", "when are ", "when was ", "when were ",
            "where is ", "where are ", "where was ", "where were ",
            "why is ", "why are ", "why was ", "why were ",
            "how is ", "how are ", "how was ", "how were "
        };
        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                term = trimmed.Substring(prefix.Length).Trim();
                return !string.IsNullOrWhiteSpace(term);
            }
        }

        return false;
    }

    private async Task RespondWithFactoidMatchAsync(ChannelState channel, SocketMessage message, string query)
    {
        var factoids = ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
        if (factoids.Count == 0)
        {
            return;
        }

        var normalizedQuery = NormalizeTerm(query);
        var exactMatch = factoids.FirstOrDefault(f =>
            f.Term != null && string.Equals(f.Term, normalizedQuery, StringComparison.OrdinalIgnoreCase));
        if (exactMatch == null)
        {
            return;
        }

        using var typingState = message.Channel.EnterTypingState();
        var response = await GenerateFactoidResponseAsync(channel, query, exactMatch, message);
        if (string.IsNullOrWhiteSpace(response))
        {
            return;
        }

        IUserMessage responseMessage;
        if (message is IUserMessage userMessage)
        {
            responseMessage = await userMessage.ReplyAsync(response);
        }
        else
        {
            responseMessage = await message.Channel.SendMessageAsync(response);
        }

        if (responseMessage != null && !string.IsNullOrWhiteSpace(exactMatch.Term))
        {
            _factoidResponseMap[responseMessage.Id] = new FactoidResponseMetadata(message.Channel.Id, exactMatch.Term);
            await responseMessage.AddReactionsAsync(new IEmote[]
            {
                new Emoji("🗑️"),
                new Emoji("🛑")
            });
        }
    }

    private async Task<string> GenerateFactoidResponseAsync(ChannelState channel, string query, FactoidEntry factoid, SocketMessage message)
    {
        var mention = factoid.SourceUserId != 0 ? $"<@{factoid.SourceUserId}>" : (factoid.SourceUsername ?? "unknown");
        var messageLink = BuildDiscordMessageLink(channel, factoid.SourceMessageId);
        var sourceLine = messageLink == null
            ? $"Source: {mention} on {factoid.CreatedAt:O}."
            : $"Source: {mention} in {messageLink} on {factoid.CreatedAt:O}.";
        var includeSourcesInstruction = messageLink == null
            ? "Include the mention verbatim."
            : "Include the mention and link verbatim.";
        var term = string.IsNullOrWhiteSpace(factoid.Term) ? query : factoid.Term;
        var prompt = $"Matched factoid\nTerm: {term}\nFact: {factoid.Text}\n{sourceLine}\n" +
                     $"Respond in one short paragraph: lead with an infobot-style sentence that repeats the fact verbatim, " +
                     $"then add a short, personable blurb that explains why it matches the question or provides likely context. {includeSourcesInstruction}";
        var personalityPrompt = channel.Options.LearningPersonalityPrompt ?? DefaultParameters.LearningPersonalityPrompt;
        var systemPrompt = string.IsNullOrWhiteSpace(personalityPrompt)
            ? "You are a concise, personable Discord bot. Answer matched factoids in an infobot-inspired style."
            : $"You are a concise, personable Discord bot. Answer matched factoids in an infobot-inspired style.\n{personalityPrompt}";
        var request = new ChatCompletionCreateRequest
        {
            Model = channel.InstructionChat.ChatBotState.Parameters.Model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System, systemPrompt),
                new(StaticValues.ChatMessageRoles.User, prompt)
            }
        };

        var response = await OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"Factoid response failed: {response.Error?.Message}");
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var missingPieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(mention) && !content.Contains(mention, StringComparison.Ordinal))
        {
            missingPieces.Add(mention);
        }
        if (!string.IsNullOrWhiteSpace(messageLink) && !content.Contains(messageLink, StringComparison.Ordinal))
        {
            missingPieces.Add(messageLink);
        }
        if (!string.IsNullOrWhiteSpace(factoid.Text) && !content.Contains(factoid.Text, StringComparison.Ordinal))
        {
            missingPieces.Add($"Fact: {factoid.Text}");
        }
        if (missingPieces.Count > 0)
        {
            content = $"{content}\n\nSource: {string.Join(" ", missingPieces)}";
        }

        if (!string.IsNullOrWhiteSpace(messageLink))
        {
            var originalMessageLine = $"Original message: {messageLink}";
            if (!content.Contains(originalMessageLine, StringComparison.OrdinalIgnoreCase))
            {
                content = $"{content}\n{originalMessageLine}";
            }
        }

        content = $"{content}\n\nReact with 🛑 to disable infobot here, or 🗑️ to delete this factoid.";

        return content;
    }

    private async Task SendFactoidAcknowledgementAsync(ChannelState channel, SocketMessage message, string term, string fact, string prefix)
    {
        var info = $"{prefix} `{term}` → {fact}";
        if (message is IUserMessage userMessage)
        {
            await userMessage.ReplyAsync(info);
        }
        else
        {
            await message.Channel.SendMessageAsync(info);
        }
    }

    private static string NormalizeTerm(string term)
    {
        return string.IsNullOrWhiteSpace(term)
            ? null
            : term.Trim().TrimEnd('?').ToLowerInvariant();
    }

    private static string BuildDiscordMessageLink(ChannelState channel, ulong messageId)
    {
        if (channel == null || channel.GuildId == 0 || channel.ChannelId == 0 || messageId == 0)
        {
            return null;
        }

        return $"https://discord.com/channels/{channel.GuildId}/{channel.ChannelId}/{messageId}";
    }

    private static List<FactoidEntry> FindMostSimilarFactoids(List<FactoidEntry> factoids, List<double> queryEmbedding, int limit, double threshold)
    {
        var matches = new List<(FactoidEntry Factoid, double Similarity)>(factoids.Count);
        foreach (var factoid in factoids)
        {
            if (factoid.Embedding == null || factoid.Embedding.Count == 0)
            {
                continue;
            }
            var similarity = CosineSimilarity.Calculate(queryEmbedding, factoid.Embedding);
            if (similarity >= threshold)
            {
                matches.Add((factoid, similarity));
            }
        }

        matches.Sort((x, y) => y.Similarity.CompareTo(x.Similarity));
        return matches.Take(limit).Select(x => x.Factoid).ToList();
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
        var channel = Client.GetChannel(channelId);
        if (channel != null)
        {
            return InitializeChannel(channel);
        }

        var fallback = CreateBaseChannelState(channelId);
        fallback.GuildId = 0;
        fallback.GuildName = "unknown";
        fallback.ChannelName = "unknown";
        ChannelBots[channelId] = fallback;
        return fallback;
    }

    private ChannelState InitializeChannel(IChannel channel)
    {
        var channelState = CreateBaseChannelState(channel.Id);

        if (channel is IGuildChannel guildChannel)
        {
            channelState.GuildId = guildChannel.GuildId;
            channelState.ChannelName = guildChannel.Name;
            channelState.GuildName = guildChannel.Guild.Name;
        }
        else if (channel is IDMChannel dmChannel)
        {
            channelState.GuildId = 0;
            channelState.GuildName = "dm";
            channelState.ChannelName = dmChannel.Recipient?.Username ?? "dm";
            channelState.Options.Enabled = true;
        }
        else if (channel is IGroupChannel groupChannel)
        {
            channelState.GuildId = 0;
            channelState.GuildName = "group-dm";
            channelState.ChannelName = string.IsNullOrWhiteSpace(groupChannel.Name) ? "group-dm" : groupChannel.Name;
            channelState.Options.Enabled = true;
        }
        else if (channel is IPrivateChannel)
        {
            channelState.GuildId = 0;
            channelState.GuildName = "private";
            channelState.ChannelName = "private";
            channelState.Options.Enabled = true;
        }
        else
        {
            throw new Exception($"Channel {channel.Id} not found");
        }

        ChannelBots[channel.Id] = channelState;
        return channelState;
    }

    private ChannelState CreateBaseChannelState(ulong channelId)
    {
        return new ChannelState
        {
            ChannelId = channelId,
            InstructionChat = new(OpenAILogic, DefaultParameters.Adapt<GptOptions>())
            {
                ChatBotState = new()
                {
                    PrimeDirectives = PrimeDirective.ToList(),
                    Parameters = DefaultParameters.Adapt<GptOptions>()
                }
            },
            Options = new()
            {
                LearningPersonalityPrompt = DefaultParameters.LearningPersonalityPrompt
            }
        };
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

        var channelState = ChannelBots.GetOrAdd(message.Channel.Id, _ => InitializeChannel(message.Channel));
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

    private async Task LoadFactoids()
    {
        Directory.CreateDirectory("channels");
        var factoidFiles = Directory.GetFiles("channels", "facts.json", SearchOption.AllDirectories).ToList();
        foreach (var file in factoidFiles)
        {
            if (!TryGetChannelIdFromFactoidPath(file, out var channelId))
            {
                continue;
            }

            await using var fileStream = File.OpenRead(file);
            var factoids = await JsonSerializer.DeserializeAsync<List<FactoidEntry>>(fileStream) ?? new List<FactoidEntry>();
            ChannelFactoids[channelId] = factoids;
        }
    }

    private static bool TryGetChannelIdFromFactoidPath(string file, out ulong channelId)
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

    private static string GetChannelFactoidFile(ChannelState channelState)
    {
        return Path.Combine(GetChannelDirectory(channelState), "facts.json");
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
        var channelState = ChannelBots.GetOrAdd(command.Channel.Id, _ => InitializeChannel(command.Channel));
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
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channel = command.Channel;
        if (!ChannelBots.TryGetValue(channel.Id, out var chatBot))
        {
            chatBot = InitializeChannel(channel);
        }

        var responses = new List<string>();
        var options = command.Data.Options;
        if (options == null || options.Count == 0)
        {
            responses.Add("No options provided.");
        }
        else
        {
            foreach (var option in options)
            {
                var subOption = option.Options?.FirstOrDefault();
                switch (option.Name)
                {
                    case "help":
                    {
                        var help = string.Join("\n", new[]
                        {
                            "**GPT-CLI help**",
                            "_Quick guide to the slash commands and reactions_",
                            "",
                            "**Core commands**",
                            "• `/gptcli help` — show this message",
                            "• `/gptcli clear messages|instructions|all`",
                            "",
                            "**Bot settings**",
                            "• `/gptcli set enabled true|false`",
                            "• `/gptcli set mute true|false`",
                            "• `/gptcli set model gpt-5.2`",
                            "• `/gptcli set max-tokens <number>`",
                            "• `/gptcli set max-chat-history-length <number>`",
                            "• `/gptcli set response-mode All|Matches`",
                            "• `/gptcli set embed-mode Explicit|All`",
                            "• `/gptcli set infobot true|false`",
                            "",
                            "**Infobot**",
                            "• `/gptcli infobot help` — how it learns & matches",
                            "• `/gptcli infobot set term text`",
                            "• `/gptcli infobot get term`",
                            "• `/gptcli infobot delete term`",
                            "• `/gptcli infobot list`",
                            "• `/gptcli infobot personality prompt:\"...\"`",
                            "",
                            "**Reactions**",
                            "• 📌 add message as instruction",
                            "• 💾 save message as embed",
                            "• 🔄 replay a user message as a prompt",
                            "• 🗑️ remove matched factoid term (on infobot reply)",
                            "• 🛑 disable infobot for this channel (on infobot reply)"
                        });
                        responses.Add(help);
                        break;
                    }
                    case "clear":
                        if (subOption == null)
                        {
                            responses.Add("Specify messages, instructions, or all.");
                            break;
                        }

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
                            default:
                                responses.Add($"Unknown clear option: {subOption.Name}");
                                break;
                        }

                        var clearName = string.IsNullOrWhiteSpace(subOption.Name) ? "Option" : subOption.Name;
                        responses.Add($"{char.ToUpper(clearName[0])}{clearName.Substring(1)} cleared.");
                        break;
                    case "instruction":
                        var instruction = option.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(instruction))
                        {
                            chatBot.InstructionChat.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));
                            responses.Add("Instruction received!");
                        }
                        break;
                    case "instructions":
                        responses.Add($"Instructions: {chatBot.InstructionChat.InstructionStr}");
                        break;
                    case "set":
                        if (subOption == null)
                        {
                            responses.Add("Specify a setting to change.");
                            break;
                        }

                        switch (subOption.Name)
                        {
                            case "enabled":
                                if (subOption.Value is bool enabled)
                                {
                                    chatBot.Options.Enabled = enabled;
                                    responses.Add($"InstructionChat bot {(enabled ? "enabled" : "disabled")}.");
                                }
                                break;
                            case "mute":
                                if (subOption.Value is bool muted)
                                {
                                    chatBot.Options.Muted = muted;
                                    responses.Add($"InstructionChat bot {(muted ? "muted" : "un-muted")}.");
                                }
                                break;
                            case "max-tokens":
                                if (subOption.Value is long maxTokens)
                                {
                                    chatBot.InstructionChat.ChatBotState.Parameters.MaxTokens = (int?)maxTokens;
                                    responses.Add($"Max tokens set to {maxTokens}.");
                                }
                                break;
                            case "max-chat-history-length":
                                if (subOption.Value is long maxChatHistoryLength)
                                {
                                    chatBot.InstructionChat.ChatBotState.Parameters.MaxChatHistoryLength = (uint)maxChatHistoryLength;
                                    responses.Add($"Max chat history length set to {maxChatHistoryLength}.");
                                }
                                break;
                            case "model":
                                if (subOption.Value is string model)
                                {
                                    chatBot.InstructionChat.ChatBotState.Parameters.Model = model;
                                    responses.Add($"Model set to {model}.");
                                }
                                break;
                            case "embed-mode":
                                if (subOption.Value is string embedMode &&
                                    Enum.TryParse<InstructionChatBot.EmbedMode>(embedMode, true, out var parsedEmbedMode))
                                {
                                    chatBot.InstructionChat.ChatBotState.EmbedMode = parsedEmbedMode;
                                    responses.Add($"Embed mode set to {embedMode}.");
                                }
                                break;
                            case "response-mode":
                                if (subOption.Value is string responseMode &&
                                    Enum.TryParse<InstructionChatBot.ResponseMode>(responseMode, true, out var parsedResponseMode))
                                {
                                    chatBot.InstructionChat.ChatBotState.ResponseMode = parsedResponseMode;
                                    responses.Add($"Response mode set to {responseMode}.");
                                }
                                break;
                            case "infobot":
                                if (subOption.Value is bool learningEnabled)
                                {
                                    chatBot.Options.LearningEnabled = learningEnabled;
                                    responses.Add($"Infobot {(learningEnabled ? "enabled" : "disabled")}.");
                                }
                                break;
                            default:
                                responses.Add($"Unknown setting: {subOption.Name}");
                                break;
                        }
                        break;
                    case "infobot":
                        if (subOption == null)
                        {
                            responses.Add("Specify an infobot command.");
                            break;
                        }

                        switch (subOption.Name)
                        {
                            case "help":
                            {
                                var help = string.Join("\n", new[]
                                {
                                    "**Infobot help**",
                                    "_Teach it facts, then ask exact-match questions._",
                                    "",
                                    "**Learn facts (infobot on)**",
                                    "• `<term> is <fact>`",
                                    "• `<terms> are <fact>`",
                                    "",
                                    "**Ask questions (exact match only)**",
                                    "• `what|who|when|where|why|how is|are|was|were <term>?`",
                                    "",
                                    "**Enable / disable**",
                                    "• `/gptcli set infobot true|false`",
                                    "",
                                    "**Factoid commands**",
                                    "• `/gptcli infobot set term text`",
                                    "• `/gptcli infobot get term`",
                                    "• `/gptcli infobot delete term`",
                                    "• `/gptcli infobot list`",
                                    "",
                                    "**Personality**",
                                    "• `/gptcli infobot personality prompt:\"...\"`",
                                    "",
                                    "**Reactions on factoid matches**",
                                    "• 🗑️ remove the matched factoid term",
                                    "• 🛑 disable infobot for this channel"
                                });
                                responses.Add(help);
                                break;
                            }
                            case "set":
                            {
                                var term = subOption.Options?.FirstOrDefault(o => o.Name == "term")?.Value?.ToString();
                                var text = subOption.Options?.FirstOrDefault(o => o.Name == "text")?.Value?.ToString();
                                if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(text))
                                {
                                    responses.Add("Provide term and text.");
                                    break;
                                }

                                var channelState = ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
                                await SaveFactoidAsync(channelState, command.User, channel, term, text);
                                responses.Add($"Factoid set: {term}.");
                                break;
                            }
                            case "get":
                            {
                                var term = subOption.Options?.FirstOrDefault(o => o.Name == "term")?.Value?.ToString();
                                if (string.IsNullOrWhiteSpace(term))
                                {
                                    responses.Add("Provide term.");
                                    break;
                                }

                                var entry = FindExactTermFactoid(channel.Id, term);
                                if (entry == null)
                                {
                                    responses.Add($"No factoid found for {term}.");
                                }
                                else
                                {
                                    responses.Add($"{term} -> {entry.Text}");
                                }
                                break;
                            }
                            case "delete":
                            {
                                var term = subOption.Options?.FirstOrDefault(o => o.Name == "term")?.Value?.ToString();
                                if (string.IsNullOrWhiteSpace(term))
                                {
                                    responses.Add("Provide term.");
                                    break;
                                }

                                var channelState = ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
                                if (RemoveFactoidByTerm(channelState, term))
                                {
                                    responses.Add($"Factoid deleted: {term}.");
                                }
                                else
                                {
                                    responses.Add($"No factoid found for {term}.");
                                }
                                break;
                            }
                            case "list":
                            {
                                if (!ChannelFactoids.TryGetValue(channel.Id, out var factoids) || factoids.Count == 0)
                                {
                                    responses.Add("No factoids stored.");
                                    break;
                                }

                                var indexed = factoids
                                    .Select((factoid, index) => new { factoid, index })
                                    .Where(x => !string.IsNullOrWhiteSpace(x.factoid.Term))
                                    .ToList();
                                if (indexed.Count == 0)
                                {
                                    responses.Add("No factoids stored.");
                                    break;
                                }

                                var lines = indexed
                                    .GroupBy(x => x.factoid.Term, StringComparer.OrdinalIgnoreCase)
                                    .Select(group =>
                                    {
                                        var latestCreatedAt = group.Max(x => x.factoid.CreatedAt);
                                        var text = string.Concat(group
                                            .Where(x => x.factoid.CreatedAt == latestCreatedAt)
                                            .OrderBy(x => x.index)
                                            .Select(x => x.factoid.Text)
                                            .Where(t => !string.IsNullOrWhiteSpace(t)));
                                        return new { Term = group.Key, Text = text, CreatedAt = latestCreatedAt };
                                    })
                                    .OrderBy(x => x.Term, StringComparer.OrdinalIgnoreCase)
                                    .Select(x => $"{x.Term} -> {x.Text}")
                                    .ToList();

                                responses.Add(lines.Count == 0
                                    ? "No factoids stored."
                                    : $"Factoids:\n{string.Join("\n", lines)}");
                                break;
                            }
                            case "personality":
                            {
                                var prompt = subOption.Options?.FirstOrDefault(o => o.Name == "prompt")?.Value?.ToString();
                                if (string.IsNullOrWhiteSpace(prompt))
                                {
                                    responses.Add("Provide a personality prompt.");
                                    break;
                                }

                                var trimmed = prompt.Trim();
                                chatBot.Options.LearningPersonalityPrompt = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
                                responses.Add(string.IsNullOrWhiteSpace(trimmed)
                                    ? "Infobot personality prompt cleared."
                                    : "Infobot personality prompt updated.");
                                break;
                            }
                            default:
                                responses.Add($"Unknown infobot command: {subOption.Name}");
                                break;
                        }
                        break;
                    default:
                        responses.Add($"Unknown option: {option.Name}");
                        break;
                }
            }
        }

        if (responses.Count == 0)
        {
            responses.Add("No changes made.");
        }

        if (command.HasResponded)
        {
            await command.FollowupAsync(string.Join("\n", responses), ephemeral: true);
        }
        else
        {
            await command.RespondAsync(string.Join("\n", responses), ephemeral: true);
        }

        await SaveCachedChannelState(channel.Id);
    }

    private FactoidEntry FindExactTermFactoid(ulong channelId, string term)
    {
        if (!ChannelFactoids.TryGetValue(channelId, out var factoids))
        {
            return null;
        }

        var normalized = NormalizeTerm(term);
        return factoids.FirstOrDefault(f => f.Term != null &&
                                            string.Equals(f.Term, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool RemoveFactoidByTerm(ChannelState channel, string term)
    {
        if (!ChannelFactoids.TryGetValue(channel.ChannelId, out var factoids))
        {
            return false;
        }

        var normalized = NormalizeTerm(term);
        var removed = factoids.RemoveAll(f => f.Term != null &&
                                              string.Equals(f.Term, normalized, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        _ = SaveFactoidsAsync(channel, factoids);
        return true;
    }

    private async Task SaveFactoidAsync(ChannelState channel, SocketMessage message, string term, string text)
    {
        var normalizedTerm = NormalizeTerm(term);
        var entry = new FactoidEntry
        {
            Term = normalizedTerm,
            Text = text.Trim(),
            SourceMessageId = message.Id,
            SourceUserId = message.Author.Id,
            SourceUsername = message.Author.Username,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var documents = await Document.ChunkToDocumentsAsync(entry.Text, channel.InstructionChat.ChatBotState.Parameters.ChunkSize);
        if (documents.Count == 0)
        {
            return;
        }

        var embeddings = await OpenAILogic.CreateEmbeddings(documents);
        if (!embeddings.Successful)
        {
            return;
        }

        foreach (var doc in documents)
        {
            if (doc.Embedding == null)
            {
                continue;
            }

            var chunkEntry = new FactoidEntry
            {
                Term = entry.Term,
                Text = doc.Text,
                Embedding = doc.Embedding,
                SourceMessageId = entry.SourceMessageId,
                SourceUserId = entry.SourceUserId,
                SourceUsername = entry.SourceUsername,
                CreatedAt = entry.CreatedAt
            };

            var list = ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
            list.Add(chunkEntry);
        }

        if (ChannelFactoids.TryGetValue(message.Channel.Id, out var channelList))
        {
            await SaveFactoidsAsync(channel, channelList);
        }
    }

    private async Task SaveFactoidAsync(ChannelState channel, IUser user, IMessageChannel messageChannel, string term, string text)
    {
        var normalizedTerm = NormalizeTerm(term);
        var entry = new FactoidEntry
        {
            Term = normalizedTerm,
            Text = text.Trim(),
            SourceMessageId = 0,
            SourceUserId = user.Id,
            SourceUsername = user.Username,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var documents = await Document.ChunkToDocumentsAsync(entry.Text, channel.InstructionChat.ChatBotState.Parameters.ChunkSize);
        if (documents.Count == 0)
        {
            return;
        }

        var embeddings = await OpenAILogic.CreateEmbeddings(documents);
        if (!embeddings.Successful)
        {
            return;
        }

        foreach (var doc in documents)
        {
            if (doc.Embedding == null)
            {
                continue;
            }

            var chunkEntry = new FactoidEntry
            {
                Term = entry.Term,
                Text = doc.Text,
                Embedding = doc.Embedding,
                SourceMessageId = entry.SourceMessageId,
                SourceUserId = entry.SourceUserId,
                SourceUsername = entry.SourceUsername,
                CreatedAt = entry.CreatedAt
            };

            var list = ChannelFactoids.GetOrAdd(messageChannel.Id, _ => new List<FactoidEntry>());
            list.Add(chunkEntry);
        }

        if (ChannelFactoids.TryGetValue(messageChannel.Id, out var channelList))
        {
            await SaveFactoidsAsync(channel, channelList);
        }
    }

    private async Task SaveFactoidsAsync(ChannelState channel, List<FactoidEntry> entries)
    {
        var channelDirectory = GetChannelDirectory(channel);
        Directory.CreateDirectory(channelDirectory);
        var filePath = GetChannelFactoidFile(channel);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, entries, new JsonSerializerOptions { WriteIndented = true });
    }
}
