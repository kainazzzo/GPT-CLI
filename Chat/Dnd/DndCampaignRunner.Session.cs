namespace GPT.CLI.Chat.Dnd;

public sealed partial class DndCampaignRunner
{
    public bool IsSessionStarted => _sessionStarted;

    public DndSessionSnapshot GetSessionSnapshot() => BuildSessionSnapshot();

    public IReadOnlyList<DndSceneDefinition> ListScenes()
        => _scenes.ToList().AsReadOnly();

    public DndCampaignResult StartSession()
    {
        if (_sessionStarted)
        {
            return SessionOk("Session already in progress.");
        }

        _scenes.Clear();
        _scenes.AddRange(DndSceneCatalog.Synthesize(_templates.Values.ToList()));
        _sessionStarted = true;
        _sessionPhase = DndGamePhase.SessionStart;
        _currentSceneId = DndSceneCatalog.IntroSceneId;
        _previousPhase = DndGamePhase.NotStarted;
        _pendingCheck = null;
        _lastCheckSuccess = false;
        _lastCheckSummary = string.Empty;

        var intro = FindScene(DndSceneCatalog.IntroSceneId);
        if (intro != null)
        {
            _currentSceneId = intro.SceneId;
        }

        return SessionOk("Session started. Choose an option to continue.");
    }

    public DndCampaignResult ChooseOption(string input)
    {
        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (_sessionPhase == DndGamePhase.Complete)
        {
            return SessionError("Campaign complete");
        }

        if (_sessionPhase == DndGamePhase.Combat)
        {
            return SessionError("In combat. Attack, cast, or pass.");
        }

        if (!TryResolveOption(input, out var option, out var error))
        {
            return SessionError(error);
        }

        return ApplyOption(option);
    }

    public DndCampaignResult RequestCheck(string actorId, DndCheckStat stat, int dc, string reason)
    {
        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (_failed)
        {
            return SessionError("Campaign failed");
        }

        if (HasActiveEncounter)
        {
            return SessionError("Cannot check during an active encounter");
        }

        if (_sessionPhase is not (DndGamePhase.Exploration or DndGamePhase.Social or DndGamePhase.Travel))
        {
            return SessionError("Checks are only available during exploration, social, or travel");
        }

        if (!TryResolveCheckActor(actorId, out var member, out var actorError))
        {
            return SessionError(actorError);
        }

        var clampedDc = _rules.ClampCheckDc(dc <= 0 ? _rules.DefaultCheckDc : dc);
        EnterOverlay(DndGamePhase.Check);
        _pendingCheck = new DndPendingCheck(
            ActorId: member.ActorId,
            ActorName: member.Name,
            Stat: stat,
            Dc: clampedDc,
            Reason: string.IsNullOrWhiteSpace(reason) ? "Ability check" : reason.Trim());

        return SessionOk($"{member.Name} must roll d20{_rules.GetCheckModifier(member.Stats, stat):+#;-#;} vs DC {clampedDc} ({_pendingCheck.Reason}).");
    }

    public DndCampaignResult ResolveCheck()
    {
        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (_sessionPhase != DndGamePhase.Check || _pendingCheck == null)
        {
            return SessionError("No pending check");
        }

        if (!_party.TryGetValue(_pendingCheck.ActorId, out var member))
        {
            return SessionError("Check actor not found");
        }

        var natural = _dice.RollDie(20);
        var modifier = _rules.GetCheckModifier(member.Stats, _pendingCheck.Stat);
        var total = natural + modifier;
        var success = total >= _pendingCheck.Dc;
        _lastCheckSuccess = success;
        var modText = modifier == 0 ? string.Empty : (modifier > 0 ? $"+{modifier}" : modifier.ToString());
        _lastCheckSummary =
            $"{member.Name} rolls {natural}{modText} = {total} vs DC {_pendingCheck.Dc}: {(success ? "success" : "failure")} ({_pendingCheck.Reason}).";

        var previous = _previousPhase;
        _pendingCheck = null;
        _sessionPhase = previous == DndGamePhase.NotStarted ? DndGamePhase.Exploration : previous;
        return SessionOk(_lastCheckSummary);
    }

