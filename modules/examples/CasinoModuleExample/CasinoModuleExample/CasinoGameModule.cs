using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace CasinoModuleExample;

public sealed class CasinoGameModule : FeatureModuleBase
{
    public override string Id => "casino";
    public override string Name => "Casino Game Mode";

    private const decimal DefaultBet = 1m;
    private const int DefaultDiceSides = 6;
    private const int MaxDiceSides = 100;

    private static readonly string DealerSystemPrompt =
        "You are a friendly casino dealer. Use the provided game results verbatim. " +
        "Keep the reply short (1-3 sentences), upbeat, and clear. Do not change any numbers. " +
        "If balance or leaderboard details are provided, repeat them clearly.";

    private static readonly string[] SlotSymbols = { "🍒", "🍋", "🔔", "7️⃣", "🍀" };

    private static readonly HashSet<int> RouletteRed = new()
    {
        1, 3, 5, 7, 9, 12, 14, 16, 18,
        19, 21, 23, 25, 27, 30, 32, 34, 36
    };

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildCasinoCommands()),
            SlashCommandContribution.ForOption("set", BuildCasinoToggleOption())
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
            if (string.Equals(option.Name, "casino", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCasinoSlashCommandAsync(context, command, option, cancellationToken);
                handled = true;
            }
            else if (string.Equals(option.Name, "set", StringComparison.OrdinalIgnoreCase))
            {
                var subOption = option.Options?.FirstOrDefault();
                if (subOption != null && string.Equals(subOption.Name, "casino", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCasinoSettingAsync(context, command, subOption, cancellationToken);
                    handled = true;
                }
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

        var channel = context.Host.GetOrCreateChannelState(message.Channel);
        if (!channel.Options.CasinoEnabled)
        {
            return;
        }
        if (!channel.Options.Enabled || channel.Options.Muted)
        {
            return;
        }

        if (message.MentionedUsers.Any(user => user.Id == context.Client.CurrentUser.Id))
        {
            return;
        }

        if (!TryParseMessageCommand(message.Content, out var request))
        {
            return;
        }

        var result = ExecuteGame(context, channel, message.Author.Id, request);
        var responseText = await BuildDealerResponseAsync(channel, result);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            responseText = BuildFallbackResponse(result);
        }

        await message.Channel.SendMessageAsync(responseText);
        channel.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.Assistant, responseText));
        await context.Host.SaveCachedChannelStateAsync(message.Channel.Id);
    }

    private static SlashCommandOptionBuilder BuildCasinoCommands()
    {
        return new SlashCommandOptionBuilder
        {
            Name = "casino",
            Description = "Casino wallet and controls (gameplay uses !commands)",
            Type = ApplicationCommandOptionType.SubCommandGroup,
            Options = new()
            {
                new SlashCommandOptionBuilder().WithName("help")
                    .WithDescription("Show casino help")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("buy")
                    .WithDescription("Purchase casino credits")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("amount")
                        .WithDescription("Amount to purchase")
                        .WithType(ApplicationCommandOptionType.Number)),
                new SlashCommandOptionBuilder().WithName("wallet")
                    .WithDescription("Check a wallet balance")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("user")
                        .WithDescription("User to check")
                        .WithType(ApplicationCommandOptionType.User)),
                new SlashCommandOptionBuilder().WithName("personal")
                    .WithDescription("Show your casino info")
                    .WithType(ApplicationCommandOptionType.SubCommand)
            }
        };
    }

    private static SlashCommandOptionBuilder BuildCasinoToggleOption()
    {
        return new SlashCommandOptionBuilder().WithName("casino")
            .WithDescription("Enable or disable casino game mode")
            .WithType(ApplicationCommandOptionType.Boolean);
    }

    private static bool TryParseMessageCommand(string content, out GameRequest request)
    {
        request = null;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var first = tokens[0].TrimStart('!').ToLowerInvariant();
        var isCasinoPrefix = string.Equals(first, "casino", StringComparison.OrdinalIgnoreCase);

        if (isCasinoPrefix && tokens.Length == 1)
        {
            request = new GameRequest(GameType.Help, Array.Empty<string>());
            return true;
        }

        var gameToken = isCasinoPrefix ? tokens[1].ToLowerInvariant() : first;
        var args = isCasinoPrefix ? tokens.Skip(2).ToArray() : tokens.Skip(1).ToArray();
        if (!TryParseGameType(gameToken, out var gameType))
        {
            request = new GameRequest(GameType.Help, Array.Empty<string>());
            return true;
        }

        request = new GameRequest(gameType, args);
        return true;
    }

    private static bool TryParseGameType(string token, out GameType gameType)
    {
        switch (token)
        {
            case "help":
                gameType = GameType.Help;
                return true;
            case "status":
                gameType = GameType.Status;
                return true;
            case "personal":
            case "me":
                gameType = GameType.Personal;
                return true;
            case "coinflip":
            case "flip":
                gameType = GameType.Coinflip;
                return true;
            case "dice":
            case "roll":
                gameType = GameType.Dice;
                return true;
            case "roulette":
                gameType = GameType.Roulette;
                return true;
            case "slots":
                gameType = GameType.Slots;
                return true;
            case "blackjack":
            case "bj":
                gameType = GameType.Blackjack;
                return true;
            case "buy":
            case "purchase":
                gameType = GameType.Purchase;
                return true;
            case "wallet":
            case "balance":
                gameType = GameType.Wallet;
                return true;
            case "leaderboard":
            case "top":
                gameType = GameType.Leaderboard;
                return true;
            default:
                gameType = GameType.Help;
                return false;
        }
    }

    private async Task HandleCasinoSettingAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption subOption, CancellationToken cancellationToken)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channel = command.Channel;
        var channelState = context.Host.GetOrCreateChannelState(channel);
        if (!context.Host.IsChannelGuildMatch(channelState, channel, "slash-command"))
        {
            await SendEphemeralResponseAsync(command,
                "Guild mismatch detected for cached channel data. Refusing to apply command.");
            return;
        }

        var responses = new List<string>();
        if (subOption.Value is bool enabled)
        {
            channelState.Options.CasinoEnabled = enabled;
            responses.Add($"Casino mode {(enabled ? "enabled" : "disabled")}. Use !casino help for commands.");
        }
        else
        {
            responses.Add("Provide true or false for casino setting.");
        }

        await SendEphemeralResponseAsync(command, string.Join("\n", responses));
        await context.Host.SaveCachedChannelStateAsync(channel.Id);
    }

    private async Task HandleCasinoSlashCommandAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption option, CancellationToken cancellationToken)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        var channel = command.Channel;
        var channelState = context.Host.GetOrCreateChannelState(channel);
        if (!context.Host.IsChannelGuildMatch(channelState, channel, "slash-command"))
        {
            await SendEphemeralResponseAsync(command,
                "Guild mismatch detected for cached channel data. Refusing to apply command.");
            return;
        }

        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            await SendEphemeralResponseAsync(command, "Specify a casino command.");
            return;
        }

        if (!TryParseGameType(subOption.Name, out var gameType))
        {
            await SendEphemeralResponseAsync(command, "Unknown casino command.");
            return;
        }

        // Slash commands are intentionally limited; gameplay is via message commands for immersion.
        if (gameType is GameType.Coinflip or GameType.Dice or GameType.Roulette or GameType.Slots or GameType.Blackjack or GameType.Leaderboard or GameType.Status)
        {
            await SendEphemeralResponseAsync(command,
                "Casino gameplay is now message-based. Try: `!coinflip`, `!dice`, `!roulette`, `!slots`, `!blackjack`, `!leaderboard`.");
            return;
        }

        var request = BuildRequestFromSlash(gameType, subOption);
        var result = ExecuteGame(context, channelState, command.User.Id, request);
        var response = await BuildDealerResponseAsync(channelState, result);
        if (string.IsNullOrWhiteSpace(response))
        {
            response = BuildFallbackResponse(result);
        }

        await SendEphemeralResponseAsync(command, response);
        await context.Host.SaveCachedChannelStateAsync(channel.Id);
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

    private static GameRequest BuildRequestFromSlash(GameType gameType, SocketSlashCommandDataOption subOption)
    {
        var args = new List<string>();
        foreach (var option in subOption.Options ?? Enumerable.Empty<SocketSlashCommandDataOption>())
        {
            if (option.Value == null)
            {
                continue;
            }

            if (option.Value is SocketUser user)
            {
                args.Add(user.Id.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            args.Add(option.Value.ToString());
        }

        return new GameRequest(gameType, args.ToArray());
    }

    private static GameResult ExecuteGame(DiscordModuleContext context, InstructionGPT.ChannelState channelState, ulong userId, GameRequest request)
    {
        channelState.CasinoBalances ??= new Dictionary<ulong, decimal>();
        var casinoEnabled = channelState.Options.CasinoEnabled;
        return request.Game switch
        {
            GameType.Help => BuildHelpResult(),
            GameType.Status => BuildStatusResult(casinoEnabled),
            GameType.Personal => BuildPersonalResult(context, channelState, userId, casinoEnabled),
            GameType.Purchase => RunPurchase(channelState, userId, request.Args),
            GameType.Wallet => RunWallet(context, channelState, userId, request.Args),
            GameType.Leaderboard => RunLeaderboard(context, channelState, request.Args),
            GameType.Coinflip => RunWithWallet(channelState, userId, request.Args, RunCoinflip, "Coinflip"),
            GameType.Dice => RunWithWallet(channelState, userId, request.Args, RunDice, "Dice"),
            GameType.Roulette => RunWithWallet(channelState, userId, request.Args, RunRoulette, "Roulette"),
            GameType.Slots => RunWithWallet(channelState, userId, request.Args, RunSlots, "Slots"),
            GameType.Blackjack => RunWithWallet(channelState, userId, request.Args, RunBlackjack, "Blackjack"),
            _ => BuildHelpResult()
        };
    }

    private static GameResult BuildHelpResult()
    {
        return new GameResult(
            "Casino Help",
            BuildHelpText(),
            null,
            null,
            "Use /gptcli set casino true to enable message commands."
        );
    }

    private static GameResult BuildPersonalResult(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        ulong userId,
        bool casinoEnabled)
    {
        var balance = GetBalance(channelState, userId);
        var name = ResolveUserDisplayName(context, channelState, userId);
        var status = casinoEnabled ? "enabled" : "disabled";
        var details = string.Join("\n", new[]
        {
            $"User: {name}",
            $"Casino mode: {status}",
            $"Balance: {balance:0.##}",
            "Gameplay: !coinflip, !dice, !roulette, !slots, !blackjack, !leaderboard"
        });
        return new GameResult("Personal", "Your casino info.", null, null, details, balance);
    }

    private static GameResult BuildStatusResult(bool enabled)
    {
        var status = enabled ? "enabled" : "disabled";
        return new GameResult("Casino Status", $"Casino mode is {status}.", null, null, null);
    }

    private static GameResult RunCoinflip(string[] args)
    {
        string guess = null;
        var bet = DefaultBet;
        foreach (var arg in args)
        {
            var normalized = NormalizeHeadsTails(arg);
            if (normalized != null)
            {
                guess = normalized;
                continue;
            }

            if (TryParseDecimal(arg, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }
        var flip = Random.Shared.Next(2) == 0 ? "heads" : "tails";
        var outcome = $"Coin landed on {flip}.";
        var net = ComputeNet(guess != null, guess == flip, bet, bet);
        var details = guess == null ? "No guess placed." : $"Guess: {guess}.";
        return new GameResult("Coinflip", outcome, bet, net, details);
    }

    private static GameResult RunPurchase(InstructionGPT.ChannelState channelState, ulong userId, string[] args)
    {
        var amount = 0m;
        if (args.Length > 0 && TryParseDecimal(args[0], out var parsed) && parsed > 0)
        {
            amount = parsed;
        }
        if (amount <= 0)
        {
            return new GameResult("Purchase", "No purchase amount provided.", null, null, "Provide an amount like: !casino buy 100.");
        }

        var balance = GetBalance(channelState, userId);
        var updated = balance + amount;
        SetBalance(channelState, userId, updated);

        var details = $"Purchased {amount:0.##}.";
        return new GameResult("Purchase", "Purchase completed.", amount, null, details, updated);
    }

    private static GameResult RunWallet(DiscordModuleContext context, InstructionGPT.ChannelState channelState, ulong requesterId, string[] args)
    {
        var targetId = requesterId;
        if (args.Length > 0 && TryParseUserId(args[0], out var parsed))
        {
            targetId = parsed;
        }

        var balance = GetBalance(channelState, targetId);
        var name = ResolveUserDisplayName(context, channelState, targetId);
        var details = $"User: {name}. Balance: {balance:0.##}.";
        return new GameResult("Wallet", "Wallet balance check.", null, null, details, balance);
    }

    private static GameResult RunLeaderboard(DiscordModuleContext context, InstructionGPT.ChannelState channelState, string[] args)
    {
        var count = ParseTopCount(args);
        if (channelState.CasinoBalances == null || channelState.CasinoBalances.Count == 0)
        {
            return new GameResult("Leaderboard", "No balances yet.", null, null, "No one has purchased credits yet.");
        }

        var lines = channelState.CasinoBalances
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(count)
            .Select((pair, index) =>
            {
                var name = ResolveUserDisplayName(context, channelState, pair.Key);
                return $"{index + 1}. {name} - {pair.Value:0.##}";
            })
            .ToList();

        var details = string.Join("\n", lines);
        return new GameResult("Leaderboard", $"Top {lines.Count} balances.", null, null, details);
    }

    private static GameResult RunWithWallet(
        InstructionGPT.ChannelState channelState,
        ulong userId,
        string[] args,
        Func<string[], GameResult> runner,
        string gameName)
    {
        var (requiresBet, bet) = GetBetRequirement(gameName, args);
        var balance = GetBalance(channelState, userId);

        if (requiresBet && balance < bet)
        {
            var details = $"Balance: {balance:0.##}. Need {bet:0.##} to place that bet.";
            return new GameResult(gameName, "Insufficient funds.", bet, null, details, balance);
        }

        var result = runner(args);
        if (result.Net.HasValue && result.Bet.HasValue)
        {
            var updated = balance + result.Net.Value;
            SetBalance(channelState, userId, updated);
            return result with { Balance = updated };
        }

        return result with { Balance = balance };
    }

    private static GameResult RunDice(string[] args)
    {
        var sides = DefaultDiceSides;
        var guess = (int?)null;
        var bet = DefaultBet;

        var numbers = new List<int>();
        foreach (var arg in args)
        {
            if (TryParseInt(arg, out var parsed))
            {
                numbers.Add(parsed);
                continue;
            }

            if (TryParseDecimal(arg, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }

        if (numbers.Count >= 2)
        {
            sides = Clamp(numbers[0], 2, MaxDiceSides);
            guess = numbers[1];
            if (numbers.Count >= 3)
            {
                bet = numbers[2] > 0 ? numbers[2] : bet;
            }
        }
        else if (numbers.Count == 1)
        {
            guess = numbers[0];
        }

        var roll = Random.Shared.Next(1, sides + 1);
        var outcome = $"Rolled a {roll} on a d{sides}.";
        var winAmount = bet * Math.Max(1, sides - 1);
        var net = ComputeNet(guess.HasValue, guess == roll, bet, winAmount);
        var details = guess.HasValue ? $"Guess: {guess.Value}." : "No guess placed.";
        return new GameResult("Dice", outcome, bet, net, details);
    }

    private static GameResult RunRoulette(string[] args)
    {
        var choice = "red";
        var bet = DefaultBet;
        var numbers = new List<int>();
        var hasColorChoice = false;

        foreach (var arg in args)
        {
            var trimmed = arg.Trim().ToLowerInvariant();
            if (trimmed is "red" or "black")
            {
                choice = trimmed;
                hasColorChoice = true;
                continue;
            }

            if (TryParseInt(trimmed, out var parsedNumber))
            {
                numbers.Add(parsedNumber);
                continue;
            }

            if (TryParseDecimal(trimmed, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }

        if (numbers.Count > 0)
        {
            if (!hasColorChoice)
            {
                choice = numbers[0].ToString(CultureInfo.InvariantCulture);
                if (numbers.Count > 1)
                {
                    bet = numbers[1] > 0 ? numbers[1] : bet;
                }
            }
            else
            {
                bet = numbers[0] > 0 ? numbers[0] : bet;
            }
        }

        var number = Random.Shared.Next(0, 37);
        var color = number == 0 ? "green" : (RouletteRed.Contains(number) ? "red" : "black");

        var outcome = $"Roulette landed on {number} ({color}).";
        var isNumberBet = int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosenNumber);
        var winAmount = isNumberBet ? bet * 35 : bet;
        var win = isNumberBet ? chosenNumber == number : string.Equals(choice, color, StringComparison.OrdinalIgnoreCase);
        var net = ComputeNet(true, win, bet, winAmount);
        var details = isNumberBet ? $"Bet on number {chosenNumber}." : $"Bet on {choice}.";
        return new GameResult("Roulette", outcome, bet, net, details);
    }

    private static GameResult RunSlots(string[] args)
    {
        var bet = args.Length > 0 ? ParseBet(args[0]) : DefaultBet;
        var reel1 = SlotSymbols[Random.Shared.Next(SlotSymbols.Length)];
        var reel2 = SlotSymbols[Random.Shared.Next(SlotSymbols.Length)];
        var reel3 = SlotSymbols[Random.Shared.Next(SlotSymbols.Length)];

        var outcome = $"Slots: [{reel1} | {reel2} | {reel3}]";
        var matches = CountMatches(reel1, reel2, reel3);
        decimal winAmount;
        string details;
        if (matches == 3)
        {
            winAmount = bet * 10;
            details = "Jackpot (3 of a kind).";
        }
        else if (matches == 2)
        {
            winAmount = bet * 2;
            details = "Two of a kind.";
        }
        else
        {
            winAmount = 0m;
            details = "No match.";
        }

        var net = winAmount - bet;
        return new GameResult("Slots", outcome, bet, net, details);
    }

    private static GameResult RunBlackjack(string[] args)
    {
        var bet = args.Length > 0 ? ParseBet(args[0]) : DefaultBet;
        var playerHand = new List<Card> { DrawCard(), DrawCard() };
        var dealerHand = new List<Card> { DrawCard(), DrawCard() };

        while (HandValue(dealerHand) < 17)
        {
            dealerHand.Add(DrawCard());
        }

        var playerValue = HandValue(playerHand);
        var dealerValue = HandValue(dealerHand);

        var playerBust = playerValue > 21;
        var dealerBust = dealerValue > 21;
        bool win;
        if (playerBust)
        {
            win = false;
        }
        else if (dealerBust)
        {
            win = true;
        }
        else if (playerValue > dealerValue)
        {
            win = true;
        }
        else if (playerValue < dealerValue)
        {
            win = false;
        }
        else
        {
            win = false;
        }

        var net = win ? bet : -bet;
        var outcome = $"Player {playerValue} vs Dealer {dealerValue}.";
        var details = $"Player: {FormatHand(playerHand)} | Dealer: {FormatHand(dealerHand)}";
        return new GameResult("Blackjack", outcome, bet, net, details);
    }

    private static string BuildHelpText()
    {
        return string.Join("\n", new[]
        {
            "Casino commands:",
            "- /gptcli set casino true|false (enable or disable message commands)",
            "- /gptcli casino help",
            "- /gptcli casino buy [amount]",
            "- /gptcli casino wallet [user]",
            "- /gptcli casino personal",
            "Gameplay (message mode, when enabled):",
            "- !coinflip heads 5",
            "- !dice 6 3 2",
            "- !roulette red 10",
            "- !slots 2",
            "- !blackjack 5",
            "- !leaderboard 5",
            "Compatibility aliases (also work):",
            "- !casino coinflip heads 5",
            "- !casino dice 6 3 2",
            "- !casino roulette red 10",
            "- !casino slots 2",
            "- !casino blackjack 5",
            "- !casino leaderboard 5"
        });
    }

    private static decimal GetBalance(InstructionGPT.ChannelState channelState, ulong userId)
    {
        channelState.CasinoBalances ??= new Dictionary<ulong, decimal>();
        return channelState.CasinoBalances.TryGetValue(userId, out var balance) ? balance : 0m;
    }

    private static void SetBalance(InstructionGPT.ChannelState channelState, ulong userId, decimal balance)
    {
        channelState.CasinoBalances ??= new Dictionary<ulong, decimal>();
        channelState.CasinoBalances[userId] = Math.Max(0m, balance);
    }

    private static (bool RequiresBet, decimal Bet) GetBetRequirement(string gameName, string[] args)
    {
        switch (gameName)
        {
            case "Coinflip":
                return GetCoinflipBet(args);
            case "Dice":
                return GetDiceBet(args);
            case "Roulette":
                return GetRouletteBet(args);
            case "Slots":
            case "Blackjack":
                var bet = args.Length > 0 ? ParseBet(args[0]) : DefaultBet;
                return (true, bet);
            default:
                return (false, 0m);
        }
    }

    private static (bool RequiresBet, decimal Bet) GetCoinflipBet(string[] args)
    {
        string guess = null;
        var bet = DefaultBet;
        foreach (var arg in args)
        {
            var normalized = NormalizeHeadsTails(arg);
            if (normalized != null)
            {
                guess = normalized;
                continue;
            }

            if (TryParseDecimal(arg, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }

        return (guess != null, bet);
    }

    private static (bool RequiresBet, decimal Bet) GetDiceBet(string[] args)
    {
        var guess = (int?)null;
        var bet = DefaultBet;
        var numbers = new List<int>();
        foreach (var arg in args)
        {
            if (TryParseInt(arg, out var parsed))
            {
                numbers.Add(parsed);
                continue;
            }

            if (TryParseDecimal(arg, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }

        if (numbers.Count >= 2)
        {
            guess = numbers[1];
            if (numbers.Count >= 3)
            {
                bet = numbers[2] > 0 ? numbers[2] : bet;
            }
        }
        else if (numbers.Count == 1)
        {
            guess = numbers[0];
        }

        return (guess.HasValue, bet);
    }

    private static (bool RequiresBet, decimal Bet) GetRouletteBet(string[] args)
    {
        var bet = DefaultBet;
        foreach (var arg in args)
        {
            if (TryParseDecimal(arg, out var parsedBet) && parsedBet > 0)
            {
                bet = parsedBet;
            }
        }

        return (true, bet);
    }

    private static bool TryParseUserId(string token, out ulong userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (trimmed.StartsWith("<@") && trimmed.EndsWith(">"))
        {
            trimmed = trimmed.Trim('<', '>', '@', '!');
        }

        return ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
    }

    private static int ParseTopCount(string[] args)
    {
        if (args.Length == 0)
        {
            return 5;
        }

        if (TryParseInt(args[0], out var parsed))
        {
            return Clamp(parsed, 1, 10);
        }

        return 5;
    }

    private static string ResolveUserDisplayName(DiscordModuleContext context, InstructionGPT.ChannelState channelState, ulong userId)
    {
        var channel = context.Client.GetChannel(channelState.ChannelId);
        if (channel is SocketGuildChannel guildChannel)
        {
            var user = guildChannel.Guild.GetUser(userId);
            if (user != null)
            {
                return user.Nickname ?? user.Username;
            }
        }

        var fallback = context.Client.GetUser(userId);
        return fallback?.Username ?? $"User {userId}";
    }

    private static string NormalizeHeadsTails(string value)
    {
        var cleaned = value.Trim().ToLowerInvariant();
        if (cleaned is "heads" or "head")
        {
            return "heads";
        }
        if (cleaned is "tails" or "tail")
        {
            return "tails";
        }
        return null;
    }

    private static decimal ParseBet(string value)
    {
        if (TryParseDecimal(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }
        return DefaultBet;
    }

    private static decimal ComputeNet(bool hasBet, bool win, decimal bet, decimal winAmount)
    {
        if (!hasBet)
        {
            return 0m;
        }

        return win ? winAmount : -bet;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    private static int CountMatches(string a, string b, string c)
    {
        if (a == b && b == c)
        {
            return 3;
        }

        if (a == b || a == c || b == c)
        {
            return 2;
        }

        return 0;
    }

    private static Card DrawCard()
    {
        var rank = CardRanks[Random.Shared.Next(CardRanks.Length)];
        var suit = CardSuits[Random.Shared.Next(CardSuits.Length)];
        return new Card(rank, suit, CardValue(rank));
    }

    private static int CardValue(string rank)
    {
        return rank switch
        {
            "A" => 11,
            "K" or "Q" or "J" => 10,
            _ => int.Parse(rank, CultureInfo.InvariantCulture)
        };
    }

    private static int HandValue(List<Card> hand)
    {
        var total = hand.Sum(card => card.Value);
        var aces = hand.Count(card => card.Rank == "A");
        while (total > 21 && aces > 0)
        {
            total -= 10;
            aces--;
        }

        return total;
    }

    private static string FormatHand(List<Card> hand)
    {
        return string.Join(", ", hand.Select(card => card.Label));
    }

    private async Task<string> BuildDealerResponseAsync(InstructionGPT.ChannelState channel, GameResult result)
    {
        var prompt = BuildResultPrompt(result);
        var additionalMessages = new List<ChatMessage>
        {
            new(StaticValues.ChatMessageRoles.System, DealerSystemPrompt),
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

    private static string BuildResultPrompt(GameResult result)
    {
        var lines = new List<string>
        {
            $"Game: {result.Game}",
            $"Outcome: {result.Outcome}"
        };

        if (result.Bet.HasValue)
        {
            lines.Add($"Bet: {result.Bet.Value:0.##}");
        }

        if (result.Net.HasValue)
        {
            lines.Add($"Net: {FormatSigned(result.Net.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(result.Details))
        {
            lines.Add($"Details: {result.Details}");
        }

        if (result.Balance.HasValue)
        {
            lines.Add($"Balance: {result.Balance.Value:0.##}");
        }

        return string.Join("\n", lines);
    }

    private static string BuildFallbackResponse(GameResult result)
    {
        var lines = new List<string>
        {
            $"{result.Game}: {result.Outcome}"
        };

        if (result.Bet.HasValue)
        {
            lines.Add($"Bet: {result.Bet.Value:0.##}");
        }

        if (result.Net.HasValue)
        {
            lines.Add($"Net: {FormatSigned(result.Net.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(result.Details))
        {
            lines.Add(result.Details);
        }

        if (result.Balance.HasValue)
        {
            lines.Add($"Balance: {result.Balance.Value:0.##}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatSigned(decimal value)
    {
        return value >= 0
            ? $"+{value.ToString("0.##", CultureInfo.InvariantCulture)}"
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static bool TryParseInt(string input, out int value)
    {
        return int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
               || int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryParseDecimal(string input, out decimal value)
    {
        return decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(input, NumberStyles.Number, CultureInfo.CurrentCulture, out value);
    }

    private enum GameType
    {
        Help,
        Status,
        Personal,
        Purchase,
        Wallet,
        Leaderboard,
        Coinflip,
        Dice,
        Roulette,
        Slots,
        Blackjack
    }

    private sealed record GameRequest(GameType Game, string[] Args);

    private sealed record GameResult(string Game, string Outcome, decimal? Bet, decimal? Net, string Details, decimal? Balance = null);

    private sealed record Card(string Rank, string Suit, int Value)
    {
        public string Label => $"{Rank}{Suit}";
    }

    private static readonly string[] CardRanks =
    {
        "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K"
    };

    private static readonly string[] CardSuits =
    {
        "♠️", "♥️", "♦️", "♣️"
    };
}
