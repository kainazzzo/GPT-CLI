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
using OpenAI.ObjectModels.SharedModels;

namespace DndModuleExample;

public sealed class DndGameMasterModule : FeatureModuleBase
{
    public override string Id => "dnd";
    public override string Name => "D&D Live GM";

    private const string ModeOff = "off";
    private const string ModePrep = "prep";
    private const string ModeLive = "live";
    private const string CombatPhaseNone = "none";
    private const string CombatPhaseInitiative = "initiative";
    private const string CombatPhasePlayers = "players";
    private const string CombatPhaseEnemies = "enemies";
    private const int MaxCampaignChars = 24000;
    private const int MaxAttachmentBytes = 250000;
    private const int MaxTranscriptLines = 24;
    private const int PendingOverwriteMinutes = 15;
    private const int PendingHitMinutes = 5;
    private const int DiscordMessageLimit = 1800;

    private static readonly Regex DiceExpressionRegex =
        new(@"^(?<count>\d{0,2})d(?<sides>\d{1,4})(?<modifier>[+-]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BotMentionRegexTemplate =
        new(@"<@!?(?<id>\d+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = new();

    private readonly ConcurrentDictionary<ulong, DndChannelState> _stateByChannel = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _joinPromptCooldownUntilUtc = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

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
            },

            // Deterministic encounter engine (JSON-persisted)
            new()
            {
                ToolName = "gptcli_dnd_encountercreate",
                ModuleId = Id,
                Description = "Create a deterministic encounter (JSON) from a prompt",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encountercreate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Encounter name", Required: true),
                    new GptCliParamSpec("prompt", GptCliParamType.String, "Encounter prompt (SRD-compatible)", Required: true)
                },
                ExecuteAsync = ExecuteEncounterCreateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_encounterlist",
                ModuleId = Id,
                Description = "List encounters for the active campaign",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterlist"),
                ExecuteAsync = ExecuteEncounterListAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_encounterstart",
                ModuleId = Id,
                Description = "Start an encounter for live combat",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterstart"),
                Parameters = new[]
                {
                    new GptCliParamSpec("id", GptCliParamType.String, "Encounter id (from encounterlist)", Required: true)
                },
                ExecuteAsync = ExecuteEncounterStartAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_encounterstatus",
                ModuleId = Id,
                Description = "Show current encounter status",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterstatus"),
                ExecuteAsync = ExecuteEncounterStatusAsync
            },
	            new()
	            {
	                ToolName = "gptcli_dnd_encounterend",
	                ModuleId = Id,
	                Description = "End the current encounter",
	                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterend"),
	                ExecuteAsync = ExecuteEncounterEndAsync
	            },
	            new()
	            {
	                ToolName = "gptcli_dnd_enemyset",
	                ModuleId = Id,
	                Description = "Update enemy combat stats in an encounter",
	                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "enemyset"),
	                Parameters = new[]
	                {
	                    new GptCliParamSpec("enemy", GptCliParamType.String, "Enemy name or id", Required: true),
	                    new GptCliParamSpec("encounter", GptCliParamType.String, "Encounter id (optional; default active encounter)"),
	                    new GptCliParamSpec("ac", GptCliParamType.Integer, "Armor Class", MinInt: 1, MaxInt: 30),
	                    new GptCliParamSpec("maxhp", GptCliParamType.Integer, "Max HP", MinInt: 1, MaxInt: 999),
	                    new GptCliParamSpec("hp", GptCliParamType.Integer, "Current HP", MinInt: 0, MaxInt: 999),
	                    new GptCliParamSpec("init", GptCliParamType.Integer, "Initiative bonus", MinInt: -5, MaxInt: 20),
	                    new GptCliParamSpec("tohit", GptCliParamType.Integer, "To-hit bonus", MinInt: -5, MaxInt: 30),
	                    new GptCliParamSpec("damage", GptCliParamType.String, "Damage dice (e.g. 1d6+2)"),
	                    new GptCliParamSpec("attack", GptCliParamType.String, "Attack name (e.g. Claw)")
	                },
	                ExecuteAsync = ExecuteEnemySetAsync
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
                        .WithType(ApplicationCommandOptionType.Integer)),

                new SlashCommandOptionBuilder().WithName("encountercreate")
                    .WithDescription("Create a deterministic encounter (JSON) from a prompt")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("name")
                        .WithDescription("Encounter name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("prompt")
                        .WithDescription("Encounter prompt (SRD-compatible)")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("encounterlist")
                    .WithDescription("List encounters for the active campaign")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("encounterstart")
                    .WithDescription("Start an encounter for live combat")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("id")
                        .WithDescription("Encounter id (from encounterlist)")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),
                new SlashCommandOptionBuilder().WithName("encounterstatus")
                    .WithDescription("Show current encounter status")
                    .WithType(ApplicationCommandOptionType.SubCommand),
                new SlashCommandOptionBuilder().WithName("encounterend")
                    .WithDescription("End the current encounter")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                ,
                new SlashCommandOptionBuilder().WithName("enemyset")
                    .WithDescription("Update enemy combat stats in an encounter")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(new SlashCommandOptionBuilder().WithName("enemy")
                        .WithDescription("Enemy name or id")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("encounter")
                        .WithDescription("Encounter id (optional; default active encounter)")
                        .WithType(ApplicationCommandOptionType.String))
                    .AddOption(new SlashCommandOptionBuilder().WithName("ac")
                        .WithDescription("Armor Class")
                        .WithType(ApplicationCommandOptionType.Integer))
                    .AddOption(new SlashCommandOptionBuilder().WithName("maxhp")
                        .WithDescription("Max HP")
                        .WithType(ApplicationCommandOptionType.Integer))
                    .AddOption(new SlashCommandOptionBuilder().WithName("hp")
                        .WithDescription("Current HP")
                        .WithType(ApplicationCommandOptionType.Integer))
                    .AddOption(new SlashCommandOptionBuilder().WithName("init")
                        .WithDescription("Initiative bonus")
                        .WithType(ApplicationCommandOptionType.Integer))
                    .AddOption(new SlashCommandOptionBuilder().WithName("tohit")
                        .WithDescription("To-hit bonus")
                        .WithType(ApplicationCommandOptionType.Integer))
                    .AddOption(new SlashCommandOptionBuilder().WithName("damage")
                        .WithDescription("Damage dice (e.g. 1d6+2)")
                        .WithType(ApplicationCommandOptionType.String))
                    .AddOption(new SlashCommandOptionBuilder().WithName("attack")
                        .WithDescription("Attack name (e.g. Claw)")
                        .WithType(ApplicationCommandOptionType.String))
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
            using var typing = DiscordTyping.Begin(ctx.Channel);

            // Single command bootstrap: generate campaign + deterministic encounters in one shot.
            var package = await GenerateCampaignPackageAsync(ctx.Context, ctx.ChannelState, campaignName.Trim(), prompt.Trim(), ct);
            if (package == null || string.IsNullOrWhiteSpace(package.CampaignMarkdown))
            {
                // Fallback to legacy markdown-only generation if the package schema fails.
                var generated = await GenerateCampaignAsync(ctx.Context, ctx.ChannelState, campaignName.Trim(), prompt.Trim(), null, ct);
                if (string.IsNullOrWhiteSpace(generated))
                {
                    return new GptCliExecutionResult(true, "Campaign generation failed.", false);
                }

                package = new CampaignPackageResponse
                {
                    CampaignMarkdown = generated.Trim(),
                    Encounters = new List<EncounterDocument>()
                };
            }

            var campaign = GetOrCreateCampaign(dndState, campaignName);
            campaign.Content = TrimToLimit(package.CampaignMarkdown.Trim(), MaxCampaignChars);
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

            var createdEncounters = new List<string>();
            if (package.Encounters is { Count: > 0 })
            {
                foreach (var encounter in package.Encounters.Where(e => e != null))
                {
                    encounter.Name = string.IsNullOrWhiteSpace(encounter.Name) ? "Encounter" : encounter.Name.Trim();
                    encounter.Status = string.IsNullOrWhiteSpace(encounter.Status) ? "prepared" : encounter.Status.Trim();
                    await SaveEncounterAsync(ctx.ChannelState, campaign.Name, encounter, ct);
                    createdEncounters.Add($"{encounter.EncounterId} | {encounter.Name} | enemies={encounter.Enemies?.Count ?? 0}");
                }
            }

            await SaveStateAsync(ctx.ChannelState, dndState, ct);
            var encounterSummary = createdEncounters.Count == 0
                ? "\n\nEncounters: none generated. Use `/gptcli dnd encountercreate`."
                : "\n\nEncounters generated:\n" + string.Join("\n", createdEncounters.Take(12).Select(s => $"- {s}"));
            return new GptCliExecutionResult(true,
                $"Campaign \"{campaign.Name}\" created.\n\n{TrimToLimit(campaign.Content, 2200)}{encounterSummary}",
                true);
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
            using var typing = DiscordTyping.Begin(ctx.Channel);
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

		            using var typing = DiscordTyping.Begin(ctx.Channel);
		            var sheet = await GenerateCharacterAsync(ctx.Context, ctx.ChannelState, campaign, characterName.Trim(), concept.Trim(), ct);
		            if (string.IsNullOrWhiteSpace(sheet))
		            {
		                return new GptCliExecutionResult(true, "Character generation failed.", false);
		            }

	            TryExtractLastJsonCodeBlock(sheet, out var statsJson, out var sheetWithoutJson);
	            CharacterStats stats = null;
	            if (!string.IsNullOrWhiteSpace(statsJson))
	            {
	                TryParseCharacterStats(statsJson, out stats);
	            }

	            var character = new DndCharacter
	            {
	                Name = characterName.Trim(),
	                Concept = concept.Trim(),
	                Sheet = TrimToLimit(string.IsNullOrWhiteSpace(sheetWithoutJson) ? sheet : sheetWithoutJson, 8000),
	                Stats = stats,
	                UpdatedUtc = DateTime.UtcNow
	            };
		
		            campaign.Characters[ctx.User.Id] = character;
		            dndState.Session.ActiveCharacterByUserId ??= new Dictionary<ulong, string>();
		            dndState.Session.ActiveCharacterByUserId[ctx.User.Id] = character.Name;
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

	            var stats = character.Stats != null
	                ? $"\n\nStats JSON:\n{TrimToLimit(JsonSerializer.Serialize(character.Stats, _jsonOptions), 1200)}"
	                : "";
	            return new GptCliExecutionResult(true, $"Character for <@{targetUserId}> ({campaign.Name})\n\n{TrimToLimit(character.Sheet, 2500)}{stats}", false);
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

		            using var typing = DiscordTyping.Begin(ctx.Channel);
		            var profile = await GenerateNpcAsync(ctx.Context, ctx.ChannelState, campaign, npcName.Trim(), concept.Trim(), ct);
		            if (string.IsNullOrWhiteSpace(profile))
		            {
		                return new GptCliExecutionResult(true, "NPC generation failed.", false);
		            }

	            TryExtractLastJsonCodeBlock(profile, out var statsJson, out var profileWithoutJson);
	            CharacterStats stats = null;
	            if (!string.IsNullOrWhiteSpace(statsJson))
	            {
	                TryParseCharacterStats(statsJson, out stats);
	            }

	            var npc = new DndNpcMember
	            {
	                Name = npcName.Trim(),
	                Concept = concept.Trim(),
	                Profile = TrimToLimit(string.IsNullOrWhiteSpace(profileWithoutJson) ? profile : profileWithoutJson, 5000),
	                Stats = stats,
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

	            var stats = npc.Stats != null
	                ? $"\n\nStats JSON:\n{TrimToLimit(JsonSerializer.Serialize(npc.Stats, _jsonOptions), 1200)}"
	                : "";
	            return new GptCliExecutionResult(true, $"NPC \"{npc.Name}\" ({campaign.Name})\nConcept: {npc.Concept}\n\n{TrimToLimit(npc.Profile, 2500)}{stats}", false);
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

    private async Task<GptCliExecutionResult> ExecuteEncounterCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !TryGetStringArg(argsJson, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `prompt`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);

            using var typing = DiscordTyping.Begin(ctx.Channel);
            var encounter = await GenerateEncounterAsync(ctx.Context, ctx.ChannelState, campaign, name.Trim(), prompt.Trim(), ct);
            if (encounter == null)
            {
                return new GptCliExecutionResult(true, "Encounter generation failed.", false);
            }

            await SaveEncounterAsync(ctx.ChannelState, campaign.Name, encounter, ct);
            return new GptCliExecutionResult(true, BuildEncounterSummary(encounter, includeHp: false), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteEncounterListAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var encounters = await LoadAllEncountersAsync(ctx.ChannelState, campaign.Name, ct);
            if (encounters.Count == 0)
            {
                return new GptCliExecutionResult(true, $"No encounters found for \"{campaign.Name}\".", false);
            }

            var lines = new List<string>
            {
                $"Encounters for \"{campaign.Name}\":"
            };
            foreach (var e in encounters.OrderByDescending(e => e.UpdatedUtc))
            {
                var status = string.IsNullOrWhiteSpace(e.Status) ? "prepared" : e.Status;
                var enemyCount = e.Enemies?.Count ?? 0;
                lines.Add($"- {e.EncounterId} | {e.Name} | {status} | enemies={enemyCount}");
            }

            return new GptCliExecutionResult(true, string.Join("\n", lines), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteEncounterStartAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return new GptCliExecutionResult(true, "Provide `id`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
	        try
	        {
	            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
	            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
	            var encounter = await LoadEncounterAsync(ctx.ChannelState, campaign.Name, id.Trim(), ct);
	            if (encounter == null)
            {
                return new GptCliExecutionResult(true, $"Encounter \"{id}\" not found.", false);
            }

	            encounter.Status = "active";
	            encounter.UpdatedUtc = DateTime.UtcNow;
	            await SaveEncounterAsync(ctx.ChannelState, campaign.Name, encounter, ct);

	            dndState.Session.ActiveEncounterId = encounter.EncounterId;
	            dndState.Session.PendingHitsByUserId?.Clear();
	            dndState.Session.Combat ??= new CombatState();
	            dndState.Session.Combat.EncounterId = encounter.EncounterId;
	            dndState.Session.Combat.Phase = CombatPhaseInitiative;
	            dndState.Session.Combat.RoundNumber = 1;
	            dndState.Session.Combat.PcInitiative?.Clear();
	            dndState.Session.Combat.EnemyInitiative?.Clear();
	            dndState.Session.Combat.PcActedThisRound?.Clear();

	            // Roll enemy initiatives deterministically (dice are still dice, but the logic is coded).
	            encounter.Enemies ??= new List<EncounterEnemy>();
	            foreach (var enemy in encounter.Enemies.Where(e => e != null && e.CurrentHp > 0))
	            {
	                var bonus = enemy.InitiativeBonus;
	                if (!TryEvaluateRoll($"d20{(bonus >= 0 ? "+" : string.Empty)}{bonus}", out var roll, out _))
	                {
	                    roll = new RollOutcome("d20", new[] { 10 }, 0, 10);
	                }
	                dndState.Session.Combat.EnemyInitiative[enemy.EnemyId ?? enemy.Name ?? Guid.NewGuid().ToString("n")[..6]] = roll.Total;
	            }

            await SaveStateAsync(ctx.ChannelState, dndState, ct);

            var partyMissingStats = campaign.Characters
	                .Where(kvp => kvp.Value != null && string.IsNullOrWhiteSpace(kvp.Value.Sheet) == false)
	                .Where(kvp => kvp.Value.Stats == null || kvp.Value.Stats.MaxHp <= 0 || kvp.Value.Stats.ArmorClass <= 0)
	                .Select(kvp => kvp.Key)
	                .ToList();
	            var warn = partyMissingStats.Count > 0
	                ? "\n\n⚠️ Some PCs are missing numeric stats JSON. Combat will still run, but enemy attacks may not resolve correctly for them. Consider rerunning `/gptcli dnd charactercreate`.\nMissing: " +
	                  string.Join(", ", partyMissingStats.Select(id2 => $"<@{id2}>"))
	                : "";

            var initPrompt =
                "\n\nInitiative phase: each player should roll `!initiative` (or `!initiative <bonus>`). " +
                "When everyone has rolled (or `!pass`), the round begins.";

            // Public narration/prompt in the channel (not ephemeral).
            try
            {
                using var typing = DiscordTyping.Begin(ctx.Channel);
                var startText =
                    $"🎲 **Encounter started:** **{encounter.Name}**\n" +
                    $"Win condition: `{encounter.WinCondition?.Type ?? "defeat_all_enemies"}`" +
                    (encounter.WinCondition?.TargetRounds > 0 ? $" (rounds={encounter.WinCondition.TargetRounds})" : "") +
                    "\n\n" +
                    "Roll initiative now: each player `!initiative` (or `!pass`). Admin/mod can `!skip all`.\n\n" +
                    BuildEncounterSummary(encounter, includeHp: true);
                await SendChunkedAsync(ctx.Channel, TrimToLimit(startText, 3500));
            }
            catch
            {
                // Best-effort; don't fail the command if the channel send fails.
            }

            return new GptCliExecutionResult(true,
                $"Active encounter set to `{encounter.EncounterId}` ({encounter.Name}).{warn}{initPrompt}\n\n{BuildEncounterSummary(encounter, includeHp: true)}",
                true);
        }
        finally
        {
	            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteEncounterStatusAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var encounter = await LoadActiveEncounterAsync(ctx.ChannelState, dndState, campaign.Name, ct);
            if (encounter == null)
            {
                return new GptCliExecutionResult(true, "No active encounter. Use `/gptcli dnd encounterstart`.", false);
            }

            return new GptCliExecutionResult(true, BuildEncounterSummary(encounter, includeHp: true), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteEncounterEndAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var encounter = await LoadActiveEncounterAsync(ctx.ChannelState, dndState, campaign.Name, ct);
            if (encounter == null)
            {
                return new GptCliExecutionResult(true, "No active encounter.", false);
            }

            encounter.Status = "completed";
            encounter.UpdatedUtc = DateTime.UtcNow;
            await SaveEncounterAsync(ctx.ChannelState, campaign.Name, encounter, ct);

	            dndState.Session.ActiveEncounterId = null;
	            dndState.Session.PendingHitsByUserId?.Clear();
	            ResetCombatState(dndState);
	            await SaveStateAsync(ctx.ChannelState, dndState, ct);

            return new GptCliExecutionResult(true, $"Encounter `{encounter.EncounterId}` ended.", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteEnemySetAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "enemy", out var enemyKey) || string.IsNullOrWhiteSpace(enemyKey))
        {
            return new GptCliExecutionResult(true, "Provide `enemy`.", false);
        }

        TryGetStringArg(argsJson, "encounter", out var encounterId);

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var dndState = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);

            EncounterDocument encounter = null;
            if (!string.IsNullOrWhiteSpace(encounterId))
            {
                encounter = await LoadEncounterAsync(ctx.ChannelState, campaign.Name, encounterId.Trim(), ct);
            }
            else
            {
                encounter = await LoadActiveEncounterAsync(ctx.ChannelState, dndState, campaign.Name, ct);
            }

            if (encounter == null)
            {
                return new GptCliExecutionResult(true, "No active encounter (or encounter not found).", false);
            }

            if (!TryResolveEncounterEnemy(encounter, enemyKey.Trim(), out var enemy, out var resolveError))
            {
                return new GptCliExecutionResult(true, resolveError, false);
            }

            if (TryGetIntArg(argsJson, "ac", out var ac))
            {
                enemy.ArmorClass = Clamp(ac, 1, 30);
            }
            if (TryGetIntArg(argsJson, "maxhp", out var maxHp))
            {
                enemy.MaxHp = Clamp(maxHp, 1, 999);
                if (enemy.CurrentHp > enemy.MaxHp)
                {
                    enemy.CurrentHp = enemy.MaxHp;
                }
            }
            if (TryGetIntArg(argsJson, "hp", out var hp))
            {
                enemy.CurrentHp = Clamp(hp, 0, Math.Max(1, enemy.MaxHp));
            }
            if (TryGetIntArg(argsJson, "init", out var init))
            {
                enemy.InitiativeBonus = Clamp(init, -5, 20);
            }
            if (TryGetIntArg(argsJson, "tohit", out var toHit))
            {
                enemy.ToHitBonus = Clamp(toHit, -5, 30);
            }
            if (TryGetStringArg(argsJson, "damage", out var damage) && !string.IsNullOrWhiteSpace(damage))
            {
                enemy.Damage = damage.Trim();
            }
            if (TryGetStringArg(argsJson, "attack", out var attack) && !string.IsNullOrWhiteSpace(attack))
            {
                enemy.AttackName = attack.Trim();
            }

            encounter.UpdatedUtc = DateTime.UtcNow;
            await SaveEncounterAsync(ctx.ChannelState, campaign.Name, encounter, ct);
            return new GptCliExecutionResult(true,
                $"Updated enemy **{enemy.Name}** in encounter `{encounter.EncounterId}`.\n" +
                $"- AC {enemy.ArmorClass}, HP {enemy.CurrentHp}/{enemy.MaxHp}, init {enemy.InitiativeBonus}, toHit {enemy.ToHitBonus}, dmg `{enemy.Damage}`, atk `{enemy.AttackName}`",
                false);
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
			            var isTagged = message.MentionedUsers.Any(user => user.Id == context.Client.CurrentUser.Id);

			            // Allow players to explicitly opt out of DM consideration for out-of-band chatter.
			            // This is intentionally simple and only applies when the tag is present in the message.
			            if (content.Contains("!ignore", StringComparison.OrdinalIgnoreCase))
			            {
			                return;
			            }

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

			            // In live mode we only run the DM loop when an encounter is active; otherwise the channel can devolve into
			            // repeated "what do you do?" prompting without deterministic state to advance.
			            var hasActiveEncounter = !string.IsNullOrWhiteSpace(dndState?.Session?.ActiveEncounterId);

		            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
		            if (!IsPartyMember(campaign, message.Author.Id))
		            {
		                if ((hasActiveEncounter || isTagged || content.StartsWith("!", StringComparison.Ordinal)) &&
		                    ShouldPromptJoin(message.Channel.Id, message.Author.Id, DateTime.UtcNow))
		                {
		                    await message.Channel.SendMessageAsync(
		                        $"<@{message.Author.Id}> you’re not in the party yet. To join, run `/gptcli dnd charactercreate` (name + concept). " +
		                        "After that, just chat normally in-character.");
		                }

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

			            if (hasActiveEncounter)
			            {
			                var naturalResult = await TryHandleNaturalCombatActionAsync(context, channelState, message, dndState, cancellationToken);
			                if (naturalResult.Handled)
			                {
			                    await PersistIfNeededAsync(context, channelState, dndState, naturalResult, cancellationToken);
			                    return;
			                }
			            }

			            if (!hasActiveEncounter)
			            {
			                if (isTagged)
			                {
			                    await message.Channel.SendMessageAsync(
			                        "No active encounter right now. Start one with `/gptcli dnd encounterlist` then `/gptcli dnd encounterstart id:<id>`. " +
			                        "During encounters, use `!initiative`, `!attack`, `!damage`, and `!pass` for official mechanics.");
			                }

			                return;
			            }

	            if (string.IsNullOrWhiteSpace(content))
	            {
	                return;
	            }

	            TryApplyIdentitySelectionFromMessage(dndState, campaign, message.Author.Id, content, out var activeCharacterName);

			            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, activeCharacterName)}: {content}");
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

	    private async Task<HandleResult> TryHandleNaturalCombatActionAsync(
	        DiscordModuleContext context,
	        InstructionGPT.ChannelState channelState,
	        SocketMessage message,
	        DndChannelState dndState,
	        CancellationToken cancellationToken)
	    {
	        if (context == null || channelState == null || message == null || dndState == null)
	        {
	            return HandleResult.NotHandled;
	        }

	        var combat = dndState.Session?.Combat;
	        if (combat == null || string.IsNullOrWhiteSpace(combat.EncounterId))
	        {
	            return HandleResult.NotHandled;
	        }

	        // Ignore explicit mechanics tags and obvious non-actions.
	        var content = (message.Content ?? string.Empty).Trim();
	        if (string.IsNullOrWhiteSpace(content) || content.StartsWith("!", StringComparison.Ordinal))
	        {
	            return HandleResult.NotHandled;
	        }

	        var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
	        var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
	        if (encounter == null)
	        {
	            return HandleResult.NotHandled;
	        }

	        // Initiative phase: allow natural language "roll initiative" to resolve as !initiative automatically.
	        if (string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase))
	        {
	            if (Regex.IsMatch(content, @"\binit(itiative)?\b", RegexOptions.IgnoreCase))
	            {
	                return await HandleInitiativeAsync(channelState, message, dndState, campaign, explicitBonus: null,
	                    transcriptText: content, cancellationToken);
	            }

	            return HandleResult.NotHandled;
	        }

	        // Player phase: only convert natural language to actions when it's this player's turn.
	        if (!string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase))
	        {
	            return HandleResult.NotHandled;
	        }

	        if (!CanPlayerActInCombat(dndState, message.Author.Id, content, out _))
	        {
	            return HandleResult.NotHandled;
	        }

	        var decision = await RouteNaturalCombatActionAsync(context, channelState, message, dndState, campaign, encounter, cancellationToken);
	        if (decision == null || string.IsNullOrWhiteSpace(decision.ToolName))
	        {
	            return HandleResult.NotHandled;
	        }

	        if (string.Equals(decision.ToolName, "dnd_pass", StringComparison.OrdinalIgnoreCase))
	        {
	            MarkPcActed(dndState, message.Author.Id);
	            await message.Channel.SendMessageAsync($"⏭️ <@{message.Author.Id}> passes.");
	            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
	            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "pass", content, "pass", cancellationToken);
	            await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	            return new HandleResult(true, true, false);
	        }

	        if (string.Equals(decision.ToolName, "dnd_attack", StringComparison.OrdinalIgnoreCase))
	        {
	            if (!TryGetStringArg(decision.ArgumentsJson, "target", out var target) || string.IsNullOrWhiteSpace(target))
	            {
	                return HandleResult.NotHandled;
	            }
	            TryGetStringArg(decision.ArgumentsJson, "attack", out var attackName);

	            return await HandleAutoAttackAsync(
	                channelState,
	                message,
	                dndState,
	                campaign,
	                encounter,
	                target.Trim(),
	                string.IsNullOrWhiteSpace(attackName) ? null : attackName.Trim(),
	                transcriptText: content,
	                cancellationToken);
	        }

	        return HandleResult.NotHandled;
	    }

	    private sealed class NaturalCombatDecision
	    {
	        public string ToolName { get; set; }
	        public string ArgumentsJson { get; set; }
	    }

	    private async Task<NaturalCombatDecision> RouteNaturalCombatActionAsync(
	        DiscordModuleContext context,
	        InstructionGPT.ChannelState channelState,
	        SocketMessage message,
	        DndChannelState dndState,
	        DndCampaign campaign,
	        EncounterDocument encounter,
	        CancellationToken cancellationToken)
	    {
	        try
	        {
	            var alive = (encounter.Enemies ?? new List<EncounterEnemy>())
	                .Where(e => e != null && e.CurrentHp > 0)
	                .Select(e => $"{e.EnemyId}:{e.Name} (AC {e.ArmorClass}, HP {e.CurrentHp}/{e.MaxHp})")
	                .ToList();

	            var characterName = ResolveActiveCharacterName(dndState, campaign, message.Author.Id);
	            var system =
	                "You are a deterministic D&D combat action router.\n" +
	                "If (and only if) the player message is attempting an in-combat action on their turn, call exactly one tool.\n" +
	                "If the message is roleplay, table talk, or a question, do not call any tools.\n" +
	                "Never invent outcomes; mechanics are handled by code.\n" +
	                "When calling dnd_attack, target must match a living enemy id or name from the provided list.\n" +
	                "If uncertain about the target, do not call tools.";

	            var user = new StringBuilder();
	            user.AppendLine($"Phase: {dndState.Session?.Combat?.Phase}");
	            user.AppendLine($"Player: {FormatPlayerLabel(message.Author.Username, characterName)}");
	            user.AppendLine("Living enemies:");
	            user.AppendLine(alive.Count == 0 ? "- (none)" : string.Join("\n", alive.Select(s => $"- {s}")));
	            user.AppendLine("Message:");
	            user.AppendLine(message.Content ?? string.Empty);

	            var tools = BuildNaturalCombatTools();
	            var request = new ChatCompletionCreateRequest
	            {
	                Model = ResolveModel(context, channelState),
	                Temperature = 0,
	                MaxTokens = null,
	                MaxCompletionTokens = 140,
	                ParallelToolCalls = false,
	                Messages = new List<ChatMessage>
	                {
	                    new(StaticValues.ChatMessageRoles.System, system),
	                    new(StaticValues.ChatMessageRoles.User, user.ToString())
	                },
	                Tools = tools,
	                ToolChoice = new ToolChoice { Type = "auto" }
	            };

	            using var typing = DiscordTyping.Begin(message.Channel);
	            var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
	            if (!response.Successful)
	            {
	                await Console.Out.WriteLineAsync($"DND natural action tools failed: {response.Error?.Code} {response.Error?.Message}");
	                return null;
	            }

	            var msg = response.Choices.FirstOrDefault()?.Message;
	            if (msg == null)
	            {
	                return null;
	            }

	            var toolCalls = msg.ToolCalls;
	            if (toolCalls == null || toolCalls.Count == 0)
	            {
	                if (msg.FunctionCall == null)
	                {
	                    return null;
	                }
	                toolCalls = new List<ToolCall> { new() { Type = "function", FunctionCall = msg.FunctionCall } };
	            }

	            var first = toolCalls.FirstOrDefault()?.FunctionCall;
	            if (first == null || string.IsNullOrWhiteSpace(first.Name))
	            {
	                return null;
	            }

	            return new NaturalCombatDecision
	            {
	                ToolName = first.Name.Trim(),
	                ArgumentsJson = string.IsNullOrWhiteSpace(first.Arguments) ? "{}" : first.Arguments
	            };
	        }
	        catch (Exception ex)
	        {
	            await Console.Out.WriteLineAsync($"DND natural action routing failed: {ex.GetType().Name} {ex.Message}");
	            return null;
	        }
	    }

	    private static List<ToolDefinition> BuildNaturalCombatTools()
	    {
	        static ToolDefinition Fn(string name, string description, Dictionary<string, PropertyDefinition> props, List<string> required)
	        {
	            return new ToolDefinition
	            {
	                Type = "function",
	                Function = new FunctionDefinition
	                {
	                    Name = name,
	                    Description = description,
	                    Strict = false,
	                    Parameters = new PropertyDefinition
	                    {
	                        Type = "object",
	                        AdditionalProperties = false,
	                        Properties = props ?? new Dictionary<string, PropertyDefinition>(),
	                        Required = required ?? new List<string>()
	                    }
	                }
	            };
	        }

	        return new List<ToolDefinition>
	        {
	            Fn(
	                "dnd_attack",
	                "Make an attack against a specific living enemy on the player's turn. Uses deterministic rules engine to roll and apply HP.",
	                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
	                {
	                    ["target"] = new PropertyDefinition { Type = "string", Description = "Enemy id or enemy name" },
	                    ["attack"] = new PropertyDefinition { Type = "string", Description = "Optional attack name (if the sheet has multiple attacks)" }
	                },
	                new List<string> { "target" }),
	            Fn(
	                "dnd_pass",
	                "Do nothing on your turn (skip your action).",
	                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase),
	                new List<string>())
	        };
	    }

    private static bool IsPartyMember(DndCampaign campaign, ulong userId)
    {
        if (campaign?.Characters == null)
        {
            return false;
        }

        if (!campaign.Characters.TryGetValue(userId, out var character) || character == null)
        {
            return false;
        }

        // Treat blank sheets as "not joined" so the GM doesn't run combat for unknown stats.
        return !string.IsNullOrWhiteSpace(character.Sheet);
    }

    private bool ShouldPromptJoin(ulong channelId, ulong userId, DateTime utcNow)
    {
        const int cooldownMinutes = 10;
        var key = $"{channelId}:{userId}";
        if (_joinPromptCooldownUntilUtc.TryGetValue(key, out var untilUtc) && untilUtc > utcNow)
        {
            return false;
        }

        _joinPromptCooldownUntilUtc[key] = utcNow.AddMinutes(cooldownMinutes);
        return true;
    }

    public override async Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(
        DiscordModuleContext context,
        SocketMessage message,
        InstructionGPT.ChannelState channel,
        CancellationToken cancellationToken)
    {
        if (context == null || message == null || channel == null)
        {
            return Array.Empty<ChatMessage>();
        }

        if (!InstructionGPT.IsModuleEnabled(channel, Id))
        {
            return Array.Empty<ChatMessage>();
        }

        var isTagged = message.MentionedUsers.Any(user => user.Id == context.Client.CurrentUser.Id);
        var isDirectMessage = message.Channel is IPrivateChannel;
        if (!isTagged && !isDirectMessage)
        {
            return Array.Empty<ChatMessage>();
        }

        var lockHandle = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(cancellationToken);
        try
        {
            var dndState = await GetOrLoadStateAsync(channel, cancellationToken);
            if (dndState == null)
            {
                return Array.Empty<ChatMessage>();
            }

            // Only bias normal-chat responses when the module is actively in prep/live mode.
            if (!string.Equals(dndState.Mode, ModePrep, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dndState.Mode, ModeLive, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<ChatMessage>();
            }

	            var campaign = FindCampaign(dndState, dndState.ActiveCampaignName);
	            var campaignName = campaign?.Name ?? dndState.ActiveCampaignName ?? "default";
	            var activeCharacterName = ResolveActiveCharacterName(dndState, campaign, message.Author.Id);
	            var activeCharacterSheet = TryGetCharacterSheet(campaign, message.Author.Id);
	            var activeCharacterStats = (campaign?.Characters != null && campaign.Characters.TryGetValue(message.Author.Id, out var activeChar))
	                ? activeChar?.Stats
	                : null;

            CampaignDocument campaignDoc = null;
            if (campaign != null && !string.IsNullOrWhiteSpace(campaign.Name))
            {
                // Avoid creating files on read unless we already have something to persist.
                campaignDoc = await LoadCampaignDocumentAsync(channel, campaign.Name, cancellationToken);
                if (campaignDoc == null && !string.IsNullOrWhiteSpace(campaign.Content))
                {
                    campaignDoc = await EnsureCampaignDocumentAsync(channel, campaign, cancellationToken);
                }
            }

            var characterCount = campaign?.Characters?.Count ?? 0;
            var npcCount = campaign?.Npcs?.Count ?? 0;
            var hasCampaignContent = !string.IsNullOrWhiteSpace(campaign?.Content) ||
                                    !string.IsNullOrWhiteSpace(campaignDoc?.CurrentContent);

            var content = campaignDoc?.CurrentContent ?? campaign?.Content ?? string.Empty;
            content = TrimToLimit(content, 1400);

            var sb = new StringBuilder();
            sb.AppendLine("D&D module is enabled for this channel.");
            sb.AppendLine($"Mode: {dndState.Mode}");
            sb.AppendLine($"Active campaign: {campaignName}");
            if (!string.IsNullOrWhiteSpace(activeCharacterName))
            {
                sb.AppendLine($"Active speaker: {message.Author.Username} as \"{activeCharacterName}\"");
            }
            else
            {
                sb.AppendLine($"Active speaker: {message.Author.Username} (no character selected)");
            }
            sb.AppendLine($"Campaign ready: {(hasCampaignContent ? "yes" : "no")}");
            sb.AppendLine($"Party: {characterCount} PC(s), {npcCount} NPC companion(s)");
            if (dndState.PendingOverwrite != null)
            {
                sb.AppendLine("Pending overwrite: yes (awaiting confirmation)");
            }

            var playerRoster = BuildPlayerRoster(campaign, dndState);
            var npcRoster = BuildNpcRoster(campaign, Array.Empty<string>());
            sb.AppendLine();
            sb.AppendLine("PC roster:");
            sb.AppendLine(playerRoster);
            sb.AppendLine();
            sb.AppendLine("NPC companion roster:");
            sb.AppendLine(npcRoster);

            var encounter = await LoadActiveEncounterAsync(channel, dndState, campaignName, cancellationToken);
            if (encounter != null)
            {
                sb.AppendLine();
                sb.AppendLine("Active encounter (deterministic):");
                sb.AppendLine(BuildEncounterSummary(encounter, includeHp: true));
            }

	            if (!string.IsNullOrWhiteSpace(activeCharacterSheet))
	            {
	                sb.AppendLine();
	                sb.AppendLine("Active speaker character sheet (excerpt):");
	                sb.AppendLine(TrimToLimit(activeCharacterSheet, 1400));
	            }
	            if (activeCharacterStats != null)
	            {
	                sb.AppendLine();
	                sb.AppendLine("Active speaker numeric stats (JSON):");
	                sb.AppendLine(TrimToLimit(JsonSerializer.Serialize(activeCharacterStats, _jsonOptions), 1200));
	            }

            if (string.Equals(dndState.Mode, ModePrep, StringComparison.OrdinalIgnoreCase))
            {
                var partyExcerpts = BuildPartySheetExcerpts(campaign, maxChars: 2600);
                if (!string.IsNullOrWhiteSpace(partyExcerpts))
                {
                    sb.AppendLine();
                    sb.AppendLine("Party sheets/profiles (condensed):");
                    sb.AppendLine(partyExcerpts);
                }
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine();
                sb.AppendLine("Current campaign content (excerpt):");
                sb.AppendLine(content);
            }

            sb.AppendLine();
            sb.AppendLine("When the user tags the bot:");
            sb.AppendLine("- In `prep` mode: act like a helpful GM assistant. Do not refuse to help. Propose next setup steps (campaign prompt, party, NPC companions). If the user asks for a good campaign prompt, produce one.");
            sb.AppendLine("- In `live` mode: act like the GM in-world and move the scene forward, but keep actions requiring dice as suggestions until the user triggers official rolls via the module's !commands.");
            sb.AppendLine("- This table uses a deterministic rules engine: never finalize hit/miss/damage/HP changes from freeform chat; require official !commands and/or use tool calls for setup.");
            sb.AppendLine("- If the active speaker has a character name, address them by that character name and stay in-world. If they do not have a character yet, ask them to create one and offer a quick default.");
            sb.AppendLine("You may suggest `/gptcli dnd ...` commands or mention-based natural language tool calls as optional shortcuts, but always answer the user's request directly first.");

            return new[] { new ChatMessage(StaticValues.ChatMessageRoles.System, sb.ToString().Trim()) };
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"DND additional context failed: {ex.GetType().Name} {ex.Message}");
            return Array.Empty<ChatMessage>();
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
	            Stats = character.Stats,
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
	            Stats = npc.Stats,
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
	        var doc = await ReadJsonFileAsync<CampaignDocument>(path, cancellationToken);
	        if (doc != null)
	        {
	            return doc;
	        }

	        // Back-compat: earlier attempt stored under <channelDir>/modules/dnd/...
	        var interimDir = ResolveInterimCampaignDirectory(channelState, campaignName);
	        var interimPath = Path.Combine(interimDir, "campaign.json");
	        doc = await ReadJsonFileAsync<CampaignDocument>(interimPath, cancellationToken);
	        if (doc != null)
	        {
	            TryCopyDirectoryContents(interimDir, ResolveCampaignDirectory(channelState, campaignName));
	            await WriteJsonFileAsync(path, doc, cancellationToken);
	            return doc;
	        }

	        // Back-compat: old location under channels/dnd/...
	        var legacyDir = ResolveLegacyCampaignDirectory(channelState, campaignName);
	        var legacyPath = Path.Combine(legacyDir, "campaign.json");
	        doc = await ReadJsonFileAsync<CampaignDocument>(legacyPath, cancellationToken);
	        if (doc != null)
	        {
	            TryCopyDirectoryContents(legacyDir, ResolveCampaignDirectory(channelState, campaignName));
	            await WriteJsonFileAsync(path, doc, cancellationToken);
	        }

	        return doc;
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
	        var doc = await ReadJsonFileAsync<CampaignLedgerDocument>(path, cancellationToken);
	        if (doc != null)
	        {
	            return doc;
	        }

	        var interimDir = ResolveInterimCampaignDirectory(channelState, campaignName);
	        var interimPath = Path.Combine(interimDir, "ledger.json");
	        doc = await ReadJsonFileAsync<CampaignLedgerDocument>(interimPath, cancellationToken);
	        if (doc != null)
	        {
	            TryCopyDirectoryContents(interimDir, ResolveCampaignDirectory(channelState, campaignName));
	            await WriteJsonFileAsync(path, doc, cancellationToken);
	            return doc;
	        }

	        var legacyDir = ResolveLegacyCampaignDirectory(channelState, campaignName);
	        var legacyPath = Path.Combine(legacyDir, "ledger.json");
	        doc = await ReadJsonFileAsync<CampaignLedgerDocument>(legacyPath, cancellationToken);
	        if (doc != null)
	        {
	            TryCopyDirectoryContents(legacyDir, ResolveCampaignDirectory(channelState, campaignName));
	            await WriteJsonFileAsync(path, doc, cancellationToken);
	        }

	        return doc;
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
	        var campaignPart = SlugifySegment(campaignName);
	        return Path.Combine(GetDndRootDirectory(channelState), "campaigns", campaignPart);
	    }

	    private static string ResolveLegacyCampaignDirectory(InstructionGPT.ChannelState channelState, string campaignName)
	    {
	        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
	        var channelPart = channelState.ChannelId.ToString();
	        var campaignPart = SlugifySegment(campaignName);
	        return Path.Combine("channels", "dnd", "campaigns", $"{guildPart}_{channelPart}", campaignPart);
	    }

	    private static string ResolveInterimCampaignDirectory(InstructionGPT.ChannelState channelState, string campaignName)
	    {
	        // Back-compat: earlier attempt stored under <channelDir>/modules/dnd/...
	        var campaignPart = SlugifySegment(campaignName);
	        return Path.Combine(GetInterimDndRootDirectory(channelState), "campaigns", campaignPart);
	    }

	    private static string GetGuildChannelKey(InstructionGPT.ChannelState channelState)
	    {
	        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
	        return $"{guildPart}_{channelState.ChannelId}";
	    }

	    private static string GetDndRootDirectory(InstructionGPT.ChannelState channelState)
	    {
	        // Keep all D&D files inside the channel folder so state/campaign/docs stay together.
	        var channelDir = InstructionGPT.GetChannelDirectory(channelState);
	        return Path.Combine(channelDir, "dnd");
	    }

		    private static string GetInterimDndRootDirectory(InstructionGPT.ChannelState channelState)
		    {
		        var channelDir = InstructionGPT.GetChannelDirectory(channelState);
		        return Path.Combine(channelDir, "modules", "dnd");
		    }

		    private static string ResolveEncountersDirectory(InstructionGPT.ChannelState channelState, string campaignName)
		    {
		        return Path.Combine(ResolveCampaignDirectory(channelState, campaignName), "encounters");
		    }

		    private static string ResolveEncounterPath(InstructionGPT.ChannelState channelState, string campaignName, string encounterId)
		    {
		        var safeId = SlugifySegment(encounterId);
		        return Path.Combine(ResolveEncountersDirectory(channelState, campaignName), $"{safeId}.json");
		    }

		    private async Task SaveEncounterAsync(
		        InstructionGPT.ChannelState channelState,
		        string campaignName,
		        EncounterDocument encounter,
		        CancellationToken cancellationToken)
		    {
		        if (encounter == null)
		        {
		            return;
		        }

		        encounter.CampaignName = campaignName;
		        encounter.UpdatedUtc = DateTime.UtcNow;
		        if (encounter.CreatedUtc == default)
		        {
		            encounter.CreatedUtc = encounter.UpdatedUtc;
		        }

		        encounter.WinCondition ??= new EncounterWinCondition();
		        encounter.WinCondition.Type = string.IsNullOrWhiteSpace(encounter.WinCondition.Type)
		            ? "defeat_all_enemies"
		            : encounter.WinCondition.Type.Trim().ToLowerInvariant();
		        if (encounter.WinCondition.Type is not ("defeat_all_enemies" or "survive_rounds"))
		        {
		            encounter.WinCondition.Type = "defeat_all_enemies";
		        }
		        if (string.Equals(encounter.WinCondition.Type, "survive_rounds", StringComparison.OrdinalIgnoreCase))
		        {
		            encounter.WinCondition.TargetRounds = Clamp(encounter.WinCondition.TargetRounds, 1, 20);
		        }
		        else
		        {
		            encounter.WinCondition.TargetRounds = 0;
		        }

		        encounter.Enemies ??= new List<EncounterEnemy>();
		        foreach (var enemy in encounter.Enemies)
		        {
		            if (enemy == null)
		            {
		                continue;
		            }

		            enemy.EnemyId = string.IsNullOrWhiteSpace(enemy.EnemyId) ? Guid.NewGuid().ToString("n")[..8] : SlugifySegment(enemy.EnemyId);
		            enemy.Name = string.IsNullOrWhiteSpace(enemy.Name) ? "Enemy" : enemy.Name.Trim();
		            enemy.MaxHp = Math.Max(1, enemy.MaxHp);
		            if (enemy.CurrentHp <= 0 || enemy.CurrentHp > enemy.MaxHp)
		            {
		                enemy.CurrentHp = enemy.MaxHp;
		            }
		            enemy.ArmorClass = Math.Max(1, enemy.ArmorClass);
		            enemy.InitiativeBonus = Clamp(enemy.InitiativeBonus, -2, 8);
		            enemy.ToHitBonus = Clamp(enemy.ToHitBonus, -2, 12);
		            enemy.Damage = string.IsNullOrWhiteSpace(enemy.Damage) ? "1d4" : enemy.Damage.Trim();
		            enemy.AttackName = string.IsNullOrWhiteSpace(enemy.AttackName) ? "Attack" : enemy.AttackName.Trim();
		        }

		        encounter.EncounterId = string.IsNullOrWhiteSpace(encounter.EncounterId)
		            ? $"{SlugifySegment(encounter.Name)}-{Guid.NewGuid().ToString("n")[..8]}"
		            : SlugifySegment(encounter.EncounterId);

		        var path = ResolveEncounterPath(channelState, campaignName, encounter.EncounterId);
		        await WriteJsonFileAsync(path, encounter, cancellationToken);
		    }

		    private async Task<EncounterDocument> LoadEncounterAsync(
		        InstructionGPT.ChannelState channelState,
		        string campaignName,
		        string encounterId,
		        CancellationToken cancellationToken)
		    {
		        if (string.IsNullOrWhiteSpace(encounterId))
		        {
		            return null;
		        }

		        var directPath = ResolveEncounterPath(channelState, campaignName, encounterId);
		        var loaded = await ReadJsonFileAsync<EncounterDocument>(directPath, cancellationToken);
		        if (loaded != null)
		        {
		            loaded.EncounterId ??= SlugifySegment(encounterId);
		            loaded.CampaignName ??= campaignName;
		            loaded.Enemies ??= new List<EncounterEnemy>();
		            return loaded;
		        }

		        // Best-effort: resolve by partial id or encounter name.
		        var dir = ResolveEncountersDirectory(channelState, campaignName);
		        if (!Directory.Exists(dir))
		        {
		            return null;
		        }

		        var wanted = SlugifySegment(encounterId);
		        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
		        {
		            var fileId = SlugifySegment(Path.GetFileNameWithoutExtension(file));
		            if (!fileId.Contains(wanted, StringComparison.OrdinalIgnoreCase))
		            {
		                continue;
		            }

		            loaded = await ReadJsonFileAsync<EncounterDocument>(file, cancellationToken);
		            if (loaded != null)
		            {
		                loaded.EncounterId ??= fileId;
		                loaded.CampaignName ??= campaignName;
		                loaded.Enemies ??= new List<EncounterEnemy>();
		                return loaded;
		            }
		        }

		        return null;
		    }

		    private async Task<List<EncounterDocument>> LoadAllEncountersAsync(
		        InstructionGPT.ChannelState channelState,
		        string campaignName,
		        CancellationToken cancellationToken)
		    {
		        var results = new List<EncounterDocument>();
		        var dir = ResolveEncountersDirectory(channelState, campaignName);
		        if (!Directory.Exists(dir))
		        {
		            return results;
		        }

		        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
		        {
		            var loaded = await ReadJsonFileAsync<EncounterDocument>(file, cancellationToken);
		            if (loaded == null)
		            {
		                continue;
		            }

		            loaded.EncounterId ??= SlugifySegment(Path.GetFileNameWithoutExtension(file));
		            loaded.CampaignName ??= campaignName;
		            loaded.Enemies ??= new List<EncounterEnemy>();
		            results.Add(loaded);
		        }

		        return results;
		    }

		    private async Task<EncounterDocument> LoadActiveEncounterAsync(
		        InstructionGPT.ChannelState channelState,
		        DndChannelState dndState,
		        string campaignName,
		        CancellationToken cancellationToken)
		    {
		        var encounterId = dndState?.Session?.ActiveEncounterId;
		        if (string.IsNullOrWhiteSpace(encounterId))
		        {
		            return null;
		        }

		        var loaded = await LoadEncounterAsync(channelState, campaignName, encounterId, cancellationToken);
		        if (loaded == null)
		        {
		            // Don't keep dangling pointers.
		            dndState.Session.ActiveEncounterId = null;
		        }

		        return loaded;
		    }

		    private static bool TryResolveEncounterEnemy(EncounterDocument encounter, string target, out EncounterEnemy enemy, out string error)
		    {
		        enemy = null;
		        error = null;
		        if (encounter?.Enemies == null || encounter.Enemies.Count == 0)
		        {
		            error = "Encounter has no enemies.";
		            return false;
		        }

		        if (string.IsNullOrWhiteSpace(target))
		        {
		            error = "Provide a target.";
		            return false;
		        }

		        var wanted = target.Trim();
		        var wantedSlug = SlugifySegment(wanted);

		        // Exact id match.
		        enemy = encounter.Enemies.FirstOrDefault(e => e != null && string.Equals(SlugifySegment(e.EnemyId), wantedSlug, StringComparison.OrdinalIgnoreCase));
		        if (enemy != null)
		        {
		            return true;
		        }

		        // Name match (exact/prefix/contains).
		        var matches = encounter.Enemies
		            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
		            .Where(e =>
		            {
		                var name = e.Name.Trim();
		                return string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
		                       name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
		                       name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
		                       SlugifySegment(name).StartsWith(wantedSlug, StringComparison.OrdinalIgnoreCase);
		            })
		            .ToList();

		        if (matches.Count == 1)
		        {
		            enemy = matches[0];
		            return true;
		        }

		        if (matches.Count > 1)
		        {
		            error = "Target is ambiguous. Matches: " + string.Join(", ", matches.Take(6).Select(m => m.Name));
		            return false;
		        }

		        error = $"Target \"{wanted}\" not found. Use `!targets`.";
		        return false;
		    }

			    private static string BuildEncounterSummary(EncounterDocument encounter, bool includeHp)
			    {
		        if (encounter == null)
		        {
		            return "No encounter.";
		        }

		        var enemies = encounter.Enemies ?? new List<EncounterEnemy>();
		        var sb = new StringBuilder();
		        sb.AppendLine($"Encounter: **{encounter.Name}**");
		        sb.AppendLine($"- id: `{encounter.EncounterId}`");
		        sb.AppendLine($"- status: {encounter.Status ?? "prepared"}");
		        if (encounter.WinCondition != null)
		        {
		            var wc = encounter.WinCondition;
		            var details = string.Equals(wc.Type, "survive_rounds", StringComparison.OrdinalIgnoreCase) && wc.TargetRounds > 0
		                ? $"survive_rounds({wc.TargetRounds})"
		                : (wc.Type ?? "defeat_all_enemies");
		            sb.AppendLine($"- win: {details}");
		        }
		        sb.AppendLine($"- enemies: {enemies.Count}");
		        foreach (var e in enemies.Where(e => e != null))
		        {
		            var hp = includeHp ? $" HP {e.CurrentHp}/{e.MaxHp}" : "";
		            var init = e.InitiativeBonus != 0 ? $" init {FormatSigned(e.InitiativeBonus)}" : "";
		            var atk = !string.IsNullOrWhiteSpace(e.AttackName) || e.ToHitBonus != 0 || !string.IsNullOrWhiteSpace(e.Damage)
		                ? $" atk {e.AttackName ?? "Attack"} {FormatSigned(e.ToHitBonus)} dmg {e.Damage ?? "?"}"
		                : "";
		            sb.AppendLine($"  - {e.Name} (AC {e.ArmorClass}){hp}{init}{atk}");
		        }

			        return TrimToLimit(sb.ToString().Trim(), 1700);
			    }

		    private async Task<EncounterDocument> GenerateEncounterAsync(
		        DiscordModuleContext context,
		        InstructionGPT.ChannelState channelState,
		        DndCampaign campaign,
		        string name,
		        string prompt,
		        CancellationToken cancellationToken)
		    {
		        if (context == null)
		        {
		            return null;
		        }

		        var model = ResolveModel(context, channelState);
		        var sb = new StringBuilder();
		        sb.AppendLine("Return JSON only. No markdown. No code fences.");
		        sb.AppendLine("You are generating a deterministic encounter document for a tabletop D&D rules engine (SRD 5.1 style).");
		        sb.AppendLine("Do NOT include copyrighted non-SRD monster text or stat blocks. Prefer original enemies with SRD-like stats.");
		        sb.AppendLine();
		        sb.AppendLine("Schema:");
		        sb.AppendLine("{");
		        sb.AppendLine("  \"name\": \"string\",");
		        sb.AppendLine("  \"status\": \"prepared\",");
		        sb.AppendLine("  \"winCondition\": { \"type\": \"defeat_all_enemies\", \"targetRounds\": 0, \"narrative\": \"\" },");
		        sb.AppendLine("  \"enemies\": [");
		        sb.AppendLine("    {");
		        sb.AppendLine("      \"enemyId\": \"string\",");
		        sb.AppendLine("      \"name\": \"string\",");
		        sb.AppendLine("      \"armorClass\": 10,");
		        sb.AppendLine("      \"maxHp\": 7,");
		        sb.AppendLine("      \"currentHp\": 7,");
		        sb.AppendLine("      \"initiativeBonus\": 1,");
		        sb.AppendLine("      \"attackName\": \"Claw\",");
		        sb.AppendLine("      \"toHitBonus\": 3,");
		        sb.AppendLine("      \"damage\": \"1d6+1\",");
		        sb.AppendLine("      \"notes\": \"string\"");
		        sb.AppendLine("    }");
		        sb.AppendLine("  ]");
		        sb.AppendLine("}");
		        sb.AppendLine();
		        sb.AppendLine($"Campaign: {campaign?.Name}");
		        sb.AppendLine("Campaign summary (excerpt):");
		        sb.AppendLine(TrimToLimit(campaign?.Content, 2000));
		        sb.AppendLine();
		        sb.AppendLine($"Encounter name: {name}");
		        sb.AppendLine("Encounter prompt:");
		        sb.AppendLine(prompt);
		        sb.AppendLine();
		        sb.AppendLine("Constraints:");
		        sb.AppendLine("- 1-8 enemies");
		        sb.AppendLine("- armorClass: 8-22");
		        sb.AppendLine("- maxHp/currentHp: 1-250");
		        sb.AppendLine("- initiativeBonus: -2 to +8");
		        sb.AppendLine("- toHitBonus: -2 to +12");
		        sb.AppendLine("- damage: dice expression like 1d6+1 (no types in the dice string)");
		        sb.AppendLine("- winCondition.type: defeat_all_enemies OR survive_rounds");
		        sb.AppendLine("- winCondition.targetRounds: only for survive_rounds (1-20)");
		        sb.AppendLine("- enemyId: short slug (letters/numbers/-) and unique within the encounter");

		        var request = new ChatCompletionCreateRequest
		        {
		            Model = model,
		            Messages = new List<ChatMessage>
		            {
		                new(StaticValues.ChatMessageRoles.System,
		                    "You output strict JSON for a deterministic encounter. " +
		                    "Output must be a single JSON object with keys exactly: name, status, winCondition, enemies. " +
		                    "No extra keys. No markdown."),
		                new(StaticValues.ChatMessageRoles.User, sb.ToString())
		            }
		        };

		        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
		        if (!response.Successful)
		        {
		            await Console.Out.WriteLineAsync($"DND encounter generation failed: {response.Error?.Code} {response.Error?.Message}");
		            return null;
		        }

		        var json = response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
		        if (string.IsNullOrWhiteSpace(json))
		        {
		            return null;
		        }

		        try
		        {
		            var parsed = JsonSerializer.Deserialize<EncounterDocument>(json, _jsonOptions);
		            if (parsed == null)
		            {
		                return null;
		            }

			            parsed.Name = string.IsNullOrWhiteSpace(parsed.Name) ? name : parsed.Name.Trim();
			            parsed.Status = string.IsNullOrWhiteSpace(parsed.Status) ? "prepared" : parsed.Status.Trim().ToLowerInvariant();
			            parsed.WinCondition ??= new EncounterWinCondition();
			            parsed.WinCondition.Type = string.IsNullOrWhiteSpace(parsed.WinCondition.Type)
			                ? "defeat_all_enemies"
			                : parsed.WinCondition.Type.Trim().ToLowerInvariant();
			            if (parsed.WinCondition.Type is not ("defeat_all_enemies" or "survive_rounds"))
			            {
			                parsed.WinCondition.Type = "defeat_all_enemies";
			            }
			            if (string.Equals(parsed.WinCondition.Type, "survive_rounds", StringComparison.OrdinalIgnoreCase))
			            {
			                parsed.WinCondition.TargetRounds = Clamp(parsed.WinCondition.TargetRounds, 1, 20);
			            }
			            else
			            {
			                parsed.WinCondition.TargetRounds = 0;
			            }
			            parsed.Enemies ??= new List<EncounterEnemy>();
			            if (parsed.Enemies.Count > 8)
			            {
			                parsed.Enemies = parsed.Enemies.Take(8).ToList();
			            }

		            // Assign ids and normalize values before saving.
		            parsed.EncounterId = $"{SlugifySegment(parsed.Name)}-{Guid.NewGuid().ToString("n")[..8]}";
		            parsed.CampaignName = campaign?.Name;
		            parsed.CreatedUtc = DateTime.UtcNow;
		            parsed.UpdatedUtc = parsed.CreatedUtc;
			            foreach (var enemy in parsed.Enemies)
			            {
			                if (enemy == null)
			                {
			                    continue;
			                }

			                enemy.EnemyId = string.IsNullOrWhiteSpace(enemy.EnemyId) ? Guid.NewGuid().ToString("n")[..8] : SlugifySegment(enemy.EnemyId);
			                enemy.Name = string.IsNullOrWhiteSpace(enemy.Name) ? "Enemy" : enemy.Name.Trim();
			                enemy.ArmorClass = Clamp(enemy.ArmorClass, 8, 22);
			                enemy.MaxHp = Clamp(enemy.MaxHp, 1, 250);
			                enemy.CurrentHp = enemy.CurrentHp <= 0 ? enemy.MaxHp : Clamp(enemy.CurrentHp, 0, enemy.MaxHp);
			                enemy.InitiativeBonus = Clamp(enemy.InitiativeBonus, -2, 8);
			                enemy.ToHitBonus = Clamp(enemy.ToHitBonus, -2, 12);
			                enemy.Damage = string.IsNullOrWhiteSpace(enemy.Damage) ? "1d4" : enemy.Damage.Trim();
			                enemy.AttackName = string.IsNullOrWhiteSpace(enemy.AttackName) ? "Attack" : enemy.AttackName.Trim();
			            }

		            // Ensure enemy ids are unique.
		            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		            foreach (var enemy in parsed.Enemies)
		            {
		                if (enemy == null)
		                {
		                    continue;
		                }

		                var baseId = string.IsNullOrWhiteSpace(enemy.EnemyId) ? Guid.NewGuid().ToString("n")[..6] : SlugifySegment(enemy.EnemyId);
		                var id = baseId;
		                var i = 2;
		                while (!seen.Add(id))
		                {
		                    id = $"{baseId}-{i}";
		                    i++;
		                }

		                enemy.EnemyId = id;
		            }

		            return parsed;
		        }
		        catch (Exception ex)
		        {
		            await Console.Out.WriteLineAsync($"DND encounter JSON parse failed: {ex.GetType().Name} {ex.Message}");
		            return null;
		        }
		    }

			    private static bool CanPlayerActInCombat(DndChannelState dndState, ulong userId, string rawCommand, out string error)
			    {
			        error = null;
			        var combat = dndState?.Session?.Combat;
			        if (combat == null || string.IsNullOrWhiteSpace(combat.EncounterId))
			        {
			            return true;
			        }

			        if (!string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase))
			        {
			            error = $"Combat is currently in `{combat.Phase}` phase. Wait for the GM engine to prompt the player phase.";
			            return false;
			        }

		        combat.PcTurnOrder ??= new List<ulong>();
		        if (combat.PcTurnOrder.Count > 0)
		        {
		            var idx = Math.Clamp(combat.CurrentPcTurnIndex, 0, combat.PcTurnOrder.Count - 1);
		            var current = combat.PcTurnOrder[idx];
		            if (current != userId)
		            {
		                error = $"It is currently <@{current}>'s turn.";
		                return false;
		            }
		        }

		        combat.PcActedThisRound ??= new List<ulong>();
		        if (combat.PcActedThisRound.Contains(userId))
		        {
		            error = "You already acted this round. Use `!pass` if you're done, or wait for the next round.";
		            return false;
		        }

		        return true;
		    }

		    private static void MarkPcActed(DndChannelState dndState, ulong userId)
		    {
		        var combat = dndState?.Session?.Combat;
		        if (combat == null)
		        {
		            return;
		        }

		        combat.PcActedThisRound ??= new List<ulong>();
		        if (!combat.PcActedThisRound.Contains(userId))
		        {
		            combat.PcActedThisRound.Add(userId);
		        }
		    }

		    private void RememberPendingHit(
		        DndChannelState dndState,
		        ulong userId,
		        string encounterId,
		        EncounterEnemy enemy,
		        RollOutcome roll,
		        bool hit,
		        bool critical)
		    {
		        if (dndState?.Session == null || enemy == null || roll == null)
		        {
		            return;
		        }

		        dndState.Session.PendingHitsByUserId ??= new Dictionary<ulong, PendingHitState>();
		        dndState.Session.PendingHitsByUserId[userId] = new PendingHitState
		        {
		            EncounterId = encounterId,
		            TargetEnemyId = enemy.EnemyId,
		            TargetName = enemy.Name,
		            Hit = hit,
		            Critical = critical,
		            AttackExpression = roll.Expression,
		            AttackTotal = roll.Total,
		            ExpiresUtc = DateTime.UtcNow.AddMinutes(PendingHitMinutes)
		        };
		    }

		    private static bool TryConsumePendingHit(
		        DndChannelState dndState,
		        ulong userId,
		        string encounterId,
		        string targetEnemyId,
		        out PendingHitState pending,
		        out string error)
		    {
		        pending = null;
		        error = null;
		        if (dndState?.Session?.PendingHitsByUserId == null ||
		            !dndState.Session.PendingHitsByUserId.TryGetValue(userId, out var stored) ||
		            stored == null)
		        {
		            error = "No pending hit recorded. Use `!attack <target> d20+mod` first.";
		            return false;
		        }

		        if (stored.ExpiresUtc != default && DateTime.UtcNow > stored.ExpiresUtc)
		        {
		            dndState.Session.PendingHitsByUserId.Remove(userId);
		            error = "Pending hit expired. Use `!attack` again.";
		            return false;
		        }

		        if (!string.Equals(stored.EncounterId, encounterId, StringComparison.OrdinalIgnoreCase) ||
		            !string.Equals(stored.TargetEnemyId, targetEnemyId, StringComparison.OrdinalIgnoreCase))
		        {
		            error = $"Pending hit does not match this target. Pending was for **{stored.TargetName}**. Use `!attack {stored.TargetName} ...` first.";
		            return false;
		        }

		        if (!stored.Hit)
		        {
		            dndState.Session.PendingHitsByUserId.Remove(userId);
		            error = "Last recorded attack was a miss. Roll `!attack` again.";
		            return false;
		        }

		        pending = stored;
		        dndState.Session.PendingHitsByUserId.Remove(userId);
		        return true;
		    }

			    private async Task TryAdvanceCombatAsync(
			        InstructionGPT.ChannelState channelState,
			        IMessageChannel discordChannel,
			        DndChannelState dndState,
			        CancellationToken cancellationToken)
			    {
		        var combat = dndState?.Session?.Combat;
		        if (combat == null || string.IsNullOrWhiteSpace(combat.EncounterId))
		        {
		            return;
		        }

		        var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
		        var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
		        if (encounter == null)
		        {
		            combat.EncounterId = null;
		            combat.Phase = CombatPhaseNone;
		            return;
		        }

			        if (string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase))
			        {
			            if (!AllPcsHaveInitiative(campaign, combat))
			            {
			                return;
			            }

			            // Establish deterministic PC turn order from initiative.
			            combat.PcTurnOrder ??= new List<ulong>();
			            combat.PcTurnOrder = combat.PcInitiative
			                .OrderByDescending(kvp => kvp.Value)
			                .ThenBy(kvp => kvp.Key)
			                .Select(kvp => kvp.Key)
			                .ToList();
			            combat.CurrentPcTurnIndex = 0;

			            var bestPc = combat.PcInitiative.Values.DefaultIfEmpty(0).Max();
			            var bestEnemy = combat.EnemyInitiative?.Values.DefaultIfEmpty(0).Max() ?? 0;
			            combat.PlayersGoFirst = bestPc >= bestEnemy;
			            combat.Phase = combat.PlayersGoFirst ? CombatPhasePlayers : CombatPhaseEnemies;
			            combat.RoundNumber = combat.RoundNumber <= 0 ? 1 : combat.RoundNumber;
			            combat.PcActedThisRound?.Clear();

		            var who = combat.PlayersGoFirst ? "Players" : "Enemies";
		            await discordChannel.SendMessageAsync($"⚔️ Combat begins (round {combat.RoundNumber}). **{who}** act first.");

			            if (!combat.PlayersGoFirst)
			            {
			                await RunEnemyPhaseAsync(channelState, discordChannel, dndState, campaign, encounter, cancellationToken);
			            }
			            else
			            {
			                await discordChannel.SendMessageAsync(BuildPlayerPhasePrompt(campaign, dndState));
			                if (TryGetCurrentPcTurn(combat, out var firstPc))
			                {
			                    await discordChannel.SendMessageAsync($"➡️ Turn: <@{firstPc}>.");
			                }
			            }

			            return;
			        }

			        if (string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase))
			        {
			            combat.PcTurnOrder ??= new List<ulong>();
			            if (combat.PcTurnOrder.Count == 0)
			            {
			                // Fallback: stable order by user id.
			                combat.PcTurnOrder = campaign.Characters.Keys.OrderBy(id => id).ToList();
			            }

			            combat.PcActedThisRound ??= new List<ulong>();
			            // Advance past PCs who already acted this round.
			            while (combat.CurrentPcTurnIndex < combat.PcTurnOrder.Count &&
			                   combat.PcActedThisRound.Contains(combat.PcTurnOrder[combat.CurrentPcTurnIndex]))
			            {
			                combat.CurrentPcTurnIndex++;
			            }

			            if (combat.CurrentPcTurnIndex >= combat.PcTurnOrder.Count)
			            {
			                // Player phase complete.
			                combat.Phase = CombatPhaseEnemies;
			                await RunEnemyPhaseAsync(channelState, discordChannel, dndState, campaign, encounter, cancellationToken);
			                return;
			            }

			            var currentPc = combat.PcTurnOrder[combat.CurrentPcTurnIndex];
			            await discordChannel.SendMessageAsync($"⏭️ Next turn: <@{currentPc}>.");
			            return;
			        }
			    }

			    private static bool AllPcsHaveInitiative(DndCampaign campaign, CombatState combat)
			    {
			        if (campaign?.Characters == null || campaign.Characters.Count == 0 || combat == null)
			        {
			            return true;
			        }

			        combat.PcInitiative ??= new Dictionary<ulong, int>();
			        foreach (var userId in campaign.Characters
			                     .Where(kvp => kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Sheet))
			                     .Select(kvp => kvp.Key))
			        {
			            if (!combat.PcInitiative.ContainsKey(userId))
			            {
			                return false;
			            }
			        }

			        return true;
			    }

			    private static bool AllPcsActedThisRound(DndCampaign campaign, CombatState combat)
			    {
			        if (campaign?.Characters == null || campaign.Characters.Count == 0 || combat == null)
			        {
			            return true;
			        }

		        combat.PcActedThisRound ??= new List<ulong>();
		        foreach (var userId in campaign.Characters.Keys)
		        {
		            if (!combat.PcActedThisRound.Contains(userId))
		            {
		                return false;
		            }
		        }

		        return true;
		    }

			    private static string BuildPlayerPhasePrompt(DndCampaign campaign, DndChannelState dndState)
			    {
			        var sb = new StringBuilder();
			        sb.AppendLine("🧭 **Player phase**: take turns in initiative order (or `!pass`).");
			        sb.AppendLine("Only the current player may act.");
			        sb.AppendLine("Use: `!attack <target> d20+mod`, then `!damage <target> <dice>` if you hit.");
			        sb.AppendLine("You can also `!encounter` to see targets/HP.");
			        if (campaign?.Characters != null && campaign.Characters.Count > 0)
			        {
			            sb.AppendLine();
			            sb.AppendLine("PCs (turn order):");
			            var combat = dndState?.Session?.Combat;
			            var order = (combat?.PcTurnOrder != null && combat.PcTurnOrder.Count > 0)
			                ? combat.PcTurnOrder
			                : campaign.Characters.Keys.OrderBy(id => id).ToList();
			            foreach (var userId in order)
			            {
			                var name = campaign.Characters.TryGetValue(userId, out var pc) ? pc?.Name : null;
			                var marker = combat != null && combat.PcTurnOrder.Count > 0 &&
			                             combat.CurrentPcTurnIndex >= 0 &&
			                             combat.CurrentPcTurnIndex < combat.PcTurnOrder.Count &&
			                             combat.PcTurnOrder[combat.CurrentPcTurnIndex] == userId
			                    ? " (current)"
			                    : "";
			                sb.AppendLine($"- <@{userId}>: {name ?? "PC"}{marker}");
			            }
			        }

			        return TrimToLimit(sb.ToString().Trim(), 1600);
			    }

		    private async Task RunEnemyPhaseAsync(
		        InstructionGPT.ChannelState channelState,
		        IMessageChannel discordChannel,
		        DndChannelState dndState,
		        DndCampaign campaign,
		        EncounterDocument encounter,
		        CancellationToken cancellationToken)
		    {
		        var combat = dndState?.Session?.Combat;
		        if (combat == null)
		        {
		            return;
		        }

		        await discordChannel.SendMessageAsync($"👹 **Enemy phase** (round {combat.RoundNumber})");

		        // Deterministic target selection: lowest current HP among PCs with stats, tie by user id.
		        var pcTargets = campaign.Characters
		            .Where(kvp => kvp.Value?.Stats != null && kvp.Value.Stats.CurrentHp > 0 && kvp.Value.Stats.ArmorClass > 0)
		            .OrderBy(kvp => kvp.Value.Stats.CurrentHp)
		            .ThenBy(kvp => kvp.Key)
		            .ToList();

		        foreach (var enemy in (encounter.Enemies ?? new List<EncounterEnemy>()).Where(e => e != null && e.CurrentHp > 0))
		        {
		            if (pcTargets.Count == 0)
		            {
		                await discordChannel.SendMessageAsync("All PCs are down (or missing stats).");
		                break;
		            }

		            var target = pcTargets[0];
		            var targetUserId = target.Key;
		            var targetStats = target.Value.Stats;

		            var toHit = enemy.ToHitBonus;
		            if (!TryEvaluateRoll($"d20{(toHit >= 0 ? "+" : string.Empty)}{toHit}", out var atkRoll, out var atkErr))
		            {
		                await discordChannel.SendMessageAsync($"Enemy attack roll failed: {atkErr}");
		                continue;
		            }

		            var d20 = atkRoll.Rolls.Count > 0 ? atkRoll.Rolls[0] : 0;
		            var critical = d20 == 20;
		            var hit = atkRoll.Total >= targetStats.ArmorClass || critical;

		            if (!hit)
		            {
		                var missText =
		                    $"🗡️ **{enemy.Name}** attacks <@{targetUserId}> ({target.Value.Name}) with {enemy.AttackName ?? "Attack"}: " +
		                    $"`{atkRoll.Expression}` => **{atkRoll.Total}** vs AC {targetStats.ArmorClass} = **MISS**";
		                await discordChannel.SendMessageAsync(TrimToLimit(missText, 900));
		                AppendTranscript(dndState, $"GM: {TrimToLimit(missText, 450)}");
		                await TryAppendLedgerEntryAsync(channelState, dndState, 0, "GM", "enemy-attack", missText, "miss", cancellationToken);
		                continue;
		            }

		            var dmgExpr = NormalizeCheckExpression(enemy.Damage);
		            if (!TryEvaluateRoll(dmgExpr, out var dmgRoll, out var dmgErr))
		            {
		                await discordChannel.SendMessageAsync($"Enemy damage roll failed: {dmgErr}");
		                continue;
		            }

		            var dmg = Math.Max(0, dmgRoll.Total);
		            var prev = targetStats.CurrentHp;
		            targetStats.CurrentHp = Math.Max(0, targetStats.CurrentHp - dmg);
		            target.Value.UpdatedUtc = DateTime.UtcNow;
		            await SaveCharacterSheetJsonAsync(channelState, campaign.Name, targetUserId, target.Value, cancellationToken);

		            var hitText =
		                $"🗡️ **{enemy.Name}** hits <@{targetUserId}> ({target.Value.Name}) with {enemy.AttackName ?? "Attack"}: " +
		                $"atk **{atkRoll.Total}** vs AC {targetStats.ArmorClass} => **HIT**\n" +
		                $"Damage `{dmgRoll.Expression}` => **{dmgRoll.Total}** | HP {prev} -> {targetStats.CurrentHp}/{targetStats.MaxHp}";
		            await discordChannel.SendMessageAsync(TrimToLimit(hitText, 1100));
		            AppendTranscript(dndState, $"GM: {TrimToLimit(hitText, 600)}");
		            await TryAppendLedgerEntryAsync(channelState, dndState, 0, "GM", "enemy-attack", TrimToLimit(hitText, 500), TrimToLimit(hitText, 500), cancellationToken);

		            // Update target ordering if HP changed.
		            pcTargets = campaign.Characters
		                .Where(kvp => kvp.Value?.Stats != null && kvp.Value.Stats.CurrentHp > 0 && kvp.Value.Stats.ArmorClass > 0)
		                .OrderBy(kvp => kvp.Value.Stats.CurrentHp)
		                .ThenBy(kvp => kvp.Key)
		                .ToList();
		        }

		        // Check win condition before advancing to the next round.
		        if (IsEncounterWon(encounter, dndState))
		        {
		            await CompleteEncounterAsync(channelState, discordChannel, dndState, campaign.Name, encounter, reason: "win", cancellationToken);
		            return;
		        }

		        // Next round.
		        combat.RoundNumber = Math.Max(1, combat.RoundNumber + 1);
		        combat.Phase = CombatPhasePlayers;
		        combat.PcActedThisRound?.Clear();
		        combat.CurrentPcTurnIndex = 0;
		        dndState.Session.PendingHitsByUserId?.Clear();

		        await discordChannel.SendMessageAsync($"🔁 Round {combat.RoundNumber} begins.");
		        await discordChannel.SendMessageAsync(BuildPlayerPhasePrompt(campaign, dndState));
		        if (TryGetCurrentPcTurn(combat, out var nextPc))
		        {
		            await discordChannel.SendMessageAsync($"➡️ Turn: <@{nextPc}>.");
		        }
		    }

		    private static bool IsEncounterWon(EncounterDocument encounter, DndChannelState dndState)
		    {
		        if (encounter == null)
		        {
		            return false;
		        }

		        var wc = encounter.WinCondition;
		        var type = wc?.Type ?? "defeat_all_enemies";
		        if (string.Equals(type, "survive_rounds", StringComparison.OrdinalIgnoreCase))
		        {
		            // Only resolve "survive_rounds" at the end of an enemy phase (i.e., after a full round).
		            var combat = dndState?.Session?.Combat;
		            if (combat == null || !string.Equals(combat.Phase, CombatPhaseEnemies, StringComparison.OrdinalIgnoreCase))
		            {
		                return false;
		            }

		            var target = wc?.TargetRounds ?? 0;
		            if (target <= 0)
		            {
		                return false;
		            }

		            var round = combat.RoundNumber;
		            return round >= target;
		        }

		        // Default: defeat all enemies.
		        return (encounter.Enemies ?? new List<EncounterEnemy>()).All(e => e == null || e.CurrentHp <= 0);
		    }

		    private async Task CompleteEncounterAsync(
		        InstructionGPT.ChannelState channelState,
		        IMessageChannel discordChannel,
		        DndChannelState dndState,
		        string campaignName,
		        EncounterDocument encounter,
		        string reason,
		        CancellationToken cancellationToken)
		    {
		        if (encounter == null || dndState?.Session == null)
		        {
		            return;
		        }

		        encounter.Status = "completed";
		        encounter.UpdatedUtc = DateTime.UtcNow;
		        await SaveEncounterAsync(channelState, campaignName, encounter, cancellationToken);

		        dndState.Session.ActiveEncounterId = null;
		        dndState.Session.PendingHitsByUserId?.Clear();
		        ResetCombatState(dndState);

		        AppendTranscript(dndState, "GM: Encounter completed.");
		        await discordChannel.SendMessageAsync($"✅ Encounter complete: **{encounter.Name}**.");
		        await TryAppendLedgerEntryAsync(channelState, dndState, 0, "GM", "encounter-complete", encounter.EncounterId, reason ?? "complete", cancellationToken);
		    }

		    private static void ResetCombatState(DndChannelState dndState)
		    {
		        var combat = dndState?.Session?.Combat;
		        if (combat == null)
		        {
		            return;
		        }

		        combat.EncounterId = null;
		        combat.Phase = CombatPhaseNone;
		        combat.RoundNumber = 0;
		        combat.PcInitiative?.Clear();
		        combat.EnemyInitiative?.Clear();
		        combat.PcActedThisRound?.Clear();
		        combat.PcTurnOrder?.Clear();
		        combat.CurrentPcTurnIndex = 0;
		        combat.PlayersGoFirst = false;
		    }

		    private static bool TryGetCurrentPcTurn(CombatState combat, out ulong userId)
		    {
		        userId = 0;
		        if (combat?.PcTurnOrder == null || combat.PcTurnOrder.Count == 0)
		        {
		            return false;
		        }

		        var idx = Math.Clamp(combat.CurrentPcTurnIndex, 0, combat.PcTurnOrder.Count - 1);
		        userId = combat.PcTurnOrder[idx];
		        return userId != 0;
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
	            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, FindCampaign(dndState, dndState.ActiveCampaignName), message.Author.Id))}: {content}");
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

            ParseCheckArgs(tail, out var skill, out var expr, out var hasExplicitExpression);
            if (string.IsNullOrWhiteSpace(skill))
            {
                await message.Channel.SendMessageAsync("Usage: `!check <skill> [d20+mod]`");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var combat = dndState?.Session?.Combat;
            var inCombatTurn = combat != null &&
                               !string.IsNullOrWhiteSpace(combat.EncounterId) &&
                               string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase);
            if (inCombatTurn && !CanPlayerActInCombat(dndState, message.Author.Id, content, out var turnError))
            {
                await message.Channel.SendMessageAsync(turnError);
                return new HandleResult(true, false, false);
            }

            var expression = hasExplicitExpression ? NormalizeCheckExpression(expr) : "d20";
            if (!hasExplicitExpression &&
                campaign?.Characters != null &&
                campaign.Characters.TryGetValue(message.Author.Id, out var character) &&
                character?.Stats?.SkillBonuses != null &&
                character.Stats.SkillBonuses.TryGetValue(skill.Trim(), out var skillBonus))
            {
                expression = NormalizeCheckExpression(skillBonus.ToString());
            }
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid check roll.");
                return new HandleResult(true, false, false);
            }

            var response = $"🧭 {skill} check for <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "check", content, response, cancellationToken);

            if (inCombatTurn)
            {
                MarkPcActed(dndState, message.Author.Id);
                await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
            }
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

            ParseCheckArgs(tail, out var ability, out var expr, out var hasExplicitExpression);
            if (string.IsNullOrWhiteSpace(ability))
            {
                await message.Channel.SendMessageAsync("Usage: `!save <ability> [d20+mod]`");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var combat = dndState?.Session?.Combat;
            var inCombatTurn = combat != null &&
                               !string.IsNullOrWhiteSpace(combat.EncounterId) &&
                               string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase);
            if (inCombatTurn && !CanPlayerActInCombat(dndState, message.Author.Id, content, out var turnError))
            {
                await message.Channel.SendMessageAsync(turnError);
                return new HandleResult(true, false, false);
            }

            var expression = hasExplicitExpression ? NormalizeCheckExpression(expr) : "d20";
            if (!hasExplicitExpression &&
                campaign?.Characters != null &&
                campaign.Characters.TryGetValue(message.Author.Id, out var character) &&
                character?.Stats?.SaveBonuses != null &&
                character.Stats.SaveBonuses.TryGetValue(ability.Trim(), out var saveBonus))
            {
                expression = NormalizeCheckExpression(saveBonus.ToString());
            }
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid save roll.");
                return new HandleResult(true, false, false);
            }

            var response = $"🛡️ {ability} save for <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "save", content, response, cancellationToken);
            if (inCombatTurn)
            {
                MarkPcActed(dndState, message.Author.Id);
                await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
            }
		            return new HandleResult(true, true, false);
		        }

	        if (string.Equals(content, "!pass", StringComparison.OrdinalIgnoreCase) ||
	            string.Equals(content, "!wait", StringComparison.OrdinalIgnoreCase) ||
	            string.Equals(content, "!done", StringComparison.OrdinalIgnoreCase) ||
	            string.Equals(content, "!skip", StringComparison.OrdinalIgnoreCase) ||
	            content.StartsWith("!skip ", StringComparison.OrdinalIgnoreCase))
	        {
	            var combat = dndState?.Session?.Combat;
	            if (combat == null || string.IsNullOrWhiteSpace(combat.EncounterId))
	            {
	                await message.Channel.SendMessageAsync("Not currently in combat.");
	                return new HandleResult(true, false, false);
	            }

	            // Optional: allow an admin to skip all remaining PCs (useful to un-stick stalled initiative/player phases).
	            var wantsSkipAll = content.Trim().Equals("!skip all", StringComparison.OrdinalIgnoreCase);
	            if (wantsSkipAll)
	            {
	                var canSkipAll = false;
	                if (message.Author is SocketGuildUser guildUser)
	                {
	                    canSkipAll =
	                        guildUser.GuildPermissions.Administrator ||
	                        guildUser.GuildPermissions.ManageGuild ||
	                        guildUser.GuildPermissions.ManageMessages;
	                }
	                if (!canSkipAll)
	                {
	                    await message.Channel.SendMessageAsync("`!skip all` requires admin/mod permissions.");
	                    return new HandleResult(true, false, false);
	                }
	            }

	            if (!string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase) &&
	                !string.Equals(combat.Phase, CombatPhasePlayers, StringComparison.OrdinalIgnoreCase))
	            {
	                await message.Channel.SendMessageAsync($"Combat is in `{combat.Phase}` phase; `!pass` isn't applicable right now.");
	                return new HandleResult(true, false, false);
	            }

	            // In initiative: treat as initiative=0 to avoid blocking.
	            if (string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase))
	            {
	                if (wantsSkipAll)
	                {
	                    var campaign0 = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
	                    combat.PcInitiative ??= new Dictionary<ulong, int>();
	                    foreach (var userId in campaign0.Characters
	                                 .Where(kvp => kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Sheet))
	                                 .Select(kvp => kvp.Key))
	                    {
	                        if (!combat.PcInitiative.ContainsKey(userId))
	                        {
	                            combat.PcInitiative[userId] = 0;
	                        }
	                    }
	                }

	                combat.PcInitiative ??= new Dictionary<ulong, int>();
	                if (!combat.PcInitiative.ContainsKey(message.Author.Id))
	                {
	                    combat.PcInitiative[message.Author.Id] = 0;
	                }
	            }
	            else
	            {
	                if (wantsSkipAll)
	                {
	                    var campaign0 = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
	                    foreach (var userId in campaign0.Characters
	                                 .Where(kvp => kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Sheet))
	                                 .Select(kvp => kvp.Key))
	                    {
	                        MarkPcActed(dndState, userId);
	                    }
	                }
	                else
	                {
	                if (!CanPlayerActInCombat(dndState, message.Author.Id, content, out var passError))
	                {
	                    await message.Channel.SendMessageAsync(passError);
	                    return new HandleResult(true, false, false);
	                }

	                if (dndState?.Session?.PendingHitsByUserId != null &&
	                    dndState.Session.PendingHitsByUserId.TryGetValue(message.Author.Id, out var pending) &&
	                    pending != null &&
	                    pending.ExpiresUtc > DateTime.UtcNow &&
	                    pending.Hit)
	                {
	                    await message.Channel.SendMessageAsync(
	                        $"You have a pending hit against **{pending.TargetName}**. Roll damage first with `!damage {pending.TargetName} <dice>`.");
	                    return new HandleResult(true, false, false);
	                }

	                MarkPcActed(dndState, message.Author.Id);
	                }
	            }

	            await message.Channel.SendMessageAsync(
	                wantsSkipAll
	                    ? "⏭️ Skipping remaining players to advance the combat."
	                    : $"⏭️ <@{message.Author.Id}> passes.");
	            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, FindCampaign(dndState, dndState.ActiveCampaignName), message.Author.Id))}: {content}");
	            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "pass", content, "pass", cancellationToken);

	            await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	            return new HandleResult(true, true, false);
	        }

        if (string.Equals(content, "!targets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(content, "!encounter", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("!encounter ", StringComparison.OrdinalIgnoreCase))
        {
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
            if (encounter == null)
            {
                await message.Channel.SendMessageAsync("No active encounter. Use `/gptcli dnd encounterstart`.");
                return new HandleResult(true, false, false);
            }

            var response = BuildEncounterSummary(encounter, includeHp: true);
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {TrimToLimit(response, 900)}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "encounter-status", content, TrimToLimit(response, 900), cancellationToken);
            return new HandleResult(true, true, false);
        }

        if (content.StartsWith("!damage", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content.Length > 7 ? content[7..].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(tail))
            {
                await message.Channel.SendMessageAsync("Usage: `!damage <target> <dice>` (example: `!damage goblin 1d8+3`)");
                return new HandleResult(true, false, false);
            }

            var damageTokens = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (damageTokens.Length < 2)
            {
                await message.Channel.SendMessageAsync("Usage: `!damage <target> <dice>` (example: `!damage goblin 1d8+3`)");
                return new HandleResult(true, false, false);
            }

            var lastToken = damageTokens[^1];
            if (!DiceExpressionRegex.IsMatch(NormalizeCheckExpression(lastToken)))
            {
                await message.Channel.SendMessageAsync("Provide a damage dice expression (example: `!damage goblin 1d8+3`).");
                return new HandleResult(true, false, false);
            }

            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
            if (encounter == null)
            {
                await message.Channel.SendMessageAsync("No active encounter. Use `/gptcli dnd encounterstart`.");
                return new HandleResult(true, false, false);
            }

            if (!CanPlayerActInCombat(dndState, message.Author.Id, content, out var turnError))
            {
                await message.Channel.SendMessageAsync(turnError);
                return new HandleResult(true, false, false);
            }

            ParseAttackArgs(tail, out var target, out var expression);
            expression = NormalizeCheckExpression(expression);
            if (!TryEvaluateRoll(expression, out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid damage roll.");
                return new HandleResult(true, false, false);
            }

            if (!TryResolveEncounterEnemy(encounter, target, out var enemy, out var resolveError))
            {
                await message.Channel.SendMessageAsync(resolveError);
                return new HandleResult(true, false, false);
            }

            // Enforce the "declare -> attack roll -> damage roll" loop.
            if (!TryConsumePendingHit(dndState, message.Author.Id, encounter.EncounterId, enemy.EnemyId, out var pending, out var pendingError))
            {
                await message.Channel.SendMessageAsync(pendingError);
                return new HandleResult(true, false, false);
            }

            var damage = Math.Max(0, roll.Total);
            var previousHp = enemy.CurrentHp;
            enemy.CurrentHp = Math.Max(0, enemy.CurrentHp - damage);
            encounter.UpdatedUtc = DateTime.UtcNow;
            await SaveEncounterAsync(channelState, campaign.Name, encounter, cancellationToken);

            var downed = enemy.CurrentHp <= 0 && previousHp > 0;
            var response =
                $"💥 Damage to **{enemy.Name}**: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**\n" +
                $"HP: {previousHp} -> {enemy.CurrentHp}/{enemy.MaxHp}" +
                (pending.Critical ? "\n(crit pending was recorded on the hit)" : "") +
                (downed ? "\n☠️ Enemy down." : "");
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {TrimToLimit(response, 900)}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "damage", content, TrimToLimit(response, 900), cancellationToken);

            // Damage roll completes the PC's action for the round.
            MarkPcActed(dndState, message.Author.Id);
            await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);

            if (IsEncounterWon(encounter, dndState))
            {
                await CompleteEncounterAsync(channelState, message.Channel, dndState, campaign.Name, encounter, reason: "win", cancellationToken);
                return new HandleResult(true, true, false);
            }

            return new HandleResult(true, true, false);
        }

	        if (content.StartsWith("!attack", StringComparison.OrdinalIgnoreCase))
	        {
	            var attackTail = content.Length > 7 ? content[7..].Trim() : string.Empty;
	            if (string.IsNullOrWhiteSpace(attackTail))
	            {
	                await message.Channel.SendMessageAsync("Usage: `!attack <target> [d20+mod]` (or just `!attack <target>` to auto-roll from your sheet)");
	                return new HandleResult(true, false, false);
	            }

	            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
	            var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
	            if (encounter == null)
	            {
	                await message.Channel.SendMessageAsync("No active encounter. Use `/gptcli dnd encounterstart`.");
	                return new HandleResult(true, false, false);
	            }

	            if (!CanPlayerActInCombat(dndState, message.Author.Id, content, out var turnError))
	            {
	                await message.Channel.SendMessageAsync(turnError);
	                return new HandleResult(true, false, false);
	            }

	            if (dndState?.Session?.PendingHitsByUserId != null &&
	                dndState.Session.PendingHitsByUserId.TryGetValue(message.Author.Id, out var pending) &&
	                pending != null &&
	                pending.ExpiresUtc > DateTime.UtcNow &&
	                pending.Hit &&
	                string.Equals(pending.EncounterId, encounter.EncounterId, StringComparison.OrdinalIgnoreCase))
	            {
	                await message.Channel.SendMessageAsync(
	                    $"You have a pending hit against **{pending.TargetName}**. Roll damage first with `!damage {pending.TargetName} <dice>`.");
	                return new HandleResult(true, false, false);
	            }

	            var parts = attackTail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	            var last = parts.Length > 0 ? parts[^1] : string.Empty;
	            var explicitRollProvided =
	                parts.Length > 1 &&
	                (DiceExpressionRegex.IsMatch(NormalizeCheckExpression(last)) ||
	                 int.TryParse(last, out _) ||
	                 (last.StartsWith("+", StringComparison.Ordinal) || last.StartsWith("-", StringComparison.Ordinal)));

	            ParseAttackArgs(attackTail, out var target, out var expression);
	            if (!TryResolveEncounterEnemy(encounter, target, out var enemy, out var resolveError))
	            {
	                await message.Channel.SendMessageAsync(resolveError);
	                return new HandleResult(true, false, false);
	            }

	            // Auto-roll mode: `!attack <target>` uses the PC's first configured attack from stats JSON.
	            if (!explicitRollProvided)
	            {
	                if (campaign.Characters == null ||
	                    !campaign.Characters.TryGetValue(message.Author.Id, out var character) ||
	                    character?.Stats == null ||
	                    character.Stats.Attacks == null ||
	                    character.Stats.Attacks.Count == 0)
	                {
	                    await message.Channel.SendMessageAsync(
	                        "I can auto-roll if your character sheet has stats JSON with at least one attack. " +
	                        "Re-run `/gptcli dnd charactercreate` to generate stats.");
	                    return new HandleResult(true, false, false);
	                }

	                var attack = character.Stats.Attacks.FirstOrDefault(a => a != null) ?? new AttackStats
	                {
	                    Name = "Attack",
	                    ToHitBonus = 0,
	                    Damage = "1"
	                };

	                var toHit = attack.ToHitBonus;
	                if (!TryEvaluateRoll($"d20{(toHit >= 0 ? "+" : string.Empty)}{toHit}", out var atkRoll, out var atkErr))
	                {
	                    await message.Channel.SendMessageAsync(atkErr ?? "Invalid attack roll.");
	                    return new HandleResult(true, false, false);
	                }

		                var atkD20 = atkRoll.Rolls.Count > 0 ? atkRoll.Rolls[0] : 0;
		                var atkCritical = atkD20 == 20;
		                var atkHit = atkRoll.Total >= enemy.ArmorClass || atkCritical;

	                var header =
	                    $"⚔️ <@{message.Author.Id}> attacks **{enemy.Name}** (AC {enemy.ArmorClass}) " +
	                    $"with **{(string.IsNullOrWhiteSpace(attack.Name) ? "Attack" : attack.Name)}**: " +
	                    $"`{atkRoll.Expression}` => [{string.Join(", ", atkRoll.Rolls)}]{FormatModifier(atkRoll.Modifier)} = **{atkRoll.Total}**";

		                if (!atkHit)
		                {
		                    var miss = $"{header}\nResult: **MISS**.";
	                    await message.Channel.SendMessageAsync(miss);
	                    AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
	                    AppendTranscript(dndState, $"GM: {TrimToLimit(miss, 900)}");
	                    await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", content, TrimToLimit(miss, 900), cancellationToken);

	                    MarkPcActed(dndState, message.Author.Id);
	                    await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	                    return new HandleResult(true, true, false);
	                }

	                var baseExpr = ExtractFirstDiceExpression(attack.Damage);
	                if (string.IsNullOrWhiteSpace(baseExpr))
	                {
	                    baseExpr = "1";
	                }
		                var dmgExpr = atkCritical ? MakeCriticalDiceExpression(baseExpr) : baseExpr;
	                if (!TryEvaluateRoll(dmgExpr, out var dmgRoll, out var dmgErr))
	                {
	                    await message.Channel.SendMessageAsync(dmgErr ?? "Invalid damage roll.");
	                    return new HandleResult(true, false, false);
	                }

	                var previousHp = enemy.CurrentHp;
	                enemy.CurrentHp = Math.Max(0, enemy.CurrentHp - Math.Max(0, dmgRoll.Total));
	                enemy.MaxHp = Math.Max(1, enemy.MaxHp);
	                var downed = enemy.CurrentHp <= 0 && previousHp > 0;

	                encounter.UpdatedUtc = DateTime.UtcNow;
	                await SaveEncounterAsync(channelState, campaign.Name, encounter, cancellationToken);

		                var hitText =
		                    $"{header}\nResult: **HIT**{(atkCritical ? " (NAT 20)" : "")}. " +
		                    $"Damage `{dmgRoll.Expression}` => **{dmgRoll.Total}** | HP {previousHp} -> {enemy.CurrentHp}/{enemy.MaxHp}" +
		                    (downed ? "\n☠️ Enemy down." : "");
	                await message.Channel.SendMessageAsync(TrimToLimit(hitText, 1200));
	                AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
	                AppendTranscript(dndState, $"GM: {TrimToLimit(hitText, 900)}");
	                await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", content, TrimToLimit(hitText, 900), cancellationToken);

	                MarkPcActed(dndState, message.Author.Id);
	                await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);

	                if (IsEncounterWon(encounter, dndState))
	                {
	                    await CompleteEncounterAsync(channelState, message.Channel, dndState, campaign.Name, encounter, reason: "win", cancellationToken);
	                }
	                return new HandleResult(true, true, false);
	            }

	            // Explicit roll mode: preserve the declare -> attack -> damage loop.
	            expression = NormalizeCheckExpression(expression);
	            if (!TryEvaluateRoll(expression, out var roll, out var error))
	            {
	                await message.Channel.SendMessageAsync(error ?? "Invalid attack roll.");
	                return new HandleResult(true, false, false);
	            }

	            var d20 = roll.Rolls.Count > 0 ? roll.Rolls[0] : 0;
	            var critical = d20 == 20;
	            var hit = roll.Total >= enemy.ArmorClass || critical;

            RememberPendingHit(dndState, message.Author.Id, encounter.EncounterId, enemy, roll, hit, critical);

            var response =
                $"⚔️ Attack vs **{enemy.Name}** (AC {enemy.ArmorClass}) by <@{message.Author.Id}>: `{roll.Expression}` => [{string.Join(", ", roll.Rolls)}]{FormatModifier(roll.Modifier)} = **{roll.Total}**\n" +
                (hit
                    ? $"Result: **HIT**{(critical ? " (NAT 20)" : "")}. Now roll damage with `!damage {enemy.Name} <dice>`."
                    : "Result: **MISS**.");
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {TrimToLimit(response, 900)}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", content, TrimToLimit(response, 900), cancellationToken);

	            if (!hit)
	            {
	                MarkPcActed(dndState, message.Author.Id);
	                await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	            }
	            return new HandleResult(true, true, false);
	        }

        if (content.StartsWith("!initiative", StringComparison.OrdinalIgnoreCase))
        {
            var campaign = GetOrCreateCampaign(dndState, dndState.ActiveCampaignName);
            var tail = content.Length > 11 ? content[11..].Trim() : string.Empty;
            var bonus = 0;
            if (!string.IsNullOrWhiteSpace(tail))
            {
                if (!int.TryParse(tail, out bonus))
                {
                    await message.Channel.SendMessageAsync("Usage: `!initiative [bonus]`");
                    return new HandleResult(true, false, false);
                }
            }
            else
            {
                // If combat is active, default to the PC's stored initiative bonus.
                if (campaign?.Characters != null &&
                    campaign.Characters.TryGetValue(message.Author.Id, out var character) &&
                    character?.Stats != null)
                {
                    bonus = character.Stats.InitiativeBonus;
                }
            }

            if (!TryEvaluateRoll($"d20{(bonus >= 0 ? "+" : string.Empty)}{bonus}", out var roll, out var error))
            {
                await message.Channel.SendMessageAsync(error ?? "Invalid initiative roll.");
                return new HandleResult(true, false, false);
            }

            // Legacy initiative order (still useful outside combat).
            dndState.Session.Initiative[message.Author.Id] = roll.Total;
            dndState.Session.TurnOrder = dndState.Session.Initiative
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key)
                .Select(entry => entry.Key)
                .ToList();
            dndState.Session.CurrentTurnIndex = 0;

            // Combat gating initiative.
            var combat = dndState?.Session?.Combat;
            if (combat != null &&
                !string.IsNullOrWhiteSpace(combat.EncounterId) &&
                string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase))
            {
                combat.PcInitiative ??= new Dictionary<ulong, int>();
                combat.PcInitiative[message.Author.Id] = roll.Total;
            }

            // If we're still waiting on initiative, show who hasn't rolled yet.
            if (combat != null &&
                !string.IsNullOrWhiteSpace(combat.EncounterId) &&
                string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase) &&
                campaign?.Characters is { Count: > 0 })
            {
                var needed = campaign.Characters
                    .Where(kvp => kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Sheet))
                    .Select(kvp => kvp.Key)
                    .ToList();
                var missing = needed
                    .Where(id => combat.PcInitiative == null || !combat.PcInitiative.ContainsKey(id))
                    .ToList();
	                if (missing.Count > 0)
	                {
	                    await message.Channel.SendMessageAsync(
	                        $"Waiting on initiative from: {string.Join(", ", missing.Select(id => $"<@{id}>"))}. " +
	                        "Each player: `!initiative` (or `!pass`). Admin/mod can use `!skip all`.");
	                }
	            }

            var order = string.Join(" -> ", dndState.Session.TurnOrder.Select(id => $"<@{id}>({dndState.Session.Initiative[id]})"));
            var response =
                $"🎯 Initiative for <@{message.Author.Id}>: `{roll.Expression}` => **{roll.Total}**\n" +
                $"Order: {order}\n" +
                $"Current turn: <@{dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex]}>";
            await message.Channel.SendMessageAsync(response);
            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, FindCampaign(dndState, dndState.ActiveCampaignName), message.Author.Id))}: {content}");
            AppendTranscript(dndState, $"GM: {response}");
            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "initiative", content, response, cancellationToken);

            await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
            return new HandleResult(true, true, false);
        }

	        if (content.StartsWith("!endturn", StringComparison.OrdinalIgnoreCase))
	        {
	            if (dndState.Session.TurnOrder.Count == 0)
	            {
	                await message.Channel.SendMessageAsync("No initiative order yet. Use `!initiative [bonus]` first.");
	                return new HandleResult(true, false, false);
	            }

	            var currentUserId = dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex];
	            if (currentUserId != message.Author.Id)
	            {
	                await message.Channel.SendMessageAsync(
	                    $"It is currently <@{currentUserId}>'s turn.");
	                return new HandleResult(true, false, false);
	            }

            dndState.Session.CurrentTurnIndex = (dndState.Session.CurrentTurnIndex + 1) % dndState.Session.TurnOrder.Count;
	            var nextUserId = dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex];
	            var response = $"⏭️ Turn ended. Next up: <@{nextUserId}>.";
	            await message.Channel.SendMessageAsync(response);
	            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, FindCampaign(dndState, dndState.ActiveCampaignName), message.Author.Id))}: {content}");
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

        using var typing = DiscordTyping.Begin(message.Channel);
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

	    private async Task<HandleResult> HandleInitiativeAsync(
	        InstructionGPT.ChannelState channelState,
	        SocketMessage message,
	        DndChannelState dndState,
	        DndCampaign campaign,
	        int? explicitBonus,
	        string transcriptText,
	        CancellationToken cancellationToken)
	    {
	        if (message == null || dndState?.Session == null)
	        {
	            return HandleResult.NotHandled;
	        }

	        var bonus = explicitBonus ?? 0;
	        if (!explicitBonus.HasValue)
	        {
	            if (campaign?.Characters != null &&
	                campaign.Characters.TryGetValue(message.Author.Id, out var character) &&
	                character?.Stats != null)
	            {
	                bonus = character.Stats.InitiativeBonus;
	            }
	        }

	        if (!TryEvaluateRoll($"d20{(bonus >= 0 ? "+" : string.Empty)}{bonus}", out var roll, out var error))
	        {
	            await message.Channel.SendMessageAsync(error ?? "Invalid initiative roll.");
	            return new HandleResult(true, false, false);
	        }

	        // Legacy initiative order (still useful outside combat).
	        dndState.Session.Initiative[message.Author.Id] = roll.Total;
	        dndState.Session.TurnOrder = dndState.Session.Initiative
	            .OrderByDescending(entry => entry.Value)
	            .ThenBy(entry => entry.Key)
	            .Select(entry => entry.Key)
	            .ToList();
	        dndState.Session.CurrentTurnIndex = 0;

	        // Combat gating initiative.
	        var combat = dndState.Session.Combat;
	        if (combat != null &&
	            !string.IsNullOrWhiteSpace(combat.EncounterId) &&
	            string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase))
	        {
	            combat.PcInitiative ??= new Dictionary<ulong, int>();
	            combat.PcInitiative[message.Author.Id] = roll.Total;
	        }

	        // If we're still waiting on initiative, show who hasn't rolled yet.
	        if (combat != null &&
	            !string.IsNullOrWhiteSpace(combat.EncounterId) &&
	            string.Equals(combat.Phase, CombatPhaseInitiative, StringComparison.OrdinalIgnoreCase) &&
	            campaign?.Characters is { Count: > 0 })
	        {
	            var needed = campaign.Characters
	                .Where(kvp => kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Sheet))
	                .Select(kvp => kvp.Key)
	                .ToList();
	            var missing = needed
	                .Where(id => combat.PcInitiative == null || !combat.PcInitiative.ContainsKey(id))
	                .ToList();
	            if (missing.Count > 0)
	            {
	                await message.Channel.SendMessageAsync(
	                    $"Waiting on initiative from: {string.Join(", ", missing.Select(id => $"<@{id}>"))}. " +
	                    "Each player: `!initiative` (or `!pass`). Admin/mod can use `!skip all`.");
	            }
	        }

	        var order = string.Join(" -> ", dndState.Session.TurnOrder.Select(id => $"<@{id}>({dndState.Session.Initiative[id]})"));
	        var response =
	            $"🎯 Initiative for <@{message.Author.Id}>: `{roll.Expression}` => **{roll.Total}**\n" +
	            $"Order: {order}\n" +
	            $"Current turn: <@{dndState.Session.TurnOrder[dndState.Session.CurrentTurnIndex]}>";
	        await message.Channel.SendMessageAsync(response);
	        AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {transcriptText}");
	        AppendTranscript(dndState, $"GM: {response}");
	        await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "initiative", transcriptText, response, cancellationToken);

	        await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	        return new HandleResult(true, true, false);
	    }

	    private async Task<HandleResult> HandleAutoAttackAsync(
	        InstructionGPT.ChannelState channelState,
	        SocketMessage message,
	        DndChannelState dndState,
	        DndCampaign campaign,
	        EncounterDocument encounter,
	        string target,
	        string requestedAttackName,
	        string transcriptText,
	        CancellationToken cancellationToken)
	    {
	        if (message == null || dndState == null || campaign == null || encounter == null)
	        {
	            return HandleResult.NotHandled;
	        }

	        if (!CanPlayerActInCombat(dndState, message.Author.Id, transcriptText, out var turnError))
	        {
	            await message.Channel.SendMessageAsync(turnError);
	            return new HandleResult(true, false, false);
	        }

	        if (campaign.Characters == null ||
	            !campaign.Characters.TryGetValue(message.Author.Id, out var character) ||
	            character?.Stats == null ||
	            character.Stats.Attacks == null ||
	            character.Stats.Attacks.Count == 0)
	        {
	            await message.Channel.SendMessageAsync(
	                "I can auto-roll if your character sheet has stats JSON with at least one attack. " +
	                "Re-run `/gptcli dnd charactercreate` to generate stats.");
	            return new HandleResult(true, false, false);
	        }

	        if (!TryResolveEncounterEnemy(encounter, target, out var enemy, out var resolveError))
	        {
	            await message.Channel.SendMessageAsync(resolveError);
	            return new HandleResult(true, false, false);
	        }

	        AttackStats attack = null;
	        if (!string.IsNullOrWhiteSpace(requestedAttackName))
	        {
	            attack = character.Stats.Attacks.FirstOrDefault(a =>
	                a != null &&
	                !string.IsNullOrWhiteSpace(a.Name) &&
	                a.Name.Contains(requestedAttackName, StringComparison.OrdinalIgnoreCase));
	        }
	        attack ??= character.Stats.Attacks.FirstOrDefault(a => a != null);
	        attack ??= new AttackStats { Name = "Attack", ToHitBonus = 0, Damage = "1" };

	        var toHit = attack.ToHitBonus;
	        if (!TryEvaluateRoll($"d20{(toHit >= 0 ? "+" : string.Empty)}{toHit}", out var atkRoll, out var atkErr))
	        {
	            await message.Channel.SendMessageAsync(atkErr ?? "Invalid attack roll.");
	            return new HandleResult(true, false, false);
	        }

	        var atkD20 = atkRoll.Rolls.Count > 0 ? atkRoll.Rolls[0] : 0;
	        var atkCritical = atkD20 == 20;
	        var atkHit = atkRoll.Total >= enemy.ArmorClass || atkCritical;

	        var header =
	            $"⚔️ <@{message.Author.Id}> attacks **{enemy.Name}** (AC {enemy.ArmorClass}) " +
	            $"with **{(string.IsNullOrWhiteSpace(attack.Name) ? "Attack" : attack.Name)}**: " +
	            $"`{atkRoll.Expression}` => [{string.Join(", ", atkRoll.Rolls)}]{FormatModifier(atkRoll.Modifier)} = **{atkRoll.Total}**";

	        if (!atkHit)
	        {
	            var miss = $"{header}\nResult: **MISS**.";
	            await message.Channel.SendMessageAsync(miss);
	            AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {transcriptText}");
	            AppendTranscript(dndState, $"GM: {TrimToLimit(miss, 900)}");
	            await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", transcriptText, TrimToLimit(miss, 900), cancellationToken);

	            MarkPcActed(dndState, message.Author.Id);
	            await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);
	            return new HandleResult(true, true, false);
	        }

	        var baseExpr = ExtractFirstDiceExpression(attack.Damage);
	        if (string.IsNullOrWhiteSpace(baseExpr))
	        {
	            baseExpr = "1";
	        }
	        var dmgExpr = atkCritical ? MakeCriticalDiceExpression(baseExpr) : baseExpr;
	        if (!TryEvaluateRoll(dmgExpr, out var dmgRoll, out var dmgErr))
	        {
	            await message.Channel.SendMessageAsync(dmgErr ?? "Invalid damage roll.");
	            return new HandleResult(true, false, false);
	        }

	        var previousHp = enemy.CurrentHp;
	        enemy.CurrentHp = Math.Max(0, enemy.CurrentHp - Math.Max(0, dmgRoll.Total));
	        enemy.MaxHp = Math.Max(1, enemy.MaxHp);
	        var downed = enemy.CurrentHp <= 0 && previousHp > 0;

	        encounter.UpdatedUtc = DateTime.UtcNow;
	        await SaveEncounterAsync(channelState, campaign.Name, encounter, cancellationToken);

	        var hitText =
	            $"{header}\nResult: **HIT**{(atkCritical ? " (NAT 20)" : "")}. " +
	            $"Damage `{dmgRoll.Expression}` => **{dmgRoll.Total}** | HP {previousHp} -> {enemy.CurrentHp}/{enemy.MaxHp}" +
	            (downed ? "\n☠️ Enemy down." : "");
	        await message.Channel.SendMessageAsync(TrimToLimit(hitText, 1200));
	        AppendTranscript(dndState, $"{FormatPlayerLabel(message.Author.Username, ResolveActiveCharacterName(dndState, campaign, message.Author.Id))}: {transcriptText}");
	        AppendTranscript(dndState, $"GM: {TrimToLimit(hitText, 900)}");
	        await TryAppendLedgerEntryAsync(channelState, dndState, message.Author.Id, message.Author.Username, "attack", transcriptText, TrimToLimit(hitText, 900), cancellationToken);

	        MarkPcActed(dndState, message.Author.Id);
	        await TryAdvanceCombatAsync(channelState, message.Channel, dndState, cancellationToken);

	        if (IsEncounterWon(encounter, dndState))
	        {
	            await CompleteEncounterAsync(channelState, message.Channel, dndState, campaign.Name, encounter, reason: "win", cancellationToken);
	            return new HandleResult(true, true, false);
	        }

	        return new HandleResult(true, true, false);
	    }

    private async Task<CampaignPackageResponse> GenerateCampaignPackageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string campaignName,
        string prompt,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(context, channelState);
        var requestPrompt = BuildCreateCampaignPackagePrompt(campaignName, prompt);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are a D&D campaign bootstrapper for a deterministic Discord rules engine. " +
                    "Return strict JSON only. Use SRD 5.1-compatible content only. " +
                    "Do not include copyrighted non-SRD monster stat blocks."),
                new(StaticValues.ChatMessageRoles.User, requestPrompt)
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            await Console.Out.WriteLineAsync($"DND campaign package generation failed: {response.Error?.Code} {response.Error?.Message}");
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var json = content;
        if (!json.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            if (TryExtractLastJsonCodeBlock(content, out var jsonBlock, out _))
            {
                json = jsonBlock;
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CampaignPackageResponse>(json, _jsonOptions);
            if (parsed == null)
            {
                return null;
            }

            parsed.CampaignMarkdown = parsed.CampaignMarkdown?.Trim();
            parsed.Encounters ??= new List<EncounterDocument>();

            // Clamp to sane bounds so the module doesn't create massive files.
            if (parsed.Encounters.Count > 12)
            {
                parsed.Encounters = parsed.Encounters.Take(12).ToList();
            }

            foreach (var e in parsed.Encounters.Where(e => e != null))
            {
                e.Name = string.IsNullOrWhiteSpace(e.Name) ? "Encounter" : e.Name.Trim();
                e.Status = string.IsNullOrWhiteSpace(e.Status) ? "prepared" : e.Status.Trim();
                e.WinCondition ??= new EncounterWinCondition();
                e.Enemies ??= new List<EncounterEnemy>();
            }

            return parsed;
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"DND campaign package parse failed: {ex.GetType().Name} {ex.Message}");
            return null;
        }
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
	        prompt.AppendLine("Return concise markdown. Put all key combat math up front so a GM can run the character without asking follow-ups.");
	        prompt.AppendLine("Start with a section titled `Quick Stats` that includes:");
	        prompt.AppendLine("- Name, Class/Level, Ancestry/Lineage, Background");
	        prompt.AppendLine("- Proficiency Bonus, AC, HP, Speed, Initiative modifier, Passive Perception");
	        prompt.AppendLine("- Ability Scores with modifiers: STR/DEX/CON/INT/WIS/CHA");
	        prompt.AppendLine("- Saving Throws and Skills (with bonuses)");
	        prompt.AppendLine("- Attacks: name, to-hit bonus, damage, and relevant traits");
	        prompt.AppendLine("- If the character can cast spells, include `Spellcasting` with:");
	        prompt.AppendLine("  - Spellcasting ability");
	        prompt.AppendLine("  - Spell attack bonus (e.g. +5)");
	        prompt.AppendLine("  - Spell save DC (e.g. DC 13)");
	        prompt.AppendLine("  - Spell slots (if any)");
	        prompt.AppendLine("  - Cantrips and prepared/known spells");
		        prompt.AppendLine("After Quick Stats, include: Proficiencies, Equipment, and 3 roleplay hooks.");
		        prompt.AppendLine("At the very end, include a trailing ```json code block with ONLY this JSON object (no commentary):");
		        prompt.AppendLine("{");
		        prompt.AppendLine("  \"name\": \"string\",");
		        prompt.AppendLine("  \"class\": \"string\",");
		        prompt.AppendLine("  \"level\": 1,");
		        prompt.AppendLine("  \"proficiencyBonus\": 2,");
		        prompt.AppendLine("  \"armorClass\": 12,");
		        prompt.AppendLine("  \"maxHp\": 10,");
		        prompt.AppendLine("  \"currentHp\": 10,");
		        prompt.AppendLine("  \"speed\": 30,");
		        prompt.AppendLine("  \"initiativeBonus\": 2,");
		        prompt.AppendLine("  \"passivePerception\": 12,");
		        prompt.AppendLine("  \"abilityScores\": {\"STR\":10,\"DEX\":10,\"CON\":10,\"INT\":10,\"WIS\":10,\"CHA\":10},");
		        prompt.AppendLine("  \"abilityMods\": {\"STR\":0,\"DEX\":0,\"CON\":0,\"INT\":0,\"WIS\":0,\"CHA\":0},");
		        prompt.AppendLine("  \"saveBonuses\": {\"STR\":0,\"DEX\":0,\"CON\":0,\"INT\":0,\"WIS\":0,\"CHA\":0},");
		        prompt.AppendLine("  \"skillBonuses\": {\"Perception\":2},");
		        prompt.AppendLine("  \"attacks\": [{\"name\":\"Dagger\",\"toHitBonus\":4,\"damage\":\"1d4+2 piercing\",\"notes\":\"\"}],");
		        prompt.AppendLine("  \"spellcasting\": {\"ability\":\"INT\",\"spellAttackBonus\":4,\"spellSaveDc\":12}");
		        prompt.AppendLine("}");
		        prompt.AppendLine("If the character has no spellcasting, set spellcasting=null.");
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
		                    "You generate practical D&D character sheets using SRD-compatible rules text only. " +
		                    "Compute all bonuses and DCs explicitly (proficiency bonus, skill bonuses, spell attack bonus, spell save DC). " +
		                    "Do not ask the user to provide their spell bonus or DC. " +
		                    "Always include the trailing JSON code block exactly as requested."),
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
	        prompt.AppendLine("Return concise markdown. Put all key combat math up front so a GM can run the NPC without asking follow-ups.");
	        prompt.AppendLine("Start with a section titled `Quick Stats` that includes:");
	        prompt.AppendLine("- Role, Level (or CR-like rough level equivalent), and primary combat style");
	        prompt.AppendLine("- Proficiency Bonus (if applicable), AC, HP, Speed, Initiative modifier, Passive Perception");
	        prompt.AppendLine("- Ability Scores with modifiers: STR/DEX/CON/INT/WIS/CHA");
	        prompt.AppendLine("- Saving Throws and Skills (with bonuses)");
	        prompt.AppendLine("- Attacks: name, to-hit bonus, damage");
	        prompt.AppendLine("- If the NPC can cast spells, include `Spellcasting` with:");
	        prompt.AppendLine("  - Spellcasting ability");
	        prompt.AppendLine("  - Spell attack bonus");
	        prompt.AppendLine("  - Spell save DC");
	        prompt.AppendLine("  - Notable spells/abilities (keep short)");
		        prompt.AppendLine("After Quick Stats, include sections: Personality, Voice, Bond, Flaw, and 4 example dialogue lines.");
		        prompt.AppendLine("At the very end, include a trailing ```json code block with ONLY this JSON object (no commentary):");
		        prompt.AppendLine("{");
		        prompt.AppendLine("  \"name\": \"string\",");
		        prompt.AppendLine("  \"class\": \"string\",");
		        prompt.AppendLine("  \"level\": 1,");
		        prompt.AppendLine("  \"proficiencyBonus\": 2,");
		        prompt.AppendLine("  \"armorClass\": 12,");
		        prompt.AppendLine("  \"maxHp\": 10,");
		        prompt.AppendLine("  \"currentHp\": 10,");
		        prompt.AppendLine("  \"speed\": 30,");
		        prompt.AppendLine("  \"initiativeBonus\": 2,");
		        prompt.AppendLine("  \"passivePerception\": 12,");
		        prompt.AppendLine("  \"abilityScores\": {\"STR\":10,\"DEX\":10,\"CON\":10,\"INT\":10,\"WIS\":10,\"CHA\":10},");
		        prompt.AppendLine("  \"abilityMods\": {\"STR\":0,\"DEX\":0,\"CON\":0,\"INT\":0,\"WIS\":0,\"CHA\":0},");
		        prompt.AppendLine("  \"saveBonuses\": {\"STR\":0,\"DEX\":0,\"CON\":0,\"INT\":0,\"WIS\":0,\"CHA\":0},");
		        prompt.AppendLine("  \"skillBonuses\": {\"Perception\":2},");
		        prompt.AppendLine("  \"attacks\": [{\"name\":\"Shortsword\",\"toHitBonus\":4,\"damage\":\"1d6+2 piercing\",\"notes\":\"\"}],");
		        prompt.AppendLine("  \"spellcasting\": {\"ability\":\"WIS\",\"spellAttackBonus\":4,\"spellSaveDc\":12}");
		        prompt.AppendLine("}");
		        prompt.AppendLine("If the NPC has no spellcasting, set spellcasting=null.");
		        prompt.AppendLine($"Concept: {concept}");
		        prompt.AppendLine("Campaign summary:");
		        prompt.AppendLine(TrimToLimit(campaign.Content, 3500));

	        var request = new ChatCompletionCreateRequest
	        {
	            Model = model,
	            Messages = new List<ChatMessage>
	            {
		                new(StaticValues.ChatMessageRoles.System,
		                    "You design NPC allies for live roleplay. Keep profiles compact and practical. " +
		                    "Compute all bonuses explicitly (to-hit, skills, spell attack, spell save DC). " +
		                    "Always include the trailing JSON code block exactly as requested."),
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
	        var activeCharacterName = ResolveActiveCharacterName(dndState, campaign, message.Author.Id);
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
	        prompt.AppendLine($"Latest player: {FormatPlayerLabel(message.Author.Username, activeCharacterName)}");
	        prompt.AppendLine("Message:");
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
        var activeCharacterName = ResolveActiveCharacterName(dndState, campaign, message.Author.Id);
        var activeCharacterSheet = TryGetCharacterSheet(campaign, message.Author.Id);
        var activeCharacterStats = (campaign?.Characters != null && campaign.Characters.TryGetValue(message.Author.Id, out var activeChar))
            ? activeChar?.Stats
            : null;
        var playerRoster = BuildPlayerRoster(campaign, dndState);
        var encounter = await LoadActiveEncounterAsync(channelState, dndState, campaign.Name, cancellationToken);
        var encounterSummary = encounter != null ? BuildEncounterSummary(encounter, includeHp: true) : null;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"Campaign name: {campaign.Name}");
        userPrompt.AppendLine("Campaign summary:");
        userPrompt.AppendLine(TrimToLimit(campaign.Content, 5000));
        userPrompt.AppendLine();
	        userPrompt.AppendLine("Player characters (PCs):");
	        userPrompt.AppendLine(playerRoster);
	        userPrompt.AppendLine();
	        userPrompt.AppendLine("Party NPC companions:");
	        userPrompt.AppendLine(npcRoster);
        userPrompt.AppendLine();
        userPrompt.AppendLine($"Initiative order: {order}");
        if (!string.IsNullOrWhiteSpace(encounterSummary))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Active encounter (deterministic state):");
            userPrompt.AppendLine(encounterSummary);
        }
        userPrompt.AppendLine("Recent transcript:");
        userPrompt.AppendLine(transcript);
        userPrompt.AppendLine();
        if (!string.IsNullOrWhiteSpace(activeCharacterName))
        {
	            userPrompt.AppendLine($"Active speaker: {message.Author.Username} as \"{activeCharacterName}\"");
	        }
        if (!string.IsNullOrWhiteSpace(activeCharacterSheet))
        {
            userPrompt.AppendLine("Active speaker character sheet (excerpt):");
            userPrompt.AppendLine(TrimToLimit(activeCharacterSheet, 1800));
        }
        if (activeCharacterStats != null)
        {
            userPrompt.AppendLine("Active speaker numeric stats (JSON):");
            userPrompt.AppendLine(TrimToLimit(JsonSerializer.Serialize(activeCharacterStats, _jsonOptions), 1400));
        }
        userPrompt.AppendLine();
        userPrompt.AppendLine("Latest player message:");
	        userPrompt.AppendLine($"{FormatPlayerLabel(message.Author.Username, activeCharacterName)}: {message.Content}");
	        if (addressedNpcs.Count > 0)
	        {
	            userPrompt.AppendLine($"Addressed NPCs in this message: {string.Join(", ", addressedNpcs)}");
	        }

        userPrompt.AppendLine();
        userPrompt.AppendLine(
            "Respond as the GM with in-world narration and options. " +
            "This table uses a deterministic rules engine: never resolve hit/miss/damage/HP changes from freeform chat. " +
            "If a chance-based resolution is needed, explicitly ask for official !commands like !roll, !check, !attack, !damage, or !save. " +
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
                    "Do not do mechanics. Do not decide hits, misses, damage, HP changes, or status effects unless the official !command output in the transcript already states it. " +
                    "When an active speaker character name is provided, address them by that name and treat them as that character. " +
                    "Do not ask who they are if an active character is provided. " +
                    "If a character sheet excerpt is provided, use its numbers (spell attack bonus, save DC, AC, HP) directly; do not ask the player for them unless missing. " +
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
	        else
	        {
	            // Back-compat: earlier attempt stored under <channelDir>/modules/dnd/...
	            var interimPath = ResolveInterimStatePath(channelState);
	            if (File.Exists(interimPath))
	            {
	                var json = await File.ReadAllTextAsync(interimPath, cancellationToken);
	                if (!string.IsNullOrWhiteSpace(json))
	                {
	                    loaded = JsonSerializer.Deserialize<DndChannelState>(json, _jsonOptions);
	                    if (loaded != null)
	                    {
	                        await SaveStateAsync(channelState, loaded, cancellationToken);
	                    }
	                }
	            }

	            if (loaded == null)
	            {
	                // Back-compat: old location under channels/dnd/...
	                var legacyPath = ResolveLegacyStatePath(channelState);
	                if (File.Exists(legacyPath))
	                {
	                    var json = await File.ReadAllTextAsync(legacyPath, cancellationToken);
	                    if (!string.IsNullOrWhiteSpace(json))
	                    {
	                        loaded = JsonSerializer.Deserialize<DndChannelState>(json, _jsonOptions);
	                        if (loaded != null)
	                        {
	                            await SaveStateAsync(channelState, loaded, cancellationToken);
	                        }
	                    }
	                }
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
	        return Path.Combine(GetDndRootDirectory(channelState), "state.json");
	    }

	    private static string ResolveInterimStatePath(InstructionGPT.ChannelState channelState)
	    {
	        return Path.Combine(GetInterimDndRootDirectory(channelState), "state.json");
	    }

	    private static string ResolveLegacyStatePath(InstructionGPT.ChannelState channelState)
	    {
	        var guildPart = channelState.GuildId == 0 ? "dm" : channelState.GuildId.ToString();
	        return Path.Combine("channels", "dnd", $"{guildPart}_{channelState.ChannelId}.json");
	    }

	    private static void TryCopyDirectoryContents(string sourceDir, string destinationDir)
	    {
	        try
	        {
	            if (!Directory.Exists(sourceDir))
	            {
	                return;
	            }

	            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
	            {
	                var relative = Path.GetRelativePath(sourceDir, file);
	                var dest = Path.Combine(destinationDir, relative);
	                var destParent = Path.GetDirectoryName(dest);
	                if (!string.IsNullOrWhiteSpace(destParent))
	                {
	                    Directory.CreateDirectory(destParent);
	                }

	                if (!File.Exists(dest))
	                {
	                    File.Copy(file, dest);
	                }
	            }
	        }
	        catch (Exception ex)
	        {
	            // Don't block gameplay on filesystem migration failures.
	            try
	            {
	                Console.WriteLine($"DND directory migrate failed: {ex.GetType().Name} {ex.Message}");
	            }
	            catch
	            {
	                // ignore
	            }
	        }
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
		        state.Session.ActiveCharacterByUserId ??= new Dictionary<ulong, string>();
		        state.Session.PendingHitsByUserId ??= new Dictionary<ulong, PendingHitState>();
		        state.Session.Combat ??= new CombatState();
		        state.Session.Combat.PcInitiative ??= new Dictionary<ulong, int>();
		        state.Session.Combat.EnemyInitiative ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		        state.Session.Combat.PcActedThisRound ??= new List<ulong>();
		        state.Session.Combat.PcTurnOrder ??= new List<ulong>();
		        if (state.Session.Combat.CurrentPcTurnIndex < 0)
		        {
		            state.Session.Combat.CurrentPcTurnIndex = 0;
		        }
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

	    private static string FormatPlayerLabel(string username, string characterName)
	    {
	        if (string.IsNullOrWhiteSpace(characterName))
	        {
	            return username;
	        }

	        return $"{characterName} ({username})";
	    }

	    private static string ResolveActiveCharacterName(DndChannelState dndState, DndCampaign campaign, ulong userId)
	    {
	        if (dndState?.Session?.ActiveCharacterByUserId != null &&
	            dndState.Session.ActiveCharacterByUserId.TryGetValue(userId, out var active) &&
	            !string.IsNullOrWhiteSpace(active))
	        {
	            return active.Trim();
	        }

	        if (campaign?.Characters != null &&
	            campaign.Characters.TryGetValue(userId, out var character) &&
	            !string.IsNullOrWhiteSpace(character?.Name))
	        {
	            return character.Name.Trim();
	        }

	        return null;
	    }

	    private static string TryGetCharacterSheet(DndCampaign campaign, ulong userId)
	    {
	        if (campaign?.Characters == null)
	        {
	            return null;
	        }

	        return campaign.Characters.TryGetValue(userId, out var character) ? character?.Sheet : null;
	    }

	    private static string BuildPlayerRoster(DndCampaign campaign, DndChannelState dndState)
	    {
	        if (campaign?.Characters == null || campaign.Characters.Count == 0)
	        {
	            return "- (none)";
	        }

	        var lines = new List<string>();
	        foreach (var entry in campaign.Characters.OrderBy(e => e.Key))
	        {
	            var userId = entry.Key;
	            var character = entry.Value;
	            if (character == null || string.IsNullOrWhiteSpace(character.Name))
	            {
	                continue;
	            }

	            var active = ResolveActiveCharacterName(dndState, campaign, userId);
	            var activeMarker = !string.IsNullOrWhiteSpace(active) &&
	                               string.Equals(active, character.Name, StringComparison.OrdinalIgnoreCase)
	                ? " (active)"
	                : string.Empty;
	            var concept = string.IsNullOrWhiteSpace(character.Concept) ? "" : $" - {TrimToLimit(character.Concept, 80)}";
	            lines.Add($"- <@{userId}>: {character.Name}{activeMarker}{concept}");
	        }

	        return lines.Count == 0 ? "- (none)" : string.Join("\n", lines);
	    }

	    private static string BuildPartySheetExcerpts(DndCampaign campaign, int maxChars)
	    {
	        if (campaign == null || maxChars <= 0)
	        {
	            return null;
	        }

	        var sb = new StringBuilder();
	        var used = 0;

	        if (campaign.Characters is { Count: > 0 })
	        {
	            foreach (var entry in campaign.Characters.OrderBy(e => e.Key))
	            {
	                var userId = entry.Key;
	                var character = entry.Value;
	                if (character == null || string.IsNullOrWhiteSpace(character.Name) || string.IsNullOrWhiteSpace(character.Sheet))
	                {
	                    continue;
	                }

	                var header = $"PC {character.Name} (<@{userId}>)";
	                var body = TrimToLimit(character.Sheet, 900);
	                var block = $"[{header}]\n{body}\n";
	                if (used + block.Length > maxChars)
	                {
	                    break;
	                }

	                if (sb.Length > 0)
	                {
	                    sb.AppendLine();
	                }

	                sb.Append(block.TrimEnd());
	                used += block.Length;
	            }
	        }

	        if (campaign.Npcs is { Count: > 0 } && used < maxChars)
	        {
	            foreach (var npc in campaign.Npcs.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
	            {
	                if (npc == null || string.IsNullOrWhiteSpace(npc.Name) || string.IsNullOrWhiteSpace(npc.Profile))
	                {
	                    continue;
	                }

	                var header = $"NPC {npc.Name}";
	                var body = TrimToLimit(npc.Profile, 700);
	                var block = $"[{header}]\n{body}\n";
	                if (used + block.Length > maxChars)
	                {
	                    break;
	                }

	                if (sb.Length > 0)
	                {
	                    sb.AppendLine();
	                }

	                sb.Append(block.TrimEnd());
	                used += block.Length;
	            }
	        }

	        return sb.Length == 0 ? null : sb.ToString().Trim();
	    }

	    private static bool IsBareIdentityMessage(string content)
	    {
	        if (string.IsNullOrWhiteSpace(content))
	        {
	            return false;
	        }

	        var trimmed = content.Trim();
	        return TryParseIdentitySelection(trimmed, out _);
	    }

	    private static bool TryApplyIdentitySelectionFromMessage(
	        DndChannelState dndState,
	        DndCampaign campaign,
	        ulong userId,
	        string content,
	        out string activeCharacterName)
	    {
	        activeCharacterName = ResolveActiveCharacterName(dndState, campaign, userId);
	        if (string.IsNullOrWhiteSpace(content) || dndState?.Session == null)
	        {
	            return false;
	        }

	        // Only treat very short "I am X" messages as identity selection to avoid false positives.
	        var trimmed = content.Trim();
	        if (trimmed.Length > 48)
	        {
	            return false;
	        }

	        if (!TryParseIdentitySelection(trimmed, out var proposed))
	        {
	            return false;
	        }

	        // Avoid common false positives like "I'm ready".
	        var low = proposed.Trim().ToLowerInvariant();
	        if (low is "ready" or "here" or "back" or "ok" or "okay" or "good" or "fine" or "done" or "sorry")
	        {
	            return false;
	        }

	        // If the user already has a saved character name, prefer its casing when it matches.
	        var known = campaign?.Characters != null && campaign.Characters.TryGetValue(userId, out var character)
	            ? character?.Name
	            : null;
	        if (!string.IsNullOrWhiteSpace(known) &&
	            string.Equals(known.Trim(), proposed, StringComparison.OrdinalIgnoreCase))
	        {
	            proposed = known.Trim();
	        }

	        if (string.IsNullOrWhiteSpace(proposed))
	        {
	            return false;
	        }

	        if (!string.IsNullOrWhiteSpace(activeCharacterName) &&
	            string.Equals(activeCharacterName, proposed, StringComparison.OrdinalIgnoreCase))
	        {
	            return false;
	        }

	        dndState.Session.ActiveCharacterByUserId ??= new Dictionary<ulong, string>();
	        dndState.Session.ActiveCharacterByUserId[userId] = proposed;
	        activeCharacterName = proposed;
	        return true;
	    }

	    private static bool TryParseIdentitySelection(string content, out string name)
	    {
	        name = null;
	        if (string.IsNullOrWhiteSpace(content))
	        {
	            return false;
	        }

	        var trimmed = content.Trim();
	        string tail = null;
	        if (trimmed.StartsWith("i'm ", StringComparison.OrdinalIgnoreCase))
	        {
	            tail = trimmed[4..];
	        }
	        else if (trimmed.StartsWith("im ", StringComparison.OrdinalIgnoreCase))
	        {
	            tail = trimmed[3..];
	        }
	        else if (trimmed.StartsWith("i am ", StringComparison.OrdinalIgnoreCase))
	        {
	            tail = trimmed[5..];
	        }
	        else if (trimmed.StartsWith("my name is ", StringComparison.OrdinalIgnoreCase))
	        {
	            tail = trimmed[11..];
	        }

	        if (string.IsNullOrWhiteSpace(tail))
	        {
	            return false;
	        }

	        tail = tail.Trim().TrimEnd('.', '!', '?');
	        tail = tail.Trim().Trim('"', '\'');

	        // Limit to a simple name (1-3 words) to prevent accidentally capturing full sentences.
	        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	        if (parts.Length == 0 || parts.Length > 3)
	        {
	            return false;
	        }

	        var candidate = string.Join(" ", parts).Trim();
	        if (candidate.Length is < 2 or > 24)
	        {
	            return false;
	        }

	        name = candidate;
	        return true;
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
            "Act 1 Outline, 5 Quest Hooks, Encounter Plan, and DM Notes. Keep it practical for live play.\n\n" +
            "In the Encounter Plan section, list 3-6 encounters. For each encounter include:\n" +
            "- Encounter name\n" +
            "- Deterministic win condition (pick one): defeat_all_enemies OR survive_rounds (with a number)\n" +
            "- A short encounter prompt the GM can paste into `/gptcli dnd encountercreate`.";
    }

    private static string BuildCreateCampaignPackagePrompt(string campaignName, string userPrompt)
    {
        return
            $"Create a D&D campaign named \"{campaignName}\" from this prompt:\n{userPrompt}\n\n" +
            "Return ONE JSON object only (no markdown, no code fences) with keys:\n" +
            "- campaignMarkdown: string (markdown content for the campaign)\n" +
            "- encounters: array of encounter objects\n\n" +
            "Encounter requirements (deterministic engine friendly):\n" +
            "- 3 to 6 encounters.\n" +
            "- Each encounter must have: name, winCondition, enemies.\n" +
            "- winCondition.type must be either defeat_all_enemies OR survive_rounds.\n" +
            "- If survive_rounds, winCondition.targetRounds must be 1-10.\n" +
            "- enemies must be 1-8 enemies. Include at least one boss across the full campaign.\n" +
            "- Each enemy must include numeric combat stats: armorClass, maxHp, currentHp, initiativeBonus, toHitBonus, damage, attackName.\n" +
            "- damage must be a dice expression like 1d6+2.\n\n" +
            "Use SRD 5.1-compatible content only. Do NOT include copyrighted non-SRD monster stat blocks.\n\n" +
            "JSON schema example:\n" +
            "{\n" +
            "  \"campaignMarkdown\": \"...\",\n" +
            "  \"encounters\": [\n" +
            "    {\n" +
            "      \"name\": \"...\",\n" +
            "      \"winCondition\": { \"type\": \"defeat_all_enemies\", \"targetRounds\": 0, \"narrative\": \"\" },\n" +
            "      \"enemies\": [\n" +
            "        {\"enemyId\":\"goblin-1\",\"name\":\"Goblin Skirmisher\",\"armorClass\":13,\"maxHp\":7,\"currentHp\":7,\"initiativeBonus\":2,\"attackName\":\"Scimitar\",\"toHitBonus\":4,\"damage\":\"1d6+2\",\"notes\":\"\"}\n" +
            "      ]\n" +
            "    }\n" +
            "  ]\n" +
            "}";
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

    private static bool TryExtractLastJsonCodeBlock(string text, out string json, out string textWithoutBlock)
    {
        json = null;
        textWithoutBlock = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var contentStart = text.IndexOf('\n', start);
        if (contentStart < 0)
        {
            return false;
        }

        var end = text.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        json = text[(contentStart + 1)..end].Trim();
        var before = text[..start].TrimEnd();
        var after = text[(end + 3)..].TrimStart();
        textWithoutBlock = string.IsNullOrWhiteSpace(after) ? before : $"{before}\n\n{after}".Trim();
        return !string.IsNullOrWhiteSpace(json);
    }

    private static bool TryParseCharacterStats(string json, out CharacterStats stats)
    {
        stats = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CharacterStats>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed == null)
            {
                return false;
            }

            parsed.AbilityScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            parsed.AbilityMods ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            parsed.SaveBonuses ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            parsed.SkillBonuses ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            parsed.Attacks ??= new List<AttackStats>();

            parsed.Level = parsed.Level <= 0 ? 1 : parsed.Level;
            parsed.ProficiencyBonus = parsed.ProficiencyBonus == 0 ? 2 : parsed.ProficiencyBonus;
            parsed.ArmorClass = Math.Max(1, parsed.ArmorClass);
            parsed.MaxHp = Math.Max(1, parsed.MaxHp);
            if (parsed.CurrentHp <= 0 || parsed.CurrentHp > parsed.MaxHp)
            {
                parsed.CurrentHp = parsed.MaxHp;
            }

            stats = parsed;
            return true;
        }
        catch
        {
            return false;
        }
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

    private static void ParseCheckArgs(string tail, out string label, out string expression, out bool hasExplicitExpression)
    {
        label = null;
        expression = null;
        hasExplicitExpression = false;
        if (string.IsNullOrWhiteSpace(tail))
        {
            return;
        }

        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var last = parts[^1];
        if (parts.Length > 1 &&
            (DiceExpressionRegex.IsMatch(NormalizeCheckExpression(last)) ||
             int.TryParse(last, out _) ||
             (last.StartsWith("+", StringComparison.Ordinal) || last.StartsWith("-", StringComparison.Ordinal))))
        {
            hasExplicitExpression = true;
            expression = last;
            label = string.Join(" ", parts.Take(parts.Length - 1)).Trim();
            return;
        }

        label = tail.Trim();
    }

    private static string ExtractFirstDiceExpression(string damageText)
    {
        if (string.IsNullOrWhiteSpace(damageText))
        {
            return null;
        }

        var match = Regex.Match(damageText, @"(?<expr>\d{0,2}d\d{1,4}(?:[+-]\d+)?)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // Allow raw numbers like "3" or "+3" as a fallback.
            var trimmed = damageText.Trim();
            return int.TryParse(trimmed, out _) ? trimmed : null;
        }

        var expr = match.Groups["expr"].Value;
        return string.IsNullOrWhiteSpace(expr) ? null : NormalizeCheckExpression(expr);
    }

    private static string MakeCriticalDiceExpression(string baseExpr)
    {
        var expr = NormalizeCheckExpression(baseExpr);
        var match = DiceExpressionRegex.Match(expr);
        if (!match.Success)
        {
            return expr;
        }

        var countRaw = match.Groups["count"].Value;
        var sidesRaw = match.Groups["sides"].Value;
        var modifierRaw = match.Groups["modifier"].Value;

        var count = string.IsNullOrWhiteSpace(countRaw) ? 1 : int.Parse(countRaw);
        var sides = int.Parse(sidesRaw);
        var modifier = string.IsNullOrWhiteSpace(modifierRaw) ? 0 : int.Parse(modifierRaw);

        var doubled = Math.Max(1, Math.Min(40, count * 2));
        var mod = modifier == 0 ? string.Empty : (modifier > 0 ? $"+{modifier}" : modifier.ToString());
        return $"{doubled}d{sides}{mod}";
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
        var activeEncounter = state.Session?.ActiveEncounterId;
        activeEncounter = string.IsNullOrWhiteSpace(activeEncounter) ? "none" : activeEncounter;
        var phase = state.Session?.Combat?.Phase ?? CombatPhaseNone;
        var round = state.Session?.Combat?.RoundNumber ?? 0;
	        return
	            $"DND status\n" +
            $"- mode: {state.Mode}\n" +
            $"- active campaign: {active}\n" +
            $"- active encounter: {activeEncounter}\n" +
            $"- combat: phase={phase}, round={round}\n" +
            $"- campaigns: {campaignCount}\n" +
            $"- npc companions (active campaign): {activeNpcCount}\n" +
	            $"- pending overwrite: {pending}\n" +
	            "- mechanics: !roll !check !save !attack !damage !encounter !initiative !pass !skip !endturn";
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
		            "- `/gptcli dnd encountercreate name:<name> prompt:<text>`\n" +
		            "- `/gptcli dnd encounterlist`\n" +
		            "- `/gptcli dnd encounterstart id:<id>`\n" +
		            "- `/gptcli dnd encounterstatus`\n" +
		            "- `/gptcli dnd encounterend`\n" +
		            "- `/gptcli dnd enemyset enemy:<name|id> [ac] [maxhp] [hp] [init] [tohit] [damage] [attack]`\n" +
		            "- `/gptcli dnd ledger [count]`\n" +
		            "- `/gptcli dnd campaignhistory [count]`\n\n" +
		            BuildActionHelpText();
		    }

	    private static string BuildActionHelpText()
	    {
	        return
	            "DND live gameplay\n" +
	            "- On your turn, normal chat like \"I slash the goblin\" will be converted into an action automatically.\n" +
	            "- You can still use explicit !commands for deterministic mechanics.\n" +
	            "- `!ignore` (DM ignores this message)\n" +
	            "- `!roll d20+5`\n" +
	            "- `!check stealth +3`\n" +
	            "- `!save dex +2`\n" +
	            "- `!encounter` (show active encounter)\n" +
	            "- `!targets` (alias of !encounter)\n" +
	            "- `!attack goblin` (auto-roll from your sheet)\n" +
	            "- `!attack goblin d20+6` (manual roll -> then `!damage`)\n" +
	            "- `!damage goblin 1d8+3` (only after a manual `!attack` hit)\n" +
	            "- `!pass` / `!skip` (do nothing this turn)\n" +
	            "- `!skip all` (admin/mod only; skips remaining players)\n" +
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

    private static string FormatSigned(int value)
    {
        return value >= 0 ? $"+{value}" : value.ToString();
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
        public CharacterStats Stats { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class DndNpcMember
    {
        public string Name { get; set; }
        public string Concept { get; set; }
        public string Profile { get; set; }
        public CharacterStats Stats { get; set; }
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

    private sealed class CampaignPackageResponse
    {
        public string CampaignMarkdown { get; set; }
        public List<EncounterDocument> Encounters { get; set; } = new();
    }

	    private sealed class CharacterSheetDocument
	    {
	        public string CampaignDocumentId { get; set; }
	        public string CampaignName { get; set; }
	        public ulong UserId { get; set; }
	        public string Name { get; set; }
	        public string Concept { get; set; }
	        public string Sheet { get; set; }
	        public CharacterStats Stats { get; set; }
	        public DateTime UpdatedUtc { get; set; }
	    }

	    private sealed class NpcSheetDocument
	    {
	        public string CampaignDocumentId { get; set; }
	        public string CampaignName { get; set; }
	        public string Name { get; set; }
	        public string Concept { get; set; }
	        public string Profile { get; set; }
	        public CharacterStats Stats { get; set; }
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

	    private sealed class CharacterStats
	    {
	        public string Name { get; set; }
	        public string Class { get; set; }
	        public int Level { get; set; }
	        public int ProficiencyBonus { get; set; }
	        public int ArmorClass { get; set; }
	        public int MaxHp { get; set; }
	        public int CurrentHp { get; set; }
	        public int Speed { get; set; }
	        public int InitiativeBonus { get; set; }
	        public int PassivePerception { get; set; }
	        public Dictionary<string, int> AbilityScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	        public Dictionary<string, int> AbilityMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	        public Dictionary<string, int> SaveBonuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	        public Dictionary<string, int> SkillBonuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	        public List<AttackStats> Attacks { get; set; } = new();
	        public SpellcastingStats Spellcasting { get; set; }
	    }

	    private sealed class AttackStats
	    {
	        public string Name { get; set; }
	        public int ToHitBonus { get; set; }
	        public string Damage { get; set; }
	        public string Notes { get; set; }
	    }

	    private sealed class SpellcastingStats
	    {
	        public string Ability { get; set; }
	        public int SpellAttackBonus { get; set; }
	        public int SpellSaveDc { get; set; }
	    }

			    private sealed class SessionState
			    {
			        public Dictionary<ulong, int> Initiative { get; set; } = new();
			        public List<ulong> TurnOrder { get; set; } = new();
			        public int CurrentTurnIndex { get; set; }
			        public List<string> Transcript { get; set; } = new();
			        public Dictionary<ulong, string> ActiveCharacterByUserId { get; set; } = new();
			        public string ActiveEncounterId { get; set; }
			        public Dictionary<ulong, PendingHitState> PendingHitsByUserId { get; set; } = new();
			        public CombatState Combat { get; set; } = new();
			    }

			    private sealed class CombatState
			    {
			        public string EncounterId { get; set; }
			        public string Phase { get; set; } = CombatPhaseNone;
			        public int RoundNumber { get; set; }
			        public Dictionary<ulong, int> PcInitiative { get; set; } = new();
			        public Dictionary<string, int> EnemyInitiative { get; set; } = new(StringComparer.OrdinalIgnoreCase);
			        public List<ulong> PcActedThisRound { get; set; } = new(); // used as a "done list" even with turn order gating
			        public List<ulong> PcTurnOrder { get; set; } = new();
			        public int CurrentPcTurnIndex { get; set; }
			        public bool PlayersGoFirst { get; set; }
			    }

			    private sealed class PendingHitState
			    {
			        public string EncounterId { get; set; }
			        public string TargetEnemyId { get; set; }
		        public string TargetName { get; set; }
		        public bool Hit { get; set; }
		        public bool Critical { get; set; }
		        public string AttackExpression { get; set; }
		        public int AttackTotal { get; set; }
		        public DateTime ExpiresUtc { get; set; }
		    }

		    private sealed class EncounterDocument
		    {
		        public string EncounterId { get; set; }
		        public string CampaignName { get; set; }
		        public string Name { get; set; }
		        public string Status { get; set; } = "prepared";
		        public EncounterWinCondition WinCondition { get; set; } = new();
		        public DateTime CreatedUtc { get; set; }
		        public DateTime UpdatedUtc { get; set; }
		        public List<EncounterEnemy> Enemies { get; set; } = new();
		    }

		    private sealed class EncounterWinCondition
		    {
		        // Supported deterministic types:
		        // - defeat_all_enemies
		        // - survive_rounds
		        public string Type { get; set; } = "defeat_all_enemies";
		        public int TargetRounds { get; set; }
		        public string Narrative { get; set; }
		    }

			    private sealed class EncounterEnemy
			    {
			        public string EnemyId { get; set; }
			        public string Name { get; set; }
			        public int ArmorClass { get; set; }
			        public int MaxHp { get; set; }
			        public int CurrentHp { get; set; }
			        public int InitiativeBonus { get; set; }
			        public string AttackName { get; set; }
			        public int ToHitBonus { get; set; }
			        public string Damage { get; set; }
			        public string Notes { get; set; }
			    }
}