    public DndCampaignResult CancelCheck()
    {
        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (_sessionPhase != DndGamePhase.Check)
        {
            return SessionError("No pending check");
        }

        var previous = _previousPhase;
        _pendingCheck = null;
        _sessionPhase = previous == DndGamePhase.NotStarted ? DndGamePhase.Exploration : previous;
        return SessionOk("Check cancelled.");
    }

    public DndCampaignResult ShortRest()
    {
        if (_encounter != null && !_encounter.IsCompleted)
        {
            return SessionError("Cannot rest during an active encounter");
        }

        if (_failed)
        {
            return SessionError("Campaign failed");
        }

        if (_sessionStarted && _sessionPhase is DndGamePhase.Complete or DndGamePhase.Combat or DndGamePhase.Check)
        {
            return SessionError("Cannot short rest from the current state");
        }

        foreach (var p in _party.Values)
        {
            var missingHp = Math.Max(0, p.MaxHp - p.Hp);
            var missingMp = Math.Max(0, p.MaxMp - p.Mp);
            p.Hp = Math.Min(p.MaxHp, p.Hp + (missingHp / 2));
            p.Mp = Math.Min(p.MaxMp, p.Mp + (missingMp / 2));
        }

        if (_sessionStarted && _sessionPhase != DndGamePhase.Rest)
        {
            EnterOverlay(DndGamePhase.Rest);
        }

        return SessionOk("Party takes a short rest: half of missing HP/MP restored.");
    }

    internal static DndGamePhase PhaseForSceneKind(DndSceneKind kind)
        => kind switch
        {
            DndSceneKind.Intro => DndGamePhase.SessionStart,
            DndSceneKind.Exploration => DndGamePhase.Exploration,
            DndSceneKind.Social => DndGamePhase.Social,
            DndSceneKind.Travel => DndGamePhase.Travel,
            DndSceneKind.Combat => DndGamePhase.Combat,
            DndSceneKind.Aftermath => DndGamePhase.Aftermath,
            DndSceneKind.Finale => DndGamePhase.Complete,
            _ => DndGamePhase.Exploration
        };

    public static string FormatOptionList(IReadOnlyList<DndSceneOption> options)
    {
        if (options == null || options.Count == 0)
        {
            return "None.";
        }

        var lines = new List<string>(options.Count);
        for (var i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            if (opt == null)
            {
                continue;
            }

            lines.Add($"{i + 1}. {opt.Label}");
        }

        return string.Join("\n", lines);
    }

