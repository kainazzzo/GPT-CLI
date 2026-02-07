using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;

namespace PinboardModuleExample;

public sealed class PinboardModule : FeatureModuleBase
{
    public override string Id => "pinboard";
    public override string Name => "Message Pinboard";

    private const int MaxSnippetLength = 160;
    private const int DefaultListCount = 10;

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildPinCommands())
        };
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
            if (string.Equals(option.Name, "pin", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePinSlashCommandAsync(context, command, option, cancellationToken);
                handled = true;
            }
        }

        return handled;
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

        if (!TryParseMessageCommand(message.Content, out var request))
        {
            return;
        }

        var channelState = context.Host.GetOrCreateChannelState(message.Channel);
        if (message.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channelState, guildChannel);
        }

        if (!context.Host.IsChannelGuildMatch(channelState, message.Channel, "pin-message"))
        {
            return;
        }

        var result = await ExecutePinActionAsync(context, channelState, message.Channel, message.Author.Id, request);
        await message.Channel.SendMessageAsync(result);
        await context.Host.SaveCachedChannelStateAsync(message.Channel.Id);
    }

    private static SlashCommandOptionBuilder BuildPinCommands()
    {
        return new SlashCommandOptionBuilder
        {
            Name = "pin",
            Description = "Pinboard commands",
            Type = ApplicationCommandOptionType.SubCommandGroup,
            Options = new()
            {
                new SlashCommandOptionBuilder().WithName("add")
                    .WithDescription("Add a message to the pinboard")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("message")
                        .WithDescription("Message link or ID")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("note")
                        .WithDescription("Optional note")
                        .WithType(ApplicationCommandOptionType.String)),
                new SlashCommandOptionBuilder().WithName("remove")
                    .WithDescription("Remove a pin by id")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Pin id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("list")
                    .WithDescription("List pins")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("count")
                        .WithDescription("How many to show (1-50)")
                        .WithType(ApplicationCommandOptionType.Integer)),
                new SlashCommandOptionBuilder().WithName("search")
                    .WithDescription("Search pins by text")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("query")
                        .WithDescription("Search text")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
            }
        };
    }

    private async Task HandlePinSlashCommandAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption option, CancellationToken cancellationToken)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channelState = context.Host.GetOrCreateChannelState(command.Channel);
        if (command.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channelState, guildChannel);
        }

        if (!context.Host.IsChannelGuildMatch(channelState, command.Channel, "pin-slash"))
        {
            await SendEphemeralResponseAsync(command, "Guild mismatch detected. Refusing to apply command.");
            return;
        }

        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            await SendEphemeralResponseAsync(command, "Specify a pin command.");
            return;
        }

        var request = BuildRequestFromSlash(subOption);
        var result = await ExecutePinActionAsync(context, channelState, command.Channel, command.User.Id, request);
        await SendEphemeralResponseAsync(command, result);
        await context.Host.SaveCachedChannelStateAsync(channelState.ChannelId);
    }

    private static PinRequest BuildRequestFromSlash(SocketSlashCommandDataOption option)
    {
        var args = new List<string>();
        foreach (var opt in option.Options ?? Enumerable.Empty<SocketSlashCommandDataOption>())
        {
            if (opt.Value == null)
            {
                continue;
            }

            args.Add(opt.Value.ToString());
        }

        return new PinRequest(option.Name, args.ToArray());
    }

    private static bool TryParseMessageCommand(string content, out PinRequest request)
    {
        request = null;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("!pin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            request = new PinRequest("list", Array.Empty<string>());
            return true;
        }

        var command = tokens[1].ToLowerInvariant();
        var args = tokens.Skip(2).ToArray();
        request = new PinRequest(command, args);
        return true;
    }

    private static async Task<string> ExecutePinActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        IMessageChannel channel,
        ulong userId,
        PinRequest request)
    {
        channelState.Pinboard ??= new InstructionGPT.PinboardState();

        switch (request.Command)
        {
            case "add":
                return await AddPinAsync(context, channelState, channel, userId, request.Args);
            case "remove":
                return RemovePin(channelState, request.Args);
            case "list":
                return ListPins(context, channelState, request.Args);
            case "search":
                return SearchPins(context, channelState, request.Args);
            default:
                return "Unknown pin command.";
        }
    }

    private static async Task<string> AddPinAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        IMessageChannel channel,
        ulong userId,
        string[] args)
    {
        if (args.Length == 0)
        {
            return "Provide a message link or ID.";
        }

        if (!TryParseMessageId(args[0], out var messageId, out var channelId) || channelId != channel.Id)
        {
            return "Message must be in this channel. Provide a valid link or message ID.";
        }

        var note = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
        var target = await channel.GetMessageAsync(messageId);
        if (target is not IUserMessage targetMessage)
        {
            return "Message not found.";
        }

        var snippet = BuildSnippet(targetMessage.Content);
        var entry = new InstructionGPT.PinboardEntry
        {
            Id = NextPinId(channelState),
            ChannelId = channel.Id,
            MessageId = messageId,
            AuthorId = targetMessage.Author.Id,
            Snippet = snippet,
            Note = note,
            CreatedUtc = DateTime.UtcNow
        };
        channelState.Pinboard.Pins.Add(entry);

        return $"Pinned #{entry.Id}: {BuildMessageLink(context, channelState, messageId)}";
    }

    private static string RemovePin(InstructionGPT.ChannelState channelState, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pinId))
        {
            return "Provide pin id.";
        }

        var pin = channelState.Pinboard.Pins.FirstOrDefault(p => p.Id == pinId);
        if (pin == null)
        {
            return $"Pin #{pinId} not found.";
        }

        channelState.Pinboard.Pins.Remove(pin);
        return $"Pin #{pinId} removed.";
    }

    private static string ListPins(DiscordModuleContext context, InstructionGPT.ChannelState channelState, string[] args)
    {
        if (channelState.Pinboard.Pins.Count == 0)
        {
            return "No pins yet.";
        }

        var count = DefaultListCount;
        if (TryParseIntArg(args, 0, out var parsed))
        {
            count = Clamp(parsed, 1, 50);
        }

        var lines = channelState.Pinboard.Pins
            .OrderByDescending(p => p.CreatedUtc)
            .Take(count)
            .Select(p => BuildPinLine(context, channelState, p))
            .ToList();

        return string.Join("\n", lines);
    }

    private static string SearchPins(DiscordModuleContext context, InstructionGPT.ChannelState channelState, string[] args)
    {
        if (args.Length == 0)
        {
            return "Provide search text.";
        }

        var query = string.Join(" ", args).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide search text.";
        }

        var matches = channelState.Pinboard.Pins
            .Where(p => ContainsText(p.Snippet, query) || ContainsText(p.Note, query))
            .OrderByDescending(p => p.CreatedUtc)
            .Take(50)
            .ToList();

        if (matches.Count == 0)
        {
            return "No matches.";
        }

        var lines = matches.Select(p => BuildPinLine(context, channelState, p)).ToList();
        return string.Join("\n", lines);
    }

    private static string BuildPinLine(DiscordModuleContext context, InstructionGPT.ChannelState channelState, InstructionGPT.PinboardEntry entry)
    {
        var link = BuildMessageLink(context, channelState, entry.MessageId);
        var note = string.IsNullOrWhiteSpace(entry.Note) ? string.Empty : $" | {entry.Note}";
        var author = context.Client.GetUser(entry.AuthorId)?.Username ?? $"User {entry.AuthorId}";
        return $"#{entry.Id} {link} by {author}: {entry.Snippet}{note}";
    }

    private static string BuildMessageLink(DiscordModuleContext context, InstructionGPT.ChannelState channelState, ulong messageId)
    {
        return InstructionGPT.BuildDiscordMessageLink(channelState, messageId);
    }

    private static string BuildSnippet(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "(no text)";
        }

        var trimmed = content.Trim();
        if (trimmed.Length <= MaxSnippetLength)
        {
            return trimmed;
        }

        return trimmed.Substring(0, MaxSnippetLength - 3) + "...";
    }

    private static int NextPinId(InstructionGPT.ChannelState channelState)
    {
        if (channelState.Pinboard.Pins.Count == 0)
        {
            return 1;
        }

        return channelState.Pinboard.Pins.Max(p => p.Id) + 1;
    }

    private static bool TryParseMessageId(string token, out ulong messageId, out ulong channelId)
    {
        messageId = 0;
        channelId = 0;

        if (ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            messageId = parsed;
            return true;
        }

        if (Uri.TryCreate(token, UriKind.Absolute, out var uri))
        {
            var segments = uri.Segments;
            if (segments.Length >= 4
                && ulong.TryParse(segments[^1].Trim('/'), out var parsedMessage)
                && ulong.TryParse(segments[^2].Trim('/'), out var parsedChannel))
            {
                messageId = parsedMessage;
                channelId = parsedChannel;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseIntArg(string[] args, int index, out int value)
    {
        value = 0;
        if (args.Length <= index)
        {
            return false;
        }

        return int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool ContainsText(string value, string query)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    private static async Task SendEphemeralResponseAsync(SocketSlashCommand command, string content)
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

    private sealed record PinRequest(string Command, string[] Args);
}
