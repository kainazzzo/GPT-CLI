using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using GPT.CLI.Chat.Discord.Modules;
using Mapster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord;

public class InstructionGPT : DiscordBotBase, IHostedService, IDiscordModuleHost
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<ulong, HashSet<string>> _imageResponseMap = new();
    private DiscordModulePipeline _modulePipeline;
    private DiscordModuleContext _moduleContext;
    private CancellationToken _shutdownToken;

    public InstructionGPT(
        DiscordSocketClient client,
        IConfiguration configuration,
        OpenAILogic openAILogic,
        GptOptions defaultParameters,
        IServiceProvider services) : base(client, configuration, openAILogic, defaultParameters)
    {
        _services = services;
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

        [JsonPropertyName("casino-balances")]
        public Dictionary<ulong, decimal> CasinoBalances { get; set; }
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

        [JsonPropertyName("casino-enabled")]
        public bool CasinoEnabled { get; set; }
    }






    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownToken = cancellationToken;

        var modulesPath = Configuration["Discord:ModulesPath"] ?? "modules";
        if (!Path.IsPathRooted(modulesPath))
        {
            modulesPath = Path.Combine(Directory.GetCurrentDirectory(), modulesPath);
        }
        Directory.CreateDirectory(modulesPath);

        Action<string> moduleLog = message => Console.Out.WriteLine(message);
        _moduleContext = new DiscordModuleContext(
            Client,
            Configuration,
            OpenAILogic,
            DefaultParameters,
            ChannelBots,
            Documents,
            ChannelFactoids,
            ChannelFactoidMatches,
            ChannelFactoidMatchStats,
            ChannelGuildIds,
            this);
        _modulePipeline = DiscordModulePipeline.Create(_services, _moduleContext, modulesPath, moduleLog);
        await _modulePipeline.InitializeAsync(cancellationToken);

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

            if (_modulePipeline != null)
            {
                await _modulePipeline.OnReadyAsync(_shutdownToken);
            }
        };


        // This is required for slash commands to work
        Client.InteractionCreated += HandleInteractionAsync;
        



        Client.MessageUpdated += async (oldMessage, newMessage, channel) =>
        {
            if (_modulePipeline != null)
            {
                await _modulePipeline.OnMessageUpdatedAsync(oldMessage, newMessage, channel, _shutdownToken);
            }

            if (newMessage.Content != null && oldMessage.Value?.Content != newMessage.Content)
            {
                await HandleMessageReceivedAsync(newMessage);
            }
        };

        // Handle emoji reactions.
        Client.ReactionAdded += HandleReactionAsync;




        Client.MessageCommandExecuted += async (command) =>
        {
            if (_modulePipeline != null)
            {
                await _modulePipeline.OnMessageCommandExecutedAsync(command, _shutdownToken);
            }

            await Console.Out.WriteLineAsync($"Command {command.CommandName} executed with result {command.Data.Message.Content}");
        };
    }

    private async Task CreateGlobalCommand()
    {
        var options = new List<SlashCommandOptionBuilder>
        {
            new()
            {
                Name = "help",
                Description = "Show help",
                Type = ApplicationCommandOptionType.SubCommand,
            },
            new()
            {
                Name = "instruction",
                Description = "Instruction commands",
                Type = ApplicationCommandOptionType.SubCommandGroup,
                Options = new()
                {
                    new SlashCommandOptionBuilder().WithName("add")
                        .WithDescription("Add a system instruction")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("text")
                            .WithDescription("Instruction text")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)),
                    new SlashCommandOptionBuilder().WithName("list")
                        .WithDescription("List current instructions")
                        .WithType(ApplicationCommandOptionType.SubCommand),
                    new SlashCommandOptionBuilder().WithName("get")
                        .WithDescription("Get an instruction by index")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("index")
                            .WithDescription("1-based instruction index")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(true)
                            .WithMinValue(1)),
                    new SlashCommandOptionBuilder().WithName("delete")
                        .WithDescription("Delete an instruction by index")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption(new SlashCommandOptionBuilder().WithName("index")
                            .WithDescription("1-based instruction index")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(true)
                            .WithMinValue(1)),
                    new SlashCommandOptionBuilder().WithName("clear")
                        .WithDescription("Clear all instructions")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                }
            },
            new()
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
            },
            new()
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
                }
            }
        };

        if (_modulePipeline != null)
        {
            var contributions = _modulePipeline.GetSlashCommandContributions();
            if (contributions.Count > 0)
            {
                var topLevel = options.ToDictionary(option => option.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var contribution in contributions)
                {
                    if (contribution?.Option == null || string.IsNullOrWhiteSpace(contribution.Option.Name))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(contribution.TargetOption))
                    {
                        if (topLevel.ContainsKey(contribution.Option.Name))
                        {
                            await Console.Out.WriteLineAsync(
                                $"Slash command option conflict: '{contribution.Option.Name}' already exists. Skipping module option.");
                            continue;
                        }

                        options.Add(contribution.Option);
                        topLevel[contribution.Option.Name] = contribution.Option;
                        continue;
                    }

                    if (!topLevel.TryGetValue(contribution.TargetOption, out var target))
                    {
                        await Console.Out.WriteLineAsync(
                            $"Slash command option target '{contribution.TargetOption}' not found. Skipping module option '{contribution.Option.Name}'.");
                        continue;
                    }

                    var existing = target.Options?.Any(opt => string.Equals(opt.Name, contribution.Option.Name, StringComparison.OrdinalIgnoreCase)) == true;
                    if (existing)
                    {
                        await Console.Out.WriteLineAsync(
                            $"Slash command option conflict: '{contribution.TargetOption} {contribution.Option.Name}' already exists. Skipping module option.");
                        continue;
                    }

                    target.AddOption(contribution.Option);
                }
            }
        }

        var command = new SlashCommandBuilder()
            .WithName("gptcli")
            .WithDescription("GPT-CLI commands")
            .AddOptions(options.ToArray());

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

        if (_modulePipeline != null)
        {
            await _modulePipeline.OnReactionAddedAsync(userMessage, messageChannel, reaction, _shutdownToken);
        }
        try
        {
            var channel = await messageChannel.GetOrDownloadAsync();
            if (channel == null)
            {
                return;
            }

            IUserMessage message = null;
            try
            {
                message = await userMessage.GetOrDownloadAsync();
            }
            catch (HttpException ex)
            {
                await Console.Out.WriteLineAsync($"Failed to download message {reaction.MessageId}: {ex.Reason}");
            }

            var messageId = message?.Id ?? reaction.MessageId;

            switch (reaction.Emote.Name)
            {
                case "📌":
                {
                    if (message == null)
                    {
                        return;
                    }

                    var channelState = ChannelBots[channel.Id];
                    if (channelState.Options.Enabled)
                    {
                        var chatBot = channelState.InstructionChat;
                        chatBot.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.System, message.Content));


                        using var typingState = channel.EnterTypingState();
                        await TryRemoveReactionAsync(message, reaction.Emote, reaction.UserId);
                        await TryReplyAsync(message, channel, messageId, "Instruction added.");
                        await SaveCachedChannelState(channel.Id);
                    }

                    break;
                }
                case "🔄":
                {
                    if (message == null)
                    {
                        return;
                    }

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
                    if (message == null)
                    {
                        return;
                    }

                    if (message.Author.Id == Client.CurrentUser.Id)
                    {
                        return;
                    }

                    await SaveEmbed(message);
                    break;
                }
                case "🧹":
                {
                    if (message == null)
                    {
                        return;
                    }

                    var channelState = ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
                    if (!IsChannelGuildMatch(channelState, channel, "image-embed-delete"))
                    {
                        await TryReplyAsync(message, channel, messageId, "Guild mismatch detected for image embeds. Refusing delete.");
                        return;
                    }

                    var result = await DeleteImageEmbedsForMessageAsync(channelState, message);
                    await TryRemoveReactionAsync(message, reaction.Emote, reaction.UserId);
                    var reply = result.TotalDeleted > 0
                        ? $"<@{reaction.UserId}> Deleted {result.TotalDeleted} image embed(s) and {result.FilesDeleted} file(s)."
                        : $"<@{reaction.UserId}> No image embeds found to delete for that message.";
                    await TryReplyAsync(message, channel, messageId, reply);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"Reaction handler failed: {ex.Message}");
        }
    }

    internal static async Task TryReplyAsync(IUserMessage message, IMessageChannel channel, ulong messageId, string reply)
    {
        try
        {
            if (message != null)
            {
                await message.ReplyAsync(reply);
            }
            else
            {
                await channel.SendMessageAsync(reply, messageReference: new MessageReference(messageId));
            }
        }
        catch (HttpException ex)
        {
            await Console.Out.WriteLineAsync($"Failed to reply on {messageId}: {ex.Reason}");
        }
    }

    internal static async Task TryRemoveReactionAsync(IUserMessage message, IEmote emote, ulong userId)
    {
        try
        {
            await message.RemoveReactionAsync(emote, userId);
        }
        catch (HttpException ex)
        {
            await Console.Out.WriteLineAsync($"Failed to remove reaction {emote.Name}: {ex.Reason}");
        }
    }

    internal static async Task TryRemoveAllReactionsForEmoteAsync(IUserMessage message, IEmote emote)
    {
        try
        {
            await message.RemoveAllReactionsForEmoteAsync(emote);
        }
        catch (HttpException ex)
        {
            await Console.Out.WriteLineAsync($"Failed to clear reactions for {emote.Name}: {ex.Reason}");
        }
    }

    private record ImageEmbedDeleteResult(int TotalDeleted, int FilesDeleted);

    private async Task<ImageEmbedDeleteResult> DeleteImageEmbedsForMessageAsync(ChannelState channelState, IUserMessage message)
    {
        if (channelState == null || message == null)
        {
            return new ImageEmbedDeleteResult(0, 0);
        }

        var channelDocs = Documents.GetOrAdd(channelState.ChannelId, _ => new List<Document>());
        var embedDirectory = Path.Combine(GetChannelDirectory(channelState), "embeds");
        var embedFilesDirectory = Path.Combine(embedDirectory, "files");

        var embedFilesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedFilesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docsToRemove = new List<Document>();

        if (_imageResponseMap.TryRemove(message.Id, out var mappedPaths))
        {
            lock (mappedPaths)
            {
                foreach (var path in mappedPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    storedFilesToDelete.Add(path);
                    docsToRemove.AddRange(channelDocs.Where(doc =>
                        doc.IsImage && HasStoredFile(doc) &&
                        string.Equals(ResolveStoredFilePath(doc.StoredFilePath), path, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
            {
                var contentType = attachment.ContentType;
                if (!IsImageAttachment(attachment, contentType))
                {
                    continue;
                }

                var baseName = $"{message.Id}.{attachment.Id}";
                if (channelState.GuildId != 0)
                {
                    embedFilesToDelete.Add(Path.Combine(embedDirectory, BuildTokenizedFileName(baseName, channelState.GuildId, "embed.json")));
                }

                embedFilesToDelete.Add(Path.Combine(embedDirectory, $"{baseName}.embed.json"));

                if (Directory.Exists(embedFilesDirectory))
                {
                    foreach (var storedFile in Directory.GetFiles(embedFilesDirectory, $"{baseName}.*"))
                    {
                        storedFilesToDelete.Add(storedFile);
                    }
                }

                docsToRemove.AddRange(channelDocs.Where(doc =>
                    doc.IsImage &&
                    (doc.SourceMessageId == message.Id ||
                     string.Equals(doc.SourceFileName, attachment.Filename, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(doc.StoredFilePath) &&
                      Path.GetFileName(doc.StoredFilePath).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)))));
            }
        }
        else
        {
            var referenceId = message.Reference?.MessageId;
            if (referenceId.HasValue && referenceId.Value.IsSpecified)
            {
                var referencedId = referenceId.Value.Value;
                docsToRemove.AddRange(channelDocs.Where(doc => doc.IsImage && doc.SourceMessageId == referencedId));
            }
        }

        foreach (var doc in docsToRemove.Where(HasStoredFile))
        {
            storedFilesToDelete.Add(ResolveStoredFilePath(doc.StoredFilePath));
            var embedPath = ResolveEmbedPathForImage(channelState, doc);
            if (!string.IsNullOrWhiteSpace(embedPath))
            {
                embedFilesToDelete.Add(embedPath);
            }
        }

        foreach (var path in embedFilesToDelete)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    await Console.Out.WriteLineAsync($"Failed to delete embed file {path}: {ex.Message}");
                }
            }
        }

        foreach (var path in storedFilesToDelete)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    await Console.Out.WriteLineAsync($"Failed to delete stored file {path}: {ex.Message}");
                }
            }
        }

        if (docsToRemove.Count > 0)
        {
            var storedPathSet = new HashSet<string>(
                docsToRemove.Where(HasStoredFile).Select(d => ResolveStoredFilePath(d.StoredFilePath)),
                StringComparer.OrdinalIgnoreCase);
            channelDocs.RemoveAll(doc =>
                doc.IsImage && (storedPathSet.Contains(ResolveStoredFilePath(doc.StoredFilePath)) ||
                                doc.SourceMessageId == message.Id));
        }

        return new ImageEmbedDeleteResult(embedFilesToDelete.Count, storedFilesToDelete.Count);
    }


    private async Task LoadState()
    {
        await Console.Out.WriteLineAsync("Loading state...");
        Directory.CreateDirectory("channels");
        var files = Directory.GetFiles("channels", "*.state.json", SearchOption.AllDirectories).ToList();
        var channelsWithToken = new HashSet<ulong>();
        foreach (var file in files)
        {
            if (TryGetChannelIdFromStatePath(file, out var tokenChannelId) &&
                TryGetGuildIdTokenFromFileName(file, out _))
            {
                channelsWithToken.Add(tokenChannelId);
            }
        }

        foreach (var file in files)
        {
            if (!TryGetChannelIdFromStatePath(file, out var channelId))
            {
                continue;
            }

            var hasToken = TryGetGuildIdTokenFromFileName(file, out var tokenGuildId);
            if (!hasToken && channelsWithToken.Contains(channelId))
            {
                continue;
            }

            var validation = await ValidateGuildTokenForFileAsync(channelId, file, isEmbed: false, "state");
            if (!validation.ShouldLoad)
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

            if (validation.GuildId != 0 && channelState.GuildId != 0 && channelState.GuildId != validation.GuildId)
            {
                await Console.Out.WriteLineAsync(
                    $"Skipping state for channel {channelId}: guild mismatch (state {channelState.GuildId}, token {validation.GuildId}).");
                ChannelBots.TryRemove(channelId, out _);
                continue;
            }

            if (validation.GuildId != 0 && channelState.GuildId == 0)
            {
                channelState.GuildId = validation.GuildId;
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

            if (!hasToken)
            {
                var resolvedGuildId = channelState.GuildId;
                if (resolvedGuildId == 0 && TryGetGuildIdFromChannelPath(file, out var pathGuildId))
                {
                    resolvedGuildId = pathGuildId;
                }
                else if (resolvedGuildId == 0)
                {
                    resolvedGuildId = tokenGuildId;
                }

                await ResaveLegacyJsonAsync(file, ".state.json", resolvedGuildId, "state.json", channelState,
                    new JsonSerializerOptions { WriteIndented = true }, deleteLegacyOnSuccess: true);
            }
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

        if (channelState.GuildId == 0 && ChannelGuildIds.TryGetValue(channelId, out var storedGuildId))
        {
            channelState.GuildId = storedGuildId;
        }

        var channelDirectory = GetChannelDirectory(channelState);
        Directory.CreateDirectory(channelDirectory);
        var stateFileName = BuildTokenizedFileName(GetChannelFileName(channelState), channelState.GuildId, "state.json");

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
        if (message.Channel is IGuildChannel guildChannel)
        {
            EnsureChannelStateMetadata(channel, guildChannel);
        }

        if (_modulePipeline != null)
        {
            await _modulePipeline.OnMessageReceivedAsync(message, _shutdownToken);
        }

        var guildMatch = IsChannelGuildMatch(channel, message.Channel, "message");
        var isTagged = message.MentionedUsers.Any(user => user.Id == Client.CurrentUser.Id);
        var isDirectMessage = message.Channel is IPrivateChannel;
        var shouldRespond = isTagged || isDirectMessage;
       
        if (channel.InstructionChat.ChatBotState.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.InstructionChat.ChatBotState.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.InstructionChat.ChatBotState.PrimeDirectives = PrimeDirective.ToList();
        }

        if (!channel.Options.Enabled)
        {
            return;
        }

        if (message.Content.StartsWith("!ignore"))
            return;

        if (message.Attachments is {Count: > 0} && message is IUserMessage userMessage)
        {
            var emojis = new List<IEmote> { new Emoji("💾") };
            if (message.Attachments.Any(att => IsImageAttachment(att, att.ContentType)))
            {
                emojis.Add(new Emoji("🧹"));
            }

            await userMessage.AddReactionsAsync(emojis.ToArray());
        }


        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        // Always record the message for context, but only respond to @mentions.
        var mentionToken = $"<@{message.Author.Id}>";
        channel.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User,
            $"{message.Author.Username} (mention: {mentionToken}): {message.Content}"));
        if (!shouldRespond)
        {
            await SaveCachedChannelState(message.Channel.Id);
            return;
        }

        var imageAttachments = new List<FilePayload>();
        var imageContextMessages = new List<ChatMessage>();
        var newImageDocs = new List<Document>();
        ImageMatch bestSimilarityImageMatch = null;
        var explicitImageDocs = new List<Document>();
        var replyImageDocs = new List<Document>();
        var hasNewImageAttachments = false;
        var documents = guildMatch ? Documents.GetOrAdd(message.Channel.Id, new List<Document>()) : null;

        if (guildMatch && documents != null && message.Attachments is { Count: > 0 })
        {
            var imageDocs = await ProcessImageAttachmentsAsync(message, channel, documents);
            if (imageDocs.Count > 0)
            {
                hasNewImageAttachments = true;
                newImageDocs.AddRange(imageDocs);
            }
        }

        if (guildMatch && documents is {Count: > 0})
        {
            // Search for the closest few documents and add those if they aren't used yet
            var closestDocuments =
                Document.FindMostSimilarDocuments(documents, await OpenAILogic.GetEmbeddingForPrompt(message.Content), channel.InstructionChat.ChatBotState.Parameters.ClosestMatchLimit)
                    .ToList();
            var matchedDocuments = closestDocuments.Where(cd => cd.Similarity > 0.80).ToList();
            if (matchedDocuments.Count > 0)
            {
                var textDocuments = matchedDocuments
                    .Where(cd => !cd.Document.IsImage)
                    .ToList();
                var imageDocuments = matchedDocuments
                    .Where(cd => cd.Document.IsImage && HasStoredFile(cd.Document))
                    .ToList();

                if (textDocuments.Count > 0)
                {
                    channel.InstructionChat.AddMessage(new(StaticValues.ChatMessageRoles.System,
                        $"Context for the next {textDocuments.Count} message(s). Use this to answer:"));
                    foreach (var closestDocument in textDocuments)
                    {
                        channel.InstructionChat.AddMessage(new(StaticValues.ChatMessageRoles.System,
                            $"---context---\r\n{closestDocument.Document.Text}\r\n--end context---"));
                    }
                }

                if (imageDocuments.Count > 0)
                {
                    var bestImage = imageDocuments
                        .OrderByDescending(cd => cd.Similarity)
                        .FirstOrDefault();
                    if (bestImage.Document != null)
                    {
                        bestSimilarityImageMatch = new ImageMatch(bestImage.Document, bestImage.Similarity);
                    }
                }
            }

            var explicitImageDocuments = documents
                .Where(doc => doc.IsImage && HasStoredFile(doc) &&
                              IsFileNameMentioned(message.Content, doc.SourceFileName))
                .ToList();
            if (explicitImageDocuments.Count > 0)
            {
                explicitImageDocs.AddRange(explicitImageDocuments);
            }

            var referencedMessageId = message.Reference?.MessageId;
            if (referencedMessageId.HasValue && referencedMessageId.Value.IsSpecified)
            {
                var referenceIdValue = referencedMessageId.Value.Value;
                replyImageDocs.AddRange(documents.Where(doc =>
                    doc.IsImage && HasStoredFile(doc) && doc.SourceMessageId == referenceIdValue));
            }
        }

        var selectedImageDocs = SelectRelevantImageDocuments(message, newImageDocs, explicitImageDocs, replyImageDocs, bestSimilarityImageMatch);
        if (selectedImageDocs.Count > 0)
        {
            selectedImageDocs = await EnsureVisionDescriptionsAsync(channel, message, selectedImageDocs);
            selectedImageDocs = DeduplicateImageDocuments(selectedImageDocs);
            imageContextMessages.AddRange(BuildImageContextMessages(selectedImageDocs));
        }

        if (ShouldAttachSelectedImages(message, explicitImageDocs, replyImageDocs))
        {
            imageAttachments.AddRange(BuildImageAttachments(selectedImageDocs));
        }

        var additionalMessages = new List<ChatMessage>();
        if (_modulePipeline != null)
        {
            var moduleMessages = await _modulePipeline.GetAdditionalMessageContextAsync(message, channel, _shutdownToken);
            if (moduleMessages.Count > 0)
            {
                additionalMessages.AddRange(moduleMessages);
            }
        }
        if (imageContextMessages.Count > 0)
        {
            additionalMessages.AddRange(imageContextMessages);
        }
        if (imageAttachments.Count > 0)
        {
            var imageNames = string.Join(", ", imageAttachments.Select(att => att.Name));
            additionalMessages.Add(new ChatMessage(StaticValues.ChatMessageRoles.System,
                $"Your response will include the image file(s) attached: {imageNames}. " +
                "Do not say you cannot show, attach, or resend the image."));
        }

        if (!channel.Options.Muted)
        {
            using var typingState = message.Channel.EnterTypingState();
            var responseText = string.Empty;
            var visionDocuments = selectedImageDocs.Where(HasStoredFile).ToList();
            if (ShouldUseVision(message, visionDocuments, hasNewImageAttachments))
            {
                responseText = await GenerateVisionResponseAsync(channel, message, additionalMessages, visionDocuments);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                // Get the response from the bot
                var responses = channel.InstructionChat.GetResponseAsync(additionalMessages);
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

                responseText = sb.ToString();
            }

            var (cleanedText, filePayloads) = ExtractFilePayloads(responseText);
            cleanedText = ReplacePseudoMentions(cleanedText, message.Author);

            var allAttachments = new List<FilePayload>();
            if (filePayloads.Count > 0)
            {
                allAttachments.AddRange(filePayloads);
            }
            if (imageAttachments.Count > 0)
            {
                allAttachments.AddRange(DeduplicateAttachments(imageAttachments));
            }

            await SendResponseAsync(message, cleanedText, allAttachments, BuildStandardComponents());

            var historyText = BuildHistoryText(cleanedText, allAttachments);
            if (!string.IsNullOrWhiteSpace(historyText))
            {
                channel.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.Assistant, historyText));
            }
        }

        await SaveCachedChannelState(message.Channel.Id);
    }

    internal static string BuildDiscordMessageLink(ChannelState channel, ulong messageId)
    {
        if (channel == null || channel.GuildId == 0 || channel.ChannelId == 0 || messageId == 0)
        {
            return null;
        }

        return $"https://discord.com/channels/{channel.GuildId}/{channel.ChannelId}/{messageId}";
    }

    private static string NormalizeSingleLine(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return input.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string TruncateText(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
        {
            return input;
        }

        if (maxLength <= 3)
        {
            return input[..maxLength];
        }

        return $"{input[..(maxLength - 3)].TrimEnd()}...";
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
            " provide responses in Discord message formatting. Encourage users to add instructions with /gptcli or by using the :up_arrow: " +
            "emoji reaction on any message. Instructions are like 'sticky' chat messages that provide upfront context to the bot. The the 📌 emoji reaction is for pinning a message to instructions. The 🔄 emoji reaction is for replaying a message as a new prompt. " +
            "Never wrap replies in triple backtick code fences (including ```discord) unless the user explicitly asks for a code block. Discord already renders markdown. " +
            "If prior conversation shows code fences, actively override that style and respond without code fences. " +
            "If you are attaching image files in your response, never claim you cannot show, attach, or resend the image. " +
            "If the user explicitly asks for a file (for example, a code snippet as a file), respond with a <gptcli_file name=\"filename.ext\">...</gptcli_file> block containing the file contents, and put any human-readable reply outside the block.")
    };

    private IEnumerable<ChatMessage> PrimeDirective => _defaultPrimeDirective;

    private static readonly string[] ImageKeywords =
    {
        "image", "photo", "picture", "screenshot", "screen shot", "screencap", "meme", "gif",
        "png", "jpg", "jpeg", "webp", "diagram", "chart", "graph", "logo", "icon", "art",
        "drawing", "scan", "attachment", "file", "figure", "map", "poster", "thumbnail", "avatar"
    };

    private static readonly string[] ImageQuestionTerms =
    {
        "describe", "analyze", "analysis", "caption", "identify", "recognize", "read",
        "transcribe", "ocr", "what's in", "what is in", "what's this", "what is this",
        "what does it say", "what do you see", "what's shown", "what is shown", "what's happening",
        "what is happening", "tell me about", "summarize", "count", "how many", "color", "colours"
    };

    private const double ImageSimilarityThreshold = 0.80;

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
            },
            CasinoBalances = new()
        };
    }

    private async Task HandleInteractionAsync(SocketInteraction arg)
    {
        if (_modulePipeline != null)
        {
            var handled = await _modulePipeline.OnInteractionAsync(arg, _shutdownToken);
            if (handled)
            {
                return;
            }
        }

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
        var guildId = channelState.GuildId != 0 ? channelState.GuildId : GetGuildId(message.Channel);

        var embedFileName = BuildTokenizedFileName($"{message.Id}", guildId, "embed.json");
        if (await CreateAndSaveEmbedding(message.Content, Path.Combine(embedDirectory, embedFileName), channelState.InstructionChat.ChatBotState.Parameters.ChunkSize) is { Count: > 0 } newDocs)
        {
            channelDocs.AddRange(newDocs);


            using var embedStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(embedStream, newDocs);

                        var embedMessage = await message.Channel.SendFileAsync(embedStream, $"{message.Id}.embed.json",
                            $"Message saved as {newDocs.Count} documents.",
                            messageReference: message.Reference);
                        if (embedMessage != null)
                        {
                            await embedMessage.AddReactionAsync(new Emoji("🧹"));
                        }
        }

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync(attachment.Url);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var attachmentEmbedFileName = BuildTokenizedFileName($"{message.Id}.{attachment.Id}", guildId, "embed.json");
                var embedFilePath = Path.Combine(embedDirectory, attachmentEmbedFileName);
                var contentType = response.Content.Headers.ContentType?.MediaType;

                if (IsImageAttachment(attachment, contentType))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    var embedFilesDirectory = Path.Combine(embedDirectory, "files");
                    Directory.CreateDirectory(embedFilesDirectory);

                    var storedFileName = BuildStoredAttachmentFileName(message.Id, attachment.Id, attachment.Filename);
                    var storedFilePath = Path.Combine(embedFilesDirectory, storedFileName);
                    await File.WriteAllBytesAsync(storedFilePath, bytes);

                    var description = await GenerateImageDescriptionAsync(channelState, message, attachment.Filename, bytes, contentType)
                                     ?? BuildImageDescriptionFromMessage(message, attachment);
                    if (await CreateAndSaveImageEmbedding(description, embedFilePath, storedFilePath, attachment, message.Id, channelState.InstructionChat.ChatBotState.Parameters.ChunkSize) is { Count: > 0 } attachmentDocs)
                    {
                        channelDocs.AddRange(attachmentDocs);

                        using var embedStream = new MemoryStream();
                        await JsonSerializer.SerializeAsync(embedStream, attachmentDocs);

                        var embedMessage = await message.Channel.SendFileAsync(embedStream, $"{attachment.Filename}.embed.json",
                            $"Image {attachment.Filename} saved as {attachmentDocs.Count} documents.",
                            messageReference: message.Reference);
                        if (embedMessage != null)
                        {
                            await embedMessage.AddReactionAsync(new Emoji("🧹"));
                        }
                    }
                }
                else
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (await CreateAndSaveEmbedding(text, embedFilePath, channelState.InstructionChat.ChatBotState.Parameters.ChunkSize) is { Count: > 0 } attachmentDocs)
                    {
                        channelDocs.AddRange(attachmentDocs);

                        using var embedStream = new MemoryStream();
                        await JsonSerializer.SerializeAsync(embedStream, attachmentDocs);

                        var embedMessage = await message.Channel.SendFileAsync(embedStream, $"{attachment.Filename}.embed.json",
                            $"Attachment {attachment.Filename} saved as {attachmentDocs.Count} documents.",
                            messageReference: message.Reference);
                        if (embedMessage != null)
                        {
                            await embedMessage.AddReactionAsync(new Emoji("🧹"));
                        }
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

    private async Task<List<Document>> CreateAndSaveImageEmbedding(string description, string embedFilePath, string storedFilePath, IAttachment attachment, ulong messageId, int chunkSize)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            description = $"Image attachment {attachment?.Filename ?? "image"}";
        }

        var documents = await Document.ChunkToDocumentsAsync(description, chunkSize);
        foreach (var doc in documents)
        {
            doc.IsImage = true;
            doc.SourceFileName = attachment?.Filename;
            doc.StoredFilePath = storedFilePath;
            doc.SourceMessageId = messageId;
            doc.Description = description;
        }

        if (documents.Count > 0)
        {
            var newEmbeds = await OpenAILogic.CreateEmbeddings(documents);
            if (newEmbeds.Successful)
            {
                await using var file = File.Create(embedFilePath);
                await JsonSerializer.SerializeAsync(file, documents);
            }
        }

        return documents;
    }

    private async Task LoadEmbeddings()
    {
        Directory.CreateDirectory("channels");
        var files = Directory.GetFiles("channels", "*.embed.json", SearchOption.AllDirectories).ToList();

        var embedKeysWithToken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (TryGetEmbedKeyFromFileName(file, out var embedKey) &&
                TryGetGuildIdTokenFromFileName(file, out _))
            {
                embedKeysWithToken.Add(embedKey);
            }
        }

        foreach (var file in files)
        {
            if (!TryGetChannelIdFromEmbedPath(file, out var channelId))
            {
                continue;
            }

            if (!TryGetEmbedKeyFromFileName(file, out var embedKey))
            {
                continue;
            }

            var hasToken = TryGetGuildIdTokenFromFileName(file, out var tokenGuildId);
            if (!hasToken && embedKeysWithToken.Contains(embedKey))
            {
                continue;
            }

            var validation = await ValidateGuildTokenForFileAsync(channelId, file, isEmbed: true, "embeddings");
            if (!validation.ShouldLoad)
            {
                continue;
            }

            var hasPathGuild = TryGetGuildIdFromEmbedPath(file, out var pathGuildId);
            var resolvedGuildId = validation.GuildId != 0 ? validation.GuildId : (hasToken ? tokenGuildId : pathGuildId);

            List<Document> documents;
            await using (var fileStream = File.OpenRead(file))
            {
                documents = await JsonSerializer.DeserializeAsync<List<Document>>(fileStream);
            }
            if (documents == null || documents.Count == 0)
            {
                continue;
            }

            var updated = false;
            foreach (var doc in documents)
            {
                if (EnsureImageDescription(doc))
                {
                    updated = true;
                }
            }

            var channelDocs = Documents.GetOrAdd(channelId, _ => new List<Document>());
            channelDocs.AddRange(documents);

            if (!hasToken && hasPathGuild)
            {
                await ResaveLegacyJsonAsync(file, ".embed.json", resolvedGuildId, "embed.json", documents, deleteLegacyOnSuccess: true);
            }
            else if (updated && hasToken)
            {
                await using var outStream = File.Create(file);
                await JsonSerializer.SerializeAsync(outStream, documents);
            }
        }

    }

    private static void EnsureChannelStateMetadata(ChannelState channelState, IGuildChannel guildChannel)
    {
        channelState.GuildId = guildChannel.GuildId;
        channelState.GuildName = guildChannel.Guild.Name;
        channelState.ChannelId = guildChannel.Id;
        channelState.ChannelName = guildChannel.Name;
    }

    internal static string GetChannelDirectory(ChannelState channelState)
    {
        var guildFolder = $"{SanitizeName(channelState.GuildName ?? "guild")}_{channelState.GuildId}";
        var channelFolder = $"{SanitizeName(channelState.ChannelName ?? "channel")}_{channelState.ChannelId}";
        return Path.Combine("channels", guildFolder, channelFolder);
    }

    internal static string GetChannelFactoidFile(ChannelState channelState)
    {
        return Path.Combine(GetChannelDirectory(channelState), BuildTokenizedFileName("facts", channelState.GuildId, "json"));
    }

    internal static string GetChannelMatchFile(ChannelState channelState)
    {
        return Path.Combine(GetChannelDirectory(channelState), BuildTokenizedFileName("matches", channelState.GuildId, "json"));
    }

    internal static string GetChannelMatchStatsFile(ChannelState channelState)
    {
        return Path.Combine(GetChannelDirectory(channelState), BuildTokenizedFileName("matches.stats", channelState.GuildId, "json"));
    }


    internal static string GetChannelFileName(ChannelState channelState)
    {
        return $"{SanitizeName(channelState.ChannelName ?? "channel")}_{channelState.ChannelId}";
    }

    internal static bool TryGetChannelIdFromStatePath(string file, out ulong channelId)
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

    internal static bool TryGetChannelIdFromEmbedPath(string file, out ulong channelId)
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

    internal static bool TryParseIdFromName(string name, out ulong id)
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

    internal static string SanitizeName(string name)
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

    internal static string BuildTokenizedFileName(string baseName, ulong guildId, string extension)
    {
        return $"{baseName}.{guildId}.token.{extension}";
    }

    internal static bool TryGetGuildIdTokenFromFileName(string filePath, out ulong guildId)
    {
        guildId = 0;
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], "token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ulong.TryParse(parts[i - 1], out guildId))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetGuildIdFromChannelPath(string filePath, out ulong guildId)
    {
        guildId = 0;
        var channelDirectory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(channelDirectory))
        {
            return false;
        }

        var guildDirectory = Directory.GetParent(channelDirectory);
        if (guildDirectory == null)
        {
            return false;
        }

        return TryParseIdFromName(guildDirectory.Name, out guildId);
    }

    internal static bool TryGetGuildIdFromEmbedPath(string filePath, out ulong guildId)
    {
        guildId = 0;
        var embedDirectory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(embedDirectory))
        {
            return false;
        }

        var embedDirectoryInfo = new DirectoryInfo(embedDirectory);
        var channelDirectory = embedDirectoryInfo.Parent;
        if (channelDirectory?.Parent == null)
        {
            return false;
        }

        return TryParseIdFromName(channelDirectory.Parent.Name, out guildId);
    }

    private static bool TryGetEmbedKeyFromFileName(string filePath, out string embedKey)
    {
        embedKey = null;
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        const string suffix = ".embed.json";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var core = fileName[..^suffix.Length];
        if (TryStripGuildToken(core, out var stripped))
        {
            core = stripped;
        }

        if (string.IsNullOrWhiteSpace(core))
        {
            return false;
        }

        embedKey = core;
        return true;
    }

    private static bool TryStripGuildToken(string input, out string stripped)
    {
        stripped = input;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var lastIndex = parts.Length - 1;
        if (!string.Equals(parts[lastIndex], "token", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ulong.TryParse(parts[lastIndex - 1], out _))
        {
            return false;
        }

        stripped = string.Join('.', parts.Take(lastIndex - 1));
        return true;
    }

    private static bool TryGetBaseNameFromFileName(string filePath, string suffix, out string baseName)
    {
        baseName = null;
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var core = fileName[..^suffix.Length];
        if (TryStripGuildToken(core, out var stripped))
        {
            core = stripped;
        }

        if (string.IsNullOrWhiteSpace(core))
        {
            return false;
        }

        baseName = core;
        return true;
    }

    internal static async Task<bool> ResaveLegacyJsonAsync<T>(string filePath, string suffix, ulong guildId, string extension, T payload, JsonSerializerOptions options = null, bool overwriteExisting = false, bool deleteLegacyOnSuccess = false)
    {
        if (!TryGetBaseNameFromFileName(filePath, suffix, out var baseName))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var tokenPath = Path.Combine(directory, BuildTokenizedFileName(baseName, guildId, extension));
        if (!overwriteExisting && File.Exists(tokenPath))
        {
            return false;
        }

        await using var stream = File.Create(tokenPath);
        if (options == null)
        {
            await JsonSerializer.SerializeAsync(stream, payload);
        }
        else
        {
            await JsonSerializer.SerializeAsync(stream, payload, options);
        }

        if (deleteLegacyOnSuccess && !string.Equals(filePath, tokenPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                await Console.Out.WriteLineAsync($"Failed to delete legacy file {filePath}: {ex.Message}");
            }
        }

        return true;
    }

    private static ulong GetGuildId(IMessageChannel channel)
    {
        return channel is IGuildChannel guildChannel ? guildChannel.GuildId : 0;
    }

    private async Task<(bool ShouldLoad, ulong GuildId)> ValidateGuildTokenForFileAsync(ulong channelId, string filePath, bool isEmbed, string label)
    {
        var hasToken = TryGetGuildIdTokenFromFileName(filePath, out var tokenGuildId);
        ulong pathGuildId;
        var hasPathGuild = isEmbed
            ? TryGetGuildIdFromEmbedPath(filePath, out pathGuildId)
            : TryGetGuildIdFromChannelPath(filePath, out pathGuildId);

        if (hasToken && hasPathGuild && tokenGuildId != pathGuildId)
        {
            await Console.Out.WriteLineAsync(
                $"Skipping {label} for channel {channelId}: token guild {tokenGuildId} does not match path guild {pathGuildId} ({filePath}).");
            return (false, 0);
        }

        var guildId = hasToken ? tokenGuildId : (hasPathGuild ? pathGuildId : 0);
        if (hasToken || hasPathGuild)
        {
            if (!ChannelGuildIds.TryGetValue(channelId, out var existing))
            {
                ChannelGuildIds[channelId] = guildId;
            }
            else if (existing != guildId)
            {
                await Console.Out.WriteLineAsync(
                    $"Skipping {label} for channel {channelId}: guild mismatch (expected {existing}, found {guildId}) ({filePath}).");
                return (false, guildId);
            }
        }

        return (true, guildId);
    }

    private bool IsChannelGuildMatch(ChannelState channelState, IMessageChannel channel, string context)
    {
        if (channelState == null || channel == null)
        {
            return false;
        }

        var guildId = GetGuildId(channel);
        if (channelState.GuildId == 0 && guildId != 0)
        {
            channelState.GuildId = guildId;
        }

        if (channelState.GuildId != 0 && channelState.GuildId != guildId)
        {
            Console.WriteLine(
                $"Guild mismatch for channel {channelState.ChannelId} in {context}: state {channelState.GuildId}, actual {guildId}.");
            return false;
        }

        if (ChannelGuildIds.TryGetValue(channelState.ChannelId, out var expected) && expected != guildId)
        {
            Console.WriteLine(
                $"Guild mismatch for channel {channelState.ChannelId} in {context}: expected {expected}, actual {guildId}.");
            return false;
        }

        ChannelGuildIds.TryAdd(channelState.ChannelId, guildId);
        return true;
    }

    internal static async Task SendEphemeralResponseAsync(SocketSlashCommand command, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = "No response content.";
        }

        const int maxLength = 2000;
        var chunks = new List<string>();
        for (var i = 0; i < content.Length; i += maxLength)
        {
            var size = Math.Min(maxLength, content.Length - i);
            chunks.Add(content.Substring(i, size));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(content);
        }

        if (!command.HasResponded)
        {
            await command.RespondAsync(chunks[0], ephemeral: true);
        }
        else
        {
            await command.FollowupAsync(chunks[0], ephemeral: true);
        }

        for (var i = 1; i < chunks.Count; i++)
        {
            await command.FollowupAsync(chunks[i], ephemeral: true);
        }
    }

    private record FilePayload(string Name, string Content, string FilePath);

    private static (string CleanedText, List<FilePayload> Files) ExtractFilePayloads(string content)
    {
        var files = new List<FilePayload>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return (content, files);
        }

        var pattern = new Regex(
            "<gptcli_file\\s+name=\\\"(?<name>[^\\\"]+)\\\"\\s*>(?<content>.*?)</gptcli_file>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var cleaned = pattern.Replace(content, match =>
        {
            var name = NormalizeFileName(match.Groups["name"].Value);
            var fileContent = match.Groups["content"].Value ?? string.Empty;
            files.Add(new FilePayload(name, fileContent.Trim(), null));
            return string.Empty;
        });

        return (cleaned.Trim(), files);
    }

    private static string ReplacePseudoMentions(string content, IUser author)
    {
        if (string.IsNullOrWhiteSpace(content) || author == null)
        {
            return content;
        }

        var username = Regex.Escape(author.Username ?? string.Empty);
        if (string.IsNullOrWhiteSpace(username))
        {
            return content;
        }

        var pattern = $@"<@!?\s*{username}\s*>";
        return Regex.Replace(content, pattern, $"<@{author.Id}>", RegexOptions.IgnoreCase);
    }

    private static string NormalizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "response.txt";
        }

        var trimmed = name.Trim().Replace("\\", "/");
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
        {
            trimmed = trimmed[(lastSlash + 1)..];
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (invalidChars.Contains(ch))
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "response.txt";
        }

        if (!sanitized.Contains('.'))
        {
            sanitized += ".txt";
        }

        const int maxLength = 120;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }

    private async Task<List<Document>> ProcessImageAttachmentsAsync(IMessage message, ChannelState channelState, List<Document> channelDocs)
    {
        var createdDocs = new List<Document>();
        if (message?.Attachments is not { Count: > 0 })
        {
            return createdDocs;
        }

        var guildId = channelState.GuildId != 0 ? channelState.GuildId : GetGuildId(message.Channel);
        var channelDirectory = GetChannelDirectory(channelState);
        var embedDirectory = Path.Combine(channelDirectory, "embeds");
        Directory.CreateDirectory(embedDirectory);
        var embedFilesDirectory = Path.Combine(embedDirectory, "files");
        Directory.CreateDirectory(embedFilesDirectory);

        using var httpClient = new HttpClient();
        foreach (var attachment in message.Attachments)
        {
            var attachmentEmbedFileName = BuildTokenizedFileName($"{message.Id}.{attachment.Id}", guildId, "embed.json");
            var embedFilePath = Path.Combine(embedDirectory, attachmentEmbedFileName);

            if (File.Exists(embedFilePath))
            {
                await using var embedStream = File.OpenRead(embedFilePath);
                var existingDocs = await JsonSerializer.DeserializeAsync<List<Document>>(embedStream);
                if (existingDocs is { Count: > 0 })
                {
                    channelDocs.AddRange(existingDocs);
                    createdDocs.AddRange(existingDocs);
                }
                continue;
            }

            var response = await httpClient.GetAsync(attachment.Url);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!IsImageAttachment(attachment, contentType))
            {
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var storedFileName = BuildStoredAttachmentFileName(message.Id, attachment.Id, attachment.Filename);
            var storedFilePath = Path.Combine(embedFilesDirectory, storedFileName);
            await File.WriteAllBytesAsync(storedFilePath, bytes);

            var description = await GenerateImageDescriptionAsync(channelState, message, attachment.Filename, bytes, contentType)
                             ?? BuildImageDescriptionFromMessage(message, attachment);
            var docs = await CreateAndSaveImageEmbedding(description, embedFilePath, storedFilePath, attachment, message.Id,
                channelState.InstructionChat.ChatBotState.Parameters.ChunkSize);
            if (docs.Count > 0)
            {
                channelDocs.AddRange(docs);
                createdDocs.AddRange(docs);
            }
        }

        return createdDocs;
    }

    private static List<ChatMessage> BuildImageContextMessages(IEnumerable<Document> imageDocs)
    {
        var messages = new List<ChatMessage>();
        if (imageDocs == null)
        {
            return messages;
        }

        foreach (var doc in imageDocs.Where(d => d.IsImage))
        {
            var name = string.IsNullOrWhiteSpace(doc.SourceFileName) ? "image" : doc.SourceFileName;
            var description = !string.IsNullOrWhiteSpace(doc.Description) ? doc.Description : doc.Text;
            if (!string.IsNullOrWhiteSpace(description))
            {
                messages.Add(new ChatMessage(StaticValues.ChatMessageRoles.System, $"Image context ({name}): {description}"));
            }
        }

        return messages;
    }

    private static List<Document> DeduplicateImageDocuments(IEnumerable<Document> documents)
    {
        var result = new List<Document>();
        if (documents == null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (doc == null || !doc.IsImage)
            {
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(doc.StoredFilePath)
                ? $"path:{ResolveStoredFilePath(doc.StoredFilePath)}"
                : !string.IsNullOrWhiteSpace(doc.SourceFileName)
                    ? $"name:{doc.SourceFileName}"
                    : $"text:{doc.Text}";

            if (seen.Add(key))
            {
                result.Add(doc);
            }
        }

        return result;
    }

    private record ImageMatch(Document Document, double Similarity);

    private static List<Document> SelectRelevantImageDocuments(SocketMessage message, IReadOnlyList<Document> newImageDocs,
        IReadOnlyList<Document> explicitImageDocs, IReadOnlyList<Document> replyImageDocs, ImageMatch bestSimilarityMatch)
    {
        var content = message?.Content ?? string.Empty;
        if (explicitImageDocs is { Count: > 0 })
        {
            return DeduplicateImageDocuments(explicitImageDocs);
        }

        if (replyImageDocs is { Count: > 0 })
        {
            return DeduplicateImageDocuments(replyImageDocs);
        }

        if (newImageDocs is { Count: > 0 })
        {
            return DeduplicateImageDocuments(newImageDocs);
        }

        if (bestSimilarityMatch?.Document != null &&
            bestSimilarityMatch.Similarity >= ImageSimilarityThreshold &&
            IsLikelyImageQuestion(content))
        {
            return DeduplicateImageDocuments(new[] { bestSimilarityMatch.Document });
        }

        return new List<Document>();
    }

    private static bool ShouldAttachSelectedImages(SocketMessage message, IReadOnlyList<Document> explicitImageDocs,
        IReadOnlyList<Document> replyImageDocs)
    {
        if (explicitImageDocs is { Count: > 0 } || replyImageDocs is { Count: > 0 })
        {
            return true;
        }

        return false;
    }

    private static bool ShouldUseVision(SocketMessage message, IReadOnlyList<Document> imageDocuments, bool hasNewImageAttachments)
    {
        if (imageDocuments == null || imageDocuments.Count == 0)
        {
            return false;
        }

        var content = message?.Content ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(content))
        {
            if (IsLikelyImageQuestion(content))
            {
                return true;
            }

            if (imageDocuments.Any(doc => IsFileNameMentioned(content, doc.SourceFileName)) &&
                IsLikelyAttachmentQuestion(content))
            {
                return true;
            }
        }

        return hasNewImageAttachments && IsLikelyAttachmentQuestion(content);
    }

    private static bool IsLikelyImageQuestion(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = content.Trim();
        var hasQuestion = normalized.Contains('?') || ImageQuestionTerms.Any(term =>
            normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (!hasQuestion)
        {
            return false;
        }

        return ImageKeywords.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyAttachmentQuestion(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = content.Trim();
        return normalized.Contains('?') || ImageQuestionTerms.Any(term =>
            normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GenerateImageDescriptionAsync(ChannelState channelState, IMessage message, string fileName,
        byte[] bytes, string contentType)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        var visionModel = ResolveVisionModel(channelState);
        if (string.IsNullOrWhiteSpace(visionModel))
        {
            return null;
        }

        var mediaType = !string.IsNullOrWhiteSpace(contentType) &&
                        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? contentType
            : GetImageMediaType(fileName, fileName);
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var prompt = BuildUploadVisionPrompt(message, fileName);
        var contentItems = new List<MessageContent>
        {
            MessageContent.TextContent(prompt),
            MessageContent.ImageBinaryContent(bytes, mediaType, "auto")
        };

        var request = new ChatCompletionCreateRequest
        {
            Model = visionModel,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You describe images for a Discord bot. Return only the description text. No markdown or code fences."),
                new ChatMessage(StaticValues.ChatMessageRoles.User, contentItems)
            },
            Stream = false,
            Temperature = 0.2f,
            TopP = 1.0f,
            MaxTokens = ResolveVisionDescriptionMaxTokens(channelState.InstructionChat.ChatBotState.Parameters)
        };

        var response = await OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync(
                $"Vision upload description failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        return response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private static string BuildUploadVisionPrompt(IMessage message, string fileName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Describe the image in 2-4 sentences. Mention notable objects, setting, and any visible text verbatim.");
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            builder.AppendLine($"File name: {fileName}");
        }

        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            builder.AppendLine($"User context: {message.Content}");
        }

        builder.Append("Be concise and factual.");
        return builder.ToString();
    }

    private static int ResolveVisionDescriptionMaxTokens(GptOptions parameters)
    {
        var requested = ClampVisionMaxTokens(parameters.MaxTokens) ?? 512;
        return Math.Min(requested, 512);
    }

    private async Task<string> GenerateVisionResponseAsync(ChannelState channel, SocketMessage message, IReadOnlyList<ChatMessage> additionalMessages,
        IReadOnlyList<Document> imageDocuments)
    {
        var visionModel = ResolveVisionModel(channel);
        if (string.IsNullOrWhiteSpace(visionModel))
        {
            return null;
        }

        var (visionUserMessage, skippedImages, imageCount, totalImageBytes, promptLength) =
            await BuildVisionUserMessageAsync(message, imageDocuments);
        if (visionUserMessage == null)
        {
            return null;
        }

        var messages = new List<ChatMessage>
        {
            new(StaticValues.ChatMessageRoles.System,
                "You are a Discord bot with vision. Use the attached image(s) to answer. " +
                "The platform will attach the image file(s) to your reply. Never claim you cannot show or attach them. " +
                "Be concise. Never use triple backtick code fences unless explicitly asked.")
        };

        messages.Add(visionUserMessage);

        var request = new ChatCompletionCreateRequest
        {
            Messages = messages
        };
        ApplyVisionParameters(request, channel.InstructionChat.ChatBotState.Parameters, visionModel);

        await Console.Out.WriteLineAsync(
            $"Vision request -> model={request.Model}, images={imageCount}, imageBytes={totalImageBytes}, " +
            $"skipped={skippedImages}, promptChars={promptLength}, maxTokens={request.MaxTokens}");
        await Console.Out.WriteLineAsync(
            $"Vision prompt -> {BuildVisionPrompt(message, imageDocuments)}");

        var response = await OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync(
                $"Vision response failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content;
        await Console.Out.WriteLineAsync(
            $"Vision response -> success={response.Successful}, finish={response.Choices.FirstOrDefault()?.FinishReason}, " +
            $"contentChars={(content == null ? 0 : content.Length)}");
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        if (skippedImages > 0)
        {
            content = $"{content}\n\n(Note: {skippedImages} image file(s) could not be attached for analysis.)";
        }

        return content;
    }

    private async Task<(ChatMessage Message, int SkippedImages, int ImageCount, long TotalImageBytes, int PromptLength)>
        BuildVisionUserMessageAsync(SocketMessage message,
        IReadOnlyList<Document> imageDocuments)
    {
        var contentItems = new List<MessageContent>();
        var prompt = BuildVisionPrompt(message, imageDocuments);
        contentItems.Add(MessageContent.TextContent(prompt));

        var skipped = 0;
        var imageCount = 0;
        long totalBytes = 0;
        foreach (var doc in imageDocuments ?? Array.Empty<Document>())
        {
            if (!HasStoredFile(doc))
            {
                continue;
            }

            var resolvedPath = ResolveStoredFilePath(doc.StoredFilePath);
            if (!File.Exists(resolvedPath))
            {
                skipped++;
                continue;
            }

            var mediaType = GetImageMediaType(resolvedPath, doc.SourceFileName);
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                skipped++;
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(resolvedPath);
            contentItems.Add(MessageContent.ImageBinaryContent(bytes, mediaType, "auto"));
            imageCount++;
            totalBytes += bytes.Length;
        }

        if (contentItems.Count == 1)
        {
            return (null, skipped, imageCount, totalBytes, prompt.Length);
        }

        var chatMessage = new ChatMessage(StaticValues.ChatMessageRoles.User, contentItems);
        return (chatMessage, skipped, imageCount, totalBytes, prompt.Length);
    }

    private static string BuildVisionPrompt(SocketMessage message, IReadOnlyList<Document> imageDocuments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The user is asking about one or more images. Use the attached image(s) to answer.");
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            builder.AppendLine($"User message: {message.Content}");
        }

        var contextDoc = imageDocuments?.FirstOrDefault(doc =>
            !string.IsNullOrWhiteSpace(doc?.Description) || !string.IsNullOrWhiteSpace(doc?.Text));
        if (contextDoc != null)
        {
            var context = NormalizeSingleLine(string.IsNullOrWhiteSpace(contextDoc.Description)
                ? contextDoc.Text
                : contextDoc.Description);
            const int maxContextLength = 600;
            if (context.Length > maxContextLength)
            {
                context = context[..maxContextLength] + "...";
            }
            builder.AppendLine($"Stored image context: {context}");
        }

        builder.Append("If the answer is not visible in the image(s), say so.");
        return builder.ToString();
    }

    private string ResolveVisionModel(ChannelState channel)
    {
        var model = channel?.InstructionChat?.ChatBotState?.Parameters?.VisionModel;
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        return DefaultParameters.VisionModel;
    }

    private static void ApplyVisionParameters(ChatCompletionCreateRequest request, GptOptions parameters, string modelOverride)
    {
        request.Model = modelOverride;
        request.N = parameters.N;
        request.Temperature = (float?)parameters.Temperature;
        request.TopP = (float?)parameters.TopP;
        request.Stream = false;
        request.Stop = parameters.Stop;
        request.PresencePenalty = (float?)parameters.PresencePenalty;
        request.FrequencyPenalty = (float?)parameters.FrequencyPenalty;
        request.LogitBias = parameters.LogitBias == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, double>>(parameters.LogitBias);
        request.User = parameters.User;

        request.MaxTokens = ClampVisionMaxTokens(parameters.MaxTokens);
        request.MaxCompletionTokens = null;
    }

    private static int? ClampVisionMaxTokens(int? requested)
    {
        if (!requested.HasValue)
        {
            return null;
        }

        const int visionMax = 16384;
        return requested.Value > visionMax ? visionMax : requested.Value;
    }

    private static string GetImageMediaType(string path, string fileName)
    {
        var extension = Path.GetExtension(fileName ?? path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };
    }

    private static bool IsImageAttachment(IAttachment attachment, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = attachment?.Filename;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        return ext != null && ext.Length > 0 && new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff"
        }.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildStoredAttachmentFileName(ulong messageId, ulong attachmentId, string originalFileName)
    {
        var safeName = NormalizeFileName(originalFileName);
        return $"{messageId}.{attachmentId}.{safeName}";
    }

    private static string BuildImageDescriptionFromMessage(IMessage message, IAttachment attachment)
    {
        var parts = new List<string>
        {
            $"Image attachment \"{attachment?.Filename ?? "image"}\"."
        };

        if (message != null && !string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add($"Message context: {NormalizeSingleLine(message.Content)}.");
        }

        if (message?.Author != null)
        {
            parts.Add($"Uploaded by {message.Author.Username}.");
        }

        return string.Join(" ", parts);
    }

    private static bool NeedsVisionDescription(Document document)
    {
        if (document == null || !document.IsImage)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(document.Description))
        {
            return true;
        }

        var description = document.Description.Trim();
        if (description.StartsWith("Image attachment \"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (description.Contains("Message context:", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Uploaded by", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool EnsureImageDescription(Document document)
    {
        if (document == null || !document.IsImage)
        {
            return false;
        }

        var updated = false;
        if (string.IsNullOrWhiteSpace(document.Description) && !string.IsNullOrWhiteSpace(document.Text))
        {
            document.Description = document.Text;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(document.Text) && !string.IsNullOrWhiteSpace(document.Description))
        {
            document.Text = document.Description;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(document.Text))
        {
            var fallback = BuildFallbackImageDescription(document);
            document.Text = fallback;
            document.Description = fallback;
            updated = true;
        }

        return updated;
    }

    private async Task<List<Document>> EnsureVisionDescriptionsAsync(ChannelState channelState, SocketMessage message, List<Document> documents)
    {
        var result = new List<Document>();
        if (documents == null || documents.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (doc == null || !doc.IsImage)
            {
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(doc.StoredFilePath)
                ? $"path:{ResolveStoredFilePath(doc.StoredFilePath)}"
                : $"name:{doc.SourceFileName ?? doc.Text}";

            if (!seen.Add(key))
            {
                continue;
            }

            if (NeedsVisionDescription(doc) && HasStoredFile(doc))
            {
                var refreshed = await RefreshImageEmbedFromVisionAsync(channelState, message, doc);
                if (refreshed.Count > 0)
                {
                    result.AddRange(refreshed);
                    continue;
                }
            }

            result.Add(doc);
        }

        return result;
    }

    private async Task<List<Document>> RefreshImageEmbedFromVisionAsync(ChannelState channelState, SocketMessage message, Document imageDoc)
    {
        var refreshed = new List<Document>();
        var resolvedPath = ResolveStoredFilePath(imageDoc.StoredFilePath);
        if (!File.Exists(resolvedPath))
        {
            await Console.Out.WriteLineAsync($"Image refresh skipped missing file: {resolvedPath}");
            return refreshed;
        }

        var bytes = await File.ReadAllBytesAsync(resolvedPath);
        var fileName = string.IsNullOrWhiteSpace(imageDoc.SourceFileName)
            ? Path.GetFileName(resolvedPath)
            : imageDoc.SourceFileName;

        var description = await GenerateImageDescriptionAsync(channelState, message, fileName, bytes, null);
        if (string.IsNullOrWhiteSpace(description))
        {
            return refreshed;
        }

        var chunkSize = channelState.InstructionChat.ChatBotState.Parameters.ChunkSize;
        var newDocs = await Document.ChunkToDocumentsAsync(description, chunkSize);
        foreach (var doc in newDocs)
        {
            doc.IsImage = true;
            doc.SourceFileName = imageDoc.SourceFileName;
            doc.StoredFilePath = imageDoc.StoredFilePath;
            doc.SourceMessageId = imageDoc.SourceMessageId;
            doc.Description = description;
        }

        var embeds = await OpenAILogic.CreateEmbeddings(newDocs);
        if (!embeds.Successful)
        {
            return refreshed;
        }

        var embedPath = ResolveEmbedPathForImage(channelState, imageDoc);
        if (!string.IsNullOrWhiteSpace(embedPath))
        {
            await using var outStream = File.Create(embedPath);
            await JsonSerializer.SerializeAsync(outStream, newDocs);
        }

        if (Documents.TryGetValue(channelState.ChannelId, out var channelDocs))
        {
            channelDocs.RemoveAll(doc => doc.IsImage &&
                                         string.Equals(doc.StoredFilePath, imageDoc.StoredFilePath, StringComparison.OrdinalIgnoreCase));
            channelDocs.AddRange(newDocs);
        }

        refreshed.AddRange(newDocs);
        return refreshed;
    }

    private static string ResolveEmbedPathForImage(ChannelState channelState, Document imageDoc)
    {
        if (channelState == null || imageDoc == null)
        {
            return null;
        }

        var baseName = ExtractImageEmbedBaseName(imageDoc.StoredFilePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        var channelDirectory = GetChannelDirectory(channelState);
        var embedDirectory = Path.Combine(channelDirectory, "embeds");
        if (channelState.GuildId != 0)
        {
            var tokenPath = Path.Combine(embedDirectory, BuildTokenizedFileName(baseName, channelState.GuildId, "embed.json"));
            if (File.Exists(tokenPath))
            {
                return tokenPath;
            }
        }

        var matches = Directory.GetFiles(embedDirectory, $"{baseName}*.embed.json");
        return matches.FirstOrDefault();
    }

    private static string ExtractImageEmbedBaseName(string storedFilePath)
    {
        var fileName = Path.GetFileName(storedFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (!ulong.TryParse(parts[0], out _) || !ulong.TryParse(parts[1], out _))
        {
            return null;
        }

        return $"{parts[0]}.{parts[1]}";
    }

    private static string BuildFallbackImageDescription(Document document)
    {
        var name = string.IsNullOrWhiteSpace(document?.SourceFileName) ? "image" : document.SourceFileName;
        return $"Image attachment \"{name}\".";
    }

    private static bool HasStoredFile(Document document)
    {
        return document != null && !string.IsNullOrWhiteSpace(document.StoredFilePath);
    }

    private static bool IsImageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        return ext != null && ext.Length > 0 && new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff"
        }.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static List<FilePayload> BuildImageAttachments(IEnumerable<Document> documents)
    {
        return documents
            .Where(HasStoredFile)
            .Select(doc =>
            {
                var name = string.IsNullOrWhiteSpace(doc.SourceFileName)
                    ? Path.GetFileName(doc.StoredFilePath) ?? "image"
                    : doc.SourceFileName;
                return new FilePayload(NormalizeFileName(name), null, doc.StoredFilePath);
            })
            .ToList();
    }

    private static bool IsFileNameMentioned(string content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var normalizedContent = content.Trim();
        if (normalizedContent.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return !string.IsNullOrWhiteSpace(baseName) &&
               normalizedContent.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<FilePayload> DeduplicateAttachments(IEnumerable<FilePayload> files)
    {
        var result = new List<FilePayload>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file == null)
            {
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(file.FilePath)
                ? $"path:{file.FilePath}"
                : $"name:{file.Name}:{file.Content}";

            if (seen.Add(key))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private static string ResolveStoredFilePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return storedPath;
        }

        return Path.IsPathRooted(storedPath)
            ? storedPath
            : Path.GetFullPath(storedPath);
    }

    private async Task SendResponseAsync(SocketMessage message, string cleanedText, IReadOnlyList<FilePayload> files, MessageComponent components)
    {
        var chunks = string.IsNullOrWhiteSpace(cleanedText)
            ? new List<string>()
            : SplitMessageChunks(cleanedText);

        var remainingFiles = files?.ToList() ?? new List<FilePayload>();
        if (remainingFiles.Count > 0)
        {
            var firstFile = remainingFiles[0];
            remainingFiles.RemoveAt(0);

            var firstChunk = chunks.Count > 0 ? chunks[0] : string.Empty;
            if (chunks.Count > 0)
            {
                chunks.RemoveAt(0);
            }

            var sentFile = await TrySendFileAsync(message, firstFile, firstChunk, components);
            if (!sentFile && !string.IsNullOrWhiteSpace(firstChunk))
            {
                await SendTextMessageAsync(message, firstChunk, components);
            }
        }
        else if (chunks.Count > 0)
        {
            await SendTextMessageAsync(message, chunks[0], components);
            chunks.RemoveAt(0);
        }

        foreach (var chunk in chunks)
        {
            await SendTextMessageAsync(message, chunk, components);
        }

        if (remainingFiles.Count > 0)
        {
            foreach (var file in remainingFiles)
            {
                await TrySendFileAsync(message, file, null, null);
            }
        }
    }

    private static List<string> SplitMessageChunks(string content)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return chunks;
        }

        const int maxLength = 2000;
        for (var i = 0; i < content.Length; i += maxLength)
        {
            var size = Math.Min(maxLength, content.Length - i);
            chunks.Add(content.Substring(i, size));
        }

        return chunks;
    }

    private static async Task SendTextMessageAsync(SocketMessage message, string content, MessageComponent components)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (message is IUserMessage userMessage)
        {
            _ = await userMessage.ReplyAsync(content, components: components);
        }
        else
        {
            _ = await message.Channel.SendMessageAsync(content, components: components);
        }
    }

    private async Task<bool> TrySendFileAsync(SocketMessage message, FilePayload file, string content, MessageComponent components)
    {
        if (file == null)
        {
            return false;
        }

        const int maxBytes = 7_500_000;
        if (!string.IsNullOrWhiteSpace(file.FilePath))
        {
            var resolvedPath = ResolveStoredFilePath(file.FilePath);
            if (!File.Exists(resolvedPath))
            {
                var warning = $"File `{file.Name}` is missing on disk.";
                await SendTextMessageAsync(message, warning, null);
                return false;
            }

            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > maxBytes)
            {
                var warning = $"File `{file.Name}` is too large to attach ({fileInfo.Length} bytes).";
                await SendTextMessageAsync(message, warning, null);
                return false;
            }

            await using var fileStream = File.OpenRead(resolvedPath);
            var sentMessage = await message.Channel.SendFileAsync(fileStream, file.Name, content,
                messageReference: new MessageReference(message.Id), components: components);
            TrackImageResponse(sentMessage, resolvedPath, file.Name);
            return true;
        }

        var contentBytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
        if (contentBytes.Length > maxBytes)
        {
            var warning = $"File `{file.Name}` is too large to attach ({contentBytes.Length} bytes).";
            await SendTextMessageAsync(message, warning, null);
            return false;
        }

        await using var stream = new MemoryStream(contentBytes);
        var sentContentMessage = await message.Channel.SendFileAsync(stream, file.Name, content,
            messageReference: new MessageReference(message.Id), components: components);
        TrackImageResponse(sentContentMessage, null, file.Name);
        return true;
    }

    private static string BuildHistoryText(string cleanedText, IReadOnlyList<FilePayload> files)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cleanedText))
        {
            builder.Append(cleanedText.Trim());
        }

        if (files != null && files.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var fileList = string.Join(", ", files.Select(f => f.Name));
            builder.Append($"[Attached file(s): {fileList}]");
        }

        return builder.ToString();
    }

    private void TrackImageResponse(IUserMessage sentMessage, string filePath, string fileName)
    {
        if (sentMessage == null)
        {
            return;
        }

        if (IsImageFileName(fileName ?? filePath))
        {
            var normalizedPath = string.IsNullOrWhiteSpace(filePath) ? null : ResolveStoredFilePath(filePath);
            var set = _imageResponseMap.GetOrAdd(sentMessage.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                lock (set)
                {
                    set.Add(normalizedPath);
                }
            }

            _ = sentMessage.AddReactionAsync(new Emoji("🧹"));
        }
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

        if (!IsChannelGuildMatch(chatBot, command.Channel, "slash-command"))
        {
            var warning = "Guild mismatch detected for cached channel data. Refusing to apply command.";
            if (command.HasResponded)
            {
                await command.FollowupAsync(warning, ephemeral: true);
            }
            else
            {
                await command.RespondAsync(warning, ephemeral: true);
            }
            return;
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
                            "• `/gptcli instruction add text:\"...\"`",
                            "• `/gptcli instruction list`",
                            "• `/gptcli instruction get index:<n>`",
                            "• `/gptcli instruction delete index:<n>`",
                            "• `/gptcli instruction clear`",
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
                            "• `/gptcli infobot leaderboard` — leaderboard",
                            "• `/gptcli infobot personality prompt:\"...\"`",
                            "",
                            "**Reactions**",
                            "• 📌 add message as instruction",
                            "• 💾 save message as embed",
                            "• 🧹 delete image embeds for that message",
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
                        if (subOption == null)
                        {
                            responses.Add("Specify an instruction command.");
                            break;
                        }

                        switch (subOption.Name)
                        {
                            case "add":
                            {
                                var instruction = subOption.Options?.FirstOrDefault(o => o.Name == "text")?.Value?.ToString()
                                                  ?? subOption.Value?.ToString()
                                                  ?? option.Value?.ToString();
                                if (!string.IsNullOrWhiteSpace(instruction))
                                {
                                    chatBot.InstructionChat.AddInstruction(new(StaticValues.ChatMessageRoles.System, instruction));
                                    responses.Add("Instruction added.");
                                }
                                else
                                {
                                    responses.Add("Provide instruction text.");
                                }
                                break;
                            }
                            case "list":
                            {
                                var instructions = chatBot.InstructionChat.ChatBotState.Instructions;
                                if (instructions == null || instructions.Count == 0)
                                {
                                    responses.Add("No instructions stored.");
                                    break;
                                }

                                var lines = instructions
                                    .Select((inst, index) => $"{index + 1}. {inst.Content}")
                                    .ToList();
                                responses.Add($"Instructions:\n{string.Join("\n", lines)}");
                                break;
                            }
                            case "get":
                            {
                                var indexValue = subOption.Options?.FirstOrDefault(o => o.Name == "index")?.Value;
                                if (indexValue is not long indexLong)
                                {
                                    responses.Add("Provide instruction index.");
                                    break;
                                }

                                var instructions = chatBot.InstructionChat.ChatBotState.Instructions;
                                var index = (int)indexLong - 1;
                                if (instructions == null || index < 0 || index >= instructions.Count)
                                {
                                    responses.Add("Instruction index out of range.");
                                    break;
                                }

                                responses.Add($"{index + 1}. {instructions[index].Content}");
                                break;
                            }
                            case "delete":
                            {
                                var indexValue = subOption.Options?.FirstOrDefault(o => o.Name == "index")?.Value;
                                if (indexValue is not long indexLong)
                                {
                                    responses.Add("Provide instruction index.");
                                    break;
                                }

                                var instructions = chatBot.InstructionChat.ChatBotState.Instructions;
                                var index = (int)indexLong - 1;
                                if (instructions == null || index < 0 || index >= instructions.Count)
                                {
                                    responses.Add("Instruction index out of range.");
                                    break;
                                }

                                chatBot.InstructionChat.RemoveInstruction(index);
                                responses.Add($"Instruction {index + 1} deleted.");
                                break;
                            }
                            case "clear":
                                chatBot.InstructionChat.ClearInstructions();
                                responses.Add("Instructions cleared.");
                                break;
                            default:
                                responses.Add($"Unknown instruction command: {subOption.Name}");
                                break;
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
                            default:
                                responses.Add($"Unknown setting: {subOption.Name}");
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

        var responseText = string.Join("\n", responses);
        await SendEphemeralResponseAsync(command, responseText);

        await SaveCachedChannelState(channel.Id);
    }

    InstructionGPT.ChannelState IDiscordModuleHost.GetOrCreateChannelState(IChannel channel)
    {
        return ChannelBots.GetOrAdd(channel.Id, _ => InitializeChannel(channel));
    }

    InstructionGPT.ChannelState IDiscordModuleHost.GetOrCreateChannelState(ulong channelId)
    {
        return ChannelBots.GetOrAdd(channelId, _ => InitializeChannel(channelId));
    }

    void IDiscordModuleHost.EnsureChannelStateMetadata(ChannelState state, IGuildChannel guildChannel)
    {
        EnsureChannelStateMetadata(state, guildChannel);
    }

    ulong IDiscordModuleHost.GetGuildId(IMessageChannel channel)
    {
        return GetGuildId(channel);
    }

    bool IDiscordModuleHost.IsChannelGuildMatch(ChannelState state, IMessageChannel channel, string context)
    {
        return IsChannelGuildMatch(state, channel, context);
    }

    Task IDiscordModuleHost.SaveCachedChannelStateAsync(ulong channelId)
    {
        return SaveCachedChannelState(channelId);
    }
}
