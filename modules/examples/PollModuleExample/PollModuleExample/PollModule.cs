using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace PollModuleExample;

public sealed class PollModule : FeatureModuleBase
{
    public override string Id => "polls";
    public override string Name => "Polls & Voting";

    private static readonly string PollSystemPrompt =
        "You are a helpful Discord poll assistant. Keep replies short (1-3 sentences). " +
        "Use the provided poll data exactly; do not change poll options, ids, or vote counts.";

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildPollCommands())
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
            if (string.Equals(option.Name, "poll", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePollSlashCommandAsync(context, command, option, cancellationToken);
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

        if (!context.Host.IsChannelGuildMatch(channelState, message.Channel, "poll-message"))
        {
            return;
        }

        var result = ExecutePollAction(context, channelState, message.Author.Id, request);
        if (result.IsListResponse)
        {
            await message.Channel.SendMessageAsync(result.PlainText);
        }
        else
        {
            var responseText = await BuildPollResponseAsync(channelState, result);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = result.PlainText;
            }

            await message.Channel.SendMessageAsync(responseText);
        }

        await context.Host.SaveCachedChannelStateAsync(message.Channel.Id);
    }

    private static SlashCommandOptionBuilder BuildPollCommands()
    {
        return new SlashCommandOptionBuilder
        {
            Name = "poll",
            Description = "Poll commands",
            Type = ApplicationCommandOptionType.SubCommandGroup,
            Options = new()
            {
                new SlashCommandOptionBuilder().WithName("create")
                    .WithDescription("Create a poll")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("question")
                        .WithDescription("Poll question")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("options")
                        .WithDescription("Options separated by commas")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("vote")
                    .WithDescription("Vote in a poll")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Poll id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("option")
                        .WithDescription("Option number (1-based)")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("list")
                    .WithDescription("List polls")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("show")
                    .WithDescription("Show poll question and options")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Poll id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("close")
                    .WithDescription("Close a poll")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Poll id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("delete")
                    .WithDescription("Delete a poll")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Poll id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("results")
                    .WithDescription("Show poll results")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Poll id")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true))
            }
        };
    }

    private async Task HandlePollSlashCommandAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption option, CancellationToken cancellationToken)
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

        if (!context.Host.IsChannelGuildMatch(channelState, command.Channel, "poll-slash"))
        {
            await SendEphemeralResponseAsync(command, "Guild mismatch detected. Refusing to apply command.");
            return;
        }

        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            await SendEphemeralResponseAsync(command, "Specify a poll command.");
            return;
        }

        var request = BuildRequestFromSlash(subOption);
        var result = ExecutePollAction(context, channelState, command.User.Id, request);

        if (result.IsListResponse)
        {
            await SendEphemeralResponseAsync(command, result.PlainText);
        }
        else
        {
            var response = await BuildPollResponseAsync(channelState, result);
            if (string.IsNullOrWhiteSpace(response))
            {
                response = result.PlainText;
            }

            await SendEphemeralResponseAsync(command, response);
        }

        await context.Host.SaveCachedChannelStateAsync(channelState.ChannelId);
    }

    private static PollRequest BuildRequestFromSlash(SocketSlashCommandDataOption option)
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

        return new PollRequest(option.Name, args.ToArray());
    }

    private static bool TryParseMessageCommand(string content, out PollRequest request)
    {
        request = null;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("!poll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            request = new PollRequest("list", Array.Empty<string>());
            return true;
        }

        var command = tokens[1].ToLowerInvariant();
        var args = tokens.Skip(2).ToArray();
        request = new PollRequest(command, args);
        return true;
    }

    private static PollActionResult ExecutePollAction(DiscordModuleContext context, InstructionGPT.ChannelState channelState, ulong userId, PollRequest request)
    {
        channelState.Polls ??= new InstructionGPT.PollState();
        var polls = channelState.Polls.Polls;

        switch (request.Command)
        {
            case "create":
                return CreatePoll(channelState, userId, request.Args);
            case "vote":
                return VotePoll(channelState, userId, request.Args);
            case "list":
                return ListPolls(channelState);
            case "show":
                return ShowPoll(channelState, request.Args);
            case "close":
                return ClosePoll(channelState, request.Args);
            case "delete":
                return DeletePoll(channelState, request.Args);
            case "results":
                return ShowResults(channelState, request.Args);
            default:
                return new PollActionResult("Unknown poll command.", true);
        }
    }

    private static PollActionResult CreatePoll(InstructionGPT.ChannelState channelState, ulong userId, string[] args)
    {
        if (args.Length < 2)
        {
            return new PollActionResult("Provide a question and options separated by commas.", true);
        }

        var question = args[0];
        var options = SplitOptions(string.Join(" ", args.Skip(1)));
        if (options.Count < 2)
        {
            return new PollActionResult("Provide at least two options.", true);
        }

        var pollId = channelState.Polls.NextId++;
        var entry = new InstructionGPT.PollEntry
        {
            Id = pollId,
            Question = question.Trim(),
            Options = options,
            CreatedBy = userId,
            CreatedUtc = DateTime.UtcNow,
            Open = true
        };
        channelState.Polls.Polls.Add(entry);

        var details = BuildPollDetails(entry);
        return new PollActionResult($"Poll #{pollId} created.", false, details);
    }

    private static PollActionResult VotePoll(InstructionGPT.ChannelState channelState, ulong userId, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pollId) || !TryParseIntArg(args, 1, out var optionIndex))
        {
            return new PollActionResult("Provide poll id and option number.", true);
        }

        var poll = FindPoll(channelState, pollId);
        if (poll == null)
        {
            return new PollActionResult($"Poll #{pollId} not found.", true);
        }

        if (!poll.Open)
        {
            return new PollActionResult($"Poll #{pollId} is closed.", true);
        }

        var optionZero = optionIndex - 1;
        if (optionZero < 0 || optionZero >= poll.Options.Count)
        {
            return new PollActionResult("Option out of range.", true);
        }

        poll.Votes[userId] = optionZero;
        var details = BuildPollDetails(poll);
        return new PollActionResult($"Vote recorded for poll #{pollId}.", false, details);
    }

    private static PollActionResult ListPolls(InstructionGPT.ChannelState channelState)
    {
        if (channelState.Polls?.Polls == null || channelState.Polls.Polls.Count == 0)
        {
            return new PollActionResult("No polls yet.", true);
        }

        var lines = channelState.Polls.Polls
            .OrderBy(p => p.Id)
            .Select(p => $"#{p.Id} {(p.Open ? "[open]" : "[closed]")} {p.Question}")
            .ToList();

        return new PollActionResult(string.Join("\n", lines), true);
    }

    private static PollActionResult ShowPoll(InstructionGPT.ChannelState channelState, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pollId))
        {
            return new PollActionResult("Provide poll id.", true);
        }

        var poll = FindPoll(channelState, pollId);
        if (poll == null)
        {
            return new PollActionResult($"Poll #{pollId} not found.", true);
        }

        var text = BuildPollDetails(poll);
        return new PollActionResult(text, true);
    }

    private static PollActionResult ClosePoll(InstructionGPT.ChannelState channelState, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pollId))
        {
            return new PollActionResult("Provide poll id.", true);
        }

        var poll = FindPoll(channelState, pollId);
        if (poll == null)
        {
            return new PollActionResult($"Poll #{pollId} not found.", true);
        }

        poll.Open = false;
        var details = BuildPollDetails(poll);
        return new PollActionResult($"Poll #{pollId} closed.", false, details);
    }

    private static PollActionResult DeletePoll(InstructionGPT.ChannelState channelState, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pollId))
        {
            return new PollActionResult("Provide poll id.", true);
        }

        var poll = FindPoll(channelState, pollId);
        if (poll == null)
        {
            return new PollActionResult($"Poll #{pollId} not found.", true);
        }

        channelState.Polls.Polls.Remove(poll);
        return new PollActionResult($"Poll #{pollId} deleted.", false);
    }

    private static PollActionResult ShowResults(InstructionGPT.ChannelState channelState, string[] args)
    {
        if (!TryParseIntArg(args, 0, out var pollId))
        {
            return new PollActionResult("Provide poll id.", true);
        }

        var poll = FindPoll(channelState, pollId);
        if (poll == null)
        {
            return new PollActionResult($"Poll #{pollId} not found.", true);
        }

        var counts = new int[poll.Options.Count];
        foreach (var vote in poll.Votes.Values)
        {
            if (vote >= 0 && vote < counts.Length)
            {
                counts[vote]++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Poll #{poll.Id} results:");
        for (var i = 0; i < poll.Options.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {poll.Options[i]} - {counts[i]}");
        }

        return new PollActionResult($"Results ready for poll #{poll.Id}.", false, sb.ToString());
    }

    private static InstructionGPT.PollEntry FindPoll(InstructionGPT.ChannelState channelState, int pollId)
    {
        return channelState.Polls?.Polls?.FirstOrDefault(p => p.Id == pollId);
    }

    private static List<string> SplitOptions(string input)
    {
        return input.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(opt => opt.Trim())
            .Where(opt => !string.IsNullOrWhiteSpace(opt))
            .ToList();
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

    private async Task<string> BuildPollResponseAsync(InstructionGPT.ChannelState channel, PollActionResult result)
    {
        var prompt = BuildResultPrompt(result);
        var additionalMessages = new List<ChatMessage>
        {
            new(StaticValues.ChatMessageRoles.System, PollSystemPrompt),
            new(StaticValues.ChatMessageRoles.User, prompt)
        };

        var sb = new StringBuilder();
        await foreach (var response in channel.InstructionChat.GetResponseAsync(additionalMessages))
        {
            if (response.Successful)
            {
                var content = response.Choices.FirstOrDefault()?.Message.Content;
                if (content != null)
                {
                    sb.Append(content);
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string BuildResultPrompt(PollActionResult result)
    {
        var lines = new List<string>
        {
            $"Result: {result.PlainText}"
        };

        if (!string.IsNullOrWhiteSpace(result.Details))
        {
            lines.Add("Details:");
            lines.Add(result.Details);
        }

        return string.Join("\n", lines);
    }

    private static string BuildPollDetails(InstructionGPT.PollEntry poll)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Poll #{poll.Id} {(poll.Open ? "[open]" : "[closed]")}: {poll.Question}");
        for (var i = 0; i < poll.Options.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {poll.Options[i]}");
        }

        return sb.ToString().Trim();
    }

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

    private sealed record PollRequest(string Command, string[] Args);

    private sealed record PollActionResult(string PlainText, bool IsListResponse, string Details = null);
}
