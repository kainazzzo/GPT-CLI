using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Chat.Discord.Modules;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;

namespace DndModuleExample;

public sealed class DndGameMasterModule : FeatureModuleBase
{
    public override string Id => "dnd";
    public override string Name => "D&D Live GM";

    private const string ModeOff = "off";
    private const string ModePrep = "prep";
    private const string ModeLive = "live";
    private const int MaxCampaignChars = 24000;
    private const int MaxAttachmentBytes = 250000;
    private const int MaxTranscriptLines = 24;
    private const int PendingOverwriteMinutes = 15;
    private const int DiscordMessageLimit = 1800;

    private static readonly Regex DiceExpressionRegex =
        new(@"^(?<count>\d{0,2})d(?<sides>\d{1,4})(?<modifier>[+-]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BotMentionRegexTemplate =
        new(@"<@!?(?<id>\d+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = new();

    private readonly ConcurrentDictionary<ulong, DndChannelState> _stateByChannel = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public override IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions(DiscordModuleContext context)
    {
        return new[]
        {
            SlashCommandContribution.TopLevel(BuildDndCommands())
        };
    }

    public override IReadOnlyList<GptCliFunction> GetGptCliFunctions(DiscordModuleContext context)
    {
        const string dndGroupDescription = "D&D GM and campaign controls";

        return new List<GptCliFunction>
        {
            // Module enable/disable toggle (must remain callable even when disabled).
            new()
            {
                ToolName = "gptcli_set_dnd",
                ModuleId = Id,
                ExposeWhenModuleDisabled = true,
                Description = "Enable or disable the D&D module in this channel",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SetOption, "set", "Settings", SetOptionName: "dnd"),
                Parameters = new[]
                {
                    new GptCliParamSpec("value", GptCliParamType.Boolean, "true or false", Required: true)
                },
                ExecuteAsync = ExecuteSetEnabledAsync
            },

            new()
            {
                ToolName = "gptcli_dnd_status",
                ModuleId = Id,
                Description = "Show D&D mode and campaign status",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "status"),
                ExecuteAsync = ExecuteStatusAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_mode",
                ModuleId = Id,
                Description = "Set D&D mode",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "mode"),
                Parameters = new[]
                {
                    new GptCliParamSpec("value", GptCliParamType.String, "off, prep, or live", Required: true,
                        Choices: new[]
                        {
                            new GptCliParamChoice("off", ModeOff),
                            new GptCliParamChoice("prep", ModePrep),
                            new GptCliParamChoice("live", ModeLive)
                        }),
                    new GptCliParamSpec("campaign", GptCliParamType.String, "Campaign name (optional)")
                },
                ExecuteAsync = ExecuteModeAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaigncreate",
                ModuleId = Id,
                Description = "Create a campaign from a prompt",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaigncreate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name", Required: true),
                    new GptCliParamSpec("prompt", GptCliParamType.String, "Campaign creation prompt", Required: true)
                },
                ExecuteAsync = ExecuteCampaignCreateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignrefine",
                ModuleId = Id,
                Description = "Refine an existing campaign",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignrefine"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name", Required: true),
                    new GptCliParamSpec("prompt", GptCliParamType.String, "Refinement prompt", Required: true)
                },
                ExecuteAsync = ExecuteCampaignRefineAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignoverwrite",
                ModuleId = Id,
                Description = "Overwrite campaign content from text or file URL",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignoverwrite"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name", Required: true),
                    new GptCliParamSpec("text", GptCliParamType.String, "Campaign content text"),
                    new GptCliParamSpec("file", GptCliParamType.Attachment, "Upload .txt, .md, or .json")
                },
                ExecuteAsync = ExecuteCampaignOverwriteAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_charactercreate",
                ModuleId = Id,
                Description = "Create your character sheet",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "charactercreate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Character name", Required: true),
                    new GptCliParamSpec("concept", GptCliParamType.String, "Character concept", Required: true)
                },
                ExecuteAsync = ExecuteCharacterCreateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_charactershow",
                ModuleId = Id,
                Description = "Show a character sheet",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "charactershow"),
                Parameters = new[]
                {
                    new GptCliParamSpec("user", GptCliParamType.User, "Optional user id")
                },
                ExecuteAsync = ExecuteCharacterShowAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_npccreate",
                ModuleId = Id,
                Description = "Create an NPC party member",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npccreate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "NPC name", Required: true),
                    new GptCliParamSpec("concept", GptCliParamType.String, "NPC concept", Required: true)
                },
                ExecuteAsync = ExecuteNpcCreateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_npclist",
                ModuleId = Id,
                Description = "List NPC party members",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npclist"),
                ExecuteAsync = ExecuteNpcListAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_npcshow",
                ModuleId = Id,
                Description = "Show an NPC profile",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npcshow"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "NPC name", Required: true)
                },
                ExecuteAsync = ExecuteNpcShowAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_npcremove",
                ModuleId = Id,
                Description = "Remove an NPC party member",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npcremove"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "NPC name", Required: true)
                },
                ExecuteAsync = ExecuteNpcRemoveAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_ledger",
                ModuleId = Id,
                Description = "Show official campaign ledger entries",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "ledger"),
                Parameters = new[]
                {
                    new GptCliParamSpec("count", GptCliParamType.Integer, "Number of entries (1-100)", MinInt: 1, MaxInt: 100)
                },
                ExecuteAsync = ExecuteLedgerAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignhistory",
                ModuleId = Id,
                Description = "Show campaign revision and tweak history",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignhistory"),
                Parameters = new[]
                {
                    new GptCliParamSpec("count", GptCliParamType.Integer, "Number of revisions (1-50)", MinInt: 1, MaxInt: 50)
                },
                ExecuteAsync = ExecuteCampaignHistoryAsync
            }
        };
    }

    public override Task<bool> OnInteractionAsync(DiscordModuleContext context, SocketInteraction interaction, CancellationToken cancellationToken)
        => Task.FromResult(false);

    private static SlashCommandOptionBuilder BuildDndCommands()
    {
        return new SlashCommandOptionBuilder
        {
            Name = "dnd",
            Description = "D&D GM and campaign controls",
            Type = ApplicationCommandOptionType.SubCommandGroup,
            Options = new()
            {
                new SlashCommandOptionBuilder().WithName("status")
                    .WithDescription("Show D&D mode and campaign status")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("mode")
                    .WithDescription("Set D&D mode")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("value")
                        .WithDescription("off, prep, or live")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("off", ModeOff)
                        .AddChoice("prep", ModePrep)
                        .AddChoice("live", ModeLive)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("campaign")
                        .WithDescription("Campaign name (optional)")
                        .WithType(ApplicationCommandOptionType.String)),
                new SlashCommandOptionBuilder().WithName("campaigncreate")
                    .WithDescription("Create a campaign from a prompt")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("Campaign name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("prompt")
                        .WithDescription("Campaign creation prompt")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("campaignrefine")
                    .WithDescription("Refine an existing campaign")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("Campaign name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("prompt")
                        .WithDescription("Refinement prompt")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("campaignoverwrite")
                    .WithDescription("Overwrite campaign content from text or file")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("Campaign name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("text")
                        .WithDescription("Campaign content text")
                        .WithType(ApplicationCommandOptionType.String))
                    .AddOption(new SlashCommandOptionBuilder().WithName("file")
                        .WithDescription("Upload .txt, .md, or .json")
                        .WithType(ApplicationCommandOptionType.Attachment)),
                new SlashCommandOptionBuilder().WithName("charactercreate")
                    .WithDescription("Create your character sheet")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("Character name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("concept")
                        .WithDescription("Character concept")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("charactershow")
                    .WithDescription("Show your character sheet")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("user")
                        .WithDescription("Optional user")
                        .WithType(ApplicationCommandOptionType.User)),
                new SlashCommandOptionBuilder().WithName("npccreate")
                    .WithDescription("Create an NPC party member")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("NPC name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("concept")
                        .WithDescription("NPC concept")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("npclist")
                    .WithDescription("List NPC party members")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("npcshow")
                    .WithDescription("Show an NPC profile")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("NPC name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("npcremove")
                    .WithDescription("Remove an NPC party member")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("NPC name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("ledger")
                    .WithDescription("Show official campaign ledger entries")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("count")
                        .WithDescription("Number of entries (1-100)")
                        .WithType(ApplicationCommandOptionType.Integer)),
                new SlashCommandOptionBuilder().WithName("campaignhistory")
                    .WithDescription("Show campaign revision and tweak history")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("count")
                        .WithDescription("Number of revisions (1-50)")
                        .WithType(ApplicationCommandOptionType.Integer))
            }
        };
    }

    private Task<GptCliExecutionResult> ExecuteSetEnabledAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetBoolArg(argsJson, "value", out var enabled))
        {
            return Task.FromResult(new GptCliExecutionResult(true, "Provide `value` as true or false.", false));
        }

        InstructionGPT.SetModuleEnabled(ctx.ChannelState, Id, enabled);
        return Task.FromResult(new GptCliExecutionResult(true, $"dnd enabled = {(enabled ? "true" : "false")}", true));
    }

    private async Task<GptCliExecutionResult> ExecuteStatusAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            return new GptCliExecutionResult(true, BuildStatusText(dndState), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteModeAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "value", out var modeRaw) || string.IsNullOrWhiteSpace(modeRaw))
        {
            return new GptCliExecutionResult(true, "Provide `value` as off, prep, or live.", false);
        }

        var mode = modeRaw.Trim().ToLowerInvariant();
        if (mode is not (ModeOff or ModePrep or ModeLive))
        {
            return new GptCliExecutionResult(true, "Mode must be one of: off, prep, live.", false);
        }

        TryGetStringArg(argsJson, "campaign", out var campaignName);

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var dndStateChanged = false;
            var channelStateChanged = false;

            if (mode == ModeOff)
            {
                dndState.Mode = ModeOff;
                dndState.PendingOverwrite = null;
                if (dndState.ModuleMutedBot)
                {
                    ctx.ChannelState.Options.Muted = dndState.PreviousBotMuted;
                    dndState.ModuleMutedBot = false;
                    channelStateChanged = true;
                }

                dndStateChanged = true;
                if (dndStateChanged)
                {
                    await SaveStateAsync(ctx.ChannelState, dndState, ct);
                }

                return new GptCliExecutionResult(true, "DND mode set to off.", channelStateChanged);
            }

            var targetCampaign = GetOrCreateCampaign(dndState,
                string.IsNullOrWhiteSpace(campaignName) ? dndState.ActiveCampaignName : campaignName);
            dndState.ActiveCampaignName = targetCampaign.Name;
            dndState.Mode = mode;
            dndStateChanged = true;

            if (mode == ModeLive && !dndState.ModuleMutedBot)
            {
                dndState.PreviousBotMuted = ctx.ChannelState.Options.Muted;
                dndState.ModuleMutedBot = true;
                ctx.ChannelState.Options.Muted = true;
                channelStateChanged = true;
            }

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"DND mode set to `{mode}` for campaign \"{targetCampaign.Name}\".", channelStateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        if (!TryGetStringArg(argsJson, "name", out var campaignName) || string.IsNullOrWhiteSpace(campaignName) ||
            !TryGetStringArg(argsJson, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `prompt`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var generated = await GenerateCampaignAsync(ctx.Context, ctx.ChannelState, campaignName.Trim(), prompt.Trim(), null, ct);
            if (string.IsNullOrWhiteSpace(generated))
            {
                return new GptCliExecutionResult(true, "Campaign generation failed.", false);
            }

            var campaign = GetOrCreateCampaign(dndState, campaignName);
            campaign.Content = TrimToLimit(generated, MaxCampaignChars);
            campaign.UpdatedUtc = DateTime.UtcNow;
            dndState.ActiveCampaignName = campaign.Name;

            await RecordCampaignRevisionAsync(
                ctx.ChannelState,
                campaign,
                triggerKind: "create",
                triggerInput: prompt.Trim(),
                ctx.User.Id,
                ctx.User.Username,
                isPromptTweak: false,
                ct);

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"Campaign \"{campaign.Name}\" created.\n\n{TrimToLimit(campaign.Content, 2500)}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignRefineAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        if (!TryGetStringArg(argsJson, "name", out var campaignName) || string.IsNullOrWhiteSpace(campaignName) ||
            !TryGetStringArg(argsJson, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `prompt`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, campaignName);
            var refined = await GenerateCampaignAsync(ctx.Context, ctx.ChannelState, campaign.Name, prompt.Trim(), campaign.Content, ct);
            if (string.IsNullOrWhiteSpace(refined))
            {
                return new GptCliExecutionResult(true, "Campaign refinement failed.", false);
            }

            campaign.Content = TrimToLimit(refined, MaxCampaignChars);
            campaign.UpdatedUtc = DateTime.UtcNow;
            dndState.ActiveCampaignName = campaign.Name;

            await RecordCampaignRevisionAsync(
                ctx.ChannelState,
                campaign,
                triggerKind: "refine",
                triggerInput: prompt.Trim(),
                ctx.User.Id,
                ctx.User.Username,
                isPromptTweak: true,
                ct);

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"Campaign \"{campaign.Name}\" refined.\n\n{TrimToLimit(campaign.Content, 2500)}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignOverwriteAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "name", out var campaignName) || string.IsNullOrWhiteSpace(campaignName))
        {
            return new GptCliExecutionResult(true, "Provide `name`.", false);
        }

        TryGetStringArg(argsJson, "text", out var inlineText);
        TryGetStringArg(argsJson, "file", out var fileUrl);

        var payload = !string.IsNullOrWhiteSpace(inlineText)
            ? inlineText
            : await ReadAttachmentPayloadAsync(fileUrl, ct);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return new GptCliExecutionResult(true, "Provide campaign `text` or upload `.txt`, `.md`, or `.json` via `file`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, campaignName);
            campaign.Content = TrimToLimit(payload, MaxCampaignChars);
            campaign.UpdatedUtc = DateTime.UtcNow;
            dndState.ActiveCampaignName = campaign.Name;

            await RecordCampaignRevisionAsync(
                ctx.ChannelState,
                campaign,
                triggerKind: "overwrite",
                triggerInput: string.IsNullOrWhiteSpace(inlineText) ? $"Attachment URL: {fileUrl}" : "Inline overwrite text",
                ctx.User.Id,
                ctx.User.Username,
                isPromptTweak: true,
                ct);

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"Campaign \"{campaign.Name}\" overwritten.", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCharacterCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        if (!TryGetStringArg(argsJson, "name", out var characterName) || string.IsNullOrWhiteSpace(characterName) ||
            !TryGetStringArg(argsJson, "concept", out var concept) || string.IsNullOrWhiteSpace(concept))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `concept`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            dndState.ActiveCampaignName = campaign.Name;

            var sheet = await GenerateCharacterAsync(ctx.Context, ctx.ChannelState, campaign, characterName.Trim(), concept.Trim(), ct);
            if (string.IsNullOrWhiteSpace(sheet))
            {
                return new GptCliExecutionResult(true, "Character generation failed.", false);
            }

            var character = new DndCharacter
            {
                Name = characterName.Trim(),
                Concept = concept.Trim(),
                Sheet = TrimToLimit(sheet, 8000),
                UpdatedUtc = DateTime.UtcNow
            };

            campaign.Characters[ctx.User.Id] = character;
            campaign.UpdatedUtc = DateTime.UtcNow;
            await SaveCharacterSheetJsonAsync(ctx.ChannelState, campaign.Name, ctx.User.Id, character, ct);

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"Character saved for <@{ctx.User.Id}> in \"{campaign.Name}\".\n\n{TrimToLimit(character.Sheet, 2500)}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCharacterShowAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        ulong targetUserId = ctx.User.Id;
        if (TryGetUlongArg(argsJson, "user", out var parsedUserId) && parsedUserId != 0)
        {
            targetUserId = parsedUserId;
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Characters.TryGetValue(targetUserId, out var character) || string.IsNullOrWhiteSpace(character.Sheet))
            {
                return new GptCliExecutionResult(true, $"No character found for <@{targetUserId}> in \"{campaign.Name}\".", false);
            }

            return new GptCliExecutionResult(true, $"Character for <@{targetUserId}> ({campaign.Name})\n\n{TrimToLimit(character.Sheet, 2500)}", false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        if (!TryGetStringArg(argsJson, "name", out var npcName) || string.IsNullOrWhiteSpace(npcName) ||
            !TryGetStringArg(argsJson, "concept", out var concept) || string.IsNullOrWhiteSpace(concept))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `concept`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            dndState.ActiveCampaignName = campaign.Name;

            var profile = await GenerateNpcAsync(ctx.Context, ctx.ChannelState, campaign, npcName.Trim(), concept.Trim(), ct);
            if (string.IsNullOrWhiteSpace(profile))
            {
                return new GptCliExecutionResult(true, "NPC generation failed.", false);
            }

            var npc = new DndNpcMember
            {
                Name = npcName.Trim(),
                Concept = concept.Trim(),
                Profile = TrimToLimit(profile, 5000),
                UpdatedUtc = DateTime.UtcNow
            };
            campaign.Npcs[npc.Name] = npc;
            campaign.UpdatedUtc = DateTime.UtcNow;
            await SaveNpcSheetJsonAsync(ctx.ChannelState, campaign.Name, npc, ct);

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"NPC \"{npc.Name}\" added to \"{campaign.Name}\".\n\n{TrimToLimit(npc.Profile, 1800)}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcListAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (campaign.Npcs.Count == 0)
            {
                return new GptCliExecutionResult(true, $"No NPC companions in \"{campaign.Name}\".", false);
            }

            var lines = campaign.Npcs
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"- {entry.Value.Name}: {GetNpcSummary(entry.Value)}");
            return new GptCliExecutionResult(true, $"NPC companions in \"{campaign.Name}\":\n{string.Join("\n", lines)}", false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcShowAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "name", out var npcName) || string.IsNullOrWhiteSpace(npcName))
        {
            return new GptCliExecutionResult(true, "Provide `name`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Npcs.TryGetValue(npcName.Trim(), out var npc))
            {
                return new GptCliExecutionResult(true, $"NPC \"{npcName}\" not found in \"{campaign.Name}\".", false);
            }

            return new GptCliExecutionResult(true, $"NPC \"{npc.Name}\" ({campaign.Name})\nConcept: {npc.Concept}\n\n{TrimToLimit(npc.Profile, 2500)}", false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcRemoveAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "name", out var npcName) || string.IsNullOrWhiteSpace(npcName))
        {
            return new GptCliExecutionResult(true, "Provide `name`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Npcs.Remove(npcName.Trim()))
            {
                return new GptCliExecutionResult(true, $"NPC \"{npcName}\" not found in \"{campaign.Name}\".", false);
            }

            await DeleteNpcSheetJsonAsync(ctx.ChannelState, campaign.Name, npcName.Trim(), ct);
            campaign.UpdatedUtc = DateTime.UtcNow;
            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            return new GptCliExecutionResult(true, $"NPC \"{npcName}\" removed from \"{campaign.Name}\".", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteLedgerAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var count = 20;
        if (TryGetIntArg(argsJson, "count", out var parsedCount))
        {
            count = Clamp(parsedCount, 1, 100);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var doc = await LoadCampaignDocumentAsync(ctx.ChannelState, campaign.Name, ct);
            var ledger = await LoadCampaignLedgerAsync(ctx.ChannelState, campaign.Name, ct);
            if (ledger == null || ledger.Entries.Count == 0)
            {
                return new GptCliExecutionResult(true, $"No ledger entries found for campaign \"{campaign.Name}\".", false);
            }

            var items = ledger.Entries
                .OrderByDescending(e => e.OccurredUtc)
                .Take(count)
                .ToList();
            var lines = new List<string>
            {
                $"Ledger for \"{campaign.Name}\"",
                $"- campaign-doc-id: {ledger.CampaignDocumentId ?? doc?.DocumentId ?? "unknown"}",
                $"- entries shown: {items.Count}/{ledger.Entries.Count}"
            };

            foreach (var entry in items)
            {
                lines.Add(
                    $"[{entry.EntryId}] {entry.OccurredUtc:O} | {entry.ActionType} | {entry.ActorUsername}({entry.ActorUserId}) | rev:{entry.CampaignRevisionId}\n" +
                    $"action: {TrimToLimit(entry.ActionText, 180)}\n" +
                    $"outcome: {TrimToLimit(entry.Outcome, 220)}");
            }

            return new GptCliExecutionResult(true, string.Join("\n", lines), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignHistoryAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var count = 10;
        if (TryGetIntArg(argsJson, "count", out var parsedCount))
        {
            count = Clamp(parsedCount, 1, 50);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var document = await LoadCampaignDocumentAsync(ctx.ChannelState, campaign.Name, ct);
            if (document == null)
            {
                return new GptCliExecutionResult(true, $"No campaign document found for \"{campaign.Name}\".", false);
            }

            var revisions = document.Revisions
                .OrderByDescending(r => r.AppliedUtc)
                .Take(count)
                .ToList();
            var lines = new List<string>
            {
                $"Campaign history for \"{campaign.Name}\"",
                $"- document-id: {document.DocumentId}",
                $"- current-revision: {document.CurrentRevisionId}",
                $"- revisions shown: {revisions.Count}/{document.Revisions.Count}",
                $"- prompt tweaks: {document.PromptTweaks.Count}"
            };

            foreach (var rev in revisions)
            {
                var previous = document.Revisions.FirstOrDefault(r => r.RevisionId == rev.PreviousRevisionId);
                var delta = BuildRevisionDeltaSummary(previous?.CampaignContent, rev.CampaignContent);
                lines.Add(
                    $"[{rev.RevisionId}] {rev.AppliedUtc:O} | {rev.TriggerKind} by {rev.TriggeredByUsername}({rev.TriggeredByUserId}) | prev:{rev.PreviousRevisionId ?? "none"}\n" +
                    $"trigger: {TrimToLimit(rev.TriggerInput, 180)}\n" +
                    $"delta: {delta}");
            }

            if (document.PromptTweaks.Count > 0)
            {
                lines.Add("Recent prompt tweaks:");
                foreach (var tweak in document.PromptTweaks
                             .OrderByDescending(t => t.TriggeredUtc)
                             .Take(Math.Min(5, document.PromptTweaks.Count)))
                {
                    lines.Add(
                        $"- [{tweak.TweakId}] {tweak.TriggeredUtc:O} | {tweak.Kind} by {tweak.TriggeredByUsername}({tweak.TriggeredByUserId}) -> rev:{tweak.ResultingRevisionId}\n" +
                        $"  {TrimToLimit(tweak.PromptOrInteraction, 180)}");
                }
            }

            return new GptCliExecutionResult(true, string.Join("\n", lines), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private static bool TryGetStringArg(string argsJson, string name, out string value)
    {
        value = null;
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return false;
        }

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.ToString()
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBoolArg(string argsJson, string name, out bool value)
    {
        value = default;
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetIntArg(string argsJson, string name, out int value)
    {
        value = default;
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetUlongArg(string argsJson, string name, out ulong value)
    {
        value = default;
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var s = element.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }

            s = new string(s.Where(char.IsDigit).ToArray());
            return ulong.TryParse(s, out value);
        }

        return false;
    }

    private static bool IsSupportedCampaignFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".json";
    }

    private async Task<string> ReadAttachmentPayloadAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (!IsSupportedCampaignFileName(fileName))
        {
            return null;
        }

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > MaxAttachmentBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > MaxAttachmentBytes)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public override async Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken)
    {
        if (message == null || message.Author.IsBot || message.Author.Id == context.Client.CurrentUser.Id)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Content) && (message.Attachments == null || message.Attachments.Count == 0))
        {
            return;
        }

        var channelState = context.Host.GetOrCreateChannelState(message.Channel);
        if (message.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channelState, guildChannel);
        }

        if (!context.Host.IsChannelGuildMatch(channelState, message.Channel, "dnd-message"))
        {
            return;
        }

        if (!InstructionGPT.IsModuleEnabled(channelState, Id))
        {
            return;
        }

        var lockHandle = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(cancellationToken);
        try
        {
	            var dndState = await GetOrLoadStateAsync(channelState, cancellationToken);
	            var content = (message.Content ?? string.Empty).Trim();

	            // Setup/GM controls are handled via `/gptcli` (and mention-triggered LLM tool routing).
	            // In-channel D&D module responses should only happen in live gameplay mode.
	            if (content.StartsWith("!dnd", StringComparison.OrdinalIgnoreCase) ||
	                content.StartsWith("!confirm", StringComparison.OrdinalIgnoreCase))
	            {
	                await message.Channel.SendMessageAsync(
	                    "Use `/gptcli dnd ...` for D&D GM setup commands. " +
	                    "Live gameplay chat remains natural in this channel when mode is `live`.");
	                return;
	            }

            if (!string.Equals(dndState.Mode, ModeLive, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (content.StartsWith("!", StringComparison.Ordinal))
            {
                var actionResult = await TryHandleActionCommandAsync(channelState, message, dndState, content, cancellationToken);
                if (actionResult.Handled)
                {
                    await PersistIfNeededAsync(context, channelState, dndState, actionResult, cancellationToken);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            AppendTranscript(dndState, $"{message.Author.Username}: {message.Content}");
            var npcReply = await TryGenerateNpcConversationReplyAsync(context, channelState, dndState, message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(npcReply))
            {
                await SendChunkedAsync(message.Channel, npcReply);
                AppendTranscript(dndState, npcReply);
                await TryAppendLedgerEntryAsync(
                    channelState,
                    dndState,
                    message.Author.Id,
                    message.Author.Username,
                    "npc-dialogue",
                    content,
                    npcReply,
                    cancellationToken);
                await PersistIfNeededAsync(context, channelState, dndState, new HandleResult(true, true, false), cancellationToken);
                return;
            }

            var dmResult = await HandleLiveNarrationAsync(context, message, channelState, dndState, cancellationToken);
            if (dmResult.Handled)
            {
                await PersistIfNeededAsync(context, channelState, dndState, dmResult, cancellationToken);
            }
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task HandleDndSlashCommandAsync(
        DiscordModuleContext context,
        SocketSlashCommand command,
        SocketSlashCommandDataOption option,
        CancellationToken cancellationToken)
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

        if (!context.Host.IsChannelGuildMatch(channelState, command.Channel, "dnd-slash"))
        {
            await SendEphemeralResponseAsync(command, "Guild mismatch detected. Refusing to apply DND command.");
            return;
        }

        var subOption = option.Options?.FirstOrDefault();
        if (subOption == null)
        {
            await SendEphemeralResponseAsync(command, "Specify a DND subcommand.");
            return;
        }

        var lockHandle = _channelLocks.GetOrAdd(command.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(cancellationToken);
        try
        {
            var dndState = await GetOrLoadStateAsync(channelState, cancellationToken);
            var dndStateChanged = false;
            var channelStateChanged = false;
            string response;

            switch (subOption.Name)
            {
                case "status":
                    response = BuildStatusText(dndState);
                    break;

                case "mode":
                {
                    var mode = (GetStringOption(subOption, "value") ?? string.Empty).Trim().ToLowerInvariant();
                    var campaignName = GetStringOption(subOption, "campaign");
                    if (mode is not (ModeOff or ModePrep or ModeLive))
                    {
                        response = "Mode must be one of: off, prep, live.";
                        break;
                    }

                    if (mode == ModeOff)
                    {
                        dndState.Mode = ModeOff;
                        dndState.PendingOverwrite = null;
                        if (dndState.ModuleMutedBot)
                        {
                            channelState.Options.Muted = dndState.PreviousBotMuted;
                            dndState.ModuleMutedBot = false;
                            channelStateChanged = true;
                        }

                        dndStateChanged = true;
                        response = "DND mode set to off.";
                        break;
                    }

                    var targetCampaign = GetOrCreateCampaign(dndState,
                        string.IsNullOrWhiteSpace(campaignName) ? dndState.ActiveCampaignName : campaignName);
                    dndState.ActiveCampaignName = targetCampaign.Name;
                    dndState.Mode = mode;
                    dndStateChanged = true;

                    if (mode == ModeLive && !dndState.ModuleMutedBot)
                    {
                        dndState.PreviousBotMuted = channelState.Options.Muted;
                        dndState.ModuleMutedBot = true;
                        channelState.Options.Muted = true;
                        channelStateChanged = true;
                    }

                    response = $"DND mode set to `{mode}` for campaign \"{targetCampaign.Name}\".";
                    break;
                }

                case "campaigncreate":
                {
                    var campaignName = GetStringOption(subOption, "name");
                    var prompt = GetStringOption(subOption, "prompt");
                    if (string.IsNullOrWhiteSpace(campaignName) || string.IsNullOrWhiteSpace(prompt))
                    {
                        response = "Provide both `name` and `prompt`.";
                        break;
                    }

                    var generated = await GenerateCampaignAsync(context, channelState, campaignName.Trim(), prompt.Trim(), null, cancellationToken);
                    if (string.IsNullOrWhiteSpace(generated))
                    {
                        response = "Campaign generation failed.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, campaignName);
                    campaign.Content = TrimToLimit(generated, MaxCampaignChars);
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    dndState.ActiveCampaignName = campaign.Name;
                    await RecordCampaignRevisionAsync(
                        channelState,
                        campaign,
                        triggerKind: "create",
                        triggerInput: prompt.Trim(),
                        command.User.Id,
                        command.User.Username,
                        isPromptTweak: false,
                        cancellationToken);
                    dndStateChanged = true;
                    response = $"Campaign \"{campaign.Name}\" created.\n\n{TrimToLimit(campaign.Content, 2500)}";
                    break;
                }

                case "campaignrefine":
                {
                    var campaignName = GetStringOption(subOption, "name");
                    var prompt = GetStringOption(subOption, "prompt");
                    if (string.IsNullOrWhiteSpace(campaignName) || string.IsNullOrWhiteSpace(prompt))
                    {
                        response = "Provide both `name` and `prompt`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, campaignName);
                    var refined = await GenerateCampaignAsync(
                        context,
                        channelState,
                        campaign.Name,
                        prompt.Trim(),
                        campaign.Content,
                        cancellationToken);

                    if (string.IsNullOrWhiteSpace(refined))
                    {
                        response = "Campaign refinement failed.";
                        break;
                    }

                    campaign.Content = TrimToLimit(refined, MaxCampaignChars);
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    dndState.ActiveCampaignName = campaign.Name;
                    await RecordCampaignRevisionAsync(
                        channelState,
                        campaign,
                        triggerKind: "refine",
                        triggerInput: prompt.Trim(),
                        command.User.Id,
                        command.User.Username,
                        isPromptTweak: true,
                        cancellationToken);
                    dndStateChanged = true;
                    response = $"Campaign \"{campaign.Name}\" refined.\n\n{TrimToLimit(campaign.Content, 2500)}";
                    break;
                }

                case "campaignoverwrite":
                {
                    var campaignName = GetStringOption(subOption, "name");
                    var inlineText = GetStringOption(subOption, "text");
                    var attachment = GetAttachmentOption(subOption, "file");
                    if (string.IsNullOrWhiteSpace(campaignName))
                    {
                        response = "Provide `name`.";
                        break;
                    }

                    var payload = !string.IsNullOrWhiteSpace(inlineText)
                        ? inlineText
                        : await ReadAttachmentPayloadAsync(attachment, cancellationToken);
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        response = "Provide campaign `text` or upload `.txt`, `.md`, or `.json` via `file`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, campaignName);
                    campaign.Content = TrimToLimit(payload, MaxCampaignChars);
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    dndState.ActiveCampaignName = campaign.Name;
                    await RecordCampaignRevisionAsync(
                        channelState,
                        campaign,
                        triggerKind: "overwrite",
                        triggerInput: string.IsNullOrWhiteSpace(inlineText)
                            ? $"Attachment: {attachment?.Filename}"
                            : "Inline overwrite text",
                        command.User.Id,
                        command.User.Username,
                        isPromptTweak: true,
                        cancellationToken);
                    dndStateChanged = true;
                    response = $"Campaign \"{campaign.Name}\" overwritten.";
                    break;
                }

                case "charactercreate":
                {
                    var characterName = GetStringOption(subOption, "name");
                    var concept = GetStringOption(subOption, "concept");
                    if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(concept))
                    {
                        response = "Provide both `name` and `concept`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    dndState.ActiveCampaignName = campaign.Name;
                    var sheet = await GenerateCharacterAsync(
                        context,
                        channelState,
                        campaign,
                        characterName.Trim(),
                        concept.Trim(),
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(sheet))
                    {
                        response = "Character generation failed.";
                        break;
                    }

                    var character = new DndCharacter
                    {
                        Name = characterName.Trim(),
                        Concept = concept.Trim(),
                        Sheet = TrimToLimit(sheet, 8000),
                        UpdatedUtc = DateTime.UtcNow
                    };
                    campaign.Characters[command.User.Id] = character;
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    await SaveCharacterSheetJsonAsync(channelState, campaign.Name, command.User.Id, character, cancellationToken);
                    dndStateChanged = true;
                    response = $"Character saved for <@{command.User.Id}> in \"{campaign.Name}\".\n\n{TrimToLimit(character.Sheet, 2500)}";
                    break;
                }

                case "charactershow":
                {
                    var targetUser = GetUserOption(subOption, "user");
                    var targetUserId = targetUser?.Id ?? command.User.Id;
                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    if (!campaign.Characters.TryGetValue(targetUserId, out var character) || string.IsNullOrWhiteSpace(character.Sheet))
                    {
                        response = $"No character found for <@{targetUserId}> in \"{campaign.Name}\".";
                        break;
                    }

                    response = $"Character for <@{targetUserId}> ({campaign.Name})\n\n{TrimToLimit(character.Sheet, 2500)}";
                    break;
                }

                case "npccreate":
                {
                    var npcName = GetStringOption(subOption, "name");
                    var concept = GetStringOption(subOption, "concept");
                    if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(concept))
                    {
                        response = "Provide both `name` and `concept`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    dndState.ActiveCampaignName = campaign.Name;
                    var profile = await GenerateNpcAsync(
                        context,
                        channelState,
                        campaign,
                        npcName.Trim(),
                        concept.Trim(),
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        response = "NPC generation failed.";
                        break;
                    }

                    var npc = new DndNpcMember
                    {
                        Name = npcName.Trim(),
                        Concept = concept.Trim(),
                        Profile = TrimToLimit(profile, 5000),
                        UpdatedUtc = DateTime.UtcNow
                    };
                    campaign.Npcs[npc.Name] = npc;
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    await SaveNpcSheetJsonAsync(channelState, campaign.Name, npc, cancellationToken);
                    dndStateChanged = true;
                    response = $"NPC \"{npc.Name}\" added to \"{campaign.Name}\".\n\n{TrimToLimit(npc.Profile, 1800)}";
                    break;
                }

                case "npclist":
                {
                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    if (campaign.Npcs.Count == 0)
                    {
                        response = $"No NPC companions in \"{campaign.Name}\".";
                        break;
                    }

                    var lines = campaign.Npcs
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(entry => $"- {entry.Value.Name}: {GetNpcSummary(entry.Value)}");
                    response = $"NPC companions in \"{campaign.Name}\":\n{string.Join("\n", lines)}";
                    break;
                }

                case "npcshow":
                {
                    var npcName = GetStringOption(subOption, "name");
                    if (string.IsNullOrWhiteSpace(npcName))
                    {
                        response = "Provide `name`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    if (!campaign.Npcs.TryGetValue(npcName.Trim(), out var npc))
                    {
                        response = $"NPC \"{npcName}\" not found in \"{campaign.Name}\".";
                        break;
                    }

                    response = $"NPC \"{npc.Name}\" ({campaign.Name})\nConcept: {npc.Concept}\n\n{TrimToLimit(npc.Profile, 2500)}";
                    break;
                }

                case "npcremove":
                {
                    var npcName = GetStringOption(subOption, "name");
                    if (string.IsNullOrWhiteSpace(npcName))
                    {
                        response = "Provide `name`.";
                        break;
                    }

                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    if (!campaign.Npcs.Remove(npcName.Trim()))
                    {
                        response = $"NPC \"{npcName}\" not found in \"{campaign.Name}\".";
                        break;
                    }

                    await DeleteNpcSheetJsonAsync(channelState, campaign.Name, npcName.Trim(), cancellationToken);
                    campaign.UpdatedUtc = DateTime.UtcNow;
                    dndStateChanged = true;
                    response = $"NPC \"{npcName}\" removed from \"{campaign.Name}\".";
                    break;
                }

                case "ledger":
                {
                    var count = Clamp(GetIntOption(subOption, "count") ?? 20, 1, 100);
                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    var doc = await LoadCampaignDocumentAsync(channelState, campaign.Name, cancellationToken);
                    var ledger = await LoadCampaignLedgerAsync(channelState, campaign.Name, cancellationToken);
                    if (ledger == null || ledger.Entries.Count == 0)
                    {
                        response = $"No ledger entries found for campaign \"{campaign.Name}\".";
                        break;
                    }

                    var items = ledger.Entries
                        .OrderByDescending(e => e.OccurredUtc)
                        .Take(count)
                        .ToList();
                    var lines = new List<string>
                    {
                        $"Ledger for \"{campaign.Name}\"",
                        $"- campaign-doc-id: {ledger.CampaignDocumentId ?? doc?.DocumentId ?? "unknown"}",
                        $"- entries shown: {items.Count}/{ledger.Entries.Count}"
                    };

                    foreach (var entry in items)
                    {
                        lines.Add(
                            $"[{entry.EntryId}] {entry.OccurredUtc:O} | {entry.ActionType} | {entry.ActorUsername}({entry.ActorUserId}) | rev:{entry.CampaignRevisionId}\n" +
                            $"action: {TrimToLimit(entry.ActionText, 180)}\n" +
                            $"outcome: {TrimToLimit(entry.Outcome, 220)}");
                    }

                    response = string.Join("\n", lines);
                    break;
                }

                case "campaignhistory":
                {
                    var count = Clamp(GetIntOption(subOption, "count") ?? 10, 1, 50);
                    var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
                    var document = await LoadCampaignDocumentAsync(channelState, campaign.Name, cancellationToken);
                    if (document == null)
                    {
                        response = $"No campaign document found for \"{campaign.Name}\".";
                        break;
                    }

                    var revisions = document.Revisions
                        .OrderByDescending(r => r.AppliedUtc)
                        .Take(count)
                        .ToList();
                    var lines = new List<string>
                    {
                        $"Campaign history for \"{campaign.Name}\"",
                        $"- document-id: {document.DocumentId}",
                        $"- current-revision: {document.CurrentRevisionId}",
                        $"- revisions shown: {revisions.Count}/{document.Revisions.Count}",
                        $"- prompt tweaks: {document.PromptTweaks.Count}"
                    };

                    foreach (var rev in revisions)
                    {
                        var previous = document.Revisions.FirstOrDefault(r => r.RevisionId == rev.PreviousRevisionId);
                        var delta = BuildRevisionDeltaSummary(previous?.CampaignContent, rev.CampaignContent);
                        lines.Add(
                            $"[{rev.RevisionId}] {rev.AppliedUtc:O} | {rev.TriggerKind} by {rev.TriggeredByUsername}({rev.TriggeredByUserId}) | prev:{rev.PreviousRevisionId ?? "none"}\n" +
                            $"trigger: {TrimToLimit(rev.TriggerInput, 180)}\n" +
                            $"delta: {delta}");
                    }

                    if (document.PromptTweaks.Count > 0)
                    {
                        lines.Add("Recent prompt tweaks:");
                        foreach (var tweak in document.PromptTweaks
                                     .OrderByDescending(t => t.TriggeredUtc)
                                     .Take(Math.Min(5, document.PromptTweaks.Count)))
                        {
                            lines.Add(
                                $"- [{tweak.TweakId}] {tweak.TriggeredUtc:O} | {tweak.Kind} by {tweak.TriggeredByUsername}({tweak.TriggeredByUserId}) -> rev:{tweak.ResultingRevisionId}\n" +
                                $"  {TrimToLimit(tweak.PromptOrInteraction, 180)}");
                        }
                    }

                    response = string.Join("\n", lines);
                    break;
                }

                default:
                    response = $"Unknown DND command: {subOption.Name}";
                    break;
            }

            if (dndStateChanged)
            {
                await SaveStateAsync(channelState, dndState, cancellationToken);
            }

            if (channelStateChanged)
            {
                await context.Host.SaveCachedChannelStateAsync(channelState.ChannelId);
            }

            await SendEphemeralResponseAsync(command, response);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private static string GetStringOption(SocketSlashCommandDataOption subOption, string name)
    {
        return subOption.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString();
    }

    private static SocketUser GetUserOption(SocketSlashCommandDataOption subOption, string name)
    {
        return subOption.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value as SocketUser;
    }

    private static int? GetIntOption(SocketSlashCommandDataOption subOption, string name)
    {
        var value = subOption.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static Discord.Attachment GetAttachmentOption(SocketSlashCommandDataOption subOption, string name)
    {
        return subOption.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value as Discord.Attachment;
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

    private async Task<string> ReadAttachmentPayloadAsync(Discord.Attachment attachment, CancellationToken cancellationToken)
    {
        if (!IsSupportedCampaignAttachment(attachment) || attachment.Size > MaxAttachmentBytes)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var text = await Http.GetStringAsync(attachment.Url, timeout.Token);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"DND slash attachment read failed: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private async Task SaveCharacterSheetJsonAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        ulong userId,
        DndCharacter character,
        CancellationToken cancellationToken)
    {
        var campaign = new DndCampaign { Name = campaignName, Content = string.Empty };
        var campaignDoc = await EnsureCampaignDocumentAsync(channelState, campaign, cancellationToken);
        var payload = new CharacterSheetDocument
        {
            CampaignDocumentId = campaignDoc.DocumentId,
            CampaignName = campaignName,
            UserId = userId,
            Name = character.Name,
            Concept = character.Concept,
            Sheet = character.Sheet,
            UpdatedUtc = DateTime.UtcNow
        };

        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "characters", $"{userId}.character.json");
        await WriteJsonFileAsync(path, payload, cancellationToken);
    }

    private async Task SaveNpcSheetJsonAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        DndNpcMember npc,
        CancellationToken cancellationToken)
    {
        var campaign = new DndCampaign { Name = campaignName, Content = string.Empty };
        var campaignDoc = await EnsureCampaignDocumentAsync(channelState, campaign, cancellationToken);
        var payload = new NpcSheetDocument
        {
            CampaignDocumentId = campaignDoc.DocumentId,
            CampaignName = campaignName,
            Name = npc.Name,
            Concept = npc.Concept,
            Profile = npc.Profile,
            UpdatedUtc = DateTime.UtcNow
        };

        var slug = SlugifySegment(npc.Name);
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "npcs", $"{slug}.npc.json");
        await WriteJsonFileAsync(path, payload, cancellationToken);
    }

    private async Task DeleteNpcSheetJsonAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        string npcName,
        CancellationToken cancellationToken)
    {
        var slug = SlugifySegment(npcName);
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "npcs", $"{slug}.npc.json");
        await Task.Yield();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task RecordCampaignRevisionAsync(
        InstructionGPT.ChannelState channelState,
        DndCampaign campaign,
        string triggerKind,
        string triggerInput,
        ulong userId,
        string username,
        bool isPromptTweak,
        CancellationToken cancellationToken)
    {
        var document = await EnsureCampaignDocumentAsync(channelState, campaign, cancellationToken);
        var now = DateTime.UtcNow;
        var previousRevisionId = document.CurrentRevisionId;
        var revisionId = $"rev-{document.Revisions.Count + 1:D4}";

        document.Revisions.Add(new CampaignRevision
        {
            RevisionId = revisionId,
            PreviousRevisionId = previousRevisionId,
            AppliedUtc = now,
            TriggerKind = triggerKind,
            TriggeredByUserId = userId,
            TriggeredByUsername = username,
            TriggerInput = triggerInput,
            CampaignContent = campaign.Content
        });
        document.CurrentRevisionId = revisionId;
        document.CurrentContent = campaign.Content;
        document.UpdatedUtc = now;

        if (document.InitialGeneration == null)
        {
            document.InitialGeneration = new CampaignGenerationTrigger
            {
                TriggerKind = triggerKind,
                TriggeredByUserId = userId,
                TriggeredByUsername = username,
                TriggeredUtc = now,
                TriggerInput = triggerInput,
                ResultingRevisionId = revisionId
            };
        }

        if (isPromptTweak)
        {
            document.PromptTweaks.Add(new CampaignPromptTweak
            {
                TweakId = $"tweak-{document.PromptTweaks.Count + 1:D4}",
                Kind = triggerKind,
                PromptOrInteraction = triggerInput,
                TriggeredByUserId = userId,
                TriggeredByUsername = username,
                TriggeredUtc = now,
                ResultingRevisionId = revisionId
            });
        }

        await SaveCampaignDocumentAsync(channelState, campaign.Name, document, cancellationToken);
    }

    private async Task TryAppendLedgerEntryAsync(
        InstructionGPT.ChannelState channelState,
        DndChannelState dndState,
        ulong actorUserId,
        string actorUsername,
        string actionType,
        string actionText,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var campaign = FindCampaign(dndState, dndState.ActiveCampaignName);
            if (campaign == null || string.IsNullOrWhiteSpace(campaign.Name))
            {
                return;
            }

            var campaignDoc = await EnsureCampaignDocumentAsync(channelState, campaign, cancellationToken);
            var ledger = await LoadCampaignLedgerAsync(channelState, campaign.Name, cancellationToken)
                         ?? new CampaignLedgerDocument
                         {
                             LedgerId = Guid.NewGuid().ToString("N"),
                             CampaignDocumentId = campaignDoc.DocumentId,
                             CampaignName = campaign.Name,
                             GuildChannelKey = GetGuildChannelKey(channelState),
                             CreatedUtc = DateTime.UtcNow
                         };

            ledger.CampaignDocumentId = campaignDoc.DocumentId;
            ledger.CampaignName = campaign.Name;
            ledger.GuildChannelKey = GetGuildChannelKey(channelState);
            ledger.Entries.Add(new CampaignLedgerEntry
            {
                EntryId = $"entry-{ledger.Entries.Count + 1:D6}",
                OccurredUtc = DateTime.UtcNow,
                ActorUserId = actorUserId,
                ActorUsername = actorUsername,
                ActionType = actionType,
                ActionText = TrimToLimit(actionText, 1000),
                Outcome = TrimToLimit(outcome, 1600),
                CampaignRevisionId = campaignDoc.CurrentRevisionId
            });

            await SaveCampaignLedgerAsync(channelState, campaign.Name, ledger, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"DND ledger append failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task<CampaignDocument> EnsureCampaignDocumentAsync(
        InstructionGPT.ChannelState channelState,
        DndCampaign campaign,
        CancellationToken cancellationToken)
    {
        var document = await LoadCampaignDocumentAsync(channelState, campaign.Name, cancellationToken);
        if (document != null)
        {
            return document;
        }

        document = new CampaignDocument
        {
            DocumentId = Guid.NewGuid().ToString("N"),
            CampaignName = campaign.Name,
            GuildChannelKey = GetGuildChannelKey(channelState),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            CurrentContent = campaign.Content
        };

        var revisionId = "rev-0001";
        document.Revisions.Add(new CampaignRevision
        {
            RevisionId = revisionId,
            PreviousRevisionId = null,
            AppliedUtc = DateTime.UtcNow,
            TriggerKind = "bootstrap",
            TriggeredByUserId = 0,
            TriggeredByUsername = "system",
            TriggerInput = "Bootstrapped from in-memory campaign state.",
            CampaignContent = campaign.Content ?? string.Empty
        });
        document.CurrentRevisionId = revisionId;
        document.InitialGeneration = new CampaignGenerationTrigger
        {
            TriggerKind = "bootstrap",
            TriggeredByUserId = 0,
            TriggeredByUsername = "system",
            TriggeredUtc = DateTime.UtcNow,
            TriggerInput = "Bootstrapped from in-memory campaign state.",
            ResultingRevisionId = revisionId
        };

        await SaveCampaignDocumentAsync(channelState, campaign.Name, document, cancellationToken);
        return document;
    }

    private async Task<CampaignDocument> LoadCampaignDocumentAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "campaign.json");
        return await ReadJsonFileAsync<CampaignDocument>(path, cancellationToken);
    }

    private async Task SaveCampaignDocumentAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        CampaignDocument document,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "campaign.json");
        await WriteJsonFileAsync(path, document, cancellationToken);
    }

    private async Task<CampaignLedgerDocument> LoadCampaignLedgerAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "ledger.json");
        return await ReadJsonFileAsync<CampaignLedgerDocument>(path, cancellationToken);
    }

    private async Task SaveCampaignLedgerAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        CampaignLedgerDocument ledger,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "ledger.json");
        await WriteJsonFileAsync(path, ledger, cancellationToken);
    }

    private async Task<T> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    private async Task WriteJsonFileAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string ResolveCampaignDirectory(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
        var channelPart = channelState.ChannelId.ToString();
        var campaignPart = SlugifySegment(campaignName);
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "channels",
            "dnd",
            "campaigns",
            $"{guildPart}_{channelPart}",
            campaignPart);
    }

    private static string GetGuildChannelKey(InstructionGPT.ChannelState channelState)
    {
        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
        return $"{guildPart}_{channelState.ChannelId}";
    }

    private static string SlugifySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-");
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }

    private static string BuildRevisionDeltaSummary(string previousContent, string currentContent)
    {
        var previousLines = NormalizeLines(previousContent);
        var currentLines = NormalizeLines(currentContent);
        var previousMap = BuildLineCounts(previousLines);
        var currentMap = BuildLineCounts(currentLines);

        var added = 0;
        foreach (var kvp in currentMap)
        {
            previousMap.TryGetValue(kvp.Key, out var previousCount);
            if (kvp.Value > previousCount)
            {
                added += kvp.Value - previousCount;
            }
        }

        var removed = 0;
        foreach (var kvp in previousMap)
        {
            currentMap.TryGetValue(kvp.Key, out var currentCount);
            if (kvp.Value > currentCount)
            {
                removed += kvp.Value - currentCount;
            }
        }

        var previousChars = previousContent?.Length ?? 0;
        var currentChars = currentContent?.Length ?? 0;
        var charDelta = currentChars - previousChars;
        var charDeltaLabel = charDelta >= 0 ? $"+{charDelta}" : charDelta.ToString();
        return $"+{added} lines, -{removed} lines, Δchars {charDeltaLabel}";
    }

    private static List<string> NormalizeLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string>();
        }

        return content
            .Split('\n', StringSplitOptions.None)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static Dictionary<string, int> BuildLineCounts(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            counts[line] = counts.TryGetValue(line, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static bool TryParseNpcConversationDecision(string raw, out NpcConversationDecision decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var content = raw.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = content.IndexOf('\n');
            if (firstNewline >= 0)
            {
                content = content[(firstNewline + 1)..];
            }

            if (content.EndsWith("```", StringComparison.Ordinal))
            {
                content = content[..^3];
            }

            content = content.Trim();
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var json = content[start..(end + 1)];
        try
        {
            decision = JsonSerializer.Deserialize<NpcConversationDecision>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return decision != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HandleResult> TryHandleControlAsync(
        DiscordModuleContext context,
        SocketMessage message,
        InstructionGPT.ChannelState channelState,
        DndChannelState dndState,
        string rawCommand,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return HandleResult.NotHandled;
        }

        var command = rawCommand.Trim();
        if (command.StartsWith("dnd ", StringComparison.OrdinalIgnoreCase))
        {
            command = command[4..].Trim();
        }
        else if (string.Equals(command, "dnd", StringComparison.OrdinalIgnoreCase))
        {
            command = "status";
        }

        if (string.Equals(command, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "?", StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(BuildHelpText());
            return new HandleResult(true, false, false);
        }

        if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(BuildStatusText(dndState));
            return new HandleResult(true, false, false);
        }

        if (command.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            var name = ParseCampaignNameFromModeCommand(command, dndState.ActiveCampaignName);
            var campaign = GetOrCreateCampaign(dndState, name);

            dndState.Mode = ModeLive;
            dndState.ActiveCampaignName = campaign.Name;
            if (!dndState.ModuleMutedBot)
            {
                dndState.PreviousBotMuted = channelState.Options.Muted;
                dndState.ModuleMutedBot = true;
                channelState.Options.Muted = true;
            }

            await message.Channel.SendMessageAsync(
                $"DND live mode is on for campaign \"{campaign.Name}\". " +
                "I will GM in channel chat. Use !roll/!check/!attack/!save/!initiative/!endturn for mechanics.");
            return new HandleResult(true, true, true);
        }

        if (command.StartsWith("prep", StringComparison.OrdinalIgnoreCase))
        {
            var name = ParseCampaignNameFromModeCommand(command, dndState.ActiveCampaignName);
            var campaign = GetOrCreateCampaign(dndState, name);
            dndState.Mode = ModePrep;
            dndState.ActiveCampaignName = campaign.Name;
            await message.Channel.SendMessageAsync($"DND prep mode is on for campaign \"{campaign.Name}\".");
            return new HandleResult(true, true, false);
        }

        if (string.Equals(command, "off", StringComparison.OrdinalIgnoreCase))
        {
            dndState.Mode = ModeOff;
            dndState.PendingOverwrite = null;
            if (dndState.ModuleMutedBot)
            {
                channelState.Options.Muted = dndState.PreviousBotMuted;
                dndState.ModuleMutedBot = false;
            }

            await message.Channel.SendMessageAsync("DND mode is off.");
            return new HandleResult(true, true, true);
        }

        if (command.StartsWith("create campaign", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["create campaign".Length..].Trim();
            if (!TryParseCampaignNameAndPrompt(args, dndState.ActiveCampaignName, out var campaignName, out var prompt) ||
                string.IsNullOrWhiteSpace(prompt))
            {
                await message.Channel.SendMessageAsync(
                    "Usage: `@bot create campaign \"Campaign Name\" <prompt>` or `!dnd create campaign \"Campaign Name\" <prompt>`.");
                return new HandleResult(true, false, false);
            }

            var generated = await GenerateCampaignAsync(context, channelState, campaignName, prompt, existingContent: null, cancellationToken);
            if (string.IsNullOrWhiteSpace(generated))
            {
                await message.Channel.SendMessageAsync("Campaign generation failed. Try again with a shorter prompt.");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, campaignName);
            campaign.Content = TrimToLimit(generated, MaxCampaignChars);
            campaign.UpdatedUtc = DateTime.UtcNow;
            dndState.ActiveCampaignName = campaign.Name;

            await SendChunkedAsync(message.Channel,
                $"Campaign \"{campaign.Name}\" created.\n\n{TrimToLimit(campaign.Content, 3000)}");
            return new HandleResult(true, true, false);
        }

        if (command.StartsWith("refine campaign", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["refine campaign".Length..].Trim();
            if (!TryParseCampaignNameAndPrompt(args, dndState.ActiveCampaignName, out var campaignName, out var refinementPrompt) ||
                string.IsNullOrWhiteSpace(refinementPrompt))
            {
                await message.Channel.SendMessageAsync(
                    "Usage: `@bot refine campaign \"Campaign Name\" <changes>` or `!dnd refine campaign \"Campaign Name\" <changes>`.");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, campaignName);
            var refined = await GenerateCampaignAsync(
                context,
                channelState,
                campaign.Name,
                refinementPrompt,
                campaign.Content,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(refined))
            {
                await message.Channel.SendMessageAsync("Campaign refinement failed. Try again.");
                return new HandleResult(true, false, false);
            }

            campaign.Content = TrimToLimit(refined, MaxCampaignChars);
            campaign.UpdatedUtc = DateTime.UtcNow;
            dndState.ActiveCampaignName = campaign.Name;

            await SendChunkedAsync(message.Channel,
                $"Campaign \"{campaign.Name}\" refined.\n\n{TrimToLimit(campaign.Content, 3000)}");
            return new HandleResult(true, true, false);
        }

        if (command.StartsWith("use this for my campaign", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["use this for my campaign".Length..].Trim();
            ParseCampaignNameAndRemainingText(args, dndState.ActiveCampaignName, out var campaignName, out var inlineText);
            if (string.IsNullOrWhiteSpace(campaignName))
            {
                await message.Channel.SendMessageAsync("Specify a campaign name or set an active campaign first.");
                return new HandleResult(true, false, false);
            }

            var payload = await TryGetCampaignPayloadAsync(message, inlineText, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                await message.Channel.SendMessageAsync(
                    "Attach a `.txt`, `.md`, or `.json` file, or include content after the command text.");
                return new HandleResult(true, false, false);
            }

            dndState.PendingOverwrite = new PendingOverwriteState
            {
                CampaignName = campaignName,
                RequestedByUserId = message.Author.Id,
                ProposedContent = TrimToLimit(payload, MaxCampaignChars),
                ExpiresUtc = DateTime.UtcNow.AddMinutes(PendingOverwriteMinutes)
            };

            await message.Channel.SendMessageAsync(
                $"Staged overwrite for campaign \"{campaignName}\". " +
                $"Run `!confirm overwrite {campaignName}` within {PendingOverwriteMinutes} minutes to apply.");
            return new HandleResult(true, true, false);
        }

        if (command.StartsWith("create character", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["create character".Length..].Trim();
            if (string.IsNullOrWhiteSpace(args))
            {
                await message.Channel.SendMessageAsync(
                    "Usage: `@bot create character \"Name\" <concept>` or `!dnd create character \"Name\" <concept>`.");
                return new HandleResult(true, false, false);
            }

            var activeCampaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            dndState.ActiveCampaignName = activeCampaign.Name;
            ParseCharacterRequest(args, message.Author.Username, out var characterName, out var concept);

            var sheet = await GenerateCharacterAsync(context, channelState, activeCampaign, characterName, concept, cancellationToken);
            if (string.IsNullOrWhiteSpace(sheet))
            {
                await message.Channel.SendMessageAsync("Character generation failed. Try a shorter concept.");
                return new HandleResult(true, false, false);
            }

            activeCampaign.Characters[message.Author.Id] = new DndCharacter
            {
                Name = characterName,
                Concept = concept,
                Sheet = TrimToLimit(sheet, 8000),
                UpdatedUtc = DateTime.UtcNow
            };
            activeCampaign.UpdatedUtc = DateTime.UtcNow;

            await SendChunkedAsync(message.Channel,
                $"Character saved for <@{message.Author.Id}> in campaign \"{activeCampaign.Name}\".\n\n{TrimToLimit(sheet, 2800)}");
            return new HandleResult(true, true, false);
        }

        if (command.StartsWith("show character", StringComparison.OrdinalIgnoreCase))
        {
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Characters.TryGetValue(message.Author.Id, out var character) || string.IsNullOrWhiteSpace(character.Sheet))
            {
                await message.Channel.SendMessageAsync("No character saved for you in the active campaign.");
                return new HandleResult(true, false, false);
            }

            await SendChunkedAsync(message.Channel,
                $"Character for <@{message.Author.Id}> ({campaign.Name}):\n\n{TrimToLimit(character.Sheet, 3200)}");
            return new HandleResult(true, false, false);
        }

        if (command.StartsWith("create npc", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["create npc".Length..].Trim();
            if (string.IsNullOrWhiteSpace(args))
            {
                await message.Channel.SendMessageAsync(
                    "Usage: `@bot create npc \"Name\" <concept>` or `!dnd create npc \"Name\" <concept>`.");
                return new HandleResult(true, false, false);
            }

            var activeCampaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            dndState.ActiveCampaignName = activeCampaign.Name;
            ParseNpcRequest(args, "Companion", out var npcName, out var npcConcept);

            var npcProfile = await GenerateNpcAsync(context, channelState, activeCampaign, npcName, npcConcept, cancellationToken);
            if (string.IsNullOrWhiteSpace(npcProfile))
            {
                await message.Channel.SendMessageAsync("NPC creation failed. Try again with a shorter concept.");
                return new HandleResult(true, false, false);
            }

            activeCampaign.Npcs[npcName] = new DndNpcMember
            {
                Name = npcName,
                Concept = npcConcept,
                Profile = TrimToLimit(npcProfile, 5000),
                UpdatedUtc = DateTime.UtcNow
            };
            activeCampaign.UpdatedUtc = DateTime.UtcNow;

            await SendChunkedAsync(message.Channel,
                $"NPC companion \"{npcName}\" added to campaign \"{activeCampaign.Name}\".\n\n{TrimToLimit(npcProfile, 1800)}");
            return new HandleResult(true, true, false);
        }

        if (command.StartsWith("list npcs", StringComparison.OrdinalIgnoreCase))
        {
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (campaign.Npcs.Count == 0)
            {
                await message.Channel.SendMessageAsync($"No NPC companions in campaign \"{campaign.Name}\".");
                return new HandleResult(true, false, false);
            }

            var lines = campaign.Npcs
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"- {entry.Value.Name}: {GetNpcSummary(entry.Value)}")
                .ToList();
            await SendChunkedAsync(message.Channel,
                $"NPC companions in \"{campaign.Name}\":\n{string.Join("\n", lines)}");
            return new HandleResult(true, false, false);
        }

        if (command.StartsWith("show npc", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["show npc".Length..].Trim();
            if (!TryParseSingleNameArg(args, out var npcName))
            {
                await message.Channel.SendMessageAsync("Usage: `@bot show npc \"Name\"` or `!dnd show npc \"Name\"`.");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Npcs.TryGetValue(npcName, out var npc))
            {
                await message.Channel.SendMessageAsync($"NPC \"{npcName}\" not found in campaign \"{campaign.Name}\".");
                return new HandleResult(true, false, false);
            }

            await SendChunkedAsync(message.Channel,
                $"NPC \"{npc.Name}\" ({campaign.Name})\nConcept: {npc.Concept}\n\n{TrimToLimit(npc.Profile, 3200)}");
            return new HandleResult(true, false, false);
        }

        if (command.StartsWith("remove npc", StringComparison.OrdinalIgnoreCase))
        {
            var args = command["remove npc".Length..].Trim();
            if (!TryParseSingleNameArg(args, out var npcName))
            {
                await message.Channel.SendMessageAsync("Usage: `@bot remove npc \"Name\"` or `!dnd remove npc \"Name\"`.");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            if (!campaign.Npcs.Remove(npcName))
            {
                await message.Channel.SendMessageAsync($"NPC \"{npcName}\" not found in campaign \"{campaign.Name}\".");
                return new HandleResult(true, false, false);
            }

            campaign.UpdatedUtc = DateTime.UtcNow;
            await message.Channel.SendMessageAsync($"Removed NPC companion \"{npcName}\" from campaign \"{campaign.Name}\".");
            return new HandleResult(true, true, false);
        }

        return HandleResult.NotHandled;
    }

    private async Task<HandleResult> TryHandleConfirmationAsync(
        SocketMessage message,
        DndChannelState dndState,
        string content,
        CancellationToken cancellationToken)
    {
        if (!content.StartsWith("!confirm", StringComparison.OrdinalIgnoreCase))
        {
            return HandleResult.NotHandled;
        }

        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !string.Equals(parts[1], "overwrite", StringComparison.OrdinalIgnoreCase))
        {
            return new HandleResult(true, false, false);
        }

        if (dndState.PendingOverwrite == null)
        {
            await message.Channel.SendMessageAsync("No pending campaign overwrite.");
            return new HandleResult(true, false, false);
        }

        if (DateTime.UtcNow > dndState.PendingOverwrite.ExpiresUtc)
        {
            dndState.PendingOverwrite = null;
            await message.Channel.SendMessageAsync("Pending overwrite expired. Submit again.");
            return new HandleResult(true, true, false);
        }

        if (dndState.PendingOverwrite.RequestedByUserId != message.Author.Id)
        {
            await message.Channel.SendMessageAsync("Only the user who staged the overwrite can confirm it.");
            return new HandleResult(true, false, false);
        }

        var providedCampaign = string.Join(" ", parts.Skip(2)).Trim().Trim('"');
        if (!string.Equals(providedCampaign, dndState.PendingOverwrite.CampaignName, StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(
                $"Pending overwrite is for \"{dndState.PendingOverwrite.CampaignName}\". " +
                $"Use `!confirm overwrite {dndState.PendingOverwrite.CampaignName}`.");
            return new HandleResult(true, false, false);
        }

        var campaign = GetOrCreateCampaign(dndState, dndState.PendingOverwrite.CampaignName);
        campaign.Content = TrimToLimit(dndState.PendingOverwrite.ProposedContent, MaxCampaignChars);
        campaign.UpdatedUtc = DateTime.UtcNow;
        dndState.ActiveCampaignName = campaign.Name;
        dndState.PendingOverwrite = null;

        await message.Channel.SendMessageAsync($"Campaign \"{campaign.Name}\" overwritten from provided content.");
        return new HandleResult(true, true, false);
    }

    private async Task<HandleResult> TryHandleActionCommandAsync(
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndChannelState dndState,
        string content,
        CancellationToken cancellationToken)
    {
        if (string.Equals(content, "!help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(content, "!dnd help", StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(BuildActionHelpText());
            return new HandleResult(true, false, false);
        }

        if (content.StartsWith("!roll", StringComparison.OrdinalIgnoreCase))
        {
            var expression = content.Length > 5 ? content[5..].Trim() : "d20";
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid roll.");
                return new HandleResult(true, false, false);
            }

            var response = $"🎲 <@{message.Author.Id}> rolled `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "roll", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!check", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content.Length > 6 ? content[6..].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(tail))
            {
                await message.Channel.SendMessageAsync("Usage: `!check <skill> [d20+mod]`");
                return new HandleResult(true, false, false);
            }

            var tokens = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var skill = tokens[0];
            var expression = tokens.Length > 1 ? NormalizeCheckExpression(tokens[1]) : "d20";
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid check roll.");
                return new HandleResult(true, false, false);
            }

            var response = $"🧭 {skill} check for <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "check", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!save", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content.Length > 5 ? content[5..].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(tail))
            {
                await message.Channel.SendMessageAsync("Usage: `!save <ability> [d20+mod]`");
                return new HandleResult(true, false, false);
            }

            var tokens = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ability = tokens[0];
            var expression = tokens.Length > 1 ? NormalizeCheckExpression(tokens[1]) : "d20";
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid save roll.");
                return new HandleResult(true, false, false);
            }

            var response = $"🛡️ {ability} save for <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "save", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!attack", StringComparison.OrdinalIgnoreCase))
        {
            var attackTail = content.Length > 7 ? content[7..].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(attackTail))
            {
                await message.Channel.SendMessageAsync("Usage: `!attack <target> [d20+mod]`");
                return new HandleResult(true, false, false);
            }

            ParseAttackArgs(attackTail, out var target, out var expression);
            expression = NormalizeCheckExpression(expression);
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid attack roll.");
                return new HandleResult(true, false, false);
            }

            var response =
                $"⚔️ Attack vs {target} by <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**\n" +
                "If it hits, roll damage with `!roll`.";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!initiative", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content.Length > 11 ? content[11..].Trim() : string.Empty;
            var bonus = 0;
            if (!string.IsNullOrWhiteSpace(tail) && !int.TryParse(tail, out bonus))
            {
                await message.Channel.SendMessageAsync("Usage: `!initiative [bonus]`");
                return new HandleResult(true, false, false);
            }

            if (!TryEvaluateRoll($"d20{(bonus >= 0 ? "+" : string.Empty)}{bonus}", out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid initiative roll.");
                return new HandleResult(true, false, false);
            }

            dndState.Session.Initiative[message.Author.Id] = roll.Total;
            dndState.Session.TurnOrder = dndState.Session.Initiative
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key)
                .Select(entry => entry.Key)
                .ToList();
            dndState.Session.CurrentTurnIndex = 0;

            var order = string.Join(" -> ", dndState.Session.TurnOrder.Select(id => $"<@{id}>({dndState.Session.Initiative[id]})"));
            var response =
                $"🎯 Initiative for <@{message.Author.Id}>: `{roll.Expression}` => **{roll.Total}**\n" +
                $"Order: {order}\n" +
                $"Current turn: <@{dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex]}>";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "initiative", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!endturn", StringComparison.OrdinalIgnoreCase))
        {
            if (dndState.Session.TurnOrder.Count == 0)
            {
                await message.Channel.SendMessageAsync("No initiative order yet. Use `!initiative [bonus]` first.");
                return new HandleResult(true, false, false);
            }

            var force = content.Contains("force", StringComparison.OrdinalIgnoreCase);
            var currentUserId = dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex];
            if (!force && currentUserId != message.Author.Id)
            {
                await message.Channel.SendMessageAsync(
                    $"It is currently <@{currentUserId}>'s turn. Use `!endturn force` if the table agrees.");
                return new HandleResult(true, false, false);
            }

            dndState.Session.CurrentTurnIndex = (dndState.Session.CurrentTurnIndex + 1) % dndState.Session.TurnOrder.Count;
            var nextUserId = dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex];
            var response = $"⏭️ Turn ended. Next up: <@{nextUserId}>.";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{message.Author.Username}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "endturn", content, response, cancellationToken);
            return new HandleResult(true, true, false);
        }

        return HandleResult.NotHandled;
    }

    private async Task<HandleResult> HandleLiveNarrationAsync(
        DiscordModuleContext context,
        SocketMessage message,
        InstructionGPT.ChannelState channelState,
        DndChannelState dndState,
        CancellationToken cancellationToken)
    {
        var campaign = FindCampaign(dndState, dndState.ActiveCampaignName);
        if (campaign == null || string.IsNullOrWhiteSpace(campaign.Content))
        {
            await message.Channel.SendMessageAsync(
                "No active campaign content. Use `@bot create campaign ...` or `@bot use this for my campaign ...` first.");
            return new HandleResult(true, false, false);
        }

        var gmReply = await GenerateNarrationAsync(context, channelState, campaign, dndState, message, cancellationToken);
        if (string.IsNullOrWhiteSpace(gmReply))
        {
            gmReply =
                "I need a quick reset before the next scene. " +
                "Describe your next action, and if it needs chance, include `!roll` or `!check`.";
        }

        await SendChunkedAsync(message.Channel, gmReply);
        AppendTranscript(dndState, $"GM: {TrimToLimit(gmReply, 3000)}");
        await TryAppendLedgerEntryAsync(
            channelState,
            dndState,
            message.Author.Id,
            message.Author.Username,
            "party-action",
            message.Content,
            TrimToLimit(gmReply, 1200),
            cancellationToken);
        return new HandleResult(true, true, false);
    }

    private async Task<string> GenerateCampaignAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string campaignName,
        string prompt,
        string existingContent,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(context, channelState);
        var requestPrompt = existingContent == null
            ? BuildCreateCampaignPrompt(campaignName, prompt)
            : BuildRefineCampaignPrompt(campaignName, existingContent, prompt);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are a campaign designer for tabletop D&D using SRD 5.1-compatible content only. " +
                    "Return concise markdown. Do not include copyrighted non-SRD text."),
                new(StaticValues.ChatMessageRoles.User, requestPrompt)
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND campaign generation failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        return response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private async Task<string> GenerateCharacterAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndCampaign campaign,
        string characterName,
        string concept,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(context, channelState);
        var prompt = new StringBuilder();
        prompt.AppendLine($"Create a level 1 character for campaign \"{campaign.Name}\".");
        prompt.AppendLine("Use SRD 5.1-compatible options only.");
        prompt.AppendLine("Return concise markdown with: Class, Ancestry/Lineage, Background, Ability scores, Proficiencies, Equipment, 3 roleplay hooks.");
        prompt.AppendLine($"Character name: {characterName}");
        prompt.AppendLine($"Concept: {concept}");
        prompt.AppendLine("Campaign summary:");
        prompt.AppendLine(TrimToLimit(campaign.Content, 4000));

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You generate practical D&D character sheets using SRD-compatible rules text only."),
                new(StaticValues.ChatMessageRoles.User, prompt.ToString())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND character generation failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        return response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private async Task<string> GenerateNpcAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndCampaign campaign,
        string npcName,
        string concept,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(context, channelState);
        var prompt = new StringBuilder();
        prompt.AppendLine($"Create a party NPC companion named \"{npcName}\".");
        prompt.AppendLine("Use SRD 5.1-compatible flavor and mechanics only.");
        prompt.AppendLine("Return concise markdown with sections: Role, Personality, Voice, Combat Style, Bond, Flaw, and 4 example dialogue lines.");
        prompt.AppendLine($"Concept: {concept}");
        prompt.AppendLine("Campaign summary:");
        prompt.AppendLine(TrimToLimit(campaign.Content, 3500));

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You design NPC allies for live roleplay. Keep profiles compact and practical."),
                new(StaticValues.ChatMessageRoles.User, prompt.ToString())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND NPC generation failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        return response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private async Task<string> TryGenerateNpcConversationReplyAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndChannelState dndState,
        SocketMessage message,
        CancellationToken cancellationToken)
    {
        var campaign = FindCampaign(dndState, dndState.ActiveCampaignName);
        if (campaign?.Npcs == null || campaign.Npcs.Count == 0 || string.IsNullOrWhiteSpace(message.Content))
        {
            return null;
        }

        var model = ResolveModel(context, channelState);
        var transcript = string.Join("\n", dndState.Session.Transcript.TakeLast(10));
        var npcRoster = string.Join("\n", campaign.Npcs.Values
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => $"- {n.Name}: {GetNpcSummary(n)}"));

        var prompt = new StringBuilder();
        prompt.AppendLine("Decide if the player message is talking to one NPC party member.");
        prompt.AppendLine("Return JSON only with keys: respond, npcName, message.");
        prompt.AppendLine("If not clearly directed to an NPC, return respond=false.");
        prompt.AppendLine($"Campaign: {campaign.Name}");
        prompt.AppendLine("NPC party roster:");
        prompt.AppendLine(npcRoster);
        prompt.AppendLine("Recent transcript:");
        prompt.AppendLine(transcript);
        prompt.AppendLine($"Latest player ({message.Author.Username}) message:");
        prompt.AppendLine(message.Content);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You classify and answer NPC-addressed player chat. " +
                    "Output strict JSON only: {\"respond\":true|false,\"npcName\":\"name\",\"message\":\"line\"}. " +
                    "When respond=true, message must be 1-3 in-character sentences from that NPC only. " +
                    "No narration, no markdown, no extra keys."),
                new(StaticValues.ChatMessageRoles.User, prompt.ToString())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND NPC conversation inference failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content;
        if (!TryParseNpcConversationDecision(content, out var decision) || !decision.Respond)
        {
            return null;
        }

        var npc = campaign.Npcs.Values.FirstOrDefault(n =>
            string.Equals(n.Name, decision.NpcName, StringComparison.OrdinalIgnoreCase));
        if (npc == null || string.IsNullOrWhiteSpace(decision.Message))
        {
            return null;
        }

        var line = decision.Message.Trim();
        if (line.StartsWith($"{npc.Name}:", StringComparison.OrdinalIgnoreCase))
        {
            line = line[(npc.Name.Length + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return $"{npc.Name}: {TrimToLimit(line, 600)}";
    }

    private async Task<string> GenerateNarrationAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndCampaign campaign,
        DndChannelState dndState,
        SocketMessage message,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(context, channelState);
        var transcript = string.Join("\n", dndState.Session.Transcript.TakeLast(12));
        var order = dndState.Session.TurnOrder.Count == 0
            ? "No initiative order set."
            : string.Join(" -> ", dndState.Session.TurnOrder.Select((id, idx) =>
                idx == dndState.Session.CurrentTurnIndex ? $"<@{id}> (current)" : $"<@{id}>"));
        var addressedNpcs = FindAddressedNpcNames(campaign, message.Content);
        var npcRoster = BuildNpcRoster(campaign, addressedNpcs);

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"Campaign name: {campaign.Name}");
        userPrompt.AppendLine("Campaign summary:");
        userPrompt.AppendLine(TrimToLimit(campaign.Content, 5000));
        userPrompt.AppendLine();
        userPrompt.AppendLine("Party NPC companions:");
        userPrompt.AppendLine(npcRoster);
        userPrompt.AppendLine();
        userPrompt.AppendLine($"Initiative order: {order}");
        userPrompt.AppendLine("Recent transcript:");
        userPrompt.AppendLine(transcript);
        userPrompt.AppendLine();
        userPrompt.AppendLine($"Latest player message from {message.Author.Username}:");
        userPrompt.AppendLine(message.Content);
        if (addressedNpcs.Count > 0)
        {
            userPrompt.AppendLine($"Addressed NPCs in this message: {string.Join(", ", addressedNpcs)}");
        }

        userPrompt.AppendLine();
        userPrompt.AppendLine(
            "Respond as the GM with in-world narration and options. " +
            "If a chance-based resolution is needed, explicitly ask for !roll, !check, !attack, or !save. " +
            "When appropriate, include short in-character party NPC dialogue lines prefixed exactly `NPC <name>:`.");

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are a live text Dungeon Master for D&D sessions. " +
                    "Stay immersive, concise, and fair. Never fabricate dice rolls. " +
                    "Use SRD 5.1 style rules and keep responses under 10 lines when possible. " +
                    "You may voice party NPC companions naturally during play events."),
                new(StaticValues.ChatMessageRoles.User, userPrompt.ToString())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND narration failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        return response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private async Task PersistIfNeededAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndChannelState dndState,
        HandleResult result,
        CancellationToken cancellationToken)
    {
        if (result.DndStateChanged)
        {
            await SaveStateAsync(channelState, dndState, cancellationToken);
        }

        if (result.ChannelStateChanged)
        {
            await context.Host.SaveCachedChannelStateAsync(channelState.ChannelId);
        }
    }

    private async Task<DndChannelState> GetOrLoadStateAsync(InstructionGPT.ChannelState channelState, CancellationToken cancellationToken)
    {
        if (_stateByChannel.TryGetValue(channelState.ChannelId, out var cached))
        {
            NormalizeState(cached);
            return cached;
        }

        var statePath = ResolveStatePath(channelState);
        DndChannelState loaded = null;
        if (File.Exists(statePath))
        {
            var json = await File.ReadAllTextAsync(statePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                loaded = JsonSerializer.Deserialize<DndChannelState>(json, _jsonOptions);
            }
        }

        loaded ??= new DndChannelState();
        NormalizeState(loaded);
        _stateByChannel[channelState.ChannelId] = loaded;
        return loaded;
    }

    private async Task SaveStateAsync(InstructionGPT.ChannelState channelState, DndChannelState dndState, CancellationToken cancellationToken)
    {
        NormalizeState(dndState);
        var statePath = ResolveStatePath(channelState);
        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(dndState, _jsonOptions);
        await File.WriteAllTextAsync(statePath, json, cancellationToken);
        _stateByChannel[channelState.ChannelId] = dndState;
    }

    private static string ResolveStatePath(InstructionGPT.ChannelState channelState)
    {
        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
        return Path.Combine(Directory.GetCurrentDirectory(), "channels", "dnd", $"{guildPart}_{channelState.ChannelId}.json");
    }

    private static string ResolveModel(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        return channelState?.InstructionChat?.ChatBotState?.Parameters?.Model
               ?? context.DefaultParameters?.Model
               ?? "gpt-4o-mini";
    }

    private static void NormalizeState(DndChannelState state)
    {
        state.Mode = NormalizeMode(state.Mode);
        state.ActiveCampaignName ??= "default";
        state.Campaigns = state.Campaigns == null
            ? new Dictionary<string, DndCampaign>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DndCampaign>(state.Campaigns, StringComparer.OrdinalIgnoreCase);

        foreach (var campaign in state.Campaigns.Values)
        {
            campaign.Name ??= "default";
            campaign.Content ??= string.Empty;
            campaign.Characters ??= new Dictionary<ulong, DndCharacter>();
            campaign.Npcs = campaign.Npcs == null
                ? new Dictionary<string, DndNpcMember>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DndNpcMember>(campaign.Npcs, StringComparer.OrdinalIgnoreCase);
        }

        state.Session ??= new SessionState();
        state.Session.Initiative ??= new Dictionary<ulong, int>();
        state.Session.TurnOrder ??= new List<ulong>();
        state.Session.Transcript ??= new List<string>();
        if (state.Session.CurrentTurnIndex < 0)
        {
            state.Session.CurrentTurnIndex = 0;
        }

        if (state.PendingOverwrite != null && DateTime.UtcNow > state.PendingOverwrite.ExpiresUtc)
        {
            state.PendingOverwrite = null;
        }
    }

    private static string NormalizeMode(string value)
    {
        if (string.Equals(value, ModeLive, StringComparison.OrdinalIgnoreCase))
        {
            return ModeLive;
        }

        if (string.Equals(value, ModePrep, StringComparison.OrdinalIgnoreCase))
        {
            return ModePrep;
        }

        return ModeOff;
    }

    private static DndCampaign GetOrCreateCampaign(DndChannelState state, string name)
    {
        NormalizeState(state);
        var key = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        if (state.Campaigns.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new DndCampaign
        {
            Name = key,
            Content = string.Empty,
            UpdatedUtc = DateTime.UtcNow
        };
        state.Campaigns[key] = created;
        return created;
    }

    private static DndCampaign FindCampaign(DndChannelState state, string name)
    {
        if (state?.Campaigns == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        state.Campaigns.TryGetValue(name.Trim(), out var campaign);
        return campaign;
    }

    private static string ParseCampaignNameFromModeCommand(string command, string activeCampaign)
    {
        var cleaned = command.Trim();
        if (cleaned.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..].Trim();
        }
        else if (cleaned.StartsWith("prep", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[4..].Trim();
        }

        if (cleaned.StartsWith("campaign", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["campaign".Length..].Trim();
        }

        cleaned = cleaned.Trim('"');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.IsNullOrWhiteSpace(activeCampaign) ? "default" : activeCampaign;
        }

        return cleaned;
    }

    private static bool TryParseCampaignNameAndPrompt(
        string args,
        string activeCampaign,
        out string campaignName,
        out string prompt)
    {
        campaignName = null;
        prompt = null;
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        var trimmed = args.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }

            campaignName = trimmed[1..endQuote].Trim();
            prompt = trimmed[(endQuote + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(campaignName) && !string.IsNullOrWhiteSpace(prompt);
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex > 0)
        {
            campaignName = trimmed[..colonIndex].Trim().Trim('"');
            prompt = trimmed[(colonIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(campaignName) && !string.IsNullOrWhiteSpace(prompt);
        }

        if (!string.IsNullOrWhiteSpace(activeCampaign))
        {
            campaignName = activeCampaign;
            prompt = trimmed;
            return true;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            return false;
        }

        campaignName = split[0].Trim('"');
        prompt = split.Length > 1 ? split[1].Trim() : string.Empty;
        return !string.IsNullOrWhiteSpace(campaignName) && !string.IsNullOrWhiteSpace(prompt);
    }

    private static void ParseCampaignNameAndRemainingText(string args, string activeCampaign, out string campaignName, out string remainder)
    {
        campaignName = activeCampaign;
        remainder = string.Empty;
        if (string.IsNullOrWhiteSpace(args))
        {
            return;
        }

        var trimmed = args.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                campaignName = trimmed[1..endQuote].Trim();
                remainder = trimmed[(endQuote + 1)..].Trim();
                return;
            }
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length > 0 && !string.IsNullOrWhiteSpace(split[0]))
        {
            campaignName = split[0].Trim('"');
            remainder = split.Length > 1 ? split[1] : string.Empty;
        }
    }

    private static void ParseCharacterRequest(string args, string fallbackName, out string characterName, out string concept)
    {
        characterName = fallbackName;
        concept = args.Trim();
        if (string.IsNullOrWhiteSpace(args))
        {
            concept = $"Create a flexible adventurer for {fallbackName}.";
            return;
        }

        var trimmed = args.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                characterName = trimmed[1..endQuote].Trim();
                concept = trimmed[(endQuote + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(concept))
                {
                    concept = "No extra concept provided.";
                }

                return;
            }
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex > 0)
        {
            characterName = trimmed[..colonIndex].Trim();
            concept = trimmed[(colonIndex + 1)..].Trim();
            return;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 1)
        {
            characterName = fallbackName;
            concept = split[0];
            return;
        }

        characterName = split[0].Trim('"');
        concept = split[1].Trim();
    }

    private static void ParseNpcRequest(string args, string fallbackName, out string npcName, out string concept)
    {
        npcName = fallbackName;
        concept = args.Trim();
        if (string.IsNullOrWhiteSpace(args))
        {
            concept = "Reliable party companion.";
            return;
        }

        var trimmed = args.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                npcName = trimmed[1..endQuote].Trim();
                concept = trimmed[(endQuote + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(concept))
                {
                    concept = $"Companion profile for {npcName}.";
                }

                return;
            }
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex > 0)
        {
            npcName = trimmed[..colonIndex].Trim();
            concept = trimmed[(colonIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(concept))
            {
                concept = $"Companion profile for {npcName}.";
            }

            return;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 1)
        {
            npcName = split[0].Trim('"');
            concept = $"Companion profile for {npcName}.";
            return;
        }

        npcName = split[0].Trim('"');
        concept = split[1].Trim();
    }

    private static bool TryParseSingleNameArg(string input, out string value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                value = trimmed[1..endQuote].Trim();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        var split = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            return false;
        }

        value = split[0].Trim('"');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IReadOnlyList<string> FindAddressedNpcNames(DndCampaign campaign, string messageContent)
    {
        if (campaign?.Npcs == null || campaign.Npcs.Count == 0 || string.IsNullOrWhiteSpace(messageContent))
        {
            return Array.Empty<string>();
        }

        var addressed = new List<string>();
        foreach (var npc in campaign.Npcs.Values)
        {
            if (string.IsNullOrWhiteSpace(npc?.Name))
            {
                continue;
            }

            if (Regex.IsMatch(messageContent, $@"\b{Regex.Escape(npc.Name)}\b", RegexOptions.IgnoreCase))
            {
                addressed.Add(npc.Name);
            }
        }

        return addressed;
    }

    private static string BuildNpcRoster(DndCampaign campaign, IReadOnlyList<string> addressedNpcs)
    {
        if (campaign?.Npcs == null || campaign.Npcs.Count == 0)
        {
            return "None";
        }

        var lines = new List<string>();
        foreach (var npc in campaign.Npcs.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var focus = addressedNpcs.Any(name => string.Equals(name, npc.Name, StringComparison.OrdinalIgnoreCase))
                ? " (addressed now)"
                : string.Empty;
            lines.Add($"- {npc.Name}{focus}: {GetNpcSummary(npc)}");
        }

        return string.Join("\n", lines);
    }

    private static string GetNpcSummary(DndNpcMember npc)
    {
        if (npc == null)
        {
            return "No profile.";
        }

        if (!string.IsNullOrWhiteSpace(npc.Concept))
        {
            return TrimToLimit(npc.Concept, 140).Replace('\n', ' ');
        }

        if (!string.IsNullOrWhiteSpace(npc.Profile))
        {
            var firstLine = npc.Profile
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return TrimToLimit(firstLine, 140);
            }
        }

        return "Companion";
    }

    private static string BuildCreateCampaignPrompt(string campaignName, string userPrompt)
    {
        return
            $"Create a D&D campaign named \"{campaignName}\" from this prompt:\n{userPrompt}\n\n" +
            "Format in markdown with sections: Tone, Setting, Starting Situation, 5 NPCs, 3 Factions, " +
            "Act 1 Outline, 5 Quest Hooks, and DM Notes. Keep it practical for live play.";
    }

    private static string BuildRefineCampaignPrompt(string campaignName, string existingContent, string refinementPrompt)
    {
        return
            $"Refine this campaign named \"{campaignName}\".\n\nCurrent campaign:\n{existingContent}\n\n" +
            $"Requested refinement:\n{refinementPrompt}\n\n" +
            "Return a full updated campaign in markdown with the same sections.";
    }

    private static async Task<string> TryGetCampaignPayloadAsync(SocketMessage message, string inlineText, CancellationToken cancellationToken)
    {
        if (message.Attachments is { Count: > 0 })
        {
            var attachment = message.Attachments.FirstOrDefault(IsSupportedCampaignAttachment);
            if (attachment != null)
            {
                if (attachment.Size > MaxAttachmentBytes)
                {
                    return null;
                }

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var text = await Http.GetStringAsync(attachment.Url, timeout.Token);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
                catch (Exception ex)
                {
                    await Console.Out.WriteLineAsync($"DND attachment read failed: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(inlineText))
        {
            return inlineText;
        }

        return null;
    }

    private static bool IsSupportedCampaignAttachment(Discord.Attachment attachment)
    {
        if (attachment == null || string.IsNullOrWhiteSpace(attachment.Filename))
        {
            return false;
        }

        var extension = Path.GetExtension(attachment.Filename)?.ToLowerInvariant();
        return extension is ".txt" or ".md" or ".json";
    }

    private static bool TryEvaluateRoll(string rawExpression, out RollOutcome outcome, out string error)
    {
        outcome = null;
        error = null;
        var expression = NormalizeCheckExpression(rawExpression);
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Provide a dice expression like `d20+5`.";
            return false;
        }

        var match = DiceExpressionRegex.Match(expression);
        if (!match.Success)
        {
            error = "Invalid dice expression. Use forms like `d20`, `d20+5`, or `2d6+3`.";
            return false;
        }

        var countRaw = match.Groups["count"].Value;
        var sidesRaw = match.Groups["sides"].Value;
        var modifierRaw = match.Groups["modifier"].Value;

        var count = string.IsNullOrWhiteSpace(countRaw) ? 1 : int.Parse(countRaw);
        var sides = int.Parse(sidesRaw);
        var modifier = string.IsNullOrWhiteSpace(modifierRaw) ? 0 : int.Parse(modifierRaw);

        if (count < 1 || count > 20)
        {
            error = "Dice count must be between 1 and 20.";
            return false;
        }

        if (sides < 2 || sides > 1000)
        {
            error = "Dice sides must be between 2 and 1000.";
            return false;
        }

        var rolls = new List<int>(count);
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            var value = RandomNumberGenerator.GetInt32(1, sides + 1);
            rolls.Add(value);
            total += value;
        }

        total += modifier;
        outcome = new RollOutcome(expression, rolls, modifier, total);
        return true;
    }

    private static string NormalizeCheckExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "d20";
        }

        var trimmed = expression.Trim();
        if (int.TryParse(trimmed, out var asModifier))
        {
            return $"d20{(asModifier >= 0 ? "+" : string.Empty)}{asModifier}";
        }

        if (trimmed.StartsWith("+", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal))
        {
            if (int.TryParse(trimmed, out var modifier))
            {
                return $"d20{(modifier >= 0 ? "+" : string.Empty)}{modifier}";
            }
        }

        return trimmed;
    }

    private static void ParseAttackArgs(string attackTail, out string target, out string expression)
    {
        target = attackTail.Trim();
        expression = "d20";
        var parts = attackTail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            target = "target";
            return;
        }

        var last = parts[^1];
        if (DiceExpressionRegex.IsMatch(NormalizeCheckExpression(last)) ||
            int.TryParse(last, out _) ||
            (last.StartsWith("+", StringComparison.Ordinal) || last.StartsWith("-", StringComparison.Ordinal)))
        {
            expression = last;
            target = string.Join(" ", parts.Take(parts.Length - 1)).Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                target = "target";
            }
        }
    }

    private static void AppendTranscript(DndChannelState state, string line)
    {
        if (state?.Session == null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        state.Session.Transcript.Add(TrimToLimit(line, 500));
        if (state.Session.Transcript.Count > MaxTranscriptLines)
        {
            state.Session.Transcript = state.Session.Transcript
                .Skip(state.Session.Transcript.Count - MaxTranscriptLines)
                .ToList();
        }
    }

    private static string BuildStatusText(DndChannelState state)
    {
        var active = state.ActiveCampaignName ?? "none";
        var campaignCount = state.Campaigns?.Count ?? 0;
        var activeNpcCount = 0;
        if (!string.IsNullOrWhiteSpace(active) &&
            state.Campaigns != null &&
            state.Campaigns.TryGetValue(active, out var activeCampaign) &&
            activeCampaign?.Npcs != null)
        {
            activeNpcCount = activeCampaign.Npcs.Count;
        }

        var pending = state.PendingOverwrite == null
            ? "none"
            : $"{state.PendingOverwrite.CampaignName} (expires {state.PendingOverwrite.ExpiresUtc:O})";
        return
            $"DND status\n" +
            $"- mode: {state.Mode}\n" +
            $"- active campaign: {active}\n" +
            $"- campaigns: {campaignCount}\n" +
            $"- npc companions (active campaign): {activeNpcCount}\n" +
            $"- pending overwrite: {pending}\n" +
            "- mechanics: !roll !check !save !attack !initiative !endturn";
    }

    private static string BuildHelpText()
    {
        return
            "DND setup is slash-only\n" +
            "- `/gptcli dnd status`\n" +
            "- `/gptcli dnd mode value:<off|prep|live> campaign:<name>`\n" +
            "- `/gptcli dnd campaigncreate name:<name> prompt:<text>`\n" +
            "- `/gptcli dnd campaignrefine name:<name> prompt:<text>`\n" +
            "- `/gptcli dnd campaignoverwrite name:<name> text:<text> or file:<attachment>`\n" +
            "- `/gptcli dnd charactercreate name:<name> concept:<text>`\n" +
            "- `/gptcli dnd charactershow [user]`\n" +
            "- `/gptcli dnd npccreate name:<name> concept:<text>`\n" +
            "- `/gptcli dnd npclist`\n" +
            "- `/gptcli dnd npcshow name:<name>`\n" +
            "- `/gptcli dnd npcremove name:<name>`\n" +
            "- `/gptcli dnd ledger [count]`\n" +
            "- `/gptcli dnd campaignhistory [count]`\n\n" +
            BuildActionHelpText();
    }

    private static string BuildActionHelpText()
    {
        return
            "DND action tags (live mode)\n" +
            "- `!roll d20+5`\n" +
            "- `!check stealth +3`\n" +
            "- `!save dex +2`\n" +
            "- `!attack goblin d20+6`\n" +
            "- `!initiative [bonus]`\n" +
            "- `!endturn`";
    }

    private static string StripBotMentions(string text, ulong botUserId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (Match match in BotMentionRegexTemplate.Matches(text))
        {
            if (!ulong.TryParse(match.Groups["id"].Value, out var id) || id != botUserId)
            {
                continue;
            }

            result = result.Replace(match.Value, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return result.Trim();
    }

    private static string TrimToLimit(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "\n...[truncated]";
    }

    private static string FormatModifier(int modifier)
    {
        if (modifier == 0)
        {
            return string.Empty;
        }

        return modifier > 0 ? $" + {modifier}" : $" - {Math.Abs(modifier)}";
    }

    private static async Task SendChunkedAsync(IMessageChannel channel, string text)
    {
        var payload = string.IsNullOrWhiteSpace(text) ? "(empty)" : text;
        if (payload.Length <= DiscordMessageLimit)
        {
            await channel.SendMessageAsync(payload);
            return;
        }

        var offset = 0;
        while (offset < payload.Length)
        {
            var take = Math.Min(DiscordMessageLimit, payload.Length - offset);
            if (take == DiscordMessageLimit)
            {
                var breakPos = payload.LastIndexOf('\n', offset + take - 1, take);
                if (breakPos >= offset + 200)
                {
                    take = breakPos - offset + 1;
                }
            }

            var chunk = payload.Substring(offset, take).TrimEnd('\n');
            if (string.IsNullOrWhiteSpace(chunk))
            {
                chunk = payload.Substring(offset, Math.Min(DiscordMessageLimit, payload.Length - offset));
            }

            await channel.SendMessageAsync(chunk);
            offset += take;
            while (offset < payload.Length && payload[offset] == '\n')
            {
                offset++;
            }
        }
    }

    private sealed record HandleResult(bool Handled, bool DndStateChanged, bool ChannelStateChanged)
    {
        public static HandleResult NotHandled { get; } = new(false, false, false);
    }

    private sealed record RollOutcome(string Expression, IReadOnlyList<int> Rolls, int Modifier, int Total);

    private sealed class DndChannelState
    {
        public string Mode { get; set; } = ModeOff;
        public string ActiveCampaignName { get; set; } = "default";
        public Dictionary<string, DndCampaign> Campaigns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SessionState Session { get; set; } = new();
        public PendingOverwriteState PendingOverwrite { get; set; }
        public bool PreviousBotMuted { get; set; }
        public bool ModuleMutedBot { get; set; }
    }

    private sealed class DndCampaign
    {
        public string Name { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<ulong, DndCharacter> Characters { get; set; } = new();
        public Dictionary<string, DndNpcMember> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DndCharacter
    {
        public string Name { get; set; }
        public string Concept { get; set; }
        public string Sheet { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class DndNpcMember
    {
        public string Name { get; set; }
        public string Concept { get; set; }
        public string Profile { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class CampaignDocument
    {
        public string DocumentId { get; set; }
        public string CampaignName { get; set; }
        public string GuildChannelKey { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public CampaignGenerationTrigger InitialGeneration { get; set; }
        public List<CampaignPromptTweak> PromptTweaks { get; set; } = new();
        public List<CampaignRevision> Revisions { get; set; } = new();
        public string CurrentRevisionId { get; set; }
        public string CurrentContent { get; set; }
    }

    private sealed class CampaignGenerationTrigger
    {
        public string TriggerKind { get; set; }
        public ulong TriggeredByUserId { get; set; }
        public string TriggeredByUsername { get; set; }
        public DateTime TriggeredUtc { get; set; }
        public string TriggerInput { get; set; }
        public string ResultingRevisionId { get; set; }
    }

    private sealed class CampaignPromptTweak
    {
        public string TweakId { get; set; }
        public string Kind { get; set; }
        public string PromptOrInteraction { get; set; }
        public ulong TriggeredByUserId { get; set; }
        public string TriggeredByUsername { get; set; }
        public DateTime TriggeredUtc { get; set; }
        public string ResultingRevisionId { get; set; }
    }

    private sealed class CampaignRevision
    {
        public string RevisionId { get; set; }
        public string PreviousRevisionId { get; set; }
        public DateTime AppliedUtc { get; set; }
        public string TriggerKind { get; set; }
        public ulong TriggeredByUserId { get; set; }
        public string TriggeredByUsername { get; set; }
        public string TriggerInput { get; set; }
        public string CampaignContent { get; set; }
    }

    private sealed class CampaignLedgerDocument
    {
        public string LedgerId { get; set; }
        public string CampaignDocumentId { get; set; }
        public string CampaignName { get; set; }
        public string GuildChannelKey { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<CampaignLedgerEntry> Entries { get; set; } = new();
    }

    private sealed class CampaignLedgerEntry
    {
        public string EntryId { get; set; }
        public DateTime OccurredUtc { get; set; }
        public ulong ActorUserId { get; set; }
        public string ActorUsername { get; set; }
        public string ActionType { get; set; }
        public string ActionText { get; set; }
        public string Outcome { get; set; }
        public string CampaignRevisionId { get; set; }
    }

    private sealed class CharacterSheetDocument
    {
        public string CampaignDocumentId { get; set; }
        public string CampaignName { get; set; }
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public string Concept { get; set; }
        public string Sheet { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class NpcSheetDocument
    {
        public string CampaignDocumentId { get; set; }
        public string CampaignName { get; set; }
        public string Name { get; set; }
        public string Concept { get; set; }
        public string Profile { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class NpcConversationDecision
    {
        public bool Respond { get; set; }
        public string NpcName { get; set; }
        public string Message { get; set; }
    }

    private sealed class PendingOverwriteState
    {
        public string CampaignName { get; set; }
        public ulong RequestedByUserId { get; set; }
        public string ProposedContent { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class SessionState
    {
        public Dictionary<ulong, int> Initiative { get; set; } = new();
        public List<ulong> TurnOrder { get; set; } = new();
        public int CurrentTurnIndex { get; set; }
        public List<string> Transcript { get; set; } = new();
    }
}
