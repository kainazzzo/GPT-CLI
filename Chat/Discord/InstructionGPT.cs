using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Embeddings;
using GPT.CLI.Chat.Discord.Modules;
using Mapster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.SharedModels;

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

        [JsonPropertyName("welcome-state")]
        public WelcomeState Welcome { get; set; }

        [JsonPropertyName("poll-state")]
        public PollState Polls { get; set; }

        [JsonPropertyName("pinboard-state")]
        public PinboardState Pinboard { get; set; }
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

	        // Module-level enable flags keyed by module id (lowercase). Missing key => enabled.
	        [JsonPropertyName("modules-enabled")]
	        public Dictionary<string, bool> ModulesEnabled { get; set; } = new();
	    }

    public record WelcomeState
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("welcome-channel-id")]
        public ulong? WelcomeChannelId { get; set; }

        [JsonPropertyName("welcome-role-id")]
        public ulong? WelcomeRoleId { get; set; }

        [JsonPropertyName("require-onboarding")]
        public bool RequireOnboarding { get; set; }

        [JsonPropertyName("rules-message-id")]
        public ulong? RulesMessageId { get; set; }

        [JsonPropertyName("rules")]
        public List<WelcomeRule> Rules { get; set; } = new();

        [JsonPropertyName("validations")]
        public List<WelcomeValidationRule> Validations { get; set; } = new();

        [JsonPropertyName("users")]
        public Dictionary<ulong, WelcomeUserState> Users { get; set; } = new();
    }

    public record WelcomeRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }
    }

    public record WelcomeValidationRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public record WelcomeUserState
    {
        [JsonPropertyName("completed")]
        public bool Completed { get; set; }

        [JsonPropertyName("completed-validations")]
        public HashSet<string> CompletedValidations { get; set; } = new();

        [JsonPropertyName("last-nudge-utc")]
        public DateTime? LastNudgeUtc { get; set; }
    }

    public record PollState
    {
        [JsonPropertyName("next-id")]
        public int NextId { get; set; } = 1;

        [JsonPropertyName("polls")]
        public List<PollEntry> Polls { get; set; } = new();
    }

    public record PollEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("question")]
        public string Question { get; set; }

        [JsonPropertyName("options")]
        public List<string> Options { get; set; } = new();

        [JsonPropertyName("votes")]
        public Dictionary<ulong, int> Votes { get; set; } = new();

        [JsonPropertyName("open")]
        public bool Open { get; set; } = true;

        [JsonPropertyName("created-by")]
        public ulong CreatedBy { get; set; }

        [JsonPropertyName("created-utc")]
        public DateTime CreatedUtc { get; set; }
    }

    public record PinboardState
    {
        [JsonPropertyName("pins")]
        public List<PinboardEntry> Pins { get; set; } = new();
    }

    public record PinboardEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("channel-id")]
        public ulong ChannelId { get; set; }

        [JsonPropertyName("message-id")]
        public ulong MessageId { get; set; }

        [JsonPropertyName("author-id")]
        public ulong AuthorId { get; set; }

        [JsonPropertyName("snippet")]
        public string Snippet { get; set; }

        [JsonPropertyName("note")]
        public string Note { get; set; }

        [JsonPropertyName("created-utc")]
        public DateTime CreatedUtc { get; set; }
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
	        try
	        {
	            var loaded = _modulePipeline?.DiscoveryReport?.LoadedModuleIds ?? new List<string>();
	            await Console.Out.WriteLineAsync(
	                $"Module pipeline: path={_modulePipeline?.DiscoveryReport?.ModulesPath ?? modulesPath}, loaded={loaded.Count} [{string.Join(", ", loaded)}]");
	        }
	        catch
	        {
	            // best-effort diagnostics only
	        }
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
		        var functions = GetAllGptCliFunctions();
		        try
		        {
		            var coreCount = functions.Count(f => f != null && string.IsNullOrWhiteSpace(f.ModuleId));
		            var moduleCount = functions.Count - coreCount;
		            var groups = functions
		                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.ModuleId))
		                .GroupBy(f => f.ModuleId, StringComparer.OrdinalIgnoreCase)
		                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
		                .Select(g => $"{g.Key}:{g.Count()}")
		                .ToList();

		            var modulesSummary = groups.Count == 0 ? "(none)" : string.Join(", ", groups);
		            await Console.Out.WriteLineAsync($"GptCliFunctions: total={functions.Count}, core={coreCount}, module={moduleCount} [{modulesSummary}]");
		        }
		        catch (Exception ex)
		        {
		            await Console.Out.WriteLineAsync($"GptCliFunctions summary failed: {ex.GetType().Name} {ex.Message}");
		        }

		        var options = BuildGptCliSlashOptions(functions);

		        var command = new SlashCommandBuilder()
		            .WithName("gptcli")
		            .WithDescription("GPT-CLI commands")
		            .AddOptions(options.ToArray());

		        await DumpBuiltCommandTree(command);

		        DiscordRestClient restClient = Client.Rest;

	        var builtCommand = command.Build();
	        try
	        {
	            var top = builtCommand.Options.IsSpecified ? builtCommand.Options.Value : new List<ApplicationCommandOptionProperties>();
	            var setOpt = top.FirstOrDefault(o => string.Equals(o.Name, "set", StringComparison.OrdinalIgnoreCase));
	            var setInner = setOpt?.Options ?? new List<ApplicationCommandOptionProperties>();
	            var setNames = setInner.Select(o => o.Name).ToList();
	            await Console.Out.WriteLineAsync($"gptcli build -> topLevel={top.Count}, setOptions={setNames.Count} ({string.Join(", ", setNames)})");
	        }
	        catch (Exception ex)
	        {
	            await Console.Out.WriteLineAsync($"gptcli build -> failed to dump options: {ex.GetType().Name} {ex.Message}");
	        }

        var forceGlobalCommands = string.Equals(
            Configuration["GPT:ForceGlobalCommands"],
            "true",
            StringComparison.OrdinalIgnoreCase);

	        // Discord global slash command updates are not immediate. If the bot is only in a single guild
	        // and no explicit guild id is configured, prefer registering guild commands for fast iteration.
	        var effectiveGuildId = DefaultParameters.DiscordGuildId;
	        if (!forceGlobalCommands && !effectiveGuildId.HasValue)
	        {
	            var guilds = Client.Guilds;
	            if (guilds is { Count: 1 })
	            {
	                effectiveGuildId = guilds.First().Id;
	                await Console.Out.WriteLineAsync(
	                    $"No GPT:DiscordGuildId configured; registering guild commands for single guild {effectiveGuildId.Value} (set GPT:ForceGlobalCommands=true to force global).");
	            }
	        }

	        if (effectiveGuildId.HasValue)
	        {
	            var guild = await restClient.GetGuildAsync(effectiveGuildId.Value);
	            if (guild == null)
	            {
	                throw new Exception($"Guild {effectiveGuildId.Value} not found.");
	            }

	            await Console.Out.WriteLineAsync($"Overwriting guild commands for {effectiveGuildId.Value}...");
	            // Ensure updates always replace existing guild commands.
	            await guild.BulkOverwriteApplicationCommandsAsync(new[] { builtCommand });
	            await DumpGuildCommands(guild);
	            return;
	        }

	        if (!forceGlobalCommands)
	        {
	            // If we don't have an explicit guild id, prefer overwriting guild commands for all joined guilds
	            // to keep iteration fast. Global command propagation can take a long time.
	            var guildIds = Client.Guilds.Select(g => g.Id).Distinct().ToList();
	            var maxGuildOverwrites = 100;
	            if (int.TryParse(Configuration["GPT:MaxGuildCommandOverwrites"], out var configuredMax) && configuredMax > 0)
	            {
	                maxGuildOverwrites = configuredMax;
	            }
	            if (guildIds.Count > 0 && guildIds.Count <= maxGuildOverwrites)
	            {
	                await Console.Out.WriteLineAsync($"No GPT:DiscordGuildId configured; overwriting guild commands for {guildIds.Count} guild(s) for fast iteration.");
		                foreach (var guildId in guildIds)
		                {
		                    var guild = await restClient.GetGuildAsync(guildId);
		                    if (guild == null)
		                    {
		                        continue;
		                    }

		                    await Console.Out.WriteLineAsync($"Overwriting guild commands for {guildId}...");
		                    await guild.BulkOverwriteApplicationCommandsAsync(new[] { builtCommand });
		                    await DumpGuildCommands(guild);
		                }

	                return;
	            }

	            if (guildIds.Count > maxGuildOverwrites)
	            {
	                await Console.Out.WriteLineAsync(
	                    $"Bot is in {guildIds.Count} guilds and GPT:DiscordGuildId is not configured; falling back to global slash command registration. " +
	                    "Set GPT:DiscordGuildId to get immediate updates.");
	            }
	        }

	        await Console.Out.WriteLineAsync("Overwriting global commands...");
	        // Ensure updates always replace existing global commands.
	        await ((IDiscordClient)restClient).BulkOverwriteGlobalApplicationCommand(new[] { builtCommand });
	        await DumpGlobalCommands((IDiscordClient)restClient);
	    }

    private static async Task DumpBuiltCommandTree(SlashCommandBuilder command)
    {
        if (command == null)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Built slash command tree:");
            sb.AppendLine($"/{command.Name} (slash) - {command.Description}");

            if (command.Options is { Count: > 0 })
            {
                foreach (var opt in command.Options)
                {
                    AppendSlashOptionBuilderTree(sb, opt, 1);
                }
            }
            else
            {
                sb.AppendLine("  (no options)");
            }

            await Console.Out.WriteLineAsync(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"Built slash command tree dump failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void AppendSlashOptionBuilderTree(StringBuilder sb, SlashCommandOptionBuilder opt, int depth)
    {
        if (sb == null || opt == null)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        var required = opt.IsRequired == true ? " required" : "";
        sb.AppendLine($"{indent}- {opt.Name} ({opt.Type}){required} - {opt.Description}");

        if (opt.Options is { Count: > 0 })
        {
            foreach (var child in opt.Options)
            {
                AppendSlashOptionBuilderTree(sb, child, depth + 1);
            }
        }
    }

    private static async Task DumpGuildCommands(IGuild guild)
    {
        var commands = await guild.GetApplicationCommandsAsync();
        await Console.Out.WriteLineAsync($"Guild commands ({guild.Id}): {string.Join(", ", commands.Select(c => c.Name))}");

        foreach (var command in commands)
        {
            if (!string.Equals(command.Name, "gptcli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Registered slash command tree for guild {guild.Id}:");
            sb.AppendLine($"/{command.Name} (id={command.Id}, type={command.Type}) - {command.Description}");

            if (command.Options is { Count: > 0 })
            {
                foreach (var opt in command.Options)
                {
                    AppendRegisteredOptionTree(sb, opt, 1);
                }
            }
            else
            {
                sb.AppendLine("  (no options)");
            }

            await Console.Out.WriteLineAsync(sb.ToString().TrimEnd());
        }
    }

    private static async Task DumpGlobalCommands(IDiscordClient client)
    {
        var commands = await client.GetGlobalApplicationCommandsAsync();
        await Console.Out.WriteLineAsync($"Global commands: {string.Join(", ", commands.Select(c => c.Name))}");
        foreach (var command in commands)
        {
            if (!string.Equals(command.Name, "gptcli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Registered global slash command tree:");
            sb.AppendLine($"/{command.Name} (id={command.Id}, type={command.Type}) - {command.Description}");

            if (command.Options is { Count: > 0 })
            {
                foreach (var opt in command.Options)
                {
                    AppendRegisteredOptionTree(sb, opt, 1);
                }
            }
            else
            {
                sb.AppendLine("  (no options)");
            }

            await Console.Out.WriteLineAsync(sb.ToString().TrimEnd());
        }
    }

    private static void AppendRegisteredOptionTree(StringBuilder sb, IApplicationCommandOption opt, int depth)
    {
        if (sb == null || opt == null)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        var required = opt.IsRequired == true ? " required" : "";
        sb.AppendLine($"{indent}- {opt.Name} ({opt.Type}){required} - {opt.Description}");

        if (opt.Choices is { Count: > 0 })
        {
            var choices = string.Join(", ", opt.Choices.Select(c => c.Name));
            sb.AppendLine($"{indent}  choices: {choices}");
        }

        if (opt.Options is { Count: > 0 })
        {
            foreach (var child in opt.Options)
            {
                AppendRegisteredOptionTree(sb, child, depth + 1);
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

        // Mention-based /gptcli set tool routing (LLM function calling).
        if (shouldRespond && !string.IsNullOrWhiteSpace(message.Content))
        {
            var stripped = StripBotMentions(message.Content, Client.CurrentUser.Id).Trim();
            var handled = await TryHandleMentionedToolCallsAsync(message, channel, stripped);
            if (handled)
            {
                await SaveCachedChannelState(message.Channel.Id);
                return;
            }
        }
       
        if (channel.InstructionChat.ChatBotState.PrimeDirectives.Count != _defaultPrimeDirective.Count || channel.InstructionChat.ChatBotState.PrimeDirectives[0].Content != _defaultPrimeDirective[0].Content)
        {
            channel.InstructionChat.ChatBotState.PrimeDirectives = PrimeDirective.ToList();
        }

        if (!channel.Options.Enabled)
        {
            // When disabled, only allow mention-based settings changes (handled above).
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

	    private static string StripBotMentions(string content, ulong botUserId)
	    {
	        if (string.IsNullOrWhiteSpace(content))
	        {
	            return string.Empty;
        }

        // Discord user mentions appear as <@id> or <@!id>
        var id = botUserId.ToString(CultureInfo.InvariantCulture);
        return content
            .Replace($"<@{id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
	            .Replace($"<@!{id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
	            .Trim();
	    }

	    private static string NormalizeModuleId(string moduleId)
	    {
	        return (moduleId ?? string.Empty).Trim().ToLowerInvariant();
	    }

	    public static bool IsModuleEnabled(ChannelState channelState, string moduleId)
	    {
	        if (channelState?.Options == null)
	        {
	            return true;
	        }

	        var key = NormalizeModuleId(moduleId);
	        if (string.IsNullOrWhiteSpace(key))
	        {
	            return true;
	        }

	        channelState.Options.ModulesEnabled ??= new Dictionary<string, bool>();

	        // Modules listed here are "loaded but off" until explicitly enabled.
	        var defaultDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	        {
	            "dnd"
	        };

	        // Back-compat: casino was historically stored as a dedicated flag.
	        if (string.Equals(key, "casino", StringComparison.OrdinalIgnoreCase) &&
	            !channelState.Options.ModulesEnabled.ContainsKey(key))
	        {
	            return channelState.Options.CasinoEnabled;
	        }

	        if (!channelState.Options.ModulesEnabled.TryGetValue(key, out var enabled))
	        {
	            return !defaultDisabled.Contains(key);
	        }

	        return enabled;
	    }

	    public static void SetModuleEnabled(ChannelState channelState, string moduleId, bool enabled)
	    {
	        if (channelState?.Options == null)
	        {
	            return;
	        }

	        var key = NormalizeModuleId(moduleId);
	        if (string.IsNullOrWhiteSpace(key))
	        {
	            return;
	        }

	        channelState.Options.ModulesEnabled ??= new Dictionary<string, bool>();
	        channelState.Options.ModulesEnabled[key] = enabled;

	        if (string.Equals(key, "casino", StringComparison.OrdinalIgnoreCase))
	        {
	            channelState.Options.CasinoEnabled = enabled;
	        }
	    }

	    private static bool IsFunctionAvailable(ChannelState channelState, GptCliFunction fn)
	    {
	        if (fn == null)
	        {
	            return false;
	        }

	        if (string.IsNullOrWhiteSpace(fn.ModuleId))
	        {
	            return true;
	        }

	        if (fn.ExposeWhenModuleDisabled)
	        {
	            return true;
	        }

	        return IsModuleEnabled(channelState, fn.ModuleId);
	    }


	    private async Task<bool> TryHandleMentionedToolCallsAsync(SocketMessage message, ChannelState channelState, string strippedContent)
	    {
	        var requestId = Guid.NewGuid().ToString("n")[..8];
	        try
	        {
	            await Console.Out.WriteLineAsync(
	                $"[tools:{requestId}] mention-tool-router start channel={message.Channel.Id} user={message.Author.Id} model={channelState?.InstructionChat?.ChatBotState?.Parameters?.Model} text={NormalizeSingleLine(strippedContent)}");
	        }
	        catch
	        {
	            // ignore
	        }

	        // Real function-calling: we always offer tools when tagged/DM'd.
	        // If the model chooses not to call any tool, we fall through to normal chat.
	        var allFunctions = GetAllGptCliFunctions()
	            .Where(f => f?.ExecuteAsync != null && !string.IsNullOrWhiteSpace(f.ToolName))
	            .ToList();

	        if (allFunctions.Count == 0)
	        {
	            return false;
	        }

	        // Hide disabled module tools from the model (except module enable/disable functions).
	        var availableFunctions = allFunctions
	            .Where(f => IsFunctionAvailable(channelState, f))
	            .ToList();

	        if (availableFunctions.Count == 0)
	        {
	            try { await Console.Out.WriteLineAsync($"[tools:{requestId}] no available tools (all disabled?)"); } catch { }
	            return false;
	        }

	        var tools = availableFunctions.Select(f => f.ToToolDefinition()).ToList();
	        try
	        {
	            var moduleCounts = availableFunctions
	                .GroupBy(f => string.IsNullOrWhiteSpace(f.ModuleId) ? "core" : f.ModuleId.Trim().ToLowerInvariant())
	                .Select(g => $"{g.Key}={g.Count()}")
	                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
	            await Console.Out.WriteLineAsync($"[tools:{requestId}] offering tools total={availableFunctions.Count} [{string.Join(", ", moduleCounts)}]");
	        }
	        catch
	        {
	            // ignore
	        }

	        var byName = new Dictionary<string, GptCliFunction>(StringComparer.OrdinalIgnoreCase);
	        foreach (var f in allFunctions)
	        {
	            if (f != null && !string.IsNullOrWhiteSpace(f.ToolName) && !byName.ContainsKey(f.ToolName))
	            {
	                byName[f.ToolName] = f;
	            }
	        }

	        var system = string.Join("\n", new[]
	        {
	            "You are a router for GPT-CLI Discord slash commands.",
	            "If (and only if) the user is asking to perform a GPT-CLI slash command, call the appropriate tool(s).",
	            "If the user is not asking to perform a slash command, do not call any tools and let the normal chat response happen.",
	            "Never explain your reasoning."
	        });

	        var request = new ChatCompletionCreateRequest
	        {
	            Model = channelState.InstructionChat.ChatBotState.Parameters.Model,
	            Temperature = 0,
	            MaxTokens = null,
	            MaxCompletionTokens = null,
	            ParallelToolCalls = false,
	            Messages = new List<ChatMessage>
	            {
	                new(StaticValues.ChatMessageRoles.System, system),
	                new(StaticValues.ChatMessageRoles.User, strippedContent)
	            },
	            Tools = tools,
	            ToolChoice = new ToolChoice { Type = "auto" }
	        };

	        ApplyModelTokenLimit(request, request.Model, maxTokens: 200);

	        var response = await OpenAILogic.CreateChatCompletionAsync(request);
	        if (!response.Successful)
	        {
	            await Console.Out.WriteLineAsync($"LLM tools failed: {response.Error?.Code} {response.Error?.Message}");
	            try { await Console.Out.WriteLineAsync($"[tools:{requestId}] router failed code={response.Error?.Code} msg={response.Error?.Message}"); } catch { }
	            return false;
	        }

	        var msg = response.Choices.FirstOrDefault()?.Message;
	        if (msg == null)
	        {
	            try { await Console.Out.WriteLineAsync($"[tools:{requestId}] router returned empty message"); } catch { }
	            return false;
	        }

	        var toolCalls = msg.ToolCalls;
	        if (toolCalls == null || toolCalls.Count == 0)
	        {
	            // Back-compat: older function_call field.
	            if (msg.FunctionCall == null)
	            {
	                try { await Console.Out.WriteLineAsync($"[tools:{requestId}] no tool calls; falling back to normal chat"); } catch { }
	                return false;
	            }

	            toolCalls = new List<ToolCall>
	            {
	                new() { Type = "function", FunctionCall = msg.FunctionCall }
	            };
	        }

	        var applied = new List<string>();
	        var errors = new List<string>();

	        try
	        {
	            var planned = toolCalls
	                .Select(c => c?.FunctionCall?.Name)
	                .Where(n => !string.IsNullOrWhiteSpace(n))
	                .ToList();
	            await Console.Out.WriteLineAsync($"[tools:{requestId}] tool calls count={planned.Count} [{string.Join(", ", planned)}]");
	        }
	        catch
	        {
	            // ignore
	        }

	        foreach (var call in toolCalls)
	        {
	            var toolFn = call?.FunctionCall;
	            if (toolFn == null || string.IsNullOrWhiteSpace(toolFn.Name))
	            {
	                continue;
	            }

	            if (!byName.TryGetValue(toolFn.Name, out var match) || match == null)
	            {
	                errors.Add($"Unknown tool '{toolFn.Name}'.");
	                try { await Console.Out.WriteLineAsync($"[tools:{requestId}] unknown tool name={toolFn.Name}"); } catch { }
	                continue;
	            }

	            if (!IsFunctionAvailable(channelState, match))
	            {
	                errors.Add($"Tool '{toolFn.Name}' is unavailable because module '{match.ModuleId}' is disabled.");
	                try { await Console.Out.WriteLineAsync($"[tools:{requestId}] tool unavailable name={toolFn.Name} module={match.ModuleId}"); } catch { }
	                continue;
	            }

		            var argsJson = string.IsNullOrWhiteSpace(toolFn.Arguments) ? "{}" : toolFn.Arguments;
		            try
		            {
		                await Console.Out.WriteLineAsync($"[tools:{requestId}] executing tool={match.ToolName} slash={match.Slash?.TopLevelName}/{match.Slash?.SubCommandName ?? match.Slash?.SetOptionName ?? ""} module={match.ModuleId ?? "core"} args={NormalizeSingleLine(argsJson)}");
		            }
		            catch
		            {
		                // ignore
		            }
	            var ctx = new GptCliExecutionContext(_moduleContext, channelState, message.Channel, message.Author, null, message);
	            GptCliExecutionResult result;
	            try
	            {
	                result = await match.ExecuteAsync(ctx, argsJson, _shutdownToken);
	            }
	            catch (Exception ex)
	            {
	                result = new GptCliExecutionResult(true, $"Tool '{match.ToolName}' failed: {ex.GetType().Name} {ex.Message}", false);
	                try { await Console.Out.WriteLineAsync($"[tools:{requestId}] tool threw tool={match.ToolName} ex={ex.GetType().Name} msg={ex.Message}"); } catch { }
	            }
	            if (result is { Handled: true } && !string.IsNullOrWhiteSpace(result.Response))
	            {
	                applied.Add(result.Response.Trim());
	            }
	        }

	        if (applied.Count == 0)
	        {
	            return false;
	        }

	        var replyLines = new List<string> { $"<@{message.Author.Id}> OK:" };
	        if (applied.Count == 1)
	        {
	            replyLines.Add(applied[0]);
	        }
	        else
	        {
	            replyLines.AddRange(applied.Select(line => $"- {line}"));
	        }

	        if (errors.Count > 0)
	        {
	            replyLines.Add("Some requests were ignored:");
	            replyLines.AddRange(errors.Take(10).Select(err => $"- {err}"));
	        }

	        await message.Channel.SendMessageAsync(string.Join("\n", replyLines));
	        try { await Console.Out.WriteLineAsync($"[tools:{requestId}] applied={applied.Count} errors={errors.Count}"); } catch { }
	        return true;
	    }

    public static string BuildDiscordMessageLink(ChannelState channel, ulong messageId)
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
            CasinoBalances = new(),
            Welcome = new(),
            Polls = new(),
            Pinboard = new()
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

    public static string GetChannelDirectory(ChannelState channelState)
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
	            MaxTokens = null,
	            MaxCompletionTokens = null
	        };
	        ApplyModelTokenLimit(
	            request,
	            visionModel,
	            ResolveVisionDescriptionMaxTokens(channelState.InstructionChat.ChatBotState.Parameters));

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

	        request.MaxTokens = null;
	        request.MaxCompletionTokens = null;
	        ApplyModelTokenLimit(request, modelOverride, ClampVisionMaxTokens(parameters.MaxTokens));
	    }

	    private static void ApplyModelTokenLimit(ChatCompletionCreateRequest request, string modelName, int? maxTokens)
	    {
	        if (request == null || !maxTokens.HasValue)
	        {
	            return;
	        }

	        // Prefer `max_completion_tokens` for chat completions to avoid model-specific rejections of `max_tokens`.
	        request.MaxTokens = null;
	        request.MaxCompletionTokens = maxTokens.Value;
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
	        var functions = GetAllGptCliFunctions();
	        await ExecuteGptCliSlashAsync(command, _moduleContext, chatBot, functions, responses, _shutdownToken);

        if (responses.Count == 0)
        {
            responses.Add("No changes made.");
        }

        await SendEphemeralResponseAsync(command, string.Join("\n", responses));
        await SaveCachedChannelState(channel.Id);
    }

    private IReadOnlyList<GptCliFunction> GetAllGptCliFunctions()
    {
        var list = new List<GptCliFunction>();
        list.AddRange(BuildCoreGptCliFunctions());
        if (_modulePipeline != null)
        {
            list.AddRange(_modulePipeline.GetGptCliFunctions());
        }

        // Deduplicate by tool name; first wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return list.Where(fn => fn != null && !string.IsNullOrWhiteSpace(fn.ToolName) && seen.Add(fn.ToolName)).ToList();
    }

    private IReadOnlyList<GptCliFunction> BuildCoreGptCliFunctions()
    {
        // Keep tool names stable.
	        return new List<GptCliFunction>
	        {
            new()
            {
                ToolName = "gptcli_help",
                Description = "Show GPT-CLI help",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SubCommand, "help", "Help"),
	                ExecuteAsync = async (ctx, argsJson, ct) =>
	                {
	                    await Task.Yield();
	                    var functions = GetAllGptCliFunctions();
	                    return new GptCliExecutionResult(true, BuildHelpText(ctx?.ChannelState, functions), false);
	                }
	            },
	            new()
	            {
	                ToolName = "gptcli_modules",
	                Description = "List loaded feature modules and module loading diagnostics",
	                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SubCommand, "modules", "Modules"),
	                ExecuteAsync = async (ctx, argsJson, ct) =>
	                {
	                    await Task.Yield();
	                    return new GptCliExecutionResult(true, BuildModulesText(ctx?.ChannelState), false);
	                }
	            },
            new()
            {
                ToolName = "gptcli_clear",
                Description = "Clear stored messages/instructions for this channel",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SubCommand, "clear", "Clear"),
                Parameters = new[]
                {
                    new GptCliParamSpec(
                        "target",
                        GptCliParamType.String,
                        "messages, instructions, or all",
                        Required: true,
                        Choices: new[]
                        {
                            new GptCliParamChoice("messages", "messages"),
                            new GptCliParamChoice("instructions", "instructions"),
                            new GptCliParamChoice("all", "all")
                        })
                },
                ExecuteAsync = ExecuteClearAsync
            },
            new()
            {
                ToolName = "gptcli_instruction_add",
                Description = "Add a system instruction",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "instruction", "Instructions", "add"),
                Parameters = new[]
                {
                    new GptCliParamSpec("text", GptCliParamType.String, "Instruction text", Required: true)
                },
                ExecuteAsync = ExecuteInstructionAddAsync
            },
            new()
            {
                ToolName = "gptcli_instruction_list",
                Description = "List current instructions",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "instruction", "Instructions", "list"),
                ExecuteAsync = ExecuteInstructionListAsync
            },
            new()
            {
                ToolName = "gptcli_instruction_get",
                Description = "Get an instruction by index (1-based)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "instruction", "Instructions", "get"),
                Parameters = new[]
                {
                    new GptCliParamSpec("index", GptCliParamType.Integer, "1-based instruction index", Required: true, MinInt: 1)
                },
                ExecuteAsync = ExecuteInstructionGetAsync
            },
            new()
            {
                ToolName = "gptcli_instruction_delete",
                Description = "Delete an instruction by index (1-based)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "instruction", "Instructions", "delete"),
                Parameters = new[]
                {
                    new GptCliParamSpec("index", GptCliParamType.Integer, "1-based instruction index", Required: true, MinInt: 1)
                },
                ExecuteAsync = ExecuteInstructionDeleteAsync
            },
            new()
            {
                ToolName = "gptcli_instruction_clear",
                Description = "Clear all instructions",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "instruction", "Instructions", "clear"),
                ExecuteAsync = ExecuteInstructionClearAsync
            },

            BuildCoreSetOption("enabled", GptCliParamType.Boolean, "Enable or disable the chat bot in this channel.", ExecuteSetEnabledAsync),
            BuildCoreSetOption("mute", GptCliParamType.Boolean, "Mute or unmute the chat bot in this channel.", ExecuteSetMuteAsync),
            BuildCoreSetOption("response-mode", GptCliParamType.String, "Set the response mode (All|Matches).", ExecuteSetResponseModeAsync,
                choices: new[] { new GptCliParamChoice("All", "All"), new GptCliParamChoice("Matches", "Matches") }),
            BuildCoreSetOption("embed-mode", GptCliParamType.String, "Set the embed mode (Explicit|All).", ExecuteSetEmbedModeAsync,
                choices: new[] { new GptCliParamChoice("Explicit", "Explicit"), new GptCliParamChoice("All", "All") }),
            BuildCoreSetOption("max-chat-history-length", GptCliParamType.Integer, "Set the maximum chat history length.", ExecuteSetMaxChatHistoryAsync, minInt: 100),
            BuildCoreSetOption("max-tokens", GptCliParamType.Integer, "Set the maximum tokens.", ExecuteSetMaxTokensAsync, minInt: 50),
            BuildCoreSetOption("model", GptCliParamType.String, "Set the model name.", ExecuteSetModelAsync)
        };
    }

    private GptCliFunction BuildCoreSetOption(
        string optionName,
        GptCliParamType type,
        string description,
        Func<GptCliExecutionContext, string, CancellationToken, Task<GptCliExecutionResult>> executor,
        int? minInt = null,
        IReadOnlyList<GptCliParamChoice> choices = null)
    {
        return new GptCliFunction
        {
            ToolName = $"gptcli_set_{optionName.Replace("-", "_")}",
            Description = description,
            Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SetOption, "set", "Settings", SetOptionName: optionName),
            Parameters = new[]
            {
                new GptCliParamSpec("value", type, "Value", Required: true, MinInt: minInt, Choices: choices)
            },
            ExecuteAsync = executor
        };
    }

	    private static async Task ExecuteGptCliSlashAsync(
	        SocketSlashCommand command,
	        DiscordModuleContext moduleContext,
	        ChannelState channelState,
	        IReadOnlyList<GptCliFunction> functions,
	        List<string> responses,
	        CancellationToken cancellationToken)
	    {
        static string FormatOptions(IReadOnlyCollection<SocketSlashCommandDataOption> opts)
        {
            if (opts == null || opts.Count == 0)
            {
                return "(none)";
            }

            var parts = new List<string>();
            foreach (var o in opts)
            {
                if (o == null)
                {
                    continue;
                }

                if (o.Options != null && o.Options.Count > 0)
                {
                    parts.Add($"{o.Name}:{o.Type}{{{FormatOptions(o.Options)}}}");
                }
                else if (o.Value != null)
                {
                    parts.Add($"{o.Name}:{o.Type}={NormalizeSingleLine(o.Value.ToString())}");
                }
                else
                {
                    parts.Add($"{o.Name}:{o.Type}");
                }
            }

            return string.Join(", ", parts);
        }

        var requestId = Guid.NewGuid().ToString("n")[..8];
        try
        {
            await Console.Out.WriteLineAsync(
                $"[slash:{requestId}] start guild={(channelState?.GuildId ?? 0)} channel={(command?.Channel?.Id ?? 0)} user={(command?.User?.Id ?? 0)} name=/{command?.Data?.Name} opts={FormatOptions(command?.Data?.Options)}");
        }
        catch
        {
            // ignore
        }

        if (command?.Data?.Options == null || command.Data.Options.Count == 0)
        {
            return;
        }

        var bySubCommand = functions
            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.SubCommand)
            .ToDictionary(f => f.Slash.TopLevelName, StringComparer.OrdinalIgnoreCase);

        var byGroup = functions
            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.GroupSubCommand)
            .ToDictionary(f => $"{f.Slash.TopLevelName}/{f.Slash.SubCommandName}", StringComparer.OrdinalIgnoreCase);

        var setOptions = functions
            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.SetOption)
            .ToDictionary(f => f.Slash.SetOptionName, StringComparer.OrdinalIgnoreCase);

        foreach (var option in command.Data.Options)
        {
            if (option == null)
            {
                continue;
            }

            if (option.Type == ApplicationCommandOptionType.SubCommand)
            {
	                if (string.Equals(option.Name, "set", StringComparison.OrdinalIgnoreCase))
	                {
	                    foreach (var setOpt in option.Options ?? Enumerable.Empty<SocketSlashCommandDataOption>())
	                    {
                        if (setOpt?.Value == null)
                        {
                            continue;
                        }

	                        if (!setOptions.TryGetValue(setOpt.Name, out var fn))
	                        {
                            try { await Console.Out.WriteLineAsync($"[slash:{requestId}] unknown set option name={setOpt.Name}"); } catch { }
	                            continue;
	                        }

	                        if (!IsFunctionAvailable(channelState, fn))
	                        {
	                            responses.Add($"Module '{fn.ModuleId}' is disabled. Enable it via `/gptcli set {fn.ModuleId} true`.");
                            try { await Console.Out.WriteLineAsync($"[slash:{requestId}] set blocked tool={fn.ToolName} module={fn.ModuleId}"); } catch { }
	                            continue;
	                        }

	                        var paramName = fn.Parameters?.FirstOrDefault()?.Name ?? "value";
	                        var argsJson = BuildJsonObject(new Dictionary<string, object> { [paramName] = setOpt.Value });
	                        var ctx = new GptCliExecutionContext(moduleContext, channelState, command.Channel, command.User, command, null);
                            try { await Console.Out.WriteLineAsync($"[slash:{requestId}] executing tool={fn.ToolName} slash=set/{fn.Slash?.SetOptionName} module={fn.ModuleId ?? "core"} args={NormalizeSingleLine(argsJson)}"); } catch { }
	                        GptCliExecutionResult result;
                            try
                            {
                                result = await fn.ExecuteAsync(ctx, argsJson, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                result = new GptCliExecutionResult(true, $"Tool '{fn.ToolName}' failed: {ex.GetType().Name} {ex.Message}", false);
                                try { await Console.Out.WriteLineAsync($"[slash:{requestId}] tool threw tool={fn.ToolName} ex={ex.GetType().Name} msg={ex.Message}"); } catch { }
                            }
	                        if (result is { Handled: true } && !string.IsNullOrWhiteSpace(result.Response))
	                        {
	                            responses.Add(result.Response.Trim());
	                        }
                    }

                    continue;
                }

	                if (bySubCommand.TryGetValue(option.Name, out var subFn))
	                {
	                    if (!IsFunctionAvailable(channelState, subFn))
	                    {
	                        responses.Add($"Module '{subFn.ModuleId}' is disabled. Enable it via `/gptcli set {subFn.ModuleId} true`.");
                            try { await Console.Out.WriteLineAsync($"[slash:{requestId}] subcommand blocked tool={subFn.ToolName} module={subFn.ModuleId}"); } catch { }
	                        continue;
	                    }

	                    var argsJson = BuildJsonObject(BuildArgsFromSlashOptions(option.Options));
	                    var ctx = new GptCliExecutionContext(moduleContext, channelState, command.Channel, command.User, command, null);
                        try { await Console.Out.WriteLineAsync($"[slash:{requestId}] executing tool={subFn.ToolName} slash={subFn.Slash?.TopLevelName} module={subFn.ModuleId ?? "core"} args={NormalizeSingleLine(argsJson)}"); } catch { }
	                    GptCliExecutionResult result;
                        try
                        {
                            result = await subFn.ExecuteAsync(ctx, argsJson, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            result = new GptCliExecutionResult(true, $"Tool '{subFn.ToolName}' failed: {ex.GetType().Name} {ex.Message}", false);
                            try { await Console.Out.WriteLineAsync($"[slash:{requestId}] tool threw tool={subFn.ToolName} ex={ex.GetType().Name} msg={ex.Message}"); } catch { }
                        }
	                    if (result is { Handled: true } && !string.IsNullOrWhiteSpace(result.Response))
	                    {
	                        responses.Add(result.Response.Trim());
	                    }
	                }
                    else
                    {
                        try { await Console.Out.WriteLineAsync($"[slash:{requestId}] unknown subcommand name={option.Name}"); } catch { }
                    }

                continue;
            }

            if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                var sub = option.Options?.FirstOrDefault();
                if (sub == null)
                {
                    continue;
                }

	                var key = $"{option.Name}/{sub.Name}";
	                if (!byGroup.TryGetValue(key, out var fn))
	                {
                        try { await Console.Out.WriteLineAsync($"[slash:{requestId}] unknown group-subcommand key={key}"); } catch { }
	                    continue;
	                }

	                if (!IsFunctionAvailable(channelState, fn))
	                {
	                    responses.Add($"Module '{fn.ModuleId}' is disabled. Enable it via `/gptcli set {fn.ModuleId} true`.");
                        try { await Console.Out.WriteLineAsync($"[slash:{requestId}] group-subcommand blocked tool={fn.ToolName} module={fn.ModuleId}"); } catch { }
	                    continue;
	                }

	                var argsJson = BuildJsonObject(BuildArgsFromSlashOptions(sub.Options));
	                var ctx = new GptCliExecutionContext(moduleContext, channelState, command.Channel, command.User, command, null);
                    try { await Console.Out.WriteLineAsync($"[slash:{requestId}] executing tool={fn.ToolName} slash={fn.Slash?.TopLevelName}/{fn.Slash?.SubCommandName} module={fn.ModuleId ?? "core"} args={NormalizeSingleLine(argsJson)}"); } catch { }
	                GptCliExecutionResult result;
                    try
                    {
                        result = await fn.ExecuteAsync(ctx, argsJson, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        result = new GptCliExecutionResult(true, $"Tool '{fn.ToolName}' failed: {ex.GetType().Name} {ex.Message}", false);
                        try { await Console.Out.WriteLineAsync($"[slash:{requestId}] tool threw tool={fn.ToolName} ex={ex.GetType().Name} msg={ex.Message}"); } catch { }
                    }
	                if (result is { Handled: true } && !string.IsNullOrWhiteSpace(result.Response))
	                {
	                    responses.Add(result.Response.Trim());
	                }
	            }
	        }
	    }

    private static Dictionary<string, object> BuildArgsFromSlashOptions(IReadOnlyCollection<SocketSlashCommandDataOption> options)
    {
        var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (options == null)
        {
            return args;
        }

        foreach (var opt in options)
        {
            if (opt?.Value == null || string.IsNullOrWhiteSpace(opt.Name))
            {
                continue;
            }

            args[opt.Name] = opt.Value;
        }

        return args;
    }

    private static string BuildJsonObject(Dictionary<string, object> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in values ?? new Dictionary<string, object>())
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                {
                    continue;
                }

                writer.WritePropertyName(kvp.Key);
                WriteJsonValue(writer, kvp.Value);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

	    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
	    {
	        switch (value)
	        {
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case double d:
                writer.WriteNumberValue(d);
                return;
            case float f:
                writer.WriteNumberValue(f);
                return;
            case decimal dec:
                writer.WriteNumberValue(dec);
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case SocketUser user:
                writer.WriteStringValue(user.Id.ToString(CultureInfo.InvariantCulture));
                return;
            case SocketGuildChannel channel:
                writer.WriteStringValue(channel.Id.ToString(CultureInfo.InvariantCulture));
                return;
	            case SocketRole role:
	                writer.WriteStringValue(role.Id.ToString(CultureInfo.InvariantCulture));
	                return;
	            case global::Discord.IAttachment attachment:
	                writer.WriteStringValue(attachment.Url ?? attachment.Filename ?? attachment.Id.ToString(CultureInfo.InvariantCulture));
	                return;
	            default:
	                writer.WriteStringValue(value.ToString());
	                return;
	        }
	    }

	    private List<SlashCommandOptionBuilder> BuildGptCliSlashOptions(IReadOnlyList<GptCliFunction> functions)
	    {
	        var topLevel = new Dictionary<string, SlashCommandOptionBuilder>(StringComparer.OrdinalIgnoreCase);
	        var order = new List<string>();

	        foreach (var fn in functions ?? Array.Empty<GptCliFunction>())
	        {
	            var slash = fn?.Slash;
	            if (slash == null || string.IsNullOrWhiteSpace(slash.TopLevelName))
	            {
	                continue;
	            }

	            switch (slash.Kind)
	            {
	                case GptCliSlashBindingKind.SubCommand:
	                {
	                    var name = slash.TopLevelName.Trim();
	                    if (!topLevel.TryGetValue(name, out var opt))
	                    {
	                        opt = new SlashCommandOptionBuilder()
	                            .WithName(name)
	                            .WithDescription(slash.TopLevelDescription ?? fn.Description ?? name)
	                            .WithType(ApplicationCommandOptionType.SubCommand);
	                        topLevel[name] = opt;
	                        order.Add(name);
	                    }

	                    if (opt.Type != ApplicationCommandOptionType.SubCommand)
	                    {
	                        continue;
	                    }

	                    foreach (var p in fn.Parameters ?? Array.Empty<GptCliParamSpec>())
	                    {
	                        if (string.IsNullOrWhiteSpace(p.Name))
	                        {
	                            continue;
	                        }

	                        var exists = opt.Options?.Any(o => string.Equals(o.Name, p.Name, StringComparison.OrdinalIgnoreCase)) == true;
	                        if (!exists)
	                        {
	                            opt.AddOption(fn.BuildSlashParamOption(p));
	                        }
	                    }

	                    break;
	                }
	                case GptCliSlashBindingKind.GroupSubCommand:
	                {
	                    var groupName = slash.TopLevelName.Trim();
	                    if (!topLevel.TryGetValue(groupName, out var group))
	                    {
	                        group = new SlashCommandOptionBuilder()
	                            .WithName(groupName)
	                            .WithDescription(slash.TopLevelDescription ?? groupName)
	                            .WithType(ApplicationCommandOptionType.SubCommandGroup);
	                        topLevel[groupName] = group;
	                        order.Add(groupName);
	                    }

	                    if (group.Type != ApplicationCommandOptionType.SubCommandGroup || string.IsNullOrWhiteSpace(slash.SubCommandName))
	                    {
	                        continue;
	                    }

	                    var subName = slash.SubCommandName.Trim();
	                    var subExists = group.Options?.Any(o => string.Equals(o.Name, subName, StringComparison.OrdinalIgnoreCase)) == true;
	                    if (!subExists)
	                    {
	                        group.AddOption(fn.BuildSlashSubCommand());
	                    }

	                    break;
	                }
	                case GptCliSlashBindingKind.SetOption:
	                {
	                    var setCommandName = slash.TopLevelName.Trim();
	                    if (!topLevel.TryGetValue(setCommandName, out var set))
	                    {
	                        set = new SlashCommandOptionBuilder()
	                            .WithName(setCommandName)
	                            .WithDescription(slash.TopLevelDescription ?? "Settings")
	                            .WithType(ApplicationCommandOptionType.SubCommand);
	                        topLevel[setCommandName] = set;
	                        order.Add(setCommandName);
	                    }

	                    if (set.Type != ApplicationCommandOptionType.SubCommandGroup && set.Type != ApplicationCommandOptionType.SubCommand)
	                    {
	                        continue;
	                    }

	                    var opt = fn.BuildSlashSetOption();
	                    var optExists = set.Options?.Any(o => string.Equals(o.Name, opt.Name, StringComparison.OrdinalIgnoreCase)) == true;
	                    if (!optExists)
	                    {
	                        set.AddOption(opt);
	                    }

	                    break;
	                }
	            }
	        }

	        // Stable ordering: core currently relies on "help"/"modules"/"instruction"/"clear"/"set" being near the top.
	        var result = new List<SlashCommandOptionBuilder>();
	        foreach (var name in order)
	        {
	            if (topLevel.TryGetValue(name, out var opt))
	            {
	                result.Add(opt);
	            }
	        }

	        return result;
	    }

	    private string BuildModulesText(ChannelState channelState)
	    {
	        if (_modulePipeline == null)
	        {
	            return "Modules are not enabled.";
	        }

	        var report = _modulePipeline.DiscoveryReport;

	        var lines = new List<string>
	        {
	            "**Modules**",
	            $"Modules path: `{report?.ModulesPath ?? "(unknown)"}`",
	        };

	        var loaded = _modulePipeline.Modules?.Where(m => !string.IsNullOrWhiteSpace(m?.Id)).ToList()
	                     ?? new List<IFeatureModule>();

	        lines.Add($"Loaded modules ({loaded.Count}):");
	        foreach (var module in loaded.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
	        {
	            var enabled = channelState == null ? true : IsModuleEnabled(channelState, module.Id);
	            lines.Add($"- {module.Id} ({module.Name}) enabled={(enabled ? "true" : "false")}");
	        }

	        if (channelState?.Options?.ModulesEnabled is { Count: > 0 })
	        {
	            lines.Add("");
	            lines.Add("Channel module overrides:");
	            foreach (var kvp in channelState.Options.ModulesEnabled.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
	            {
	                lines.Add($"- {kvp.Key}={(kvp.Value ? "true" : "false")}");
	            }
	        }

	        if (report != null)
	        {
	            if (report.FoundDlls is { Count: > 0 })
	            {
	                lines.Add($"Module DLLs found: {report.FoundDlls.Count}");
	            }

	            if (report.LoadErrors is { Count: > 0 })
	            {
	                lines.Add("");
	                lines.Add("**Module load errors**");
	                foreach (var err in report.LoadErrors.Take(10))
	                {
	                    lines.Add($"- {err}");
	                }
	                if (report.LoadErrors.Count > 10)
	                {
	                    lines.Add($"- (and {report.LoadErrors.Count - 10} more)");
	                }
	            }
	        }

	        // Function registry debug to help diagnose missing slash options/tools.
	        try
	        {
	            var all = GetAllGptCliFunctions();
	            var moduleFns = all.Where(f => !string.IsNullOrWhiteSpace(f?.ModuleId)).ToList();
	            lines.Add("");
	            lines.Add($"GptCliFunctions: total={all.Count}, module={moduleFns.Count}");

	            foreach (var group in moduleFns
	                         .GroupBy(f => f.ModuleId, StringComparer.OrdinalIgnoreCase)
	                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
	            {
	                lines.Add($"- {group.Key}: {group.Count()} function(s)");
	                foreach (var fn in group.OrderBy(f => f.ToolName, StringComparer.OrdinalIgnoreCase).Take(40))
	                {
	                    var slash = fn.Slash == null
	                        ? "(no slash binding)"
	                        : fn.Slash.Kind switch
	                        {
	                            GptCliSlashBindingKind.SetOption => $"set/{fn.Slash.SetOptionName}",
	                            GptCliSlashBindingKind.SubCommand => fn.Slash.TopLevelName,
	                            GptCliSlashBindingKind.GroupSubCommand => $"{fn.Slash.TopLevelName}/{fn.Slash.SubCommandName}",
	                            _ => fn.Slash.TopLevelName
	                        };
	                    lines.Add($"  - {fn.ToolName} -> {slash}");
	                }
	            }
	        }
	        catch (Exception ex)
	        {
	            lines.Add($"(Function registry debug failed: {ex.GetType().Name} {ex.Message})");
	        }

	        return string.Join("\n", lines);
	    }

		    private static string BuildHelpText(ChannelState channelState, IReadOnlyList<GptCliFunction> functions)
		    {
		        var lines = new List<string>
		        {
		            "**GPT-CLI help**",
		            ""
		        };

		        functions ??= Array.Empty<GptCliFunction>();

		        var setFunctions = functions
		            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.SetOption &&
		                        string.Equals(f.Slash.TopLevelName, "set", StringComparison.OrdinalIgnoreCase) &&
		                        !string.IsNullOrWhiteSpace(f.Slash.SetOptionName))
		            .ToList();

		        var coreFunctions = functions
		            .Where(f => f?.Slash != null && string.IsNullOrWhiteSpace(f.ModuleId))
		            .ToList();

		        var moduleFunctions = functions
		            .Where(f => f?.Slash != null && !string.IsNullOrWhiteSpace(f.ModuleId))
		            .ToList();

		        lines.Add("**Core**");
		        lines.AddRange(BuildHelpLinesForFunctions(
		            channelState,
		            coreFunctions.Where(f => f.Slash.Kind != GptCliSlashBindingKind.SetOption).ToList(),
		            "core"));

		        lines.Add("");
		        lines.Add("**Settings**");
		        lines.Add("• `/gptcli set <option>:<value>` (provide exactly one option)");
		        lines.AddRange(BuildHelpLinesForSetOptions(setFunctions));

		        // Module commands (including their `set` toggles) live here.
		        var moduleIds = moduleFunctions
		            .Select(f => f.ModuleId)
		            .Distinct(StringComparer.OrdinalIgnoreCase)
		            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
		            .ToList();

		        if (moduleIds.Count > 0)
		        {
		            lines.Add("");
		            lines.Add("**Modules**");
		            foreach (var moduleId in moduleIds)
		            {
		                var enabled = channelState != null ? IsModuleEnabled(channelState, moduleId) : (bool?)null;
		                var enabledText = enabled.HasValue ? (enabled.Value ? "enabled" : "disabled") : "enabled=?";
		                lines.Add($"**{moduleId}** ({enabledText})");

		                // If a module has a set toggle, show it first.
		                var setToggles = setFunctions
		                    .Where(f => string.Equals(f.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
		                    .ToList();
		                foreach (var toggle in setToggles)
		                {
		                    lines.Add($"• {FormatSetOptionLine(toggle)}");
		                }

		                var moduleCmds = moduleFunctions
		                    .Where(f => string.Equals(f.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) &&
		                                f.Slash.Kind != GptCliSlashBindingKind.SetOption)
		                    .ToList();

		                var moduleLines = BuildHelpLinesForFunctions(channelState, moduleCmds, moduleId);
		                if (moduleLines.Count == 0)
		                {
		                    lines.Add("• (no commands)");
		                }
		                else
		                {
		                    lines.AddRange(moduleLines);
		                }
		            }
		        }

		        lines.Add("");
		        lines.Add("**Natural language tool calling**");
		        lines.Add("Tag the bot and ask for a slash command in plain English.");
		        lines.Add("Example: `@bot set max tokens to 8000`");

		        return string.Join("\n", lines);
		    }

		    private static List<string> BuildHelpLinesForSetOptions(IReadOnlyList<GptCliFunction> setFunctions)
		    {
		        var lines = new List<string>();
		        if (setFunctions == null || setFunctions.Count == 0)
		        {
		            return lines;
		        }

		        foreach (var fn in setFunctions.OrderBy(f => f.Slash.SetOptionName, StringComparer.OrdinalIgnoreCase))
		        {
		            lines.Add($"• {FormatSetOptionLine(fn)}");
		        }

		        return lines;
		    }

		    private static string FormatSetOptionLine(GptCliFunction fn)
		    {
		        var name = fn?.Slash?.SetOptionName ?? "unknown";
		        var valueSpec = fn?.Parameters?.FirstOrDefault();
		        var valueText = valueSpec != null ? FormatParamValueHint(valueSpec) : "<value>";
		        var desc = !string.IsNullOrWhiteSpace(fn?.Description) ? $" - {fn.Description}" : "";
		        return $"`/gptcli set {name}:{valueText}`{desc}";
		    }

		    private static List<string> BuildHelpLinesForFunctions(ChannelState channelState, IReadOnlyList<GptCliFunction> functions, string label)
		    {
		        var lines = new List<string>();
		        if (functions == null || functions.Count == 0)
		        {
		            return lines;
		        }

		        // Group subcommands by their top-level group.
		        var subCommands = functions
		            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.SubCommand)
		            .OrderBy(f => f.Slash.TopLevelName, StringComparer.OrdinalIgnoreCase)
		            .ToList();

		        foreach (var fn in subCommands)
		        {
		            lines.Add($"• `{FormatSlashUsage(fn)}`{FormatHelpSuffix(fn)}");
		        }

		        var groups = functions
		            .Where(f => f?.Slash?.Kind == GptCliSlashBindingKind.GroupSubCommand && !string.IsNullOrWhiteSpace(f.Slash.TopLevelName))
		            .GroupBy(f => f.Slash.TopLevelName, StringComparer.OrdinalIgnoreCase)
		            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
		            .ToList();

		        foreach (var group in groups)
		        {
		            var groupName = group.Key;
		            lines.Add($"• `/gptcli {groupName} ...`");

		            foreach (var fn in group.OrderBy(f => f.Slash.SubCommandName, StringComparer.OrdinalIgnoreCase))
		            {
		                lines.Add($"• `{FormatSlashUsage(fn)}`{FormatHelpSuffix(fn)}");
		            }
		        }

		        return lines;
		    }

		    private static string FormatHelpSuffix(GptCliFunction fn)
		    {
		        if (fn == null || string.IsNullOrWhiteSpace(fn.Description))
		        {
		            return "";
		        }

		        return $" - {fn.Description}";
		    }

		    private static string FormatSlashUsage(GptCliFunction fn)
		    {
		        if (fn?.Slash == null)
		        {
		            return "/gptcli";
		        }

		        var args = FormatParamHints(fn.Parameters);
		        return fn.Slash.Kind switch
		        {
		            GptCliSlashBindingKind.SubCommand => $"/gptcli {fn.Slash.TopLevelName}{args}",
		            GptCliSlashBindingKind.GroupSubCommand => $"/gptcli {fn.Slash.TopLevelName} {fn.Slash.SubCommandName}{args}",
		            _ => $"/gptcli {fn.Slash.TopLevelName}{args}"
		        };
		    }

		    private static string FormatParamHints(IReadOnlyList<GptCliParamSpec> parameters)
		    {
		        if (parameters == null || parameters.Count == 0)
		        {
		            return "";
		        }

		        var parts = new List<string>();
		        foreach (var p in parameters)
		        {
		            if (p == null || string.IsNullOrWhiteSpace(p.Name))
		            {
		                continue;
		            }

		            var value = FormatParamValueHint(p);
		            var part = $"{p.Name}:{value}";
		            parts.Add(p.Required ? part : $"[{part}]");
		        }

		        return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
		    }

		    private static string FormatParamValueHint(GptCliParamSpec p)
		    {
		        if (p == null)
		        {
		            return "<value>";
		        }

		        if (p.Choices is { Count: > 0 })
		        {
		            return $"<{string.Join("|", p.Choices.Select(c => c.Value))}>";
		        }

		        return p.Type switch
		        {
		            GptCliParamType.Boolean => "<true|false>",
		            GptCliParamType.Integer => "<int>",
		            GptCliParamType.Number => "<number>",
		            GptCliParamType.User => "<user>",
		            GptCliParamType.Channel => "<channel>",
		            GptCliParamType.Role => "<role>",
		            GptCliParamType.Attachment => "<file>",
		            _ => "<text>"
		        };
		    }

    private Task<GptCliExecutionResult> ExecuteClearAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "target", out var targetEl) || targetEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide `target` as messages, instructions, or all.", false));
        }

        var target = targetEl.GetString()?.Trim().ToLowerInvariant();
        switch (target)
        {
            case "messages":
                ctx.ChannelState.InstructionChat.ClearMessages();
                return Task.FromResult(new GptCliExecutionResult(true, "Messages cleared."));
            case "instructions":
                ctx.ChannelState.InstructionChat.ClearInstructions();
                return Task.FromResult(new GptCliExecutionResult(true, "Instructions cleared."));
            case "all":
                ctx.ChannelState.InstructionChat.ClearMessages();
                ctx.ChannelState.InstructionChat.ClearInstructions();
                return Task.FromResult(new GptCliExecutionResult(true, "Messages and instructions cleared."));
            default:
                return Task.FromResult(new GptCliExecutionResult(true, "Unknown target. Use messages, instructions, or all.", false));
        }
    }

    private Task<GptCliExecutionResult> ExecuteInstructionAddAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide instruction text.", false));
        }

        var text = textEl.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide instruction text.", false));
        }

        ctx.ChannelState.InstructionChat.AddInstruction(new ChatMessage(StaticValues.ChatMessageRoles.System, text.Trim()));
        return Task.FromResult(new GptCliExecutionResult(true, "Instruction added."));
    }

    private Task<GptCliExecutionResult> ExecuteInstructionListAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var instructions = ctx.ChannelState.InstructionChat.ChatBotState.Instructions;
        if (instructions == null || instructions.Count == 0)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "No instructions stored.", false));
        }

        var lines = instructions.Select((inst, index) => $"{index + 1}. {inst.Content}").ToList();
        return Task.FromResult(new GptCliExecutionResult(true, $"Instructions:\n{string.Join("\n", lines)}", false));
    }

    private Task<GptCliExecutionResult> ExecuteInstructionGetAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "index", out var indexEl) || !indexEl.TryGetInt32(out var index))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide instruction index.", false));
        }

        var instructions = ctx.ChannelState.InstructionChat.ChatBotState.Instructions;
        var i = index - 1;
        if (instructions == null || i < 0 || i >= instructions.Count)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Instruction index out of range.", false));
        }

        return Task.FromResult(new GptCliExecutionResult(true, $"{index}. {instructions[i].Content}", false));
    }

    private Task<GptCliExecutionResult> ExecuteInstructionDeleteAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "index", out var indexEl) || !indexEl.TryGetInt32(out var index))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide instruction index.", false));
        }

        var instructions = ctx.ChannelState.InstructionChat.ChatBotState.Instructions;
        var i = index - 1;
        if (instructions == null || i < 0 || i >= instructions.Count)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Instruction index out of range.", false));
        }

        ctx.ChannelState.InstructionChat.RemoveInstruction(i);
        return Task.FromResult(new GptCliExecutionResult(true, $"Instruction {index} deleted."));
    }

    private Task<GptCliExecutionResult> ExecuteInstructionClearAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        ctx.ChannelState.InstructionChat.ClearInstructions();
        return Task.FromResult(new GptCliExecutionResult(true, "Instructions cleared."));
    }

    private Task<GptCliExecutionResult> ExecuteSetEnabledAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || valueEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "enabled expects boolean.", false));
        }

        var enabled = valueEl.GetBoolean();
        ctx.ChannelState.Options.Enabled = enabled;
        return Task.FromResult(new GptCliExecutionResult(true, $"enabled = {enabled.ToString().ToLowerInvariant()}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetMuteAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || valueEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "mute expects boolean.", false));
        }

        var muted = valueEl.GetBoolean();
        ctx.ChannelState.Options.Muted = muted;
        return Task.FromResult(new GptCliExecutionResult(true, $"mute = {muted.ToString().ToLowerInvariant()}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetResponseModeAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "response-mode expects All or Matches.", false));
        }

        var value = valueEl.GetString();
        if (!Enum.TryParse<InstructionChatBot.ResponseMode>(value, true, out var parsed))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "response-mode expects All or Matches.", false));
        }

        ctx.ChannelState.InstructionChat.ChatBotState.ResponseMode = parsed;
        return Task.FromResult(new GptCliExecutionResult(true, $"response-mode = {parsed}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetEmbedModeAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "embed-mode expects Explicit or All.", false));
        }

        var value = valueEl.GetString();
        if (!Enum.TryParse<InstructionChatBot.EmbedMode>(value, true, out var parsed))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "embed-mode expects Explicit or All.", false));
        }

        ctx.ChannelState.InstructionChat.ChatBotState.EmbedMode = parsed;
        return Task.FromResult(new GptCliExecutionResult(true, $"embed-mode = {parsed}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetMaxChatHistoryAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || !valueEl.TryGetInt32(out var value) || value < 100)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "max-chat-history-length expects integer >= 100.", false));
        }

        ctx.ChannelState.InstructionChat.ChatBotState.Parameters.MaxChatHistoryLength = (uint)value;
        return Task.FromResult(new GptCliExecutionResult(true, $"max-chat-history-length = {value}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetMaxTokensAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || !valueEl.TryGetInt32(out var value) || value < 50)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "max-tokens expects integer >= 50.", false));
        }

        ctx.ChannelState.InstructionChat.ChatBotState.Parameters.MaxTokens = value;
        return Task.FromResult(new GptCliExecutionResult(true, $"max-tokens = {value}"));
    }

    private Task<GptCliExecutionResult> ExecuteSetModelAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!GptCliFunction.TryGetJsonProperty(argsJson, "value", out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new GptCliExecutionResult(true, "model expects a string.", false));
        }

        var model = valueEl.GetString();
        if (string.IsNullOrWhiteSpace(model))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "model expects a non-empty string.", false));
        }

        ctx.ChannelState.InstructionChat.ChatBotState.Parameters.Model = model.Trim();
        return Task.FromResult(new GptCliExecutionResult(true, $"model = {model.Trim()}"));
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