    private DndCampaignResult ApplyOption(DndSceneOption option)
    {
        var id = option.Id ?? string.Empty;

        if (string.Equals(id, "begin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "continue", StringComparison.OrdinalIgnoreCase))
        {
            var nextId = !string.IsNullOrWhiteSpace(option.NextSceneId)
                ? option.NextSceneId
                : CurrentScene()?.NextSceneId;
            return MoveToScene(nextId, startCombatIfNeeded: true);
        }

        if (string.Equals(id, "recap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "party", StringComparison.OrdinalIgnoreCase))
        {
            return SessionOk(string.Equals(id, "party", StringComparison.OrdinalIgnoreCase)
                ? "Party status."
                : "Session recap.");
        }

        if (id.StartsWith("check:", StringComparison.OrdinalIgnoreCase))
        {
            var stat = option.CheckStat ?? DndCheckStat.Dex;
            var dc = option.CheckDc ?? _rules.DefaultCheckDc;
            var reason = string.IsNullOrWhiteSpace(option.CheckReason) ? option.Label : option.CheckReason;
            return RequestCheck(actorId: null, stat, dc, reason);
        }

        if (string.Equals(id, "social", StringComparison.OrdinalIgnoreCase))
        {
            EnterOverlay(DndGamePhase.Social);
            return SessionOk("You turn to conversation.");
        }

        if (string.Equals(id, "travel", StringComparison.OrdinalIgnoreCase))
        {
            EnterOverlay(DndGamePhase.Travel);
            return SessionOk("You take to the road.");
        }

        if (string.Equals(id, "rest", StringComparison.OrdinalIgnoreCase))
        {
            EnterOverlay(DndGamePhase.Rest);
            return SessionOk("The party makes camp.");
        }

        if (string.Equals(id, "return", StringComparison.OrdinalIgnoreCase))
        {
            var previous = _previousPhase;
            if (previous is DndGamePhase.NotStarted or DndGamePhase.Combat or DndGamePhase.Check)
            {
                previous = DndGamePhase.Exploration;
            }

            _pendingCheck = null;
            _sessionPhase = previous;
            return SessionOk("You return to the scene.");
        }

        if (string.Equals(id, "roll", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCheck();
        }

        if (string.Equals(id, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            return CancelCheck();
        }

        if (string.Equals(id, "rest:short", StringComparison.OrdinalIgnoreCase))
        {
            return ShortRest();
        }

        if (string.Equals(id, "rest:long", StringComparison.OrdinalIgnoreCase))
        {
            return LongRest(clearFailure: _failed || _sessionPhase == DndGamePhase.Failed);
        }

        if (id.StartsWith("combat", StringComparison.OrdinalIgnoreCase))
        {
            var templateId = option.EncounterTemplateId;
            if (string.IsNullOrWhiteSpace(templateId))
            {
                templateId = CurrentScene()?.LinkedEncounterTemplateId;
            }

            if (string.IsNullOrWhiteSpace(templateId))
            {
                return SessionError("No encounter is linked to this scene");
            }

            var combatScene = _scenes.FirstOrDefault(s =>
                s.Kind == DndSceneKind.Combat &&
                string.Equals(s.LinkedEncounterTemplateId, templateId, StringComparison.OrdinalIgnoreCase));
            if (combatScene != null)
            {
                return MoveToScene(combatScene.SceneId, startCombatIfNeeded: true);
            }

            return StartEncounter(templateId);
        }

        if (string.Equals(id, "end", StringComparison.OrdinalIgnoreCase))
        {
            _sessionPhase = DndGamePhase.Complete;
            _pendingCheck = null;
            _currentSceneId = DndSceneCatalog.FinaleSceneId;
            return SessionOk("The session ends.");
        }

        return SessionError("Option is not implemented");
    }

    private DndCampaignResult MoveToScene(string sceneId, bool startCombatIfNeeded)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            _sessionPhase = DndGamePhase.Complete;
            _currentSceneId = DndSceneCatalog.FinaleSceneId;
            return SessionOk("No remaining scenes. The campaign is complete.");
        }

        var scene = FindScene(sceneId);
        if (scene == null)
        {
            return SessionError($"Scene '{sceneId}' not found");
        }

        _pendingCheck = null;
        _currentSceneId = scene.SceneId;
        _sessionPhase = PhaseForSceneKind(scene.Kind);

        if (scene.Kind == DndSceneKind.Combat && startCombatIfNeeded && !string.IsNullOrWhiteSpace(scene.LinkedEncounterTemplateId))
        {
            if (!HasActiveEncounter)
            {
                return StartEncounter(scene.LinkedEncounterTemplateId);
            }
        }

        var label = scene.Kind switch
        {
            DndSceneKind.Intro => "Session start.",
            DndSceneKind.Finale => "The campaign is complete.",
            DndSceneKind.Aftermath => string.IsNullOrWhiteSpace(scene.Summary) ? "Aftermath." : scene.Summary,
            DndSceneKind.Combat => $"Combat begins: {scene.Title}.",
            _ => string.IsNullOrWhiteSpace(scene.Summary) ? $"Scene: {scene.Title}." : scene.Summary
        };

        return SessionOk(label);
    }

    private void EnterOverlay(DndGamePhase phase)
    {
        if (_sessionPhase != phase)
        {
            _previousPhase = _sessionPhase;
        }

        _sessionPhase = phase;
    }

    private void EnterCombatFromEncounter(string templateId, List<DndCampaignLedgerEntry> newEntries)
    {
        _sessionPhase = DndGamePhase.Combat;
        _pendingCheck = null;
        var combatScene = _scenes.FirstOrDefault(s =>
            s.Kind == DndSceneKind.Combat &&
            string.Equals(s.LinkedEncounterTemplateId, templateId, StringComparison.OrdinalIgnoreCase));
        if (combatScene != null)
        {
            _currentSceneId = combatScene.SceneId;
        }

        AppendSessionMessage($"Combat started ({templateId}).", newEntries);
    }

    private void MaybeAdvanceSessionFromEncounter(DndEncounterSnapshot encounter, List<DndCampaignLedgerEntry> newEntries)
    {
        if (!_sessionStarted || encounter == null)
        {
            return;
        }

        if (!encounter.IsCompleted && encounter.Phase != DndEncounterPhase.Completed)
        {
            return;
        }

        if (_failed || string.Equals(encounter.CompletionReason, "defeat", StringComparison.OrdinalIgnoreCase))
        {
            _sessionPhase = DndGamePhase.Failed;
            _pendingCheck = null;
            AppendSessionMessage("The party has fallen.", newEntries);
            return;
        }

        if (!string.Equals(encounter.CompletionReason, "victory", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var aftermathId = !string.IsNullOrWhiteSpace(_activeTemplateId)
            ? DndSceneCatalog.AftermathSceneId(_activeTemplateId)
            : CurrentScene()?.NextSceneId;
        var aftermath = FindScene(aftermathId);
        if (aftermath != null)
        {
            _currentSceneId = aftermath.SceneId;
            _sessionPhase = DndGamePhase.Aftermath;
            _pendingCheck = null;
            AppendSessionMessage(
                string.IsNullOrWhiteSpace(aftermath.Summary) ? "Victory. Aftermath." : aftermath.Summary,
                newEntries);
            return;
        }

        _sessionPhase = DndGamePhase.Aftermath;
        _pendingCheck = null;
        AppendSessionMessage("Victory. Aftermath.", newEntries);
    }

    private bool TryResolveOption(string input, out DndSceneOption option, out string error)
    {
        option = null;
        error = string.Empty;
        var options = BuildSessionOptions();
        if (options.Count == 0)
        {
            error = "No options are available.";
            return false;
        }

        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "Option required. Legal: " + string.Join(", ", options.Select(o => o.Id));
            return false;
        }

        if (int.TryParse(raw, out var index) && index >= 1 && index <= options.Count)
        {
            option = options[index - 1];
            return true;
        }

        var exactId = options.FirstOrDefault(o => string.Equals(o.Id, raw, StringComparison.OrdinalIgnoreCase));
        if (exactId != null)
        {
            option = exactId;
            return true;
        }

        var exactLabel = options.Where(o => string.Equals(o.Label, raw, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactLabel.Count == 1)
        {
            option = exactLabel[0];
            return true;
        }

        var contains = options
            .Where(o =>
                (!string.IsNullOrWhiteSpace(o.Label) && o.Label.Contains(raw, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(o.Id) && o.Id.Contains(raw, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (contains.Count == 1)
        {
            option = contains[0];
            return true;
        }

        if (contains.Count > 1)
        {
            error = "Option is ambiguous. Legal: " + string.Join(", ", options.Select(o => o.Id));
            return false;
        }

        error = $"Unknown option '{raw}'. Legal: " + string.Join(", ", options.Select(o => o.Id));
        return false;
    }

    private bool TryResolveCheckActor(string actorId, out PartyMemberState member, out string error)
    {
        member = null;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(actorId))
        {
            if (!_party.TryGetValue(actorId, out member) || member == null)
            {
                error = "Actor not found";
                return false;
            }

            if (!member.IsAlive)
            {
                error = "Actor is not alive";
                return false;
            }

            return true;
        }

        member = _party.Values
            .Where(p => p.IsAlive)
            .OrderBy(p => p.ActorId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (member == null)
        {
            error = "No living party member";
            return false;
        }

        return true;
    }

    private DndSessionSnapshot BuildSessionSnapshot()
    {
        var scene = CurrentScene();
        return new DndSessionSnapshot(
            Started: _sessionStarted,
            Phase: _sessionPhase,
            CurrentSceneId: _currentSceneId ?? string.Empty,
            CurrentSceneTitle: scene?.Title ?? string.Empty,
            CurrentSceneSummary: scene?.Summary ?? string.Empty,
            PreviousPhase: _previousPhase,
            PendingCheck: _pendingCheck,
            LastCheckSuccess: _lastCheckSuccess,
            LastCheckSummary: _lastCheckSummary ?? string.Empty,
            Scenes: _scenes.ToList().AsReadOnly(),
            Options: BuildSessionOptions());
    }

    private IReadOnlyList<DndSceneOption> BuildSessionOptions()
    {
        if (!_sessionStarted)
        {
            return Array.Empty<DndSceneOption>();
        }

        var scene = CurrentScene();
        var linked = scene?.LinkedEncounterTemplateId ?? string.Empty;
        var nextId = scene?.NextSceneId ?? string.Empty;
        var list = new List<DndSceneOption>();

        switch (_sessionPhase)
        {
            case DndGamePhase.SessionStart:
                list.Add(new DndSceneOption("begin", "Begin the adventure", DndGamePhase.Exploration, NextSceneId: nextId));
                list.Add(new DndSceneOption("recap", "Recap the hook", DndGamePhase.SessionStart));
                list.Add(new DndSceneOption("party", "Show the party", DndGamePhase.SessionStart));
                break;

            case DndGamePhase.Exploration:
                list.Add(new DndSceneOption("check:search", "Search the area", DndGamePhase.Check, CheckStat: DndCheckStat.Dex, CheckDc: _rules.DefaultCheckDc, CheckReason: "Search the area"));
                list.Add(new DndSceneOption("social", "Talk to someone", DndGamePhase.Social));
                list.Add(new DndSceneOption("travel", "Travel on", DndGamePhase.Travel));
                list.Add(new DndSceneOption("rest", "Make camp", DndGamePhase.Rest));
                if (!string.IsNullOrWhiteSpace(linked))
                {
                    list.Add(new DndSceneOption($"combat:{linked}", "Start the fight", DndGamePhase.Combat, EncounterTemplateId: linked));
                }

                if (!string.IsNullOrWhiteSpace(nextId))
                {
                    list.Add(new DndSceneOption("continue", "Continue to the next scene", PhaseForSceneKind(FindScene(nextId)?.Kind ?? DndSceneKind.Exploration), NextSceneId: nextId));
                }

                break;

            case DndGamePhase.Social:
                list.Add(new DndSceneOption("check:persuade", "Try to persuade", DndGamePhase.Check, CheckStat: DndCheckStat.Luck, CheckDc: _rules.DefaultCheckDc, CheckReason: "Persuade"));
                list.Add(new DndSceneOption("return", "Return to exploring", DndGamePhase.Exploration));
                if (!string.IsNullOrWhiteSpace(linked))
                {
                    list.Add(new DndSceneOption($"combat:{linked}", "Start the fight", DndGamePhase.Combat, EncounterTemplateId: linked));
                }

                if (!string.IsNullOrWhiteSpace(nextId))
                {
                    list.Add(new DndSceneOption("continue", "Continue to the next scene", PhaseForSceneKind(FindScene(nextId)?.Kind ?? DndSceneKind.Exploration), NextSceneId: nextId));
                }

                break;

            case DndGamePhase.Travel:
                if (!string.IsNullOrWhiteSpace(nextId))
                {
                    list.Add(new DndSceneOption("continue", "Continue traveling", PhaseForSceneKind(FindScene(nextId)?.Kind ?? DndSceneKind.Exploration), NextSceneId: nextId));
                }

                list.Add(new DndSceneOption("check:navigate", "Navigate the route", DndGamePhase.Check, CheckStat: DndCheckStat.Dex, CheckDc: _rules.DefaultCheckDc, CheckReason: "Navigate"));
                list.Add(new DndSceneOption("rest", "Camp for the night", DndGamePhase.Rest));
                if (!string.IsNullOrWhiteSpace(linked))
                {
                    list.Add(new DndSceneOption($"combat:{linked}", "Ambush! Start the fight", DndGamePhase.Combat, EncounterTemplateId: linked));
                }

                list.Add(new DndSceneOption("return", "Turn back", DndGamePhase.Exploration));
                break;

            case DndGamePhase.Check:
                list.Add(new DndSceneOption("roll", "Roll the check", DndGamePhase.Check));
                list.Add(new DndSceneOption("cancel", "Cancel the check", _previousPhase == DndGamePhase.NotStarted ? DndGamePhase.Exploration : _previousPhase));
                break;

            case DndGamePhase.Rest:
                list.Add(new DndSceneOption("rest:short", "Take a short rest", DndGamePhase.Rest));
                list.Add(new DndSceneOption("rest:long", "Take a long rest", DndGamePhase.Rest));
                list.Add(new DndSceneOption("return", "Break camp", _previousPhase == DndGamePhase.NotStarted ? DndGamePhase.Exploration : _previousPhase));
                break;

            case DndGamePhase.Combat:
                break;

            case DndGamePhase.Aftermath:
                if (!string.IsNullOrWhiteSpace(nextId))
                {
                    list.Add(new DndSceneOption("continue", "Continue the adventure", PhaseForSceneKind(FindScene(nextId)?.Kind ?? DndSceneKind.Exploration), NextSceneId: nextId));
                }

                list.Add(new DndSceneOption("rest", "Make camp", DndGamePhase.Rest));
                list.Add(new DndSceneOption("travel", "Travel on", DndGamePhase.Travel));
                break;

            case DndGamePhase.Failed:
                list.Add(new DndSceneOption("rest:long", "Long rest and revive", DndGamePhase.Exploration));
                list.Add(new DndSceneOption("end", "End the session", DndGamePhase.Complete));
                break;

            case DndGamePhase.Complete:
                break;
        }

        return list.AsReadOnly();
    }

    private DndSceneDefinition CurrentScene() => FindScene(_currentSceneId);

    private DndSceneDefinition FindScene(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        return _scenes.FirstOrDefault(s => string.Equals(s.SceneId, sceneId, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasActiveEncounter => _encounter != null && !_encounter.IsCompleted;

    private DndCampaignResult SessionOk(string message)
    {
        var entries = new List<DndCampaignLedgerEntry>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            AppendSessionMessage(message, entries);
        }

        return new DndCampaignResult(
            Ok: true,
            Error: string.Empty,
            Campaign: BuildSnapshot(_encounter?.GetState()),
            EncounterResult: null,
            NewCampaignLedgerEntries: entries);
    }

    private DndCampaignResult SessionError(string error)
        => ErrorCampaign(error, _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());

    private void AppendSessionMessage(string message, List<DndCampaignLedgerEntry> newEntries)
    {
        var entry = new DndCampaignLedgerEntry(
            Sequence: ++_ledgerSeq,
            OccurredUtc: _clock.UtcNow,
            EncounterId: _activeEncounterId ?? string.Empty,
            EncounterName: _activeEncounterName ?? string.Empty,
            EncounterEntry: null,
            Message: message ?? string.Empty);
        _ledger.Add(entry);
        newEntries?.Add(entry);
    }
}
