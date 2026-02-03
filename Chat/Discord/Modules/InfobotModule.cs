using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GPT.CLI.Embeddings;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord.Modules;

public sealed class InfobotModule : FeatureModuleBase
{
    public override string Id => "infobot";
    public override string Name => "Infobot";

    private readonly ConcurrentDictionary<ulong, FactoidResponseMetadata> _factoidResponseMap = new();
    private record FactoidResponseMetadata(ulong ChannelId, string Term);

    private static readonly string[] InfobotQuestionPrefixes =
    {
        "who", "who is", "who are",
        "what", "what's", "what is", "what are",
        "where", "where's", "where is", "where are"
    };

    private static SlashCommandOptionBuilder BuildInfobotCommands()
    {
        return new SlashCommandOptionBuilder
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
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("leaderboard").WithDescription("Show the infobot leaderboard")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("clear").WithDescription("Clear all factoids for this channel")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("personality").WithDescription("Set the infobot personality prompt")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("prompt")
                        .WithDescription("The personality prompt")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
            }
        };
    }

    private static SlashCommandOptionBuilder BuildInfobotToggleOption()
    {
        return new SlashCommandOptionBuilder().WithName("infobot").WithDescription("Enable or disable infobot learning")
            .WithType(ApplicationCommandOptionType.Boolean);
    }

    public override async Task InitializeAsync(DiscordModuleContext context, CancellationToken cancellationToken)
    {
        await LoadFactoids(context);
        await LoadFactoidMatches(context);
        await LoadFactoidMatchStats(context);
    }

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildInfobotCommands()),
            SlashCommandContribution.ForOption("set", BuildInfobotToggleOption())
        };
    }

    public override async Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken)
    {
        if (message == null || message.Author.Id == context.Client.CurrentUser.Id)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var channel = context.Host.GetOrCreateChannelState(message.Channel);
        if (message.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channel, guildChannel);
        }

        if (!context.Host.IsChannelGuildMatch(channel, message.Channel, "factoid-message"))
        {
            return;
        }

        if (!channel.Options.LearningEnabled)
        {
            return;
        }

        var isTagged = message.MentionedUsers.Any(user => user.Id == context.Client.CurrentUser.Id);
        var isDirectMessage = message.Channel is IPrivateChannel;
        var shouldRespond = isTagged || isDirectMessage;
        await HandleInfobotMessageAsync(context, channel, message, shouldRespond);
    }

    public override async Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(DiscordModuleContext context, SocketMessage message, InstructionGPT.ChannelState channel, CancellationToken cancellationToken)
    {
        if (message == null || channel == null)
        {
            return Array.Empty<ChatMessage>();
        }

        return await BuildFactoidContextMessagesAsync(context, channel, message);
    }

    public override async Task OnReactionAddedAsync(DiscordModuleContext context, Cacheable<IUserMessage, ulong> userMessage, Cacheable<IMessageChannel, ulong> messageChannel, SocketReaction reaction, CancellationToken cancellationToken)
    {
        if (reaction.UserId == context.Client.CurrentUser.Id)
        {
            return;
        }

        if (reaction.Emote.Name is not ("🗑️" or "🗑" or "🛑"))
        {
            return;
        }

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

        if (!_factoidResponseMap.TryGetValue(messageId, out var metadata) || metadata.ChannelId != channel.Id)
        {
            return;
        }

        switch (reaction.Emote.Name)
        {
            case "🗑️":
            case "🗑":
            {
                var channelState = context.Host.GetOrCreateChannelState(channel);
                var removed = RemoveFactoidByTerm(context, channelState, context.Host.GetGuildId(channel), metadata.Term);

                if (message != null)
                {
                    await InstructionGPT.TryRemoveAllReactionsForEmoteAsync(message, reaction.Emote);
                }

                var reply = removed
                    ? $"<@{reaction.UserId}> Factoid removed: {metadata.Term}."
                    : $"<@{reaction.UserId}> No factoid found for {metadata.Term}.";
                await InstructionGPT.TryReplyAsync(message, channel, messageId, reply);
                await context.Host.SaveCachedChannelStateAsync(channel.Id);
                break;
            }
            case "🛑":
            {
                var channelState = context.Host.GetOrCreateChannelState(channel);
                if (channelState.Options.LearningEnabled)
                {
                    channelState.Options.LearningEnabled = false;
                    await context.Host.SaveCachedChannelStateAsync(channel.Id);
                }

                if (message != null)
                {
                    await InstructionGPT.TryRemoveReactionAsync(message, reaction.Emote, reaction.UserId);
                }

                await InstructionGPT.TryReplyAsync(message, channel, messageId, "Infobot disabled for this channel.");
                break;
            }
        }
    }

    public override async Task<bool> OnInteractionAsync(DiscordModuleContext context, SocketInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction is not SocketSlashCommand { CommandName: "gptcli" } command)
        {
            return false;
        }

        if (command.Data.Options == null || command.Data.Options.Count == 0)
        {
            return false;
        }

        var handled = false;
        foreach (var option in command.Data.Options)
        {
            if (string.Equals(option.Name, "infobot", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInfobotSlashCommandAsync(context, command, option);
                handled = true;
            }
            else if (string.Equals(option.Name, "set", StringComparison.OrdinalIgnoreCase))
            {
                var subOption = option.Options?.FirstOrDefault();
                if (subOption != null && string.Equals(subOption.Name, "infobot", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInfobotSettingAsync(context, command, subOption);
                    handled = true;
                }
            }
        }

        return handled;
    }

    private static async Task HandleInfobotSettingAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption subOption)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channel = command.Channel;
        var channelState = context.Host.GetOrCreateChannelState(channel);
        if (!context.Host.IsChannelGuildMatch(channelState, channel, "slash-command"))
        {
            await InstructionGPT.SendEphemeralResponseAsync(command,
                "Guild mismatch detected for cached channel data. Refusing to apply command.");
            return;
        }

        var responses = new List<string>();
        if (subOption.Value is bool learningEnabled)
        {
            channelState.Options.LearningEnabled = learningEnabled;
            responses.Add($"Infobot {(learningEnabled ? "enabled" : "disabled")}.");
        }
        else
        {
            responses.Add("Provide true or false for infobot setting.");
        }

        var responseText = string.Join("\n", responses);
        await InstructionGPT.SendEphemeralResponseAsync(command, responseText);
        await context.Host.SaveCachedChannelStateAsync(channel.Id);
    }

    private async Task HandleInfobotSlashCommandAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption option)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channel = command.Channel;
        var channelState = context.Host.GetOrCreateChannelState(channel);
        if (!context.Host.IsChannelGuildMatch(channelState, channel, "slash-command"))
        {
            await InstructionGPT.SendEphemeralResponseAsync(command,
                "Guild mismatch detected for cached channel data. Refusing to apply command.");
            return;
        }

        var responses = new List<string>();
        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            responses.Add("Specify an infobot command.");
        }
        else
        {
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
                        "• `<term> was <fact>`",
                        "• `<terms> were <fact>`",
                        "",
                        "**Ask questions (exact match only)**",
                        "• `what|who|where is|are <term>?`",
                        "• `<term>?` (short form)",
                        "",
                        "**Enable / disable**",
                        "• `/gptcli set infobot true|false`",
                        "",
                        "**Factoid commands**",
                        "• `/gptcli infobot set term text`",
                        "• `/gptcli infobot get term`",
                        "• `/gptcli infobot delete term`",
                        "• `/gptcli infobot list`",
                        "• `/gptcli infobot leaderboard` — leaderboard",
                        "• `/gptcli infobot clear`",
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

                    await SaveFactoidAsync(context, channelState, command.User, channel, term, text);
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

                    var entry = FindExactTermFactoid(context, channel.Id, context.Host.GetGuildId(channel), term);
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

                    if (RemoveFactoidByTerm(context, channelState, context.Host.GetGuildId(channel), term))
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
                    if (!context.ChannelFactoids.TryGetValue(channel.Id, out var factoids) || factoids.Count == 0)
                    {
                        responses.Add("No factoids stored.");
                        break;
                    }

                    var guildId = context.Host.GetGuildId(channel);
                    var usableFactoids = FilterFactoidsForChannel(factoids, guildId, channel.Id);
                    if (usableFactoids.Count == 0)
                    {
                        responses.Add("No factoids stored.");
                        break;
                    }

                    var indexed = usableFactoids
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

                    var summary = lines.Count == 0
                        ? "No factoids stored."
                        : $"Factoids:\n{string.Join("\n", lines)}";
                    var footer = string.Join("\n", new[]
                    {
                        "",
                        "**Manage factoids**",
                        "• `/gptcli infobot set term text`",
                        "• `/gptcli infobot get term`",
                        "• `/gptcli infobot delete term`",
                        "• `/gptcli infobot list`",
                        "• `/gptcli infobot clear`",
                        "• `/gptcli infobot leaderboard`"
                    });
                    responses.Add($"{summary}{footer}");
                    break;
                }
                case "leaderboard":
                {
                    if (channel is IPrivateChannel)
                    {
                        responses.Add("Infobot leaderboard is not tracked in DMs.");
                        break;
                    }

                    if (channel is IGuildChannel guildChannel)
                    {
                        context.Host.EnsureChannelStateMetadata(channelState, guildChannel);
                    }

                    var stats = context.ChannelFactoidMatchStats.GetOrAdd(channel.Id, _ => new FactoidMatchStats());
                    stats = EnsureMatchStats(stats);
                    context.ChannelFactoidMatchStats[channel.Id] = stats;

                    if (stats.TotalMatches == 0 && context.ChannelFactoidMatches.TryGetValue(channel.Id, out var matches) && matches.Count > 0)
                    {
                        stats = BuildMatchStatsFromEntries(matches);
                        context.ChannelFactoidMatchStats[channel.Id] = stats;
                        await SaveFactoidMatchStatsAsync(context, channelState, stats);
                    }

                    if (stats.TotalMatches == 0 || stats.TermCounts.Count == 0)
                    {
                        responses.Add("No infobot matches recorded yet.");
                        break;
                    }

                    const int maxMatchesToShow = 20;
                    var leaderboard = stats.TermCounts
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(maxMatchesToShow)
                        .Select((kv, index) =>
                        {
                            var display = stats.TermDisplayNames.TryGetValue(kv.Key, out var name) && !string.IsNullOrWhiteSpace(name)
                                ? name
                                : kv.Key;
                            var link = stats.LastResponseMessageIds.TryGetValue(kv.Key, out var messageId)
                                ? InstructionGPT.BuildDiscordMessageLink(channelState, messageId)
                                : null;
                            var linkText = string.IsNullOrWhiteSpace(link) ? "(link unavailable)" : link;
                            var label = kv.Value == 1 ? "match" : "matches";
                            return $"• {index + 1}. {display} — {kv.Value} {label} — {linkText}";
                        })
                        .ToList();

                    var footer = $"Total matches: {stats.TotalMatches}.";
                    var summary = $"**Infobot leaderboard**\n{string.Join("\n", leaderboard)}\n{footer}";
                    responses.Add(summary);
                    break;
                }
                case "clear":
                {
                    if (context.ChannelFactoids.TryRemove(channel.Id, out _))
                    {
                        await SaveFactoidsAsync(context, channelState, new List<FactoidEntry>());
                        responses.Add("All factoids cleared for this channel.");
                    }
                    else
                    {
                        responses.Add("No factoids stored.");
                    }
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
                    channelState.Options.LearningPersonalityPrompt = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
                    responses.Add(string.IsNullOrWhiteSpace(trimmed)
                        ? "Infobot personality prompt cleared."
                        : "Infobot personality prompt updated.");
                    break;
                }
                default:
                    responses.Add($"Unknown infobot command: {subOption.Name}");
                    break;
            }
        }

        if (responses.Count == 0)
        {
            responses.Add("No changes made.");
        }

        var responseText = string.Join("\n", responses);
        await InstructionGPT.SendEphemeralResponseAsync(command, responseText);
        await context.Host.SaveCachedChannelStateAsync(channel.Id);
    }

    private async Task HandleInfobotMessageAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, SocketMessage message, bool isTagged)
    {
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var hasExplicitMention = message.MentionedUsers.Any(user => user.Id == context.Client.CurrentUser.Id);
        var isListening = channel.Options.LearningEnabled;
        if (!isListening)
        {
            return;
        }

        var content = message.Content.Trim();
        if (isTagged)
        {
            content = content.Replace($"<@{context.Client.CurrentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
                             .Replace($"<@!{context.Client.CurrentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
                             .Trim();
        }

        var hasQuestionMark = content.Trim().EndsWith("?", StringComparison.Ordinal);
        if (isTagged || hasQuestionMark)
        {
            var question = PreprocessInfobotQuestion(content);
            var queries = BuildInfobotQueries(question, message.Author.Username, isTagged, context.Client.CurrentUser.Username);
            if (queries.Count > 0)
            {
                if (!isTagged && !hasQuestionMark)
                {
                    return;
                }

                if (!isTagged)
                {
                    const int minLen = 2;
                    const int maxLen = 512;
                    var queryLength = queries[0].Length;
                    if (queryLength < minLen || queryLength > maxLen)
                    {
                        return;
                    }
                }

                var matched = await RespondWithFactoidMatchAsync(context, channel, message, queries);
                if (matched || hasQuestionMark)
                {
                    return;
                }
            }
        }

        if (TryParseInfobotSet(content, out var term, out var fact))
        {
            if (isListening && !hasExplicitMention)
            {
                await SaveFactoidAsync(context, channel, message, term, fact);
                if (isTagged)
                {
                    await SendFactoidAcknowledgementAsync(channel, message, term, fact, "Learned a new factoid.");
                }
            }
        }
    }

    private async Task<List<ChatMessage>> BuildFactoidContextMessagesAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, SocketMessage message)
    {
        if (!context.Host.IsChannelGuildMatch(channel, message.Channel, "factoid-context"))
        {
            return new List<ChatMessage>();
        }

        var channelFactoids = context.ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
        var guildId = context.Host.GetGuildId(message.Channel);
        var usableFactoids = FilterFactoidsForChannel(channelFactoids, guildId, message.Channel.Id);
        if (usableFactoids.Count == 0)
        {
            return new List<ChatMessage>();
        }

        var embedding = await context.OpenAILogic.GetEmbeddingForPrompt(message.Content);
        var threshold = channel.Options.FactoidSimilarityThreshold;
        var closestChannel = FindMostSimilarFactoids(usableFactoids, embedding, channel.InstructionChat.ChatBotState.Parameters.ClosestMatchLimit, threshold);
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

    private async Task<bool> RespondWithFactoidMatchAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, SocketMessage message, IReadOnlyList<string> queries)
    {
        if (!context.Host.IsChannelGuildMatch(channel, message.Channel, "factoid-match"))
        {
            return false;
        }

        var factoids = context.ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
        var guildId = context.Host.GetGuildId(message.Channel);
        var usableFactoids = FilterFactoidsForChannel(factoids, guildId, message.Channel.Id);
        if (usableFactoids.Count == 0)
        {
            return false;
        }

        FactoidEntry exactMatch = null;
        foreach (var query in queries)
        {
            var normalizedQuery = NormalizeTerm(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                continue;
            }

            exactMatch = usableFactoids.FirstOrDefault(f =>
                f.Term != null && string.Equals(NormalizeTerm(f.Term), normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                break;
            }
        }
        if (exactMatch == null)
        {
            return false;
        }

        using var typingState = message.Channel.EnterTypingState();
        var response = await GenerateFactoidResponseAsync(context, channel, queries[0], exactMatch, message);
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
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
            await TrackFactoidMatchAsync(context, channel, message, exactMatch, queries[0], responseMessage);
        }

        return true;
    }

    private async Task TrackFactoidMatchAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, SocketMessage message, FactoidEntry factoid, string query, IUserMessage responseMessage)
    {
        if (message.Channel is IPrivateChannel)
        {
            return;
        }

        if (responseMessage == null || channel == null)
        {
            return;
        }

        if (message.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channel, guildChannel);
        }

        var matches = context.ChannelFactoidMatches.GetOrAdd(message.Channel.Id, _ => new List<FactoidMatchEntry>());
        var entry = new FactoidMatchEntry
        {
            Term = factoid.Term,
            Query = query,
            QueryMessageId = message.Id,
            ResponseMessageId = responseMessage.Id,
            UserId = message.Author?.Id ?? 0,
            MatchedAt = DateTimeOffset.UtcNow
        };
        matches.Add(entry);

        const int maxMatches = 50;
        if (matches.Count > maxMatches)
        {
            matches.RemoveRange(0, matches.Count - maxMatches);
        }

        await SaveFactoidMatchesAsync(context, channel, matches);

        var stats = context.ChannelFactoidMatchStats.GetOrAdd(message.Channel.Id, _ => new FactoidMatchStats());
        stats = EnsureMatchStats(stats);
        context.ChannelFactoidMatchStats[message.Channel.Id] = stats;
        var normalizedTerm = NormalizeTerm(factoid.Term) ?? NormalizeTerm(query) ?? "unknown";
        var displayTerm = string.IsNullOrWhiteSpace(factoid.Term) ? normalizedTerm : factoid.Term;
        lock (stats)
        {
            stats.TotalMatches++;
            stats.TermCounts[normalizedTerm] = stats.TermCounts.TryGetValue(normalizedTerm, out var count) ? count + 1 : 1;
            stats.TermDisplayNames[normalizedTerm] = displayTerm;
            stats.LastResponseMessageIds[normalizedTerm] = responseMessage.Id;
            stats.LastUserIds[normalizedTerm] = message.Author?.Id ?? 0;
        }

        await SaveFactoidMatchStatsAsync(context, channel, stats);
    }

    private async Task<string> GenerateFactoidResponseAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, string query, FactoidEntry factoid, SocketMessage message)
    {
        var mention = factoid.SourceUserId != 0 ? $"<@{factoid.SourceUserId}>" : (factoid.SourceUsername ?? "unknown");
        var messageLink = InstructionGPT.BuildDiscordMessageLink(channel, factoid.SourceMessageId);
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
        var personalityPrompt = channel.Options.LearningPersonalityPrompt ?? context.DefaultParameters.LearningPersonalityPrompt;
        var systemPrompt = string.IsNullOrWhiteSpace(personalityPrompt)
            ? "You are a concise, personable Discord bot. Answer matched factoids in an infobot-inspired style. Never wrap replies in triple backtick code fences (including ```discord) unless the user explicitly asks for a code block. If prior conversation includes code fences, override that style and respond without code fences."
            : $"You are a concise, personable Discord bot. Answer matched factoids in an infobot-inspired style. Never wrap replies in triple backtick code fences (including ```discord) unless the user explicitly asks for a code block. If prior conversation includes code fences, override that style and respond without code fences.\n{personalityPrompt}";
        var request = new ChatCompletionCreateRequest
        {
            Model = channel.InstructionChat.ChatBotState.Parameters.Model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System, systemPrompt),
                new(StaticValues.ChatMessageRoles.User, prompt)
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
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

    private static async Task SendFactoidAcknowledgementAsync(InstructionGPT.ChannelState channel, SocketMessage message, string term, string fact, string prefix)
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

    private static bool TryParseInfobotSet(string content, out string term, out string fact)
    {
        term = null;
        fact = null;

        var isIndex = content.IndexOf(" is ", StringComparison.OrdinalIgnoreCase);
        var areIndex = content.IndexOf(" are ", StringComparison.OrdinalIgnoreCase);
        var wasIndex = content.IndexOf(" was ", StringComparison.OrdinalIgnoreCase);
        var wereIndex = content.IndexOf(" were ", StringComparison.OrdinalIgnoreCase);
        var separatorIndex = new[] { isIndex, areIndex, wasIndex, wereIndex }
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (separatorIndex <= 0)
        {
            return false;
        }

        var separatorLength = separatorIndex == isIndex
            ? 4
            : separatorIndex == wereIndex
                ? 6
                : 5;
        term = content[..separatorIndex].Trim();
        fact = content[(separatorIndex + separatorLength)..].Trim();
        return !string.IsNullOrWhiteSpace(term) && !string.IsNullOrWhiteSpace(fact);
    }

    private static string NormalizeTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var normalized = term.Trim().TrimEnd('?');
        normalized = Regex.Replace(normalized, @"^(the|da|an?)\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim();
        return normalized.Length == 0 ? null : normalized.ToLowerInvariant();
    }

    private static string PreprocessInfobotQuestion(string message)
    {
        if (message == null)
        {
            return null;
        }

        var question = message;
        question = Regex.Replace(question, @"^where is ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"\s+\?$", "?", RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^whois ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^who is ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^what is (a|an)?", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^how do i ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^where can i (find|get|download)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^how about ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @" da ", " the ", RegexOptions.IgnoreCase);

        question = Regex.Replace(question, @"^(stupid )?q(uestion)?:\s+", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^(does )?(any|ne)(1|one|body) know ", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^[uh]+m*[,\.]* +", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^well([, ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^still([, ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^(gee|boy|golly|gosh)([, ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^(well|and|but|or|yes)([, ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^o+[hk]+(a+y+)?([,. ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^g(eez|osh|olly)([,. ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^w(ow|hee|o+ho+)([,. ]+)", string.Empty, RegexOptions.IgnoreCase);
        question = Regex.Replace(question, @"^heya?,?( folks)?([,. ]+)", string.Empty, RegexOptions.IgnoreCase);

        return question;
    }

    private static string NormalizeInfobotQuery(string input, ref bool finalQMark)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var query = $" {input} ";

        query = Regex.Replace(query, @" (where|what|who)\s+(\S+)\s+(is|are) ", " $1 $3 $2 ", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @" (where|what|who)\s+(.*)\s+(is|are) ", " $1 $3 $2 ", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"^\s*(.*?)\s*", "$1");
        query = Regex.Replace(query, @"be tellin'?g?", "tell", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @" '?bout", " about", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @",? any(hoo?w?|ways?)", " ", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @",?\s*(pretty )*please\??\s*$", "?", RegexOptions.IgnoreCase);

        var countryMatch = Regex.IsMatch(query,
            @"wh(at|ich)\s+(add?res?s|country|place|net (suffix|domain))",
            RegexOptions.IgnoreCase);
        if (countryMatch)
        {
            if (query.Trim().Length == 2 && !query.Trim().StartsWith(".", StringComparison.Ordinal))
            {
                query = "." + query.Trim();
            }
            query = query.TrimEnd() + "?";
        }

        query = Regex.Replace(query,
            @"th(e|at|is) (((m(o|u)th(a|er) ?)?fuck(in'?g?)?|hell|heck|(god-?)?damn?(ed)?) ?)+",
            string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"wtf", "where", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"this (.*) thingy?", " $1", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"this thingy? (called )?", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"ha(s|ve) (an?y?|some|ne) (idea|clue|guess|seen) ", "know ", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"does (any|ne|some) ?(1|one|body) know ", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"do you know ", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"can (you|u|((any|ne|some) ?(1|one|body)))( please)? tell (me|us|him|her)", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"where (\S+) can \S+ (a|an|the)?", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"(can|do) (i|you|one|we|he|she) (find|get)( this)?", "is", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"(i|one|we|he|she) can (find|get)", "is", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"(the )?(address|url) (for|to) ", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"(where is )+", "where is ", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"\s+", " ");
        query = Regex.Replace(query, @"^\s+", string.Empty);

        if (Regex.IsMatch(query, @"\s*[\/?!]*\?+\s*$"))
        {
            finalQMark = true;
            query = Regex.Replace(query, @"\s*[\/?!]*\?+\s*$", string.Empty);
        }

        query = Regex.Replace(query, @"\s+", " ");
        query = Regex.Replace(query, @"^\s*(.*?)\s*$", "$1");
        query = query.Trim();

        return query;
    }

    private static string SwitchPerson(string input, string who, bool addressed, string botName)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var text = input;

        if (!string.IsNullOrWhiteSpace(who))
        {
            text = Regex.Replace(text, @"\b(I am)\b", who, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(i'm)\b", who, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(i am)\b", who, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(am i)\b", who, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(me)\b", who, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(my)\b", $"{who}'s", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bI\b", who, RegexOptions.IgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(botName))
        {
            var youReplacement = addressed ? "I" : botName;
            text = Regex.Replace(text, @"\byou\b", youReplacement, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\byour\b", addressed ? "my" : $"{botName}'s", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\byourself\b", addressed ? "myself" : botName, RegexOptions.IgnoreCase);
        }

        return text;
    }

    private static List<string> BuildInfobotQueries(string message, string who, bool addressed, string botName)
    {
        var queries = new List<string>();
        if (string.IsNullOrWhiteSpace(message))
        {
            return queries;
        }

        var query = message;

        var finalQMark = false;
        var trimmed = query.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            queries.Add(trimmed);
        }

        var normalized = NormalizeInfobotQuery(query, ref finalQMark);
        if (!string.Equals(normalized, query, StringComparison.Ordinal))
        {
            queries.Add(normalized);
        }

        var switched = SwitchPerson(normalized, who, addressed, botName);
        if (!string.Equals(switched, normalized, StringComparison.Ordinal))
        {
            queries.Add(switched);
        }

        var cleaned = switched;
        cleaned = Regex.Replace(cleaned, @"\s+at\s*(\?*)$", "$1", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"^explain\s*(\?*)", "$1", RegexOptions.IgnoreCase);
        cleaned = $" {cleaned} ";

        var qregex = string.Join("|", InfobotQuestionPrefixes.Select(Regex.Escape));
        var match = Regex.Match(cleaned, @"^\s(" + qregex + @")\s", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            cleaned = Regex.Replace(cleaned, @"^\s(" + qregex + @")\s", " ", RegexOptions.IgnoreCase);
        }

        cleaned = cleaned.Trim();
        if (!string.IsNullOrWhiteSpace(cleaned) && !queries.Contains(cleaned))
        {
            queries.Add(cleaned);
        }

        return queries;
    }

    private static FactoidMatchStats EnsureMatchStats(FactoidMatchStats stats)
    {
        if (stats == null)
        {
            return new FactoidMatchStats();
        }

        stats.TermCounts = EnsureMatchDictionary(stats.TermCounts);
        stats.TermDisplayNames = EnsureMatchDictionary(stats.TermDisplayNames);
        stats.LastResponseMessageIds = EnsureMatchDictionary(stats.LastResponseMessageIds);
        stats.LastUserIds = EnsureMatchDictionary(stats.LastUserIds);
        return stats;
    }

    private static FactoidMatchStats BuildMatchStatsFromEntries(IEnumerable<FactoidMatchEntry> matches)
    {
        var stats = EnsureMatchStats(new FactoidMatchStats());
        if (matches == null)
        {
            return stats;
        }

        foreach (var match in matches.OrderBy(m => m.MatchedAt))
        {
            var normalized = NormalizeTerm(match.Term) ?? NormalizeTerm(match.Query) ?? "unknown";
            var display = string.IsNullOrWhiteSpace(match.Term) ? normalized : match.Term;
            stats.TotalMatches++;
            stats.TermCounts[normalized] = stats.TermCounts.TryGetValue(normalized, out var count) ? count + 1 : 1;
            stats.TermDisplayNames[normalized] = display;
            if (match.ResponseMessageId != 0)
            {
                stats.LastResponseMessageIds[normalized] = match.ResponseMessageId;
            }

            if (match.UserId != 0)
            {
                stats.LastUserIds[normalized] = match.UserId;
            }
        }

        return stats;
    }

    private static Dictionary<string, int> EnsureMatchDictionary(Dictionary<string, int> dictionary)
    {
        if (dictionary == null)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return dictionary.Comparer == StringComparer.OrdinalIgnoreCase
            ? dictionary
            : new Dictionary<string, int>(dictionary, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> EnsureMatchDictionary(Dictionary<string, string> dictionary)
    {
        if (dictionary == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return dictionary.Comparer == StringComparer.OrdinalIgnoreCase
            ? dictionary
            : new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, ulong> EnsureMatchDictionary(Dictionary<string, ulong> dictionary)
    {
        if (dictionary == null)
        {
            return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        }

        return dictionary.Comparer == StringComparer.OrdinalIgnoreCase
            ? dictionary
            : new Dictionary<string, ulong>(dictionary, StringComparer.OrdinalIgnoreCase);
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

    private static List<FactoidEntry> FilterFactoidsForChannel(IEnumerable<FactoidEntry> factoids, ulong guildId, ulong channelId)
    {
        if (factoids == null)
        {
            return new List<FactoidEntry>();
        }

        return factoids.Where(f =>
                (f.SourceGuildId == 0 || f.SourceGuildId == guildId) &&
                (f.SourceChannelId == 0 || f.SourceChannelId == channelId))
            .ToList();
    }

    private static FactoidEntry FindExactTermFactoid(DiscordModuleContext context, ulong channelId, ulong guildId, string term)
    {
        if (!context.ChannelFactoids.TryGetValue(channelId, out var factoids))
        {
            return null;
        }

        var usableFactoids = FilterFactoidsForChannel(factoids, guildId, channelId);
        var normalized = NormalizeTerm(term);
        return usableFactoids.FirstOrDefault(f => f.Term != null &&
                                                 string.Equals(NormalizeTerm(f.Term), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RemoveFactoidByTerm(DiscordModuleContext context, InstructionGPT.ChannelState channel, ulong guildId, string term)
    {
        if (!context.ChannelFactoids.TryGetValue(channel.ChannelId, out var factoids))
        {
            return false;
        }

        var normalized = NormalizeTerm(term);
        var removed = factoids.RemoveAll(f => f.Term != null &&
                                              string.Equals(NormalizeTerm(f.Term), normalized, StringComparison.OrdinalIgnoreCase) &&
                                              (f.SourceGuildId == 0 || f.SourceGuildId == guildId) &&
                                              (f.SourceChannelId == 0 || f.SourceChannelId == channel.ChannelId));
        if (removed == 0)
        {
            return false;
        }

        if (removed > 0)
        {
            _ = SaveFactoidsAsync(context, channel, factoids);
        }
        return true;
    }

    private static async Task SaveFactoidAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, SocketMessage message, string term, string text)
    {
        var normalizedTerm = NormalizeTerm(term);
        var sourceGuildId = context.Host.GetGuildId(message.Channel);
        var entry = new FactoidEntry
        {
            Term = normalizedTerm,
            Text = text.Trim(),
            SourceGuildId = sourceGuildId,
            SourceChannelId = message.Channel.Id,
            SourceMessageId = message.Id,
            SourceUserId = message.Author?.Id ?? 0,
            SourceUsername = message.Author?.Username,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var documents = await Document.ChunkToDocumentsAsync(entry.Text, channel.InstructionChat.ChatBotState.Parameters.ChunkSize);
        if (documents.Count == 0)
        {
            return;
        }

        var embeddings = await context.OpenAILogic.CreateEmbeddings(documents);
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
                SourceGuildId = entry.SourceGuildId,
                SourceChannelId = entry.SourceChannelId,
                SourceMessageId = entry.SourceMessageId,
                SourceUserId = entry.SourceUserId,
                SourceUsername = entry.SourceUsername,
                CreatedAt = entry.CreatedAt
            };

            var list = context.ChannelFactoids.GetOrAdd(message.Channel.Id, _ => new List<FactoidEntry>());
            list.Add(chunkEntry);
        }

        if (context.ChannelFactoids.TryGetValue(message.Channel.Id, out var channelList))
        {
            await SaveFactoidsAsync(context, channel, channelList);
        }
    }

    private static async Task SaveFactoidAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, IUser user, IMessageChannel messageChannel, string term, string text)
    {
        var normalizedTerm = NormalizeTerm(term);
        var sourceGuildId = context.Host.GetGuildId(messageChannel);
        var entry = new FactoidEntry
        {
            Term = normalizedTerm,
            Text = text.Trim(),
            SourceGuildId = sourceGuildId,
            SourceChannelId = messageChannel.Id,
            SourceMessageId = 0,
            SourceUserId = user?.Id ?? 0,
            SourceUsername = user?.Username,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var documents = await Document.ChunkToDocumentsAsync(entry.Text, channel.InstructionChat.ChatBotState.Parameters.ChunkSize);
        if (documents.Count == 0)
        {
            return;
        }

        var embeddings = await context.OpenAILogic.CreateEmbeddings(documents);
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
                SourceGuildId = entry.SourceGuildId,
                SourceChannelId = entry.SourceChannelId,
                SourceMessageId = entry.SourceMessageId,
                SourceUserId = entry.SourceUserId,
                SourceUsername = entry.SourceUsername,
                CreatedAt = entry.CreatedAt
            };

            var list = context.ChannelFactoids.GetOrAdd(messageChannel.Id, _ => new List<FactoidEntry>());
            list.Add(chunkEntry);
        }

        if (context.ChannelFactoids.TryGetValue(messageChannel.Id, out var channelList))
        {
            await SaveFactoidsAsync(context, channel, channelList);
        }
    }

    private static async Task SaveFactoidsAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, List<FactoidEntry> entries)
    {
        var channelDirectory = InstructionGPT.GetChannelDirectory(channel);
        Directory.CreateDirectory(channelDirectory);
        var filePath = InstructionGPT.GetChannelFactoidFile(channel);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, entries, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task SaveFactoidMatchesAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, List<FactoidMatchEntry> entries)
    {
        var channelDirectory = InstructionGPT.GetChannelDirectory(channel);
        Directory.CreateDirectory(channelDirectory);
        var filePath = InstructionGPT.GetChannelMatchFile(channel);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, entries, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task SaveFactoidMatchStatsAsync(DiscordModuleContext context, InstructionGPT.ChannelState channel, FactoidMatchStats stats)
    {
        var channelDirectory = InstructionGPT.GetChannelDirectory(channel);
        Directory.CreateDirectory(channelDirectory);
        var filePath = InstructionGPT.GetChannelMatchStatsFile(channel);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, stats, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task LoadFactoids(DiscordModuleContext context)
    {
        Directory.CreateDirectory("channels");
        var factoidFiles = Directory.GetFiles("channels", "facts*.json", SearchOption.AllDirectories).ToList();
        var channelsWithToken = new HashSet<ulong>();
        foreach (var file in factoidFiles)
        {
            if (TryGetChannelIdFromFactoidPath(file, out var tokenChannelId) &&
                InstructionGPT.TryGetGuildIdTokenFromFileName(file, out _))
            {
                channelsWithToken.Add(tokenChannelId);
            }
        }

        foreach (var file in factoidFiles)
        {
            if (!TryGetChannelIdFromFactoidPath(file, out var channelId))
            {
                continue;
            }

            var hasToken = InstructionGPT.TryGetGuildIdTokenFromFileName(file, out var tokenGuildId);
            if (!hasToken && channelsWithToken.Contains(channelId))
            {
                continue;
            }

            var validation = await ValidateGuildTokenForFileAsync(context, channelId, file, isEmbed: false, "factoids");
            if (!validation.ShouldLoad)
            {
                continue;
            }

            await using var fileStream = File.OpenRead(file);
            var factoids = await JsonSerializer.DeserializeAsync<List<FactoidEntry>>(fileStream) ?? new List<FactoidEntry>();
            var hasPathGuild = InstructionGPT.TryGetGuildIdFromChannelPath(file, out var pathGuildId);
            var resolvedGuildId = validation.GuildId != 0 ? validation.GuildId : (hasToken ? tokenGuildId : pathGuildId);
            var updated = false;
            if (factoids.Count > 0)
            {
                foreach (var factoid in factoids)
                {
                    if (factoid.SourceChannelId == 0)
                    {
                        factoid.SourceChannelId = channelId;
                        updated = true;
                    }

                    if (factoid.SourceGuildId == 0 && resolvedGuildId != 0)
                    {
                        factoid.SourceGuildId = resolvedGuildId;
                        updated = true;
                    }
                }
            }

            context.ChannelFactoids[channelId] = factoids;

            if (!hasToken && hasPathGuild)
            {
                await InstructionGPT.ResaveLegacyJsonAsync(file, ".json", resolvedGuildId, "json", factoids,
                    new JsonSerializerOptions { WriteIndented = true }, deleteLegacyOnSuccess: true);
            }
            else if (updated && hasToken)
            {
                await InstructionGPT.ResaveLegacyJsonAsync(file, ".json", resolvedGuildId, "json", factoids,
                    new JsonSerializerOptions { WriteIndented = true }, overwriteExisting: true);
            }
        }
    }

    private async Task LoadFactoidMatches(DiscordModuleContext context)
    {
        Directory.CreateDirectory("channels");
        var matchFiles = Directory.GetFiles("channels", "matches*.json", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).StartsWith("matches.stats", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var channelsWithToken = new HashSet<ulong>();
        foreach (var file in matchFiles)
        {
            if (TryGetChannelIdFromMatchPath(file, out var tokenChannelId) &&
                InstructionGPT.TryGetGuildIdTokenFromFileName(file, out _))
            {
                channelsWithToken.Add(tokenChannelId);
            }
        }

        foreach (var file in matchFiles)
        {
            if (!TryGetChannelIdFromMatchPath(file, out var channelId))
            {
                continue;
            }

            var hasToken = InstructionGPT.TryGetGuildIdTokenFromFileName(file, out var tokenGuildId);
            if (!hasToken && channelsWithToken.Contains(channelId))
            {
                continue;
            }

            var validation = await ValidateGuildTokenForFileAsync(context, channelId, file, isEmbed: false, "matches");
            if (!validation.ShouldLoad)
            {
                continue;
            }

            await using var fileStream = File.OpenRead(file);
            var matches = await JsonSerializer.DeserializeAsync<List<FactoidMatchEntry>>(fileStream) ?? new List<FactoidMatchEntry>();
            context.ChannelFactoidMatches[channelId] = matches;

            if (!hasToken && InstructionGPT.TryGetGuildIdFromChannelPath(file, out var pathGuildId))
            {
                var resolvedGuildId = validation.GuildId != 0 ? validation.GuildId : (hasToken ? tokenGuildId : pathGuildId);
                await InstructionGPT.ResaveLegacyJsonAsync(file, ".json", resolvedGuildId, "json", matches,
                    new JsonSerializerOptions { WriteIndented = true }, deleteLegacyOnSuccess: true);
            }
        }
    }

    private async Task LoadFactoidMatchStats(DiscordModuleContext context)
    {
        Directory.CreateDirectory("channels");
        var matchFiles = Directory.GetFiles("channels", "matches.stats*.json", SearchOption.AllDirectories).ToList();
        var channelsWithToken = new HashSet<ulong>();
        foreach (var file in matchFiles)
        {
            if (TryGetChannelIdFromMatchPath(file, out var tokenChannelId) &&
                InstructionGPT.TryGetGuildIdTokenFromFileName(file, out _))
            {
                channelsWithToken.Add(tokenChannelId);
            }
        }

        foreach (var file in matchFiles)
        {
            if (!TryGetChannelIdFromMatchPath(file, out var channelId))
            {
                continue;
            }

            var hasToken = InstructionGPT.TryGetGuildIdTokenFromFileName(file, out var tokenGuildId);
            if (!hasToken && channelsWithToken.Contains(channelId))
            {
                continue;
            }

            var validation = await ValidateGuildTokenForFileAsync(context, channelId, file, isEmbed: false, "match-stats");
            if (!validation.ShouldLoad)
            {
                continue;
            }

            await using var fileStream = File.OpenRead(file);
            var stats = await JsonSerializer.DeserializeAsync<FactoidMatchStats>(fileStream) ?? new FactoidMatchStats();
            context.ChannelFactoidMatchStats[channelId] = EnsureMatchStats(stats);

            if (!hasToken && InstructionGPT.TryGetGuildIdFromChannelPath(file, out var pathGuildId))
            {
                var resolvedGuildId = validation.GuildId != 0 ? validation.GuildId : (hasToken ? tokenGuildId : pathGuildId);
                await InstructionGPT.ResaveLegacyJsonAsync(file, ".json", resolvedGuildId, "json", stats,
                    new JsonSerializerOptions { WriteIndented = true }, deleteLegacyOnSuccess: true);
            }
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
        return InstructionGPT.TryParseIdFromName(channelFolder, out channelId);
    }

    private static bool TryGetChannelIdFromMatchPath(string file, out ulong channelId)
    {
        channelId = 0;
        var channelDirectory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(channelDirectory))
        {
            return false;
        }

        var channelFolder = new DirectoryInfo(channelDirectory).Name;
        return InstructionGPT.TryParseIdFromName(channelFolder, out channelId);
    }

    private static async Task<(bool ShouldLoad, ulong GuildId)> ValidateGuildTokenForFileAsync(DiscordModuleContext context, ulong channelId, string filePath, bool isEmbed, string label)
    {
        var hasToken = InstructionGPT.TryGetGuildIdTokenFromFileName(filePath, out var tokenGuildId);
        ulong pathGuildId;
        var hasPathGuild = isEmbed
            ? InstructionGPT.TryGetGuildIdFromEmbedPath(filePath, out pathGuildId)
            : InstructionGPT.TryGetGuildIdFromChannelPath(filePath, out pathGuildId);

        if (hasToken && hasPathGuild && tokenGuildId != pathGuildId)
        {
            await Console.Out.WriteLineAsync(
                $"Skipping {label} for channel {channelId}: token guild {tokenGuildId} does not match path guild {pathGuildId} ({filePath}).");
            return (false, 0);
        }

        var guildId = hasToken ? tokenGuildId : (hasPathGuild ? pathGuildId : 0);
        if (hasToken || hasPathGuild)
        {
            if (!context.ChannelGuildIds.TryGetValue(channelId, out var existing))
            {
                context.ChannelGuildIds[channelId] = guildId;
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

    
}
