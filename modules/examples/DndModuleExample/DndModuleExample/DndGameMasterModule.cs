using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Chat.Discord.Modules;
using GPT.CLI.Chat.Dnd;
using Betalgo.Ranul.OpenAI.ObjectModels;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;

namespace DndModuleExample;

public sealed class DndGameMasterModule : FeatureModuleBase, IModuleEnablementHooks
{
    public override string Id => "dnd";
    public override string Name => "D&D (Simplified)";

    private const string ModeOff = "off";
    private const string ModeDraft = "draft";
    private const string ModeGame = "game";

    private const int DiscordMessageLimit = 1800;
    private const int MaxCampaignChars = 24000;
    private const int CampaignCreateTimeoutSeconds = 60;
    private const int ResponsesLoopMaxRounds = 4;
    private const int DraftPendingActionTtlMinutes = 5;
    private const string PendingActionCampaignCreate = "campaign-create";
    private const string PendingActionDraftUpdate = "draft-update";
    private const string PendingActionPartyEdit = "party-edit";
    private const string RouteToolCampaignCreate = "dnd_route_campaign_create";
    private const string RouteToolDraftUpdate = "dnd_route_draft_update";
    private const string RouteToolPartyEdit = "dnd_route_party_edit";
    private const string RouteToolSheetCreate = "dnd_route_sheet_create";
    private const string RouteToolPassTimeout = "dnd_route_passtimeout";
    private const string RouteToolChatReply = "dnd_route_chat_reply";
    private const string RouteToolClarify = "dnd_route_clarify";
    private const string GameNarrationModeLlm = "llm";
    private const string GameNarrationModeDeterministic = "deterministic";
    private const string GameNarrationModeOff = "off";

    private static readonly HttpClient ResponsesHttpClient = new();

    private enum DndLogLevel
    {
        None = 0,
        Error = 1,
        Warning = 2,
        Information = 3,
        Debug = 4,
        Trace = 5
    }

    private sealed record DndLogPolicy(DndLogLevel ConsoleMinLevel, DndLogLevel DiscordMinLevel);
    private sealed record DraftStatusUpdate(string Key, string Message, bool Important);
    private sealed record DraftIntentRouterDispatchResult(bool Handled, string Reply = null, string Error = null);

    private sealed class DraftPartyEditIntent
    {
        public bool WantsAdd { get; set; }
        public bool WantsRemove { get; set; }
        public List<ulong> MentionedUserIds { get; set; } = new();
        public List<string> NpcActorIds { get; set; } = new();
        public bool IsAmbiguous => WantsAdd && WantsRemove;
        public int TargetCount => (MentionedUserIds?.Count ?? 0) + (NpcActorIds?.Count ?? 0);
        public bool HasTargets => TargetCount > 0;
    }

    private static readonly Regex BotMentionRegexTemplate =
        new(@"<@!?(?<id>\d+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<ulong, DndLiteChannelState> _stateByChannel = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private readonly ConcurrentDictionary<ulong, RandomDiceRoller> _diceByChannel = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _tickCtsByChannel = new();
    private readonly ConcurrentDictionary<ulong, Task> _tickTasksByChannel = new();
    private readonly ConcurrentDictionary<ulong, (DateTime SentUtc, string Key)> _lastStatusUpdateByChannel = new();

    // Used by background tick loops (no message context).
    private DiscordModuleContext _moduleContext;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public override Task InitializeAsync(DiscordModuleContext context, CancellationToken cancellationToken)
    {
        _moduleContext = context;
        return Task.CompletedTask;
    }

    public override async Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(
        DiscordModuleContext context,
        SocketMessage message,
        InstructionGPT.ChannelState channel,
        CancellationToken cancellationToken)
    {
        if (message == null || channel == null)
        {
            return Array.Empty<ChatMessage>();
        }

        // When the module is disabled, it should be totally invisible to the LLM (no preambles/tools).
        if (!InstructionGPT.IsModuleEnabled(channel, Id))
        {
            return Array.Empty<ChatMessage>();
        }

        // Keep the global model aware of DnD semantics, but keep it mode-safe.
        var st = await GetOrLoadStateAsync(channel, cancellationToken);
        var mode = NormalizeMode(st?.Mode);

        if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                BuildModulePreambleMessage(
                    "Sub-Prime Directive:\n" +
                    "- The DnD module is enabled, but DnD mode is OFF.\n" +
                    "- Do not interpret messages as DnD gameplay or drafting unless the user turns DnD mode on.\n" +
                    "- When enabled, this module can help draft adventures/campaigns (draft) and run encounters (game).\n" +
                    "When To Engage (triggers):\n" +
                    "- `/gptcli dnd mode value:draft` (draft/worldbuilding)\n" +
                    "- `/gptcli dnd mode value:game` (gameplay)\n")
            };
        }

        if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                BuildModulePreambleMessage(
                    "Sub-Prime Directive (DRAFT / GM Prep):\n" +
                    "- Draft mode is the preparatory phase a GM would normally do before play: define premise/tone, write hooks, outline scenes, sketch key NPCs, pick encounters, and set up the party.\n" +
                    "- Default to conversational GM collaboration first: brainstorm, refine, and ask concise follow-up questions.\n" +
                    "- Only call `gptcli_dnd_*` functions when the user clearly asks for a persistent state change.\n" +
                    "- Do not finalize/save the campaign into the catalog until game mode.\n" +
                    "When To Engage (triggers):\n" +
                    "- new campaign, rewrite/update draft, add/remove party members, create/show NPC/PC sheets, list encounters/campaigns, set pass timeout\n")
            };
        }

        return new[]
        {
            BuildModulePreambleMessage(
                "Sub-Prime Directive (GAME / Play):\n" +
                "- Game mode is live play: narrate scenes, run encounters, track turns, and respond as GM.\n" +
                "- The user may describe actions in natural language; your job is to call the relevant `gptcli_dnd_*` functions for mechanics/state (encounters, rolls, attacks/casts, status, timeouts).\n" +
                "- Do not require `!` commands for gameplay; plain language like \"I attack the boss\" should work.\n" +
                "- Prefer Discord-friendly formatting for readability: bold beats, short paragraphs, and occasional emojis.\n" +
                "- Keep responses short, in-character, and always drive toward the next playable decision.\n" +
                "When To Engage (triggers):\n" +
                "- start/begin, encounter, attack/cast, roll/initiative, turn/pass, targets, status\n")
        };
    }

    public override IReadOnlyList<GptCliFunction> GetGptCliFunctions(DiscordModuleContext context)
    {
        const string dndGroupDescription = "Simplified D&D campaign + encounter controls";

        return new List<GptCliFunction>
        {
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
                    new GptCliParamSpec("value", GptCliParamType.String, "off, draft, or game", Required: true,
                        Choices: new[]
                        {
                            new GptCliParamChoice("off", ModeOff),
                            new GptCliParamChoice("draft", ModeDraft),
                            new GptCliParamChoice("game", ModeGame)
                        }),
                    new GptCliParamSpec("campaign", GptCliParamType.String, "Campaign name (optional)")
                },
                ExecuteAsync = ExecuteModeAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignfinalize",
                ModuleId = Id,
                Description = "Finalize the active draft campaign into the catalog (and run roster) so game mode can play it",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignfinalize"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name (optional; defaults to active campaign)"),
                    new GptCliParamSpec("overwrite", GptCliParamType.Boolean, "Overwrite existing saved campaign if it exists (default false)")
                },
                ExecuteAsync = ExecuteCampaignFinalizeAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_draftupdate",
                ModuleId = Id,
                Description = "Update (rewrite) the current draft campaign markdown by applying a modification prompt to the existing draft story",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "draftupdate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name (optional; defaults to active campaign)"),
                    new GptCliParamSpec("prompt", GptCliParamType.String, "Modification prompt (what to change/add/remove)", Required: true)
                },
                ExecuteAsync = ExecuteDraftUpdateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_partyshow",
                ModuleId = Id,
                Description = "Show party roster for the active campaign (draft or game)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "partyshow"),
                ExecuteAsync = ExecutePartyShowAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_partyaddpc",
                ModuleId = Id,
                Description = "Add an existing Discord user to the draft party roster (does not create a sheet)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "partyaddpc"),
                Parameters = new[] { new GptCliParamSpec("user", GptCliParamType.User, "Discord user", Required: true) },
                ExecuteAsync = ExecutePartyAddPcAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_partyremovepc",
                ModuleId = Id,
                Description = "Remove a Discord user from the draft party roster",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "partyremovepc"),
                Parameters = new[] { new GptCliParamSpec("user", GptCliParamType.User, "Discord user", Required: true) },
                ExecuteAsync = ExecutePartyRemovePcAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_partyaddnpc",
                ModuleId = Id,
                Description = "Add an existing NPC profile (npc:...) to the draft party roster",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "partyaddnpc"),
                Parameters = new[] { new GptCliParamSpec("id", GptCliParamType.String, "NPC actor id (npc:...)", Required: true) },
                ExecuteAsync = ExecutePartyAddNpcAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_partyremovenpc",
                ModuleId = Id,
                Description = "Remove an NPC from the party roster (keeps the sheet file)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "partyremovenpc"),
                Parameters = new[] { new GptCliParamSpec("id", GptCliParamType.String, "NPC actor id (npc:...)", Required: true) },
                ExecuteAsync = ExecutePartyRemoveNpcAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaigncreate",
                ModuleId = Id,
                Description = "Build a draft campaign (encounters + mobs) from a prompt (finalize on game)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaigncreate"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name (optional; defaults to active campaign)"),
                    new GptCliParamSpec("prompt", GptCliParamType.String, "Campaign creation prompt", Required: true)
                },
                ExecuteAsync = ExecuteCampaignCreateAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignlist",
                ModuleId = Id,
                Description = "List saved campaigns for this channel",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignlist"),
                ExecuteAsync = ExecuteCampaignListAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_campaignstart",
                ModuleId = Id,
                Description = "Set the active campaign (does not start an encounter)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "campaignstart"),
                Parameters = new[]
                {
                    new GptCliParamSpec("name", GptCliParamType.String, "Campaign name (from campaignlist)", Required: true)
                },
                ExecuteAsync = ExecuteCampaignStartAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_charactercreate",
                ModuleId = Id,
                Description = "Create your simplified character sheet",
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
                Description = "Show a simplified character sheet",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "charactershow"),
                Parameters = new[] { new GptCliParamSpec("user", GptCliParamType.User, "Optional user id") },
                ExecuteAsync = ExecuteCharacterShowAsync
            },

            new()
            {
                ToolName = "gptcli_dnd_npccreate",
                ModuleId = Id,
                Description = "Create an NPC character sheet (stored outside campaigns)",
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
                Description = "Show an NPC character sheet",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npcshow"),
                Parameters = new[] { new GptCliParamSpec("id", GptCliParamType.String, "NPC actor id (npc:...)", Required: true) },
                ExecuteAsync = ExecuteNpcShowAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_npcremove",
                ModuleId = Id,
                Description = "Remove an NPC from the party roster (keeps the sheet file)",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "npcremove"),
                Parameters = new[] { new GptCliParamSpec("id", GptCliParamType.String, "NPC actor id (npc:...)", Required: true) },
                ExecuteAsync = ExecuteNpcRemoveAsync
            },

	            new()
	            {
	                ToolName = "gptcli_dnd_liveconfig",
	                ModuleId = Id,
	                Description = "Configure game-mode encounter ticking/timeouts (module-side)",
	                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "liveconfig"),
	                Parameters = new[]
	                {
	                    new GptCliParamSpec("tick_seconds", GptCliParamType.Integer, "Tick interval seconds (1-30)", MinInt: 1, MaxInt: 30),
	                    new GptCliParamSpec("player_turn_timeout_seconds", GptCliParamType.Integer, "Player turn timeout seconds (5-3600)", MinInt: 5, MaxInt: 3600),
	                    new GptCliParamSpec("encounter_timeout_seconds", GptCliParamType.Integer, "Encounter timeout seconds (30-3600)", MinInt: 30, MaxInt: 3600),
	                    new GptCliParamSpec("npc_autoplay", GptCliParamType.Boolean, "Autoplay NPC turns"),
	                    new GptCliParamSpec("autoroll_policy", GptCliParamType.String, "npc-only, all, or never",
	                        Choices: new[]
	                        {
                            new GptCliParamChoice("npc-only", "npc-only"),
                            new GptCliParamChoice("all", "all"),
                            new GptCliParamChoice("never", "never")
                        }),
                    new GptCliParamSpec("npc_flavor", GptCliParamType.Boolean, "Generate 1-line NPC flavor via LLM")
	                },
	                ExecuteAsync = ExecuteLiveConfigAsync
	            },
	            new()
	            {
	                ToolName = "gptcli_dnd_passtimeout",
	                ModuleId = Id,
	                Description = "Set the auto-pass timeout (player turn timeout) for this campaign; defaults to 30 minutes",
	                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "passtimeout"),
	                Parameters = new[]
	                {
	                    new GptCliParamSpec("minutes", GptCliParamType.Integer, "Minutes until a player's turn auto-passes (default 30)", MinInt: 1, MaxInt: 60),
	                    new GptCliParamSpec("seconds", GptCliParamType.Integer, "Seconds until a player's turn auto-passes (overrides minutes; 5-3600)", MinInt: 5, MaxInt: 3600)
	                },
	                ExecuteAsync = ExecutePassTimeoutAsync
	            },

            new()
            {
                ToolName = "gptcli_dnd_encounterlist",
                ModuleId = Id,
                Description = "List encounters (templates) for the active campaign",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterlist"),
                ExecuteAsync = ExecuteEncounterListAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_encounterstart",
                ModuleId = Id,
                Description = "Start an encounter from a template",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "encounterstart"),
                Parameters = new[] { new GptCliParamSpec("id", GptCliParamType.String, "Encounter template id (from encounterlist)", Required: true) },
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

            // Game-mode mechanics as tools so natural language can drive combat without "!" commands.
            new()
            {
                ToolName = "gptcli_dnd_attack",
                ModuleId = Id,
                Description = "In game mode, make a basic attack against a target (enemy id or name)",
                Parameters = new[] { new GptCliParamSpec("target", GptCliParamType.String, "Enemy id or enemy name", Required: true) },
                ExecuteAsync = ExecuteAttackAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_cast",
                ModuleId = Id,
                Description = "In game mode, cast a basic spell against a target (enemy id or name)",
                Parameters = new[] { new GptCliParamSpec("target", GptCliParamType.String, "Enemy id or enemy name", Required: true) },
                ExecuteAsync = ExecuteCastAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_pass",
                ModuleId = Id,
                Description = "In game mode, pass your turn",
                ExecuteAsync = ExecutePassAsync
            },
            new()
            {
                ToolName = "gptcli_dnd_rollall",
                ModuleId = Id,
                Description = "In game mode, roll all pending rolls (initiative/attacks/damage)",
                ExecuteAsync = ExecuteRollAllAsync
            },

            new()
            {
                ToolName = "gptcli_dnd_ledger",
                ModuleId = Id,
                Description = "Show campaign ledger entries",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.GroupSubCommand, "dnd", dndGroupDescription, "ledger"),
                Parameters = new[] { new GptCliParamSpec("count", GptCliParamType.Integer, "Number of entries (1-100)", MinInt: 1, MaxInt: 100) },
                ExecuteAsync = ExecuteLedgerAsync
            }
        };
    }

    public async Task OnModuleEnabledChangedAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        IMessageChannel channel,
        bool enabled,
        CancellationToken cancellationToken)
    {
        // Only disable needs side effects today.
        if (enabled)
        {
            return;
        }

        // Stop any live tick loop.
        if (channel != null)
        {
            StopTickLoop(channel.Id);
        }

        if (channelState == null)
        {
            return;
        }

        try
        {
            var st = await GetOrLoadStateAsync(channelState, cancellationToken);
            if (st != null)
            {
                st.Mode = ModeOff;
                if (st.ModuleMutedBot)
                {
                    channelState.Options.Muted = st.PreviousBotMuted;
                    st.ModuleMutedBot = false;
                }
                await SaveStateAsync(channelState, st, cancellationToken);
            }
        }
        catch
        {
            // ignore
        }
    }

    public override async Task OnMessageReceivedAsync(DiscordModuleContext context, SocketMessage message, CancellationToken cancellationToken)
    {
        if (context == null || message == null || message.Author.IsBot || message.Author.Id == context.Client.CurrentUser.Id)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var content = (message.Content ?? string.Empty).Trim();
        var isTagged = message.MentionedUsers.Any(u => u.Id == context.Client.CurrentUser.Id);

        // Lightweight message tracing to make it obvious when we're ignoring vs processing.
        // Keep previews short and single-line.
        var preview = content.Replace('\n', ' ').Replace('\r', ' ');
        Console.WriteLine(
            $"[dnd] rx: channel={message.Channel?.Id} author={message.Author?.Username}({message.Author?.Id}) tagged={isTagged} len={content.Length} \"{preview}\"");

        var channelState = context.Host.GetOrCreateChannelState(message.Channel);
        if (message.Channel is IGuildChannel guildChannel)
        {
            context.Host.EnsureChannelStateMetadata(channelState, guildChannel);
        }

        if (!InstructionGPT.IsModuleEnabled(channelState, Id))
        {
            Console.WriteLine($"[dnd] ignore: module disabled (channel={message.Channel?.Id})");
            return;
        }

        if (!context.Host.IsChannelGuildMatch(channelState, message.Channel, "dnd-message"))
        {
            Console.WriteLine($"[dnd] ignore: guild mismatch (channel={message.Channel?.Id})");
            return;
        }

        var dndState = await GetOrLoadStateAsync(channelState, cancellationToken);

        if (string.Equals(dndState.Mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            // When off: this module does not process messages at all.
            // Re-enabling is done via `/gptcli dnd mode value:draft|game` (slash) or via the core mention-tool router.
            Console.WriteLine($"[dnd] ignore: mode=off (channel={message.Channel?.Id})");
            return;
        }

        if (string.Equals(dndState.Mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[dnd] processing: mode=draft tagged={isTagged} (channel={message.Channel?.Id})");
            // In draft mode: treat *all* untagged messages as drafting statements and route to tools/chat.
            // If the user tags the bot, core tool-routing may also run; prefer users NOT tag in draft mode.
            if (!isTagged)
            {
                // If the core chat bot is disabled for this channel, we still want conversation continuity
                // for DnD draft/game LLM calls. Record user messages ourselves in that case.
                if (TryRecordUserMessageWhenCoreDisabled(channelState, message, content))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }

                // First: handle pending draft confirmations, if any.
                if (await TryHandlePendingDraftActionAsync(context, channelState, message, dndState, cancellationToken))
                {
                    return;
                }

                var stripped = StripBotMentions(content, context.Client.CurrentUser.Id);
                var intentRouterEnabled = IsDraftIntentRouterEnabled(context);
                if (intentRouterEnabled)
                {
                    var routed = await TryHandleDraftIntentRouterAsync(
                        context,
                        channelState,
                        message,
                        dndState,
                        stripped,
                        cancellationToken);
                    if (routed)
                    {
                        return;
                    }

                    if (!IsDraftIntentRouterLegacyFallbackEnabled(context))
                    {
                        Console.WriteLine($"[dnd] draft-route: router unhandled and legacy fallback disabled (channel={message.Channel?.Id})");
                        var fallbackHandled = await TryHandleAutoRoutedMessageAsync(
                            context,
                            channelState,
                            message,
                            dndState,
                            cancellationToken,
                            allowToolCalls: false);
                        if (fallbackHandled)
                        {
                            return;
                        }

                        var fallback = BuildDraftConversationalFallback(stripped);
                        try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> {fallback}"); } catch { }
                        return;
                    }

                    Console.WriteLine($"[dnd] draft-route: router unhandled, using legacy fallback (channel={message.Channel?.Id})");
                }

                var partyIntent = AnalyzeDraftPartyEditIntent(context, message, stripped);
                var chatFirstEnabled = IsDraftChatFirstEnabled(context);
                var confirmRiskyEditsEnabled = IsDraftConfirmRiskyEditsEnabled(context);
                var conversationOnly = chatFirstEnabled && !LooksLikeExplicitDraftMutationIntent(stripped, partyIntent);

                Console.WriteLine(
                    $"[dnd] draft-route: chatFirst={chatFirstEnabled} confirmRisky={confirmRiskyEditsEnabled} conversationOnly={conversationOnly} " +
                    $"partyTargets={partyIntent.TargetCount} wantsAdd={partyIntent.WantsAdd} wantsRemove={partyIntent.WantsRemove} " +
                    $"(channel={message.Channel?.Id})");

                // Deterministic campaign creation: avoid LLM tool-choice ambiguity for explicit campaign create requests.
                if (TryDetectCampaignCreateIntent(stripped, out var extractedCampaignName, out var wantsOverwrite))
                {
                    var reqId = Guid.NewGuid().ToString("n")[..8];
                    extractedCampaignName = string.IsNullOrWhiteSpace(extractedCampaignName)
                        ? (dndState.ActiveCampaignName ?? "default")
                        : extractedCampaignName.Trim();

                    Console.WriteLine(
                        $"[dnd] draft-create[{reqId}]: intent=yes campaign=\"{extractedCampaignName}\" overwriteHint={wantsOverwrite} msgLen={stripped.Length} (channel={message.Channel?.Id})");

                    var existingPath = ResolveDraftCampaignPath(channelState, extractedCampaignName);
                    var exists = false;
                    try { exists = File.Exists(existingPath); } catch { exists = false; }

                    if (exists && !wantsOverwrite)
                    {
                        Console.WriteLine($"[dnd] draft-create[{reqId}]: draft exists; requesting confirmation path={existingPath}");
                        await SetPendingDraftActionAsync(
                            channelState,
                            dndState,
                            new PendingDraftActionRequest
                            {
                                ActionType = PendingActionCampaignCreate,
                                ArgumentsJson = JsonSerializer.Serialize(new { name = extractedCampaignName, prompt = stripped }),
                                RequestedByUserId = message.Author.Id,
                                RequestedUtc = DateTime.UtcNow,
                                ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                                Summary = $"overwrite draft campaign \"{extractedCampaignName}\""
                            },
                            cancellationToken);

                        try
                        {
                            await message.Channel.SendMessageAsync(
                                $"<@{message.Author.Id}> Draft \"{extractedCampaignName}\" already exists. Reply `confirm overwrite` to replace it, or `cancel`.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[dnd] send failed (overwrite confirm prompt): {ex.GetType().Name} {ex.Message}");
                        }
                        return;
                    }

                    var argsJson = JsonSerializer.Serialize(new
                    {
                        name = extractedCampaignName,
                        prompt = stripped
                    });

                    try
                    {
                        Console.WriteLine($"[dnd] draft-create[{reqId}]: invoking ExecuteCampaignCreateAsync");
                        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
                        var res = await ExecuteCampaignCreateAsync(execCtx, argsJson, cancellationToken);
                        if (res is { Handled: true })
                        {
                            try
                            {
	                                var reply = string.IsNullOrWhiteSpace(res.Response)
	                                    ? $"<@{message.Author.Id}> (campaign created)"
	                                    : $"<@{message.Author.Id}>\n{res.Response.Trim()}";
                                Console.WriteLine($"[dnd] draft-create[{reqId}]: responding handled=true respLen={(res.Response ?? string.Empty).Length} replyLen={reply.Length}");
                                Console.WriteLine($"[dnd] draft-create[{reqId}]: reply={reply}");
                                await SendChunkedAsync(message.Channel, reply);
                                if (TryRecordAssistantReply(channelState, reply))
                                {
                                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[dnd] send failed (campaign create reply): {ex.GetType().Name} {ex.Message}");
                            }
                            return;
                        }
                        Console.WriteLine($"[dnd] draft-create[{reqId}]: ExecuteCampaignCreateAsync handled=false");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[dnd] draft-create: deterministic campaign create failed: {ex.GetType().Name} {ex.Message}");
                        // Fall through to LLM router as a last resort.
                    }
                }

                // Confirm destructive or bulk party changes before writing state.
                if (confirmRiskyEditsEnabled && IsRiskyDraftPartyEdit(partyIntent))
                {
                    var op = partyIntent.WantsRemove ? "remove" : "add";
                    var argsJson = JsonSerializer.Serialize(new
                    {
                        add = partyIntent.WantsAdd,
                        remove = partyIntent.WantsRemove,
                        userIds = partyIntent.MentionedUserIds,
                        npcIds = partyIntent.NpcActorIds
                    });
                    await SetPendingDraftActionAsync(
                        channelState,
                        dndState,
                        new PendingDraftActionRequest
                        {
                            ActionType = PendingActionPartyEdit,
                            ArgumentsJson = argsJson,
                            RequestedByUserId = message.Author.Id,
                            RequestedUtc = DateTime.UtcNow,
                            ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                            Summary = $"{op} {partyIntent.TargetCount} party member(s)"
                        },
                        cancellationToken);
                    try
                    {
                        await message.Channel.SendMessageAsync(
                            $"<@{message.Author.Id}> This will {op} {partyIntent.TargetCount} party member(s). Reply `confirm` to apply, or `cancel`.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[dnd] send failed (party confirm prompt): {ex.GetType().Name} {ex.Message}");
                    }
                    return;
                }

                // Draft mode: high-confidence deterministic party edits (mentions + npc: ids).
                if (await TryHandleDeterministicDraftPartyEditsAsync(context, channelState, message, dndState, stripped, cancellationToken))
                {
                    return;
                }

                // Confirm broad story rewrites before writing state in chat-first mode.
                if (confirmRiskyEditsEnabled && LooksLikeDraftUpdateIntent(stripped))
                {
                    var activeCampaign = dndState.ActiveCampaignName ?? "default";
                    var draftPath = ResolveDraftCampaignPath(channelState, activeCampaign);
                    var draftExists = false;
                    try { draftExists = File.Exists(draftPath); } catch { draftExists = false; }
                    if (draftExists)
                    {
                        await SetPendingDraftActionAsync(
                            channelState,
                            dndState,
                            new PendingDraftActionRequest
                            {
                                ActionType = PendingActionDraftUpdate,
                                ArgumentsJson = JsonSerializer.Serialize(new { name = activeCampaign, prompt = stripped }),
                                RequestedByUserId = message.Author.Id,
                                RequestedUtc = DateTime.UtcNow,
                                ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                                Summary = $"rewrite draft campaign \"{activeCampaign}\""
                            },
                            cancellationToken);
                        try
                        {
                            await message.Channel.SendMessageAsync(
                                $"<@{message.Author.Id}> I can update the draft story now. Reply `confirm` to apply it, or `cancel`.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[dnd] send failed (draft update confirm prompt): {ex.GetType().Name} {ex.Message}");
                        }
                        return;
                    }
                }

                // Draft mode: high-confidence deterministic story edits ("rewrite/change/update..." etc).
                if (await TryHandleDeterministicDraftUpdateAsync(context, channelState, message, dndState, stripped, cancellationToken))
                {
                    return;
                }

                // Stateful draft operations should stay deterministic (no general LLM state manager).
                if (!conversationOnly)
                {
                    if (await TryHandleDeterministicDraftSheetGenerationFromMessageAsync(
                            context, channelState, message, dndState, stripped, cancellationToken))
                    {
                        return;
                    }

                    if (await TryHandleDeterministicDraftPassTimeoutFromMessageAsync(
                            context, channelState, message, dndState, stripped, cancellationToken))
                    {
                        return;
                    }

                    if (TryBuildDraftStateClarification(stripped, partyIntent, out var clarification))
                    {
                        try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> {clarification}"); } catch { }
                        return;
                    }

                    try
                    {
                        await message.Channel.SendMessageAsync(
                            $"<@{message.Author.Id}> I can apply draft state changes directly, but I need explicit details. " +
                            "For party edits, mention users and/or use `npc:...` ids. For other actions, use `/gptcli dnd ...`.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[dnd] send failed (draft deterministic hint): {ex.GetType().Name} {ex.Message}");
                    }
                    return;
                }

                // Conversational-only draft chat (no tool calls).
                var handled = await TryHandleAutoRoutedMessageAsync(
                    context,
                    channelState,
                    message,
                    dndState,
                    cancellationToken,
                    allowToolCalls: false);
                Console.WriteLine($"[dnd] auto-route: handled={handled} mode=draft conversationOnly=true (channel={message.Channel?.Id})");
                if (handled)
                {
                    return;
                }

                var draftFallback = BuildDraftConversationalFallback(stripped);
                try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> {draftFallback}"); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[dnd] send failed (draft fallback): {ex.GetType().Name} {ex.Message}");
                }
                return;
            }

            // Fallback: tagged campaign Q&A.
            if (isTagged)
            {
                await TryHandleTaggedCampaignChatAsync(context, channelState, message, dndState, cancellationToken);
            }
            return;
        }

        if (content.StartsWith("!", StringComparison.Ordinal))
        {
            // Bang commands only run in game mode.
            if (!string.Equals(dndState.Mode, ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

        Console.WriteLine($"[dnd] processing: game bang command (channel={message.Channel?.Id})");

            // Serialize combat mutations.
            var lockHandle = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
            await lockHandle.WaitAsync(cancellationToken);
            try
            {
                EnsureTickLoopRunning(message.Channel.Id);

                var handled = await TryHandleBangCommandAsync(context, channelState, message, dndState, content, cancellationToken);
                if (handled.handled)
                {
                    if (handled.stateChanged)
                    {
                        await SaveStateAsync(channelState, dndState, cancellationToken);
                    }

                    return;
                }
            }
            finally
            {
                lockHandle.Release();
            }
        }

        if (string.Equals(dndState.Mode, ModeGame, StringComparison.OrdinalIgnoreCase) && !isTagged)
        {
            Console.WriteLine($"[dnd] processing: mode=game untagged (channel={message.Channel?.Id})");
            // Game mode: treat untagged messages as gameplay/roleplay and route to tools/chat.
            if (TryRecordUserMessageWhenCoreDisabled(channelState, message, content))
            {
                try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
            }
            if (await TryHandleDeterministicGameStartAsync(context, channelState, message, dndState, cancellationToken))
            {
                return;
            }

            var naturalHandled = false;
            var lockHandle = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
            await lockHandle.WaitAsync(cancellationToken);
            try
            {
                EnsureTickLoopRunning(message.Channel.Id);
                naturalHandled = await TryHandleNaturalGameActionAsync(context, channelState, message, dndState, cancellationToken);
            }
            finally
            {
                lockHandle.Release();
            }
            if (naturalHandled)
            {
                return;
            }

            var handled = await TryHandleAutoRoutedMessageAsync(context, channelState, message, dndState, cancellationToken);
            Console.WriteLine($"[dnd] auto-route: handled={handled} mode=game (channel={message.Channel?.Id})");
            if (handled)
            {
                return;
            }

            // Avoid silent failures in game mode; keep it brief.
            try
            {
                await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Sorry, I couldn't process that right now. Try again.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dnd] send failed (game fallback): {ex.GetType().Name} {ex.Message}");
            }
            return;
        }

        if (!isTagged)
        {
            return;
        }

        if (string.Equals(dndState.Mode, ModeGame, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[dnd] processing: mode=game tagged (channel={message.Channel?.Id})");
            // Serialize combat mutations.
            var lockHandle = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
            await lockHandle.WaitAsync(cancellationToken);
            try
            {
                EnsureTickLoopRunning(message.Channel.Id);

                // Hybrid input: when tagged, allow a tiny action-router to convert the message into a single deterministic action.
                // If it doesn't match a combat action, fall back to campaign-aware chat below.
                var handled = await TryHandleNaturalGameActionAsync(context, channelState, message, dndState, cancellationToken);
                if (handled)
                {
                    return;
                }
            }
            finally
            {
                lockHandle.Release();
            }
        }

        // Tagged campaign Q&A: use saved campaign JSON as context so the model can describe the campaign and encounters.
        await TryHandleTaggedCampaignChatAsync(context, channelState, message, dndState, cancellationToken);
    }

    private async Task<GptCliExecutionResult> RejectWhenOffAsync(GptCliExecutionContext ctx, CancellationToken ct)
    {
        var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
        if (!string.Equals(st.Mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new GptCliExecutionResult(true, "DND mode is `off`. Use `/gptcli dnd mode value:draft` or `/gptcli dnd mode value:game`.", false);
    }

    private async Task<bool> TryHandleAutoRoutedMessageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct,
        bool allowToolCalls = true)
    {
        if (context == null || channelState == null || message == null)
        {
            return false;
        }

        var raw = message.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        // Offer only this module's DnD tools to avoid cross-module surprises,
        // and filter the toolset by mode (draft != game toolset).
        var mode = NormalizeMode(dndState?.Mode);
        var compactDraftChatOnly = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) && !allowToolCalls;
        // In GAME mode, mode changes must be done via slash command to avoid accidental in-character triggers.
        var allowModeTool = !string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) && LooksLikeModeChangeIntent(text);
        var functions = allowToolCalls
            ? GetGptCliFunctions(context)
                .Where(f => f?.ExecuteAsync != null &&
                            !string.IsNullOrWhiteSpace(f.ToolName) &&
                            f.ToolName.StartsWith("gptcli_dnd_", StringComparison.OrdinalIgnoreCase) &&
                            IsDndToolAllowedForMode(f.ToolName, mode) &&
                            (allowModeTool || !string.Equals(f.ToolName, "gptcli_dnd_mode", StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : new List<GptCliFunction>();
        if (allowToolCalls && functions.Count == 0)
        {
            return false;
        }

        var tools = allowToolCalls ? functions.Select(f => f.ToToolDefinition()).ToList() : new List<ToolDefinition>();
        var byName = functions
            .Where(f => !string.IsNullOrWhiteSpace(f.ToolName))
            .GroupBy(f => f.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var activeCampaign = dndState?.ActiveCampaignName ?? "default";

        var userCtx = new StringBuilder();
        userCtx.AppendLine($"DnD mode: {mode}");
        userCtx.AppendLine($"Active campaign: {activeCampaign}");

        if (!compactDraftChatOnly)
        {
            // Include party roster in-context so the model can reliably map "remove Kain" -> npc:... without
            // needing a multi-turn tool loop (tool calls are single-pass).
            try
            {
                await AppendPartyRosterContextAsync(userCtx, channelState, mode, activeCampaign, ct);
            }
            catch
            {
                // ignore
            }
        }

        if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
        {
            // Add just enough context for target selection.
            var enc = await GetActiveEncounterSnapshotAsync(channelState, activeCampaign, ct);
            if (enc != null)
            {
                userCtx.AppendLine("Encounter:");
                userCtx.AppendLine($"phase={enc.Phase} round={enc.RoundNumber} current={enc.CurrentActorId}");
                userCtx.AppendLine(RenderTargets(enc));
            }

            // Also include encounter template ids so the model can pick one to start without extra tool loops.
            // (Tool-calls are single-pass; the model does not see tool results before choosing the next call.)
            try
            {
                var campaign = await LoadCampaignAsync(channelState, activeCampaign, ct);
                if (campaign?.EncounterTemplates is { Count: > 0 })
                {
                    userCtx.AppendLine("Encounter templates (ids):");
                    userCtx.AppendLine(string.Join(", ",
                        campaign.EncounterTemplates
                            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
                            .Select(t => t.TemplateId.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                            .Take(20)));
                }
            }
            catch
            {
                // ignore
            }
        }
        else if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            var draft = await LoadDraftCampaignAsync(channelState, activeCampaign, ct);
            if (draft != null)
            {
                if (!compactDraftChatOnly && !string.IsNullOrWhiteSpace(draft.CampaignMarkdown))
                {
                    userCtx.AppendLine("Existing draft campaign markdown (excerpt):");
                    userCtx.AppendLine(TrimToLimit(draft.CampaignMarkdown, 500));
                }
                if (!compactDraftChatOnly && draft.EncounterTemplates is { Count: > 0 })
                {
                    userCtx.AppendLine("Encounter templates (ids):");
                    userCtx.AppendLine(string.Join(", ",
                        draft.EncounterTemplates
                            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
                            .Select(t => t.TemplateId.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                            .Take(20)));
                }
            }
        }

        userCtx.AppendLine("User message:");
        userCtx.AppendLine(text);

            var system = mode switch
            {
                ModeDraft =>
                    compactDraftChatOnly
                        ? "You are the D&D draft copilot in conversation-only mode.\n" +
                          "No tool calls. Give a concise response (1-3 sentences) and one actionable follow-up question.\n" +
                          "Do not output JSON or mention internal commands.\n"
                        : "You are the D&D game master assistant operating in DRAFT mode.\n" +
                          "Draft mode is GM prep: build the campaign premise/tone, hooks, scenes, NPCs, encounter templates, and party composition.\n" +
                          "Default to conversational collaboration first: brief, practical, and co-design oriented.\n" +
                          "Call `gptcli_dnd_*` tools only when the user explicitly asks for a persistent state change or command-like action.\n" +
                          "If intent is ambiguous, ask one short clarifying question instead of mutating state.\n" +
                          "Do not call `gptcli_dnd_mode` unless the user explicitly asks to change modes.\n" +
                          "Party roster is provided in context under \"Party roster (actors)\".\n" +
                          "If the user asks to add/remove party members:\n" +
                          "- PCs: use `gptcli_dnd_partyaddpc` / `gptcli_dnd_partyremovepc`.\n" +
                          "- NPCs: use `gptcli_dnd_partyremovenpc` (or `gptcli_dnd_npcremove`).\n" +
                          "If the user asks to change the auto-pass / player turn timeout / pass timeout, call `gptcli_dnd_passtimeout`.\n" +
                          "If the user specifies party members: create NPCs via tools; for PCs, only create a sheet for the author unless you have an explicit Discord user reference.\n" +
                          "In DRAFT mode, do not finalize/save campaigns to the catalog. Build drafts only; finalization happens when entering game mode or via campaignfinalize.\n" +
                          "If the message is discussion/brainstorming, respond as a GM collaborator with concrete suggestions.\n" +
                          "When responding in draft mode, end with one actionable follow-up question unless the user asked for direct execution only.\n",
                ModeGame =>
                    "You are the D&D game master assistant operating in GAME mode.\n" +
                    "Game mode is live play. Treat every user message as in-game gameplay or roleplay.\n" +
                    "The user may mention any available DnD capability; your job is to call the appropriate `gptcli_dnd_*` functions to carry out mechanics/state changes when needed.\n" +
                    "Never require `!` commands for combat actions; plain language should be enough.\n" +
                    "Do not call `gptcli_dnd_mode` unless the user explicitly asks to change modes.\n" +
                    "Important: if the user asks to change modes while in GAME mode, instruct them to use the slash command `/gptcli dnd mode value:draft` (or `off`). Do NOT call `gptcli_dnd_mode` from natural language.\n" +
                    "Party roster is provided in context under \"Party roster (actors)\".\n" +
                    "If there is no active encounter and the user wants to start/begin/play, call `gptcli_dnd_encounterstart` using a template id from context.\n" +
                    "If the user asks to change the auto-pass / player turn timeout / pass timeout, call `gptcli_dnd_passtimeout`.\n" +
                    "If the user intends a mechanical action (attack/cast/pass/rolls/encounter control), call the appropriate tool(s).\n" +
                    "If it is roleplay/table talk, respond as GM but do not invent mechanical outcomes.\n" +
                    "Style: short, in-character, vivid. No meta/explanations.\n" +
                    "Formatting: use Discord markdown for readability: **bold** for scene beats, *italics* for tone, `code` for mechanical snippets.\n" +
                    "Use emojis sparingly (1-3) to signal beats/roles (e.g. 🎲 ⚔️ 🧙 🛡️ 🧠 🧭).\n" +
                    "Use bullet points only when the user explicitly asks for options/lists.\n" +
                    "End with a short playable prompt only when the encounter is waiting on player input.\n",
                _ =>
                    "You are the D&D game master assistant.\n" +
                    "If the user is asking to perform a DnD slash command, call the appropriate tool(s).\n" +
                    "Otherwise respond briefly.\n"
            };

        var request = new ChatCompletionCreateRequest
        {
            Model = ResolveModel(context, channelState),
            Temperature = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) ? 0.3f : 0.2f,
            MaxCompletionTokens = string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase)
                ? 350
                : (compactDraftChatOnly ? 260 : 700),
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System, system)
            }
        };
        if (allowToolCalls && tools.Count > 0)
        {
            request.ParallelToolCalls = false;
            request.Tools = tools;
            request.ToolChoice = new ToolChoice { Type = "auto" };
        }

        // Add recent conversation history so followups like "no I meant X" can be resolved in context.
        // Only include user/assistant roles to keep the prompt small and avoid unrelated system noise.
        try
        {
            var historyMaxMessages = compactDraftChatOnly ? 4 : 14;
            var historyMaxChars = compactDraftChatOnly ? 1200 : 6000;
            var history = SnapshotRecentUserAssistantHistory(channelState, maxMessages: historyMaxMessages, maxChars: historyMaxChars);
            if (history.Count > 0)
            {
                foreach (var h in history)
                {
                    request.Messages.Add(h);
                }
            }
        }
        catch
        {
            // ignore
        }

        request.Messages.Add(new ChatMessage(ChatCompletionRole.User, userCtx.ToString().Trim()));

        ChatCompletionCreateResponse response;
        try
        {
            try
            {
                var toolNames = functions
                    .Where(f => !string.IsNullOrWhiteSpace(f?.ToolName))
                    .Select(f => f.ToolName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Console.WriteLine(
                    $"[dnd] auto-route: llm request mode={mode} model={request.Model} tools={(allowToolCalls ? tools.Count : 0)} allowToolCalls={allowToolCalls} campaign={activeCampaign} channel={message.Channel?.Id} toolNames=[{string.Join(", ", toolNames)}]");
                Console.WriteLine($"[dnd] auto-route: system={system}");
                Console.WriteLine($"[dnd] auto-route: userCtx={userCtx.ToString().Trim()}");
                try
                {
                    Console.WriteLine($"[dnd] auto-route: request messages={request.Messages?.Count ?? 0}");
                    var i = 0;
                    foreach (var m in request.Messages ?? Array.Empty<ChatMessage>())
                    {
                        var c = m?.Content ?? string.Empty;
                        Console.WriteLine($"[dnd] auto-route: msg[{i}] role={m?.Role} len={c.Length}");
                        Console.WriteLine($"[dnd] auto-route: msg[{i}] content={c}");
                        i++;
                    }
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // ignore
            }

            using var typing = DiscordTyping.Begin(message.Channel);
            var responseTask = context.OpenAILogic.CreateChatCompletionAsync(request);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25), ct);
            var done = await Task.WhenAny(responseTask, timeoutTask);
            if (done != responseTask)
            {
                try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Timed out."); }
                catch (Exception ex) { Console.WriteLine($"[dnd] send failed (timeout): {ex.GetType().Name} {ex.Message}"); }
                return true;
            }

            response = await responseTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] auto-route: llm call failed: {ex.GetType().Name} {ex.Message}");
            return false;
        }

        if (!response.Successful)
        {
            Console.WriteLine($"[dnd] auto-route: llm response failed: {response.Error?.Code} {response.Error?.Message}");
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) &&
                (!allowToolCalls || compactDraftChatOnly) &&
                IsTokenLimitError(response.Error?.Message))
            {
                var fallback = $"<@{message.Author.Id}> {BuildDraftConversationalFallback(text)}";
                try { await SendChunkedAsync(message.Channel, fallback); } catch { }
                if (TryRecordAssistantReply(channelState, fallback))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
            return false;
        }

        var msg = response.Choices?.FirstOrDefault()?.Message;
        if (msg == null)
        {
            Console.WriteLine("[dnd] auto-route: llm returned no message");
            return false;
        }

        var msgText = ExtractChatMessageText(msg);
        try
        {
            var choice = response.Choices?.FirstOrDefault();
            var finish = choice?.FinishReason?.ToString() ?? "(null)";
            var contentLen = msgText == null ? -1 : msgText.Length;
            var toolCallCount = msg.ToolCalls?.Count ?? 0;
            var hasFnCall = msg.FunctionCall != null;
            Console.WriteLine($"[dnd] auto-route: llm response choices={(response.Choices?.Count ?? 0)} finish={finish} contentLen={contentLen} toolCalls={toolCallCount} functionCall={hasFnCall}");
        }
        catch
        {
            // ignore
        }

        var toolCalls = msg.ToolCalls;
        if ((toolCalls == null || toolCalls.Count == 0) && msg.FunctionCall != null)
        {
            toolCalls = new List<ToolCall> { new() { Type = "function", FunctionCall = msg.FunctionCall } };
        }
        if (!allowToolCalls)
        {
            toolCalls = null;
        }

        if (toolCalls is { Count: > 0 })
        {
            var applied = new List<string>();
            var errors = new List<string>();

            foreach (var call in toolCalls)
            {
                var toolFn = call?.FunctionCall;
                if (toolFn == null || string.IsNullOrWhiteSpace(toolFn.Name))
                {
                    continue;
                }

                if (!byName.TryGetValue(toolFn.Name.Trim(), out var fn) || fn == null)
                {
                    errors.Add($"Unknown tool '{toolFn.Name}'.");
                    continue;
                }

                var argsJson = string.IsNullOrWhiteSpace(toolFn.Arguments) ? "{}" : toolFn.Arguments;
                var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
                try
                {
                    Console.WriteLine($"[dnd] auto-route: toolcall name={toolFn.Name.Trim()} args={argsJson}");
                    var res = await fn.ExecuteAsync(execCtx, argsJson, ct);
                    if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
                    {
                        applied.Add(res.Response.Trim());
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Tool '{toolFn.Name}' failed: {ex.GetType().Name} {ex.Message}");
                }
            }

            if (applied.Count == 0 && errors.Count == 0)
            {
                Console.WriteLine("[dnd] auto-route: tool calls present, but no applied responses and no errors");
                return false;
            }

            var replyLines = new List<string>();
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                var gmLead = !string.IsNullOrWhiteSpace(msgText)
                    ? SanitizeDraftToolNarration(msgText)
                    : "Updated the draft based on your request.";
                gmLead = string.IsNullOrWhiteSpace(gmLead)
                    ? "Updated the draft based on your request."
                    : TrimToLimit(gmLead.Trim(), 700);
                replyLines.Add($"<@{message.Author.Id}> {gmLead}");
                if (applied.Count > 0)
                {
                    replyLines.Add("Applied:");
                    replyLines.AddRange(applied.Select(line => $"- {line}"));
                }
            }
            else if (applied.Count == 1)
            {
                // For gameplay output (often multi-line), avoid wrapping it in a bullet list.
                replyLines.Add($"<@{message.Author.Id}>");
                replyLines.Add(applied[0]);
            }
            else
            {
                replyLines.Add($"<@{message.Author.Id}> OK:");
                replyLines.AddRange(applied.Select(line => $"- {line}"));
            }

            if (errors.Count > 0)
            {
                replyLines.Add("Some requests were ignored:");
                replyLines.AddRange(errors.Take(8).Select(err => $"- {err}"));
            }

            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(msgText))
            {
                replyLines.Add(string.Empty);
                replyLines.Add("GM:");
                replyLines.Add(TrimToLimit(msgText.Trim(), 900));
            }

            var reply = string.Join("\n", replyLines);
            Console.WriteLine($"[dnd] auto-route: toolReplyLen={reply.Length} toolReply={reply}");
            try { await SendChunkedAsync(message.Channel, reply); }
            catch (Exception ex) { Console.WriteLine($"[dnd] send failed (tool reply): {ex.GetType().Name} {ex.Message}"); }
            if (TryRecordAssistantReply(channelState, reply))
            {
                try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
            }
            return true;
        }

        var content = msgText;
        if (string.IsNullOrWhiteSpace(content))
        {
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                var fallbackText = BuildDraftConversationalFallback(text);
                var fallbackReply = $"<@{message.Author.Id}> {fallbackText}";
                Console.WriteLine($"[dnd] auto-route: empty draft completion; using fallback reply len={fallbackReply.Length}");
                try { await SendChunkedAsync(message.Channel, fallbackReply); }
                catch (Exception ex) { Console.WriteLine($"[dnd] send failed (draft fallback reply): {ex.GetType().Name} {ex.Message}"); }
                if (TryRecordAssistantReply(channelState, fallbackReply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
            return false;
        }

        var chatReply = $"<@{message.Author.Id}> {content.Trim()}";
        Console.WriteLine($"[dnd] auto-route: chatReplyLen={chatReply.Length} chatReply={chatReply}");
        try { await SendChunkedAsync(message.Channel, chatReply); }
        catch (Exception ex) { Console.WriteLine($"[dnd] send failed (chat reply): {ex.GetType().Name} {ex.Message}"); }
        if (TryRecordAssistantReply(channelState, chatReply))
        {
            try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
        }
        return true;
    }

    private async Task<bool> TryHandleDraftIntentRouterAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string strippedText,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(strippedText))
        {
            return false;
        }

        var activeCampaign = dndState.ActiveCampaignName ?? "default";
        var timeoutSeconds = GetDraftIntentRouterTimeoutSeconds(context);

        var userCtx = new StringBuilder();
        userCtx.AppendLine("DND draft intent router");
        userCtx.AppendLine($"Active campaign: {activeCampaign}");
        try
        {
            await AppendPartyRosterContextAsync(userCtx, channelState, ModeDraft, activeCampaign, ct);
        }
        catch
        {
            // ignore
        }

        try
        {
            var draft = await LoadDraftCampaignAsync(channelState, activeCampaign, ct);
            if (draft != null && !string.IsNullOrWhiteSpace(draft.CampaignMarkdown))
            {
                userCtx.AppendLine("Existing draft excerpt:");
                userCtx.AppendLine(TrimToLimit(draft.CampaignMarkdown, 500));
            }
        }
        catch
        {
            // ignore
        }

        userCtx.AppendLine("User message:");
        userCtx.AppendLine(strippedText.Trim());

        var request = new ChatCompletionCreateRequest
        {
            Model = ResolveModel(context, channelState),
            Temperature = 0.1f,
            MaxCompletionTokens = 360,
            ParallelToolCalls = false,
            ToolChoice = new ToolChoice { Type = "auto" },
            Tools = BuildDraftIntentRouterTools(),
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System, BuildDraftIntentRouterSystemPrompt())
            }
        };

        try
        {
            var history = SnapshotRecentUserAssistantHistory(channelState, maxMessages: 8, maxChars: 2200);
            if (history.Count > 0)
            {
                foreach (var h in history)
                {
                    request.Messages.Add(h);
                }
            }
        }
        catch
        {
            // ignore
        }

        request.Messages.Add(new ChatMessage(ChatCompletionRole.User, userCtx.ToString().Trim()));

        ChatCompletionCreateResponse response;
        try
        {
            Console.WriteLine(
                $"[dnd] draft-router: llm request model={request.Model} campaign={activeCampaign} channel={message.Channel?.Id} timeout={timeoutSeconds}s");
            using var typing = DiscordTyping.Begin(message.Channel);
            var responseTask = context.OpenAILogic.CreateChatCompletionAsync(request);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);
            var done = await Task.WhenAny(responseTask, timeoutTask);
            if (done != responseTask)
            {
                Console.WriteLine($"[dnd] draft-router: timed out after {timeoutSeconds}s");
                return false;
            }

            response = await responseTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] draft-router: llm call failed: {ex.GetType().Name} {ex.Message}");
            return false;
        }

        if (!response.Successful)
        {
            Console.WriteLine($"[dnd] draft-router: llm response failed: {response.Error?.Code} {response.Error?.Message}");
            return false;
        }

        var msg = response.Choices?.FirstOrDefault()?.Message;
        if (msg == null)
        {
            Console.WriteLine("[dnd] draft-router: no message in response");
            return false;
        }

        var msgText = ExtractChatMessageText(msg);
        var toolCalls = msg.ToolCalls;
        if ((toolCalls == null || toolCalls.Count == 0) && msg.FunctionCall != null)
        {
            toolCalls = new List<ToolCall> { new() { Type = "function", FunctionCall = msg.FunctionCall } };
        }

        if (toolCalls is not { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(msgText))
            {
                return false;
            }

            var reply = $"<@{message.Author.Id}> {TrimToLimit(msgText.Trim(), 1400)}";
            try { await SendChunkedAsync(message.Channel, reply); } catch { return false; }
            if (TryRecordAssistantReply(channelState, reply))
            {
                try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
            }
            return true;
        }

        var replies = new List<string>();
        var errors = new List<string>();

        foreach (var call in toolCalls)
        {
            var fn = call?.FunctionCall;
            if (fn == null || string.IsNullOrWhiteSpace(fn.Name))
            {
                continue;
            }

            var argsJson = string.IsNullOrWhiteSpace(fn.Arguments) ? "{}" : fn.Arguments;
            Console.WriteLine($"[dnd] draft-router: toolcall name={fn.Name.Trim()} args={argsJson}");
            DraftIntentRouterDispatchResult dispatch;
            try
            {
                dispatch = await DispatchDraftIntentRouterToolAsync(
                    context,
                    channelState,
                    message,
                    dndState,
                    fn.Name.Trim(),
                    argsJson,
                    strippedText,
                    ct);
            }
            catch (Exception ex)
            {
                errors.Add($"Tool '{fn.Name}' failed: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (!dispatch.Handled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dispatch.Reply))
            {
                replies.Add(dispatch.Reply.Trim());
            }

            if (!string.IsNullOrWhiteSpace(dispatch.Error))
            {
                errors.Add(dispatch.Error.Trim());
            }
        }

        if (replies.Count == 0 && errors.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(msgText))
            {
                return false;
            }

            var fallbackReply = $"<@{message.Author.Id}> {TrimToLimit(msgText.Trim(), 1200)}";
            try { await SendChunkedAsync(message.Channel, fallbackReply); } catch { return false; }
            if (TryRecordAssistantReply(channelState, fallbackReply))
            {
                try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
            }
            return true;
        }

        var replyLines = new List<string> { $"<@{message.Author.Id}>" };
        foreach (var r in replies)
        {
            if (replyLines.Count > 1)
            {
                replyLines.Add(string.Empty);
            }
            replyLines.Add(r);
        }
        if (errors.Count > 0)
        {
            if (replyLines.Count > 1)
            {
                replyLines.Add(string.Empty);
            }
            replyLines.Add("Some requests were ignored:");
            replyLines.AddRange(errors.Take(8).Select(e => $"- {e}"));
        }

        var finalReply = TrimToLimit(string.Join("\n", replyLines).Trim(), 3500);
        try { await SendChunkedAsync(message.Channel, finalReply); } catch { return false; }
        if (TryRecordAssistantReply(channelState, finalReply))
        {
            try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
        }

        return true;
    }

    private async Task<DraftIntentRouterDispatchResult> DispatchDraftIntentRouterToolAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string toolName,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        return toolName switch
        {
            RouteToolCampaignCreate => await HandleDraftRouteCampaignCreateAsync(context, channelState, message, dndState, argsJson, strippedText, ct),
            RouteToolDraftUpdate => await HandleDraftRouteDraftUpdateAsync(context, channelState, message, dndState, argsJson, strippedText, ct),
            RouteToolPartyEdit => await HandleDraftRoutePartyEditAsync(context, channelState, message, dndState, argsJson, strippedText, ct),
            RouteToolSheetCreate => await HandleDraftRouteSheetCreateAsync(context, channelState, message, dndState, argsJson, strippedText, ct),
            RouteToolPassTimeout => await HandleDraftRoutePassTimeoutAsync(context, channelState, message, dndState, argsJson, strippedText, ct),
            RouteToolChatReply => HandleDraftRouteChatReply(argsJson, strippedText),
            RouteToolClarify => HandleDraftRouteClarify(argsJson),
            _ => new DraftIntentRouterDispatchResult(false, Error: $"Unknown route tool '{toolName}'.")
        };
    }

    private async Task<DraftIntentRouterDispatchResult> HandleDraftRouteCampaignCreateAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        TryGetStringArg(argsJson, "name", out var campaignNameRaw);
        TryGetStringArg(argsJson, "prompt", out var promptRaw);
        TryGetBoolArg(argsJson, "overwrite", out var overwrite);

        if (string.IsNullOrWhiteSpace(campaignNameRaw) &&
            TryDetectCampaignCreateIntent(strippedText, out var detectedName, out var detectedOverwrite))
        {
            campaignNameRaw = detectedName;
            overwrite = overwrite || detectedOverwrite;
        }

        var campaignName = string.IsNullOrWhiteSpace(campaignNameRaw)
            ? (dndState?.ActiveCampaignName ?? "default")
            : campaignNameRaw.Trim();
        var prompt = string.IsNullOrWhiteSpace(promptRaw) ? strippedText?.Trim() : promptRaw.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new DraftIntentRouterDispatchResult(true, "Tell me the campaign premise and vibe, and I can generate it.");
        }

        var existingPath = ResolveDraftCampaignPath(channelState, campaignName);
        var exists = false;
        try { exists = File.Exists(existingPath); } catch { exists = false; }

        if (exists && !overwrite)
        {
            await SetPendingDraftActionAsync(
                channelState,
                dndState,
                new PendingDraftActionRequest
                {
                    ActionType = PendingActionCampaignCreate,
                    ArgumentsJson = JsonSerializer.Serialize(new { name = campaignName, prompt }),
                    RequestedByUserId = message.Author.Id,
                    RequestedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                    Summary = $"overwrite draft campaign \"{campaignName}\""
                },
                ct);
            return new DraftIntentRouterDispatchResult(
                true,
                $"Draft \"{campaignName}\" already exists. Reply `confirm overwrite` to replace it, or `cancel`.");
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var execArgs = JsonSerializer.Serialize(new { name = campaignName, prompt });
        var res = await ExecuteCampaignCreateAsync(execCtx, execArgs, ct);
        if (res is { Handled: true })
        {
            return new DraftIntentRouterDispatchResult(
                true,
                string.IsNullOrWhiteSpace(res.Response) ? $"Campaign \"{campaignName}\" created." : res.Response.Trim());
        }

        return new DraftIntentRouterDispatchResult(false, Error: "Campaign creation was not applied.");
    }

    private async Task<DraftIntentRouterDispatchResult> HandleDraftRouteDraftUpdateAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        TryGetStringArg(argsJson, "name", out var campaignNameRaw);
        TryGetStringArg(argsJson, "prompt", out var promptRaw);

        var campaignName = string.IsNullOrWhiteSpace(campaignNameRaw)
            ? (dndState?.ActiveCampaignName ?? "default")
            : campaignNameRaw.Trim();
        var prompt = string.IsNullOrWhiteSpace(promptRaw) ? strippedText?.Trim() : promptRaw.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new DraftIntentRouterDispatchResult(true, "Tell me exactly what to change in the draft and I’ll apply it.");
        }

        var draftPath = ResolveDraftCampaignPath(channelState, campaignName);
        var exists = false;
        try { exists = File.Exists(draftPath); } catch { exists = false; }
        if (!exists)
        {
            return new DraftIntentRouterDispatchResult(
                true,
                $"No draft found for \"{campaignName}\". Ask me to create a new campaign first.");
        }

        if (IsDraftConfirmRiskyEditsEnabled(context) && LooksLikeDraftUpdateIntent(strippedText))
        {
            await SetPendingDraftActionAsync(
                channelState,
                dndState,
                new PendingDraftActionRequest
                {
                    ActionType = PendingActionDraftUpdate,
                    ArgumentsJson = JsonSerializer.Serialize(new { name = campaignName, prompt }),
                    RequestedByUserId = message.Author.Id,
                    RequestedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                    Summary = $"rewrite draft campaign \"{campaignName}\""
                },
                ct);
            return new DraftIntentRouterDispatchResult(
                true,
                "I can update the draft story now. Reply `confirm` to apply it, or `cancel`.");
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var execArgs = JsonSerializer.Serialize(new { name = campaignName, prompt });
        var res = await ExecuteDraftUpdateAsync(execCtx, execArgs, ct);
        if (res is { Handled: true })
        {
            return new DraftIntentRouterDispatchResult(
                true,
                string.IsNullOrWhiteSpace(res.Response) ? $"Updated draft \"{campaignName}\"." : res.Response.Trim());
        }

        return new DraftIntentRouterDispatchResult(false, Error: "Draft update was not applied.");
    }

    private async Task<DraftIntentRouterDispatchResult> HandleDraftRoutePartyEditAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        TryGetStringArg(argsJson, "operation", out var opRaw);
        var op = (opRaw ?? string.Empty).Trim().ToLowerInvariant();
        var wantsAdd = op is "add" or "include" or "invite";
        var wantsRemove = op is "remove" or "drop" or "kick";
        if (!wantsAdd && !wantsRemove)
        {
            return new DraftIntentRouterDispatchResult(
                true,
                "For party edits, specify whether to add or remove members.");
        }

        var userIds = TryGetUlongListArg(argsJson, "pc_user_ids");
        var npcIds = TryGetStringListArg(argsJson, "npc_actor_ids");
        if (userIds.Count == 0 && npcIds.Count == 0)
        {
            var inferred = AnalyzeDraftPartyEditIntent(context, message, strippedText);
            if (inferred.MentionedUserIds.Count > 0)
            {
                userIds.AddRange(inferred.MentionedUserIds);
            }
            if (inferred.NpcActorIds.Count > 0)
            {
                npcIds.AddRange(inferred.NpcActorIds);
            }
        }

        userIds = userIds.Where(x => x != 0).Distinct().ToList();
        npcIds = npcIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (userIds.Count == 0 && npcIds.Count == 0)
        {
            return new DraftIntentRouterDispatchResult(
                true,
                "Tag PC users (`@user`) and/or include NPC ids like `npc:roland` so I can update the party.");
        }

        var intent = new DraftPartyEditIntent
        {
            WantsAdd = wantsAdd,
            WantsRemove = wantsRemove,
            MentionedUserIds = userIds,
            NpcActorIds = npcIds
        };

        if (IsDraftConfirmRiskyEditsEnabled(context) && IsRiskyDraftPartyEdit(intent))
        {
            var opLabel = wantsRemove ? "remove" : "add";
            var pendingArgs = JsonSerializer.Serialize(new
            {
                add = wantsAdd,
                remove = wantsRemove,
                userIds,
                npcIds
            });
            await SetPendingDraftActionAsync(
                channelState,
                dndState,
                new PendingDraftActionRequest
                {
                    ActionType = PendingActionPartyEdit,
                    ArgumentsJson = pendingArgs,
                    RequestedByUserId = message.Author.Id,
                    RequestedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes),
                    Summary = $"{opLabel} {intent.TargetCount} party member(s)"
                },
                ct);
            return new DraftIntentRouterDispatchResult(
                true,
                $"This will {opLabel} {intent.TargetCount} party member(s). Reply `confirm` to apply, or `cancel`.");
        }

        var (applied, errors) = await ExecuteDraftPartyEditCoreAsync(
            context,
            channelState,
            message,
            wantsAdd,
            userIds,
            npcIds,
            ct);

        if (applied.Count == 0 && errors.Count == 0)
        {
            return new DraftIntentRouterDispatchResult(true, "No party changes were applied.");
        }

        var lines = new List<string> { "Updated the draft party roster." };
        if (applied.Count > 0)
        {
            lines.Add("Applied:");
            lines.AddRange(applied.Select(x => $"- {x}"));
        }
        if (errors.Count > 0)
        {
            lines.Add("Some requests were ignored:");
            lines.AddRange(errors.Take(8).Select(e => $"- {e}"));
        }

        return new DraftIntentRouterDispatchResult(true, string.Join("\n", lines));
    }

    private async Task<DraftIntentRouterDispatchResult> HandleDraftRouteSheetCreateAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        TryGetStringArg(argsJson, "kind", out var kindRaw);
        TryGetStringArg(argsJson, "name", out var nameRaw);
        TryGetStringArg(argsJson, "concept", out var conceptRaw);

        var kind = (kindRaw ?? string.Empty).Trim().ToLowerInvariant();
        var wantsNpc = kind is "npc";
        var wantsPc = kind is "pc" or "character";

        if (!wantsNpc && !wantsPc)
        {
            wantsNpc = LooksLikeNpcCreateIntentFromText(strippedText);
            wantsPc = !wantsNpc;
        }

        var name = (nameRaw ?? string.Empty).Trim();
        var concept = (conceptRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(concept))
        {
            if (TryExtractNameConceptForSheetCreate(strippedText, out var inferredName, out var inferredConcept))
            {
                name = string.IsNullOrWhiteSpace(name) ? inferredName : name;
                concept = string.IsNullOrWhiteSpace(concept) ? inferredConcept : concept;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(concept))
        {
            var target = wantsNpc ? "NPC" : "character";
            return new DraftIntentRouterDispatchResult(
                true,
                $"I can generate that {target}. Include both `name` and `concept`.");
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var execArgs = JsonSerializer.Serialize(new { name = name.Trim(), concept = concept.Trim() });
        var res = wantsNpc
            ? await ExecuteNpcCreateAsync(execCtx, execArgs, ct)
            : await ExecuteCharacterCreateAsync(execCtx, execArgs, ct);

        if (res is { Handled: true })
        {
            return new DraftIntentRouterDispatchResult(
                true,
                string.IsNullOrWhiteSpace(res.Response) ? "Sheet created." : res.Response.Trim());
        }

        return new DraftIntentRouterDispatchResult(false, Error: "Sheet generation was not applied.");
    }

    private async Task<DraftIntentRouterDispatchResult> HandleDraftRoutePassTimeoutAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string argsJson,
        string strippedText,
        CancellationToken ct)
    {
        int? seconds = null;
        int? minutes = null;

        if (TryGetIntArg(argsJson, "seconds", out var parsedSeconds) && parsedSeconds > 0)
        {
            seconds = Math.Clamp(parsedSeconds, 5, 3600);
        }
        if (TryGetIntArg(argsJson, "minutes", out var parsedMinutes) && parsedMinutes > 0)
        {
            minutes = Math.Clamp(parsedMinutes, 1, 60);
        }

        if (!seconds.HasValue && !minutes.HasValue)
        {
            if (TryExtractPassTimeoutArgs(strippedText, out var inferredSeconds, out var inferredMinutes))
            {
                seconds = inferredSeconds;
                minutes = inferredMinutes;
            }
        }

        if (!seconds.HasValue && !minutes.HasValue)
        {
            return new DraftIntentRouterDispatchResult(
                true,
                "Tell me the exact timeout, for example `pass timeout 20 minutes` or `pass timeout 90 seconds`.");
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var execArgs = seconds.HasValue
            ? JsonSerializer.Serialize(new { seconds = seconds.Value })
            : JsonSerializer.Serialize(new { minutes = minutes.GetValueOrDefault(30) });
        var res = await ExecutePassTimeoutAsync(execCtx, execArgs, ct);
        if (res is { Handled: true })
        {
            return new DraftIntentRouterDispatchResult(
                true,
                string.IsNullOrWhiteSpace(res.Response) ? "Pass timeout updated." : res.Response.Trim());
        }

        return new DraftIntentRouterDispatchResult(false, Error: "Pass timeout update was not applied.");
    }

    private static DraftIntentRouterDispatchResult HandleDraftRouteChatReply(string argsJson, string strippedText)
    {
        TryGetStringArg(argsJson, "reply", out var replyRaw);
        var reply = string.IsNullOrWhiteSpace(replyRaw)
            ? BuildDraftConversationalFallback(strippedText)
            : replyRaw.Trim();
        return new DraftIntentRouterDispatchResult(true, TrimToLimit(reply, 1300));
    }

    private static DraftIntentRouterDispatchResult HandleDraftRouteClarify(string argsJson)
    {
        TryGetStringArg(argsJson, "question", out var questionRaw);
        var question = string.IsNullOrWhiteSpace(questionRaw)
            ? "Can you clarify exactly what you want me to change?"
            : questionRaw.Trim();
        return new DraftIntentRouterDispatchResult(true, TrimToLimit(question, 600));
    }

    private static List<ToolDefinition> BuildDraftIntentRouterTools()
    {
        var tools = new List<ToolDefinition>
        {
            BuildDraftIntentRouterTool(
                RouteToolCampaignCreate,
                "Create a new draft campaign package from the user's message. Use for new/start/create campaign requests.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = new PropertyDefinition { Type = "string", Description = "Campaign name (optional)." },
                    ["prompt"] = new PropertyDefinition { Type = "string", Description = "Campaign generation prompt." },
                    ["overwrite"] = new PropertyDefinition { Type = "boolean", Description = "True only when the user explicitly asked to overwrite an existing draft." }
                },
                new[] { "prompt" }),
            BuildDraftIntentRouterTool(
                RouteToolDraftUpdate,
                "Update/rewrite an existing draft campaign from a modification prompt.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = new PropertyDefinition { Type = "string", Description = "Campaign name (optional)." },
                    ["prompt"] = new PropertyDefinition { Type = "string", Description = "Modification prompt describing what to change." }
                },
                new[] { "prompt" }),
            BuildDraftIntentRouterTool(
                RouteToolPartyEdit,
                "Add/remove party members in the draft roster.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["operation"] = new PropertyDefinition
                    {
                        Type = "string",
                        Description = "Edit operation.",
                        Enum = new List<string> { "add", "remove" }
                    },
                    ["pc_user_ids"] = new PropertyDefinition { Type = "string", Description = "Comma-separated Discord user ids (optional)." },
                    ["npc_actor_ids"] = new PropertyDefinition { Type = "string", Description = "Comma-separated npc: ids (optional)." }
                },
                new[] { "operation" }),
            BuildDraftIntentRouterTool(
                RouteToolSheetCreate,
                "Create a character/NPC sheet in draft mode.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = new PropertyDefinition
                    {
                        Type = "string",
                        Description = "Sheet kind.",
                        Enum = new List<string> { "pc", "npc" }
                    },
                    ["name"] = new PropertyDefinition { Type = "string", Description = "Character name." },
                    ["concept"] = new PropertyDefinition { Type = "string", Description = "Character concept." }
                },
                new[] { "kind", "name", "concept" }),
            BuildDraftIntentRouterTool(
                RouteToolPassTimeout,
                "Set auto-pass timeout for player turns.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["minutes"] = new PropertyDefinition { Type = "integer", Description = "Timeout in minutes." },
                    ["seconds"] = new PropertyDefinition { Type = "integer", Description = "Timeout in seconds." }
                }),
            BuildDraftIntentRouterTool(
                RouteToolChatReply,
                "Send a conversational draft collaboration response with no state mutation.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reply"] = new PropertyDefinition { Type = "string", Description = "Concise collaborative reply to the user." }
                },
                new[] { "reply" }),
            BuildDraftIntentRouterTool(
                RouteToolClarify,
                "Ask one concise clarifying question before taking any state action.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["question"] = new PropertyDefinition { Type = "string", Description = "Clarifying question for the user." }
                },
                new[] { "question" })
        };

        return tools;
    }

    private static ToolDefinition BuildDraftIntentRouterTool(
        string name,
        string description,
        Dictionary<string, PropertyDefinition> properties,
        IReadOnlyList<string> required = null)
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
                    Properties = properties ?? new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase),
                    Required = required?.ToList() ?? new List<string>()
                }
            }
        };
    }

    private static string BuildDraftIntentRouterSystemPrompt()
    {
        return
            "You route DND DRAFT user intent to transition tools.\n" +
            "Choose the single best transition tool for the user message.\n" +
            "Use tools only; do not answer in plain text unless no tool applies.\n" +
            "Priorities:\n" +
            "1) Campaign creation/start requests -> dnd_route_campaign_create.\n" +
            "2) Rewrite/update existing draft story -> dnd_route_draft_update.\n" +
            "3) Add/remove party members -> dnd_route_party_edit.\n" +
            "4) Create sheets -> dnd_route_sheet_create.\n" +
            "5) Pass timeout changes -> dnd_route_passtimeout.\n" +
            "6) If no state mutation requested -> dnd_route_chat_reply.\n" +
            "7) If mutation intent is clear but missing required details -> dnd_route_clarify.\n" +
            "Never call gptcli_dnd_* tools directly in this step.\n" +
            "Keep tool arguments minimal and explicit. For overwrite=true, only set when user explicitly asked to overwrite/replace.";
    }

    private static bool IsDndToolAllowedForMode(string toolName, string mode)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        // Always allow turning DnD back on/off.
        if (string.Equals(toolName, "gptcli_dnd_mode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        mode = NormalizeMode(mode);

        // Off: do not expose any tools other than `mode`.
        if (string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Always allow status while not off.
        if (string.Equals(toolName, "gptcli_dnd_status", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Draft/off: draft campaign + party management only.
	        if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
	        {
	            return toolName.Equals("gptcli_dnd_campaigncreate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_draftupdate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_campaignfinalize", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_campaignlist", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_campaignstart", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyshow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyaddpc", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyremovepc", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyaddnpc", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyremovenpc", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_charactercreate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_charactershow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npccreate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npclist", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npcshow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npcremove", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_passtimeout", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_encounterlist", StringComparison.OrdinalIgnoreCase);
	        }

        // Game: gameplay + party management; exclude campaign creation/catalog.
	        if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
	        {
	            return toolName.Equals("gptcli_dnd_charactercreate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_charactershow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npccreate", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npclist", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npcshow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_npcremove", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyshow", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_partyremovenpc", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_liveconfig", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_passtimeout", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_encounterlist", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_encounterstart", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_encounterstatus", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_encounterend", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_attack", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_cast", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_pass", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_rollall", StringComparison.OrdinalIgnoreCase) ||
	                   toolName.Equals("gptcli_dnd_ledger", StringComparison.OrdinalIgnoreCase);
	        }

        return false;
    }

    private static bool TryDetectCampaignCreateIntent(string text, out string campaignName, out bool wantsOverwrite)
    {
        campaignName = null;
        wantsOverwrite = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        var lower = t.ToLowerInvariant();

        // Overwrite hints: allow bypassing confirm flow when the user is explicit.
        wantsOverwrite =
            lower.Contains("overwrite", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("wipe", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("reset it", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("start over", StringComparison.OrdinalIgnoreCase);

        // Intent heuristics: require explicit create/start wording so phrases like
        // "add NPC named X to the campaign" don't trigger campaigncreate.
        var looksLikeCreate =
            lower.Contains("new campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("create a campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("create campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("start a campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("start campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("begin a campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("begin campaign", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                t,
                @"\b(create|start|begin|build|generate|make)\b[^\n]{0,40}\bcampaign\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!looksLikeCreate)
        {
            return false;
        }

        // Extract quoted name first.
        // Examples:
        // - new campaign named "Sour Patch Kids"
        // - create campaign called 'Sour Patch Kids'
        var m = Regex.Match(t, @"\bcampaign\b[^\n]{0,80}?\b(?:named|called)\b\s*(?:(?:""(?<q>[^""]{1,120})"")|(?:'(?<q>[^']{1,120})'))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            m = Regex.Match(t, @"\b(?:new|create|start|begin|build|generate|make)\b[^\n]{0,40}\bcampaign\b[^\n]{0,80}?\b(?:named|called)\b\s*(?:(?:""(?<q>[^""]{1,120})"")|(?:'(?<q>[^']{1,120})'))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (m.Success)
        {
            var q = (m.Groups["q"]?.Value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                campaignName = q.Length > 120 ? q[..120].Trim() : q;
                return true;
            }
        }

        // Unquoted fallback: take a short tail after "campaign named/called".
        m = Regex.Match(t, @"\bcampaign\b[^\n]{0,80}?\b(?:named|called)\b\s*(?<u>[^.\n,!]{1,120})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            var u = (m.Groups["u"]?.Value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(u))
            {
                campaignName = u.Length > 120 ? u[..120].Trim() : u;
                return true;
            }
        }

        // Intent is present even if we couldn't parse a name; caller can fall back to active campaign.
        return true;
    }

    private static bool LooksLikeDraftUpdateIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        var lower = t.ToLowerInvariant();

        // Don't treat "new campaign" as an update.
        if (lower.Contains("new campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("create a campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("start a campaign", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasDraftSubject =
            lower.Contains("campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("story", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("hook", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("lore", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("setting", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("premise", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("encounter", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("template", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("quest", StringComparison.OrdinalIgnoreCase);

        var hasPartyOrSheetSubject =
            lower.Contains("party", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("npc", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("character", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("player", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("roster", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("sheet", StringComparison.OrdinalIgnoreCase);

        // Keep deterministic draft rewrites conservative; party/sheet messages should route elsewhere.
        if (hasPartyOrSheetSubject && !hasDraftSubject)
        {
            return false;
        }

        // High-signal edit verbs. Require draft/story context for deterministic rewrite.
        if (lower.Contains("rewrite", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("revise", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("retcon", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("update the", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("change the", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("modify", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("replace", StringComparison.OrdinalIgnoreCase))
        {
            return hasDraftSubject;
        }

        // Start-of-message imperative edits are only deterministic when clearly about story/draft content.
        if (lower.StartsWith("add ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("remove ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("make ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("swap ", StringComparison.OrdinalIgnoreCase))
        {
            return hasDraftSubject;
        }

        return false;
    }

    private static List<string> ExtractNpcActorIds(string text)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ids;
        }

        foreach (Match m in Regex.Matches(text, @"\bnpc:[a-z0-9_-]+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var v = (m.Value ?? string.Empty).Trim();
            if (v.Length == 0)
            {
                continue;
            }

            if (!ids.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
            {
                ids.Add(v);
            }
        }

        return ids;
    }

    private static bool LooksLikePartyAdd(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var l = text.Trim().ToLowerInvariant();
        return l.Contains("add ", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("include ", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("bring ", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("invite ", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("join", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePartyRemove(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var l = text.Trim().ToLowerInvariant();
        return l.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("kick", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("drop", StringComparison.OrdinalIgnoreCase) ||
               l.Contains("exclude", StringComparison.OrdinalIgnoreCase);
    }

    private static DraftPartyEditIntent AnalyzeDraftPartyEditIntent(
        DiscordModuleContext context,
        SocketMessage message,
        string strippedText)
    {
        var intent = new DraftPartyEditIntent();
        if (context == null || message == null || string.IsNullOrWhiteSpace(strippedText))
        {
            return intent;
        }

        var botId = context.Client?.CurrentUser?.Id ?? 0;
        intent.MentionedUserIds = message.MentionedUsers
            .Where(u => u != null && u.Id != 0 && u.Id != botId)
            .Select(u => u.Id)
            .Distinct()
            .ToList();
        intent.NpcActorIds = ExtractNpcActorIds(strippedText);
        intent.WantsAdd = LooksLikePartyAdd(strippedText);
        intent.WantsRemove = LooksLikePartyRemove(strippedText);
        return intent;
    }

    private static bool IsRiskyDraftPartyEdit(DraftPartyEditIntent intent)
    {
        if (intent == null || !intent.HasTargets || intent.IsAmbiguous || (!intent.WantsAdd && !intent.WantsRemove))
        {
            return false;
        }

        if (intent.WantsRemove)
        {
            return true;
        }

        // Bulk edits are easy to misread in natural language; require confirmation.
        return intent.TargetCount > 2;
    }

    private static bool LooksLikePassTimeoutIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.Trim().ToLowerInvariant();
        return lower.Contains("pass timeout", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("auto-pass", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("autopass", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("turn timeout", StringComparison.OrdinalIgnoreCase) ||
               (lower.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
                lower.Contains("pass", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryExtractPassTimeoutArgs(string text, out int? seconds, out int? minutes)
    {
        seconds = null;
        minutes = null;
        if (!LooksLikePassTimeoutIntent(text))
        {
            return false;
        }

        var secMatch = Regex.Match(
            text ?? string.Empty,
            @"\b(?<n>\d{1,4})\s*(sec|secs|second|seconds|s)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (secMatch.Success && int.TryParse(secMatch.Groups["n"].Value, out var sec))
        {
            seconds = Math.Clamp(sec, 5, 3600);
            return true;
        }

        var minMatch = Regex.Match(
            text ?? string.Empty,
            @"\b(?<n>\d{1,3})\s*(min|mins|minute|minutes|m)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (minMatch.Success && int.TryParse(minMatch.Groups["n"].Value, out var min))
        {
            minutes = Math.Clamp(min, 1, 60);
            return true;
        }

        // Fallback: if user gave only a bare number with pass-timeout wording, interpret as minutes.
        var bareNumber = Regex.Match(
            text ?? string.Empty,
            @"\b(?<n>\d{1,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (bareNumber.Success && int.TryParse(bareNumber.Groups["n"].Value, out var bare))
        {
            minutes = Math.Clamp(bare, 1, 60);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleDeterministicDraftPassTimeoutFromMessageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string strippedText,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryExtractPassTimeoutArgs(strippedText, out var seconds, out var minutes))
        {
            return false;
        }

        var argsJson = seconds.HasValue
            ? JsonSerializer.Serialize(new { seconds = seconds.Value })
            : JsonSerializer.Serialize(new { minutes = minutes.GetValueOrDefault(30) });

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        try
        {
            var res = await ExecutePassTimeoutAsync(execCtx, argsJson, ct);
            if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
            {
                var reply = $"<@{message.Author.Id}> {res.Response.Trim()}";
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] deterministic passtimeout failed: {ex.GetType().Name} {ex.Message}");
        }

        return false;
    }

    private static bool LooksLikeCharacterCreateIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.Trim().ToLowerInvariant();
        return lower.Contains("create character", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("make character", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("new character", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("charactercreate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNpcCreateIntentFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.Trim().ToLowerInvariant();
        return lower.Contains("create npc", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("make npc", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("new npc", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("npccreate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractNameConceptForSheetCreate(string text, out string name, out string concept)
    {
        name = null;
        concept = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();

        var nameMatch = Regex.Match(
            t,
            @"\bname\s*[:=]\s*(?:(?:""(?<v>[^""]{1,80})"")|(?:'(?<v>[^']{1,80})')|(?<v>[^,\n;]{1,80}))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (nameMatch.Success)
        {
            name = (nameMatch.Groups["v"]?.Value ?? string.Empty).Trim();
        }

        var conceptMatch = Regex.Match(
            t,
            @"\bconcept\s*[:=]\s*(?<v>[^;\n]{2,220})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (conceptMatch.Success)
        {
            concept = (conceptMatch.Groups["v"]?.Value ?? string.Empty).Trim().TrimEnd('.', '!', '?');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var namedMatch = Regex.Match(
                t,
                @"\b(?:character|npc)\b[^\n]{0,30}?\bnamed\b\s*(?:(?:""(?<n>[^""]{1,80})"")|(?:'(?<n>[^']{1,80})')|(?<n>[^,\n;]{1,80}))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (namedMatch.Success)
            {
                name = (namedMatch.Groups["n"]?.Value ?? string.Empty).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(concept))
        {
            var withConcept = Regex.Match(
                t,
                @"\b(?:character|npc)\b[^\n]{0,120}?[,:-]\s*(?<c>[^;\n]{3,220})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (withConcept.Success)
            {
                concept = (withConcept.Groups["c"]?.Value ?? string.Empty).Trim().TrimEnd('.', '!', '?');
            }
        }

        name = string.IsNullOrWhiteSpace(name) ? null : TrimToLimit(name, 80);
        concept = string.IsNullOrWhiteSpace(concept) ? null : TrimToLimit(concept, 220);
        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(concept);
    }

    private async Task<bool> TryHandleDeterministicDraftSheetGenerationFromMessageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string strippedText,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var wantsCharacter = LooksLikeCharacterCreateIntent(strippedText);
        var wantsNpc = LooksLikeNpcCreateIntentFromText(strippedText);
        if (!wantsCharacter && !wantsNpc)
        {
            return false;
        }

        if (!TryExtractNameConceptForSheetCreate(strippedText, out var name, out var concept))
        {
            var target = wantsCharacter ? "character" : "NPC";
            try
            {
                await message.Channel.SendMessageAsync(
                    $"<@{message.Author.Id}> I can generate that {target} now. Please include both `name` and `concept` (for example: `create {target} name: Roland concept: Gunslinger haunted by ka`).");
            }
            catch
            {
                // ignore
            }
            return true;
        }

        var argsJson = JsonSerializer.Serialize(new { name, concept });
        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        try
        {
            var res = wantsCharacter
                ? await ExecuteCharacterCreateAsync(execCtx, argsJson, ct)
                : await ExecuteNpcCreateAsync(execCtx, argsJson, ct);
            if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
            {
                var reply = $"<@{message.Author.Id}>\n{res.Response.Trim()}";
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] deterministic sheet generation failed: {ex.GetType().Name} {ex.Message}");
        }

        return true;
    }

    private static bool TryBuildDraftStateClarification(
        string strippedText,
        DraftPartyEditIntent partyIntent,
        out string clarification)
    {
        clarification = null;
        var text = strippedText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        if (LooksLikePassTimeoutIntent(text) &&
            !TryExtractPassTimeoutArgs(text, out _, out _))
        {
            clarification = "I can set that now. Tell me the exact value, for example `pass timeout 20 minutes` or `pass timeout 90 seconds`.";
            return true;
        }

        var lower = text.ToLowerInvariant();
        var partyWords =
            lower.Contains("party", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("companion", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("companions", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("pc", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("npc", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("roster", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("character", StringComparison.OrdinalIgnoreCase);
        var editWords =
            lower.Contains("swap", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("add", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("remove", StringComparison.OrdinalIgnoreCase);

        if (partyWords && editWords && (partyIntent == null || !partyIntent.HasTargets))
        {
            clarification =
                "I can do that directly. Please tag the PC user (`@user`) and include NPC ids like `npc:roland` so I can apply the swap without guessing.";
            return true;
        }

        return false;
    }

    private static bool LooksLikeExplicitDraftMutationIntent(string text, DraftPartyEditIntent partyIntent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryDetectCampaignCreateIntent(text, out _, out _))
        {
            return true;
        }

        if (LooksLikeDraftUpdateIntent(text))
        {
            return true;
        }

        if (partyIntent is { HasTargets: true } &&
            !partyIntent.IsAmbiguous &&
            (partyIntent.WantsAdd || partyIntent.WantsRemove))
        {
            return true;
        }

        var lower = text.Trim().ToLowerInvariant();
        var partyWords =
            lower.Contains("party", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("companion", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("companions", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("pc", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("npc", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("roster", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("character", StringComparison.OrdinalIgnoreCase);
        var editWords =
            lower.Contains("swap", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("add", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("remove", StringComparison.OrdinalIgnoreCase);
        if (partyWords && editWords)
        {
            return true;
        }

        if (LooksLikePassTimeoutIntent(text))
        {
            return true;
        }

        return lower.Contains("/gptcli", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("campaignlist", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("encounterlist", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("campaignstart", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("partyshow", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("npccreate", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("npclist", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("npcshow", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("charactercreate", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("charactershow", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("list campaigns", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("list encounters", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("set active campaign", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("create npc", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("show npc", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("create character", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("show character", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDraftChatFirstEnabled(DiscordModuleContext context)
        => ReadBooleanConfiguration(context, "Discord:Modules:Dnd:Draft:ChatFirst", defaultValue: true);

    private static bool IsDraftConfirmRiskyEditsEnabled(DiscordModuleContext context)
        => ReadBooleanConfiguration(context, "Discord:Modules:Dnd:Draft:ConfirmRiskyEdits", defaultValue: true);

    private static bool IsDraftIntentRouterEnabled(DiscordModuleContext context)
        => ReadBooleanConfiguration(context, "Discord:Modules:Dnd:Draft:IntentRouterEnabled", defaultValue: true);

    private static bool IsDraftIntentRouterLegacyFallbackEnabled(DiscordModuleContext context)
        => ReadBooleanConfiguration(context, "Discord:Modules:Dnd:Draft:IntentRouterLegacyFallback", defaultValue: true);

    private static int GetDraftIntentRouterTimeoutSeconds(DiscordModuleContext context)
        => Math.Clamp(ReadIntConfiguration(context, "Discord:Modules:Dnd:Draft:IntentRouterTimeoutSeconds", defaultValue: 20), 8, 60);

    private static bool IsGameNarrationEnabled(DiscordModuleContext context)
        => ReadBooleanConfiguration(context, "Discord:Modules:Dnd:GameNarration:Enabled", defaultValue: true);

    private static string GetGameNarrationMode(DiscordModuleContext context)
        => NormalizeGameNarrationMode(ReadStringConfiguration(context, "Discord:Modules:Dnd:GameNarration:Mode", GameNarrationModeLlm));

    private static string GetGameNarrationEmojiLevel(DiscordModuleContext context)
        => NormalizeGameNarrationEmojiLevel(ReadStringConfiguration(context, "Discord:Modules:Dnd:GameNarration:EmojiLevel", "medium"));

    private static int GetGameNarrationTimeoutSeconds(DiscordModuleContext context)
        => Math.Clamp(ReadIntConfiguration(context, "Discord:Modules:Dnd:GameNarration:TimeoutSeconds", defaultValue: 8), 3, 30);

    private static int GetGameNarrationMaxLeadChars(DiscordModuleContext context)
        => Math.Clamp(ReadIntConfiguration(context, "Discord:Modules:Dnd:GameNarration:MaxLeadChars", defaultValue: 500), 120, 1200);

    private static string NormalizeGameNarrationMode(string value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "off" or "none" or "disabled" => GameNarrationModeOff,
            "deterministic" or "template" or "templates" => GameNarrationModeDeterministic,
            _ => GameNarrationModeLlm
        };
    }

    private static string NormalizeGameNarrationEmojiLevel(string value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "low" => "low",
            "high" => "high",
            _ => "medium"
        };
    }

    private static bool ReadBooleanConfiguration(DiscordModuleContext context, string key, bool defaultValue)
    {
        var raw = context?.Configuration?[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(raw, out var asInt))
        {
            return asInt != 0;
        }

        var lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "yes" or "y" or "on" or "enabled" => true,
            "no" or "n" or "off" or "disabled" => false,
            _ => defaultValue
        };
    }

    private static int ReadIntConfiguration(DiscordModuleContext context, string key, int defaultValue)
    {
        var raw = context?.Configuration?[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static string ReadStringConfiguration(DiscordModuleContext context, string key, string defaultValue)
    {
        var raw = context?.Configuration?[key];
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw.Trim();
    }

    private static string BuildDraftConversationalFallback(string userText)
    {
        var text = (userText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "I'm here with you. Want to sketch the campaign premise, tone, and first hook?";
        }

        if (text.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("cosmic", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Great seed. I can build this as a time-and-space arc. Do you want it to feel more cosmic mystery, hard sci-fi, or mythic fantasy?";
        }

        return "I’m on it. I can help shape that into a playable draft. Do you want to start with premise, tone, or the opening scene?";
    }

    private static bool IsTokenLimitError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var lower = message.Trim().ToLowerInvariant();
        return lower.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("max tokens", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("output limit", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("could not finish the message", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("length", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDraftToolNarration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = (raw ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var kept = new List<string>();
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("calling ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lower.Contains("gptcli_dnd_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal)) ||
                (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)))
            {
                if (TryParseJsonElement(line, out _))
                {
                    continue;
                }
            }

            kept.Add(line);
            if (kept.Count >= 3)
            {
                break;
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        return string.Join(" ", kept);
    }

    private static bool LooksLikeModeChangeIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        var lower = t.ToLowerInvariant();

        // Strong signals.
        if (lower.Contains("/gptcli", StringComparison.OrdinalIgnoreCase) &&
            lower.Contains("dnd", StringComparison.OrdinalIgnoreCase) &&
            lower.Contains("mode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Phrases explicitly about switching modes.
        var mentionsMode =
            lower.Contains("mode", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("switch to", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("go into", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("enter", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("turn off", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("disable dnd", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("enable dnd", StringComparison.OrdinalIgnoreCase);

        if (!mentionsMode)
        {
            return false;
        }

        // Must reference one of the known modes.
        return lower.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("game", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("live", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("prep", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("build", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeGameStartIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        var lower = t.ToLowerInvariant();

        // Keep this conservative: only fire for "start/begin/play/go" style messages.
        if (lower.Contains("encounter", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("combat", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("battle", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("fight", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lower.Contains("start", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("begin", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("let's go", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("lets go", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("let's play", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("lets play", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("ok go", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("ok start", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Single-token nudges.
        if (string.Equals(lower, "go", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lower, "start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lower, "begin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lower, "play", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleDeterministicDraftPartyEditsAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string strippedText,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(strippedText))
        {
            return false;
        }

        var intent = AnalyzeDraftPartyEditIntent(context, message, strippedText);

        // If no obvious party signals, skip.
        if (!intent.HasTargets)
        {
            return false;
        }

        // If both add and remove words exist, it's ambiguous; let the LLM router handle it.
        if (intent.IsAmbiguous)
        {
            return false;
        }

        if (!intent.WantsAdd && !intent.WantsRemove)
        {
            // Mentions without add/remove: let the LLM handle (could just be addressing someone).
            return false;
        }

        var (applied, errors) = await ExecuteDraftPartyEditCoreAsync(
            context,
            channelState,
            message,
            wantsAdd: intent.WantsAdd,
            mentionedUserIds: intent.MentionedUserIds,
            npcActorIds: intent.NpcActorIds,
            ct);

        if (applied.Count == 0 && errors.Count == 0)
        {
            return false;
        }

        var replyLines = new List<string> { $"<@{message.Author.Id}> Updated the draft party roster." };
        if (applied.Count > 0)
        {
            replyLines.Add("Applied:");
            replyLines.AddRange(applied.Select(x => $"- {x}"));
        }
        if (errors.Count > 0)
        {
            replyLines.Add("Some requests were ignored:");
            replyLines.AddRange(errors.Take(8).Select(e => $"- {e}"));
        }

        var reply = TrimToLimit(string.Join("\n", replyLines), 3500);
        try { await SendChunkedAsync(message.Channel, reply); }
        catch (Exception ex) { Console.WriteLine($"[dnd] send failed (draft party deterministic): {ex.GetType().Name} {ex.Message}"); }
        if (TryRecordAssistantReply(channelState, reply))
        {
            try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
        }
        return true;
    }

    private async Task<(List<string> Applied, List<string> Errors)> ExecuteDraftPartyEditCoreAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        bool wantsAdd,
        IReadOnlyCollection<ulong> mentionedUserIds,
        IReadOnlyCollection<string> npcActorIds,
        CancellationToken ct)
    {
        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var applied = new List<string>();
        var errors = new List<string>();

        if (wantsAdd)
        {
            foreach (var uid in (mentionedUserIds ?? Array.Empty<ulong>()))
            {
                var argsJson = JsonSerializer.Serialize(new { user = uid });
                try
                {
                    var res = await ExecutePartyAddPcAsync(execCtx, argsJson, ct);
                    if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
                    {
                        applied.Add(res.Response.Trim());
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"partyaddpc {uid}: {ex.GetType().Name} {ex.Message}");
                }
            }

            foreach (var npc in (npcActorIds ?? Array.Empty<string>()))
            {
                var argsJson = JsonSerializer.Serialize(new { id = npc });
                try
                {
                    var res = await ExecutePartyAddNpcAsync(execCtx, argsJson, ct);
                    if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
                    {
                        applied.Add(res.Response.Trim());
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"partyaddnpc {npc}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        else
        {
            foreach (var uid in (mentionedUserIds ?? Array.Empty<ulong>()))
            {
                var argsJson = JsonSerializer.Serialize(new { user = uid });
                try
                {
                    var res = await ExecutePartyRemovePcAsync(execCtx, argsJson, ct);
                    if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
                    {
                        applied.Add(res.Response.Trim());
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"partyremovepc {uid}: {ex.GetType().Name} {ex.Message}");
                }
            }

            foreach (var npc in (npcActorIds ?? Array.Empty<string>()))
            {
                var argsJson = JsonSerializer.Serialize(new { id = npc });
                try
                {
                    var res = await ExecuteNpcRemoveAsync(execCtx, argsJson, ct);
                    if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
                    {
                        applied.Add(res.Response.Trim());
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"npcremove {npc}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        return (applied, errors);
    }

    private async Task<bool> TryHandleDeterministicGameStartAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = (message.Content ?? string.Empty).Trim();
        if (!LooksLikeGameStartIntent(raw))
        {
            return false;
        }

        var active = dndState.ActiveCampaignName ?? "default";

        // If an encounter is already in progress (or waiting on rolls/actions), do not auto-start another.
        var enc = await GetActiveEncounterSnapshotAsync(channelState, active, ct);
        if (enc != null && enc.Phase != DndEncounterPhase.NotStarted && !enc.IsCompleted)
        {
            return false;
        }

        // Choose a deterministic first encounter template.
        var campaign = await LoadCampaignAsync(channelState, active, ct);
        var templateId = campaign?.EncounterTemplates?
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
            .Select(t => t.TemplateId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(templateId))
        {
            return false;
        }

        Console.WriteLine($"[dnd] deterministic gamestart: starting encounter templateId={templateId} campaign={active} channel={message.Channel?.Id}");

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var argsJson = JsonSerializer.Serialize(new { id = templateId });
        try
        {
            var res = await ExecuteEncounterStartAsync(execCtx, argsJson, ct);
            if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
            {
                var reply = $"<@{message.Author.Id}>\n{res.Response.Trim()}";
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] deterministic gamestart failed: {ex.GetType().Name} {ex.Message}");
        }

        return false;
    }

    private async Task<bool> TryHandleDeterministicDraftUpdateAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string strippedText,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!LooksLikeDraftUpdateIntent(strippedText))
        {
            return false;
        }

        // Only do this if a draft already exists; otherwise the user probably meant to create one.
        var active = dndState.ActiveCampaignName ?? "default";
        var draftPath = ResolveDraftCampaignPath(channelState, active);
        var exists = false;
        try { exists = File.Exists(draftPath); } catch { exists = false; }
        if (!exists)
        {
            return false;
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var argsJson = JsonSerializer.Serialize(new { name = active, prompt = strippedText });
        try
        {
	            var res = await ExecuteDraftUpdateAsync(execCtx, argsJson, ct);
	            if (res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response))
	            {
	                var reply = $"<@{message.Author.Id}>\n{res.Response.Trim()}";
	                await SendChunkedAsync(message.Channel, reply);
	                if (TryRecordAssistantReply(channelState, reply))
	                {
	                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
	                }
	                return true;
	            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] deterministic draftupdate failed: {ex.GetType().Name} {ex.Message}");
        }

        return false;
    }

    private async Task SetPendingDraftActionAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        PendingDraftActionRequest pending,
        CancellationToken ct)
    {
        if (dndState == null)
        {
            return;
        }

        dndState.PendingCampaignCreate = null;
        dndState.PendingDraftAction = pending;
        await SaveStateAsync(channelState, dndState, ct);
    }

    private static bool TryParseDraftConfirmationResponse(string raw, out bool isConfirm, out bool isCancel)
    {
        isConfirm = false;
        isCancel = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        isConfirm = normalized == "confirm" ||
                    normalized == "confirm overwrite" ||
                    normalized == "yes" ||
                    normalized == "y";
        isCancel = normalized == "cancel" ||
                   normalized == "no" ||
                   normalized == "n";
        return isConfirm || isCancel;
    }

    private async Task<bool> TryHandlePendingDraftActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState?.PendingDraftAction == null)
        {
            return false;
        }

        var pending = dndState.PendingDraftAction;
        var now = DateTime.UtcNow;
        if (pending.ExpiresUtc != default && pending.ExpiresUtc <= now)
        {
            dndState.PendingDraftAction = null;
            try { await SaveStateAsync(channelState, dndState, ct); } catch { }
            return false;
        }

        var raw = (message.Content ?? string.Empty).Trim();
        if (!TryParseDraftConfirmationResponse(raw, out var isConfirm, out var isCancel))
        {
            return false;
        }

        if (pending.RequestedByUserId != 0 && pending.RequestedByUserId != message.Author.Id)
        {
            try
            {
                await message.Channel.SendMessageAsync(
                    $"<@{message.Author.Id}> Only <@{pending.RequestedByUserId}> can confirm or cancel this pending draft change.");
            }
            catch
            {
                // ignore
            }
            return true;
        }

        if (isCancel)
        {
            dndState.PendingDraftAction = null;
            await SaveStateAsync(channelState, dndState, ct);
            try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Canceled."); } catch { }
            return true;
        }

        dndState.PendingDraftAction = null;
        await SaveStateAsync(channelState, dndState, ct);
        return await ExecutePendingDraftActionAsync(context, channelState, message, pending, ct);
    }

    private async Task<bool> ExecutePendingDraftActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        PendingDraftActionRequest pending,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || pending == null)
        {
            return false;
        }

        var actionType = (pending.ActionType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actionType))
        {
            try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Pending draft action is invalid."); } catch { }
            return true;
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);

        try
        {
            if (string.Equals(actionType, PendingActionCampaignCreate, StringComparison.OrdinalIgnoreCase))
            {
                var argsJson = string.IsNullOrWhiteSpace(pending.ArgumentsJson) ? "{}" : pending.ArgumentsJson;
                var res = await ExecuteCampaignCreateAsync(execCtx, argsJson, ct);
                var reply = res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response)
                    ? $"<@{message.Author.Id}>\n{res.Response.Trim()}"
                    : $"<@{message.Author.Id}> Sorry, campaign creation failed. Try `/gptcli dnd campaigncreate`.";
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }

            if (string.Equals(actionType, PendingActionDraftUpdate, StringComparison.OrdinalIgnoreCase))
            {
                var argsJson = string.IsNullOrWhiteSpace(pending.ArgumentsJson) ? "{}" : pending.ArgumentsJson;
                var res = await ExecuteDraftUpdateAsync(execCtx, argsJson, ct);
                var reply = res is { Handled: true } && !string.IsNullOrWhiteSpace(res.Response)
                    ? $"<@{message.Author.Id}>\n{res.Response.Trim()}"
                    : $"<@{message.Author.Id}> Sorry, draft update failed.";
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }

            if (string.Equals(actionType, PendingActionPartyEdit, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseJsonElement(pending.ArgumentsJson, out var root))
                {
                    try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Pending party change is invalid JSON."); } catch { }
                    return true;
                }

                var wantsAdd = TryGetPropertyIgnoreCase(root, "add", out var addEl) && addEl.ValueKind is JsonValueKind.True or JsonValueKind.False && addEl.GetBoolean();
                var wantsRemove = TryGetPropertyIgnoreCase(root, "remove", out var remEl) && remEl.ValueKind is JsonValueKind.True or JsonValueKind.False && remEl.GetBoolean();
                if (wantsAdd == wantsRemove)
                {
                    try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Pending party change was ambiguous."); } catch { }
                    return true;
                }

                var userIds = new List<ulong>();
                if (TryGetPropertyIgnoreCase(root, "userIds", out var usersEl) && usersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in usersEl.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out var uid) && uid != 0)
                        {
                            userIds.Add(uid);
                        }
                        else if (el.ValueKind == JsonValueKind.String && ulong.TryParse(el.GetString(), out uid) && uid != 0)
                        {
                            userIds.Add(uid);
                        }
                    }
                }

                var npcIds = new List<string>();
                if (TryGetPropertyIgnoreCase(root, "npcIds", out var npcsEl) && npcsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in npcsEl.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var id = (el.GetString() ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                npcIds.Add(id);
                            }
                        }
                    }
                }

                var (applied, errors) = await ExecuteDraftPartyEditCoreAsync(
                    context,
                    channelState,
                    message,
                    wantsAdd,
                    userIds,
                    npcIds,
                    ct);

                var replyLines = new List<string> { $"<@{message.Author.Id}> Updated the draft party roster." };
                if (applied.Count > 0)
                {
                    replyLines.Add("Applied:");
                    replyLines.AddRange(applied.Select(x => $"- {x}"));
                }
                if (errors.Count > 0)
                {
                    replyLines.Add("Some requests were ignored:");
                    replyLines.AddRange(errors.Take(8).Select(e => $"- {e}"));
                }

                var reply = TrimToLimit(string.Join("\n", replyLines), 3500);
                await SendChunkedAsync(message.Channel, reply);
                if (TryRecordAssistantReply(channelState, reply))
                {
                    try { await context.Host.SaveCachedChannelStateAsync(message.Channel.Id); } catch { }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] pending action failed type={actionType}: {ex.GetType().Name} {ex.Message}");
            try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Pending draft action failed: {ex.Message}"); } catch { }
            return true;
        }

        try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Unknown pending draft action type `{actionType}`."); } catch { }
        return true;
    }

    private async Task<GptCliExecutionResult> ExecuteStatusAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var campaignName = st.ActiveCampaignName ?? "default";

            DndLiteCampaignDocument campaign = null;
            DndLiteCampaignCatalogDocument draft = null;
            DndLitePartyDocument party;

            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                draft = await LoadDraftCampaignAsync(ctx.ChannelState, campaignName, ct);
                party = await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct);
            }
            else
            {
                campaign = await LoadCampaignAsync(ctx.ChannelState, campaignName, ct);
                party = await LoadPartyAsync(ctx.ChannelState, campaignName, ct);
            }

            DndCampaignSnapshot snap = null;
            if (campaign?.RunnerState != null)
            {
                var runner = RestoreCampaignRunner(ctx.ChannelState.ChannelId, campaign.RunnerState);
                snap = runner.GetState();
            }

            // Party roster can come from party.json (explicit roster) and/or the campaign runner snapshot (runtime roster).
            // Status should reflect the effective roster even if the party.json wasn't updated (e.g., older runs).
            var pcIds = new HashSet<ulong>();
            var npcIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (party?.PlayerUserIds != null)
            {
                foreach (var id in party.PlayerUserIds)
                {
                    if (id != 0)
                    {
                        pcIds.Add(id);
                    }
                }
            }

            if (party?.NpcActorIds != null)
            {
                foreach (var id in party.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    npcIds.Add(id.Trim());
                }
            }

            if (snap?.Party != null)
            {
                foreach (var actorId in snap.Party.Keys.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    if (IsNpcActorId(actorId))
                    {
                        npcIds.Add(actorId.Trim());
                    }
                    else if (IsPcActorId(actorId))
                    {
                        var raw = actorId.Trim();
                        if (raw.Length > 2 && ulong.TryParse(raw[2..], out var uid) && uid != 0)
                        {
                            pcIds.Add(uid);
                        }
                    }
                }
            }

            var pcCount = pcIds.Count;
            var npcCount = npcIds.Count;
            var partyTotal = pcCount + npcCount;

            var sb = new StringBuilder();
            sb.AppendLine("DND status");
            sb.AppendLine($"- channel: {ctx.ChannelState?.ChannelId} guild: {ctx.ChannelState?.GuildId}");
            sb.AppendLine($"- storage: {GetLiteRootDirectory(ctx.ChannelState)}");
            sb.AppendLine($"- mode: {mode}");
            sb.AppendLine($"- active campaign: {campaignName}");
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- draft exists: {(draft != null ? "true" : "false")}");
                sb.AppendLine($"- draft templates: {draft?.EncounterTemplates?.Count ?? 0}");
                var savedPath = ResolveCampaignPath(ctx.ChannelState, campaignName);
                var savedExists = false;
                try { savedExists = File.Exists(savedPath); } catch { savedExists = false; }
                sb.AppendLine($"- saved campaign exists: {(savedExists ? "true" : "false")}");
            }
            else
            {
                sb.AppendLine($"- campaign exists: {(campaign != null ? "true" : "false")}");
                if (campaign != null && campaign.UpdatedUtc != default)
                {
                    sb.AppendLine($"- catalog updated: {campaign.UpdatedUtc:O}");
                }
                if (campaign != null && campaign.RunUpdatedUtc != default)
                {
                    sb.AppendLine($"- run updated: {campaign.RunUpdatedUtc:O}");
                }
                sb.AppendLine($"- templates: {campaign?.EncounterTemplates?.Count ?? 0}");
            }
            sb.AppendLine($"- party members: {partyTotal} (pcs={pcCount}, npcs={npcCount})");

            if (npcIds.Count > 0)
            {
                var npcList = new List<string>();
                foreach (var id in npcIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(6))
                {
                    var npc = await LoadNpcProfileAsync(ctx.ChannelState, id, ct);
                    npcList.Add(npc == null || string.IsNullOrWhiteSpace(npc.Name) ? id : npc.Name.Trim());
                }
                sb.AppendLine($"- npcs: {string.Join(", ", npcList)}");
            }

            if (snap != null)
            {
                sb.AppendLine($"- campaign party snapshot: {RenderPartySummary(snap)}");
            }

            if (snap?.ActiveEncounterState != null)
            {
                var enc = snap.ActiveEncounterState;
                sb.AppendLine($"- active encounter: {snap.ActiveEncounterId} ({snap.ActiveEncounterName})");
                sb.AppendLine($"- phase: {enc.Phase}, round: {enc.RoundNumber}, current: {enc.CurrentActorId}");
                sb.AppendLine($"- pending rolls: {campaign?.RunnerState?.ActiveEncounter?.PendingRolls?.Count ?? 0}");
            }
            else
            {
                sb.AppendLine("- active encounter: none");
            }

            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) && draft?.EncounterTemplates is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine(RenderEncounterTemplates(new DndLiteCampaignDocument
                {
                    CampaignName = draft.CampaignName,
                    CampaignMarkdown = draft.CampaignMarkdown,
                    EncounterTemplates = draft.EncounterTemplates
                }));
            }
            else if (campaign?.EncounterTemplates is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine(RenderEncounterTemplates(campaign));
            }

            sb.AppendLine();
            sb.AppendLine("Game commands: `!help`, `!state`, `!targets`, `!attack <target>`, `!cast <target>`, `!pass`, `!rollall`, `!roll initiative`, `!roll <rollId>`, `!ledger [n]`");

            return new GptCliExecutionResult(true, TrimToLimit(sb.ToString().Trim(), 3500), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private static string RenderEncounterTemplates(DndLiteCampaignDocument campaign)
    {
        if (campaign?.EncounterTemplates == null || campaign.EncounterTemplates.Count == 0)
        {
            return "Encounter templates: (none)";
        }

        static string OneLine(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            var line = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length <= max)
            {
                return line;
            }

            if (max <= 3)
            {
                return line[..max];
            }

            return line[..(max - 3)].TrimEnd() + "...";
        }

        var list = (campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
            .OrderBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Encounter templates ({list.Count}):");

        // Keep status output readable: show a handful in detail, but be deterministic.
        foreach (var t in list.Take(8))
        {
            var name = string.IsNullOrWhiteSpace(t.Name) ? t.TemplateId : t.Name.Trim();
            sb.AppendLine($"- {t.TemplateId} | {name}");

            var scene = OneLine(t.Scene, 160);
            if (!string.IsNullOrWhiteSpace(scene))
            {
                sb.AppendLine($"  scene: {scene}");
            }

            var rewards = OneLine(t.Rewards, 120);
            if (!string.IsNullOrWhiteSpace(rewards))
            {
                sb.AppendLine($"  rewards: {rewards}");
            }

            var bossName = t.Boss?.Name;
            if (string.IsNullOrWhiteSpace(bossName))
            {
                bossName = t.Boss?.ActorId;
            }

            if (t.Mechanics?.Boss != null)
            {
                var b = t.Mechanics.Boss;
                sb.AppendLine($"  boss: {OneLine(bossName, 60)} | HP {b.MaxHp} MP {b.MaxMp} | STR {b.Stats?.Str} DEF {b.Stats?.Def} DEX {b.Stats?.Dex} SP {b.Stats?.SpellPower} LUCK {b.Stats?.Luck}");
            }
            else if (!string.IsNullOrWhiteSpace(bossName))
            {
                sb.AppendLine($"  boss: {OneLine(bossName, 80)}");
            }

            var addNames = (t.Mechanics?.Adds ?? Array.Empty<DndActorDefinition>())
                .Where(a => a != null)
                .Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.ActorId : a.Name.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(8)
                .ToList();
            if (addNames.Count > 0)
            {
                sb.AppendLine($"  adds: {addNames.Count} [{string.Join(", ", addNames)}]");
            }
            else
            {
                var addCount = t.Adds?.Count ?? 0;
                sb.AppendLine($"  adds: {addCount}");
            }
        }

        if (list.Count > 8)
        {
            sb.AppendLine($"- ... +{list.Count - 8} more");
        }

        return TrimToLimit(sb.ToString().Trim(), 2200);
    }

    private async Task<GptCliExecutionResult> ExecuteModeAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "value", out var modeRaw) || string.IsNullOrWhiteSpace(modeRaw))
        {
            return new GptCliExecutionResult(true, "Provide `value` as off, draft, or game.", false);
        }

        var mode = NormalizeMode(modeRaw);
        TryGetStringArg(argsJson, "campaign", out var campaignName);

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var channelStateChanged = false;

            // Safety: while in GAME mode, require slash command to switch back to DRAFT/OFF.
            // This avoids in-character lines accidentally being interpreted as mode switches.
            var currentMode = NormalizeMode(st.Mode);
            if (string.Equals(currentMode, ModeGame, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) &&
                ctx.SlashCommand == null)
            {
                return new GptCliExecutionResult(
                    true,
                    "You're currently in `game` mode. To switch modes, use the slash command: `/gptcli dnd mode value:draft` (or `off`).",
                    false);
            }

            if (!string.IsNullOrWhiteSpace(campaignName))
            {
                st.ActiveCampaignName = campaignName.Trim();
            }

            // Switching to GAME requires a finalized catalog campaign. If we have a draft, finalize it now.
            if (mode == ModeGame)
            {
                var finalizeRes = await TryFinalizeDraftOnGameSwitchAsync(ctx, st, overwrite: false, ct);
                if (finalizeRes != null)
                {
                    // Keep the user in DRAFT mode until the draft can be finalized.
                    return finalizeRes;
                }
            }
            st.Mode = mode;

            if (mode == ModeGame && !st.ModuleMutedBot)
            {
                st.PreviousBotMuted = ctx.ChannelState.Options.Muted;
                st.ModuleMutedBot = true;
                ctx.ChannelState.Options.Muted = true;
                channelStateChanged = true;
            }
            if (mode != ModeGame && st.ModuleMutedBot)
            {
                ctx.ChannelState.Options.Muted = st.PreviousBotMuted;
                st.ModuleMutedBot = false;
                channelStateChanged = true;
            }

            await SaveStateAsync(ctx.ChannelState, st, ct);

            if (mode == ModeGame)
            {
                EnsureTickLoopRunning(ctx.Channel.Id);
            }
            else
            {
                StopTickLoop(ctx.Channel.Id);
            }

            var extra = mode == ModeDraft
                ? "\nNext: draft your campaign in chat (untagged) or use `/gptcli dnd campaigncreate`."
                : string.Empty;
            return new GptCliExecutionResult(true, $"DND mode set to `{mode}` for campaign \"{st.ActiveCampaignName}\".{extra}", channelStateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> TryFinalizeDraftOnGameSwitchAsync(
        GptCliExecutionContext ctx,
        DndLiteChannelState st,
        bool overwrite,
        CancellationToken ct)
    {
        if (ctx == null || st == null)
        {
            return new GptCliExecutionResult(true, "Internal error: missing context.", false);
        }

        var campaignName = st.ActiveCampaignName ?? "default";

        // If the campaign is already saved, no need to finalize.
        var savedPath = ResolveCampaignPath(ctx.ChannelState, campaignName);
        var savedExists = false;
        try { savedExists = File.Exists(savedPath); } catch { savedExists = false; }
        if (savedExists)
        {
            return null;
        }

        // If there's no draft either, we can't enter game mode.
        var draft = await LoadDraftCampaignAsync(ctx.ChannelState, campaignName, ct);
        if (draft == null)
        {
            return new GptCliExecutionResult(
                true,
                $"No saved campaign found for \"{campaignName}\". Create a draft in draft mode first (describe it in chat or run `/gptcli dnd campaigncreate`).",
                false);
        }

        // Finalize draft into catalog before game mode begins.
        // NOTE: this runs under the same channel lock as ExecuteModeAsync, so do not call
        // ExecuteCampaignFinalizeAsync (it would deadlock trying to take the lock again).
        var res = await FinalizeDraftCoreAsync(ctx, st, campaignName, overwrite, ct);

        // If finalize succeeded, the saved campaign file should exist now.
        var savedNow = false;
        try { savedNow = File.Exists(savedPath); } catch { savedNow = false; }
        return savedNow ? null : res;
    }

    private async Task<GptCliExecutionResult> FinalizeDraftCoreAsync(
        GptCliExecutionContext ctx,
        DndLiteChannelState st,
        string campaignName,
        bool overwrite,
        CancellationToken ct)
    {
        var draft = await LoadDraftCampaignAsync(ctx.ChannelState, campaignName, ct);
        if (draft == null)
        {
            return new GptCliExecutionResult(true, $"No draft found for \"{campaignName}\". Build one with `/gptcli dnd campaigncreate` (or describe it in draft mode).", false);
        }

        var catalogPath = ResolveCampaignPath(ctx.ChannelState, campaignName);
        var savedExists = false;
        try { savedExists = File.Exists(catalogPath); } catch { savedExists = false; }
        if (savedExists && !overwrite)
        {
            return new GptCliExecutionResult(
                true,
                $"A saved campaign already exists for \"{campaignName}\". Re-run `/gptcli dnd campaignfinalize name:\"{campaignName}\" overwrite:true` to overwrite it, or pick a different campaign name.",
                false);
        }

        // Write run roster from draft party so runner bootstrap and live mode are consistent.
        var partyDraft = await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument();
        await SavePartyAsync(ctx.ChannelState, campaignName, partyDraft, ct);

        var doc = new DndLiteCampaignDocument
        {
            CampaignName = campaignName,
            CampaignMarkdown = draft.CampaignMarkdown ?? string.Empty,
            UpdatedUtc = DateTime.UtcNow,
            EncounterTemplates = draft.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>(),
            RunnerState = null,
            LiveRuntime = new DndLiteEncounterLiveRuntime()
        };

        // Bootstrap runner state from profiles now that run party.json exists.
        await NormalizeCampaignRunnerStateFromProfilesAsync(ctx.ChannelState, doc, ct);

        await SaveCampaignAsync(ctx.ChannelState, doc, ct);

        // Clear the draft so subsequent builds start fresh.
        await DeleteDraftAsync(ctx.ChannelState, campaignName, ct);

        st.ActiveCampaignName = campaignName;
        await SaveStateAsync(ctx.ChannelState, st, ct);

        return new GptCliExecutionResult(true, $"Campaign \"{campaignName}\" finalized and saved. You can now use game mode.", true);
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        var reqId = Guid.NewGuid().ToString("n")[..8];
        // name is optional: defaults to active campaign.
        TryGetStringArg(argsJson, "name", out var campaignNameRaw);
        if (!TryGetStringArg(argsJson, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            return new GptCliExecutionResult(true, "Provide `prompt` (and optionally `name`).", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            using var typing = DiscordTyping.Begin(ctx.Channel);

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaignName = !string.IsNullOrWhiteSpace(campaignNameRaw)
                ? campaignNameRaw.Trim()
                : (st.ActiveCampaignName ?? "default");

            var partyDoc = await LoadDraftPartyAsync(ctx.ChannelState, campaignName.Trim(), ct) ?? new DndLitePartyDocument();
            partyDoc.PlayerUserIds ??= new List<ulong>();
            partyDoc.NpcActorIds ??= new List<string>();
            if (ctx.User?.Id != 0 && !partyDoc.PlayerUserIds.Contains(ctx.User.Id))
            {
                // In draft mode, default the author into the roster so the campaign generator can map at least one PC.
                partyDoc.PlayerUserIds.Add(ctx.User.Id);
            }

            var pcRosterContext = await BuildPcRosterContextAsync(ctx.ChannelState, partyDoc, ct);
            Console.WriteLine(
                $"[dnd] campaigncreate[{reqId}]: start campaign=\"{campaignName}\" promptLen={prompt.Trim().Length} pcRosterLen={(pcRosterContext ?? string.Empty).Length} (channel={ctx.Channel?.Id})");

            async Task Progress(string text)
                => await SendProgressUpdateAsync(ctx.Channel, text, ctx.Context);

            await Progress($"Campaign generation started for \"{campaignName}\".");
            var gen = await GenerateCampaignPackageAsync(
                ctx.Context,
                ctx.ChannelState,
                reqId,
                campaignName.Trim(),
                prompt.Trim(),
                pcRosterContext,
                ct,
                Progress);
            var pkg = gen?.Package;
            if (pkg == null)
            {
                var reason = string.IsNullOrWhiteSpace(gen?.Error) ? "Unknown error." : gen.Error.Trim();
                Console.WriteLine($"[dnd] campaigncreate[{reqId}]: generation failed model={gen?.Model} httpMs={gen?.HttpMs} err={reason}");
                await Progress($"Campaign generation failed: {TrimToLimit(reason, 220)}");
                return new GptCliExecutionResult(true, $"Campaign generation failed: {reason}", false);
            }
            if (string.IsNullOrWhiteSpace(pkg.CampaignMarkdown))
            {
                Console.WriteLine(
                    $"[dnd] campaigncreate[{reqId}]: generation returned empty markdown model={gen?.Model} httpMs={gen?.HttpMs} encounters={(pkg.Encounters?.Count ?? 0)} fnCalls={(pkg.FunctionCalls?.Count ?? 0)}");
                await Progress("Campaign generation failed: model returned empty markdown.");
                return new GptCliExecutionResult(true, "Campaign generation failed: empty campaignMarkdown returned by OpenAI.", false);
            }

            var doc = new DndLiteCampaignDocument
            {
                CampaignName = campaignName.Trim(),
                CampaignMarkdown = TrimToLimit(pkg.CampaignMarkdown.Trim(), MaxCampaignChars),
                UpdatedUtc = DateTime.UtcNow,
                EncounterTemplates = new List<DndLiteEncounterTemplateDocument>()
            };

            foreach (var e in pkg.Encounters ?? new List<EncounterTemplateDto>())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.TemplateId) || e.Boss == null)
                {
                    continue;
                }

                var templateId = SlugifySegment(e.TemplateId);
                var bossActorId = $"{templateId}:{SlugifySegment(e.Boss.Id ?? "boss")}";
                var boss = ToEnemyDef(bossActorId, e.Boss.Name, isBoss: true, e.Boss.Stats, e.Boss.MaxHp, e.Boss.MaxMp);

                var adds = new List<DndActorDefinition>();
                var addDescs = new List<DndLiteActorDescriptor>();
                foreach (var add in e.Adds ?? new List<ActorDto>())
                {
                    if (add == null)
                    {
                        continue;
                    }

                    var addActorId = $"{templateId}:{SlugifySegment(add.Id ?? add.Name ?? "add")}";
                    adds.Add(ToEnemyDef(addActorId, add.Name, isBoss: false, add.Stats, add.MaxHp, add.MaxMp));
                    addDescs.Add(new DndLiteActorDescriptor
                    {
                        ActorId = addActorId,
                        Name = add.Name ?? addActorId,
                        Description = add.Description ?? string.Empty
                    });
                }

                doc.EncounterTemplates.Add(new DndLiteEncounterTemplateDocument
                {
                    TemplateId = templateId,
                    Name = string.IsNullOrWhiteSpace(e.Name) ? templateId : e.Name.Trim(),
                    Scene = e.Scene ?? string.Empty,
                    Rewards = e.Rewards ?? string.Empty,
                    Boss = new DndLiteActorDescriptor
                    {
                        ActorId = bossActorId,
                        Name = e.Boss.Name ?? "Boss",
                        Description = e.Boss.Description ?? string.Empty
                    },
                    Adds = addDescs,
                    Mechanics = new DndEncounterTemplate(
                        TemplateId: templateId,
                        Name: string.IsNullOrWhiteSpace(e.Name) ? templateId : e.Name.Trim(),
                        Boss: boss,
                        Adds: adds)
                });
            }

            // Build draft (greenfield) and reset mechanics state.
            doc.RunnerState = null;
            await SaveDraftCampaignAsync(ctx.ChannelState, doc, ct);

            st.ActiveCampaignName = doc.CampaignName;
            await SaveStateAsync(ctx.ChannelState, st, ct);

            // Apply party-related sheets from the campaign create response (if any),
            // then persist the party roster as a draft roster so the user can iterate before finalizing.
            var (npcsWritten, pcsWritten) = await ApplyCampaignCreatePartyEditsAsync(ctx.ChannelState, doc.CampaignName, partyDoc, pkg, ct);
            await SaveDraftPartyAsync(ctx.ChannelState, doc.CampaignName, partyDoc, ct);

            var summary = BuildDraftCampaignSummary(doc);
            if (npcsWritten.Count > 0 || pcsWritten.Count > 0 || (pkg.UnassignedPcs?.Count ?? 0) > 0)
            {
                summary += "\n\n" + BuildCampaignCreatePartySummary(npcsWritten, pcsWritten, pkg.UnassignedPcs);
            }
            await Progress(
                $"Campaign generation complete: templates={doc.EncounterTemplates?.Count ?? 0}, npcSheets={npcsWritten.Count}, pcSheets={pcsWritten.Count}.");
            Console.WriteLine(
                $"[dnd] campaigncreate[{reqId}]: done campaign=\"{doc.CampaignName}\" templates={(doc.EncounterTemplates?.Count ?? 0)} npcs={npcsWritten.Count} pcs={pcsWritten.Count} unassigned={(pkg.UnassignedPcs?.Count ?? 0)}");
            return new GptCliExecutionResult(true, summary, true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignFinalizeAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        // name is optional: defaults to active campaign.
        TryGetStringArg(argsJson, "name", out var campaignNameRaw);
        TryGetBoolArg(argsJson, "overwrite", out var overwrite);

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaignName = !string.IsNullOrWhiteSpace(campaignNameRaw)
                ? campaignNameRaw.Trim()
                : (st.ActiveCampaignName ?? "default");

            return await FinalizeDraftCoreAsync(ctx, st, campaignName, overwrite, ct);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private static string BuildDraftUpdatePrompt(string campaignName, string existingMarkdown, string modificationPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Return strict JSON only with key exactly: campaignMarkdown");
        sb.AppendLine("campaignMarkdown: revised markdown string describing the campaign premise, tone, and 3-6 bullet hooks.");
        sb.AppendLine("No markdown fences. No code fences.");
        sb.AppendLine();
        sb.AppendLine("You are applying the user's modification request to the existing campaign draft.");
        sb.AppendLine("Preserve what still fits; rewrite what conflicts; incorporate requested changes explicitly.");
        sb.AppendLine();
        sb.AppendLine($"Campaign name: {campaignName}");
        sb.AppendLine();
        sb.AppendLine("Existing campaign markdown:");
        sb.AppendLine(existingMarkdown ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("User modification request:");
        sb.AppendLine(modificationPrompt ?? string.Empty);
        return sb.ToString().Trim();
    }

    private async Task<GptCliExecutionResult> ExecuteDraftUpdateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        TryGetStringArg(argsJson, "name", out var campaignNameRaw);
        if (!TryGetStringArg(argsJson, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            return new GptCliExecutionResult(true, "Provide `prompt` (and optionally `name`).", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in draft mode. Use `/gptcli dnd mode value:draft`.", false);
            }

            var campaignName = !string.IsNullOrWhiteSpace(campaignNameRaw)
                ? campaignNameRaw.Trim()
                : (st.ActiveCampaignName ?? "default");

            var draft = await LoadDraftCampaignAsync(ctx.ChannelState, campaignName, ct);
            if (draft == null || string.IsNullOrWhiteSpace(draft.CampaignMarkdown))
            {
                return new GptCliExecutionResult(true, $"No draft found for \"{campaignName}\". Build one first with `/gptcli dnd campaigncreate`.", false);
            }

            using var typing = DiscordTyping.Begin(ctx.Channel);

            var requestPrompt = BuildDraftUpdatePrompt(campaignName, draft.CampaignMarkdown, prompt.Trim());
            Console.WriteLine($"[dnd] draftupdate: campaign=\"{campaignName}\" existingLen={draft.CampaignMarkdown.Length} modLen={prompt.Trim().Length} promptLen={requestPrompt.Length}");
            async Task Progress(string text)
                => await SendProgressUpdateAsync(ctx.Channel, text, ctx.Context);

            await Progress($"Draft update started for \"{campaignName}\".");
            var generated = await GenerateDraftUpdateMarkdownAsync(
                ctx.Context,
                ctx.ChannelState,
                campaignName,
                draft.CampaignMarkdown,
                prompt.Trim(),
                ct,
                Progress);
            if (!generated.Successful)
            {
                Console.WriteLine($"[dnd] draftupdate: generation failed err={generated.Error}");
                await Progress($"Draft update failed: {TrimToLimit(generated.Error, 220)}");
                return new GptCliExecutionResult(true, $"Draft update failed: {generated.Error}", false);
            }

            var updated = generated.CampaignMarkdown?.Trim();
            if (string.IsNullOrWhiteSpace(updated))
            {
                Console.WriteLine("[dnd] draftupdate: missing campaignMarkdown from responses loop");
                return new GptCliExecutionResult(true, "Draft update failed: no campaign markdown produced.", false);
            }

            draft.CampaignMarkdown = TrimToLimit(updated, MaxCampaignChars);
            draft.UpdatedUtc = DateTime.UtcNow;

            // Persist updated draft.
            await SaveDraftCampaignAsync(ctx.ChannelState, new DndLiteCampaignDocument
            {
                CampaignName = draft.CampaignName,
                CampaignMarkdown = draft.CampaignMarkdown,
                UpdatedUtc = draft.UpdatedUtc,
                EncounterTemplates = draft.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>()
            }, ct);

            st.ActiveCampaignName = draft.CampaignName;
            await SaveStateAsync(ctx.ChannelState, st, ct);
            await Progress("Draft update complete.");

            var sb = new StringBuilder();
            sb.AppendLine($"Draft updated for \"{draft.CampaignName}\".");
            sb.AppendLine();
            sb.AppendLine(draft.CampaignMarkdown ?? string.Empty);
            return new GptCliExecutionResult(true, sb.ToString().Trim(), true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecutePartyShowAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in draft/game mode. Use `/gptcli dnd mode value:draft` or `/gptcli dnd mode value:game`.", false);
            }

            var campaignName = st.ActiveCampaignName ?? "default";
            var party = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase)
                ? (await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument())
                : (await LoadPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument());
            party.PlayerUserIds ??= new List<ulong>();
            party.NpcActorIds ??= new List<string>();

            var lines = new List<string> { $"Draft party for \"{campaignName}\":" };
            if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                lines[0] = $"Party for \"{campaignName}\":";
            }

            if (party.PlayerUserIds.Count > 0)
            {
                lines.Add("PCs:");
                foreach (var uid in party.PlayerUserIds.Distinct().OrderBy(x => x))
                {
                    var pc = await LoadPcProfileAsync(ctx.ChannelState, uid, ct);
                    if (pc == null)
                    {
                        lines.Add($"- <@{uid}> (sheet missing)");
                    }
                    else
                    {
                        lines.Add($"- <@{uid}> | {pc.Name}");
                    }
                }
            }
            else
            {
                lines.Add("PCs: (none)");
            }

            if (party.NpcActorIds.Count > 0)
            {
                lines.Add("NPCs:");
                foreach (var id in party.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    var npc = await LoadNpcProfileAsync(ctx.ChannelState, id, ct);
                    lines.Add(npc == null ? $"- {id} (sheet missing)" : $"- {id} | {npc.Name}");
                }
            }
            else
            {
                lines.Add("NPCs: (none)");
            }

            return new GptCliExecutionResult(true, TrimToLimit(string.Join("\n", lines), 3500), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecutePartyRemoveNpcAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        // Alias for `npcremove` so "party remove npc" is more discoverable (and reads better for natural language).
        return await ExecuteNpcRemoveAsync(ctx, argsJson, ct);
    }

    private async Task<GptCliExecutionResult> ExecutePartyAddPcAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetUlongArg(argsJson, "user", out var userId) || userId == 0)
        {
            return new GptCliExecutionResult(true, "Provide `user`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in draft mode. Use `/gptcli dnd mode value:draft`.", false);
            }

            var campaignName = st.ActiveCampaignName ?? "default";
            var party = await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument();
            party.PlayerUserIds ??= new List<ulong>();
            if (!party.PlayerUserIds.Contains(userId))
            {
                party.PlayerUserIds.Add(userId);
                await SaveDraftPartyAsync(ctx.ChannelState, campaignName, party, ct);
            }

            var pc = await LoadPcProfileAsync(ctx.ChannelState, userId, ct);
            var note = pc == null ? " (sheet missing; ask them to run `/gptcli dnd charactercreate`)" : $" ({pc.Name})";
            return new GptCliExecutionResult(true, $"Added PC to draft party: <@{userId}>{note}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecutePartyRemovePcAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetUlongArg(argsJson, "user", out var userId) || userId == 0)
        {
            return new GptCliExecutionResult(true, "Provide `user`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in draft mode. Use `/gptcli dnd mode value:draft`.", false);
            }

            var campaignName = st.ActiveCampaignName ?? "default";
            var party = await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument();
            party.PlayerUserIds ??= new List<ulong>();
            var before = party.PlayerUserIds.Count;
            party.PlayerUserIds = party.PlayerUserIds.Where(x => x != userId).ToList();
            var changed = party.PlayerUserIds.Count != before;
            if (changed)
            {
                await SaveDraftPartyAsync(ctx.ChannelState, campaignName, party, ct);
            }

            return new GptCliExecutionResult(true, $"Removed PC from draft party: <@{userId}>", changed);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecutePartyAddNpcAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "id", out var actorIdRaw) || string.IsNullOrWhiteSpace(actorIdRaw))
        {
            return new GptCliExecutionResult(true, "Provide `id`.", false);
        }

        var actorId = actorIdRaw.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in draft mode. Use `/gptcli dnd mode value:draft`.", false);
            }

            if (!IsNpcActorId(actorId))
            {
                return new GptCliExecutionResult(true, "NPC id must start with `npc:`.", false);
            }

            var npc = await LoadNpcProfileAsync(ctx.ChannelState, actorId, ct);
            if (npc == null)
            {
                return new GptCliExecutionResult(true, $"No NPC profile found for `{actorId}`. Create one with `/gptcli dnd npccreate`.", false);
            }

            var campaignName = st.ActiveCampaignName ?? "default";
            var party = await LoadDraftPartyAsync(ctx.ChannelState, campaignName, ct) ?? new DndLitePartyDocument();
            party.NpcActorIds ??= new List<string>();
            if (!party.NpcActorIds.Any(x => string.Equals(x, actorId, StringComparison.OrdinalIgnoreCase)))
            {
                party.NpcActorIds.Add(actorId);
                await SaveDraftPartyAsync(ctx.ChannelState, campaignName, party, ct);
            }

            return new GptCliExecutionResult(true, $"Added NPC to draft party: `{actorId}` | {npc.Name}", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<(List<string> npcsWritten, List<string> pcsWritten)> ApplyCampaignCreatePartyEditsAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        DndLitePartyDocument partyDoc,
        CampaignCreateResponseDto pkg,
        CancellationToken ct)
    {
        partyDoc ??= new DndLitePartyDocument();
        partyDoc.PlayerUserIds ??= new List<ulong>();
        partyDoc.NpcActorIds ??= new List<string>();

        var npcsWritten = new List<string>();
        var pcsWritten = new List<string>();

        foreach (var call in pkg?.FunctionCalls ?? new List<DndLiteFunctionCallDto>())
        {
            if (call == null || string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            var fn = call.Name.Trim();
            if (string.Equals(fn, "dnd_create_npc_sheet", StringComparison.OrdinalIgnoreCase))
            {
                var args = TryDeserializeArgs<DndCreateNpcSheetArgsDto>(call.Arguments);
                if (args == null || string.IsNullOrWhiteSpace(args.Name))
                {
                    continue;
                }

                var actorId = BuildNpcActorId(args.Id, args.Name);
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    actorId = await AllocateNpcActorIdAsync(channelState, args.Name.Trim(), ct);
                }
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    continue;
                }

                var stats = args.Stats ?? new DndStats(10, 10, 10, 10, 10);
                var npc = new DndLiteNpcProfile
                {
                    ActorId = actorId,
                    IsNpc = true,
                    Name = args.Name.Trim(),
                    Concept = (args.Concept ?? string.Empty).Trim(),
                    ProfileMarkdown = (args.ProfileMarkdown ?? string.Empty).Trim(),
                    PersonalityNotes = (args.PersonalityNotes ?? string.Empty).Trim(),
                    MaxHp = Math.Clamp(args.MaxHp <= 0 ? 20 : args.MaxHp, 1, 250),
                    MaxMp = Math.Clamp(args.MaxMp, 0, 100),
                    Stats = new DndStats(
                        Str: Math.Clamp(stats.Str, 6, 20),
                        Def: Math.Clamp(stats.Def, 6, 20),
                        Dex: Math.Clamp(stats.Dex, 6, 20),
                        SpellPower: Math.Clamp(stats.SpellPower, 6, 20),
                        Luck: Math.Clamp(stats.Luck, 6, 20))
                };

                await SaveNpcProfileAsync(channelState, npc, ct);
                if (!partyDoc.NpcActorIds.Any(x => string.Equals(x, npc.ActorId, StringComparison.OrdinalIgnoreCase)))
                {
                    partyDoc.NpcActorIds.Add(npc.ActorId);
                }

                npcsWritten.Add($"{npc.ActorId} | {npc.Name}");
                continue;
            }

            if (string.Equals(fn, "dnd_create_pc_sheet", StringComparison.OrdinalIgnoreCase))
            {
                var args = TryDeserializeArgs<DndCreatePcSheetArgsDto>(call.Arguments);
                if (args == null || string.IsNullOrWhiteSpace(args.ActorId) || string.IsNullOrWhiteSpace(args.Name))
                {
                    continue;
                }

                if (!TryParsePcActorId(args.ActorId, out var userId))
                {
                    continue;
                }

                var stats = args.Stats ?? new DndStats(10, 10, 10, 10, 10);
                var pc = new DndLitePcProfile
                {
                    UserId = userId,
                    ActorId = ToActorId(userId),
                    Name = args.Name.Trim(),
                    Concept = (args.Concept ?? string.Empty).Trim(),
                    ProfileMarkdown = (args.ProfileMarkdown ?? string.Empty).Trim(),
                    MaxHp = Math.Clamp(args.MaxHp <= 0 ? 20 : args.MaxHp, 1, 250),
                    MaxMp = Math.Clamp(args.MaxMp, 0, 100),
                    Stats = new DndStats(
                        Str: Math.Clamp(stats.Str, 6, 20),
                        Def: Math.Clamp(stats.Def, 6, 20),
                        Dex: Math.Clamp(stats.Dex, 6, 20),
                        SpellPower: Math.Clamp(stats.SpellPower, 6, 20),
                        Luck: Math.Clamp(stats.Luck, 6, 20))
                };

                await SavePcProfileAsync(channelState, pc, ct);
                if (!partyDoc.PlayerUserIds.Contains(userId))
                {
                    partyDoc.PlayerUserIds.Add(userId);
                }

                pcsWritten.Add($"{userId} | {pc.Name}");
            }
        }

        // If the campaign generator mentioned PCs it couldn't map, convert them into NPC companions for this run.
        foreach (var u in pkg?.UnassignedPcs ?? new List<UnassignedPcDto>())
        {
            if (u == null || string.IsNullOrWhiteSpace(u.Name))
            {
                continue;
            }

            var actorId = await AllocateNpcActorIdAsync(channelState, u.Name.Trim(), ct);
            if (string.IsNullOrWhiteSpace(actorId))
            {
                continue;
            }

            var npc = new DndLiteNpcProfile
            {
                ActorId = actorId,
                IsNpc = true,
                Name = u.Name.Trim(),
                Concept = (u.Concept ?? "Companion").Trim(),
                ProfileMarkdown = $"Companion NPC generated from campaign prompt.\n\nConcept: {(u.Concept ?? string.Empty).Trim()}".Trim(),
                PersonalityNotes = string.Empty,
                MaxHp = 20,
                MaxMp = 5,
                Stats = new DndStats(10, 10, 10, 10, 10)
            };

            await SaveNpcProfileAsync(channelState, npc, ct);
            if (!partyDoc.NpcActorIds.Any(x => string.Equals(x, npc.ActorId, StringComparison.OrdinalIgnoreCase)))
            {
                partyDoc.NpcActorIds.Add(npc.ActorId);
            }

            npcsWritten.Add($"{npc.ActorId} | {npc.Name} (from unassigned PC)");
        }

        return (npcsWritten, pcsWritten);
    }

    private static string BuildDraftCampaignSummary(DndLiteCampaignDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Draft campaign \"{doc.CampaignName}\" built (not finalized).");
        sb.AppendLine("To finalize and save into the catalog, switch to game mode: `/gptcli dnd mode value:game` (or run `/gptcli dnd campaignfinalize`).");
        sb.AppendLine();
        sb.AppendLine(doc.CampaignMarkdown ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("Encounter templates:");
        foreach (var t in (doc.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>()).Take(12))
        {
            if (t == null)
            {
                continue;
            }

            sb.AppendLine($"- {t.TemplateId} | {t.Name} | adds={(t.Adds?.Count ?? 0)}");
        }

        return sb.ToString().Trim();
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignListAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var root = GetLiteRootDirectory(ctx.ChannelState);
            var campaignsDir = Path.Combine(root, "campaigns");
            var draftsDir = Path.Combine(root, "drafts");

            // List campaigns saved in this channel, and (when in a guild) also list campaigns saved in other channels
            // in the same guild. This helps when users create campaigns in #general but try to list in #dnd, etc.
            var items = await LoadCampaignCatalogAsync(ctx.ChannelState, campaignsDir, includeGuildWide: true, ct);
            var drafts = await LoadDraftCatalogAsync(ctx.ChannelState, draftsDir, ct);

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var active = st.ActiveCampaignName ?? "default";

            var sb = new StringBuilder();
            if (drafts.Count == 0 && items.Count == 0)
            {
                return new GptCliExecutionResult(true, "No drafts or saved campaigns yet.", false);
            }

            if (drafts.Count > 0)
            {
                sb.AppendLine($"Drafts ({drafts.Count}) [this channel]:");
                foreach (var d in drafts
                             .OrderByDescending(x => x.UpdatedUtc == default ? DateTime.MinValue : x.UpdatedUtc)
                             .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                             .Take(40))
                {
                    var isActive = string.Equals(d.Name, active, StringComparison.OrdinalIgnoreCase);
                    var stamp = d.UpdatedUtc == default ? "" : $" | updated {d.UpdatedUtc:yyyy-MM-dd HH:mm:ss}Z";
                    sb.AppendLine($"- {(isActive ? "*" : " ")} {d.Name} | templates {d.Templates}{stamp}");
                }
                if (drafts.Count > 40)
                {
                    sb.AppendLine($"- ... +{drafts.Count - 40} more");
                }
                sb.AppendLine();
                sb.AppendLine("Finalize a draft: `/gptcli dnd campaignfinalize name:<campaign>` (or switch to game mode).");
                sb.AppendLine();
            }

            if (items.Count > 0)
            {
                sb.AppendLine($"Saved campaigns ({items.Count}) [this channel + guild]:");
                foreach (var c in items
                             .OrderByDescending(x => x.UpdatedUtc == default ? DateTime.MinValue : x.UpdatedUtc)
                             .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                             .Take(40))
                {
                    var isActive = string.Equals(c.Name, active, StringComparison.OrdinalIgnoreCase);
                    var stamp = c.UpdatedUtc == default ? "" : $" | updated {c.UpdatedUtc:yyyy-MM-dd HH:mm:ss}Z";
                    sb.AppendLine($"- {(isActive ? "*" : " ")} {c.Name} | templates {c.Templates}{stamp}");
                }

                if (items.Count > 40)
                {
                    sb.AppendLine($"- ... +{items.Count - 40} more");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Use `/gptcli dnd campaignstart name:<campaign>` to set the active campaign.");

            return new GptCliExecutionResult(true, TrimToLimit(sb.ToString().Trim(), 3500), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<List<CampaignCatalogItem>> LoadDraftCatalogAsync(
        InstructionGPT.ChannelState channelState,
        string draftsDir,
        CancellationToken ct)
    {
        var items = new List<CampaignCatalogItem>();
        if (!Directory.Exists(draftsDir))
        {
            return items;
        }

        foreach (var dir in Directory.GetDirectories(draftsDir))
        {
            var draftJson = Path.Combine(dir, "draft.json");
            if (!File.Exists(draftJson))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(draftJson, ct);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var doc = JsonSerializer.Deserialize<DndLiteCampaignCatalogDocument>(json, _jsonOptions);
                if (doc == null || string.IsNullOrWhiteSpace(doc.CampaignName))
                {
                    continue;
                }

                items.Add(new CampaignCatalogItem(
                    Name: doc.CampaignName.Trim(),
                    UpdatedUtc: doc.UpdatedUtc,
                    Templates: doc.EncounterTemplates?.Count ?? 0,
                    Source: "draft"));
            }
            catch
            {
                // ignore malformed draft
            }
        }

        return items;
    }

    private async Task<GptCliExecutionResult> ExecuteCampaignStartAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return new GptCliExecutionResult(true, "Provide `name`.", false);
        }

        var wanted = name.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);

            var campaign = await LoadCampaignAsync(ctx.ChannelState, wanted, ct);
            if (campaign == null)
            {
                // Not found in this channel: attempt to import from another channel in the same guild.
                var imported = await TryImportCampaignFromGuildAsync(ctx.ChannelState, wanted, ct);
                if (imported == null)
                {
                    // In draft mode, allow selecting a non-saved draft name (so users can draft before finalizing).
                    if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
                    {
                        st.ActiveCampaignName = wanted;
                        await SaveStateAsync(ctx.ChannelState, st, ct);
                        return new GptCliExecutionResult(true, $"Active campaign set to \"{st.ActiveCampaignName}\" (draft).", true);
                    }

                    return new GptCliExecutionResult(true, $"Campaign not found: \"{wanted}\". Use `/gptcli dnd campaignlist`.", false);
                }

                campaign = imported;
            }

            st.ActiveCampaignName = campaign.CampaignName ?? wanted;
            await SaveStateAsync(ctx.ChannelState, st, ct);

            return new GptCliExecutionResult(true, $"Active campaign set to \"{st.ActiveCampaignName}\".", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private sealed record CampaignCatalogItem(string Name, DateTime UpdatedUtc, int Templates, string Source);

    private async Task<List<CampaignCatalogItem>> LoadCampaignCatalogAsync(
        InstructionGPT.ChannelState channelState,
        string campaignsDir,
        bool includeGuildWide,
        CancellationToken ct)
    {
        var items = new List<CampaignCatalogItem>();

        async Task ReadFromCampaignsDirAsync(string dirPath, string sourceLabel)
        {
            if (!Directory.Exists(dirPath))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(dirPath))
            {
                var campaignJson = Path.Combine(dir, "campaign.json");
                if (!File.Exists(campaignJson))
                {
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(campaignJson, ct);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var doc = JsonSerializer.Deserialize<DndLiteCampaignCatalogDocument>(json, _jsonOptions);
                    if (doc == null)
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(doc.CampaignName) ? Path.GetFileName(dir) : doc.CampaignName.Trim();
                    items.Add(new CampaignCatalogItem(
                        Name: name,
                        UpdatedUtc: doc.UpdatedUtc,
                        Templates: doc.EncounterTemplates?.Count ?? 0,
                        Source: sourceLabel));
                }
                catch
                {
                    // ignore broken entries
                }
            }
        }

        // Channel-local.
        await ReadFromCampaignsDirAsync(campaignsDir, sourceLabel: "this-channel");

        // Guild-wide discovery: enumerate sibling channel folders under the same guild folder and aggregate.
        if (includeGuildWide && channelState != null && channelState.GuildId != 0)
        {
            var guildRoot = GetGuildRootDirectory(channelState);
            if (Directory.Exists(guildRoot))
            {
                foreach (var chDir in Directory.GetDirectories(guildRoot))
                {
                    // Skip current channel directory; we already added those.
                    if (string.Equals(Path.GetFullPath(chDir).TrimEnd('/'), Path.GetFullPath(InstructionGPT.GetChannelDirectory(channelState)).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var dndRoot = Path.Combine(chDir, "dnd-lite");
                    var chCampaigns = Path.Combine(dndRoot, "campaigns");
                    if (!Directory.Exists(chCampaigns))
                    {
                        continue;
                    }

                    var source = $"channel:{Path.GetFileName(chDir)}";
                    await ReadFromCampaignsDirAsync(chCampaigns, source);
                }
            }
        }

        // De-dupe by name: keep latest UpdatedUtc.
        return items
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var best = g
                    .OrderByDescending(x => x.UpdatedUtc == default ? DateTime.MinValue : x.UpdatedUtc)
                    .ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                    .First();
                return best;
            })
            .OrderByDescending(x => x.UpdatedUtc == default ? DateTime.MinValue : x.UpdatedUtc)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetGuildRootDirectory(InstructionGPT.ChannelState channelState)
    {
        // The guild root is the parent folder of the channel directory:
        // channels/<guild>_<guildId>/<channel>_<channelId>
        var chDir = InstructionGPT.GetChannelDirectory(channelState);
        return Path.GetDirectoryName(chDir) ?? "channels";
    }

    private async Task<DndLiteCampaignDocument> TryImportCampaignFromGuildAsync(
        InstructionGPT.ChannelState channelState,
        string wantedName,
        CancellationToken ct)
    {
        if (channelState == null || channelState.GuildId == 0)
        {
            return null;
        }

        var wanted = wantedName?.Trim();
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return null;
        }

        var wantedSlug = SlugifySegment(wanted);
        var guildRoot = GetGuildRootDirectory(channelState);
        if (!Directory.Exists(guildRoot))
        {
            return null;
        }

        DndLiteCampaignCatalogDocument bestCatalog = null;
        DateTime bestUtc = DateTime.MinValue;

        foreach (var chDir in Directory.GetDirectories(guildRoot))
        {
            var chCampaigns = Path.Combine(chDir, "dnd-lite", "campaigns");
            if (!Directory.Exists(chCampaigns))
            {
                continue;
            }

            foreach (var campaignDir in Directory.GetDirectories(chCampaigns))
            {
                var campaignJson = Path.Combine(campaignDir, "campaign.json");
                if (!File.Exists(campaignJson))
                {
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(campaignJson, ct);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var doc = JsonSerializer.Deserialize<DndLiteCampaignCatalogDocument>(json, _jsonOptions);
                    if (doc == null)
                    {
                        continue;
                    }

                    var name = (doc.CampaignName ?? string.Empty).Trim();
                    var dirName = Path.GetFileName(campaignDir) ?? string.Empty;
                    var match =
                        string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, wantedSlug, StringComparison.OrdinalIgnoreCase);
                    if (!match)
                    {
                        continue;
                    }

                    var utc = doc.UpdatedUtc == default ? DateTime.MinValue : doc.UpdatedUtc;
                    if (bestCatalog == null || utc > bestUtc)
                    {
                        bestCatalog = doc;
                        bestUtc = utc;
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (bestCatalog == null)
        {
            return null;
        }

        // Import should be replayable only: catalog-only.
        var imported = new DndLiteCampaignDocument
        {
            CampaignName = string.IsNullOrWhiteSpace(bestCatalog.CampaignName) ? wanted : bestCatalog.CampaignName.Trim(),
            CampaignMarkdown = bestCatalog.CampaignMarkdown ?? string.Empty,
            UpdatedUtc = bestCatalog.UpdatedUtc,
            EncounterTemplates = bestCatalog.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>(),
            RunnerState = null,
            LiveRuntime = new DndLiteEncounterLiveRuntime(),
            RunUpdatedUtc = default
        };

        await SaveCampaignAsync(channelState, imported, ct);
        return imported;
    }

    private async Task<GptCliExecutionResult> ExecuteCharacterCreateAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (ctx.Context == null)
        {
            return new GptCliExecutionResult(true, "Module context not initialized.", false);
        }

        if (!TryGetStringArg(argsJson, "name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !TryGetStringArg(argsJson, "concept", out var concept) || string.IsNullOrWhiteSpace(concept))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `concept`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var active = st.ActiveCampaignName ?? "default";

            DndLiteCampaignDocument campaign = null;
            DndLiteCampaignCatalogDocument draft = null;
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                draft = await LoadDraftCampaignAsync(ctx.ChannelState, active, ct);
            }
            else
            {
                campaign = await LoadCampaignAsync(ctx.ChannelState, active, ct);
            }

            using var typing = DiscordTyping.Begin(ctx.Channel);
            async Task Progress(string text)
                => await SendProgressUpdateAsync(ctx.Channel, text, ctx.Context);

            await Progress($"Character generation started for \"{name.Trim()}\".");
            var generationCampaignContext = BuildGenerationCampaignContext(campaign?.CampaignMarkdown ?? draft?.CampaignMarkdown);
            var created = await GenerateCharacterAsync(
                ctx.Context,
                ctx.ChannelState,
                name.Trim(),
                concept.Trim(),
                generationCampaignContext,
                ct,
                Progress);
            if (created == null || created.Stats == null || created.MaxHp <= 0)
            {
                await Progress("Character generation failed.");
                return new GptCliExecutionResult(true, "Character generation failed.", false);
            }

            var profile = new DndLitePcProfile
            {
                UserId = ctx.User.Id,
                ActorId = ToActorId(ctx.User.Id),
                Name = name.Trim(),
                Concept = concept.Trim(),
                ProfileMarkdown = created.ProfileMarkdown ?? string.Empty,
                MaxHp = Math.Max(1, created.MaxHp),
                MaxMp = Math.Max(0, created.MaxMp),
                Stats = created.Stats
            };

            await SavePcProfileAsync(ctx.ChannelState, profile, ct);

            var party = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase)
                ? await LoadDraftPartyAsync(ctx.ChannelState, active, ct)
                : (await LoadPartyAsync(ctx.ChannelState, active, ct) ?? new DndLitePartyDocument());
            if (!party.PlayerUserIds.Contains(ctx.User.Id))
            {
                party.PlayerUserIds.Add(ctx.User.Id);
                if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
                {
                    await SaveDraftPartyAsync(ctx.ChannelState, active, party, ct);
                }
                else
                {
                    await SavePartyAsync(ctx.ChannelState, active, party, ct);
                }
            }

            // Only game mode mutates runner state; draft mode is draft-only.
            if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) && campaign != null)
            {
                // If campaign already has a runner state, inject the new party member (only when no active encounter).
                if (campaign.RunnerState != null && campaign.RunnerState.ActiveEncounter != null && campaign.RunnerState.ActiveEncounter.Completed == false)
                {
                    // Active encounter: don't attempt to merge party membership.
                }
                else
                {
                    var runnerState = campaign.RunnerState ?? new DndCampaignRunnerState();
                    runnerState.Party ??= new List<DndCampaignPartyMember>();
                    if (runnerState.Party.All(p => !string.Equals(p.ActorId, profile.ActorId, StringComparison.OrdinalIgnoreCase)))
                    {
                        runnerState.Party.Add(new DndCampaignPartyMember(
                            ActorId: profile.ActorId,
                            Name: profile.Name,
                            Stats: profile.Stats,
                            MaxHp: profile.MaxHp,
                            Hp: profile.MaxHp,
                            MaxMp: profile.MaxMp,
                            Mp: profile.MaxMp));
                    }

                    // Keep templates from the campaign doc.
                    runnerState.Templates = (campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
                        .Where(t => t?.Mechanics != null)
                        .Select(t => t.Mechanics)
                        .ToList();

                    campaign.RunnerState = runnerState;
                    await SaveCampaignAsync(ctx.ChannelState, campaign, ct);
                }
            }

            return new GptCliExecutionResult(true, BuildCharacterSummary(profile), true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCharacterShowAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        ulong targetUserId = ctx.User.Id;
        if (TryGetUlongArg(argsJson, "user", out var parsed) && parsed != 0)
        {
            targetUserId = parsed;
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var profile = await LoadPcProfileAsync(ctx.ChannelState, targetUserId, ct);
            if (profile == null)
            {
                return new GptCliExecutionResult(true, $"No character profile found for <@{targetUserId}>.", false);
            }

            return new GptCliExecutionResult(true, BuildCharacterSummary(profile), false);
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

        if (!TryGetStringArg(argsJson, "name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !TryGetStringArg(argsJson, "concept", out var concept) || string.IsNullOrWhiteSpace(concept))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `concept`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var active = st.ActiveCampaignName ?? "default";

            DndLiteCampaignDocument campaign = null;
            DndLiteCampaignCatalogDocument draft = null;
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                draft = await LoadDraftCampaignAsync(ctx.ChannelState, active, ct);
            }
            else
            {
                campaign = await LoadCampaignAsync(ctx.ChannelState, active, ct);
            }

            using var typing = DiscordTyping.Begin(ctx.Channel);
            async Task Progress(string text)
                => await SendProgressUpdateAsync(ctx.Channel, text, ctx.Context);

            await Progress($"NPC generation started for \"{name.Trim()}\".");
            var generationCampaignContext = BuildGenerationCampaignContext(campaign?.CampaignMarkdown ?? draft?.CampaignMarkdown);
            var created = await GenerateCharacterAsync(
                ctx.Context,
                ctx.ChannelState,
                name.Trim(),
                concept.Trim(),
                generationCampaignContext,
                ct,
                Progress);
            if (created == null || created.Stats == null || created.MaxHp <= 0)
            {
                await Progress("NPC generation failed.");
                return new GptCliExecutionResult(true, "NPC generation failed.", false);
            }

            var actorId = await AllocateNpcActorIdAsync(ctx.ChannelState, name.Trim(), ct);
            if (string.IsNullOrWhiteSpace(actorId))
            {
                return new GptCliExecutionResult(true, "Unable to allocate NPC id.", false);
            }

            var npc = new DndLiteNpcProfile
            {
                ActorId = actorId,
                IsNpc = true,
                Name = name.Trim(),
                Concept = concept.Trim(),
                ProfileMarkdown = created.ProfileMarkdown ?? string.Empty,
                MaxHp = Math.Max(1, created.MaxHp),
                MaxMp = Math.Max(0, created.MaxMp),
                Stats = created.Stats
            };

            await SaveNpcProfileAsync(ctx.ChannelState, npc, ct);

            var party = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase)
                ? await LoadDraftPartyAsync(ctx.ChannelState, active, ct)
                : (await LoadPartyAsync(ctx.ChannelState, active, ct) ?? new DndLitePartyDocument());
            party.NpcActorIds ??= new List<string>();
            if (!party.NpcActorIds.Any(x => string.Equals(x, actorId, StringComparison.OrdinalIgnoreCase)))
            {
                party.NpcActorIds.Add(actorId);
                if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
                {
                    await SaveDraftPartyAsync(ctx.ChannelState, active, party, ct);
                }
                else
                {
                    await SavePartyAsync(ctx.ChannelState, active, party, ct);
                }
            }

            // Only game mode mutates runner state; draft mode is draft-only.
            if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) && campaign != null)
            {
                // If campaign already has a runner state, inject the new NPC (only when no active encounter).
                if (campaign.RunnerState != null && campaign.RunnerState.ActiveEncounter != null && campaign.RunnerState.ActiveEncounter.Completed == false)
                {
                    // Active encounter: don't attempt to merge party membership.
                }
                else
                {
                    var runnerState = campaign.RunnerState ?? new DndCampaignRunnerState();
                    runnerState.Party ??= new List<DndCampaignPartyMember>();
                    if (runnerState.Party.All(p => !string.Equals(p.ActorId, npc.ActorId, StringComparison.OrdinalIgnoreCase)))
                    {
                        runnerState.Party.Add(new DndCampaignPartyMember(
                            ActorId: npc.ActorId,
                            Name: npc.Name,
                            Stats: npc.Stats,
                            MaxHp: npc.MaxHp,
                            Hp: npc.MaxHp,
                            MaxMp: npc.MaxMp,
                            Mp: npc.MaxMp));
                    }

                    runnerState.Templates = (campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
                        .Where(t => t?.Mechanics != null)
                        .Select(t => t.Mechanics)
                        .ToList();

                    campaign.RunnerState = runnerState;
                    await SaveCampaignAsync(ctx.ChannelState, campaign, ct);
                }
            }

            return new GptCliExecutionResult(true, BuildNpcSummary(npc), true);
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
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var active = st.ActiveCampaignName ?? "default";
            var party = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase)
                ? await LoadDraftPartyAsync(ctx.ChannelState, active, ct)
                : (await LoadPartyAsync(ctx.ChannelState, active, ct) ?? new DndLitePartyDocument());
            party.NpcActorIds ??= new List<string>();
            if (party.NpcActorIds.Count == 0)
            {
                return new GptCliExecutionResult(true, "No NPCs in party roster.", false);
            }

            var lines = new List<string> { "NPC party roster:" };
            foreach (var id in party.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var npc = await LoadNpcProfileAsync(ctx.ChannelState, id, ct);
                lines.Add(npc == null
                    ? $"- {id}"
                    : $"- {id} | {npc.Name}");
            }

            return new GptCliExecutionResult(true, TrimToLimit(string.Join("\n", lines), 3500), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcShowAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "id", out var actorIdRaw) || string.IsNullOrWhiteSpace(actorIdRaw))
        {
            return new GptCliExecutionResult(true, "Provide `id`.", false);
        }

        var actorId = actorIdRaw.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var npc = await LoadNpcProfileAsync(ctx.ChannelState, actorId, ct);
            if (npc == null)
            {
                return new GptCliExecutionResult(true, $"No NPC profile found for `{actorId}`.", false);
            }

            return new GptCliExecutionResult(true, BuildNpcSummary(npc), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteNpcRemoveAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "id", out var actorIdRaw) || string.IsNullOrWhiteSpace(actorIdRaw))
        {
            return new GptCliExecutionResult(true, "Provide `id`.", false);
        }

        var actorId = actorIdRaw.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var active = st.ActiveCampaignName ?? "default";
            var party = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase)
                ? await LoadDraftPartyAsync(ctx.ChannelState, active, ct)
                : (await LoadPartyAsync(ctx.ChannelState, active, ct) ?? new DndLitePartyDocument());
            party.NpcActorIds ??= new List<string>();
            var before = party.NpcActorIds.Count;
            party.NpcActorIds = party.NpcActorIds
                .Where(s => !string.Equals(s, actorId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (party.NpcActorIds.Count != before)
            {
                if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
                {
                    await SaveDraftPartyAsync(ctx.ChannelState, active, party, ct);
                }
                else
                {
                    await SavePartyAsync(ctx.ChannelState, active, party, ct);
                }
            }

            var changed = party.NpcActorIds.Count != before;

            // Best-effort: if campaign runner is not in an active encounter, remove from campaign party too.
            if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                var campaign = await LoadCampaignAsync(ctx.ChannelState, active, ct);
                if (campaign?.RunnerState?.Party != null)
                {
                    var hasInProgressEncounter = campaign.RunnerState.ActiveEncounter != null && campaign.RunnerState.ActiveEncounter.Completed == false;
                    if (!hasInProgressEncounter)
                    {
                        var partyBefore = campaign.RunnerState.Party.Count;
                        campaign.RunnerState.Party = campaign.RunnerState.Party
                            .Where(m => m != null && !string.Equals(m.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (campaign.RunnerState.Party.Count != partyBefore)
                        {
                            await SaveCampaignAsync(ctx.ChannelState, campaign, ct);
                            changed = true;
                        }
                    }
                }
            }

            return new GptCliExecutionResult(true, $"NPC removed: `{actorId}`.", changed);
        }
        finally
        {
            lockHandle.Release();
        }
    }

	    private async Task<GptCliExecutionResult> ExecuteLiveConfigAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
	    {
	        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
	        await lockHandle.WaitAsync(ct);
	        try
	        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            st.Live ??= new DndLiteLiveConfig();

            if (TryGetIntArg(argsJson, "tick_seconds", out var tickSeconds))
            {
                st.Live.TickSeconds = Math.Clamp(tickSeconds, 1, 30);
            }
	            if (TryGetIntArg(argsJson, "player_turn_timeout_seconds", out var playerTimeout))
	            {
	                st.Live.PlayerTurnTimeoutSeconds = Math.Clamp(playerTimeout, 5, 3600);
	            }
            if (TryGetIntArg(argsJson, "encounter_timeout_seconds", out var encTimeout))
            {
                st.Live.EncounterTimeoutSeconds = Math.Clamp(encTimeout, 30, 3600);
            }
            if (TryGetBoolArg(argsJson, "npc_autoplay", out var npcAutoplay))
            {
                st.Live.NpcAutoplayEnabled = npcAutoplay;
            }
            if (TryGetBoolArg(argsJson, "npc_flavor", out var npcFlavor))
            {
                st.Live.NpcFlavorEnabled = npcFlavor;
            }
            if (TryGetStringArg(argsJson, "autoroll_policy", out var policy))
            {
                st.Live.AutoRollPolicy = NormalizeAutoRollPolicy(policy);
            }

            await SaveStateAsync(ctx.ChannelState, st, ct);

            if (string.Equals(st.Mode, ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                EnsureTickLoopRunning(ctx.Channel.Id);
            }

	            return new GptCliExecutionResult(true, RenderLiveConfig(st.Live), true);
	        }
	        finally
	        {
	            lockHandle.Release();
	        }
	    }

	    private async Task<GptCliExecutionResult> ExecutePassTimeoutAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
	    {
	        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
	        await lockHandle.WaitAsync(ct);
	        try
	        {
	            var off = await RejectWhenOffAsync(ctx, ct);
	            if (off != null)
	            {
	                return off;
	            }

	            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
	            var mode = NormalizeMode(st.Mode);
	            if (!string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) &&
	                !string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
	            {
	                return new GptCliExecutionResult(true, "Not in draft/game mode. Use `/gptcli dnd mode value:draft` or `/gptcli dnd mode value:game`.", false);
	            }

	            st.Live ??= new DndLiteLiveConfig();

	            // Default to 30 minutes if no args provided.
	            var seconds = 30 * 60;

	            if (TryGetIntArg(argsJson, "minutes", out var minutes) && minutes > 0)
	            {
	                seconds = minutes * 60;
	            }

	            if (TryGetIntArg(argsJson, "seconds", out var rawSeconds) && rawSeconds > 0)
	            {
	                seconds = rawSeconds;
	            }

	            seconds = Math.Clamp(seconds, 5, 3600);
	            st.Live.PlayerTurnTimeoutSeconds = seconds;

	            await SaveStateAsync(ctx.ChannelState, st, ct);

	            if (string.Equals(st.Mode, ModeGame, StringComparison.OrdinalIgnoreCase))
	            {
	                EnsureTickLoopRunning(ctx.Channel.Id);
	            }

	            var mins = seconds / 60.0;
	            return new GptCliExecutionResult(true, $"Auto-pass timeout set to {seconds}s ({mins:0.#} minutes).", true);
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
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var mode = NormalizeMode(st.Mode);
            var active = st.ActiveCampaignName ?? "default";

            string label;
            List<DndLiteEncounterTemplateDocument> templates;
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
            {
                var draft = await LoadDraftCampaignAsync(ctx.ChannelState, active, ct);
                templates = draft?.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>();
                label = draft?.CampaignName ?? active;
            }
            else
            {
                var campaign = await LoadCampaignAsync(ctx.ChannelState, active, ct);
                templates = campaign?.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>();
                label = campaign?.CampaignName ?? active;
            }
            if (templates.Count == 0)
            {
                return new GptCliExecutionResult(true, "No encounter templates found. Build a draft campaign first.", false);
            }

            var lines = new List<string> { $"Encounters for \"{label}\":" };
            foreach (var t in templates.OrderBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase))
            {
                if (t == null)
                {
                    continue;
                }

                lines.Add($"- {t.TemplateId} | {t.Name} | adds={(t.Adds?.Count ?? 0)}");
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
        if (!TryGetStringArg(argsJson, "id", out var templateIdRaw) || string.IsNullOrWhiteSpace(templateIdRaw))
        {
            return new GptCliExecutionResult(true, "Provide `id`.", false);
        }

        var templateId = SlugifySegment(templateIdRaw);
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign == null)
            {
                return new GptCliExecutionResult(true, "No campaign found. Create one with `/gptcli dnd campaigncreate`.", false);
            }

            var party = await LoadPartyAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            party ??= new DndLitePartyDocument();
            party.PlayerUserIds ??= new List<ulong>();
            party.NpcActorIds ??= new List<string>();
            if (party.PlayerUserIds.Count == 0 && party.NpcActorIds.Count == 0)
            {
                return new GptCliExecutionResult(true, "No party members yet. Create a character with `/gptcli dnd charactercreate` or `/gptcli dnd npccreate`.", false);
            }

            var runner = await LoadOrCreateRunnerAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (runner == null)
            {
                return new GptCliExecutionResult(true, "Unable to create campaign runner (missing party profiles).", false);
            }

            var res = runner.StartEncounter(templateId);
            await PersistRunnerAsync(ctx.ChannelState, st.ActiveCampaignName, campaign, runner, ct);

            var legacyText = RenderTurnResult(res);
            var text = await FormatGameTurnOutputAsync(ctx.Context, ctx.ChannelState, res, legacyText, "encounter-start", ct);
            return new GptCliExecutionResult(true, text, true);
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
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                return new GptCliExecutionResult(true, "No campaign runner state yet. Start an encounter first.", false);
            }

            var runner = RestoreCampaignRunner(ctx.ChannelState.ChannelId, campaign.RunnerState);
            var snap = runner.GetState();
            if (snap.ActiveEncounterState == null)
            {
                return new GptCliExecutionResult(true, "No active encounter.", false);
            }

            var sb = new StringBuilder();
            sb.AppendLine(RenderEncounterSnapshot(snap.ActiveEncounterState));
            sb.AppendLine();
            sb.AppendLine(RenderNextRequest(DndTurnResult.BuildNextRequest(snap.ActiveEncounterState, campaign.RunnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>())));
            return new GptCliExecutionResult(true, TrimToLimit(sb.ToString().Trim(), 3500), false);
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
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                return new GptCliExecutionResult(true, "No campaign runner state.", false);
            }

            // Just clear active encounter; mechanics state is persisted in runner state.
            ClearActiveEncounter(campaign);
            await SaveCampaignAsync(ctx.ChannelState, campaign, ct);

            return new GptCliExecutionResult(true, "Encounter cleared.", true);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteAttackAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "target", out var targetRaw) || string.IsNullOrWhiteSpace(targetRaw))
        {
            return new GptCliExecutionResult(true, "Provide `target`.", false);
        }

        var target = targetRaw.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            if (!string.Equals(NormalizeMode(st.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in game mode. Use `/gptcli dnd mode value:game`.", false);
            }

            EnsureTickLoopRunning(ctx.Channel.Id);

            var actorId = ToActorId(ctx.User.Id);
            var r = await RunEncounterActionAsync(ctx.Context, ctx.ChannelState, st, ctx.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(ctx.ChannelState, st.ActiveCampaignName, ct), target, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.Attack(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, actionLabel: "attack", postToChannel: false, ct: ct);

            return new GptCliExecutionResult(true, r.responseText, r.stateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteCastAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        if (!TryGetStringArg(argsJson, "target", out var targetRaw) || string.IsNullOrWhiteSpace(targetRaw))
        {
            return new GptCliExecutionResult(true, "Provide `target`.", false);
        }

        var target = targetRaw.Trim();
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            if (!string.Equals(NormalizeMode(st.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in game mode. Use `/gptcli dnd mode value:game`.", false);
            }

            EnsureTickLoopRunning(ctx.Channel.Id);

            var actorId = ToActorId(ctx.User.Id);
            var r = await RunEncounterActionAsync(ctx.Context, ctx.ChannelState, st, ctx.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(ctx.ChannelState, st.ActiveCampaignName, ct), target, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.CastSpell(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, actionLabel: "cast", postToChannel: false, ct: ct);

            return new GptCliExecutionResult(true, r.responseText, r.stateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecutePassAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            if (!string.Equals(NormalizeMode(st.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in game mode. Use `/gptcli dnd mode value:game`.", false);
            }

            EnsureTickLoopRunning(ctx.Channel.Id);

            var actorId = ToActorId(ctx.User.Id);
            var r = await RunEncounterActionAsync(ctx.Context, ctx.ChannelState, st, ctx.Channel, runner =>
            {
                var res = runner.Pass(actorId);
                return Task.FromResult((res, RenderTurnResult(res)));
            }, actionLabel: "pass", postToChannel: false, ct: ct);

            return new GptCliExecutionResult(true, r.responseText, r.stateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteRollAllAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            if (!string.Equals(NormalizeMode(st.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
            {
                return new GptCliExecutionResult(true, "Not in game mode. Use `/gptcli dnd mode value:game`.", false);
            }

            EnsureTickLoopRunning(ctx.Channel.Id);

            var r = await RunEncounterActionAsync(ctx.Context, ctx.ChannelState, st, ctx.Channel, runner =>
            {
                var res = runner.RollAll();
                return Task.FromResult((res, RenderTurnResult(res)));
            }, actionLabel: "rollall", postToChannel: false, ct: ct);

            return new GptCliExecutionResult(true, r.responseText, r.stateChanged);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<GptCliExecutionResult> ExecuteLedgerAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var count = 20;
        if (TryGetIntArg(argsJson, "count", out var parsed))
        {
            count = Math.Clamp(parsed, 1, 100);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var off = await RejectWhenOffAsync(ctx, ct);
            if (off != null)
            {
                return off;
            }

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                return new GptCliExecutionResult(true, "No campaign runner state.", false);
            }

            var ledger = campaign.RunnerState.Ledger ?? new List<DndCampaignLedgerEntry>();
            if (ledger.Count == 0)
            {
                return new GptCliExecutionResult(true, "No ledger entries yet.", false);
            }

            var items = ledger.TakeLast(count).ToList();
            var lines = new List<string> { $"Ledger for \"{campaign.CampaignName}\" (last {items.Count}/{ledger.Count})" };
            foreach (var e in items)
            {
                lines.Add($"[{e.Sequence}] {e.OccurredUtc:O} {e.Message}");
            }

            return new GptCliExecutionResult(true, TrimToLimit(string.Join("\n", lines), 3500), false);
        }
        finally
        {
            lockHandle.Release();
        }
    }

    private async Task<(bool handled, bool stateChanged)> TryHandleBangCommandAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        string content,
        CancellationToken ct)
    {
        if (string.Equals(content, "!help", StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(BuildHelpText());
            return (true, false);
        }

        if (content.StartsWith("!ledger", StringComparison.OrdinalIgnoreCase))
        {
            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var n = 20;
            if (parts.Length > 1 && int.TryParse(parts[1], out var parsed))
            {
                n = Math.Clamp(parsed, 1, 100);
            }

            var st = await GetOrLoadStateAsync(channelState, ct);
            var campaign = await LoadCampaignAsync(channelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState?.Ledger == null || campaign.RunnerState.Ledger.Count == 0)
            {
                await message.Channel.SendMessageAsync("No ledger entries yet.");
                return (true, false);
            }

            var items = campaign.RunnerState.Ledger.TakeLast(n).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Ledger for \"{campaign.CampaignName}\" (last {items.Count}/{campaign.RunnerState.Ledger.Count})");
            foreach (var e in items)
            {
                sb.AppendLine($"[{e.Sequence}] {e.Message}");
            }

            await SendChunkedAsync(message.Channel, TrimToLimit(sb.ToString().Trim(), 3500));
            return (true, false);
        }

        if (string.Equals(content, "!state", StringComparison.OrdinalIgnoreCase))
        {
            var st = await GetOrLoadStateAsync(channelState, ct);
            var campaign = await LoadCampaignAsync(channelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                await message.Channel.SendMessageAsync("No campaign state yet.");
                return (true, false);
            }

            var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
            var snap = runner.GetState();
            var sb = new StringBuilder();
            sb.AppendLine(RenderPartySummary(snap));
            if (snap.ActiveEncounterState != null)
            {
                sb.AppendLine();
                sb.AppendLine(RenderEncounterSnapshot(snap.ActiveEncounterState));
                sb.AppendLine();
                sb.AppendLine(RenderNextRequest(DndTurnResult.BuildNextRequest(snap.ActiveEncounterState, campaign.RunnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>())));
            }
            await SendChunkedAsync(message.Channel, TrimToLimit(sb.ToString().Trim(), 3500));
            return (true, false);
        }

        if (string.Equals(content, "!targets", StringComparison.OrdinalIgnoreCase))
        {
            var st = await GetOrLoadStateAsync(channelState, ct);
            var campaign = await LoadCampaignAsync(channelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                await message.Channel.SendMessageAsync("No campaign state yet.");
                return (true, false);
            }

            var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
            var snap = runner.GetState();
            if (snap.ActiveEncounterState == null)
            {
                await message.Channel.SendMessageAsync("No active encounter.");
                return (true, false);
            }

            await message.Channel.SendMessageAsync(RenderTargets(snap.ActiveEncounterState));
            return (true, false);
        }

        if (string.Equals(content, "!rollall", StringComparison.OrdinalIgnoreCase))
        {
            var r = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.RollAll();
                return (res, RenderTurnResult(res));
            }, actionLabel: "rollall", postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        if (content.StartsWith("!roll ", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content[6..].Trim();
            if (string.Equals(tail, "initiative", StringComparison.OrdinalIgnoreCase))
            {
                var actorId = ToActorId(message.Author.Id);
                var r = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
                {
                    var res = runner.RollInitiative(actorId);
                    return (res, RenderTurnResult(res));
                }, actionLabel: "initiative", postToChannel: true, ct: ct);
                return (r.handled, r.stateChanged);
            }

            var rollId = tail.Trim();
            var rr = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
            {
                var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
                var pending = campaign?.RunnerState?.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>();
                var pr = pending.FirstOrDefault(r => r != null && string.Equals(r.RollId, rollId, StringComparison.OrdinalIgnoreCase));
                if (pr == null)
                {
                    return (null, "Pending roll not found.");
                }

                DndCampaignResult res = pr.Kind switch
                {
                    DndPendingRollKind.Initiative => runner.RollInitiative(pr.ActorId),
                    DndPendingRollKind.AttackToHit => runner.RollAttack(pr.RollId),
                    DndPendingRollKind.AttackDamage => runner.RollDamage(pr.RollId),
                    DndPendingRollKind.SpellToHit => runner.RollSpellAttack(pr.RollId),
                    DndPendingRollKind.SpellDamage => runner.RollSpellDamage(pr.RollId),
                    _ => new DndCampaignResult(false, "Unknown roll kind", runner.GetState(), null, Array.Empty<DndCampaignLedgerEntry>())
                };

                return (res, RenderTurnResult(res));
            }, actionLabel: "roll", postToChannel: true, ct: ct);
            return (rr.handled, rr.stateChanged);
        }

        if (content.StartsWith("!attack ", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content[8..].Trim();
            if (string.IsNullOrWhiteSpace(tail))
            {
                await message.Channel.SendMessageAsync("Usage: `!attack <target>`");
                return (true, false);
            }

            var actorId = ToActorId(message.Author.Id);
            var r = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.Attack(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, actionLabel: "attack", postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        if (content.StartsWith("!cast ", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content[6..].Trim();
            if (string.IsNullOrWhiteSpace(tail))
            {
                await message.Channel.SendMessageAsync("Usage: `!cast <target>`");
                return (true, false);
            }

            var actorId = ToActorId(message.Author.Id);
            var r = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.CastSpell(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, actionLabel: "cast", postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        if (string.Equals(content, "!pass", StringComparison.OrdinalIgnoreCase))
        {
            var actorId = ToActorId(message.Author.Id);
            var r = await RunEncounterActionAsync(context, channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.Pass(actorId);
                return (res, RenderTurnResult(res));
            }, actionLabel: "pass", postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        return (false, false);
    }

    private enum NaturalGameActionKind
    {
        None = 0,
        Attack = 1,
        Cast = 2,
        Pass = 3,
        RollAll = 4,
        RollInitiative = 5,
        RollById = 6
    }

    private sealed class NaturalGameIntent
    {
        public NaturalGameActionKind Action { get; init; }
        public bool LooksMechanical { get; init; }
        public bool AmbiguousActionType { get; init; }
        public string TargetActorId { get; init; }
        public string RollId { get; init; }
        public List<string> CandidateTargetIds { get; init; } = new();
    }

    private async Task<bool> TryHandleNaturalGameActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState == null)
        {
            return false;
        }

        if (!string.Equals(NormalizeMode(dndState.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = StripBotMentions(message.Content ?? string.Empty, context.Client.CurrentUser.Id);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign?.RunnerState == null)
        {
            return false;
        }

        var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
        var snap = runner.GetState();
        var encounter = snap?.ActiveEncounterState;
        if (encounter == null || encounter.IsCompleted)
        {
            return false;
        }

        var pending = campaign.RunnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>();
        var next = DndTurnResult.BuildNextRequest(encounter, pending);
        if (next == null || next.Kind == DndNextRequestKind.Completed)
        {
            return false;
        }

        var intent = ParseNaturalGameIntent(text, encounter);
        if (intent == null || !intent.LooksMechanical)
        {
            return false;
        }

        var actorId = ToActorId(message.Author.Id);
        var currentActorId = next.CurrentActorId ?? encounter.CurrentActorId ?? string.Empty;
        var currentActorName = ResolveActorDisplayName(encounter, currentActorId);
        var authorMention = $"<@{message.Author.Id}>";

        if (next.Kind == DndNextRequestKind.NeedInitiativeRolls)
        {
            if (intent.Action == NaturalGameActionKind.RollAll)
            {
                var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, "!rollall", ct);
                return handled;
            }

            if (intent.Action == NaturalGameActionKind.RollById && !string.IsNullOrWhiteSpace(intent.RollId))
            {
                var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!roll {intent.RollId}", ct);
                return handled;
            }

            if (intent.Action == NaturalGameActionKind.RollInitiative)
            {
                var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, "!roll initiative", ct);
                return handled;
            }

            await message.Channel.SendMessageAsync(
                $"{authorMention} Initiative is still pending. Say you want to roll initiative (or ask me to resolve all pending initiative rolls).");
            return true;
        }

        if (next.Kind == DndNextRequestKind.NeedRolls)
        {
            if (intent.Action == NaturalGameActionKind.RollAll)
            {
                var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, "!rollall", ct);
                return handled;
            }

            if (intent.Action == NaturalGameActionKind.RollById && !string.IsNullOrWhiteSpace(intent.RollId))
            {
                var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!roll {intent.RollId}", ct);
                return handled;
            }

            await message.Channel.SendMessageAsync(
                $"{authorMention} There are pending rolls to resolve first. Ask for a specific pending roll ID or ask me to resolve all pending rolls.");
            return true;
        }

        if (next.Kind != DndNextRequestKind.NeedAction)
        {
            return false;
        }

        if (!string.Equals(currentActorId, actorId, StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync(
                $"{authorMention} It's {currentActorName}'s turn right now. Once their turn resolves, I'll map your action.");
            return true;
        }

        if (intent.AmbiguousActionType)
        {
            await message.Channel.SendMessageAsync(
                $"{authorMention} I need one clarification: do you want to attack or cast this turn?");
            return true;
        }

        if (intent.Action == NaturalGameActionKind.Pass)
        {
            var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, "!pass", ct);
            return handled;
        }

        if (intent.Action == NaturalGameActionKind.RollAll ||
            intent.Action == NaturalGameActionKind.RollById ||
            intent.Action == NaturalGameActionKind.RollInitiative)
        {
            await message.Channel.SendMessageAsync(
                $"{authorMention} No roll is pending for your turn right now. Choose an action: attack, cast, or pass.");
            return true;
        }

        if (intent.Action is NaturalGameActionKind.Attack or NaturalGameActionKind.Cast)
        {
            if (intent.CandidateTargetIds.Count > 1)
            {
                await message.Channel.SendMessageAsync(
                    $"{authorMention} Target is ambiguous. Pick one enemy: {BuildEnemyChoiceLine(encounter, intent.CandidateTargetIds)}.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(intent.TargetActorId))
            {
                await message.Channel.SendMessageAsync(
                    $"{authorMention} Who are you targeting? Living enemies: {BuildEnemyChoiceLine(encounter)}.");
                return true;
            }

            var cmd = intent.Action == NaturalGameActionKind.Attack
                ? $"!attack {intent.TargetActorId}"
                : $"!cast {intent.TargetActorId}";
            var (handled, _) = await TryHandleBangCommandAsync(context, channelState, message, dndState, cmd, ct);
            return handled;
        }

        await message.Channel.SendMessageAsync(
            $"{authorMention} Tell me your action directly and I'll run it: attack, cast, or pass.");
        return true;
    }

    private static NaturalGameIntent ParseNaturalGameIntent(string text, DndEncounterSnapshot encounter)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.None, LooksMechanical = false };
        }

        var lower = t.ToLowerInvariant();
        var rollAll = Regex.IsMatch(lower, @"\broll\s*all\b|\brollall\b|\bresolve\s+all\s+rolls?\b", RegexOptions.CultureInvariant);
        var rollId = TryExtractRollId(lower);
        var rollWord = Regex.IsMatch(lower, @"\broll(?:ing)?\b", RegexOptions.CultureInvariant);
        var initiative = Regex.IsMatch(lower, @"\binitiative\b|\binit\b", RegexOptions.CultureInvariant) && (rollWord || lower.StartsWith("init", StringComparison.OrdinalIgnoreCase));
        var pass = Regex.IsMatch(lower, @"\b(pass|skip|wait|delay|forfeit)\b", RegexOptions.CultureInvariant) ||
                   lower.Contains("end turn", StringComparison.OrdinalIgnoreCase) ||
                   lower.Contains("do nothing", StringComparison.OrdinalIgnoreCase);
        var attack = Regex.IsMatch(lower, @"\b(attack|strike|swing|stab|slash|shoot|hit)\b", RegexOptions.CultureInvariant);
        var cast = Regex.IsMatch(lower, @"\b(cast|spell|cantrip|firebolt|magic missile|eldritch|hex)\b", RegexOptions.CultureInvariant);

        var looksMechanical = rollAll || rollWord || !string.IsNullOrWhiteSpace(rollId) || initiative || pass || attack || cast;
        if (!looksMechanical)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.None, LooksMechanical = false };
        }

        if (rollAll)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.RollAll, LooksMechanical = true };
        }

        if (!string.IsNullOrWhiteSpace(rollId))
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.RollById, LooksMechanical = true, RollId = rollId };
        }

        if (initiative)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.RollInitiative, LooksMechanical = true };
        }

        if (pass && !attack && !cast)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.Pass, LooksMechanical = true };
        }

        if (attack && cast)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.None, LooksMechanical = true, AmbiguousActionType = true };
        }

        if (!attack && !cast)
        {
            return new NaturalGameIntent { Action = NaturalGameActionKind.None, LooksMechanical = true };
        }

        var action = attack ? NaturalGameActionKind.Attack : NaturalGameActionKind.Cast;
        var matchedEnemies = FindNaturalEnemyMatches(encounter, t);
        if (matchedEnemies.Count == 1)
        {
            return new NaturalGameIntent
            {
                Action = action,
                LooksMechanical = true,
                TargetActorId = matchedEnemies[0].ActorId
            };
        }

        if (matchedEnemies.Count > 1)
        {
            return new NaturalGameIntent
            {
                Action = action,
                LooksMechanical = true,
                CandidateTargetIds = matchedEnemies.Select(e => e.ActorId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        var extracted = TryExtractNaturalTargetPhrase(t);
        var resolvedTarget = TryResolveNaturalTarget(encounter, extracted);

        if (string.IsNullOrWhiteSpace(resolvedTarget))
        {
            var living = encounter?.Actors?.Values?
                .Where(a => a != null && a.IsAlive && a.Side == DndSide.Enemy)
                .OrderBy(a => a.IsBoss ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<DndActorSnapshot>();
            if (living.Count == 1)
            {
                resolvedTarget = living[0].ActorId;
            }
            else if (living.Count > 1)
            {
                return new NaturalGameIntent
                {
                    Action = action,
                    LooksMechanical = true,
                    CandidateTargetIds = living.Select(a => a.ActorId).ToList()
                };
            }
        }

        return new NaturalGameIntent
        {
            Action = action,
            LooksMechanical = true,
            TargetActorId = resolvedTarget
        };
    }

    private static string TryExtractRollId(string text)
    {
        var match = Regex.Match(text ?? string.Empty, @"\br\d{3,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value.Trim() : null;
    }

    private static List<DndActorSnapshot> FindNaturalEnemyMatches(DndEncounterSnapshot encounter, string text)
    {
        var hay = $" {NormalizePhrase(text)} ";
        if (string.IsNullOrWhiteSpace(hay))
        {
            return new List<DndActorSnapshot>();
        }

        return encounter?.Actors?.Values?
            .Where(a => a != null && a.IsAlive && a.Side == DndSide.Enemy)
            .Where(a =>
            {
                var actorToken = NormalizePhrase(a.ActorId);
                if (!string.IsNullOrWhiteSpace(actorToken) && hay.Contains($" {actorToken} ", StringComparison.Ordinal))
                {
                    return true;
                }

                var nameToken = NormalizePhrase(a.Name);
                return !string.IsNullOrWhiteSpace(nameToken) && hay.Contains($" {nameToken} ", StringComparison.Ordinal);
            })
            .DistinctBy(a => a.ActorId, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<DndActorSnapshot>();
    }

    private static string TryExtractNaturalTargetPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var prepositionMatch = Regex.Match(
            text,
            @"\b(?:at|against|on|into|toward(?:s)?|target(?:ing)?|vs\.?)\s+(?<target>[^,\.\!\?\;\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (prepositionMatch.Success)
        {
            var target = prepositionMatch.Groups["target"].Value?.Trim();
            if (!string.IsNullOrWhiteSpace(target))
            {
                return target;
            }
        }

        var actionMatch = Regex.Match(
            text,
            @"\b(?:attack|cast|strike|swing|stab|shoot|hit)\b\s+(?<target>[^,\.\!\?\;\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (actionMatch.Success)
        {
            var target = actionMatch.Groups["target"].Value?.Trim();
            if (!string.IsNullOrWhiteSpace(target))
            {
                return target;
            }
        }

        return null;
    }

    private static string TryResolveNaturalTarget(DndEncounterSnapshot encounter, string targetPhrase)
    {
        if (encounter == null || string.IsNullOrWhiteSpace(targetPhrase))
        {
            return null;
        }

        var target = targetPhrase.Trim().Trim('"', '\'', '`');
        target = Regex.Replace(target, @"^(?:the|a|an)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (target.Length == 0)
        {
            return null;
        }

        var direct = ResolveTargetActorId(encounter, target, out _);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var matches = FindNaturalEnemyMatches(encounter, target);
        return matches.Count == 1 ? matches[0].ActorId : null;
    }

    private static string BuildEnemyChoiceLine(DndEncounterSnapshot encounter, IEnumerable<string> preferredActorIds = null)
    {
        var preferred = preferredActorIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enemies = encounter?.Actors?.Values?
            .Where(a => a != null && a.IsAlive && a.Side == DndSide.Enemy)
            .Where(a => preferred == null || preferred.Count == 0 || preferred.Contains(a.ActorId))
            .OrderBy(a => a.IsBoss ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(a => $"{a.Name} ({a.ActorId})")
            .ToList() ?? new List<string>();

        return enemies.Count == 0 ? "no living enemies listed" : string.Join(", ", enemies);
    }

    private static string ResolveActorDisplayName(DndEncounterSnapshot encounter, string actorId)
    {
        if (encounter?.Actors != null &&
            !string.IsNullOrWhiteSpace(actorId) &&
            encounter.Actors.TryGetValue(actorId, out var actor) &&
            actor != null &&
            !string.IsNullOrWhiteSpace(actor.Name))
        {
            return actor.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(actorId) ? "unknown actor" : actorId.Trim();
    }

    private static string NormalizePhrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ");
        compact = Regex.Replace(compact, @"\s+", " ").Trim();
        return compact;
    }

    private async Task<bool> TryHandleTaggedCampaignChatAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null)
        {
            return false;
        }

        var raw = StripBotMentions(message.Content ?? string.Empty, context.Client.CurrentUser.Id);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var lowered = text.ToLowerInvariant();

        // Only engage when the user is plausibly asking about campaign content (avoid hijacking general chat).
        var looksLikeCampaignQuestion =
            lowered.Contains("campaign") ||
            lowered.Contains("encounter") ||
            lowered.Contains("boss") ||
            lowered.Contains("hook") ||
            lowered.Contains("premise") ||
            lowered.Contains("setting") ||
            lowered.Contains("story") ||
            lowered.Contains("plot") ||
            lowered.Contains("summar") ||
            lowered.Contains("describe") ||
            lowered.Contains("what is") ||
            lowered.Contains("what's") ||
            lowered.Contains("recap") ||
            text.Contains('?');

        if (!looksLikeCampaignQuestion)
        {
            return false;
        }

        var campaignName = dndState?.ActiveCampaignName ?? "default";
        var campaign = await LoadCampaignAsync(channelState, campaignName, ct);
        if (campaign == null)
        {
            try
            {
                await message.Channel.SendMessageAsync("No campaign found. Create one with `/gptcli dnd campaigncreate` or select one with `/gptcli dnd campaignstart`.");
            }
            catch { }
            return true;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Campaign context (authoritative):");
        sb.AppendLine($"Name: {campaign.CampaignName}");
        if (!string.IsNullOrWhiteSpace(campaign.CampaignMarkdown))
        {
            sb.AppendLine("Campaign markdown:");
            sb.AppendLine(TrimToLimit(campaign.CampaignMarkdown, 3500));
        }
        if (campaign.EncounterTemplates is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Encounter templates (summary):");
            foreach (var t in campaign.EncounterTemplates
                         .Where(t => t != null && !string.IsNullOrWhiteSpace(t.TemplateId))
                         .OrderBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                var name = string.IsNullOrWhiteSpace(t.Name) ? t.TemplateId : t.Name.Trim();
                sb.AppendLine($"- {t.TemplateId} | {name}");
                if (!string.IsNullOrWhiteSpace(t.Scene))
                {
                    sb.AppendLine($"  scene: {TrimToLimit(t.Scene.Replace('\n', ' ').Replace('\r', ' ').Trim(), 220)}");
                }
                if (t.Boss != null && !string.IsNullOrWhiteSpace(t.Boss.Name))
                {
                    sb.AppendLine($"  boss: {t.Boss.Name} ({t.Boss.ActorId})");
                }
                var adds = t.Adds?.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name.Trim()).Take(6).ToList()
                           ?? new List<string>();
                sb.AppendLine(adds.Count > 0 ? $"  adds: {string.Join(", ", adds)}" : $"  adds: {t.Adds?.Count ?? 0}");
            }
        }

        var system =
            "You are a D&D campaign assistant for a simplified Discord engine.\n" +
            "Use the provided campaign context as the source of truth.\n" +
            "If the user asks to describe the campaign: give a 1-paragraph premise + 3-6 bullet hooks.\n" +
            "If the user asks about encounters: summarize relevant encounters by templateId and name.\n" +
            "Keep it concise and actionable. Do not invent lore not present in the context; if missing, say so.\n";

        var request = new ChatCompletionCreateRequest
        {
            Model = ResolveModel(context, channelState),
            Temperature = 0.4f,
            MaxCompletionTokens = 700,
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System, system),
                new(ChatCompletionRole.User, sb.ToString().Trim()),
                new(ChatCompletionRole.User, $"User question: {text}")
            }
        };

        ChatCompletionCreateResponse response;
        try
        {
            using var typing = DiscordTyping.Begin(message.Channel);
            var responseTask = context.OpenAILogic.CreateChatCompletionAsync(request);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25), ct);
            var done = await Task.WhenAny(responseTask, timeoutTask);
            if (done != responseTask)
            {
                await message.Channel.SendMessageAsync("Campaign description timed out. Try a shorter question or use `/gptcli dnd status`.");
                return true;
            }

            response = await responseTask;
        }
        catch
        {
            return false;
        }

        if (!response.Successful)
        {
            try { await message.Channel.SendMessageAsync("Campaign description failed."); } catch { }
            return true;
        }

        var content = ExtractChatMessageText(response.Choices?.FirstOrDefault()?.Message);
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        try { await SendChunkedAsync(message.Channel, TrimToLimit(content.Trim(), 3500)); } catch { }
        return true;
    }

    private async Task<(bool handled, bool stateChanged, string responseText)> RunEncounterActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        IMessageChannel discordChannel,
        Func<DndCampaignRunner, Task<(DndCampaignResult result, string responseText)>> action,
        string actionLabel,
        bool postToChannel,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign == null)
        {
            var msg = "No campaign found. Create one with `/gptcli dnd campaigncreate`.";
            if (postToChannel)
            {
                await discordChannel.SendMessageAsync(msg);
            }
            return (true, false, msg);
        }

        var runner = await LoadOrCreateRunnerAsync(channelState, dndState.ActiveCampaignName, ct);
        if (runner == null)
        {
            var msg = "No party runner available. Create a character with `/gptcli dnd charactercreate`.";
            if (postToChannel)
            {
                await discordChannel.SendMessageAsync(msg);
            }
            return (true, false, msg);
        }

        var (res, responseText) = await action(runner);
        if (res == null)
        {
            var msg = TrimToLimit(responseText ?? "error", 1800);
            if (postToChannel)
            {
                await discordChannel.SendMessageAsync(msg);
            }
            return (true, false, msg);
        }

        await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
        var finalResponse = await FormatGameTurnOutputAsync(context, channelState, res, responseText, actionLabel, ct);
        if (postToChannel)
        {
            await SendChunkedAsync(discordChannel, finalResponse);
        }
        return (true, true, finalResponse);
    }

    private async Task PersistRunnerAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        DndLiteCampaignDocument campaign,
        DndCampaignRunner runner,
        CancellationToken ct)
    {
        if (campaign == null || runner == null)
        {
            return;
        }

        var state = runner.ToState();
        var snap = runner.GetState();

        // Normalize party members from PC profiles (stored outside campaigns).
        await NormalizeCampaignRunnerStateFromProfilesAsync(channelState, campaign, state, ct);

        campaign.RunnerState = state;
        campaign.LiveRuntime ??= new DndLiteEncounterLiveRuntime();
        UpdateLiveRuntimeFromSnapshot(campaign, snap, DateTime.UtcNow);
        await SaveCampaignAsync(channelState, campaign, ct);
    }

    private async Task<DndEncounterSnapshot> GetActiveEncounterSnapshotAsync(
        InstructionGPT.ChannelState channelState,
        string campaignName,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, campaignName, ct);
        if (campaign?.RunnerState == null)
        {
            return null;
        }

        var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
        return runner.GetState().ActiveEncounterState;
    }

    private void EnsureTickLoopRunning(ulong channelId)
    {
        if (_moduleContext == null)
        {
            return;
        }

        // If a task is already running, do nothing.
        if (_tickTasksByChannel.TryGetValue(channelId, out var existing) && existing != null && !existing.IsCompleted)
        {
            return;
        }

        StopTickLoop(channelId);

        var cts = new CancellationTokenSource();
        _tickCtsByChannel[channelId] = cts;
        _tickTasksByChannel[channelId] = Task.Run(() => TickLoopAsync(channelId, cts.Token));
    }

    private void StopTickLoop(ulong channelId)
    {
        if (_tickCtsByChannel.TryRemove(channelId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task TickLoopAsync(ulong channelId, CancellationToken ct)
    {
        // A lightweight, module-owned loop to keep encounters moving (NPC autoplay + timeouts).
        while (!ct.IsCancellationRequested)
        {
            var delaySeconds = 5;
            try
            {
                var ch = _moduleContext.Client.GetChannel(channelId) as IMessageChannel;
                if (ch == null)
                {
                    StopTickLoop(channelId);
                    return;
                }

                var channelState = _moduleContext.Host.GetOrCreateChannelState(channelId);
                if (ch is IGuildChannel guildChannel)
                {
                    _moduleContext.Host.EnsureChannelStateMetadata(channelState, guildChannel);
                }

                if (!_moduleContext.Host.IsChannelGuildMatch(channelState, ch, "dnd-tick"))
                {
                    StopTickLoop(channelId);
                    return;
                }

                if (!InstructionGPT.IsModuleEnabled(channelState, Id))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                var lockHandle = _channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
                await lockHandle.WaitAsync(ct);
                try
                {
                    var dndState = await GetOrLoadStateAsync(channelState, ct);
                    if (!string.Equals(NormalizeMode(dndState.Mode), ModeGame, StringComparison.OrdinalIgnoreCase))
                    {
                        StopTickLoop(channelId);
                        return;
                    }

                    dndState.Live ??= new DndLiteLiveConfig();
                    NormalizeState(dndState);
                    delaySeconds = dndState.Live.TickSeconds;

                    await RunTickIterationAsync(ch, channelState, dndState, ct);
                }
                finally
                {
                    lockHandle.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Best-effort: don't crash the tick loop.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 1, 30)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunTickIterationAsync(
        IMessageChannel discordChannel,
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign?.RunnerState == null)
        {
            return;
        }

        campaign.LiveRuntime ??= new DndLiteEncounterLiveRuntime();

        var runner = await LoadOrCreateRunnerAsync(channelState, dndState.ActiveCampaignName, ct);
        if (runner == null)
        {
            return;
        }

        var snap = runner.GetState();
        if (snap?.ActiveEncounterState == null)
        {
            // No active encounter: clear runtime.
            if (ResetLiveRuntime(campaign.LiveRuntime))
            {
                await SaveCampaignAsync(channelState, campaign, ct);
            }
            return;
        }

        // Maintain runtime tracking even if no auto-steps happen this tick.
        var runtimeChanged = UpdateLiveRuntimeFromSnapshot(campaign, snap, DateTime.UtcNow);

        // If encounter completed, stop autoplay and clear runtime.
        if (snap.ActiveEncounterState.IsCompleted)
        {
            if (ResetLiveRuntime(campaign.LiveRuntime) || runtimeChanged)
            {
                await SaveCampaignAsync(channelState, campaign, ct);
            }
            return;
        }

        // Encounter time limit enforcement (module-side).
        if (campaign.LiveRuntime.EncounterStartedUtc != default &&
            dndState.Live.EncounterTimeoutSeconds > 0 &&
            DateTime.UtcNow - campaign.LiveRuntime.EncounterStartedUtc > TimeSpan.FromSeconds(dndState.Live.EncounterTimeoutSeconds))
        {
            ClearActiveEncounter(campaign);
            await SaveCampaignAsync(channelState, campaign, ct);
            try { await discordChannel.SendMessageAsync("Encounter timed out and was ended by the game master."); } catch { }
            return;
        }

        // Auto-play/timeout steps (bounded).
        for (var step = 0; step < dndState.Live.MaxAutoStepsPerTick; step++)
        {
            snap = runner.GetState();
            if (snap?.ActiveEncounterState == null || snap.ActiveEncounterState.IsCompleted)
            {
                break;
            }

            var runnerState = runner.ToState();
            var pending = runnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>();
            var next = DndTurnResult.BuildNextRequest(snap.ActiveEncounterState, pending);

            if (next.Kind is DndNextRequestKind.Completed)
            {
                break;
            }

            var currentActorId = next.CurrentActorId ?? snap.ActiveEncounterState.CurrentActorId ?? string.Empty;
            var isPc = IsPcActorId(currentActorId);
            var isNpc = IsNpcActorId(currentActorId);
            var nowUtc = DateTime.UtcNow;
            var turnStartedUtc = campaign.LiveRuntime.CurrentActorTurnStartedUtc == default ? nowUtc : campaign.LiveRuntime.CurrentActorTurnStartedUtc;
            var timedOut = dndState.Live.PlayerTurnTimeoutSeconds > 0 &&
                           nowUtc - turnStartedUtc > TimeSpan.FromSeconds(dndState.Live.PlayerTurnTimeoutSeconds);

            // Keep runtime updated as we progress.
            runtimeChanged |= UpdateLiveRuntimeFromSnapshot(campaign, snap, nowUtc);

            // Initiative stage: auto-roll for non-PC actors only (NPCs + enemies).
            if (next.Kind == DndNextRequestKind.NeedInitiativeRolls)
            {
                var rolled = new List<string>();
                foreach (var r in pending.Where(r => r != null && r.Kind == DndPendingRollKind.Initiative).ToList())
                {
                    if (string.IsNullOrWhiteSpace(r.ActorId) || IsPcActorId(r.ActorId))
                    {
                        continue;
                    }

                    var res = runner.RollInitiative(r.ActorId);
                    if (res != null && res.Ok)
                    {
                        rolled.Add(r.ActorId);
                    }
                }

                if (rolled.Count == 0)
                {
                    break;
                }

                await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                snap = runner.GetState();
                var line = "Auto-rolled initiative for: " + string.Join(", ", rolled.Select(id =>
                {
                    var a = snap.ActiveEncounterState.Actors.TryGetValue(id, out var st) ? st : null;
                    var total = a?.InitiativeTotal.HasValue == true ? a.InitiativeTotal.Value.ToString() : "?";
                    return $"{a?.Name ?? id}={total}";
                }));
                try { await SendChunkedAsync(discordChannel, TrimToLimit(line, DiscordMessageLimit)); } catch { }
                continue;
            }

            // Pending rolls: auto-resolve (policy).
            if (next.Kind == DndNextRequestKind.NeedRolls)
            {
                var policy = NormalizeAutoRollPolicy(dndState.Live.AutoRollPolicy);
                var allow = policy switch
                {
                    "all" => timedOut || !isPc,
                    "npc-only" => !isPc,
                    _ => false
                };

                if (!allow)
                {
                    break;
                }

                var res = runner.RollAll();
                await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                var legacyText = RenderTurnResult(res);
                var text = await FormatGameTurnOutputAsync(_moduleContext, channelState, res, legacyText, "autoroll", ct);
                try { await SendChunkedAsync(discordChannel, text); } catch { }
                continue;
            }

            // Need action: autoplay NPCs, timeout PCs.
            if (next.Kind == DndNextRequestKind.NeedAction)
            {
                if (isNpc && dndState.Live.NpcAutoplayEnabled)
                {
                    var (npcRes, npcFlavor, npcLegacyText) = await RunNpcAutoplayStepAsync(channelState, dndState, campaign, runner, currentActorId, ct);
                    if (npcRes == null)
                    {
                        break;
                    }

                    await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                    var npcText = await FormatGameTurnOutputAsync(
                        _moduleContext,
                        channelState,
                        npcRes,
                        npcLegacyText,
                        "npc-autoplay",
                        ct,
                        forcedLead: npcFlavor);
                    try { await SendChunkedAsync(discordChannel, npcText); } catch { }
                    continue;
                }

                if (isPc && timedOut)
                {
                    var res = runner.Pass(currentActorId);
                    await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                    var legacyText = RenderTurnResult(res);
                    var text = await FormatGameTurnOutputAsync(
                        _moduleContext,
                        channelState,
                        res,
                        legacyText,
                        "auto-pass",
                        ct,
                        forcedLead: "⏱️ **Turn timer hits zero.** Auto-pass kicks in and the fight keeps moving.");
                    try { await SendChunkedAsync(discordChannel, TrimToLimit(text, 3500)); } catch { }
                    continue;
                }

                break;
            }

            break;
        }

        if (runtimeChanged)
        {
            await SaveCampaignAsync(channelState, campaign, ct);
        }
    }

    private async Task<(DndCampaignResult result, string flavorLine, string legacyText)> RunNpcAutoplayStepAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        DndLiteCampaignDocument campaign,
        DndCampaignRunner runner,
        string npcActorId,
        CancellationToken ct)
    {
        var snap = runner.GetState();
        var enc = snap.ActiveEncounterState;
        if (enc?.Actors == null || !enc.Actors.TryGetValue(npcActorId, out var npc) || npc == null || !npc.IsAlive)
        {
            return (null, null, "NPC not found/alive.");
        }

        var target = ChooseNpcTarget(enc);
        if (target == null)
        {
            var res0 = runner.Pass(npcActorId);
            return (res0, null, RenderTurnResult(res0));
        }

        var shouldCast = npc.Mp >= DndRuleset.Default.SpellMpCost &&
                         DndRuleset.Default.GetStatMod(npc.Stats.SpellPower) >= DndRuleset.Default.GetStatMod(npc.Stats.Str);

        DndCampaignResult res;
        if (shouldCast)
        {
            res = runner.CastSpell(npcActorId, target.ActorId);
        }
        else
        {
            res = runner.Attack(npcActorId, target.ActorId);
        }

        string flavorLine = null;
        if (dndState.Live.NpcFlavorEnabled)
        {
            var npcProfile = await LoadNpcProfileAsync(channelState, npcActorId, ct);
            if (npcProfile != null)
            {
                var flavor = await GenerateNpcFlavorLineAsync(_moduleContext, channelState, campaign, npcProfile, enc, target, ct);
                if (!string.IsNullOrWhiteSpace(flavor))
                {
                    flavorLine = flavor.Trim();
                }
            }
        }

        return (res, flavorLine, RenderTurnResult(res));
    }

    private static DndActorSnapshot ChooseNpcTarget(DndEncounterSnapshot enc)
    {
        var enemies = enc?.Actors?.Values?.Where(a => a != null && a.IsAlive && a.Side == DndSide.Enemy).ToList() ?? new List<DndActorSnapshot>();
        if (enemies.Count == 0)
        {
            return null;
        }

        var boss = enemies.FirstOrDefault(e => e.IsBoss);
        if (boss != null)
        {
            return boss;
        }

        return enemies.OrderBy(e => e.Hp).ThenBy(e => e.ActorId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string GuessTemplateId(DndEncounterSnapshot enc)
    {
        var enemy = enc?.Actors?.Values?.FirstOrDefault(a => a != null && a.Side == DndSide.Enemy);
        var id = enemy?.ActorId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var idx = id.IndexOf(':');
        if (idx <= 0)
        {
            return null;
        }

        return id[..idx];
    }

    private static void ClearActiveEncounter(DndLiteCampaignDocument campaign)
    {
        if (campaign?.RunnerState == null)
        {
            return;
        }

        campaign.RunnerState.ActiveEncounter = null;
        campaign.RunnerState.ActiveEncounterId = string.Empty;
        campaign.RunnerState.ActiveEncounterName = string.Empty;
        campaign.LiveRuntime ??= new DndLiteEncounterLiveRuntime();
        ResetLiveRuntime(campaign.LiveRuntime);
    }

    private static bool ResetLiveRuntime(DndLiteEncounterLiveRuntime rt)
    {
        if (rt == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rt.EncounterId) &&
            rt.EncounterStartedUtc == default &&
            string.IsNullOrWhiteSpace(rt.CurrentActorId) &&
            rt.CurrentActorTurnStartedUtc == default)
        {
            return false;
        }

        rt.EncounterId = string.Empty;
        rt.EncounterStartedUtc = default;
        rt.CurrentActorId = string.Empty;
        rt.CurrentActorTurnStartedUtc = default;
        return true;
    }

    private static bool UpdateLiveRuntimeFromSnapshot(DndLiteCampaignDocument campaign, DndCampaignSnapshot snap, DateTime utcNow)
    {
        if (campaign == null)
        {
            return false;
        }

        campaign.LiveRuntime ??= new DndLiteEncounterLiveRuntime();

        if (snap?.ActiveEncounterState == null || snap.ActiveEncounterState.IsCompleted)
        {
            return ResetLiveRuntime(campaign.LiveRuntime);
        }

        var encounterId = campaign.RunnerState?.ActiveEncounterId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encounterId))
        {
            encounterId = "enc";
        }

        var changed = false;
        if (!string.Equals(campaign.LiveRuntime.EncounterId, encounterId, StringComparison.OrdinalIgnoreCase))
        {
            campaign.LiveRuntime.EncounterId = encounterId;
            campaign.LiveRuntime.EncounterStartedUtc = utcNow;
            campaign.LiveRuntime.CurrentActorId = snap.ActiveEncounterState.CurrentActorId ?? string.Empty;
            campaign.LiveRuntime.CurrentActorTurnStartedUtc = utcNow;
            return true;
        }

        if (campaign.LiveRuntime.EncounterStartedUtc == default)
        {
            campaign.LiveRuntime.EncounterStartedUtc = utcNow;
            changed = true;
        }

        var currentActorId = snap.ActiveEncounterState.CurrentActorId ?? string.Empty;
        if (campaign.LiveRuntime.CurrentActorTurnStartedUtc == default)
        {
            campaign.LiveRuntime.CurrentActorTurnStartedUtc = utcNow;
            changed = true;
        }

        if (!string.Equals(campaign.LiveRuntime.CurrentActorId ?? string.Empty, currentActorId, StringComparison.OrdinalIgnoreCase))
        {
            campaign.LiveRuntime.CurrentActorId = currentActorId;
            campaign.LiveRuntime.CurrentActorTurnStartedUtc = utcNow;
            changed = true;
        }

        return changed;
    }

    private async Task<DndCampaignRunner> LoadOrCreateRunnerAsync(InstructionGPT.ChannelState channelState, string campaignName, CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, campaignName, ct);
        if (campaign == null)
        {
            return null;
        }

        if (campaign.RunnerState != null && campaign.RunnerState.Party is { Count: > 0 })
        {
            var runner1 = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);

            // Ensure templates from doc are present.
            foreach (var t in campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
            {
                if (t?.Mechanics == null)
                {
                    continue;
                }

                var exists = runner1.ListEncounterTemplates().Any(x => string.Equals(x.TemplateId, t.Mechanics.TemplateId, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    runner1.RegisterEncounterTemplate(t.Mechanics);
                }
            }

            return runner1;
        }

        // Bootstrap runner from party profiles.
        var partyDoc = await LoadPartyAsync(channelState, campaignName, ct);
        partyDoc ??= new DndLitePartyDocument();
        partyDoc.PlayerUserIds ??= new List<ulong>();
        partyDoc.NpcActorIds ??= new List<string>();
        if (partyDoc.PlayerUserIds.Count == 0 && partyDoc.NpcActorIds.Count == 0)
        {
            return null;
        }

        var members = new List<DndCampaignPartyMember>();
        foreach (var userId in partyDoc.PlayerUserIds.Distinct().OrderBy(id => id))
        {
            var pc = await LoadPcProfileAsync(channelState, userId, ct);
            if (pc == null || string.IsNullOrWhiteSpace(pc.ActorId) || pc.Stats == null || pc.MaxHp <= 0)
            {
                continue;
            }

            members.Add(new DndCampaignPartyMember(
                ActorId: pc.ActorId,
                Name: pc.Name,
                Stats: pc.Stats,
                MaxHp: pc.MaxHp,
                Hp: pc.MaxHp,
                MaxMp: pc.MaxMp,
                Mp: pc.MaxMp));
        }

        foreach (var npcId in partyDoc.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var npc = await LoadNpcProfileAsync(channelState, npcId.Trim(), ct);
            if (npc == null || string.IsNullOrWhiteSpace(npc.ActorId) || npc.Stats == null || npc.MaxHp <= 0)
            {
                continue;
            }

            members.Add(new DndCampaignPartyMember(
                ActorId: npc.ActorId,
                Name: npc.Name,
                Stats: npc.Stats,
                MaxHp: npc.MaxHp,
                Hp: npc.MaxHp,
                MaxMp: npc.MaxMp,
                Mp: npc.MaxMp));
        }

        if (members.Count == 0)
        {
            return null;
        }

        var dice = _diceByChannel.GetOrAdd(channelState.ChannelId, _ => new RandomDiceRoller());
        var runner = new DndCampaignRunner(new DndCampaignDefinition(members), diceRoller: dice);

        foreach (var t in campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
        {
            if (t?.Mechanics != null)
            {
                runner.RegisterEncounterTemplate(t.Mechanics);
            }
        }

        campaign.RunnerState = runner.ToState();
        await SaveCampaignAsync(channelState, campaign, ct);
        return runner;
    }

    private async Task<bool> NormalizeCampaignRunnerStateFromProfilesAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteCampaignDocument campaign,
        CancellationToken ct)
    {
        if (campaign == null)
        {
            return false;
        }

        // If there is no runner state yet, bootstrap it from party roster + PC profiles so the campaign can
        // reference stable character ids and other docs can normalize against it.
        if (campaign.RunnerState == null)
        {
            var party = await LoadPartyAsync(channelState, campaign.CampaignName, ct);
            party ??= new DndLitePartyDocument();
            party.PlayerUserIds ??= new List<ulong>();
            party.NpcActorIds ??= new List<string>();
            if (party.PlayerUserIds.Count == 0 && party.NpcActorIds.Count == 0)
            {
                return false;
            }

            var members = new List<DndCampaignPartyMember>();
            foreach (var userId in party.PlayerUserIds.Distinct().OrderBy(id => id))
            {
                var pc = await LoadPcProfileAsync(channelState, userId, ct);
                if (pc == null || pc.Stats == null || pc.MaxHp <= 0)
                {
                    continue;
                }

                members.Add(new DndCampaignPartyMember(
                    ActorId: pc.ActorId ?? ToActorId(userId),
                    Name: string.IsNullOrWhiteSpace(pc.Name) ? (pc.ActorId ?? ToActorId(userId)) : pc.Name.Trim(),
                    Stats: pc.Stats,
                    MaxHp: Math.Max(1, pc.MaxHp),
                    Hp: Math.Max(1, pc.MaxHp),
                    MaxMp: Math.Max(0, pc.MaxMp),
                    Mp: Math.Max(0, pc.MaxMp)));
            }

            foreach (var npcId in party.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var npc = await LoadNpcProfileAsync(channelState, npcId.Trim(), ct);
                if (npc == null || npc.Stats == null || npc.MaxHp <= 0)
                {
                    continue;
                }

                members.Add(new DndCampaignPartyMember(
                    ActorId: npc.ActorId,
                    Name: string.IsNullOrWhiteSpace(npc.Name) ? npc.ActorId : npc.Name.Trim(),
                    Stats: npc.Stats,
                    MaxHp: Math.Max(1, npc.MaxHp),
                    Hp: Math.Max(1, npc.MaxHp),
                    MaxMp: Math.Max(0, npc.MaxMp),
                    Mp: Math.Max(0, npc.MaxMp)));
            }

            if (members.Count == 0)
            {
                return false;
            }

            campaign.RunnerState = new DndCampaignRunnerState
            {
                Party = members,
                Templates = (campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
                    .Where(t => t?.Mechanics != null)
                    .Select(t => t.Mechanics)
                    .ToList()
            };

            return true;
        }

        return await NormalizeCampaignRunnerStateFromProfilesAsync(channelState, campaign, campaign.RunnerState, ct);
    }

    private async Task<bool> NormalizeCampaignRunnerStateFromProfilesAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteCampaignDocument campaign,
        DndCampaignRunnerState runnerState,
        CancellationToken ct)
    {
        if (campaign == null || runnerState == null)
        {
            return false;
        }

        var changed = false;

        // Keep templates aligned with campaign document.
        runnerState.Templates = (campaign.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>())
            .Where(t => t?.Mechanics != null)
            .Select(t => t.Mechanics)
            .ToList();

        runnerState.Party ??= new List<DndCampaignPartyMember>();

        var partyDoc = await LoadPartyAsync(channelState, campaign.CampaignName, ct) ?? new DndLitePartyDocument();
        partyDoc.PlayerUserIds ??= new List<ulong>();
        partyDoc.NpcActorIds ??= new List<string>();

        var profilesByActorId = new Dictionary<string, (string Name, DndStats Stats, int MaxHp, int MaxMp)>(StringComparer.OrdinalIgnoreCase);
        foreach (var userId in partyDoc.PlayerUserIds.Distinct().OrderBy(id => id))
        {
            var pc = await LoadPcProfileAsync(channelState, userId, ct);
            if (pc == null || pc.Stats == null || pc.MaxHp <= 0)
            {
                continue;
            }

            var actorId = pc.ActorId ?? ToActorId(userId);
            profilesByActorId[actorId] = (
                Name: string.IsNullOrWhiteSpace(pc.Name) ? actorId : pc.Name.Trim(),
                Stats: pc.Stats ?? new DndStats(10, 10, 10, 10, 10),
                MaxHp: Math.Max(1, pc.MaxHp),
                MaxMp: Math.Max(0, pc.MaxMp));
        }

        foreach (var npcId in partyDoc.NpcActorIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var npc = await LoadNpcProfileAsync(channelState, npcId.Trim(), ct);
            if (npc == null || npc.Stats == null || npc.MaxHp <= 0 || string.IsNullOrWhiteSpace(npc.ActorId))
            {
                continue;
            }

            profilesByActorId[npc.ActorId] = (
                Name: string.IsNullOrWhiteSpace(npc.Name) ? npc.ActorId : npc.Name.Trim(),
                Stats: npc.Stats ?? new DndStats(10, 10, 10, 10, 10),
                MaxHp: Math.Max(1, npc.MaxHp),
                MaxMp: Math.Max(0, npc.MaxMp));
        }

        // Update existing members from profiles.
        for (var i = 0; i < runnerState.Party.Count; i++)
        {
            var p = runnerState.Party[i];
            if (p == null || string.IsNullOrWhiteSpace(p.ActorId))
            {
                continue;
            }

            if (!profilesByActorId.TryGetValue(p.ActorId, out var prof))
            {
                continue;
            }

            var maxHp = Math.Max(1, prof.MaxHp);
            var maxMp = Math.Max(0, prof.MaxMp);
            var hp = Math.Clamp(p.Hp, 0, maxHp);
            var mp = Math.Clamp(p.Mp, 0, maxMp);

            var updated = p with
            {
                Name = string.IsNullOrWhiteSpace(prof.Name) ? p.Name : prof.Name.Trim(),
                Stats = prof.Stats ?? p.Stats,
                MaxHp = maxHp,
                Hp = hp,
                MaxMp = maxMp,
                Mp = mp
            };

            if (!Equals(updated, p))
            {
                runnerState.Party[i] = updated;
                changed = true;
            }
        }

        // If no active in-progress encounter, we can add missing roster members.
        var hasInProgressEncounter = runnerState.ActiveEncounter != null && !runnerState.ActiveEncounter.Completed;
        if (!hasInProgressEncounter)
        {
            foreach (var (actorId, prof) in profilesByActorId.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    continue;
                }

                if (runnerState.Party.Any(m => m != null && string.Equals(m.ActorId, actorId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                runnerState.Party.Add(new DndCampaignPartyMember(
                    ActorId: actorId,
                    Name: string.IsNullOrWhiteSpace(prof.Name) ? actorId : prof.Name.Trim(),
                    Stats: prof.Stats ?? new DndStats(10, 10, 10, 10, 10),
                    MaxHp: Math.Max(1, prof.MaxHp),
                    Hp: Math.Max(1, prof.MaxHp),
                    MaxMp: Math.Max(0, prof.MaxMp),
                    Mp: Math.Max(0, prof.MaxMp)));
                changed = true;
            }
        }

        // IMPORTANT: do not normalize runnerState.ActiveEncounter. It must remain stable for resuming.
        return changed;
    }

    private DndCampaignRunner RestoreCampaignRunner(ulong channelId, DndCampaignRunnerState state)
    {
        var dice = _diceByChannel.GetOrAdd(channelId, _ => new RandomDiceRoller());
        return DndCampaignRunner.FromState(state, diceRoller: dice);
    }

    private static string ResolveTargetActorId(DndEncounterSnapshot snap, string target, out string error)
    {
        error = null;
        if (snap?.Actors == null || snap.Actors.Count == 0)
        {
            error = "No active encounter.";
            return null;
        }

        var wanted = target?.Trim();
        if (string.IsNullOrWhiteSpace(wanted))
        {
            error = "Target required.";
            return null;
        }

        // Exact id.
        if (snap.Actors.TryGetValue(wanted, out var exact) && exact != null && exact.IsAlive && exact.Side == DndSide.Enemy)
        {
            return exact.ActorId;
        }

        var matches = snap.Actors.Values
            .Where(a => a != null && a.IsAlive && a.Side == DndSide.Enemy)
            .Where(a =>
                string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                (a.Name?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true) ||
                string.Equals(a.ActorId, wanted, StringComparison.OrdinalIgnoreCase) ||
                a.ActorId.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0].ActorId;
        }

        if (matches.Count > 1)
        {
            error = "Target is ambiguous. Matches: " + string.Join(", ", matches.Take(6).Select(m => $"{m.ActorId}:{m.Name}"));
            return null;
        }

        error = $"Target \"{wanted}\" not found. Ask for targets or name a listed enemy.";
        return null;
    }

    private static string RenderTurnResult(DndCampaignResult res)
    {
        if (res == null)
        {
            return "error";
        }

        if (!res.Ok)
        {
            return string.IsNullOrWhiteSpace(res.Error) ? "error" : res.Error;
        }

        var sb = new StringBuilder();
        foreach (var e in res.NewCampaignLedgerEntries ?? Array.Empty<DndCampaignLedgerEntry>())
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Message))
            {
                continue;
            }

            sb.AppendLine(e.Message.Trim());
        }

        if (res.EncounterResult?.State != null)
        {
            sb.AppendLine(RenderPartySummary(res.Campaign));
            sb.AppendLine(RenderNextRequest(res.EncounterResult.NextRequest));
        }

        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static GameNarrationSettings ResolveGameNarrationSettings(DiscordModuleContext context)
    {
        var enabled = IsGameNarrationEnabled(context);
        var mode = GetGameNarrationMode(context);
        var emojiLevel = GetGameNarrationEmojiLevel(context);
        var timeoutSeconds = GetGameNarrationTimeoutSeconds(context);
        var maxLeadChars = GetGameNarrationMaxLeadChars(context);
        return new GameNarrationSettings(enabled, mode, emojiLevel, timeoutSeconds, maxLeadChars);
    }

    private async Task<string> FormatGameTurnOutputAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndCampaignResult res,
        string fallbackText,
        string actionLabel,
        CancellationToken ct,
        string forcedLead = null)
    {
        context ??= _moduleContext;
        var settings = ResolveGameNarrationSettings(context);

        var mechanics = RenderCompactMechanics(res);
        if (string.IsNullOrWhiteSpace(mechanics))
        {
            mechanics = TrimToLimit((fallbackText ?? (res?.Error ?? "error")).Trim(), 2600);
        }

        if (!settings.Enabled || string.Equals(settings.Mode, GameNarrationModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return TrimToLimit(mechanics, 3500);
        }

        var lead = string.IsNullOrWhiteSpace(forcedLead) ? null : forcedLead.Trim();
        if (string.IsNullOrWhiteSpace(lead))
        {
            if (string.Equals(settings.Mode, GameNarrationModeLlm, StringComparison.OrdinalIgnoreCase))
            {
                lead = await GenerateGameFlavorLeadAsync(context, channelState, res, actionLabel, settings, ct);
            }

            if (string.IsNullOrWhiteSpace(lead))
            {
                lead = BuildDeterministicGameLead(res, actionLabel, settings.EmojiLevel);
            }
        }

        lead = string.IsNullOrWhiteSpace(lead)
            ? null
            : TrimToLimit(lead.Replace("\r", " ").Replace("\n", " ").Trim(), settings.MaxLeadChars);

        if (string.IsNullOrWhiteSpace(lead))
        {
            return TrimToLimit(mechanics, 3500);
        }

        var sb = new StringBuilder();
        sb.AppendLine(lead);
        sb.AppendLine();
        sb.AppendLine("**Mechanics**");
        sb.AppendLine(mechanics);
        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static string RenderCompactMechanics(DndCampaignResult res)
    {
        if (res == null)
        {
            return string.Empty;
        }

        if (!res.Ok)
        {
            var err = string.IsNullOrWhiteSpace(res.Error) ? "Action failed." : res.Error.Trim();
            return $"❌ {TrimToLimit(err, 800)}";
        }

        var sb = new StringBuilder();
        var highlights = ExtractMechanicsHighlights(res).Take(4).ToList();
        if (highlights.Count > 0)
        {
            foreach (var line in highlights)
            {
                sb.AppendLine($"{PickMechanicsEmoji(line)} {line}");
            }
        }

        if (res.Campaign != null)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.AppendLine($"📊 {RenderPartySummary(res.Campaign)}");
        }

        var next = res.EncounterResult?.NextRequest;
        var nextText = next == null ? string.Empty : RenderNextRequest(next);
        if (!string.IsNullOrWhiteSpace(nextText))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            var lines = nextText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                sb.AppendLine($"🧭 {lines[0].Trim()}");
                foreach (var line in lines.Skip(1).Take(8))
                {
                    sb.AppendLine($"🧾 {line.Trim()}");
                }
            }
        }

        return TrimToLimit(sb.ToString().Trim(), 2400);
    }

    private static List<string> ExtractMechanicsHighlights(DndCampaignResult res)
    {
        var entries = (res?.NewCampaignLedgerEntries ?? Array.Empty<DndCampaignLedgerEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Message))
            .Select(e => StripEncounterPrefix(e.Message))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (entries.Count == 0)
        {
            return new List<string>();
        }

        var filtered = entries
            .Where(line => !line.StartsWith("Round ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filtered.Count == 0)
        {
            filtered = entries;
        }

        return filtered
            .TakeLast(4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string PickMechanicsEmoji(string line)
    {
        var l = (line ?? string.Empty).Trim();
        if (l.Contains("=> HIT", StringComparison.OrdinalIgnoreCase) || l.Contains("declares Attack", StringComparison.OrdinalIgnoreCase))
        {
            return "⚔️";
        }
        if (l.Contains("=> MISS", StringComparison.OrdinalIgnoreCase))
        {
            return "🛡️";
        }
        if (l.Contains("deals", StringComparison.OrdinalIgnoreCase) || l.Contains("damage", StringComparison.OrdinalIgnoreCase))
        {
            return "💥";
        }
        if (l.Contains("needs", StringComparison.OrdinalIgnoreCase) || l.Contains("roll", StringComparison.OrdinalIgnoreCase))
        {
            return "🎲";
        }
        if (l.Contains("spends", StringComparison.OrdinalIgnoreCase) || l.Contains("MP", StringComparison.OrdinalIgnoreCase))
        {
            return "🪄";
        }
        if (l.Contains("Encounter completed: victory", StringComparison.OrdinalIgnoreCase))
        {
            return "🏆";
        }
        if (l.Contains("Encounter completed: defeat", StringComparison.OrdinalIgnoreCase))
        {
            return "💀";
        }
        if (l.StartsWith("Turn:", StringComparison.OrdinalIgnoreCase))
        {
            return "🧭";
        }
        return "🧩";
    }

    private static string StripEncounterPrefix(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return Regex.Replace(message.Trim(), @"^\[[^\]]+\]\s*", string.Empty, RegexOptions.CultureInvariant);
    }

    private static string BuildDeterministicGameLead(DndCampaignResult res, string actionLabel, string emojiLevel)
    {
        var level = NormalizeGameNarrationEmojiLevel(emojiLevel);
        var combatEmoji = level switch
        {
            "low" => "⚔️",
            "high" => "⚔️🔥🎲",
            _ => "⚔️🎲"
        };
        var victoryEmoji = level switch
        {
            "low" => "🏆",
            "high" => "🏆🔥✨",
            _ => "🏆✨"
        };
        var dangerEmoji = level switch
        {
            "low" => "⚠️",
            "high" => "⚠️💀",
            _ => "⚠️🛡️"
        };

        if (res == null)
        {
            return $"{dangerEmoji} **Hold pressure.** Reset your footing and call the next move.";
        }

        if (!res.Ok)
        {
            var err = string.IsNullOrWhiteSpace(res.Error) ? "action failed" : res.Error.Trim();
            return $"{dangerEmoji} **That action stalls.** {TrimToLimit(err, 180)}";
        }

        var next = res.EncounterResult?.NextRequest;
        var highlights = ExtractMechanicsHighlights(res);
        var beat = highlights.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(beat))
        {
            beat = TrimToLimit(beat, 180);
        }

        if (next?.Kind == DndNextRequestKind.Completed)
        {
            var won = (beat ?? string.Empty).Contains("victory", StringComparison.OrdinalIgnoreCase);
            if (won)
            {
                return $"{victoryEmoji} **Boss down.** You broke their line and owned the finish.";
            }
            return $"{dangerEmoji} **The encounter closes.** Regroup fast and decide your next push.";
        }

        if (next?.Kind == DndNextRequestKind.NeedRolls || next?.Kind == DndNextRequestKind.NeedInitiativeRolls)
        {
            var line = string.IsNullOrWhiteSpace(beat) ? "Dice are up." : beat;
            return $"🎲 **Pressure moment.** {line}";
        }

        if (next?.Kind == DndNextRequestKind.NeedAction)
        {
            var line = string.IsNullOrWhiteSpace(beat) ? "Your window is open." : beat;
            return $"{combatEmoji} **Stay sharp.** {line}";
        }

        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            return $"{combatEmoji} **{actionLabel.Trim()} resolved.** Keep momentum and call the next beat.";
        }

        return $"{combatEmoji} **Combat pressure stays high.** Keep your line and drive the turn.";
    }

    private async Task<string> GenerateGameFlavorLeadAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndCampaignResult res,
        string actionLabel,
        GameNarrationSettings settings,
        CancellationToken ct)
    {
        if (context == null || channelState == null || settings == null)
        {
            return null;
        }

        var model = ResolveModel(context, channelState);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Temperature = 0.7f,
            MaxCompletionTokens = 180,
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System,
                    "You are a cinematic tabletop GM narrator. " +
                    "Return strict JSON only with key exactly: lead. " +
                    "lead must be one short paragraph, vivid, in-the-moment, no code fences."),
                new(ChatCompletionRole.User, BuildGameFlavorPrompt(res, actionLabel, settings.EmojiLevel, settings.MaxLeadChars))
            }
        };

        try
        {
            var responseTask = context.OpenAILogic.CreateChatCompletionAsync(request);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(settings.TimeoutSeconds), ct);
            var done = await Task.WhenAny(responseTask, timeoutTask);
            if (done != responseTask)
            {
                return null;
            }

            var response = await responseTask;
            if (!response.Successful)
            {
                return null;
            }

            var content = ExtractChatMessageText(response.Choices?.FirstOrDefault()?.Message)?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var json = ExtractJsonObject(content);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<GameFlavorResponseDto>(json, _jsonOptions);
            var lead = parsed?.Lead?.Trim();
            if (string.IsNullOrWhiteSpace(lead))
            {
                return null;
            }

            lead = lead.Replace("\r", " ").Replace("\n", " ").Trim();
            return TrimToLimit(lead, settings.MaxLeadChars);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildGameFlavorPrompt(
        DndCampaignResult res,
        string actionLabel,
        string emojiLevel,
        int maxLeadChars)
    {
        var highlights = ExtractMechanicsHighlights(res);
        var next = res?.EncounterResult?.NextRequest;
        var nextLine = next == null ? string.Empty : RenderNextRequest(next).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Write a GM combat lead line for Discord.");
        sb.AppendLine($"Character limit: {Math.Clamp(maxLeadChars, 120, 1200)}");
        sb.AppendLine($"Emoji density: {NormalizeGameNarrationEmojiLevel(emojiLevel)} (2-5 emojis when possible).");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Do not invent new mechanics, dice values, or damage.");
        sb.AppendLine("- Keep urgency, pressure, and table energy high.");
        sb.AppendLine("- Keep it to one short paragraph.");
        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            sb.AppendLine($"Action label: {actionLabel}");
        }
        if (highlights.Count > 0)
        {
            sb.AppendLine("Mechanical highlights:");
            foreach (var line in highlights.TakeLast(4))
            {
                sb.AppendLine($"- {line}");
            }
        }
        if (!string.IsNullOrWhiteSpace(nextLine))
        {
            sb.AppendLine($"Next prompt summary: {nextLine}");
        }
        return sb.ToString().Trim();
    }

    private static string RenderPartySummary(DndCampaignSnapshot snap)
    {
        if (snap?.Party == null || snap.Party.Count == 0)
        {
            return "Party: (none)";
        }

        var parts = snap.Party.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Name} HP {p.Hp}/{p.MaxHp} MP {p.Mp}/{p.MaxMp}")
            .ToList();

        return "Party: " + string.Join(" | ", parts);
    }

    private static string RenderEncounterSnapshot(DndEncounterSnapshot enc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Encounter: phase={enc.Phase}, round={enc.RoundNumber}, current={enc.CurrentActorId}");
        sb.AppendLine(RenderTargets(enc));
        return TrimToLimit(sb.ToString().Trim(), 1700);
    }

    private static string RenderTargets(DndEncounterSnapshot enc)
    {
        var enemies = enc.Actors.Values.Where(a => a != null && a.Side == DndSide.Enemy).OrderBy(a => a.IsBoss ? 0 : 1).ThenBy(a => a.Name).ToList();
        if (enemies.Count == 0)
        {
            return "Enemies: (none)";
        }

        var lines = new List<string> { "Enemies:" };
        foreach (var e in enemies)
        {
            var alive = e.IsAlive ? "" : " (down)";
            lines.Add($"- {e.ActorId} | {e.Name}{alive} | HP {e.Hp}/{e.MaxHp} MP {e.Mp}/{e.MaxMp}");
        }

        return TrimToLimit(string.Join("\n", lines), 1700);
    }

    private static string RenderNextRequest(DndNextRequest next)
    {
        if (next == null)
        {
            return "Next: (unknown)";
        }

        if (next.Kind == DndNextRequestKind.Completed)
        {
            return "Next: encounter completed.";
        }

        if (next.Kind == DndNextRequestKind.NeedAction)
        {
            return $"Next: {next.CurrentActorId} to act. Describe the move naturally (attack, cast, or pass).";
        }

        if (next.RequiredRolls == null || next.RequiredRolls.Count == 0)
        {
            return "Next: pending rolls required.";
        }

        var lines = new List<string>();
        lines.Add(next.Kind == DndNextRequestKind.NeedInitiativeRolls
            ? "Next: initiative rolls are pending. Ask to roll initiative (or resolve all pending initiative rolls)."
            : "Next: rolls are pending before the turn can continue. Ask to resolve a specific roll ID (or all pending rolls).");

        var rollParts = next.RequiredRolls
            .Take(8)
            .Select(r =>
            {
                var target = string.IsNullOrWhiteSpace(r.TargetId) ? string.Empty : $" vs {r.TargetId}";
                return $"{r.RollId} ({r.Kind} for {r.ActorId}{target})";
            })
            .ToList();
        if (rollParts.Count > 0)
        {
            lines.Add("Pending roll IDs: " + string.Join(", ", rollParts));
        }

        return TrimToLimit(string.Join("\n", lines), 900);
    }

    private static string BuildHelpText()
    {
        return
            "DND simplified gameplay (game mode)\n" +
            "- Natural play: say actions in plain language (for example: \"I attack Boss\", \"I cast at Boss\", \"I pass\")\n" +
            "- `!state` (party + encounter summary)\n" +
            "- `!targets` (list enemies)\n" +
            "- `!attack <target>`\n" +
            "- `!cast <target>`\n" +
            "- `!pass`\n" +
            "- `!roll initiative`\n" +
            "- `!roll <rollId>`\n" +
            "- `!rollall`\n" +
            "- `!ledger [n]`\n\n" +
            "Bang commands are still available, but plain-language actions are preferred.";
    }

    private static string BuildCampaignSummary(DndLiteCampaignDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Campaign \"{doc.CampaignName}\" created.");
        sb.AppendLine();
        sb.AppendLine(TrimToLimit(doc.CampaignMarkdown, 1800));
        sb.AppendLine();
        sb.AppendLine("Encounter templates:");
        foreach (var t in (doc.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>()).Take(12))
        {
            if (t == null)
            {
                continue;
            }

            sb.AppendLine($"- {t.TemplateId} | {t.Name} | adds={(t.Adds?.Count ?? 0)}");
        }

        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static string BuildCampaignCreatePartySummary(
        IReadOnlyList<string> npcsCreatedOrOverwritten,
        IReadOnlyList<string> pcsCreated,
        IReadOnlyList<UnassignedPcDto> unassigned)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Party (from prompt):");

        npcsCreatedOrOverwritten ??= Array.Empty<string>();
        pcsCreated ??= Array.Empty<string>();
        unassigned ??= Array.Empty<UnassignedPcDto>();

        if (npcsCreatedOrOverwritten.Count > 0)
        {
            sb.AppendLine($"- NPC sheets written: {npcsCreatedOrOverwritten.Count}");
            foreach (var x in npcsCreatedOrOverwritten.Take(8))
            {
                sb.AppendLine($"  - {x}");
            }
            if (npcsCreatedOrOverwritten.Count > 8)
            {
                sb.AppendLine($"  - ... +{npcsCreatedOrOverwritten.Count - 8} more");
            }
        }
        else
        {
            sb.AppendLine("- NPC sheets written: 0");
        }

        if (pcsCreated.Count > 0)
        {
            sb.AppendLine($"- PC sheets created (missing only): {pcsCreated.Count}");
            foreach (var x in pcsCreated.Take(8))
            {
                sb.AppendLine($"  - {x}");
            }
            if (pcsCreated.Count > 8)
            {
                sb.AppendLine($"  - ... +{pcsCreated.Count - 8} more");
            }
        }
        else
        {
            sb.AppendLine("- PC sheets created (missing only): 0");
        }

        var unassignedCount = unassigned.Count(u => u != null && !string.IsNullOrWhiteSpace(u.Name));
        if (unassignedCount > 0)
        {
            sb.AppendLine($"- Unassigned PCs mentioned: {unassignedCount}");
            foreach (var u in unassigned.Where(u => u != null && !string.IsNullOrWhiteSpace(u.Name)).Take(6))
            {
                var concept = string.IsNullOrWhiteSpace(u.Concept) ? "" : $" | {u.Concept.Trim()}";
                sb.AppendLine($"  - {u.Name.Trim()}{concept}");
            }
            if (unassignedCount > 6)
            {
                sb.AppendLine($"  - ... +{unassignedCount - 6} more");
            }
        }

        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static string BuildCharacterSummary(DndLitePcProfile pc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Character for <@{pc.UserId}>: **{pc.Name}**");
        sb.AppendLine($"Concept: {pc.Concept}");
        sb.AppendLine($"HP {pc.MaxHp}, MP {pc.MaxMp}");
        sb.AppendLine($"Stats: STR {pc.Stats.Str}, DEF {pc.Stats.Def}, DEX {pc.Stats.Dex}, SP {pc.Stats.SpellPower}, LUCK {pc.Stats.Luck}");
        if (!string.IsNullOrWhiteSpace(pc.ProfileMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine(TrimToLimit(pc.ProfileMarkdown, 1600));
        }
        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private async Task<string> BuildPcRosterContextAsync(InstructionGPT.ChannelState channelState, DndLitePartyDocument party, CancellationToken ct)
    {
        party ??= new DndLitePartyDocument();
        party.PlayerUserIds ??= new List<ulong>();

        var ids = party.PlayerUserIds.Distinct().OrderBy(x => x).Take(32).ToList();
        if (ids.Count == 0)
        {
            return "PC roster is empty.";
        }

        var lines = new List<string>();
        foreach (var userId in ids)
        {
            var actorId = ToActorId(userId);
            var pc = await LoadPcProfileAsync(channelState, userId, ct);
            if (pc == null)
            {
                lines.Add($"- {actorId} (sheet missing)");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(pc.Name) ? "?" : pc.Name.Trim();
            var concept = string.IsNullOrWhiteSpace(pc.Concept) ? "" : $" | concept={pc.Concept.Trim()}";
            lines.Add($"- {actorId} | name={name}{concept}");
        }

        return TrimToLimit(string.Join("\n", lines), 1800);
    }

    private static DndStats ClampStats(DndStats stats)
    {
        stats ??= new DndStats(10, 10, 10, 10, 10);
        return new DndStats(
            Str: Math.Clamp(stats.Str, 6, 18),
            Def: Math.Clamp(stats.Def, 6, 18),
            Dex: Math.Clamp(stats.Dex, 6, 18),
            SpellPower: Math.Clamp(stats.SpellPower, 6, 18),
            Luck: Math.Clamp(stats.Luck, 6, 18));
    }

    private static T TryDeserializeArgs<T>(JsonElement args) where T : class
    {
        try
        {
            if (args.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<T>(args.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            if (args.ValueKind == JsonValueKind.String)
            {
                var s = args.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return JsonSerializer.Deserialize<T>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildNpcActorId(string id, string name)
    {
        var slug = SlugifySegment(id);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = SlugifySegment(name);
        }
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }
        return $"npc:{slug}";
    }

    private static bool TryParsePcActorId(string actorId, out ulong userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return false;
        }

        var s = actorId.Trim();
        if (!s.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tail = s[2..];
        return ulong.TryParse(tail, out userId) && userId != 0;
    }

    private static string BuildNpcSummary(DndLiteNpcProfile npc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"NPC: **{npc.Name}** (`{npc.ActorId}`)");
        sb.AppendLine($"Concept: {npc.Concept}");
        sb.AppendLine($"HP {npc.MaxHp}, MP {npc.MaxMp}");
        sb.AppendLine($"Stats: STR {npc.Stats.Str}, DEF {npc.Stats.Def}, DEX {npc.Stats.Dex}, SP {npc.Stats.SpellPower}, LUCK {npc.Stats.Luck}");
        if (!string.IsNullOrWhiteSpace(npc.ProfileMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine(TrimToLimit(npc.ProfileMarkdown, 1600));
        }
        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static string RenderLiveConfig(DndLiteLiveConfig cfg)
    {
        cfg ??= new DndLiteLiveConfig();
        var sb = new StringBuilder();
        sb.AppendLine("Game config:");
        sb.AppendLine($"- tick_seconds: {cfg.TickSeconds}");
        sb.AppendLine($"- player_turn_timeout_seconds: {cfg.PlayerTurnTimeoutSeconds}");
        sb.AppendLine($"- encounter_timeout_seconds: {cfg.EncounterTimeoutSeconds}");
        sb.AppendLine($"- npc_autoplay: {(cfg.NpcAutoplayEnabled ? "true" : "false")}");
        sb.AppendLine($"- autoroll_policy: {NormalizeAutoRollPolicy(cfg.AutoRollPolicy)}");
        sb.AppendLine($"- npc_flavor: {(cfg.NpcFlavorEnabled ? "true" : "false")}");
        return TrimToLimit(sb.ToString().Trim(), 3500);
    }

    private static DndActorDefinition ToEnemyDef(string actorId, string name, bool isBoss, DndStats stats, int maxHp, int maxMp)
    {
        stats ??= new DndStats(10, 10, 10, 10, 10);
        maxHp = Math.Clamp(maxHp, 1, 999);
        maxMp = Math.Max(0, maxMp);

        return new DndActorDefinition(
            ActorId: actorId,
            Name: string.IsNullOrWhiteSpace(name) ? actorId : name.Trim(),
            Side: DndSide.Enemy,
            IsBoss: isBoss,
            Stats: stats,
            MaxHp: maxHp,
            MaxMp: maxMp,
            StartingHp: maxHp,
            StartingMp: maxMp);
    }

    private sealed record CampaignGenerateResult(CampaignCreateResponseDto Package, string Error, int HttpMs, string Model);
    private sealed record DraftUpdateGenerateResult(bool Successful, string Error, string CampaignMarkdown);
    private sealed record ResponsesToolCall(string CallId, string Name, string ArgumentsJson, string Status, bool IsIncomplete);
    private sealed record ResponsesApiResult(bool Successful, string Error, int HttpMs, string ResponseId, string OutputText, List<ResponsesToolCall> ToolCalls, string RawJson);
    private sealed record ResponsesLoopResult(bool Successful, string Error, int HttpMs, string OutputText, int Rounds);
    private sealed record GameNarrationSettings(bool Enabled, string Mode, string EmojiLevel, int TimeoutSeconds, int MaxLeadChars);

    private sealed class CampaignStageOneAccumulator
    {
        public string CampaignMarkdown { get; set; }
        public List<EncounterTemplateDto> Encounters { get; } = new();
        public bool Finalized { get; set; }
        public int CampaignMarkdownSetCalls { get; set; }
    }

    private sealed class CampaignStageTwoAccumulator
    {
        public List<DndLiteFunctionCallDto> FunctionCalls { get; } = new();
        public List<UnassignedPcDto> UnassignedPcs { get; } = new();
        public HashSet<string> NpcIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PcActorIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UnassignedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Finalized { get; set; }
    }

    private sealed class CharacterSheetAccumulator
    {
        public CharacterCreateResponseDto Sheet { get; set; }
        public bool Finalized { get; set; }
    }

    private static string ResolveCampaignBootstrapModel(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        // Responses-based campaign generation should use the primary text model, not the vision model.
        // Some saved channel states still carry older vision defaults (e.g. gpt-5.2-nano) that are invalid.
        var model = ResolveModel(context, channelState);
        return string.IsNullOrWhiteSpace(model) ? "gpt-5.2" : model.Trim();
    }

    private static string ResolveResponsesApiKey(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        var key = context?.DefaultParameters?.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        return context?.Configuration?["OpenAI:ApiKey"]?.Trim();
    }

    private static string ResolveResponsesEndpoint(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        var baseDomain = context?.DefaultParameters?.BaseDomain;
        if (string.IsNullOrWhiteSpace(baseDomain))
        {
            baseDomain = context?.Configuration?["OpenAI:BaseDomain"];
        }

        if (string.IsNullOrWhiteSpace(baseDomain))
        {
            return "https://api.openai.com/v1/responses";
        }

        var trimmed = baseDomain.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}/responses";
        }
        return $"{trimmed}/v1/responses";
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseJsonElement(string json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractResponsesError(string rawJson)
    {
        if (!TryParseJsonElement(rawJson, out var root))
        {
            return null;
        }

        if (TryGetPropertyIgnoreCase(root, "error", out var err))
        {
            if (TryGetPropertyIgnoreCase(err, "message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                var msg = msgEl.GetString();
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    var code = TryGetPropertyIgnoreCase(err, "code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                        ? codeEl.GetString()
                        : null;
                    return string.IsNullOrWhiteSpace(code) ? msg.Trim() : $"{code}: {msg.Trim()}";
                }
            }
        }

        return null;
    }

    private static List<ResponsesToolCall> ExtractResponsesToolCalls(JsonElement root)
    {
        var calls = new List<ResponsesToolCall>();
        if (!TryGetPropertyIgnoreCase(root, "output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(item, "type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(typeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string callId = null;
            if (TryGetPropertyIgnoreCase(item, "call_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String)
            {
                callId = callIdEl.GetString();
            }
            if (string.IsNullOrWhiteSpace(callId) &&
                TryGetPropertyIgnoreCase(item, "id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                callId = idEl.GetString();
            }

            string name = null;
            if (TryGetPropertyIgnoreCase(item, "name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                name = nameEl.GetString();
            }

            string status = null;
            if (TryGetPropertyIgnoreCase(item, "status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            {
                status = statusEl.GetString()?.Trim();
            }

            string arguments = "{}";
            if (TryGetPropertyIgnoreCase(item, "arguments", out var argsEl))
            {
                arguments = argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() : argsEl.GetRawText();
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                var isIncomplete = string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase);
                calls.Add(new ResponsesToolCall(
                    callId ?? string.Empty,
                    name.Trim(),
                    string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments,
                    status ?? string.Empty,
                    isIncomplete));
            }
        }

        return calls;
    }

    private static string ExtractResponsesOutputText(JsonElement root)
    {
        if (TryGetPropertyIgnoreCase(root, "output_text", out var outputTextEl) && outputTextEl.ValueKind == JsonValueKind.String)
        {
            var direct = outputTextEl.GetString();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct.Trim();
            }
        }

        if (!TryGetPropertyIgnoreCase(root, "output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var chunks = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(item, "type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeEl.GetString();
            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) &&
                TryGetPropertyIgnoreCase(item, "content", out var contentEl) &&
                contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in contentEl.EnumerateArray())
                {
                    if (!TryGetPropertyIgnoreCase(part, "type", out var partTypeEl) || partTypeEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!string.Equals(partTypeEl.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryGetPropertyIgnoreCase(part, "text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        var t = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            chunks.Add(t.Trim());
                        }
                    }
                }
            }
        }

        return chunks.Count == 0 ? null : string.Join("\n", chunks);
    }

    private async Task<ResponsesApiResult> ExecuteResponsesRequestAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string requestId,
        string operation,
        object requestBody,
        int timeoutSeconds,
        CancellationToken ct,
        Func<string, Task> progress = null,
        DndLogPolicy logPolicy = null)
    {
        logPolicy ??= ResolveDndLogPolicy(context);
        var endpoint = ResolveResponsesEndpoint(context, channelState);
        var apiKey = ResolveResponsesApiKey(context, channelState);
        var opLabel = DescribeResponsesOperation(operation);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ResponsesApiResult(false, "Missing OpenAI API key.", 0, null, null, new List<ResponsesToolCall>(), null);
        }

        var requestJson = JsonSerializer.Serialize(requestBody);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Information, $"{opLabel}: endpoint={endpoint} timeout={timeoutSeconds}s bodyLen={requestJson.Length}", progress);
            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Debug, $"{opLabel}: request-json={NormalizeJsonForTrace(requestJson)}", progress);

            using var resp = await ResponsesHttpClient.SendAsync(req, timeoutCts.Token);
            var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();
            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Debug, $"{opLabel}: response-json={NormalizeJsonForTrace(raw)}", progress);

            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractResponsesError(raw) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}".Trim();
                await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Error, $"{opLabel}: http={(int)resp.StatusCode} ms={sw.ElapsedMilliseconds} err={err}", progress);
                return new ResponsesApiResult(false, err, (int)sw.ElapsedMilliseconds, null, null, new List<ResponsesToolCall>(), raw);
            }

            if (!TryParseJsonElement(raw, out var root))
            {
                await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Error, $"{opLabel}: ms={sw.ElapsedMilliseconds} parse=invalid-json", progress);
                return new ResponsesApiResult(false, "Invalid JSON response from OpenAI Responses API.", (int)sw.ElapsedMilliseconds, null, null, new List<ResponsesToolCall>(), raw);
            }

            var responseId = TryGetPropertyIgnoreCase(root, "id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            var toolCalls = ExtractResponsesToolCalls(root);
            var outputText = ExtractResponsesOutputText(root);
            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Information, $"{opLabel}: ms={sw.ElapsedMilliseconds} responseId={responseId} toolCalls={toolCalls.Count} textLen={(outputText?.Length ?? 0)}", progress);
            return new ResponsesApiResult(true, null, (int)sw.ElapsedMilliseconds, responseId, outputText, toolCalls, raw);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var err = ct.IsCancellationRequested ? "Canceled." : $"Timed out after {timeoutSeconds}s.";
            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Error, $"{opLabel}: ms={sw.ElapsedMilliseconds} timeout=true", progress);
            return new ResponsesApiResult(false, err, (int)sw.ElapsedMilliseconds, null, null, new List<ResponsesToolCall>(), null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await EmitResponsesTraceAsync(requestId, operation, logPolicy, DndLogLevel.Error, $"{opLabel}: ms={sw.ElapsedMilliseconds} ex={ex.GetType().Name} msg={ex.Message}", progress);
            return new ResponsesApiResult(false, $"{ex.GetType().Name}: {ex.Message}", (int)sw.ElapsedMilliseconds, null, null, new List<ResponsesToolCall>(), null);
        }
    }

    private static Dictionary<string, object> BuildResponsesFunctionTool(string name, string description, Dictionary<string, object> parameters, bool strict = true)
    {
        // Responses strict tool schemas require object nodes to explicitly set additionalProperties=false.
        // Normalize nested schema nodes so hand-authored schemas don't fail validation.
        if (strict)
        {
            NormalizeStrictSchema(parameters);
        }
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters,
            ["strict"] = strict
        };
    }

    private static void NormalizeStrictSchema(object schemaNode)
    {
        if (schemaNode is IDictionary<string, object> map)
        {
            var isObjectSchema = false;
            IDictionary<string, object> propsMap = null;
            if (TryGetMapValueIgnoreCase(map, "type", out var typeObj) &&
                typeObj is string typeStr &&
                string.Equals(typeStr, "object", StringComparison.OrdinalIgnoreCase))
            {
                isObjectSchema = true;
            }

            if (TryGetMapValueIgnoreCase(map, "properties", out var propsObj) &&
                propsObj is IDictionary<string, object> existingProps)
            {
                isObjectSchema = true;
                propsMap = existingProps;
                foreach (var property in propsMap.Values)
                {
                    NormalizeStrictSchema(property);
                }
            }

            if (isObjectSchema)
            {
                propsMap ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                map["properties"] = propsMap;

                // Strict function schemas expect `required` to exactly mirror property keys.
                // Derive it from `properties` to avoid drift between hand-edited definitions and validator rules.
                map["required"] = propsMap.Keys.ToArray();
                map["additionalProperties"] = false;
            }

            if (TryGetMapValueIgnoreCase(map, "items", out var itemsObj))
            {
                NormalizeStrictSchema(itemsObj);
            }

            if (TryGetMapValueIgnoreCase(map, "oneOf", out var oneOf))
            {
                NormalizeStrictSchema(oneOf);
            }

            if (TryGetMapValueIgnoreCase(map, "anyOf", out var anyOf))
            {
                NormalizeStrictSchema(anyOf);
            }

            if (TryGetMapValueIgnoreCase(map, "allOf", out var allOf))
            {
                NormalizeStrictSchema(allOf);
            }

            return;
        }

        if (schemaNode is IEnumerable<object> list)
        {
            foreach (var item in list)
            {
                NormalizeStrictSchema(item);
            }
        }
    }

    private static bool TryGetMapValueIgnoreCase(IDictionary<string, object> map, string key, out object value)
    {
        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static List<Dictionary<string, object>> BuildResponsesInput(string systemPrompt, string userPrompt)
        => new()
        {
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["role"] = "system",
                ["content"] = new List<Dictionary<string, object>>
                {
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "input_text",
                        ["text"] = systemPrompt
                    }
                }
            },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["role"] = "user",
                ["content"] = new List<Dictionary<string, object>>
                {
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "input_text",
                        ["text"] = userPrompt
                    }
                }
            }
        };

    private static string DescribeResponsesOperation(string operation)
    {
        var raw = operation?.Trim() ?? string.Empty;
        var colon = raw.IndexOf(':');
        var key = (colon >= 0 ? raw[..colon] : raw).Trim().ToLowerInvariant();
        return key switch
        {
            "campaign-stage1" => "Stage 1 - Campaign Foundation",
            "campaign-stage1-recovery" => "Stage 1 Recovery - Encounter Templates",
            "campaign-stage2" => "Stage 2 - Party and Sheets",
            "draft-update" => "Draft Revision",
            "character-create" => "Sheet Generation",
            _ => string.IsNullOrWhiteSpace(operation) ? "Responses Stage" : operation.Trim()
        };
    }

    private static DndLogPolicy ResolveDndLogPolicy(DiscordModuleContext context)
    {
        var cfg = context?.Configuration;
        var minLevelRaw = cfg?["Discord:Modules:Dnd:Logging:MinLevel"];
        var consoleMinRaw = cfg?["Discord:Modules:Dnd:Logging:ConsoleMinLevel"];
        var discordMinRaw = cfg?["Discord:Modules:Dnd:Logging:DiscordMinLevel"];

        var globalMin = ParseDndLogLevel(minLevelRaw, DndLogLevel.Information);
        var consoleMin = ParseDndLogLevel(consoleMinRaw, globalMin);
        // Keep Discord chat focused on normal dialogue by default; opt in via config if needed.
        var discordMin = ParseDndLogLevel(discordMinRaw, DndLogLevel.None);
        return new DndLogPolicy(consoleMin, discordMin);
    }

    private static DndLogLevel ParseDndLogLevel(string raw, DndLogLevel defaultLevel)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultLevel;
        }

        var s = raw.Trim().ToLowerInvariant();
        return s switch
        {
            "none" or "off" or "silent" => DndLogLevel.None,
            "error" => DndLogLevel.Error,
            "warn" or "warning" => DndLogLevel.Warning,
            "info" or "information" => DndLogLevel.Information,
            "debug" => DndLogLevel.Debug,
            "trace" => DndLogLevel.Trace,
            _ => defaultLevel
        };
    }

    private static bool ShouldLog(DndLogLevel messageLevel, DndLogLevel minLevel)
        => minLevel != DndLogLevel.None && messageLevel <= minLevel;

    private static async Task EmitResponsesTraceAsync(
        string requestId,
        string operation,
        DndLogPolicy policy,
        DndLogLevel level,
        string message,
        Func<string, Task> progress = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        policy ??= new DndLogPolicy(DndLogLevel.Information, DndLogLevel.Information);
        var text = message.Trim();
        if (ShouldLog(level, policy.ConsoleMinLevel))
        {
            Console.WriteLine($"[dnd] responses[{requestId}]: op={operation} [{level}] {text}");
        }

        if (progress == null || !ShouldLog(level, policy.DiscordMinLevel))
        {
            return;
        }

        try
        {
            await progress(text);
        }
        catch
        {
            // Trace messaging should not break generation.
        }
    }

    private async Task<ResponsesLoopResult> RunResponsesToolLoopAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string requestId,
        string operation,
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<Dictionary<string, object>> tools,
        Func<ResponsesToolCall, string> onToolCall,
        Func<bool> isComplete,
        int maxOutputTokens,
        int timeoutSeconds,
        CancellationToken ct,
        bool allowNoToolCallsTerminal = false,
        Func<string, Task> progress = null)
    {
        var logPolicy = ResolveDndLogPolicy(context);
        Task EmitProgressAsync(string message, DndLogLevel level = DndLogLevel.Information)
            => EmitResponsesTraceAsync(requestId, operation, logPolicy, level, message, progress);

        var operationLabel = DescribeResponsesOperation(operation);

        string previousResponseId = null;
        object input = BuildResponsesInput(systemPrompt, userPrompt);
        var totalMs = 0;
        var lastToolCallNames = string.Empty;
        var lastToolCallDetail = string.Empty;
        var lastOutputText = string.Empty;

        for (var round = 1; round <= ResponsesLoopMaxRounds; round++)
        {
            await EmitProgressAsync($"{operationLabel}: round {round}/{ResponsesLoopMaxRounds} started.");

            var body = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["model"] = model,
                ["input"] = input,
                ["tools"] = tools,
                ["tool_choice"] = "auto",
                ["parallel_tool_calls"] = false,
                ["max_output_tokens"] = Math.Max(200, maxOutputTokens),
                ["reasoning"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["effort"] = "low"
                },
                ["text"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["verbosity"] = "low"
                }
            };
            if (!string.IsNullOrWhiteSpace(previousResponseId))
            {
                body["previous_response_id"] = previousResponseId;
            }

            var resp = await ExecuteResponsesRequestAsync(context, channelState, requestId, $"{operation}:round{round}", body, timeoutSeconds, ct, progress, logPolicy);
            totalMs += resp.HttpMs;
            lastOutputText = resp.OutputText;
            if (!resp.Successful)
            {
                await EmitProgressAsync($"{operationLabel}: round {round} failed ({TrimToLimit(resp.Error, 220)}).", DndLogLevel.Error);
                return new ResponsesLoopResult(false, resp.Error, totalMs, resp.OutputText, round);
            }

            if (resp.ToolCalls.Count == 0)
            {
                if (allowNoToolCallsTerminal || isComplete == null || isComplete())
                {
                    await EmitProgressAsync($"{operationLabel}: round {round} complete (no more tool calls).");
                    return new ResponsesLoopResult(true, null, totalMs, resp.OutputText, round);
                }

                var extra = string.IsNullOrWhiteSpace(resp.OutputText) ? string.Empty : $" output={TrimToLimit(resp.OutputText, 200)}";
                await EmitProgressAsync($"{operationLabel}: round {round} returned no tool calls before completion.", DndLogLevel.Warning);
                return new ResponsesLoopResult(false, $"No tool calls returned before completion.{extra}", totalMs, resp.OutputText, round);
            }

            var callNames = string.Join(", ", resp.ToolCalls.Select(c => c?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(6));
            lastToolCallNames = callNames;
            await EmitProgressAsync($"{operationLabel}: round {round} received {resp.ToolCalls.Count} tool call(s){(string.IsNullOrWhiteSpace(callNames) ? string.Empty : $": {callNames}")}.");

            var outputs = new List<Dictionary<string, object>>();
            for (var i = 0; i < resp.ToolCalls.Count; i++)
            {
                var call = resp.ToolCalls[i];
                var callName = string.IsNullOrWhiteSpace(call?.Name) ? "(unnamed)" : call.Name.Trim();
                var argsPreview = NormalizeJsonForTrace(call?.ArgumentsJson);
                await EmitResponsesTraceAsync(
                    requestId,
                    $"{operation}:round{round}",
                    logPolicy,
                    DndLogLevel.Debug,
                    $"{operationLabel}: round {round} toolcall[{i + 1}/{resp.ToolCalls.Count}] name={callName} callId={call?.CallId} args={argsPreview}",
                    progress);

                if (call?.IsIncomplete == true)
                {
                    var preview = TrimToLimit(argsPreview, 240);
                    var err =
                        $"Incomplete tool call arguments for '{callName}' (status={call.Status}). " +
                        $"The model likely hit max_output_tokens before finishing valid JSON. args={preview}";
                    await EmitProgressAsync($"{operationLabel}: round {round} failed ({TrimToLimit(err, 260)}).", DndLogLevel.Error);
                    return new ResponsesLoopResult(false, err, totalMs, resp.OutputText, round);
                }

                if (string.IsNullOrWhiteSpace(call.CallId))
                {
                    await EmitProgressAsync($"{operationLabel}: round {round} failed (tool call missing call_id).", DndLogLevel.Error);
                    return new ResponsesLoopResult(false, $"Tool call '{call.Name}' missing call_id.", totalMs, resp.OutputText, round);
                }

                string outText;
                try
                {
                    outText = onToolCall?.Invoke(call) ?? "{\"ok\":true}";
                }
                catch (Exception ex)
                {
                    outText = JsonSerializer.Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
                }
                var outPreview = NormalizeJsonForTrace(outText);
                lastToolCallDetail = $"{callName} args={argsPreview} output={outPreview}";
                await EmitResponsesTraceAsync(
                    requestId,
                    $"{operation}:round{round}",
                    logPolicy,
                    DndLogLevel.Debug,
                    $"{operationLabel}: round {round} toolout[{i + 1}/{resp.ToolCalls.Count}] name={callName} callId={call.CallId} output={outPreview}",
                    progress);

                outputs.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call.CallId,
                    ["output"] = string.IsNullOrWhiteSpace(outText) ? "{\"ok\":true}" : outText
                });
            }

            if (isComplete != null && isComplete())
            {
                await EmitProgressAsync($"{operationLabel}: round {round} completion criteria satisfied.");
                return new ResponsesLoopResult(true, null, totalMs, resp.OutputText, round);
            }

            previousResponseId = resp.ResponseId;
            input = outputs;
            await EmitProgressAsync($"{operationLabel}: round {round} tool outputs submitted, continuing.");
        }

        var details = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(lastToolCallNames))
        {
            details.Add($"last tool calls: {lastToolCallNames}");
        }
        if (!string.IsNullOrWhiteSpace(lastToolCallDetail))
        {
            details.Add($"last tool detail: {TrimToLimit(lastToolCallDetail, 260)}");
        }
        if (!string.IsNullOrWhiteSpace(lastOutputText))
        {
            details.Add($"last output: {TrimToLimit(lastOutputText, 180)}");
        }

        var reason =
            $"Exceeded max rounds ({ResponsesLoopMaxRounds}); model did not reach completion criteria." +
            (details.Count == 0 ? string.Empty : $" ({string.Join(" | ", details)})");
        await EmitProgressAsync($"{operationLabel}: failed ({TrimToLimit(reason, 260)}).", DndLogLevel.Error);
        return new ResponsesLoopResult(false, reason, totalMs, lastOutputText, ResponsesLoopMaxRounds);
    }

    private List<EncounterTemplateDto> NormalizeEncounterTemplates(IEnumerable<EncounterTemplateDto> encounters)
    {
        var normalized = new List<EncounterTemplateDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in encounters ?? Enumerable.Empty<EncounterTemplateDto>())
        {
            if (src == null)
            {
                continue;
            }

            var templateId = SlugifySegment(src.TemplateId);
            if (string.IsNullOrWhiteSpace(templateId))
            {
                templateId = SlugifySegment(src.Name);
            }
            if (string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }
            if (!seen.Add(templateId))
            {
                continue;
            }

            var boss = src.Boss ?? new ActorDto();
            boss.Id = SlugifySegment(boss.Id);
            if (string.IsNullOrWhiteSpace(boss.Id))
            {
                boss.Id = "boss";
            }
            boss.Name = string.IsNullOrWhiteSpace(boss.Name) ? "Boss" : boss.Name.Trim();
            boss.Description ??= string.Empty;
            boss.MaxHp = Math.Clamp(boss.MaxHp <= 0 ? 40 : boss.MaxHp, 1, 250);
            boss.MaxMp = Math.Clamp(boss.MaxMp, 0, 100);
            boss.Stats = ClampStats(boss.Stats);

            var adds = new List<ActorDto>();
            foreach (var add in (src.Adds ?? new List<ActorDto>()).Take(6))
            {
                if (add == null)
                {
                    continue;
                }
                var id = SlugifySegment(add.Id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = SlugifySegment(add.Name);
                }
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                adds.Add(new ActorDto
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(add.Name) ? id : add.Name.Trim(),
                    Description = add.Description ?? string.Empty,
                    MaxHp = Math.Clamp(add.MaxHp <= 0 ? 12 : add.MaxHp, 1, 250),
                    MaxMp = Math.Clamp(add.MaxMp, 0, 100),
                    Stats = ClampStats(add.Stats)
                });
            }

            normalized.Add(new EncounterTemplateDto
            {
                TemplateId = templateId,
                Name = string.IsNullOrWhiteSpace(src.Name) ? templateId : src.Name.Trim(),
                Scene = src.Scene ?? string.Empty,
                Rewards = src.Rewards ?? string.Empty,
                Boss = boss,
                Adds = adds
            });

            if (normalized.Count >= 12)
            {
                break;
            }
        }

        return normalized;
    }

    private static HashSet<string> ParseAllowedPcActorIds(string pcRosterContext)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pcRosterContext))
        {
            return set;
        }

        foreach (Match m in Regex.Matches(pcRosterContext, @"u:\d+", RegexOptions.IgnoreCase))
        {
            if (m.Success && !string.IsNullOrWhiteSpace(m.Value))
            {
                set.Add(m.Value.Trim());
            }
        }
        return set;
    }

    private static string BuildCampaignStageOneSystemPrompt(bool strict)
        => strict
            ? "You are a GM prep agent in DRAFT mode. Call tools only. Follow this exact order: 1) call builder_set_campaign_markdown exactly once, 2) call builder_add_encounter_templates with 3-8 encounters, 3) call builder_finalize_package. Do not call builder_set_campaign_markdown again after step 1 unless the tool explicitly returned an error. Keep campaignMarkdown compact and concise."
            : "You are a GM prep agent in DRAFT mode. Use tools to draft campaign markdown and encounter templates. Call builder_set_campaign_markdown first, then builder_add_encounter_templates (3-8 encounters), then builder_finalize_package. Avoid repeated markdown tool calls. Keep campaignMarkdown compact.";

    private static string BuildCampaignStageOneUserPrompt(string campaignName, string prompt, string pcRosterContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Stage 1 of 2: build campaign story + encounters only.");
        sb.AppendLine("Use tools in order: set markdown once, add 3-8 encounter templates, finalize.");
        sb.AppendLine("Do not call builder_set_campaign_markdown repeatedly.");
        sb.AppendLine("Keep campaignMarkdown concise (target <= 4000 chars, compact bullets/headings).");
        sb.AppendLine("Do not create NPC/PC sheets in this stage.");
        sb.AppendLine();
        sb.AppendLine($"Campaign name: {campaignName}");
        if (!string.IsNullOrWhiteSpace(pcRosterContext))
        {
            sb.AppendLine("PC roster context:");
            sb.AppendLine(pcRosterContext.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("Prompt:");
        sb.AppendLine(prompt ?? string.Empty);
        return sb.ToString().Trim();
    }

    private static string BuildCampaignStageTwoSystemPrompt(bool strict)
        => strict
            ? "You are a GM prep agent in DRAFT mode. Call tools only. Build NPC/PC sheet specs and unassigned PCs, then call builder_finalize_package. Keep outputs compact: each profileMarkdown <= 600 chars; do not emit long narrative blocks."
            : "You are a GM prep agent in DRAFT mode. Use tools to build party sheet specs and unassigned PCs, then finalize. Keep outputs compact (profileMarkdown <= 600 chars).";

    private static string BuildCampaignStageTwoUserPrompt(
        string campaignName,
        string prompt,
        string pcRosterContext,
        string campaignMarkdown,
        IReadOnlyList<EncounterTemplateDto> encounters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Stage 2 of 2: build party-related artifacts only.");
        sb.AppendLine("Use tools to add NPC/PC sheet specs and unassigned PCs.");
        sb.AppendLine("Do not rewrite campaign markdown or encounters in this stage.");
        sb.AppendLine("Keep each profileMarkdown concise (target <= 600 chars).");
        sb.AppendLine();
        sb.AppendLine($"Campaign name: {campaignName}");
        sb.AppendLine("Draft markdown excerpt:");
        sb.AppendLine(TrimToLimit(campaignMarkdown ?? string.Empty, 1200));
        sb.AppendLine();
        sb.AppendLine("Encounter template ids:");
        sb.AppendLine(string.Join(", ", (encounters ?? Array.Empty<EncounterTemplateDto>()).Select(e => e?.TemplateId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(pcRosterContext))
        {
            sb.AppendLine("PC roster context (only these actorIds may be used for PC sheets):");
            sb.AppendLine(pcRosterContext.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("Prompt:");
        sb.AppendLine(prompt ?? string.Empty);
        return sb.ToString().Trim();
    }

    private async Task<CampaignGenerateResult> GenerateCampaignPackageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string requestId,
        string campaignName,
        string prompt,
        string pcRosterContext,
        CancellationToken ct,
        Func<string, Task> progress = null)
    {
        var model = ResolveCampaignBootstrapModel(context, channelState);
        var sw = Stopwatch.StartNew();

        async Task<(CampaignCreateResponseDto Package, string Error)> AttemptAsync(bool strict)
        {
            var stage1 = new CampaignStageOneAccumulator();
            var stage1Tools = new List<Dictionary<string, object>>
            {
                BuildResponsesFunctionTool(
                    "builder_set_campaign_markdown",
                    "Set or replace the campaign markdown draft.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["campaignMarkdown"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "string" }
                        },
                        ["required"] = new[] { "campaignMarkdown" }
                    }),
                BuildResponsesFunctionTool(
                    "builder_add_encounter_templates",
                    "Add encounter template definitions.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["encounters"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = true
                                }
                            }
                        },
                        ["required"] = new[] { "encounters" }
                    },
                    strict: false),
                BuildResponsesFunctionTool(
                    "builder_finalize_package",
                    "Signal that stage output is complete.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    })
            };

            string StageOneToolHandler(ResponsesToolCall call)
            {
                if (!TryParseJsonElement(call.ArgumentsJson, out var args))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "Invalid arguments JSON." });
                }

                switch (call.Name.Trim().ToLowerInvariant())
                {
                    case "builder_set_campaign_markdown":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "campaignMarkdown", out var mdEl) || mdEl.ValueKind != JsonValueKind.String)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "campaignMarkdown missing." });
                        }
                        var markdown = mdEl.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace(markdown))
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "campaignMarkdown empty." });
                        }
                        stage1.CampaignMarkdownSetCalls++;
                        if (!string.IsNullOrWhiteSpace(stage1.CampaignMarkdown) && stage1.Encounters.Count == 0)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                ok = false,
                                error = "campaignMarkdown already set. Next call must be builder_add_encounter_templates with 3-8 encounters, then builder_finalize_package."
                            });
                        }
                        stage1.CampaignMarkdown = TrimToLimit(markdown, MaxCampaignChars);
                        return JsonSerializer.Serialize(new { ok = true, markdownLen = stage1.CampaignMarkdown.Length, setCalls = stage1.CampaignMarkdownSetCalls });
                    }
                    case "builder_add_encounter_templates":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "encounters", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "encounters array missing." });
                        }
                        List<EncounterTemplateDto> parsed;
                        try
                        {
                            parsed = JsonSerializer.Deserialize<List<EncounterTemplateDto>>(arrEl.GetRawText(), _jsonOptions) ?? new List<EncounterTemplateDto>();
                        }
                        catch (Exception ex)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = $"Encounter parse failed: {ex.Message}" });
                        }
                        stage1.Encounters.AddRange(parsed);
                        return JsonSerializer.Serialize(new { ok = true, added = parsed.Count, total = stage1.Encounters.Count });
                    }
                    case "builder_finalize_package":
                        stage1.Finalized = true;
                        return JsonSerializer.Serialize(new { ok = true, finalized = true });
                    default:
                        return JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool: {call.Name}" });
                }
            }

            var stage1Loop = await RunResponsesToolLoopAsync(
                context,
                channelState,
                requestId,
                "campaign-stage1",
                model,
                BuildCampaignStageOneSystemPrompt(strict),
                BuildCampaignStageOneUserPrompt(campaignName, prompt, pcRosterContext),
                stage1Tools,
                StageOneToolHandler,
                () => stage1.Finalized || (!string.IsNullOrWhiteSpace(stage1.CampaignMarkdown) && stage1.Encounters.Count > 0),
                maxOutputTokens: strict ? 2800 : 1600,
                timeoutSeconds: CampaignCreateTimeoutSeconds,
                ct: ct,
                progress: progress);
            if (!stage1Loop.Successful)
            {
                var canRecoverEncountersOnly =
                    !string.IsNullOrWhiteSpace(stage1.CampaignMarkdown) &&
                    stage1.Encounters.Count == 0;
                if (!canRecoverEncountersOnly)
                {
                    return (null, $"Stage 1 failed: {stage1Loop.Error}");
                }

                if (progress != null)
                {
                    await progress($"Stage 1 Recovery - Encounter Templates: started because the first pass stalled ({TrimToLimit(stage1Loop.Error, 180)}).");
                }

                var stage1RecoveryTools = new List<Dictionary<string, object>>
                {
                    BuildResponsesFunctionTool(
                        "builder_add_encounter_templates",
                        "Add encounter template definitions.",
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["encounters"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["type"] = "object",
                                        ["additionalProperties"] = true
                                    }
                                }
                            },
                            ["required"] = new[] { "encounters" }
                        },
                        strict: false),
                    BuildResponsesFunctionTool(
                        "builder_finalize_package",
                        "Signal that stage output is complete.",
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = true
                        })
                };

                var recoveryLoop = await RunResponsesToolLoopAsync(
                    context,
                    channelState,
                    requestId,
                    "campaign-stage1-recovery",
                    model,
                    "You are finishing stage 1. Campaign markdown is already locked and cannot be changed. Call builder_add_encounter_templates with 3-8 encounters, then call builder_finalize_package.",
                    $"Campaign name: {campaignName}\n\nCampaign markdown (do not rewrite):\n{TrimToLimit(stage1.CampaignMarkdown, 1400)}\n\nOriginal prompt:\n{prompt ?? string.Empty}",
                    stage1RecoveryTools,
                    StageOneToolHandler,
                    () => stage1.Finalized || stage1.Encounters.Count > 0,
                    maxOutputTokens: 1400,
                    timeoutSeconds: CampaignCreateTimeoutSeconds,
                    ct: ct,
                    progress: progress);
                if (!recoveryLoop.Successful)
                {
                    return (null, $"Stage 1 failed: {stage1Loop.Error}; encounter recovery failed: {recoveryLoop.Error}");
                }
            }

            var normalizedEncounters = NormalizeEncounterTemplates(stage1.Encounters);
            if (string.IsNullOrWhiteSpace(stage1.CampaignMarkdown))
            {
                return (null, "Stage 1 produced no campaign markdown.");
            }
            if (normalizedEncounters.Count == 0)
            {
                return (null, "Stage 1 produced no encounter templates.");
            }

            var stage2 = new CampaignStageTwoAccumulator();
            var allowedPcActorIds = ParseAllowedPcActorIds(pcRosterContext);
            var stage2Tools = new List<Dictionary<string, object>>
            {
                BuildResponsesFunctionTool(
                    "builder_add_npc_sheet_specs",
                    "Add NPC sheet specs to be turned into dnd_create_npc_sheet function calls.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["npcs"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = true
                                }
                            }
                        },
                        ["required"] = new[] { "npcs" }
                    },
                    strict: false),
                BuildResponsesFunctionTool(
                    "builder_add_pc_sheet_specs",
                    "Add PC sheet specs to be turned into dnd_create_pc_sheet function calls.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["pcs"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = true
                                }
                            }
                        },
                        ["required"] = new[] { "pcs" }
                    },
                    strict: false),
                BuildResponsesFunctionTool(
                    "builder_set_unassigned_pcs",
                    "Set or append unassigned PCs that could not be mapped to roster actorIds.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["unassignedPcs"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = true
                                }
                            }
                        },
                        ["required"] = new[] { "unassignedPcs" }
                    },
                    strict: false),
                BuildResponsesFunctionTool(
                    "builder_finalize_package",
                    "Signal that stage output is complete.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    })
            };

            void AddFunctionCall(string name, object argsObj)
            {
                stage2.FunctionCalls.Add(new DndLiteFunctionCallDto
                {
                    Name = name,
                    Arguments = JsonSerializer.SerializeToElement(argsObj)
                });
            }

            string StageTwoToolHandler(ResponsesToolCall call)
            {
                if (!TryParseJsonElement(call.ArgumentsJson, out var args))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "Invalid arguments JSON." });
                }

                switch (call.Name.Trim().ToLowerInvariant())
                {
                    case "builder_add_npc_sheet_specs":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "npcs", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "npcs array missing." });
                        }

                        List<DndCreateNpcSheetArgsDto> specs;
                        try
                        {
                            specs = JsonSerializer.Deserialize<List<DndCreateNpcSheetArgsDto>>(arrEl.GetRawText(), _jsonOptions) ?? new List<DndCreateNpcSheetArgsDto>();
                        }
                        catch (Exception ex)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = $"NPC parse failed: {ex.Message}" });
                        }

                        var added = 0;
                        foreach (var s in specs)
                        {
                            if (s == null)
                            {
                                continue;
                            }
                            var id = SlugifySegment(s.Id);
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                id = SlugifySegment(s.Name);
                            }
                            if (string.IsNullOrWhiteSpace(id) || !stage2.NpcIds.Add(id))
                            {
                                continue;
                            }

                            var normalized = new DndCreateNpcSheetArgsDto
                            {
                                Id = id,
                                Name = string.IsNullOrWhiteSpace(s.Name) ? id : s.Name.Trim(),
                                Concept = s.Concept?.Trim() ?? string.Empty,
                                ProfileMarkdown = s.ProfileMarkdown?.Trim() ?? string.Empty,
                                PersonalityNotes = s.PersonalityNotes?.Trim() ?? string.Empty,
                                MaxHp = Math.Clamp(s.MaxHp <= 0 ? 24 : s.MaxHp, 1, 250),
                                MaxMp = Math.Clamp(s.MaxMp, 0, 100),
                                Stats = ClampStats(s.Stats)
                            };
                            AddFunctionCall("dnd_create_npc_sheet", normalized);
                            added++;
                        }

                        return JsonSerializer.Serialize(new { ok = true, added, total = stage2.FunctionCalls.Count });
                    }
                    case "builder_add_pc_sheet_specs":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "pcs", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "pcs array missing." });
                        }

                        List<DndCreatePcSheetArgsDto> specs;
                        try
                        {
                            specs = JsonSerializer.Deserialize<List<DndCreatePcSheetArgsDto>>(arrEl.GetRawText(), _jsonOptions) ?? new List<DndCreatePcSheetArgsDto>();
                        }
                        catch (Exception ex)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = $"PC parse failed: {ex.Message}" });
                        }

                        var added = 0;
                        var unassignedAdded = 0;
                        foreach (var s in specs)
                        {
                            if (s == null)
                            {
                                continue;
                            }

                            var actorId = s.ActorId?.Trim() ?? string.Empty;
                            var actorAllowed = !string.IsNullOrWhiteSpace(actorId) &&
                                               TryParsePcActorId(actorId, out _) &&
                                               (allowedPcActorIds.Count == 0 || allowedPcActorIds.Contains(actorId));
                            if (!actorAllowed)
                            {
                                var unassignedName = string.IsNullOrWhiteSpace(s.Name) ? "unknown-pc" : s.Name.Trim();
                                if (stage2.UnassignedNames.Add(unassignedName))
                                {
                                    stage2.UnassignedPcs.Add(new UnassignedPcDto
                                    {
                                        Name = unassignedName,
                                        Concept = s.Concept?.Trim() ?? string.Empty
                                    });
                                    unassignedAdded++;
                                }
                                continue;
                            }

                            if (!stage2.PcActorIds.Add(actorId))
                            {
                                continue;
                            }

                            var normalized = new DndCreatePcSheetArgsDto
                            {
                                ActorId = actorId,
                                Name = string.IsNullOrWhiteSpace(s.Name) ? actorId : s.Name.Trim(),
                                Concept = s.Concept?.Trim() ?? string.Empty,
                                ProfileMarkdown = s.ProfileMarkdown?.Trim() ?? string.Empty,
                                MaxHp = Math.Clamp(s.MaxHp <= 0 ? 20 : s.MaxHp, 1, 250),
                                MaxMp = Math.Clamp(s.MaxMp, 0, 100),
                                Stats = ClampStats(s.Stats)
                            };
                            AddFunctionCall("dnd_create_pc_sheet", normalized);
                            added++;
                        }

                        return JsonSerializer.Serialize(new { ok = true, added, unassignedAdded, total = stage2.FunctionCalls.Count });
                    }
                    case "builder_set_unassigned_pcs":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "unassignedPcs", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "unassignedPcs array missing." });
                        }

                        List<UnassignedPcDto> parsed;
                        try
                        {
                            parsed = JsonSerializer.Deserialize<List<UnassignedPcDto>>(arrEl.GetRawText(), _jsonOptions) ?? new List<UnassignedPcDto>();
                        }
                        catch (Exception ex)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = $"Unassigned parse failed: {ex.Message}" });
                        }

                        var added = 0;
                        foreach (var p in parsed)
                        {
                            var name = p?.Name?.Trim();
                            if (string.IsNullOrWhiteSpace(name) || !stage2.UnassignedNames.Add(name))
                            {
                                continue;
                            }
                            stage2.UnassignedPcs.Add(new UnassignedPcDto { Name = name, Concept = p?.Concept?.Trim() ?? string.Empty });
                            added++;
                        }
                        return JsonSerializer.Serialize(new { ok = true, added, total = stage2.UnassignedPcs.Count });
                    }
                    case "builder_finalize_package":
                        stage2.Finalized = true;
                        return JsonSerializer.Serialize(new { ok = true, finalized = true });
                    default:
                        return JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool: {call.Name}" });
                }
            }

            var stage2Loop = await RunResponsesToolLoopAsync(
                context,
                channelState,
                requestId,
                "campaign-stage2",
                model,
                BuildCampaignStageTwoSystemPrompt(strict),
                BuildCampaignStageTwoUserPrompt(campaignName, prompt, pcRosterContext, stage1.CampaignMarkdown, normalizedEncounters),
                stage2Tools,
                StageTwoToolHandler,
                () => stage2.Finalized,
                maxOutputTokens: strict ? 2200 : 1400,
                timeoutSeconds: CampaignCreateTimeoutSeconds,
                ct: ct,
                allowNoToolCallsTerminal: true,
                progress: progress);
            if (!stage2Loop.Successful)
            {
                return (null, $"Stage 2 failed: {stage2Loop.Error}");
            }

            var package = new CampaignCreateResponseDto
            {
                CampaignMarkdown = TrimToLimit(stage1.CampaignMarkdown.Trim(), MaxCampaignChars),
                Encounters = normalizedEncounters,
                FunctionCalls = stage2.FunctionCalls,
                UnassignedPcs = stage2.UnassignedPcs
            };
            return (package, null);
        }

        var first = await AttemptAsync(strict: false);
        if (first.Package != null)
        {
            sw.Stop();
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: ok responses model={model} ms={sw.ElapsedMilliseconds} encounters={first.Package.Encounters.Count} fnCalls={first.Package.FunctionCalls.Count}");
            return new CampaignGenerateResult(first.Package, null, (int)sw.ElapsedMilliseconds, model);
        }

        Console.WriteLine($"[dnd] campaign-generate[{requestId}]: first attempt failed err={first.Error}; retrying strict");
        if (progress != null)
        {
            await progress($"Stage 1 - Campaign Foundation: retrying with stricter instructions ({TrimToLimit(first.Error, 200)}).");
        }
        var second = await AttemptAsync(strict: true);
        sw.Stop();
        if (second.Package != null)
        {
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: strict retry succeeded model={model} ms={sw.ElapsedMilliseconds}");
            return new CampaignGenerateResult(second.Package, null, (int)sw.ElapsedMilliseconds, model);
        }

        var err = string.IsNullOrWhiteSpace(second.Error) ? first.Error : second.Error;
        Console.WriteLine($"[dnd] campaign-generate[{requestId}]: failed model={model} ms={sw.ElapsedMilliseconds} err={err}");
        return new CampaignGenerateResult(null, err ?? "Responses campaign generation failed.", (int)sw.ElapsedMilliseconds, model);
    }

    private async Task<DraftUpdateGenerateResult> GenerateDraftUpdateMarkdownAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string campaignName,
        string existingMarkdown,
        string modificationPrompt,
        CancellationToken ct,
        Func<string, Task> progress = null)
    {
        var model = ResolveCampaignBootstrapModel(context, channelState);

        async Task<DraftUpdateGenerateResult> AttemptAsync(bool strict)
        {
            string updated = null;
            var finalized = false;
            var tools = new List<Dictionary<string, object>>
            {
                BuildResponsesFunctionTool(
                    "builder_set_campaign_markdown",
                    "Set the updated campaign markdown.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["campaignMarkdown"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "string" }
                        },
                        ["required"] = new[] { "campaignMarkdown" }
                    }),
                BuildResponsesFunctionTool(
                    "builder_finalize_package",
                    "Signal completion.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    })
            };

            string ToolHandler(ResponsesToolCall call)
            {
                if (!TryParseJsonElement(call.ArgumentsJson, out var args))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON args." });
                }

                switch (call.Name.Trim().ToLowerInvariant())
                {
                    case "builder_set_campaign_markdown":
                    {
                        if (!TryGetPropertyIgnoreCase(args, "campaignMarkdown", out var mdEl) || mdEl.ValueKind != JsonValueKind.String)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "campaignMarkdown missing." });
                        }
                        var md = mdEl.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace(md))
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = "campaignMarkdown empty." });
                        }
                        updated = TrimToLimit(md, MaxCampaignChars);
                        return JsonSerializer.Serialize(new { ok = true, markdownLen = updated.Length });
                    }
                    case "builder_finalize_package":
                        finalized = true;
                        return JsonSerializer.Serialize(new { ok = true, finalized = true });
                    default:
                        return JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool: {call.Name}" });
                }
            }

            var loop = await RunResponsesToolLoopAsync(
                context,
                channelState,
                Guid.NewGuid().ToString("n")[..8],
                "draft-update",
                model,
                strict
                    ? "You are a GM prep agent revising a campaign draft. Call tools only. You MUST call builder_set_campaign_markdown and then builder_finalize_package."
                    : "You are a GM prep agent revising a campaign draft. Use tools to set updated campaign markdown, then finalize.",
                BuildDraftUpdatePrompt(campaignName, existingMarkdown, modificationPrompt),
                tools,
                ToolHandler,
                () => finalized || !string.IsNullOrWhiteSpace(updated),
                maxOutputTokens: 1200,
                timeoutSeconds: 45,
                ct: ct,
                progress: progress);

            if (!loop.Successful)
            {
                return new DraftUpdateGenerateResult(false, loop.Error, null);
            }
            if (string.IsNullOrWhiteSpace(updated))
            {
                return new DraftUpdateGenerateResult(false, "No updated campaign markdown produced.", null);
            }

            return new DraftUpdateGenerateResult(true, null, updated);
        }

        var first = await AttemptAsync(false);
        if (first.Successful)
        {
            return first;
        }

        Console.WriteLine($"[dnd] draftupdate: first responses attempt failed err={first.Error}; retrying strict");
        if (progress != null)
        {
            await progress($"Draft Revision: retrying with stricter instructions ({TrimToLimit(first.Error, 200)}).");
        }
        return await AttemptAsync(true);
    }

    private async Task<CharacterCreateResponseDto> GenerateCharacterAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string name,
        string concept,
        string campaignMarkdown,
        CancellationToken ct,
        Func<string, Task> progress = null)
    {
        var model = ResolveCampaignBootstrapModel(context, channelState);
        var prompt = BuildCreateCharacterPrompt(name, concept, campaignMarkdown);

        async Task<CharacterCreateResponseDto> AttemptAsync(bool strict)
        {
            var acc = new CharacterSheetAccumulator();
            var tools = new List<Dictionary<string, object>>
            {
                BuildResponsesFunctionTool(
                    "builder_set_character_sheet",
                    "Set the generated character sheet fields.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["profileMarkdown"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "string" },
                            ["maxHp"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                            ["maxMp"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                            ["stats"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["properties"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["str"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                                    ["def"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                                    ["dex"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                                    ["spellPower"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" },
                                    ["luck"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" }
                                },
                                ["required"] = new[] { "str", "def", "dex", "spellPower", "luck" }
                            }
                        },
                        ["required"] = new[] { "profileMarkdown", "maxHp", "maxMp", "stats" }
                    }),
                BuildResponsesFunctionTool(
                    "builder_finalize_package",
                    "Signal completion.",
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    })
            };

            string ToolHandler(ResponsesToolCall call)
            {
                if (!TryParseJsonElement(call.ArgumentsJson, out var args))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON args." });
                }

                switch (call.Name.Trim().ToLowerInvariant())
                {
                    case "builder_set_character_sheet":
                    {
                        try
                        {
                            var parsed = JsonSerializer.Deserialize<CharacterCreateResponseDto>(args.GetRawText(), _jsonOptions);
                            if (parsed == null)
                            {
                                return JsonSerializer.Serialize(new { ok = false, error = "Sheet payload missing." });
                            }

                            parsed.ProfileMarkdown = parsed.ProfileMarkdown?.Trim() ?? string.Empty;
                            parsed.Stats = ClampStats(parsed.Stats);
                            parsed.MaxHp = Math.Clamp(parsed.MaxHp <= 0 ? 20 : parsed.MaxHp, 1, 200);
                            parsed.MaxMp = Math.Clamp(parsed.MaxMp, 0, 200);
                            acc.Sheet = parsed;
                            return JsonSerializer.Serialize(new { ok = true, hp = parsed.MaxHp, mp = parsed.MaxMp });
                        }
                        catch (Exception ex)
                        {
                            return JsonSerializer.Serialize(new { ok = false, error = $"Sheet parse failed: {ex.Message}" });
                        }
                    }
                    case "builder_finalize_package":
                        acc.Finalized = true;
                        return JsonSerializer.Serialize(new { ok = true, finalized = true });
                    default:
                        return JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool: {call.Name}" });
                }
            }

            var loop = await RunResponsesToolLoopAsync(
                context,
                channelState,
                Guid.NewGuid().ToString("n")[..8],
                "character-create",
                model,
                strict
                    ? "You are generating a simplified D&D character sheet. Call tools only. You MUST call builder_set_character_sheet and then builder_finalize_package."
                    : "You are generating a simplified D&D character sheet. Use tools to set the sheet, then finalize.",
                prompt,
                tools,
                ToolHandler,
                () => acc.Finalized || acc.Sheet != null,
                maxOutputTokens: 900,
                timeoutSeconds: 45,
                ct: ct,
                progress: progress);

            if (!loop.Successful)
            {
                Console.WriteLine($"[dnd] character-generate: loop failed err={loop.Error}");
                return null;
            }

            return acc.Sheet;
        }

        var first = await AttemptAsync(false);
        if (first != null)
        {
            return first;
        }

        Console.WriteLine("[dnd] character-generate: retrying strict");
        if (progress != null)
        {
            await progress("Sheet Generation: retrying with stricter instructions.");
        }
        return await AttemptAsync(true);
    }

    private async Task<string> GenerateNpcFlavorLineAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        DndLiteCampaignDocument campaign,
        DndLiteNpcProfile npc,
        DndEncounterSnapshot encounter,
        DndActorSnapshot target,
        CancellationToken ct)
    {
        if (context == null || channelState == null || npc == null || encounter == null)
        {
            return null;
        }

        var model = ResolveModel(context, channelState);

        var sb = new StringBuilder();
        sb.AppendLine("Return strict JSON only with key exactly: line");
        sb.AppendLine("line: a single in-character sentence (<= 200 chars) said or thought by the NPC during their action.");
        sb.AppendLine("No markdown. No code fences.");
        sb.AppendLine($"NPC name: {npc.Name}");
        sb.AppendLine($"NPC concept: {npc.Concept}");
        if (!string.IsNullOrWhiteSpace(npc.ProfileMarkdown))
        {
            sb.AppendLine("NPC profile excerpt:");
            sb.AppendLine(TrimToLimit(npc.ProfileMarkdown, 800));
        }
        if (!string.IsNullOrWhiteSpace(npc.PersonalityNotes))
        {
            sb.AppendLine("NPC personality notes:");
            sb.AppendLine(TrimToLimit(npc.PersonalityNotes, 400));
        }
        sb.AppendLine($"Target: {(target?.Name ?? target?.ActorId ?? "enemy")}");

        // Light encounter context: boss/add descriptions if available.
        var templateId = GuessTemplateId(encounter);
        if (!string.IsNullOrWhiteSpace(templateId) && campaign?.EncounterTemplates != null)
        {
            var t = campaign.EncounterTemplates.FirstOrDefault(x => x != null && string.Equals(x.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
            if (t != null)
            {
                if (!string.IsNullOrWhiteSpace(t.Scene))
                {
                    sb.AppendLine("Scene:");
                    sb.AppendLine(TrimToLimit(t.Scene, 400));
                }
                if (t.Boss != null && !string.IsNullOrWhiteSpace(t.Boss.Description))
                {
                    sb.AppendLine($"Boss description: {TrimToLimit(t.Boss.Description, 250)}");
                }
            }
        }

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(ChatCompletionRole.System,
                    "You are the game master. Return strict JSON only. No markdown. No code fences."),
                new(ChatCompletionRole.User, sb.ToString().Trim())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            return null;
        }

        var content = ExtractChatMessageText(response.Choices.FirstOrDefault()?.Message)?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<NpcFlavorResponseDto>(json, _jsonOptions);
            var line = parsed?.Line?.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            line = line.Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length > 220)
            {
                line = line[..220].Trim();
            }
            return line;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCreateCampaignPrompt(string campaignName, string prompt, string pcRosterContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Return a single JSON object with keys exactly: campaignMarkdown, encounters, functionCalls, unassignedPcs.");
        sb.AppendLine("campaignMarkdown: short markdown (max ~1200 chars) describing the campaign premise, tone, and 3-5 bullet hooks.");
        sb.AppendLine("encounters: array of 3-5 encounter templates (keep scenes short).");
        sb.AppendLine("functionCalls: array of objects: {\"name\":\"...\",\"arguments\":{...}}.");
        sb.AppendLine("unassignedPcs: array of PCs mentioned in the prompt that cannot be mapped to the provided PC roster; each: {\"name\":\"...\",\"concept\":\"...\"}.");
        sb.AppendLine("Hard limit: keep the entire JSON response under ~6000 characters.");
        sb.AppendLine("Output rule: the FIRST character of your response must be '{' and the LAST character must be '}'.");
        sb.AppendLine();
        sb.AppendLine("You are generating a NEW version of the campaign. Treat any existing campaign content as disposable and overwrite it.");
        sb.AppendLine("This tool catalogs replayable campaigns (campaign.json is party-free).");
        sb.AppendLine("If the prompt names party members or important NPCs, include functionCalls to create NPC sheets so the host can populate the active run's party roster.");
        sb.AppendLine("If the prompt contains Discord user actorIds from the provided roster context, you may include dnd_create_pc_sheet calls for those users.");
        sb.AppendLine("Keep functionCalls minimal (only for party/NPCs that matter).");
        sb.AppendLine();
        sb.AppendLine("Function definitions:");
        sb.AppendLine("1) dnd_create_npc_sheet(arguments)");
        sb.AppendLine("arguments schema:");
        sb.AppendLine("{\"id\":\"slug\",\"name\":\"string\",\"concept\":\"string\",\"profileMarkdown\":\"string\",\"personalityNotes\":\"string\",\"maxHp\":10,\"maxMp\":0,\"stats\":{\"str\":10,\"def\":10,\"dex\":10,\"spellPower\":10,\"luck\":10}}");
        sb.AppendLine("2) dnd_create_pc_sheet(arguments)");
        sb.AppendLine("arguments schema:");
        sb.AppendLine("{\"actorId\":\"u:<discordUserId>\",\"name\":\"string\",\"concept\":\"string\",\"profileMarkdown\":\"string\",\"maxHp\":10,\"maxMp\":0,\"stats\":{\"str\":10,\"def\":10,\"dex\":10,\"spellPower\":10,\"luck\":10}}");
        sb.AppendLine();
        sb.AppendLine("PC mapping rules:");
        sb.AppendLine("- Only create dnd_create_pc_sheet calls for actorId values present in the provided PC roster.");
        sb.AppendLine("- If the prompt describes PCs not present in roster, list them under unassignedPcs instead.");
        sb.AppendLine("- NPC ids must be short slugs (no 'npc:' prefix).");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(pcRosterContext))
        {
            sb.AppendLine("PC roster context (only these actorIds may be used for PCs):");
            sb.AppendLine(pcRosterContext.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Encounter template schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"templateId\": \"string\",");
        sb.AppendLine("  \"name\": \"string\",");
        sb.AppendLine("  \"scene\": \"string\",");
        sb.AppendLine("  \"rewards\": \"string\",");
        sb.AppendLine("  \"boss\": {\"id\":\"string\",\"name\":\"string\",\"description\":\"string\",\"maxHp\":40,\"maxMp\":10,\"stats\":{\"str\":12,\"def\":12,\"dex\":12,\"spellPower\":12,\"luck\":10}},");
        sb.AppendLine("  \"adds\": [{\"id\":\"string\",\"name\":\"string\",\"description\":\"string\",\"maxHp\":12,\"maxMp\":0,\"stats\":{\"str\":10,\"def\":10,\"dex\":10,\"spellPower\":10,\"luck\":10}}]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Constraints:");
        sb.AppendLine("- Keep stats simple and balanced for a small party.");
        sb.AppendLine("- maxHp: 1-250, maxMp: 0-100");
        sb.AppendLine("- stats range: 6-20");
        sb.AppendLine("- adds: 0-6");
        sb.AppendLine("- All ids should be short slugs.");
        sb.AppendLine();
        sb.AppendLine($"Campaign name: {campaignName}");
        sb.AppendLine("Prompt:");
        sb.AppendLine(prompt ?? string.Empty);
        return sb.ToString().Trim();
    }

    private static string BuildGenerationCampaignContext(string campaignMarkdown)
    {
        if (string.IsNullOrWhiteSpace(campaignMarkdown))
        {
            return null;
        }

        // Keep generation inputs focused; sheets don't need full campaign prose.
        return TrimToLimit(campaignMarkdown.Trim(), 400);
    }

    private static string BuildCreateCharacterPrompt(string name, string concept, string campaignMarkdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Return a single JSON object with keys exactly: profileMarkdown, maxHp, maxMp, stats.");
        sb.AppendLine("profileMarkdown: short markdown profile (5-12 lines).");
        sb.AppendLine("stats schema: {\"str\":int,\"def\":int,\"dex\":int,\"spellPower\":int,\"luck\":int}");
        sb.AppendLine("Constraints: maxHp 10-60, maxMp 0-30, stats 6-18.");
        sb.AppendLine($"Name: {name}");
        sb.AppendLine($"Concept: {concept}");
        if (!string.IsNullOrWhiteSpace(campaignMarkdown))
        {
            sb.AppendLine("Campaign context (excerpt):");
            sb.AppendLine(TrimToLimit(campaignMarkdown, 400));
        }
        return sb.ToString().Trim();
    }

    private async Task<DndLiteChannelState> GetOrLoadStateAsync(InstructionGPT.ChannelState channelState, CancellationToken ct)
    {
        if (_stateByChannel.TryGetValue(channelState.ChannelId, out var cached))
        {
            NormalizeState(cached);
            return cached;
        }

        var path = ResolveStatePath(channelState);
        DndLiteChannelState loaded = null;
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                loaded = JsonSerializer.Deserialize<DndLiteChannelState>(json, _jsonOptions);
            }
        }

        loaded ??= new DndLiteChannelState();
        NormalizeState(loaded);
        _stateByChannel[channelState.ChannelId] = loaded;
        return loaded;
    }

    private async Task SaveStateAsync(InstructionGPT.ChannelState channelState, DndLiteChannelState st, CancellationToken ct)
    {
        NormalizeState(st);
        var path = ResolveStatePath(channelState);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(st, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
        _stateByChannel[channelState.ChannelId] = st;
    }

    private async Task<DndLiteCampaignDocument> LoadCampaignAsync(InstructionGPT.ChannelState channelState, string name, CancellationToken ct)
    {
        var campaignName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        var path = ResolveCampaignPath(channelState, campaignName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        DndLiteCampaignCatalogDocument catalog = null;
        DndLiteCampaignDocument legacyFull = null;
        try
        {
            catalog = JsonSerializer.Deserialize<DndLiteCampaignCatalogDocument>(json, _jsonOptions);
        }
        catch
        {
            catalog = null;
        }

        // Back-compat: older campaign.json stored runtime state inline. Attempt to migrate it into run.json.
        if (catalog == null)
        {
            try
            {
                legacyFull = JsonSerializer.Deserialize<DndLiteCampaignDocument>(json, _jsonOptions);
                if (legacyFull != null)
                {
                    catalog = new DndLiteCampaignCatalogDocument
                    {
                        CampaignName = legacyFull.CampaignName,
                        CampaignMarkdown = legacyFull.CampaignMarkdown ?? string.Empty,
                        UpdatedUtc = legacyFull.UpdatedUtc,
                        EncounterTemplates = legacyFull.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>()
                    };
                }
            }
            catch
            {
                catalog = null;
            }
        }

        if (catalog == null)
        {
            return null;
        }

        catalog.CampaignName = string.IsNullOrWhiteSpace(catalog.CampaignName) ? campaignName : catalog.CampaignName.Trim();
        catalog.CampaignMarkdown ??= string.Empty;
        catalog.EncounterTemplates ??= new List<DndLiteEncounterTemplateDocument>();

        // Load per-run (runtime) state.
        var runPath = ResolveRunPath(channelState, catalog.CampaignName);
        DndLiteCampaignRunDocument run = null;
        if (File.Exists(runPath))
        {
            try
            {
                var runJson = await File.ReadAllTextAsync(runPath, ct);
                if (!string.IsNullOrWhiteSpace(runJson))
                {
                    run = JsonSerializer.Deserialize<DndLiteCampaignRunDocument>(runJson, _jsonOptions);
                }
            }
            catch
            {
                run = null;
            }
        }

        if (run == null && legacyFull != null && (legacyFull.RunnerState != null || HasLiveRuntimeState(legacyFull.LiveRuntime)))
        {
            run = new DndLiteCampaignRunDocument
            {
                CampaignName = catalog.CampaignName,
                UpdatedUtc = DateTime.UtcNow,
                RunnerState = legacyFull.RunnerState,
                LiveRuntime = legacyFull.LiveRuntime ?? new DndLiteEncounterLiveRuntime()
            };
            await SaveRunStateAsync(channelState, catalog.CampaignName, run, ct);

            // Rewrite campaign.json as catalog-only to finish migration.
            await SaveCampaignCatalogAsync(channelState, catalog, ct);
        }

        var doc = new DndLiteCampaignDocument
        {
            CampaignName = catalog.CampaignName,
            CampaignMarkdown = catalog.CampaignMarkdown,
            UpdatedUtc = catalog.UpdatedUtc,
            EncounterTemplates = catalog.EncounterTemplates,
            RunnerState = run?.RunnerState,
            LiveRuntime = run?.LiveRuntime ?? new DndLiteEncounterLiveRuntime(),
            RunUpdatedUtc = run?.UpdatedUtc ?? default
        };

        var changed = await NormalizeCampaignRunnerStateFromProfilesAsync(channelState, doc, ct);
        if (changed)
        {
            await SaveCampaignAsync(channelState, doc, ct);
        }

        return doc;
    }

    private async Task SaveCampaignAsync(InstructionGPT.ChannelState channelState, DndLiteCampaignDocument doc, CancellationToken ct)
    {
        if (doc == null || string.IsNullOrWhiteSpace(doc.CampaignName))
        {
            return;
        }

        doc.CampaignName = doc.CampaignName.Trim();
        doc.CampaignMarkdown ??= string.Empty;
        doc.EncounterTemplates ??= new List<DndLiteEncounterTemplateDocument>();
        doc.LiveRuntime ??= new DndLiteEncounterLiveRuntime();

        // Persist catalog without runtime state.
        var catalog = new DndLiteCampaignCatalogDocument
        {
            CampaignName = doc.CampaignName,
            CampaignMarkdown = doc.CampaignMarkdown,
            UpdatedUtc = doc.UpdatedUtc,
            EncounterTemplates = doc.EncounterTemplates
        };
        await SaveCampaignCatalogAsync(channelState, catalog, ct);

        // Persist run state separately (keeps catalog replayable and party-free).
        if (doc.RunnerState != null || HasLiveRuntimeState(doc.LiveRuntime))
        {
            var run = new DndLiteCampaignRunDocument
            {
                CampaignName = doc.CampaignName,
                UpdatedUtc = DateTime.UtcNow,
                RunnerState = doc.RunnerState,
                LiveRuntime = doc.LiveRuntime ?? new DndLiteEncounterLiveRuntime()
            };
            await SaveRunStateAsync(channelState, doc.CampaignName, run, ct);
            doc.RunUpdatedUtc = run.UpdatedUtc;
        }
        else
        {
            var runPath = ResolveRunPath(channelState, doc.CampaignName);
            try
            {
                if (File.Exists(runPath))
                {
                    File.Delete(runPath);
                }
            }
            catch
            {
                // ignore
            }
            doc.RunUpdatedUtc = default;
        }
    }

    private async Task<DndLiteCampaignCatalogDocument> LoadDraftCampaignAsync(InstructionGPT.ChannelState channelState, string name, CancellationToken ct)
    {
        var campaignName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        var path = ResolveDraftCampaignPath(channelState, campaignName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var draft = JsonSerializer.Deserialize<DndLiteCampaignCatalogDocument>(json, _jsonOptions);
            if (draft == null)
            {
                return null;
            }

            draft.CampaignName = string.IsNullOrWhiteSpace(draft.CampaignName) ? campaignName : draft.CampaignName.Trim();
            draft.CampaignMarkdown ??= string.Empty;
            draft.EncounterTemplates ??= new List<DndLiteEncounterTemplateDocument>();
            return draft;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveDraftCampaignAsync(InstructionGPT.ChannelState channelState, DndLiteCampaignDocument doc, CancellationToken ct)
    {
        if (doc == null || string.IsNullOrWhiteSpace(doc.CampaignName))
        {
            return;
        }

        var draft = new DndLiteCampaignCatalogDocument
        {
            CampaignName = doc.CampaignName.Trim(),
            CampaignMarkdown = doc.CampaignMarkdown ?? string.Empty,
            UpdatedUtc = doc.UpdatedUtc == default ? DateTime.UtcNow : doc.UpdatedUtc,
            EncounterTemplates = doc.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>()
        };

        var path = ResolveDraftCampaignPath(channelState, draft.CampaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(draft, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<DndLitePartyDocument> LoadDraftPartyAsync(InstructionGPT.ChannelState channelState, string campaignName, CancellationToken ct)
    {
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();
        var path = ResolveDraftPartyPath(channelState, campaignName);
        if (!File.Exists(path))
        {
            return new DndLitePartyDocument();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new DndLitePartyDocument();
            }

            var doc = JsonSerializer.Deserialize<DndLitePartyDocument>(json, _jsonOptions) ?? new DndLitePartyDocument();
            doc.PlayerUserIds ??= new List<ulong>();
            doc.NpcActorIds ??= new List<string>();
            return doc;
        }
        catch
        {
            return new DndLitePartyDocument();
        }
    }

    private async Task SaveDraftPartyAsync(InstructionGPT.ChannelState channelState, string campaignName, DndLitePartyDocument party, CancellationToken ct)
    {
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();
        party ??= new DndLitePartyDocument();
        party.PlayerUserIds ??= new List<ulong>();
        party.NpcActorIds ??= new List<string>();
        var path = ResolveDraftPartyPath(channelState, campaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(party, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task DeleteDraftAsync(InstructionGPT.ChannelState channelState, string campaignName, CancellationToken ct)
    {
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();
        var cPath = ResolveDraftCampaignPath(channelState, campaignName);
        var pPath = ResolveDraftPartyPath(channelState, campaignName);
        try
        {
            if (File.Exists(cPath))
            {
                File.Delete(cPath);
            }
        }
        catch { }
        try
        {
            if (File.Exists(pPath))
            {
                File.Delete(pPath);
            }
        }
        catch { }

        // Best-effort: remove empty draft directory.
        try
        {
            var dir = Path.GetDirectoryName(cPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch { }

        await Task.CompletedTask;
    }

    private async Task SaveCampaignCatalogAsync(InstructionGPT.ChannelState channelState, DndLiteCampaignCatalogDocument catalog, CancellationToken ct)
    {
        if (catalog == null || string.IsNullOrWhiteSpace(catalog.CampaignName))
        {
            return;
        }

        catalog.CampaignName = catalog.CampaignName.Trim();
        catalog.CampaignMarkdown ??= string.Empty;
        catalog.EncounterTemplates ??= new List<DndLiteEncounterTemplateDocument>();

        // If a campaign is being created/imported and doesn't have an UpdatedUtc, stamp it once.
        if (catalog.UpdatedUtc == default)
        {
            catalog.UpdatedUtc = DateTime.UtcNow;
        }

        var path = ResolveCampaignPath(channelState, catalog.CampaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(catalog, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task SaveRunStateAsync(InstructionGPT.ChannelState channelState, string campaignName, DndLiteCampaignRunDocument run, CancellationToken ct)
    {
        if (run == null || string.IsNullOrWhiteSpace(campaignName))
        {
            return;
        }

        run.CampaignName = string.IsNullOrWhiteSpace(run.CampaignName) ? campaignName.Trim() : run.CampaignName.Trim();
        run.LiveRuntime ??= new DndLiteEncounterLiveRuntime();

        var path = ResolveRunPath(channelState, campaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(run, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static bool HasLiveRuntimeState(DndLiteEncounterLiveRuntime rt)
    {
        if (rt == null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(rt.EncounterId) ||
               rt.EncounterStartedUtc != default ||
               !string.IsNullOrWhiteSpace(rt.CurrentActorId) ||
               rt.CurrentActorTurnStartedUtc != default;
    }

    private async Task<DndLitePartyDocument> LoadPartyAsync(InstructionGPT.ChannelState channelState, string campaignName, CancellationToken ct)
    {
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();
        var path = ResolvePartyPath(channelState, campaignName);

        async Task<DndLitePartyDocument> ReadPartyAsync(string p)
        {
            if (!File.Exists(p))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(p, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var doc = JsonSerializer.Deserialize<DndLitePartyDocument>(json, _jsonOptions);
                doc ??= new DndLitePartyDocument();
                doc.PlayerUserIds ??= new List<ulong>();
                doc.NpcActorIds ??= new List<string>();
                return doc;
            }
            catch
            {
                return null;
            }
        }

        // New layout: party is per-run (campaign instance).
        var party = await ReadPartyAsync(path);
        if (party != null)
        {
            return party;
        }

        // Migration: legacy channel-wide party.json -> per-run party.json (only if missing).
        var legacyPath = ResolveLegacyPartyPath(channelState);
        var legacy = await ReadPartyAsync(legacyPath);
        if (legacy != null)
        {
            await SavePartyAsync(channelState, campaignName, legacy, ct);
            return legacy;
        }

        return new DndLitePartyDocument();
    }

    private async Task SavePartyAsync(InstructionGPT.ChannelState channelState, string campaignName, DndLitePartyDocument party, CancellationToken ct)
    {
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();
        party ??= new DndLitePartyDocument();
        party.PlayerUserIds ??= new List<ulong>();
        party.NpcActorIds ??= new List<string>();
        var path = ResolvePartyPath(channelState, campaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(party, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<DndLitePcProfile> LoadPcProfileAsync(InstructionGPT.ChannelState channelState, ulong userId, CancellationToken ct)
    {
        var path = ResolvePcProfilePath(channelState, userId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<DndLitePcProfile>(json, _jsonOptions);
            if (doc == null)
            {
                return null;
            }

            doc.UserId = userId;
            doc.ActorId ??= ToActorId(userId);
            doc.Stats ??= new DndStats(10, 10, 10, 10, 10);
            doc.ProfileMarkdown ??= string.Empty;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private async Task SavePcProfileAsync(InstructionGPT.ChannelState channelState, DndLitePcProfile profile, CancellationToken ct)
    {
        if (profile == null || profile.UserId == 0)
        {
            return;
        }

        profile.ActorId ??= ToActorId(profile.UserId);
        profile.Stats ??= new DndStats(10, 10, 10, 10, 10);
        profile.ProfileMarkdown ??= string.Empty;

        var path = ResolvePcProfilePath(channelState, profile.UserId);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<DndLiteNpcProfile> LoadNpcProfileAsync(InstructionGPT.ChannelState channelState, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }

        var path = ResolveNpcProfilePath(channelState, actorId.Trim());
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<DndLiteNpcProfile>(json, _jsonOptions);
            if (doc == null)
            {
                return null;
            }

            doc.ActorId ??= actorId.Trim();
            doc.IsNpc = true;
            doc.Stats ??= new DndStats(10, 10, 10, 10, 10);
            doc.ProfileMarkdown ??= string.Empty;
            doc.PersonalityNotes ??= string.Empty;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveNpcProfileAsync(InstructionGPT.ChannelState channelState, DndLiteNpcProfile profile, CancellationToken ct)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.ActorId))
        {
            return;
        }

        profile.ActorId = profile.ActorId.Trim();
        profile.IsNpc = true;
        profile.Stats ??= new DndStats(10, 10, 10, 10, 10);
        profile.ProfileMarkdown ??= string.Empty;
        profile.PersonalityNotes ??= string.Empty;

        var path = ResolveNpcProfilePath(channelState, profile.ActorId);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<string> AllocateNpcActorIdAsync(InstructionGPT.ChannelState channelState, string name, CancellationToken ct)
    {
        var baseSlug = SlugifySegment(name);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = "npc";
        }

        for (var i = 1; i <= 50; i++)
        {
            var actorId = i == 1 ? $"npc:{baseSlug}" : $"npc:{baseSlug}-{i}";
            var existing = await LoadNpcProfileAsync(channelState, actorId, ct);
            if (existing != null)
            {
                continue;
            }

            return actorId;
        }

        return null;
    }

    private static string ResolveModel(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        return channelState?.InstructionChat?.ChatBotState?.Parameters?.Model
               ?? context.DefaultParameters?.Model
               ?? "gpt-4o-mini";
    }

    private static string NormalizeMode(string value)
    {
        if (string.Equals(value, ModeGame, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "live", StringComparison.OrdinalIgnoreCase))
        {
            return ModeGame;
        }
        if (string.Equals(value, ModeDraft, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "build", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "prep", StringComparison.OrdinalIgnoreCase))
        {
            return ModeDraft;
        }
        return ModeOff;
    }

    private static string NormalizeAutoRollPolicy(string value)
    {
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }
        if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
        {
            return "never";
        }
        return "npc-only";
    }

    private static bool IsPcActorId(string actorId)
        => !string.IsNullOrWhiteSpace(actorId) && actorId.StartsWith("u:", StringComparison.OrdinalIgnoreCase);

    private static bool IsNpcActorId(string actorId)
        => !string.IsNullOrWhiteSpace(actorId) && actorId.StartsWith("npc:", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeActorIdForPath(string actorId)
    {
        var s = (actorId ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return "actor";
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '-' || ch == '_')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var outStr = sb.ToString();
        if (outStr.Length > 120)
        {
            outStr = outStr[..120];
        }
        return outStr;
    }

    private static void NormalizeState(DndLiteChannelState st)
    {
        st.Mode = NormalizeMode(st.Mode);
        st.ActiveCampaignName = string.IsNullOrWhiteSpace(st.ActiveCampaignName) ? "default" : st.ActiveCampaignName.Trim();
        if (st.PendingDraftAction == null && st.PendingCampaignCreate != null)
        {
            // Back-compat: migrate overwrite confirmations from older builds.
            st.PendingDraftAction = new PendingDraftActionRequest
            {
                ActionType = PendingActionCampaignCreate,
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    name = st.PendingCampaignCreate.CampaignName ?? "default",
                    prompt = st.PendingCampaignCreate.Prompt ?? string.Empty
                }),
                RequestedByUserId = 0,
                RequestedUtc = st.PendingCampaignCreate.RequestedUtc == default ? DateTime.UtcNow : st.PendingCampaignCreate.RequestedUtc,
                ExpiresUtc = st.PendingCampaignCreate.ExpiresUtc == default ? DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes) : st.PendingCampaignCreate.ExpiresUtc,
                Summary = $"overwrite draft campaign \"{(st.PendingCampaignCreate.CampaignName ?? "default").Trim()}\""
            };
            st.PendingCampaignCreate = null;
        }

        if (st.PendingCampaignCreate != null)
        {
            st.PendingCampaignCreate.CampaignName = (st.PendingCampaignCreate.CampaignName ?? string.Empty).Trim();
            st.PendingCampaignCreate.Prompt ??= string.Empty;
            // If someone edits state.json by hand or clocks drift, ensure the expiry is sane.
            if (st.PendingCampaignCreate.ExpiresUtc == default)
            {
                st.PendingCampaignCreate.ExpiresUtc = DateTime.UtcNow.AddMinutes(5);
            }
        }
        if (st.PendingDraftAction != null)
        {
            st.PendingDraftAction.ActionType = (st.PendingDraftAction.ActionType ?? string.Empty).Trim();
            st.PendingDraftAction.ArgumentsJson = string.IsNullOrWhiteSpace(st.PendingDraftAction.ArgumentsJson)
                ? "{}"
                : st.PendingDraftAction.ArgumentsJson.Trim();
            st.PendingDraftAction.Summary = (st.PendingDraftAction.Summary ?? string.Empty).Trim();
            if (st.PendingDraftAction.RequestedUtc == default)
            {
                st.PendingDraftAction.RequestedUtc = DateTime.UtcNow;
            }
            if (st.PendingDraftAction.ExpiresUtc == default)
            {
                st.PendingDraftAction.ExpiresUtc = DateTime.UtcNow.AddMinutes(DraftPendingActionTtlMinutes);
            }
        }
        st.Live ??= new DndLiteLiveConfig();
        st.Live.TickSeconds = Math.Clamp(st.Live.TickSeconds, 1, 30);
        st.Live.PlayerTurnTimeoutSeconds = Math.Clamp(st.Live.PlayerTurnTimeoutSeconds, 5, 3600);
        st.Live.EncounterTimeoutSeconds = Math.Clamp(st.Live.EncounterTimeoutSeconds, 30, 3600);
        st.Live.AutoRollPolicy = NormalizeAutoRollPolicy(st.Live.AutoRollPolicy);
        st.Live.MaxAutoStepsPerTick = Math.Clamp(st.Live.MaxAutoStepsPerTick, 1, 64);
    }

    private static string ToActorId(ulong userId) => $"u:{userId}";

    private static string GetLiteRootDirectory(InstructionGPT.ChannelState channelState)
    {
        var channelDir = InstructionGPT.GetChannelDirectory(channelState);
        return Path.Combine(channelDir, "dnd-lite");
    }

    private static string ResolveStatePath(InstructionGPT.ChannelState channelState)
        => Path.Combine(GetLiteRootDirectory(channelState), "state.json");

    private static string ResolveLegacyPartyPath(InstructionGPT.ChannelState channelState)
        => Path.Combine(GetLiteRootDirectory(channelState), "party.json");

    private static string ResolveRunPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "runs", safe, "run.json");
    }

    private static string ResolvePartyPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "runs", safe, "party.json");
    }

    private static string ResolveCampaignPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "campaigns", safe, "campaign.json");
    }

    private static string ResolveDraftCampaignPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "drafts", safe, "draft.json");
    }

    private static string ResolveDraftPartyPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "drafts", safe, "party.json");
    }

    private static string ResolvePcProfilePath(InstructionGPT.ChannelState channelState, ulong userId)
        => Path.Combine(GetLiteRootDirectory(channelState), "profiles", "pcs", $"{userId}.json");

    private static string ResolveNpcProfilePath(InstructionGPT.ChannelState channelState, string actorId)
        => Path.Combine(GetLiteRootDirectory(channelState), "profiles", "npcs", $"{SanitizeActorIdForPath(actorId)}.json");

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

    private static List<string> TryGetStringListArg(string argsJson, string name)
    {
        var values = new List<string>();
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return values;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        values.AddRange(SplitCsvValues(s));
                    }
                }
                else
                {
                    var raw = item.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        values.AddRange(SplitCsvValues(raw));
                    }
                }
            }

            return values
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var single = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return SplitCsvValues(single)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ulong> TryGetUlongListArg(string argsJson, string name)
    {
        var values = new List<ulong>();
        if (!GptCliFunction.TryGetJsonProperty(argsJson, name, out var element))
        {
            return values;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var n) && n != 0)
                {
                    values.Add(n);
                    continue;
                }

                var raw = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                foreach (var parsed in ParseCsvUlongValues(raw))
                {
                    values.Add(parsed);
                }
            }

            return values.Where(v => v != 0).Distinct().ToList();
        }

        var single = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        values.AddRange(ParseCsvUlongValues(single));
        return values.Where(v => v != 0).Distinct().ToList();
    }

    private static List<string> SplitCsvValues(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(new[] { ',', ';', '|', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static List<ulong> ParseCsvUlongValues(string raw)
    {
        var values = new List<ulong>();
        foreach (var token in SplitCsvValues(raw))
        {
            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (ulong.TryParse(digits, out var id) && id != 0)
            {
                values.Add(id);
            }
        }

        return values;
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

        // Hard-cut only. Avoid adding a truncation marker since it pollutes prompts and chat output.
        return value[..maxChars];
    }

    private static string NormalizeJsonForTrace(string json, int maxChars = 0)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        string normalized;
        try
        {
            using var doc = JsonDocument.Parse(json);
            normalized = JsonSerializer.Serialize(doc.RootElement);
        }
        catch
        {
            normalized = json;
        }

        var compact = normalized.Replace('\r', ' ').Replace('\n', ' ').Trim();
        compact = Regex.Replace(compact, @"\s+", " ");
        if (maxChars > 0)
        {
            return TrimToLimit(compact, Math.Max(40, maxChars));
        }

        return compact;
    }

    private async Task AppendPartyRosterContextAsync(
        StringBuilder userCtx,
        InstructionGPT.ChannelState channelState,
        string mode,
        string campaignName,
        CancellationToken ct)
    {
        if (userCtx == null || channelState == null)
        {
            return;
        }

        mode = NormalizeMode(mode);
        campaignName = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();

        DndLitePartyDocument party = null;
        if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase))
        {
            party = await LoadDraftPartyAsync(channelState, campaignName, ct);
        }
        else if (string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase))
        {
            party = await LoadPartyAsync(channelState, campaignName, ct);

            // Fallback: if party.json is missing/empty, try the runner state party list.
            if (party == null || ((party.PlayerUserIds?.Count ?? 0) == 0 && (party.NpcActorIds?.Count ?? 0) == 0))
            {
                try
                {
                    var campaign = await LoadCampaignAsync(channelState, campaignName, ct);
                    var actorIds = campaign?.RunnerState?.Party?
                        .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ActorId))
                        .Select(m => m.ActorId.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>();

                    party ??= new DndLitePartyDocument();
                    party.PlayerUserIds ??= new List<ulong>();
                    party.NpcActorIds ??= new List<string>();

                    foreach (var actorId in actorIds)
                    {
                        if (IsNpcActorId(actorId))
                        {
                            if (!party.NpcActorIds.Any(x => string.Equals(x, actorId, StringComparison.OrdinalIgnoreCase)))
                            {
                                party.NpcActorIds.Add(actorId);
                            }
                        }
                        else if (TryParsePcActorId(actorId, out var userId) && userId != 0)
                        {
                            if (!party.PlayerUserIds.Contains(userId))
                            {
                                party.PlayerUserIds.Add(userId);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (party == null)
        {
            return;
        }

        party.PlayerUserIds ??= new List<ulong>();
        party.NpcActorIds ??= new List<string>();

        if (party.PlayerUserIds.Count == 0 && party.NpcActorIds.Count == 0)
        {
            return;
        }

        userCtx.AppendLine("Party roster (actors):");

        foreach (var uid in party.PlayerUserIds.Distinct().OrderBy(x => x).Take(12))
        {
            var pc = await LoadPcProfileAsync(channelState, uid, ct);
            var name = pc?.Name;
            userCtx.AppendLine($"- {ToActorId(uid)} | <@{uid}> | {(string.IsNullOrWhiteSpace(name) ? "(no sheet)" : name.Trim())}");
        }

        foreach (var id in party.NpcActorIds
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                     .Take(16))
        {
            var npc = await LoadNpcProfileAsync(channelState, id.Trim(), ct);
            var name = npc?.Name;
            userCtx.AppendLine($"- {id.Trim()} | {(string.IsNullOrWhiteSpace(name) ? "(no sheet)" : name.Trim())}");
        }
    }

    private static bool TryRecordUserMessageWhenCoreDisabled(
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        string content)
    {
        try
        {
            if (channelState?.Options?.Enabled == true)
            {
                // Core bot already records user messages for context even when not responding.
                return false;
            }

            if (channelState?.InstructionChat == null || message?.Author == null)
            {
                return false;
            }

            var mentionToken = $"<@{message.Author.Id}>";
            var text = $"{message.Author.Username} (mention: {mentionToken}): {content}";
            channelState.InstructionChat.AddMessage(new ChatMessage(ChatCompletionRole.User, text));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRecordAssistantReply(InstructionGPT.ChannelState channelState, string reply)
    {
        try
        {
            if (channelState?.InstructionChat == null || string.IsNullOrWhiteSpace(reply))
            {
                return false;
            }

            // Avoid polluting history with raw mention prefixes.
            var cleaned = Regex.Replace(reply.Trim(), @"^\s*<@!?\d+>\s*", string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return false;
            }

            channelState.InstructionChat.AddMessage(new ChatMessage(ChatCompletionRole.Assistant, cleaned));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<ChatMessage> SnapshotRecentUserAssistantHistory(
        InstructionGPT.ChannelState channelState,
        int maxMessages,
        int maxChars)
    {
        var result = new List<ChatMessage>();
        if (channelState?.InstructionChat?.ChatBotState?.Messages == null ||
            maxMessages <= 0 ||
            maxChars <= 0)
        {
            return result;
        }

        var total = 0;
        var node = channelState.InstructionChat.ChatBotState.Messages.Last;
        while (node != null && result.Count < maxMessages)
        {
            var msg = node.Value;
            node = node.Previous;

            if (msg == null || string.IsNullOrWhiteSpace(msg.Content))
            {
                continue;
            }

            if (msg.Role is not { } role)
            {
                continue;
            }

            // Keep only user/assistant turns for follow-up corrections.
            if (role != ChatCompletionRole.User &&
                role != ChatCompletionRole.Assistant)
            {
                continue;
            }

            var content = msg.Content.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            if (total >= maxChars)
            {
                break;
            }

            // We want most recent messages; if we're near the limit, trim the oldest we include.
            var remaining = Math.Max(1, maxChars - total);
            if (content.Length > remaining)
            {
                content = content[^remaining..];
            }

            result.Add(new ChatMessage(role, content));
            total += content.Length;
        }

        result.Reverse();
        return result;
    }

    private static string ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return trimmed[start..(end + 1)].Trim();
    }

    private static string ExtractChatMessageText(ChatMessage msg)
    {
        if (msg == null)
        {
            return null;
        }

        // Most models populate `Content` directly.
        if (!string.IsNullOrWhiteSpace(msg.Content))
        {
            return msg.Content;
        }

        // Some providers/models return structured content parts; the SDK exposes those via ContentCalculated (typed as object).
        try
        {
            var calc = msg.ContentCalculated;
            if (calc is string s)
            {
                return s;
            }

            if (calc is IEnumerable<MessageContent> parts)
            {
                var sb = new StringBuilder();
                foreach (var p in parts)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    if (string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Text))
                    {
                        sb.Append(p.Text);
                    }
                }

                var joined = sb.ToString();
                return string.IsNullOrWhiteSpace(joined) ? null : joined;
            }

            return calc?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static DraftStatusUpdate BuildConversationalStatusUpdate(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var text = rawText.Trim();
        var lower = text.ToLowerInvariant();

        if (lower.Contains("campaign generation started", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "campaign-gen-start",
                "Got it. I'll get started on that now. First pass is usually about 30-90 seconds.",
                Important: true);
        }

        if (lower.Contains("stage 1 - campaign foundation", StringComparison.OrdinalIgnoreCase) &&
            lower.Contains("round 1/", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "campaign-stage1",
                "I'm working on the campaign foundation now: premise, tone, and encounter seeds. Usually about 20-40 seconds.",
                Important: false);
        }

        if (lower.Contains("stage 2 - party and sheets", StringComparison.OrdinalIgnoreCase) &&
            lower.Contains("round 1/", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "campaign-stage2",
                "Now I'm on party setup: roster and sheet details. Usually another 20-60 seconds.",
                Important: false);
        }

        if (lower.Contains("stage 1 recovery - encounter templates", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "campaign-stage1-recovery",
                "Quick update: I'm filling in encounter templates before I continue. Usually another 15-35 seconds.",
                Important: true);
        }

        if (lower.Contains("draft update started", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "draft-update-start",
                "Got it. I'm revising the draft now. This usually takes about 15-45 seconds.",
                Important: true);
        }

        if (lower.Contains("character generation started", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "character-gen-start",
                "On it. I'm generating that character sheet now. Usually 10-30 seconds.",
                Important: true);
        }

        if (lower.Contains("npc generation started", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "npc-gen-start",
                "On it. I'm generating that NPC profile now. Usually 10-30 seconds.",
                Important: true);
        }

        if (lower.Contains("retrying", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "generation-retry",
                "Still working on it; retrying with stricter instructions. This can add about 20-45 seconds.",
                Important: true);
        }

        if (lower.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return new DraftStatusUpdate(
                "generation-failed",
                "I hit a generation issue while working on that. Please try again in a moment.",
                Important: true);
        }

        return null;
    }

    private bool ShouldSendSparseStatusUpdate(ulong channelId, DraftStatusUpdate update)
    {
        if (channelId == 0 || update == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (_lastStatusUpdateByChannel.TryGetValue(channelId, out var last))
        {
            if (string.Equals(last.Key, update.Key, StringComparison.OrdinalIgnoreCase) &&
                (now - last.SentUtc) < TimeSpan.FromMinutes(2))
            {
                return false;
            }

            var minGap = update.Important ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(25);
            if ((now - last.SentUtc) < minGap)
            {
                return false;
            }
        }

        _lastStatusUpdateByChannel[channelId] = (now, update.Key);
        return true;
    }

    private async Task SendProgressUpdateAsync(IMessageChannel channel, string text, DiscordModuleContext context = null)
    {
        if (channel == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var logPolicy = ResolveDndLogPolicy(context ?? _moduleContext);
        var update = BuildConversationalStatusUpdate(text);
        if (update == null || !ShouldSendSparseStatusUpdate(channel.Id, update))
        {
            return;
        }
        try
        {
            var payload = $"⏳ {update.Message}";
            if (ShouldLog(DndLogLevel.Debug, logPolicy.ConsoleMinLevel))
            {
                var preview = TrimToLimit(payload.Replace('\n', ' ').Replace('\r', ' '), 220);
                Console.WriteLine($"[dnd] progress->send channel={channel.Id} len={payload.Length} text={preview}");
            }
            await SendChunkedAsync(channel, payload);
            if (ShouldLog(DndLogLevel.Debug, logPolicy.ConsoleMinLevel))
            {
                Console.WriteLine($"[dnd] progress->sent channel={channel.Id} len={payload.Length}");
            }
        }
        catch (Exception ex)
        {
            if (ShouldLog(DndLogLevel.Error, logPolicy.ConsoleMinLevel))
            {
                Console.WriteLine($"[dnd] progress->send-failed channel={channel.Id} ex={ex.GetType().Name} msg={ex.Message}");
            }
        }
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

    private sealed class DndLiteChannelState
    {
        public string Mode { get; set; } = ModeOff;
        public string ActiveCampaignName { get; set; } = "default";
        public bool PreviousBotMuted { get; set; }
        public bool ModuleMutedBot { get; set; }
        public DndLiteLiveConfig Live { get; set; } = new();

        // Overwrite confirmations for deterministic "new campaign ..." chat creation in build mode.
        public PendingCampaignCreateRequest PendingCampaignCreate { get; set; }
        public PendingDraftActionRequest PendingDraftAction { get; set; }
    }

    private sealed class PendingCampaignCreateRequest
    {
        public string CampaignName { get; set; }
        public string Prompt { get; set; }
        public DateTime RequestedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class PendingDraftActionRequest
    {
        public string ActionType { get; set; }
        public string ArgumentsJson { get; set; }
        public ulong RequestedByUserId { get; set; }
        public DateTime RequestedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public string Summary { get; set; }
    }

    private sealed class DndLiteLiveConfig
    {
        public int TickSeconds { get; set; } = 5;
        // Default: 30 minutes. (Older versions were 30s and felt unusably short.)
        public int PlayerTurnTimeoutSeconds { get; set; } = 1800;
        public int EncounterTimeoutSeconds { get; set; } = 600;
        public bool NpcAutoplayEnabled { get; set; } = true;

        // "npc-only" (default), "all", "never"
        public string AutoRollPolicy { get; set; } = "npc-only";

        // Optional LLM call per NPC action to generate a 1-line in-character quip.
        public bool NpcFlavorEnabled { get; set; } = true;

        // Safety: maximum number of auto steps per tick loop.
        public int MaxAutoStepsPerTick { get; set; } = 8;
    }

    private sealed class DndLitePartyDocument
    {
        public List<ulong> PlayerUserIds { get; set; } = new();
        public List<string> NpcActorIds { get; set; } = new();
    }

    private sealed class DndLitePcProfile
    {
        public ulong UserId { get; set; }
        public string ActorId { get; set; }
        public string Name { get; set; }
        public string Concept { get; set; }
        public string ProfileMarkdown { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }
    }

    private sealed class DndLiteNpcProfile
    {
        public string ActorId { get; set; }
        public bool IsNpc { get; set; } = true;
        public string Name { get; set; }
        public string Concept { get; set; }
        public string ProfileMarkdown { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }

        public string PersonalityNotes { get; set; }
    }

    // Catalog-only campaign definition (replayable, no party/runtime state).
    private sealed class DndLiteCampaignCatalogDocument
    {
        public string CampaignName { get; set; }
        public string CampaignMarkdown { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public List<DndLiteEncounterTemplateDocument> EncounterTemplates { get; set; } = new();
    }

    // Per-channel per-campaign runtime state (party progress + active encounter pointers).
    private sealed class DndLiteCampaignRunDocument
    {
        public string CampaignName { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DndCampaignRunnerState RunnerState { get; set; }
        public DndLiteEncounterLiveRuntime LiveRuntime { get; set; } = new();
    }

    // In-memory combined view of catalog + run state.
    private sealed class DndLiteCampaignDocument
    {
        public string CampaignName { get; set; }
        public string CampaignMarkdown { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public List<DndLiteEncounterTemplateDocument> EncounterTemplates { get; set; } = new();
        public DndCampaignRunnerState RunnerState { get; set; }
        public DndLiteEncounterLiveRuntime LiveRuntime { get; set; } = new();
        public DateTime RunUpdatedUtc { get; set; }
    }

    private sealed class DndLiteEncounterLiveRuntime
    {
        public string EncounterId { get; set; } = string.Empty;
        public DateTime EncounterStartedUtc { get; set; }
        public string CurrentActorId { get; set; } = string.Empty;
        public DateTime CurrentActorTurnStartedUtc { get; set; }
    }

    private sealed class DndLiteEncounterTemplateDocument
    {
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Scene { get; set; }
        public string Rewards { get; set; }
        public DndLiteActorDescriptor Boss { get; set; }
        public List<DndLiteActorDescriptor> Adds { get; set; } = new();
        public DndEncounterTemplate Mechanics { get; set; }
    }

    private sealed class DndLiteActorDescriptor
    {
        public string ActorId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    private sealed class CampaignCreateResponseDto
    {
        public string CampaignMarkdown { get; set; }
        public List<EncounterTemplateDto> Encounters { get; set; } = new();
        public List<DndLiteFunctionCallDto> FunctionCalls { get; set; } = new();
        public List<UnassignedPcDto> UnassignedPcs { get; set; } = new();
    }

    private sealed class DndLiteFunctionCallDto
    {
        public string Name { get; set; }
        public JsonElement Arguments { get; set; }
    }

    private sealed class DndCreateNpcSheetArgsDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Concept { get; set; }
        public string ProfileMarkdown { get; set; }
        public string PersonalityNotes { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }
    }

    private sealed class DndCreatePcSheetArgsDto
    {
        public string ActorId { get; set; }
        public string Name { get; set; }
        public string Concept { get; set; }
        public string ProfileMarkdown { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }
    }

    private sealed class UnassignedPcDto
    {
        public string Name { get; set; }
        public string Concept { get; set; }
    }

    private sealed class EncounterTemplateDto
    {
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Scene { get; set; }
        public string Rewards { get; set; }
        public ActorDto Boss { get; set; }
        public List<ActorDto> Adds { get; set; } = new();
    }

    private sealed class ActorDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }
    }

    private sealed class CharacterCreateResponseDto
    {
        public string ProfileMarkdown { get; set; }
        public int MaxHp { get; set; }
        public int MaxMp { get; set; }
        public DndStats Stats { get; set; }
    }

    private sealed class NpcFlavorResponseDto
    {
        public string Line { get; set; }
    }

    private sealed class GameFlavorResponseDto
    {
        public string Lead { get; set; }
    }
}
