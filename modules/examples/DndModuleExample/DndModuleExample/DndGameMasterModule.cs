using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Commands;
using GPT.CLI.Chat.Discord.Modules;
using GPT.CLI.Chat.Dnd;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;
using OpenAI.ObjectModels.SharedModels;

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

    private static readonly Regex BotMentionRegexTemplate =
        new(@"<@!?(?<id>\d+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<ulong, DndLiteChannelState> _stateByChannel = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private readonly ConcurrentDictionary<ulong, RandomDiceRoller> _diceByChannel = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _tickCtsByChannel = new();
    private readonly ConcurrentDictionary<ulong, Task> _tickTasksByChannel = new();

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
                    "- Treat natural-language requests as instructions to build/update persistent draft state (campaign draft, party roster, character sheets, encounter templates).\n" +
                    "- The user may mention any available DnD capability; your job is to call the relevant `gptcli_dnd_*` functions to create/update what they asked for.\n" +
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
                "- Prefer Discord-friendly formatting for readability: bold beats, short paragraphs, bullet options, and occasional emojis.\n" +
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

                // First: handle pending overwrite confirmations, if any.
                if (await TryHandlePendingCampaignOverwriteAsync(context, channelState, message, dndState, cancellationToken))
                {
                    return;
                }

                // Deterministic campaign creation: avoid LLM tool-choice ambiguity for "new campaign ..." messages.
                // This makes "describe a new campaign" reliably persist to the JSON draft storage.
                var stripped = StripBotMentions(content, context.Client.CurrentUser.Id);
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
                        dndState.PendingCampaignCreate = new PendingCampaignCreateRequest
                        {
                            CampaignName = extractedCampaignName,
                            Prompt = stripped,
                            RequestedUtc = DateTime.UtcNow,
                            ExpiresUtc = DateTime.UtcNow.AddMinutes(5)
                        };
                        await SaveStateAsync(channelState, dndState, cancellationToken);

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

                // Draft mode: high-confidence deterministic party edits (mentions + npc: ids).
                if (await TryHandleDeterministicDraftPartyEditsAsync(context, channelState, message, dndState, stripped, cancellationToken))
                {
                    return;
                }

                // Draft mode: high-confidence deterministic story edits ("rewrite/change/update..." etc).
                if (await TryHandleDeterministicDraftUpdateAsync(context, channelState, message, dndState, stripped, cancellationToken))
                {
                    return;
                }

                var handled = await TryHandleAutoRoutedMessageAsync(context, channelState, message, dndState, cancellationToken);
                Console.WriteLine($"[dnd] auto-route: handled={handled} mode=draft (channel={message.Channel?.Id})");
                if (handled)
                {
                    return;
                }

                // Avoid silent failures in draft mode; if auto-routing can't respond, tell the user.
                try
                {
                    await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Sorry, I couldn't process that draft message. Try again in a moment.");
                }
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
                var handled = await TryHandleTaggedNaturalActionAsync(context, channelState, message, dndState, cancellationToken);
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
        CancellationToken ct)
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
        // In GAME mode, mode changes must be done via slash command to avoid accidental in-character triggers.
        var allowModeTool = !string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) && LooksLikeModeChangeIntent(text);
        var functions = GetGptCliFunctions(context)
            .Where(f => f?.ExecuteAsync != null &&
                        !string.IsNullOrWhiteSpace(f.ToolName) &&
                        f.ToolName.StartsWith("gptcli_dnd_", StringComparison.OrdinalIgnoreCase) &&
                        IsDndToolAllowedForMode(f.ToolName, mode) &&
                        (allowModeTool || !string.Equals(f.ToolName, "gptcli_dnd_mode", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (functions.Count == 0)
        {
            return false;
        }

        var tools = functions.Select(f => f.ToToolDefinition()).ToList();
        var byName = functions
            .Where(f => !string.IsNullOrWhiteSpace(f.ToolName))
            .GroupBy(f => f.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var activeCampaign = dndState?.ActiveCampaignName ?? "default";

        var userCtx = new StringBuilder();
        userCtx.AppendLine($"DnD mode: {mode}");
        userCtx.AppendLine($"Active campaign: {activeCampaign}");

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
                if (!string.IsNullOrWhiteSpace(draft.CampaignMarkdown))
                {
                    userCtx.AppendLine("Existing draft campaign markdown (excerpt):");
                    userCtx.AppendLine(TrimToLimit(draft.CampaignMarkdown, 1200));
                }
                if (draft.EncounterTemplates is { Count: > 0 })
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
                    "You are the D&D game master assistant operating in DRAFT mode.\n" +
                    "Draft mode is GM prep: build the campaign premise/tone, hooks, scenes, NPCs, encounter templates, and party composition.\n" +
                    "The user may mention any available DnD capability; your job is to call the appropriate `gptcli_dnd_*` functions to create/update the requested state.\n" +
                    "Do not call `gptcli_dnd_mode` unless the user explicitly asks to change modes.\n" +
                    "If the user is describing a new campaign (name + premise/party), you must call `gptcli_dnd_campaigncreate`.\n" +
                    "If the user asks to change/rewrite/update the existing draft story, you must call `gptcli_dnd_draftupdate`.\n" +
                    "Party roster is provided in context under \"Party roster (actors)\".\n" +
                    "If the user asks to add/remove party members:\n" +
                    "- PCs: use `gptcli_dnd_partyaddpc` / `gptcli_dnd_partyremovepc`.\n" +
                    "- NPCs: use `gptcli_dnd_partyremovenpc` (or `gptcli_dnd_npcremove`).\n" +
                    "If the user asks to change the auto-pass / player turn timeout / pass timeout, call `gptcli_dnd_passtimeout`.\n" +
                    "If the user specifies party members: create NPCs via tools; for PCs, only create a sheet for the author unless you have an explicit Discord user reference.\n" +
                    "In DRAFT mode, do not finalize/save campaigns to the catalog. Build drafts only; finalization happens when entering game mode or via campaignfinalize.\n" +
                    "If the message should persist state (create/overwrite a draft, set active campaign, add/remove party members, list campaigns/encounters), call the appropriate tool(s) even if the user did not say \"run a command\".\n" +
                    "If the message is only discussion/brainstorming with no persistent change requested, respond as GM: concise, actionable, and incorporate the draft statement.\n",
                ModeGame =>
                    "You are the D&D game master assistant operating in GAME mode.\n" +
                    "Game mode is live play. Treat every user message as in-game gameplay or roleplay.\n" +
                    "The user may mention any available DnD capability; your job is to call the appropriate `gptcli_dnd_*` functions to carry out mechanics/state changes when needed.\n" +
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
                    "When presenting choices, use bullet points and keep them actionable.\n" +
                    "Always end with a playable prompt (a question or 2-4 bullet options).\n",
                _ =>
                    "You are the D&D game master assistant.\n" +
                    "If the user is asking to perform a DnD slash command, call the appropriate tool(s).\n" +
                    "Otherwise respond briefly.\n"
            };

        var request = new ChatCompletionCreateRequest
        {
            Model = ResolveModel(context, channelState),
            Temperature = string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) ? 0.3f : 0.2f,
            MaxCompletionTokens = string.Equals(mode, ModeGame, StringComparison.OrdinalIgnoreCase) ? 350 : 700,
            ParallelToolCalls = false,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System, system)
            },
            Tools = tools,
            ToolChoice = new ToolChoice { Type = "auto" }
        };

        // Add recent conversation history so followups like "no I meant X" can be resolved in context.
        // Only include user/assistant roles to keep the prompt small and avoid unrelated system noise.
        try
        {
            var history = SnapshotRecentUserAssistantHistory(channelState, maxMessages: 14, maxChars: 6000);
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

        request.Messages.Add(new ChatMessage(StaticValues.ChatMessageRoles.User, userCtx.ToString().Trim()));

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
                    $"[dnd] auto-route: llm request mode={mode} model={request.Model} tools={tools.Count} campaign={activeCampaign} channel={message.Channel?.Id} toolNames=[{string.Join(", ", toolNames)}]");
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
            return false;
        }

        var msg = response.Choices?.FirstOrDefault()?.Message;
        if (msg == null)
        {
            Console.WriteLine("[dnd] auto-route: llm returned no message");
            return false;
        }

        try
        {
            var choice = response.Choices?.FirstOrDefault();
            var finish = choice?.FinishReason?.ToString() ?? "(null)";
            var contentLen = msg.Content == null ? -1 : msg.Content.Length;
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
            if (applied.Count == 1)
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

            // In draft mode, allow the model to include a short GM note alongside the tool calls (e.g. next steps).
            if (string.Equals(mode, ModeDraft, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(msg.Content))
            {
                replyLines.Add(string.Empty);
                replyLines.Add("GM:");
                replyLines.Add(TrimToLimit(msg.Content.Trim(), 900));
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

        var content = msg.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
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

        // Intent heuristics: keep it simple and explicit.
        var looksLikeCreate =
            lower.Contains("new campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("create a campaign", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("start a campaign", StringComparison.OrdinalIgnoreCase) ||
            (lower.Contains("campaign", StringComparison.OrdinalIgnoreCase) && lower.Contains("named", StringComparison.OrdinalIgnoreCase));

        if (!looksLikeCreate)
        {
            return false;
        }

        // Extract quoted name first.
        // Examples:
        // - new campaign named "Sour Patch Kids"
        // - campaign named 'Sour Patch Kids'
        var m = Regex.Match(t, @"\bcampaign\b[^\n]{0,80}?\bnamed\b\s*(?:(?:""(?<q>[^""]{1,120})"")|(?:'(?<q>[^']{1,120})'))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            m = Regex.Match(t, @"\bnew\s+campaign\b[^\n]{0,80}?\bnamed\b\s*(?:(?:""(?<q>[^""]{1,120})"")|(?:'(?<q>[^']{1,120})'))",
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

        // Unquoted fallback: take a short tail after "campaign named".
        m = Regex.Match(t, @"\bcampaign\b[^\n]{0,80}?\bnamed\b\s*(?<u>[^.\n,!]{1,120})",
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

        // High-signal edit verbs.
        if (lower.Contains("rewrite", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("revise", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("retcon", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("update the", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("change the", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("modify", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("replace", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Start-of-message imperative edits are usually meant to apply to the current draft.
        if (lower.StartsWith("add ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("remove ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("make ", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("swap ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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

        // Mention-based PC party edits.
        var mentioned = message.MentionedUsers
            .Where(u => u != null && u.Id != 0 && u.Id != context.Client.CurrentUser.Id)
            .Select(u => u.Id)
            .Distinct()
            .ToList();

        var npcIds = ExtractNpcActorIds(strippedText);

        var wantsAdd = LooksLikePartyAdd(strippedText);
        var wantsRemove = LooksLikePartyRemove(strippedText);

        // If no obvious party signals, skip.
        if (mentioned.Count == 0 && npcIds.Count == 0)
        {
            return false;
        }

        // If both add and remove words exist, it's ambiguous; let the LLM router handle it.
        if (wantsAdd && wantsRemove)
        {
            return false;
        }

        if (!wantsAdd && !wantsRemove)
        {
            // Mentions without add/remove: let the LLM handle (could just be addressing someone).
            return false;
        }

        var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
        var applied = new List<string>();
        var errors = new List<string>();

        if (wantsAdd)
        {
            foreach (var uid in mentioned)
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

            foreach (var npc in npcIds)
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
        else if (wantsRemove)
        {
            foreach (var uid in mentioned)
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

            foreach (var npc in npcIds)
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

        if (applied.Count == 0 && errors.Count == 0)
        {
            return false;
        }

        var replyLines = new List<string> { $"<@{message.Author.Id}> OK:" };
        replyLines.AddRange(applied.Select(x => $"- {x}"));
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

    private async Task<bool> TryHandlePendingCampaignOverwriteAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        if (context == null || channelState == null || message == null || dndState?.PendingCampaignCreate == null)
        {
            return false;
        }

        var pending = dndState.PendingCampaignCreate;
        var now = DateTime.UtcNow;
        if (pending.ExpiresUtc != default && pending.ExpiresUtc <= now)
        {
            dndState.PendingCampaignCreate = null;
            try { await SaveStateAsync(channelState, dndState, ct); } catch { }
            return false;
        }

        var raw = (message.Content ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        var isConfirm = normalized == "confirm overwrite" || normalized == "confirm";
        var isCancel = normalized == "cancel";

        if (!isConfirm && !isCancel)
        {
            return false;
        }

        if (isCancel)
        {
            dndState.PendingCampaignCreate = null;
            await SaveStateAsync(channelState, dndState, ct);
            try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Canceled."); } catch { }
            return true;
        }

        // Confirm overwrite: execute creation using the stored prompt/name.
        var argsJson = JsonSerializer.Serialize(new
        {
            name = pending.CampaignName,
            prompt = pending.Prompt
        });

        dndState.PendingCampaignCreate = null;
        await SaveStateAsync(channelState, dndState, ct);

        try
        {
            var execCtx = new GptCliExecutionContext(context, channelState, message.Channel, message.Author, null, message);
            var res = await ExecuteCampaignCreateAsync(execCtx, argsJson, ct);
            if (res is { Handled: true })
            {
	                var reply = string.IsNullOrWhiteSpace(res.Response)
	                    ? $"<@{message.Author.Id}> OK."
	                    : $"<@{message.Author.Id}>\n{res.Response.Trim()}";
                Console.WriteLine(
                    $"[dnd] draft-create-confirm: responding handled=true respLen={(res.Response ?? string.Empty).Length} replyLen={reply.Length}");
                Console.WriteLine($"[dnd] draft-create-confirm: reply={reply}");
                await SendChunkedAsync(message.Channel, reply);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dnd] confirm overwrite campaign create failed: {ex.GetType().Name} {ex.Message}");
        }

        try { await message.Channel.SendMessageAsync($"<@{message.Author.Id}> Sorry, campaign overwrite failed. Try `/gptcli dnd campaigncreate`."); } catch { }
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

            var gen = await GenerateCampaignPackageAsync(ctx.Context, ctx.ChannelState, reqId, campaignName.Trim(), prompt.Trim(), pcRosterContext, ct);
            var pkg = gen?.Package;
            if (pkg == null)
            {
                var reason = string.IsNullOrWhiteSpace(gen?.Error) ? "Unknown error." : gen.Error.Trim();
                Console.WriteLine($"[dnd] campaigncreate[{reqId}]: generation failed model={gen?.Model} httpMs={gen?.HttpMs} err={reason}");
                return new GptCliExecutionResult(true, $"Campaign generation failed: {reason}", false);
            }
            if (string.IsNullOrWhiteSpace(pkg.CampaignMarkdown))
            {
                Console.WriteLine(
                    $"[dnd] campaigncreate[{reqId}]: generation returned empty markdown model={gen?.Model} httpMs={gen?.HttpMs} encounters={(pkg.Encounters?.Count ?? 0)} fnCalls={(pkg.FunctionCalls?.Count ?? 0)}");
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

    private sealed class DraftUpdateResponseDto
    {
        public string CampaignMarkdown { get; set; }
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

            var model = ResolveModel(ctx.Context, ctx.ChannelState);
            var requestPrompt = BuildDraftUpdatePrompt(campaignName, draft.CampaignMarkdown, prompt.Trim());

            Console.WriteLine($"[dnd] draftupdate: campaign=\"{campaignName}\" model={model} existingLen={draft.CampaignMarkdown.Length} modLen={prompt.Trim().Length} promptLen={requestPrompt.Length}");
            Console.WriteLine("[dnd] draftupdate: system=You are revising a D&D campaign draft. Return strict JSON only. No markdown. No code fences.");
            Console.WriteLine($"[dnd] draftupdate: userPrompt={requestPrompt}");

            var request = new ChatCompletionCreateRequest
            {
                Model = model,
                Temperature = 0.2f,
                MaxCompletionTokens = 2200,
                Messages = new List<ChatMessage>
                {
                    new(StaticValues.ChatMessageRoles.System, "You are revising a D&D campaign draft. Return strict JSON only. No markdown. No code fences."),
                    new(StaticValues.ChatMessageRoles.User, requestPrompt)
                }
            };

            ChatCompletionCreateResponse response;
            try
            {
                response = await ctx.Context.OpenAILogic.CreateChatCompletionAsync(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dnd] draftupdate: llm exception {ex.GetType().Name} {ex.Message}");
                return new GptCliExecutionResult(true, $"Draft update failed: {ex.GetType().Name} {ex.Message}", false);
            }

            if (!response.Successful)
            {
                var err = $"{response.Error?.Code} {response.Error?.Message}".Trim();
                Console.WriteLine($"[dnd] draftupdate: llm unsuccessful {err}");
                return new GptCliExecutionResult(true, $"Draft update failed: {err}", false);
            }

            var content = response.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                Console.WriteLine("[dnd] draftupdate: empty content");
                try
                {
                    var choice = response.Choices?.FirstOrDefault();
                    var dumped = JsonSerializer.Serialize(choice, _jsonOptions);
                    Console.WriteLine($"[dnd] draftupdate: choiceDump={dumped}");
                }
                catch
                {
                    // ignore
                }
                return new GptCliExecutionResult(true, "Draft update failed: empty response content from OpenAI.", false);
            }

            var json = ExtractJsonObject(content);
            if (string.IsNullOrWhiteSpace(json))
            {
                Console.WriteLine($"[dnd] draftupdate: non-json content={content}");
                return new GptCliExecutionResult(true, "Draft update failed: OpenAI returned non-JSON content.", false);
            }

            DraftUpdateResponseDto parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DraftUpdateResponseDto>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dnd] draftupdate: json parse exception {ex.GetType().Name} {ex.Message} json={json}");
                return new GptCliExecutionResult(true, $"Draft update failed: {ex.GetType().Name} {ex.Message}", false);
            }

            var updated = parsed?.CampaignMarkdown?.Trim();
            if (string.IsNullOrWhiteSpace(updated))
            {
                Console.WriteLine($"[dnd] draftupdate: missing campaignMarkdown json={json}");
                return new GptCliExecutionResult(true, "Draft update failed: JSON missing campaignMarkdown.", false);
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
            var created = await GenerateCharacterAsync(
                ctx.Context,
                ctx.ChannelState,
                name.Trim(),
                concept.Trim(),
                (campaign?.CampaignMarkdown ?? draft?.CampaignMarkdown),
                ct);
            if (created == null || created.Stats == null || created.MaxHp <= 0)
            {
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
            var created = await GenerateCharacterAsync(
                ctx.Context,
                ctx.ChannelState,
                name.Trim(),
                concept.Trim(),
                (campaign?.CampaignMarkdown ?? draft?.CampaignMarkdown),
                ct);
            if (created == null || created.Stats == null || created.MaxHp <= 0)
            {
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

            var text = RenderTurnResult(res);
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
            var r = await RunEncounterActionAsync(ctx.ChannelState, st, ctx.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(ctx.ChannelState, st.ActiveCampaignName, ct), target, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.Attack(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, postToChannel: false, ct: ct);

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
            var r = await RunEncounterActionAsync(ctx.ChannelState, st, ctx.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(ctx.ChannelState, st.ActiveCampaignName, ct), target, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.CastSpell(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, postToChannel: false, ct: ct);

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
            var r = await RunEncounterActionAsync(ctx.ChannelState, st, ctx.Channel, runner =>
            {
                var res = runner.Pass(actorId);
                return Task.FromResult((res, RenderTurnResult(res)));
            }, postToChannel: false, ct: ct);

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

            var r = await RunEncounterActionAsync(ctx.ChannelState, st, ctx.Channel, runner =>
            {
                var res = runner.RollAll();
                return Task.FromResult((res, RenderTurnResult(res)));
            }, postToChannel: false, ct: ct);

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
            var r = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.RollAll();
                return (res, RenderTurnResult(res));
            }, postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        if (content.StartsWith("!roll ", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content[6..].Trim();
            if (string.Equals(tail, "initiative", StringComparison.OrdinalIgnoreCase))
            {
                var actorId = ToActorId(message.Author.Id);
                var r = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
                {
                    var res = runner.RollInitiative(actorId);
                    return (res, RenderTurnResult(res));
                }, postToChannel: true, ct: ct);
                return (r.handled, r.stateChanged);
            }

            var rollId = tail.Trim();
            var rr = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
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
            }, postToChannel: true, ct: ct);
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
            var r = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.Attack(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, postToChannel: true, ct: ct);
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
            var r = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.CastSpell(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        if (string.Equals(content, "!pass", StringComparison.OrdinalIgnoreCase))
        {
            var actorId = ToActorId(message.Author.Id);
            var r = await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.Pass(actorId);
                return (res, RenderTurnResult(res));
            }, postToChannel: true, ct: ct);
            return (r.handled, r.stateChanged);
        }

        return (false, false);
    }

    private async Task<bool> TryHandleTaggedNaturalActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign?.RunnerState == null)
        {
            return false;
        }

        var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
        var snap = runner.GetState();
        if (snap.ActiveEncounterState == null || snap.ActiveEncounterState.IsCompleted)
        {
            return false;
        }

        var pending = campaign.RunnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>();
        var next = DndTurnResult.BuildNextRequest(snap.ActiveEncounterState, pending);
        if (next.Kind != DndNextRequestKind.NeedAction)
        {
            return false;
        }

        var actorId = ToActorId(message.Author.Id);
        if (!string.Equals(snap.ActiveEncounterState.CurrentActorId, actorId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var decision = await RouteTaggedActionAsync(context, channelState, message, snap.ActiveEncounterState, ct);
        if (decision == null)
        {
            return false;
        }

        if (string.Equals(decision.ToolName, "dnd_rollall", StringComparison.OrdinalIgnoreCase))
        {
            await TryHandleBangCommandAsync(context, channelState, message, dndState, "!rollall", ct);
            return true;
        }
        if (string.Equals(decision.ToolName, "dnd_pass", StringComparison.OrdinalIgnoreCase))
        {
            await TryHandleBangCommandAsync(context, channelState, message, dndState, "!pass", ct);
            return true;
        }
        if (string.Equals(decision.ToolName, "dnd_attack", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetStringArg(decision.ArgumentsJson, "target", out var target) && !string.IsNullOrWhiteSpace(target))
            {
                await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!attack {target}", ct);
            }
            return true;
        }
        if (string.Equals(decision.ToolName, "dnd_cast", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetStringArg(decision.ArgumentsJson, "target", out var target) && !string.IsNullOrWhiteSpace(target))
            {
                await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!cast {target}", ct);
            }
            return true;
        }

        return false;
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
                new(StaticValues.ChatMessageRoles.System, system),
                new(StaticValues.ChatMessageRoles.User, sb.ToString().Trim()),
                new(StaticValues.ChatMessageRoles.User, $"User question: {text}")
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

        var content = response.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        try { await SendChunkedAsync(message.Channel, TrimToLimit(content.Trim(), 3500)); } catch { }
        return true;
    }

    private sealed class NaturalDecision
    {
        public string ToolName { get; set; }
        public string ArgumentsJson { get; set; }
    }

    private async Task<NaturalDecision> RouteTaggedActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndEncounterSnapshot encounter,
        CancellationToken ct)
    {
        try
        {
            var livingEnemies = encounter.Actors.Values
                .Where(a => a != null && a.Side == DndSide.Enemy && a.IsAlive)
                .Select(a => $"{a.ActorId}:{a.Name} (HP {a.Hp}/{a.MaxHp})")
                .ToList();

            var system =
                "You are a deterministic combat action router.\n" +
                "Only call a tool if the user is clearly attempting an in-combat action.\n" +
                "If it is roleplay or table talk, do not call tools.\n" +
                "Never invent outcomes; mechanics are handled by code.\n" +
                "When calling dnd_attack or dnd_cast, target must match a living enemy id or name from the provided list.\n";

            var user = new StringBuilder();
            user.AppendLine("Living enemies:");
            user.AppendLine(livingEnemies.Count == 0 ? "- (none)" : string.Join("\n", livingEnemies.Select(s => $"- {s}")));
            user.AppendLine("Message:");
            user.AppendLine(StripBotMentions(message.Content ?? string.Empty, context.Client.CurrentUser.Id));

            var tools = BuildRouterTools();
            var request = new ChatCompletionCreateRequest
            {
                Model = ResolveModel(context, channelState),
                Temperature = 0,
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
                return null;
            }

            var msg = response.Choices.FirstOrDefault()?.Message;
            if (msg == null)
            {
                return null;
            }

            var toolCalls = msg.ToolCalls;
            if ((toolCalls == null || toolCalls.Count == 0) && msg.FunctionCall != null)
            {
                toolCalls = new List<ToolCall> { new() { Type = "function", FunctionCall = msg.FunctionCall } };
            }
            var first = toolCalls?.FirstOrDefault()?.FunctionCall;
            if (first == null || string.IsNullOrWhiteSpace(first.Name))
            {
                return null;
            }

            return new NaturalDecision
            {
                ToolName = first.Name.Trim(),
                ArgumentsJson = string.IsNullOrWhiteSpace(first.Arguments) ? "{}" : first.Arguments
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<ToolDefinition> BuildRouterTools()
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
                "Make a basic attack against a specific living enemy.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["target"] = new PropertyDefinition { Type = "string", Description = "Enemy id or enemy name" }
                },
                new List<string> { "target" }),
            Fn(
                "dnd_cast",
                "Cast a basic spell against a specific living enemy.",
                new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["target"] = new PropertyDefinition { Type = "string", Description = "Enemy id or enemy name" }
                },
                new List<string> { "target" }),
            Fn(
                "dnd_pass",
                "Pass your turn.",
                new Dictionary<string, PropertyDefinition>(),
                new List<string>()),
            Fn(
                "dnd_rollall",
                "Roll all pending rolls (initiative/attacks/damage).",
                new Dictionary<string, PropertyDefinition>(),
                new List<string>())
        };
    }

    private async Task<(bool handled, bool stateChanged, string responseText)> RunEncounterActionAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        IMessageChannel discordChannel,
        Func<DndCampaignRunner, Task<(DndCampaignResult result, string responseText)>> action,
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
        if (postToChannel)
        {
            await SendChunkedAsync(discordChannel, responseText);
        }
        return (true, true, responseText);
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
                var text = RenderTurnResult(res);
                try { await SendChunkedAsync(discordChannel, text); } catch { }
                continue;
            }

            // Need action: autoplay NPCs, timeout PCs.
            if (next.Kind == DndNextRequestKind.NeedAction)
            {
                if (isNpc && dndState.Live.NpcAutoplayEnabled)
                {
                    var (npcRes, npcText) = await RunNpcAutoplayStepAsync(channelState, dndState, campaign, runner, currentActorId, ct);
                    if (npcRes == null)
                    {
                        break;
                    }

                    await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                    try { await SendChunkedAsync(discordChannel, npcText); } catch { }
                    continue;
                }

                if (isPc && timedOut)
                {
                    var res = runner.Pass(currentActorId);
                    await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
                    var text = "Player timed out; auto-pass.\n" + RenderTurnResult(res);
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

    private async Task<(DndCampaignResult result, string text)> RunNpcAutoplayStepAsync(
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
            return (null, "NPC not found/alive.");
        }

        var target = ChooseNpcTarget(enc);
        if (target == null)
        {
            var res0 = runner.Pass(npcActorId);
            return (res0, RenderTurnResult(res0));
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

        var sb = new StringBuilder();
        if (dndState.Live.NpcFlavorEnabled)
        {
            var npcProfile = await LoadNpcProfileAsync(channelState, npcActorId, ct);
            if (npcProfile != null)
            {
                var flavor = await GenerateNpcFlavorLineAsync(_moduleContext, channelState, campaign, npcProfile, enc, target, ct);
                if (!string.IsNullOrWhiteSpace(flavor))
                {
                    sb.AppendLine(flavor.Trim());
                }
            }
        }

        sb.AppendLine(RenderTurnResult(res));
        return (res, TrimToLimit(sb.ToString().Trim(), 3500));
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

        error = $"Target \"{wanted}\" not found. Use `!targets`.";
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
            return $"Next: {next.CurrentActorId} to act. Use `!attack <target>` or `!cast <target>` or `!pass`.";
        }

        if (next.RequiredRolls == null || next.RequiredRolls.Count == 0)
        {
            return "Next: pending rolls required.";
        }

        var lines = new List<string>();
        lines.Add(next.Kind == DndNextRequestKind.NeedInitiativeRolls
            ? "Next: initiative rolls pending. Players: `!roll initiative` (or GM: `!rollall`)."
            : "Next: rolls pending. Use `!roll <rollId>` (or `!rollall`).");

        foreach (var r in next.RequiredRolls.Take(8))
        {
            lines.Add($"- {r.RollId} {r.Kind} actor={r.ActorId} target={r.TargetId}");
        }

        return TrimToLimit(string.Join("\n", lines), 900);
    }

    private static string BuildHelpText()
    {
        return
            "DND simplified gameplay (game mode)\n" +
            "- `!state` (party + encounter summary)\n" +
            "- `!targets` (list enemies)\n" +
            "- `!attack <target>`\n" +
            "- `!cast <target>`\n" +
            "- `!pass`\n" +
            "- `!roll initiative`\n" +
            "- `!roll <rollId>`\n" +
            "- `!rollall`\n" +
            "- `!ledger [n]`\n\n" +
            "Hybrid: mention the bot to attempt routing your message into one of the above actions (only on your turn, when no rolls are pending).";
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

    private async Task<CampaignGenerateResult> GenerateCampaignPackageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string requestId,
        string campaignName,
        string prompt,
        string pcRosterContext,
        CancellationToken ct)
    {
        var model = ResolveModel(context, channelState);
        var requestPrompt = BuildCreateCampaignPrompt(campaignName, prompt, pcRosterContext);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            // Keep this tight: large generations tend to hit token limits and produce invalid/empty JSON.
            Temperature = 0.1f,
            MaxCompletionTokens = 1200,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are a campaign bootstrapper for a simplified D&D-like Discord engine. " +
                    "Return strict JSON only. No markdown. No code fences."),
                new(StaticValues.ChatMessageRoles.User, requestPrompt)
            }
        };

        ChatCompletionCreateResponse response;
        var sw = Stopwatch.StartNew();
        try
        {
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: request model={model} temp={request.Temperature} maxCompletionTokens={request.MaxCompletionTokens} msgChars={requestPrompt.Length}");
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: system={request.Messages[0].Content}");
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: userPrompt={requestPrompt}");
        }
        catch
        {
            // ignore log serialization issues
        }
        try
        {
            var responseTask = context.OpenAILogic.CreateChatCompletionAsync(request);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(CampaignCreateTimeoutSeconds), ct);
            var done = await Task.WhenAny(responseTask, timeoutTask);
            if (done != responseTask)
            {
                sw.Stop();
                Console.WriteLine(
                    $"[dnd] campaign-generate[{requestId}]: timeout after {sw.ElapsedMilliseconds}ms (limit={CampaignCreateTimeoutSeconds}s) model={model} promptLen={requestPrompt.Length}");
                return new CampaignGenerateResult(null, $"Timed out after {CampaignCreateTimeoutSeconds}s.", (int)sw.ElapsedMilliseconds, model);
            }

            response = await responseTask;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: exception after {sw.ElapsedMilliseconds}ms model={model} ex={ex.GetType().Name} msg={ex.Message}");
            return new CampaignGenerateResult(null, $"{ex.GetType().Name}: {ex.Message}", (int)sw.ElapsedMilliseconds, model);
        }
        sw.Stop();

        try
        {
            var choice = response.Choices?.FirstOrDefault();
            var msg = choice?.Message;
            var finish = choice?.FinishReason?.ToString() ?? "(null)";
            var role = msg?.Role ?? "(null)";
            var contentLen = msg?.Content == null ? -1 : msg.Content.Length;
            var toolCalls = msg?.ToolCalls?.Count ?? 0;
            var hasFnCall = msg?.FunctionCall != null;
            var usage = response.Usage == null
                ? "(no usage)"
                : $"prompt={response.Usage.PromptTokens} completion={response.Usage.CompletionTokens} total={response.Usage.TotalTokens}";

            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: response httpMs={sw.ElapsedMilliseconds} ok={response.Successful} choices={(response.Choices?.Count ?? 0)} finish={finish} role={role} contentLen={contentLen} toolCalls={toolCalls} functionCall={hasFnCall} usage={usage}");
        }
        catch
        {
            // ignore
        }

        if (!response.Successful)
        {
            var err = $"{response.Error?.Code} {response.Error?.Message}".Trim();
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: openai unsuccessful httpMs={sw.ElapsedMilliseconds} model={model} err={err}");
            return new CampaignGenerateResult(null, $"OpenAI error: {err}", (int)sw.ElapsedMilliseconds, model);
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: empty content httpMs={sw.ElapsedMilliseconds} model={model} choices={(response.Choices?.Count ?? 0)}");

            string recoveredFromArgs = null;
            try
            {
                // Dump the entire choice message so we can see if output is in an unexpected field.
                var choice = response.Choices?.FirstOrDefault();
                var dumped = JsonSerializer.Serialize(choice, _jsonOptions);
                Console.WriteLine($"[dnd] campaign-generate[{requestId}]: choiceDump={dumped}");

                // Sometimes models emit JSON in function/tool-call arguments even when not requested.
                var msg = choice?.Message;
                var argCandidate = msg?.FunctionCall?.Arguments
                                   ?? msg?.ToolCalls?.FirstOrDefault()?.FunctionCall?.Arguments;
                var argJson = ExtractJsonObject(argCandidate ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(argJson))
                {
                    recoveredFromArgs = argJson.Trim();
                    Console.WriteLine($"[dnd] campaign-generate[{requestId}]: recoveredJsonFromArgs len={recoveredFromArgs.Length}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dnd] campaign-generate[{requestId}]: choiceDump failed: {ex.GetType().Name} {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(recoveredFromArgs))
            {
                content = recoveredFromArgs;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                // One retry with stricter instructions; we've observed occasional 200 responses with empty content.
                try
                {
                    var retry = new ChatCompletionCreateRequest
                    {
                        Model = model,
                        Temperature = 0,
                        MaxCompletionTokens = 1200,
                        Messages = new List<ChatMessage>
                        {
                            new(StaticValues.ChatMessageRoles.System,
                                "You are a campaign bootstrapper for a simplified D&D-like Discord engine. " +
                                "Return strict JSON only in assistant message content. " +
                                "Do not call tools or functions. Do not return empty content. No markdown. No code fences."),
                            new(StaticValues.ChatMessageRoles.User, requestPrompt)
                        }
                    };

                    Console.WriteLine($"[dnd] campaign-generate[{requestId}]: retry request model={model} temp={retry.Temperature} maxCompletionTokens={retry.MaxCompletionTokens} msgChars={requestPrompt.Length}");
                    var retrySw = Stopwatch.StartNew();
                    var retryResp = await context.OpenAILogic.CreateChatCompletionAsync(retry);
                    retrySw.Stop();
                    var retryContent = retryResp?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
                    Console.WriteLine($"[dnd] campaign-generate[{requestId}]: retry response httpMs={retrySw.ElapsedMilliseconds} ok={retryResp?.Successful} contentLen={(retryContent == null ? -1 : retryContent.Length)}");
                    if (retryResp is { Successful: true } && !string.IsNullOrWhiteSpace(retryContent))
                    {
                        content = retryContent;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[dnd] campaign-generate[{requestId}]: retry exception {ex.GetType().Name} {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return new CampaignGenerateResult(null, "Empty response content from OpenAI.", (int)sw.ElapsedMilliseconds, model);
            }
        }

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: non-JSON response httpMs={sw.ElapsedMilliseconds} model={model} contentLen={content.Length}");
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: content={content}");
            return new CampaignGenerateResult(null, "OpenAI returned non-JSON content (no JSON object found).", (int)sw.ElapsedMilliseconds, model);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CampaignCreateResponseDto>(json, _jsonOptions);
            if (parsed == null)
            {
                Console.WriteLine(
                    $"[dnd] campaign-generate[{requestId}]: JSON deserialized to null httpMs={sw.ElapsedMilliseconds} model={model} jsonLen={json.Length}");
                Console.WriteLine($"[dnd] campaign-generate[{requestId}]: json={json}");
                return new CampaignGenerateResult(null, "Failed to parse JSON response (null).", (int)sw.ElapsedMilliseconds, model);
            }

            parsed.CampaignMarkdown = parsed.CampaignMarkdown?.Trim();
            parsed.Encounters ??= new List<EncounterTemplateDto>();
            parsed.FunctionCalls ??= new List<DndLiteFunctionCallDto>();
            parsed.UnassignedPcs ??= new List<UnassignedPcDto>();
            if (parsed.Encounters.Count > 12)
            {
                parsed.Encounters = parsed.Encounters.Take(12).ToList();
            }

            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: ok httpMs={sw.ElapsedMilliseconds} model={model} mdLen={(parsed.CampaignMarkdown ?? string.Empty).Length} encounters={parsed.Encounters.Count} fnCalls={parsed.FunctionCalls.Count} unassigned={parsed.UnassignedPcs.Count}");
            return new CampaignGenerateResult(parsed, null, (int)sw.ElapsedMilliseconds, model);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[dnd] campaign-generate[{requestId}]: JSON parse exception httpMs={sw.ElapsedMilliseconds} model={model} ex={ex.GetType().Name} msg={ex.Message} jsonLen={json.Length}");
            Console.WriteLine($"[dnd] campaign-generate[{requestId}]: json={json}");
            return new CampaignGenerateResult(null, $"Failed to parse JSON response: {ex.GetType().Name} {ex.Message}", (int)sw.ElapsedMilliseconds, model);
        }
    }

    private async Task<CharacterCreateResponseDto> GenerateCharacterAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string name,
        string concept,
        string campaignMarkdown,
        CancellationToken ct)
    {
        var model = ResolveModel(context, channelState);
        var requestPrompt = BuildCreateCharacterPrompt(name, concept, campaignMarkdown);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are creating a simplified character sheet for a Discord D&D-like engine. " +
                    "Return strict JSON only. No markdown. No code fences."),
                new(StaticValues.ChatMessageRoles.User, requestPrompt)
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
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
            var parsed = JsonSerializer.Deserialize<CharacterCreateResponseDto>(json, _jsonOptions);
            if (parsed == null)
            {
                return null;
            }

            parsed.ProfileMarkdown = parsed.ProfileMarkdown?.Trim();
            parsed.Stats ??= new DndStats(10, 10, 10, 10, 10);
            parsed.MaxHp = Math.Clamp(parsed.MaxHp, 1, 200);
            parsed.MaxMp = Math.Clamp(parsed.MaxMp, 0, 200);
            return parsed;
        }
        catch
        {
            return null;
        }
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
                new(StaticValues.ChatMessageRoles.System,
                    "You are the game master. Return strict JSON only. No markdown. No code fences."),
                new(StaticValues.ChatMessageRoles.User, sb.ToString().Trim())
            }
        };

        var response = await context.OpenAILogic.CreateChatCompletionAsync(request);
        if (!response.Successful)
        {
            return null;
        }

        var content = response.Choices.FirstOrDefault()?.Message?.Content?.Trim();
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
            sb.AppendLine(TrimToLimit(campaignMarkdown, 1500));
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
            channelState.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.User, text));
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

            channelState.InstructionChat.AddMessage(new ChatMessage(StaticValues.ChatMessageRoles.Assistant, cleaned));
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

            if (msg == null || string.IsNullOrWhiteSpace(msg.Content) || string.IsNullOrWhiteSpace(msg.Role))
            {
                continue;
            }

            // Keep only user/assistant turns for follow-up corrections.
            if (!string.Equals(msg.Role, StaticValues.ChatMessageRoles.User, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(msg.Role, StaticValues.ChatMessageRoles.Assistant, StringComparison.OrdinalIgnoreCase))
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

            result.Add(new ChatMessage(msg.Role, content));
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
    }

    private sealed class PendingCampaignCreateRequest
    {
        public string CampaignName { get; set; }
        public string Prompt { get; set; }
        public DateTime RequestedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
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
}
