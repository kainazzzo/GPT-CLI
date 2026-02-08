using System.Collections.Concurrent;
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
using OpenAI.ObjectModels.SharedModels;

namespace DndModuleExample;

public sealed class DndGameMasterModule : FeatureModuleBase
{
    public override string Id => "dnd";
    public override string Name => "D&D (Simplified)";

    private const string ModeOff = "off";
    private const string ModePrep = "prep";
    private const string ModeLive = "live";

    private const int DiscordMessageLimit = 1800;
    private const int MaxCampaignChars = 24000;

    private static readonly Regex BotMentionRegexTemplate =
        new(@"<@!?(?<id>\d+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<ulong, DndLiteChannelState> _stateByChannel = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private readonly ConcurrentDictionary<ulong, RandomDiceRoller> _diceByChannel = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public override IReadOnlyList<GptCliFunction> GetGptCliFunctions(DiscordModuleContext context)
    {
        const string dndGroupDescription = "Simplified D&D campaign + encounter controls";

        return new List<GptCliFunction>
        {
            new()
            {
                ToolName = "gptcli_set_dnd",
                ModuleId = Id,
                ExposeWhenModuleDisabled = true,
                Description = "Enable or disable the D&D module in this channel",
                Slash = new GptCliSlashBinding(GptCliSlashBindingKind.SetOption, "set", "Settings", SetOptionName: "dnd"),
                Parameters = new[] { new GptCliParamSpec("value", GptCliParamType.Boolean, "true or false", Required: true) },
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
                Description = "Create a simplified campaign (encounters + mobs) from a prompt",
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
            if (!string.Equals(dndState.Mode, ModeLive, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var content = (message.Content ?? string.Empty).Trim();
            if (content.StartsWith("!", StringComparison.Ordinal))
            {
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

            var isTagged = message.MentionedUsers.Any(u => u.Id == context.Client.CurrentUser.Id);
            if (!isTagged)
            {
                return;
            }

            // Hybrid input: when tagged, allow a tiny action-router to convert the message into a single deterministic action.
            await TryHandleTaggedNaturalActionAsync(context, channelState, message, dndState, cancellationToken);
        }
        finally
        {
            lockHandle.Release();
        }
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
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaignName = st.ActiveCampaignName ?? "default";
            var campaign = await LoadCampaignAsync(ctx.ChannelState, campaignName, ct);
            var party = await LoadPartyAsync(ctx.ChannelState, ct);

            DndCampaignSnapshot snap = null;
            if (campaign?.RunnerState != null)
            {
                var runner = RestoreCampaignRunner(ctx.ChannelState.ChannelId, campaign.RunnerState);
                snap = runner.GetState();
            }

            var sb = new StringBuilder();
            sb.AppendLine("DND status");
            sb.AppendLine($"- mode: {st.Mode}");
            sb.AppendLine($"- active campaign: {campaignName}");
            sb.AppendLine($"- templates: {campaign?.EncounterTemplates?.Count ?? 0}");
            sb.AppendLine($"- party members: {party?.PlayerUserIds?.Count ?? 0}");
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

            sb.AppendLine();
            sb.AppendLine("Live commands: `!help`, `!state`, `!targets`, `!attack <target>`, `!cast <target>`, `!pass`, `!rollall`, `!roll initiative`, `!roll <rollId>`, `!ledger [n]`");

            return new GptCliExecutionResult(true, TrimToLimit(sb.ToString().Trim(), 3500), false);
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

        var mode = NormalizeMode(modeRaw);
        TryGetStringArg(argsJson, "campaign", out var campaignName);

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var channelStateChanged = false;

            if (!string.IsNullOrWhiteSpace(campaignName))
            {
                st.ActiveCampaignName = campaignName.Trim();
            }
            st.Mode = mode;

            if (mode == ModeLive && !st.ModuleMutedBot)
            {
                st.PreviousBotMuted = ctx.ChannelState.Options.Muted;
                st.ModuleMutedBot = true;
                ctx.ChannelState.Options.Muted = true;
                channelStateChanged = true;
            }
            if (mode != ModeLive && st.ModuleMutedBot)
            {
                ctx.ChannelState.Options.Muted = st.PreviousBotMuted;
                st.ModuleMutedBot = false;
                channelStateChanged = true;
            }

            await SaveStateAsync(ctx.ChannelState, st, ct);
            return new GptCliExecutionResult(true, $"DND mode set to `{mode}` for campaign \"{st.ActiveCampaignName}\".", channelStateChanged);
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
            using var typing = DiscordTyping.Begin(ctx.Channel);
            var pkg = await GenerateCampaignPackageAsync(ctx.Context, ctx.ChannelState, campaignName.Trim(), prompt.Trim(), ct);
            if (pkg == null || string.IsNullOrWhiteSpace(pkg.CampaignMarkdown))
            {
                return new GptCliExecutionResult(true, "Campaign generation failed.", false);
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

            // Overwrite campaign doc (greenfield) and reset mechanics state.
            doc.RunnerState = null;
            await SaveCampaignAsync(ctx.ChannelState, doc, ct);

            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            st.ActiveCampaignName = doc.CampaignName;
            await SaveStateAsync(ctx.ChannelState, st, ct);

            var summary = BuildCampaignSummary(doc);
            return new GptCliExecutionResult(true, summary, true);
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

        if (!TryGetStringArg(argsJson, "name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !TryGetStringArg(argsJson, "concept", out var concept) || string.IsNullOrWhiteSpace(concept))
        {
            return new GptCliExecutionResult(true, "Provide both `name` and `concept`.", false);
        }

        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);

            using var typing = DiscordTyping.Begin(ctx.Channel);
            var created = await GenerateCharacterAsync(ctx.Context, ctx.ChannelState, name.Trim(), concept.Trim(), campaign?.CampaignMarkdown, ct);
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

            var party = await LoadPartyAsync(ctx.ChannelState, ct) ?? new DndLitePartyDocument();
            if (!party.PlayerUserIds.Contains(ctx.User.Id))
            {
                party.PlayerUserIds.Add(ctx.User.Id);
                await SavePartyAsync(ctx.ChannelState, party, ct);
            }

            // If campaign already has a runner state, inject the new party member (only when no active encounter).
            if (campaign?.RunnerState != null && campaign.RunnerState.ActiveEncounter != null && campaign.RunnerState.ActiveEncounter.Completed == false)
            {
                // Active encounter: don't attempt to merge party membership.
            }
            else if (campaign != null)
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
                campaign.UpdatedUtc = DateTime.UtcNow;
                await SaveCampaignAsync(ctx.ChannelState, campaign, ct);
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

    private async Task<GptCliExecutionResult> ExecuteEncounterListAsync(GptCliExecutionContext ctx, string argsJson, CancellationToken ct)
    {
        var lockHandle = _channelLocks.GetOrAdd(ctx.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await lockHandle.WaitAsync(ct);
        try
        {
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            var templates = campaign?.EncounterTemplates ?? new List<DndLiteEncounterTemplateDocument>();
            if (templates.Count == 0)
            {
                return new GptCliExecutionResult(true, "No encounter templates found. Create a campaign first.", false);
            }

            var lines = new List<string> { $"Encounters for \"{campaign.CampaignName}\":" };
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
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign == null)
            {
                return new GptCliExecutionResult(true, "No campaign found. Create one with `/gptcli dnd campaigncreate`.", false);
            }

            var party = await LoadPartyAsync(ctx.ChannelState, ct);
            if (party?.PlayerUserIds == null || party.PlayerUserIds.Count == 0)
            {
                return new GptCliExecutionResult(true, "No party members yet. Create a character with `/gptcli dnd charactercreate`.", false);
            }

            var runner = await LoadOrCreateRunnerAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (runner == null)
            {
                return new GptCliExecutionResult(true, "Unable to create campaign runner (missing party profiles).", false);
            }

            var res = runner.StartEncounter(templateId);
            await PersistRunnerAsync(ctx.ChannelState, st.ActiveCampaignName, campaign, runner, ct);

            // Announce in-channel (best effort) and return ephemeral response text.
            var text = RenderTurnResult(res);
            try { await SendChunkedAsync(ctx.Channel, text); } catch { }
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
            var st = await GetOrLoadStateAsync(ctx.ChannelState, ct);
            var campaign = await LoadCampaignAsync(ctx.ChannelState, st.ActiveCampaignName, ct);
            if (campaign?.RunnerState == null)
            {
                return new GptCliExecutionResult(true, "No campaign runner state.", false);
            }

            // Just clear active encounter; mechanics state is persisted in runner state.
            campaign.RunnerState.ActiveEncounter = null;
            campaign.RunnerState.ActiveEncounterId = string.Empty;
            campaign.RunnerState.ActiveEncounterName = string.Empty;
            await SaveCampaignAsync(ctx.ChannelState, campaign, ct);

            return new GptCliExecutionResult(true, "Encounter cleared.", true);
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
            return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.RollAll();
                return (res, RenderTurnResult(res));
            }, ct);
        }

        if (content.StartsWith("!roll ", StringComparison.OrdinalIgnoreCase))
        {
            var tail = content[6..].Trim();
            if (string.Equals(tail, "initiative", StringComparison.OrdinalIgnoreCase))
            {
                var actorId = ToActorId(message.Author.Id);
                return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
                {
                    var res = runner.RollInitiative(actorId);
                    return (res, RenderTurnResult(res));
                }, ct);
            }

            var rollId = tail.Trim();
            return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
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
            }, ct);
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
            return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.Attack(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, ct);
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
            return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var targetId = ResolveTargetActorId(await GetActiveEncounterSnapshotAsync(channelState, dndState.ActiveCampaignName, ct), tail, out var err);
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return (null, err ?? "Target not found.");
                }

                var res = runner.CastSpell(actorId, targetId);
                return (res, RenderTurnResult(res));
            }, ct);
        }

        if (string.Equals(content, "!pass", StringComparison.OrdinalIgnoreCase))
        {
            var actorId = ToActorId(message.Author.Id);
            return await RunEncounterActionAsync(channelState, dndState, message.Channel, async runner =>
            {
                var res = runner.Pass(actorId);
                return (res, RenderTurnResult(res));
            }, ct);
        }

        return (false, false);
    }

    private async Task TryHandleTaggedNaturalActionAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        SocketMessage message,
        DndLiteChannelState dndState,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign?.RunnerState == null)
        {
            return;
        }

        var runner = RestoreCampaignRunner(channelState.ChannelId, campaign.RunnerState);
        var snap = runner.GetState();
        if (snap.ActiveEncounterState == null || snap.ActiveEncounterState.IsCompleted)
        {
            return;
        }

        var pending = campaign.RunnerState.ActiveEncounter?.PendingRolls ?? new List<DndPendingRoll>();
        var next = DndTurnResult.BuildNextRequest(snap.ActiveEncounterState, pending);
        if (next.Kind != DndNextRequestKind.NeedAction)
        {
            return;
        }

        var actorId = ToActorId(message.Author.Id);
        if (!string.Equals(snap.ActiveEncounterState.CurrentActorId, actorId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var decision = await RouteTaggedActionAsync(context, channelState, message, snap.ActiveEncounterState, ct);
        if (decision == null)
        {
            return;
        }

        if (string.Equals(decision.ToolName, "dnd_rollall", StringComparison.OrdinalIgnoreCase))
        {
            await TryHandleBangCommandAsync(context, channelState, message, dndState, "!rollall", ct);
            return;
        }
        if (string.Equals(decision.ToolName, "dnd_pass", StringComparison.OrdinalIgnoreCase))
        {
            await TryHandleBangCommandAsync(context, channelState, message, dndState, "!pass", ct);
            return;
        }
        if (string.Equals(decision.ToolName, "dnd_attack", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetStringArg(decision.ArgumentsJson, "target", out var target) && !string.IsNullOrWhiteSpace(target))
            {
                await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!attack {target}", ct);
            }
            return;
        }
        if (string.Equals(decision.ToolName, "dnd_cast", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetStringArg(decision.ArgumentsJson, "target", out var target) && !string.IsNullOrWhiteSpace(target))
            {
                await TryHandleBangCommandAsync(context, channelState, message, dndState, $"!cast {target}", ct);
            }
            return;
        }
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

    private async Task<(bool handled, bool stateChanged)> RunEncounterActionAsync(
        InstructionGPT.ChannelState channelState,
        DndLiteChannelState dndState,
        IMessageChannel discordChannel,
        Func<DndCampaignRunner, Task<(DndCampaignResult result, string responseText)>> action,
        CancellationToken ct)
    {
        var campaign = await LoadCampaignAsync(channelState, dndState.ActiveCampaignName, ct);
        if (campaign == null)
        {
            await discordChannel.SendMessageAsync("No campaign found. Create one with `/gptcli dnd campaigncreate`.");
            return (true, false);
        }

        var runner = await LoadOrCreateRunnerAsync(channelState, dndState.ActiveCampaignName, ct);
        if (runner == null)
        {
            await discordChannel.SendMessageAsync("No party runner available. Create a character with `/gptcli dnd charactercreate`.");
            return (true, false);
        }

        var (res, responseText) = await action(runner);
        if (res == null)
        {
            await discordChannel.SendMessageAsync(TrimToLimit(responseText ?? "error", 1800));
            return (true, false);
        }

        await PersistRunnerAsync(channelState, dndState.ActiveCampaignName, campaign, runner, ct);
        await SendChunkedAsync(discordChannel, responseText);
        return (true, true);
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

        campaign.RunnerState = runner.ToState();
        campaign.UpdatedUtc = DateTime.UtcNow;
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
        var partyDoc = await LoadPartyAsync(channelState, ct);
        if (partyDoc?.PlayerUserIds == null || partyDoc.PlayerUserIds.Count == 0)
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
        campaign.UpdatedUtc = DateTime.UtcNow;
        await SaveCampaignAsync(channelState, campaign, ct);
        return runner;
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
            "DND simplified live gameplay\n" +
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

    private async Task<CampaignCreateResponseDto> GenerateCampaignPackageAsync(
        DiscordModuleContext context,
        InstructionGPT.ChannelState channelState,
        string campaignName,
        string prompt,
        CancellationToken ct)
    {
        var model = ResolveModel(context, channelState);
        var requestPrompt = BuildCreateCampaignPrompt(campaignName, prompt);

        var request = new ChatCompletionCreateRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new(StaticValues.ChatMessageRoles.System,
                    "You are a campaign bootstrapper for a simplified D&D-like Discord engine. " +
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
            var parsed = JsonSerializer.Deserialize<CampaignCreateResponseDto>(json, _jsonOptions);
            if (parsed == null)
            {
                return null;
            }

            parsed.CampaignMarkdown = parsed.CampaignMarkdown?.Trim();
            parsed.Encounters ??= new List<EncounterTemplateDto>();
            if (parsed.Encounters.Count > 12)
            {
                parsed.Encounters = parsed.Encounters.Take(12).ToList();
            }

            return parsed;
        }
        catch
        {
            return null;
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

    private static string BuildCreateCampaignPrompt(string campaignName, string prompt)
    {
        return
            "Return a single JSON object with keys exactly: campaignMarkdown, encounters.\n" +
            "campaignMarkdown: markdown string describing the campaign premise, tone, and 3-6 bullet hooks.\n" +
            "encounters: array of 3-8 encounter templates.\n\n" +
            "Encounter template schema:\n" +
            "{\n" +
            "  \"templateId\": \"string\",\n" +
            "  \"name\": \"string\",\n" +
            "  \"scene\": \"string\",\n" +
            "  \"rewards\": \"string\",\n" +
            "  \"boss\": {\"id\":\"string\",\"name\":\"string\",\"description\":\"string\",\"maxHp\":40,\"maxMp\":10,\"stats\":{\"str\":12,\"def\":12,\"dex\":12,\"spellPower\":12,\"luck\":10}},\n" +
            "  \"adds\": [{\"id\":\"string\",\"name\":\"string\",\"description\":\"string\",\"maxHp\":12,\"maxMp\":0,\"stats\":{\"str\":10,\"def\":10,\"dex\":10,\"spellPower\":10,\"luck\":10}}]\n" +
            "}\n\n" +
            "Constraints:\n" +
            "- Keep stats simple and balanced for a small party.\n" +
            "- maxHp: 1-250, maxMp: 0-100\n" +
            "- stats range: 6-20\n" +
            "- adds: 0-6\n" +
            "- All ids should be short slugs.\n\n" +
            $"Campaign name: {campaignName}\n" +
            "Prompt:\n" +
            prompt;
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

        try
        {
            var doc = JsonSerializer.Deserialize<DndLiteCampaignDocument>(json, _jsonOptions);
            if (doc == null)
            {
                return null;
            }

            doc.CampaignName ??= campaignName;
            doc.CampaignMarkdown ??= string.Empty;
            doc.EncounterTemplates ??= new List<DndLiteEncounterTemplateDocument>();
            return doc;
        }
        catch
        {
            return null;
        }
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
        doc.UpdatedUtc = doc.UpdatedUtc == default ? DateTime.UtcNow : doc.UpdatedUtc;

        var path = ResolveCampaignPath(channelState, doc.CampaignName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(doc, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<DndLitePartyDocument> LoadPartyAsync(InstructionGPT.ChannelState channelState, CancellationToken ct)
    {
        var path = ResolvePartyPath(channelState);
        if (!File.Exists(path))
        {
            return new DndLitePartyDocument();
        }

        var json = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DndLitePartyDocument();
        }

        try
        {
            var doc = JsonSerializer.Deserialize<DndLitePartyDocument>(json, _jsonOptions) ?? new DndLitePartyDocument();
            doc.PlayerUserIds ??= new List<ulong>();
            return doc;
        }
        catch
        {
            return new DndLitePartyDocument();
        }
    }

    private async Task SavePartyAsync(InstructionGPT.ChannelState channelState, DndLitePartyDocument party, CancellationToken ct)
    {
        party ??= new DndLitePartyDocument();
        party.PlayerUserIds ??= new List<ulong>();
        var path = ResolvePartyPath(channelState);
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

    private static string ResolveModel(DiscordModuleContext context, InstructionGPT.ChannelState channelState)
    {
        return channelState?.InstructionChat?.ChatBotState?.Parameters?.Model
               ?? context.DefaultParameters?.Model
               ?? "gpt-4o-mini";
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

    private static void NormalizeState(DndLiteChannelState st)
    {
        st.Mode = NormalizeMode(st.Mode);
        st.ActiveCampaignName = string.IsNullOrWhiteSpace(st.ActiveCampaignName) ? "default" : st.ActiveCampaignName.Trim();
    }

    private static string ToActorId(ulong userId) => $"u:{userId}";

    private static string GetLiteRootDirectory(InstructionGPT.ChannelState channelState)
    {
        var channelDir = InstructionGPT.GetChannelDirectory(channelState);
        return Path.Combine(channelDir, "dnd-lite");
    }

    private static string ResolveStatePath(InstructionGPT.ChannelState channelState)
        => Path.Combine(GetLiteRootDirectory(channelState), "state.json");

    private static string ResolvePartyPath(InstructionGPT.ChannelState channelState)
        => Path.Combine(GetLiteRootDirectory(channelState), "party.json");

    private static string ResolveCampaignPath(InstructionGPT.ChannelState channelState, string campaignName)
    {
        var safe = SlugifySegment(campaignName);
        return Path.Combine(GetLiteRootDirectory(channelState), "campaigns", safe, "campaign.json");
    }

    private static string ResolvePcProfilePath(InstructionGPT.ChannelState channelState, ulong userId)
        => Path.Combine(GetLiteRootDirectory(channelState), "profiles", "pcs", $"{userId}.json");

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

        return value[..maxChars] + "\n...[truncated]";
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
    }

    private sealed class DndLitePartyDocument
    {
        public List<ulong> PlayerUserIds { get; set; } = new();
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

    private sealed class DndLiteCampaignDocument
    {
        public string CampaignName { get; set; }
        public string CampaignMarkdown { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public List<DndLiteEncounterTemplateDocument> EncounterTemplates { get; set; } = new();
        public DndCampaignRunnerState RunnerState { get; set; }
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
}
