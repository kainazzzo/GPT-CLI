using System.Globalization;
using System.Text;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Modules;

namespace WelcomeModuleExample;

public sealed class WelcomeOnboardingModule : FeatureModuleBase
{
    public override string Id => "welcome";
    public override string Name => "Welcome & Onboarding";

    private const int MaxRulesPerMessage = 25;
    private static readonly TimeSpan NudgeCooldown = TimeSpan.FromMinutes(10);

    private static readonly string[] ValidationTypes = { "acknowledge", "reaction", "phrase" };

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildWelcomeCommands())
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
            if (string.Equals(option.Name, "welcome", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWelcomeSlashCommandAsync(context, command, option, cancellationToken);
                handled = true;
            }
        }

        return handled;
    }

    public override async Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken)
    {
        if (message == null || message.Author.IsBot)
        {
            return;
        }

        if (message.Channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        var configState = FindWelcomeConfigState(context, guildChannel.Guild.Id);
        if (configState == null)
        {
            return;
        }

        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        if (!welcome.Enabled)
        {
            return;
        }

        var userState = GetOrCreateUserState(welcome, message.Author.Id);
        if (userState.Completed)
        {
            return;
        }

        var updated = false;
        if (welcome.WelcomeChannelId.HasValue && message.Channel.Id == welcome.WelcomeChannelId.Value)
        {
            updated = ApplyPhraseValidation(welcome, userState, message.Content) || updated;
        }

        var guildUser = message.Author as SocketGuildUser;
        if (guildUser != null)
        {
            updated |= await TryCompleteOnboardingAsync(context, configState, guildChannel.Guild, guildUser, userState);
        }

        if (welcome.RequireOnboarding && !userState.Completed)
        {
            if (ShouldNudge(userState))
            {
                userState.LastNudgeUtc = DateTime.UtcNow;
                await SendNudgeAsync(context, configState, guildChannel.Guild, message.Channel, (SocketGuildUser)message.Author);
                updated = true;
            }
        }

        if (updated)
        {
            await context.Host.SaveCachedChannelStateAsync(configState.ChannelId);
        }
    }

    public override async Task OnReactionAddedAsync(DiscordModuleContext context, Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, CancellationToken cancellationToken)
    {
        if (reaction.UserId == context.Client.CurrentUser.Id)
        {
            return;
        }

        var reactionChannel = await channel.GetOrDownloadAsync();
        if (reactionChannel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        var configState = FindWelcomeConfigState(context, guildChannel.Guild.Id);
        if (configState == null)
        {
            return;
        }

        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        if (!welcome.Enabled || welcome.RulesMessageId == null)
        {
            return;
        }

        if (reaction.MessageId != welcome.RulesMessageId.Value)
        {
            return;
        }

        var user = guildChannel.Guild.GetUser(reaction.UserId);
        if (user == null || user.IsBot)
        {
            return;
        }

        var userState = GetOrCreateUserState(welcome, reaction.UserId);
        if (userState.Completed)
        {
            return;
        }

        var updated = ApplyReactionValidation(welcome, userState, reaction.Emote?.Name);
        updated |= await TryCompleteOnboardingAsync(context, configState, guildChannel.Guild, user, userState);

        if (updated)
        {
            await context.Host.SaveCachedChannelStateAsync(configState.ChannelId);
        }
    }

    private static SlashCommandOptionBuilder BuildWelcomeCommands()
    {
        return new SlashCommandOptionBuilder
        {
            Name = "welcome",
            Description = "Welcome and onboarding configuration",
            Type = ApplicationCommandOptionType.SubCommandGroup,
            Options = new()
            {
                new SlashCommandOptionBuilder().WithName("status")
                    .WithDescription("Show onboarding status")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("enable")
                    .WithDescription("Enable or disable onboarding")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("value")
                        .WithDescription("true or false")
                        .WithType(ApplicationCommandOptionType.Boolean)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("require")
                    .WithDescription("Require onboarding before chatting")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("value")
                        .WithDescription("true or false")
                        .WithType(ApplicationCommandOptionType.Boolean)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("setchannel")
                    .WithDescription("Set the welcome channel")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("channel")
                        .WithDescription("Welcome channel")
                        .WithType(ApplicationCommandOptionType.Channel)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("setrole")
                    .WithDescription("Set the role to grant after onboarding")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("role")
                        .WithDescription("Role to grant")
                        .WithType(ApplicationCommandOptionType.Role)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("post")
                    .WithDescription("Post the rules to the welcome channel")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("acknowledge")
                    .WithDescription("Acknowledge the rules")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder
                {
                    Name = "rule",
                    Description = "Manage welcome rules",
                    Type = ApplicationCommandOptionType.SubCommandGroup,
                    Options = new()
                    {
                        new SlashCommandOptionBuilder().WithName("add")
                            .WithDescription("Add a rule")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("text")
                                .WithDescription("Rule text")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithRequired(true)),
                        new SlashCommandOptionBuilder().WithName("update")
                            .WithDescription("Update a rule by index")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("index")
                                .WithDescription("1-based rule index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true))
                            .AddOption(new SlashCommandOptionBuilder().WithName("text")
                                .WithDescription("New rule text")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithRequired(true)),
                        new SlashCommandOptionBuilder().WithName("delete")
                            .WithDescription("Delete a rule by index")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("index")
                                .WithDescription("1-based rule index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true)),
                        new SlashCommandOptionBuilder().WithName("list")
                            .WithDescription("List rules")
                            .WithType(ApplicationCommandOptionType.SubCommand),
                        new SlashCommandOptionBuilder().WithName("move")
                            .WithDescription("Move a rule from one index to another")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("from")
                                .WithDescription("1-based source index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true))
                            .AddOption(new SlashCommandOptionBuilder().WithName("to")
                                .WithDescription("1-based destination index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true))
                    }
                },
                new SlashCommandOptionBuilder
                {
                    Name = "validation",
                    Description = "Manage onboarding validations",
                    Type = ApplicationCommandOptionType.SubCommandGroup,
                    Options = new()
                    {
                        new SlashCommandOptionBuilder().WithName("add")
                            .WithDescription("Add a validation rule")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("type")
                                .WithDescription("acknowledge, reaction, or phrase")
                                .WithType(ApplicationCommandOptionType.String)
                                .AddChoice("acknowledge", "acknowledge")
                                .AddChoice("reaction", "reaction")
                                .AddChoice("phrase", "phrase")
                                .WithRequired(true))
                            .AddOption(new SlashCommandOptionBuilder().WithName("value")
                                .WithDescription("Emoji for reaction, or phrase to say")
                                .WithType(ApplicationCommandOptionType.String)),
                        new SlashCommandOptionBuilder().WithName("delete")
                            .WithDescription("Delete a validation by index")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("index")
                                .WithDescription("1-based validation index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true)),
                        new SlashCommandOptionBuilder().WithName("list")
                            .WithDescription("List validations")
                            .WithType(ApplicationCommandOptionType.SubCommand),
                        new SlashCommandOptionBuilder().WithName("move")
                            .WithDescription("Move a validation from one index to another")
                            .WithType(ApplicationCommandOptionType.SubCommand)
                            .AddOption(new SlashCommandOptionBuilder().WithName("from")
                                .WithDescription("1-based source index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true))
                            .AddOption(new SlashCommandOptionBuilder().WithName("to")
                                .WithDescription("1-based destination index")
                                .WithType(ApplicationCommandOptionType.Integer)
                                .WithRequired(true))
                    }
                }
            }
        };
    }

    private static InstructionGPT.ChannelState FindWelcomeConfigState(DiscordModuleContext context, ulong guildId)
    {
        foreach (var entry in context.ChannelStates)
        {
            var state = entry.Value;
            if (state?.Welcome == null || !state.Welcome.Enabled)
            {
                continue;
            }

            if (state.GuildId == guildId || (state.Welcome.WelcomeChannelId.HasValue && state.Welcome.WelcomeChannelId.Value == state.ChannelId))
            {
                return state;
            }
        }

        return null;
    }

    private async Task HandleWelcomeSlashCommandAsync(DiscordModuleContext context, SocketSlashCommand command, SocketSlashCommandDataOption option, CancellationToken cancellationToken)
    {
        if (!command.HasResponded)
        {
            await command.DeferAsync(ephemeral: true);
        }

        if (command.Channel is not SocketGuildChannel guildChannel)
        {
            await SendEphemeralResponseAsync(command, "Welcome commands can only be used in a server.");
            return;
        }

        var configState = context.Host.GetOrCreateChannelState(command.Channel);
        context.Host.EnsureChannelStateMetadata(configState, guildChannel);
        if (!context.Host.IsChannelGuildMatch(configState, command.Channel, "welcome-slash"))
        {
            await SendEphemeralResponseAsync(command, "Guild mismatch detected. Refusing to apply command.");
            return;
        }

        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            await SendEphemeralResponseAsync(command, "Specify a welcome command.");
            return;
        }

        var response = await HandleWelcomeSubcommandAsync(context, configState, guildChannel.Guild, command, subOption);
        await SendEphemeralResponseAsync(command, response);
        await context.Host.SaveCachedChannelStateAsync(configState.ChannelId);
    }

    private async Task<string> HandleWelcomeSubcommandAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState configState,
        SocketGuild guild,
        SocketSlashCommand command,
        SocketSlashCommandDataOption subOption)
    {
        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();

        switch (subOption.Name)
        {
            case "status":
                return BuildStatusResponse(welcome, guild);
            case "enable":
                return RequireAdmin(command) ? SetEnabled(welcome, subOption) : "You need Manage Server to change onboarding settings.";
            case "require":
                return RequireAdmin(command) ? SetRequire(welcome, subOption) : "You need Manage Server to change onboarding settings.";
            case "setchannel":
                return RequireAdmin(command) ? SetWelcomeChannel(welcome, subOption) : "You need Manage Server to change onboarding settings.";
            case "setrole":
                return RequireAdmin(command) ? SetWelcomeRole(welcome, subOption) : "You need Manage Server to change onboarding settings.";
            case "post":
                return RequireAdmin(command) ? await PostRulesAsync(context, configState, guild) : "You need Manage Server to post rules.";
            case "acknowledge":
                return await AcknowledgeAsync(context, configState, guild, command.User);
            case "rule":
                return RequireAdmin(command) ? HandleRuleCommand(welcome, subOption) : "You need Manage Server to manage rules.";
            case "validation":
                return RequireAdmin(command) ? HandleValidationCommand(welcome, subOption) : "You need Manage Server to manage validations.";
            default:
                return "Unknown welcome command.";
        }
    }

    private static string BuildStatusResponse(InstructionGPT.WelcomeState welcome, SocketGuild guild)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Enabled: {welcome.Enabled}");
        sb.AppendLine($"Require onboarding: {welcome.RequireOnboarding}");
        sb.AppendLine($"Welcome channel: {(welcome.WelcomeChannelId.HasValue ? $"<#{welcome.WelcomeChannelId.Value}>" : "not set")}");
        sb.AppendLine($"Welcome role: {(welcome.WelcomeRoleId.HasValue ? $"<@&{welcome.WelcomeRoleId.Value}>" : "not set")}");
        sb.AppendLine($"Rules: {welcome.Rules.Count}");
        sb.AppendLine($"Validations: {welcome.Validations.Count}");
        sb.AppendLine($"Users completed: {welcome.Users.Values.Count(u => u.Completed)}");
        return sb.ToString().Trim();
    }

    private static string SetEnabled(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        if (subOption.Options?.FirstOrDefault()?.Value is bool enabled)
        {
            welcome.Enabled = enabled;
            return $"Welcome onboarding {(enabled ? "enabled" : "disabled")}.";
        }

        return "Provide true or false.";
    }

    private static string SetRequire(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        if (subOption.Options?.FirstOrDefault()?.Value is bool enabled)
        {
            welcome.RequireOnboarding = enabled;
            return $"Require onboarding {(enabled ? "enabled" : "disabled")}.";
        }

        return "Provide true or false.";
    }

    private static string SetWelcomeChannel(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        if (subOption.Options?.FirstOrDefault()?.Value is SocketGuildChannel channel)
        {
            welcome.WelcomeChannelId = channel.Id;
            return $"Welcome channel set to <#{channel.Id}>.";
        }

        return "Provide a channel.";
    }

    private static string SetWelcomeRole(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        if (subOption.Options?.FirstOrDefault()?.Value is SocketRole role)
        {
            welcome.WelcomeRoleId = role.Id;
            return $"Welcome role set to {role.Name}.";
        }

        return "Provide a role.";
    }

    private static string HandleRuleCommand(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        var action = subOption.Options?.FirstOrDefault();
        if (action == null)
        {
            return "Specify a rule command.";
        }

        switch (action.Name)
        {
            case "add":
                return AddRule(welcome, action);
            case "update":
                return UpdateRule(welcome, action);
            case "delete":
                return DeleteRule(welcome, action);
            case "list":
                return ListRules(welcome);
            case "move":
                return MoveRule(welcome, action);
            default:
                return "Unknown rule command.";
        }
    }

    private static string AddRule(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var text = action.Options?.FirstOrDefault(opt => opt.Name == "text")?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Provide rule text.";
        }

        var rule = new InstructionGPT.WelcomeRule { Id = Guid.NewGuid().ToString("n"), Text = text.Trim() };
        welcome.Rules.Add(rule);
        return $"Rule added at #{welcome.Rules.Count}.";
    }

    private static string UpdateRule(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var index = GetIndexOption(action, "index");
        var text = action.Options?.FirstOrDefault(opt => opt.Name == "text")?.Value?.ToString();
        if (!index.HasValue || string.IsNullOrWhiteSpace(text))
        {
            return "Provide index and text.";
        }

        var ruleIndex = index.Value - 1;
        if (ruleIndex < 0 || ruleIndex >= welcome.Rules.Count)
        {
            return "Rule index out of range.";
        }

        welcome.Rules[ruleIndex].Text = text.Trim();
        return $"Rule #{index.Value} updated.";
    }

    private static string DeleteRule(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var index = GetIndexOption(action, "index");
        if (!index.HasValue)
        {
            return "Provide rule index.";
        }

        var ruleIndex = index.Value - 1;
        if (ruleIndex < 0 || ruleIndex >= welcome.Rules.Count)
        {
            return "Rule index out of range.";
        }

        welcome.Rules.RemoveAt(ruleIndex);
        return $"Rule #{index.Value} deleted.";
    }

    private static string MoveRule(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var from = GetIndexOption(action, "from");
        var to = GetIndexOption(action, "to");
        if (!from.HasValue || !to.HasValue)
        {
            return "Provide from and to indexes.";
        }

        var fromIndex = from.Value - 1;
        var toIndex = to.Value - 1;
        if (fromIndex < 0 || fromIndex >= welcome.Rules.Count || toIndex < 0 || toIndex >= welcome.Rules.Count)
        {
            return "Rule index out of range.";
        }

        var item = welcome.Rules[fromIndex];
        welcome.Rules.RemoveAt(fromIndex);
        welcome.Rules.Insert(toIndex, item);
        return $"Rule moved from #{from.Value} to #{to.Value}.";
    }

    private static string ListRules(InstructionGPT.WelcomeState welcome)
    {
        if (welcome.Rules.Count == 0)
        {
            return "No rules configured.";
        }

        var lines = welcome.Rules
            .Select((rule, index) => $"{index + 1}. {rule.Text}")
            .ToList();
        return string.Join("\n", lines);
    }

    private static string HandleValidationCommand(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption subOption)
    {
        var action = subOption.Options?.FirstOrDefault();
        if (action == null)
        {
            return "Specify a validation command.";
        }

        switch (action.Name)
        {
            case "add":
                return AddValidation(welcome, action);
            case "delete":
                return DeleteValidation(welcome, action);
            case "list":
                return ListValidations(welcome);
            case "move":
                return MoveValidation(welcome, action);
            default:
                return "Unknown validation command.";
        }
    }

    private static string AddValidation(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var type = action.Options?.FirstOrDefault(opt => opt.Name == "type")?.Value?.ToString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type) || !ValidationTypes.Contains(type))
        {
            return "Validation type must be acknowledge, reaction, or phrase.";
        }

        var value = action.Options?.FirstOrDefault(opt => opt.Name == "value")?.Value?.ToString();
        if ((type == "reaction" || type == "phrase") && string.IsNullOrWhiteSpace(value))
        {
            return "Validation value required for reaction or phrase.";
        }

        var validation = new InstructionGPT.WelcomeValidationRule
        {
            Id = Guid.NewGuid().ToString("n"),
            Type = type,
            Value = value?.Trim()
        };
        welcome.Validations.Add(validation);
        return $"Validation added at #{welcome.Validations.Count}.";
    }

    private static string DeleteValidation(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var index = GetIndexOption(action, "index");
        if (!index.HasValue)
        {
            return "Provide validation index.";
        }

        var ruleIndex = index.Value - 1;
        if (ruleIndex < 0 || ruleIndex >= welcome.Validations.Count)
        {
            return "Validation index out of range.";
        }

        welcome.Validations.RemoveAt(ruleIndex);
        return $"Validation #{index.Value} deleted.";
    }

    private static string MoveValidation(InstructionGPT.WelcomeState welcome, SocketSlashCommandDataOption action)
    {
        var from = GetIndexOption(action, "from");
        var to = GetIndexOption(action, "to");
        if (!from.HasValue || !to.HasValue)
        {
            return "Provide from and to indexes.";
        }

        var fromIndex = from.Value - 1;
        var toIndex = to.Value - 1;
        if (fromIndex < 0 || fromIndex >= welcome.Validations.Count || toIndex < 0 || toIndex >= welcome.Validations.Count)
        {
            return "Validation index out of range.";
        }

        var item = welcome.Validations[fromIndex];
        welcome.Validations.RemoveAt(fromIndex);
        welcome.Validations.Insert(toIndex, item);
        return $"Validation moved from #{from.Value} to #{to.Value}.";
    }

    private static string ListValidations(InstructionGPT.WelcomeState welcome)
    {
        if (welcome.Validations.Count == 0)
        {
            return "No validations configured.";
        }

        var lines = welcome.Validations.Select((rule, index) =>
        {
            var suffix = string.IsNullOrWhiteSpace(rule.Value) ? string.Empty : $" ({rule.Value})";
            return $"{index + 1}. {rule.Type}{suffix}";
        });
        return string.Join("\n", lines);
    }

    private static async Task<string> PostRulesAsync(DiscordModuleContext context, InstructionGPT.ChannelState configState, SocketGuild guild)
    {
        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        if (!welcome.WelcomeChannelId.HasValue)
        {
            return "Set the welcome channel first.";
        }

        var channel = context.Client.GetChannel(welcome.WelcomeChannelId.Value) as IMessageChannel;
        if (channel == null)
        {
            return "Welcome channel not found.";
        }

        var rulesText = BuildRulesText(welcome);
        var message = await channel.SendMessageAsync(rulesText);
        welcome.RulesMessageId = message.Id;

        foreach (var validation in welcome.Validations.Where(v => v.Type == "reaction"))
        {
            var emote = ParseEmote(validation.Value);
            if (emote != null)
            {
                await message.AddReactionAsync(emote);
            }
        }

        return $"Rules posted in <#{welcome.WelcomeChannelId.Value}>.";
    }

    private static string BuildRulesText(InstructionGPT.WelcomeState welcome)
    {
        if (welcome.Rules.Count == 0)
        {
            return "Welcome! No rules are configured yet.";
        }

        var rules = welcome.Rules.Take(MaxRulesPerMessage)
            .Select((rule, index) => $"{index + 1}. {rule.Text}")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Welcome! Please review the rules:");
        sb.AppendLine(string.Join("\n", rules));
        if (welcome.Rules.Count > MaxRulesPerMessage)
        {
            sb.AppendLine($"(Showing first {MaxRulesPerMessage} rules.)");
        }

        if (welcome.Validations.Any(v => v.Type == "acknowledge"))
        {
            sb.AppendLine("Use /gptcli welcome acknowledge after reading.");
        }
        return sb.ToString().Trim();
    }

    private static async Task<string> AcknowledgeAsync(DiscordModuleContext context, InstructionGPT.ChannelState configState, SocketGuild guild, IUser user)
    {
        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        var userState = GetOrCreateUserState(welcome, user.Id);
        var updated = ApplyAcknowledgeValidation(welcome, userState);
        updated |= await TryCompleteOnboardingAsync(context, configState, guild, guild.GetUser(user.Id), userState);

        return updated ? "Acknowledged. Checking your onboarding status now." : "No acknowledge validation configured.";
    }

    private static bool ApplyAcknowledgeValidation(InstructionGPT.WelcomeState welcome, InstructionGPT.WelcomeUserState userState)
    {
        var updated = false;
        foreach (var validation in welcome.Validations.Where(v => v.Type == "acknowledge"))
        {
            if (userState.CompletedValidations.Add(validation.Id))
            {
                updated = true;
            }
        }

        return updated;
    }

    private static bool ApplyReactionValidation(InstructionGPT.WelcomeState welcome, InstructionGPT.WelcomeUserState userState, string emojiName)
    {
        if (string.IsNullOrWhiteSpace(emojiName))
        {
            return false;
        }

        var updated = false;
        foreach (var validation in welcome.Validations.Where(v => v.Type == "reaction"))
        {
            if (string.Equals(validation.Value, emojiName, StringComparison.OrdinalIgnoreCase))
            {
                if (userState.CompletedValidations.Add(validation.Id))
                {
                    updated = true;
                }
            }
        }

        return updated;
    }

    private static bool ApplyPhraseValidation(InstructionGPT.WelcomeState welcome, InstructionGPT.WelcomeUserState userState, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var updated = false;
        foreach (var validation in welcome.Validations.Where(v => v.Type == "phrase"))
        {
            if (!string.IsNullOrWhiteSpace(validation.Value)
                && content.IndexOf(validation.Value, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (userState.CompletedValidations.Add(validation.Id))
                {
                    updated = true;
                }
            }
        }

        return updated;
    }

    private static InstructionGPT.WelcomeUserState GetOrCreateUserState(InstructionGPT.WelcomeState welcome, ulong userId)
    {
        welcome.Users ??= new Dictionary<ulong, InstructionGPT.WelcomeUserState>();
        if (!welcome.Users.TryGetValue(userId, out var userState))
        {
            userState = new InstructionGPT.WelcomeUserState();
            welcome.Users[userId] = userState;
        }

        return userState;
    }

    private static async Task<bool> TryCompleteOnboardingAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState configState,
        SocketGuild guild,
        SocketGuildUser user,
        InstructionGPT.WelcomeUserState userState)
    {
        if (user == null)
        {
            return false;
        }

        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        if (welcome.Validations.Count == 0)
        {
            return false;
        }

        if (welcome.Validations.All(rule => userState.CompletedValidations.Contains(rule.Id)))
        {
            if (!userState.Completed)
            {
                userState.Completed = true;
                await GrantWelcomeRoleAsync(context, welcome, user);
                return true;
            }
        }

        return false;
    }

    private static async Task GrantWelcomeRoleAsync(DiscordModuleContext context, InstructionGPT.WelcomeState welcome, SocketGuildUser user)
    {
        if (!welcome.WelcomeRoleId.HasValue)
        {
            return;
        }

        var role = user.Guild.GetRole(welcome.WelcomeRoleId.Value);
        if (role == null)
        {
            return;
        }

        try
        {
            await user.AddRoleAsync(role);
        }
        catch (HttpException ex)
        {
            await Console.Out.WriteLineAsync($"Failed to add welcome role: {ex.Reason}");
        }
    }

    private static IEmote ParseEmote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Emote.TryParse(trimmed, out var emote))
        {
            return emote;
        }

        try
        {
            return new Emoji(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldNudge(InstructionGPT.WelcomeUserState userState)
    {
        if (!userState.LastNudgeUtc.HasValue)
        {
            return true;
        }

        return DateTime.UtcNow - userState.LastNudgeUtc.Value >= NudgeCooldown;
    }

    private static async Task SendNudgeAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState configState,
        SocketGuild guild,
        IMessageChannel channel,
        SocketGuildUser user)
    {
        var welcome = configState.Welcome ??= new InstructionGPT.WelcomeState();
        var welcomeChannelText = welcome.WelcomeChannelId.HasValue ? $" Please visit <#{welcome.WelcomeChannelId.Value}>." : string.Empty;
        var message = $"<@{user.Id}> Please complete onboarding.{welcomeChannelText} Use /gptcli welcome acknowledge when done.";
        await channel.SendMessageAsync(message);
    }

    private static bool RequireAdmin(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser guildUser)
        {
            return false;
        }

        return guildUser.GuildPermissions.Administrator || guildUser.GuildPermissions.ManageGuild;
    }

    private static int? GetIndexOption(SocketSlashCommandDataOption action, string name)
    {
        var option = action.Options?.FirstOrDefault(opt => opt.Name == name);
        if (option?.Value is long value)
        {
            return (int)value;
        }

        if (option?.Value is int intValue)
        {
            return intValue;
        }

        return null;
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
}
