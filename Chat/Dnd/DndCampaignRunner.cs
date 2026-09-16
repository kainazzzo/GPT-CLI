namespace GPT.CLI.Chat.Dnd;

public sealed partial class DndCampaignRunner
{
    private sealed class PartyMemberState
    {
        public PartyMemberState(DndCampaignPartyMember member)
        {
            ActorId = member.ActorId;
            Name = member.Name;
            Stats = member.Stats;
            MaxHp = member.MaxHp;
            Hp = Math.Clamp(member.Hp, 0, member.MaxHp);
            MaxMp = member.MaxMp;
            Mp = Math.Clamp(member.Mp, 0, member.MaxMp);
        }

        public string ActorId { get; }
        public string Name { get; }
        public DndStats Stats { get; }
        public int MaxHp { get; }
        public int Hp { get; set; }
        public int MaxMp { get; }
        public int Mp { get; set; }
        public bool IsAlive => Hp > 0;
    }

    private readonly DndRuleset _rules;
    private readonly IDiceRoller _dice;
    private readonly IClock _clock;

    private readonly Dictionary<string, PartyMemberState> _party = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DndEncounterTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DndCampaignLedgerEntry> _ledger = new();
    private int _ledgerSeq;
    private int _encounterSeq;

    private bool _failed;
    private string _failureReason = string.Empty;

    private DndEncounterRunner _encounter;
    private string _activeEncounterId = string.Empty;
    private string _activeEncounterName = string.Empty;
    private string _activeTemplateId = string.Empty;

    private bool _sessionStarted;
    private DndGamePhase _sessionPhase = DndGamePhase.NotStarted;
    private string _currentSceneId = string.Empty;
    private DndGamePhase _previousPhase = DndGamePhase.NotStarted;
    private DndPendingCheck _pendingCheck;
    private bool _lastCheckSuccess;
    private string _lastCheckSummary = string.Empty;
    private bool _sceneBeatResolved;
    private readonly List<DndSceneDefinition> _scenes = new();
    private readonly DndTableRound _sessionRound = new();

    public DndCampaignRunner(
        DndCampaignDefinition definition,
        DndRuleset ruleset = null,
        IDiceRoller diceRoller = null,
        IClock clock = null)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        _rules = ruleset ?? DndRuleset.Default;
        _dice = diceRoller ?? new RandomDiceRoller();
        _clock = clock ?? new SystemClock();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in definition.Party ?? Array.Empty<DndCampaignPartyMember>())
        {
            AddValidatedMember(m, seen, allowDuplicate: false);
        }
    }

    private void AddValidatedMember(DndCampaignPartyMember m, HashSet<string> seen, bool allowDuplicate)
    {
        if (m == null)
        {
            throw new ArgumentException("Null party member");
        }

        if (string.IsNullOrWhiteSpace(m.ActorId))
        {
            throw new ArgumentException("Party member ActorId required");
        }

        if (!seen.Add(m.ActorId) && !allowDuplicate)
        {
            throw new ArgumentException($"Duplicate party member ActorId '{m.ActorId}'");
        }

        if (m.MaxHp <= 0)
        {
            throw new ArgumentException($"MaxHp must be > 0 for '{m.ActorId}'");
        }

        if (m.MaxMp < 0)
        {
            throw new ArgumentException($"MaxMp must be >= 0 for '{m.ActorId}'");
        }

        _party[m.ActorId] = new PartyMemberState(m);
        _sessionRound.Seat(m.ActorId);
    }

    public DndCampaignSnapshot GetState() => BuildSnapshot(activeEncounterState: _encounter?.GetState());

    public IReadOnlyList<DndCampaignLedgerEntry> GetLedger(int? lastN = null)
    {
        if (!lastN.HasValue)
        {
            return _ledger.ToList().AsReadOnly();
        }

        var take = Math.Clamp(lastN.Value, 1, 500);
        return _ledger.TakeLast(take).ToList().AsReadOnly();
    }

    public void RegisterEncounterTemplate(DndEncounterTemplate template)
    {
        if (template == null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (string.IsNullOrWhiteSpace(template.TemplateId))
        {
            throw new ArgumentException("TemplateId required", nameof(template));
        }

        if (_templates.ContainsKey(template.TemplateId))
        {
            throw new InvalidOperationException($"Encounter template already exists: '{template.TemplateId}'.");
        }

        _templates[template.TemplateId] = template;
    }

    public IReadOnlyList<DndEncounterTemplate> ListEncounterTemplates()
        => _templates.Values.OrderBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();

    public DndCampaignRunnerState ToState()
    {
        var party = _party.Values.Select(p => new DndCampaignPartyMember(
            ActorId: p.ActorId,
            Name: p.Name,
            Stats: p.Stats,
            MaxHp: p.MaxHp,
            Hp: p.Hp,
            MaxMp: p.MaxMp,
            Mp: p.Mp)).ToList();

        return new DndCampaignRunnerState
        {
            Party = party,
            Templates = _templates.Values.ToList(),
            Ledger = _ledger.ToList(),
            LedgerSeq = _ledgerSeq,
            EncounterSeq = _encounterSeq,
            Failed = _failed,
            FailureReason = _failureReason ?? string.Empty,
            ActiveEncounterId = _activeEncounterId ?? string.Empty,
            ActiveEncounterName = _activeEncounterName ?? string.Empty,
            ActiveTemplateId = _activeTemplateId ?? string.Empty,
            ActiveEncounter = _encounter?.ToState(),
            Session = new DndSessionRunnerState
            {
                Started = _sessionStarted,
                Phase = _sessionPhase,
                CurrentSceneId = _currentSceneId ?? string.Empty,
                PreviousPhase = _previousPhase,
                PendingCheck = _pendingCheck,
                LastCheckSuccess = _lastCheckSuccess,
                LastCheckSummary = _lastCheckSummary ?? string.Empty,
                SceneBeatResolved = _sceneBeatResolved,
                Scenes = _scenes.ToList(),
                TableRound = _sessionRound.ToState()
            }
        };
    }

    public static DndCampaignRunner FromState(
        DndCampaignRunnerState state,
        DndRuleset ruleset = null,
        IDiceRoller diceRoller = null,
        IClock clock = null)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var def = new DndCampaignDefinition((state.Party ?? new List<DndCampaignPartyMember>()).ToList());
        var runner = new DndCampaignRunner(def, ruleset, diceRoller, clock);

        // Templates.
        runner._templates.Clear();
        foreach (var t in state.Templates ?? new List<DndEncounterTemplate>())
        {
            if (t == null || string.IsNullOrWhiteSpace(t.TemplateId))
            {
                continue;
            }

            runner._templates[t.TemplateId] = t;
        }

        // Ledger and counters.
        runner._ledger.Clear();
        if (state.Ledger is { Count: > 0 })
        {
            runner._ledger.AddRange(state.Ledger.Where(e => e != null));
        }
        runner._ledgerSeq = state.LedgerSeq;
        runner._encounterSeq = state.EncounterSeq;

        runner._failed = state.Failed;
        runner._failureReason = state.FailureReason ?? string.Empty;
        runner._activeEncounterId = state.ActiveEncounterId ?? string.Empty;
        runner._activeEncounterName = state.ActiveEncounterName ?? string.Empty;
        runner._activeTemplateId = state.ActiveTemplateId ?? string.Empty;

        // Active encounter.
        runner._encounter = state.ActiveEncounter == null
            ? null
            : DndEncounterRunner.FromState(state.ActiveEncounter, runner._rules, runner._dice, runner._clock);

        var session = state.Session;
        if (session != null)
        {
            runner._sessionStarted = session.Started;
            runner._sessionPhase = session.Phase;
            runner._currentSceneId = session.CurrentSceneId ?? string.Empty;
            runner._previousPhase = session.PreviousPhase;
            runner._pendingCheck = session.PendingCheck;
            runner._lastCheckSuccess = session.LastCheckSuccess;
            runner._lastCheckSummary = session.LastCheckSummary ?? string.Empty;
            runner._sceneBeatResolved = session.SceneBeatResolved;
            runner._scenes.Clear();
            if (session.Scenes is { Count: > 0 })
            {
                runner._scenes.AddRange(session.Scenes.Where(s => s != null && !string.IsNullOrWhiteSpace(s.SceneId)));
            }

            if (session.TableRound != null)
            {
                runner._sessionRound.LoadFrom(session.TableRound);
            }
        }

        return runner;
    }

    public DndCampaignResult JoinParty(DndCampaignPartyMember member)
    {
        if (member == null || string.IsNullOrWhiteSpace(member.ActorId))
        {
            return ErrorCampaign("Party member required", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        try
        {
            if (_party.ContainsKey(member.ActorId))
            {
                _sessionRound.Seat(member.ActorId, joinedThisRound: true);
            }
            else
            {
                AddValidatedMember(member, new HashSet<string>(_party.Keys, StringComparer.OrdinalIgnoreCase), allowDuplicate: true);
                _sessionRound.Seat(member.ActorId, joinedThisRound: true);
            }
        }
        catch (ArgumentException ex)
        {
            return ErrorCampaign(ex.Message, _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        DndTurnResult encounterResult = null;
        if (HasActiveEncounter && _sessionPhase != DndGamePhase.PartyFormation)
        {
            var p = _party[member.ActorId];
            encounterResult = _encounter.AddPartyActor(new DndActorDefinition(
                ActorId: p.ActorId,
                Name: p.Name,
                Side: DndSide.Party,
                IsBoss: false,
                Stats: p.Stats,
                MaxHp: p.MaxHp,
                MaxMp: p.MaxMp,
                StartingHp: p.Hp,
                StartingMp: p.Mp));
            if (encounterResult != null)
            {
                ReconcilePartyFromSnapshot(encounterResult.State);
            }
        }

        var entries = new List<DndCampaignLedgerEntry>();
        AppendSessionMessage($"{member.Name} sits down at the table.", entries);
        return BuildCampaignResult(encounterResult, entries);
    }

    public DndCampaignResult BeginPlayFromFormation()
    {
        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (_sessionPhase != DndGamePhase.PartyFormation)
        {
            return SessionError("Party is not forming");
        }

        if (!HasSeatedPc())
        {
            return SessionError("At least one player must join before play can start");
        }

        var resume = _previousPhase;
        if (resume is DndGamePhase.NotStarted or DndGamePhase.PartyFormation)
        {
            _sessionPhase = DndGamePhase.SessionStart;
        }
        else
        {
            _sessionPhase = resume;
        }

        _sessionRound.ClearRoundFlags();
        foreach (var id in SeatedActorIds())
        {
            _sessionRound.Participating.Add(id);
        }

        return SessionOk("The party is assembled. Play is open.");
    }

    public DndCampaignResult Ready(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return SessionError("actorId required");
        }

        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            if (!_party.ContainsKey(actorId) || _sessionRound.SittingOut.Contains(actorId))
            {
                return SessionError("Join the party first");
            }

            return BeginPlayFromFormation();
        }

        if (HasActiveEncounter)
        {
            return RunEncounterStep(() => _encounter.Ready(actorId));
        }

        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (!_sessionRound.ActedThisRound.Contains(actorId))
        {
            return SessionError("Act first, then you can skip the rest of the round");
        }

        _sessionRound.ReadyThisRound.Add(actorId);
        var entries = new List<DndCampaignLedgerEntry>();
        AppendSessionMessage($"{ActorDisplayName(actorId)} is done for this round.", entries);
        if (SessionReadyQuorumMet())
        {
            EndSessionTableRound(entries);
        }

        return new DndCampaignResult(true, string.Empty, BuildSnapshot(_encounter?.GetState()), null, entries);
    }

    public DndCampaignResult EndPartyRound()
    {
        if (HasActiveEncounter)
        {
            return RunEncounterStep(() => _encounter.EndPartyRound());
        }

        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        var entries = new List<DndCampaignLedgerEntry>();
        EndSessionTableRound(entries);
        return new DndCampaignResult(true, string.Empty, BuildSnapshot(_encounter?.GetState()), null, entries);
    }

    public DndCampaignResult TimeoutIdle()
    {
        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            return SessionOk("The table is still forming.");
        }

        if (HasActiveEncounter)
        {
            return RunEncounterStep(() => _encounter.EndPartyRound());
        }

        var entries = new List<DndCampaignLedgerEntry>();
        EndSessionTableRound(entries);
        return new DndCampaignResult(true, string.Empty, BuildSnapshot(_encounter?.GetState()), null, entries);
    }

    public DndCampaignResult StartEncounter(string templateId)
    {
        if (_failed)
        {
            return ErrorCampaign("Campaign failed", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        if (_encounter != null && !_encounter.IsCompleted)
        {
            return ErrorCampaign("Encounter already active", _encounter.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        if (_sessionStarted && _sessionPhase == DndGamePhase.PartyFormation)
        {
            return ErrorCampaign("Party is still forming", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        if (!HasSeatedPc())
        {
            return ErrorCampaign("No seated party members", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            return ErrorCampaign("templateId required", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        if (!_templates.TryGetValue(templateId, out var template) || template == null)
        {
            return ErrorCampaign("Encounter template not found", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        var encounterId = $"enc-{++_encounterSeq:D6}";
        _activeEncounterId = encounterId;
        _activeEncounterName = template.Name ?? template.TemplateId;
        _activeTemplateId = template.TemplateId;

        var partyDefs = _party.Values
            .Where(p => !_sessionRound.SittingOut.Contains(p.ActorId))
            .Select(p => new DndActorDefinition(
                ActorId: p.ActorId,
                Name: p.Name,
                Side: DndSide.Party,
                IsBoss: false,
                Stats: p.Stats,
                MaxHp: p.MaxHp,
                MaxMp: p.MaxMp,
                StartingHp: p.Hp,
                StartingMp: p.Mp))
            .ToList();
        if (partyDefs.Count == 0)
        {
            return ErrorCampaign("No seated party members", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        var boss = NormalizeEnemy(template.Boss, isBoss: true);
        var adds = (template.Adds ?? Array.Empty<DndActorDefinition>()).Select(a => NormalizeEnemy(a, isBoss: false)).ToList();

        var def = new DndEncounterDefinition(partyDefs, boss, adds);
        _encounter = new DndEncounterRunner(def, _rules, _dice, _clock);
        var res = _encounter.StartEncounter();

        var newCampaignEntries = AppendEncounterEntries(res, encounterId, _activeEncounterName).ToList();
        ReconcilePartyFromSnapshot(res.State);
        if (_sessionStarted)
        {
            EnterCombatFromEncounter(template.TemplateId, newCampaignEntries);
        }
        MaybeFailCampaignFromEncounter(res.State, newCampaignEntries);
        return BuildCampaignResult(res, newCampaignEntries);
    }

    public DndCampaignResult Attack(string actorId, string targetId)
    {
        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            return ErrorCampaign("Party is still forming", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        return RunEncounterStep(() => _encounter.DeclareAttack(actorId, targetId));
    }

    public DndCampaignResult CastSpell(string actorId, string targetId)
    {
        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            return ErrorCampaign("Party is still forming", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        return RunEncounterStep(() => _encounter.DeclareCastSpell(actorId, targetId));
    }

    public DndCampaignResult Pass(string actorId)
    {
        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            return ErrorCampaign("Party is still forming", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        if (HasActiveEncounter)
        {
            return RunEncounterStep(() => _encounter.Pass(actorId));
        }

        if (!_sessionStarted)
        {
            return SessionError("Session not started");
        }

        if (!TryConsumeSessionAction(actorId, out var error))
        {
            return SessionError(error);
        }

        _sessionRound.MarkActed(actorId);
        MaybeEndSessionRoundIfPartyActed();
        return SessionOk($"{ActorDisplayName(actorId)} waits.");
    }
    public DndCampaignResult RollAll() => RunEncounterStep(() => _encounter.RollAll());
    public DndCampaignResult RollInitiative(string actorId) => RunEncounterStep(() => _encounter.RollInitiative(actorId));
    public DndCampaignResult RollAttack(string rollId) => RunEncounterStep(() => _encounter.RollAttack(rollId));
    public DndCampaignResult RollDamage(string rollId) => RunEncounterStep(() => _encounter.RollDamage(rollId));
    public DndCampaignResult RollSpellAttack(string rollId) => RunEncounterStep(() => _encounter.RollSpellAttack(rollId));
    public DndCampaignResult RollSpellDamage(string rollId) => RunEncounterStep(() => _encounter.RollSpellDamage(rollId));

    public DndCampaignResult LongRest(bool clearFailure = false)
    {
        if (_sessionStarted && _sessionPhase == DndGamePhase.PartyFormation && !clearFailure)
        {
            return ErrorCampaign("Party is still forming", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        if (_encounter != null && !_encounter.IsCompleted)
        {
            return ErrorCampaign("Cannot long rest during an active encounter", _encounter.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        foreach (var p in _party.Values)
        {
            p.Hp = p.MaxHp;
            p.Mp = p.MaxMp;
        }

        if (clearFailure)
        {
            _failed = false;
            _failureReason = string.Empty;
            if (_sessionStarted && _sessionPhase == DndGamePhase.Failed)
            {
                _pendingCheck = null;
                var retryId = !string.IsNullOrWhiteSpace(_activeTemplateId)
                    ? DndSceneCatalog.ApproachSceneId(_activeTemplateId)
                    : DndSceneCatalog.IntroSceneId;
                var scene = FindScene(retryId) ?? FindScene(DndSceneCatalog.IntroSceneId);
                _currentSceneId = scene?.SceneId ?? DndSceneCatalog.IntroSceneId;
                _sessionPhase = scene == null || scene.Kind == DndSceneKind.Intro
                    ? DndGamePhase.SessionStart
                    : DndGamePhase.Exploration;
            }
        }
        else if (_sessionStarted && _sessionPhase != DndGamePhase.Failed && _sessionPhase != DndGamePhase.Complete)
        {
            // Stay in Rest if already there; otherwise this is a direct API long rest.
            if (_sessionPhase != DndGamePhase.Rest)
            {
                EnterOverlay(DndGamePhase.Rest);
            }
        }

        var msg = clearFailure
            ? "Party takes a long rest: HP/MP restored. Campaign failure cleared."
            : "Party takes a long rest: HP/MP restored.";

        var entry = new DndCampaignLedgerEntry(
            Sequence: ++_ledgerSeq,
            OccurredUtc: _clock.UtcNow,
            EncounterId: _activeEncounterId ?? string.Empty,
            EncounterName: _activeEncounterName ?? string.Empty,
            EncounterEntry: null,
            Message: msg);
        _ledger.Add(entry);

        return new DndCampaignResult(
            Ok: true,
            Error: string.Empty,
            Campaign: BuildSnapshot(activeEncounterState: _encounter?.GetState()),
            EncounterResult: null,
            NewCampaignLedgerEntries: new[] { entry });
    }

    private DndCampaignResult RunEncounterStep(Func<DndTurnResult> step)
    {
        if (_failed)
        {
            return ErrorCampaign("Campaign failed", _encounter?.GetState(), Array.Empty<DndCampaignLedgerEntry>());
        }

        if (_encounter == null)
        {
            return ErrorCampaign("No active encounter", null, Array.Empty<DndCampaignLedgerEntry>());
        }

        var res = step();
        var newCampaignEntries = AppendEncounterEntries(res, _activeEncounterId, _activeEncounterName).ToList();

        if (res != null)
        {
            ReconcilePartyFromSnapshot(res.State);
            MaybeFailCampaignFromEncounter(res.State, newCampaignEntries);
            MaybeAdvanceSessionFromEncounter(res.State, newCampaignEntries);
            MaybeEnterFormationFromEmptyTable();
        }

        // If encounter finished, leave it instantiated but inert; next encounter requires StartEncounter().
        return BuildCampaignResult(res, newCampaignEntries);
    }

    private DndCampaignResult BuildCampaignResult(DndTurnResult encounterResult, IReadOnlyList<DndCampaignLedgerEntry> newEntries)
    {
        var snap = BuildSnapshot(activeEncounterState: _encounter?.GetState());
        return new DndCampaignResult(
            Ok: encounterResult == null ? true : encounterResult.Ok,
            Error: encounterResult == null ? string.Empty : encounterResult.Error,
            Campaign: snap,
            EncounterResult: encounterResult,
            NewCampaignLedgerEntries: newEntries ?? Array.Empty<DndCampaignLedgerEntry>());
    }

    private DndCampaignResult ErrorCampaign(string error, DndEncounterSnapshot encounterState, IReadOnlyList<DndCampaignLedgerEntry> newEntries)
    {
        var snap = BuildSnapshot(encounterState);
        return new DndCampaignResult(
            Ok: false,
            Error: error ?? "error",
            Campaign: snap,
            EncounterResult: null,
            NewCampaignLedgerEntries: newEntries ?? Array.Empty<DndCampaignLedgerEntry>());
    }

    private DndActorDefinition NormalizeEnemy(DndActorDefinition enemy, bool isBoss)
    {
        if (enemy == null)
        {
            throw new ArgumentException("Enemy required");
        }

        // Ensure enemies spawn at full HP/MP for each encounter.
        return enemy with
        {
            Side = DndSide.Enemy,
            IsBoss = isBoss,
            StartingHp = enemy.MaxHp,
            StartingMp = enemy.MaxMp
        };
    }

    private IReadOnlyList<DndCampaignLedgerEntry> AppendEncounterEntries(DndTurnResult res, string encounterId, string encounterName)
    {
        if (res == null || res.NewLedgerEntries == null || res.NewLedgerEntries.Count == 0)
        {
            return Array.Empty<DndCampaignLedgerEntry>();
        }

        var list = new List<DndCampaignLedgerEntry>(res.NewLedgerEntries.Count);
        foreach (var e in res.NewLedgerEntries)
        {
            var msg = string.IsNullOrWhiteSpace(encounterName)
                ? e.Message
                : $"[{encounterName}] {e.Message}";

            var ce = new DndCampaignLedgerEntry(
                Sequence: ++_ledgerSeq,
                OccurredUtc: e.OccurredUtc,
                EncounterId: encounterId ?? string.Empty,
                EncounterName: encounterName ?? string.Empty,
                EncounterEntry: e,
                Message: msg ?? string.Empty);
            _ledger.Add(ce);
            list.Add(ce);
        }

        return list.AsReadOnly();
    }

    private void ReconcilePartyFromSnapshot(DndEncounterSnapshot encounter)
    {
        if (encounter == null || encounter.Actors == null)
        {
            return;
        }

        foreach (var kvp in encounter.Actors)
        {
            var actor = kvp.Value;
            if (actor == null || actor.Side != DndSide.Party)
            {
                continue;
            }

            if (!_party.TryGetValue(actor.ActorId, out var p))
            {
                _party[actor.ActorId] = new PartyMemberState(new DndCampaignPartyMember(
                    ActorId: actor.ActorId,
                    Name: actor.Name,
                    Stats: actor.Stats,
                    MaxHp: actor.MaxHp,
                    Hp: actor.Hp,
                    MaxMp: actor.MaxMp,
                    Mp: actor.Mp));
                p = _party[actor.ActorId];
            }

            p.Hp = Math.Clamp(actor.Hp, 0, p.MaxHp);
            p.Mp = Math.Clamp(actor.Mp, 0, p.MaxMp);
        }
    }

    private void MaybeFailCampaignFromEncounter(DndEncounterSnapshot encounter, IReadOnlyList<DndCampaignLedgerEntry> newEntries)
    {
        if (_failed)
        {
            return;
        }

        // Defeat condition: all party members at 0 HP. An empty table is formation, not a wipe.
        if (_party.Count == 0)
        {
            return;
        }

        var anyAlive = _party.Values.Any(p => p.IsAlive);
        if (!anyAlive)
        {
            _failed = true;
            _failureReason = "defeat";

            var msg = "Campaign failed: the party has fallen.";
            var entry = new DndCampaignLedgerEntry(
                Sequence: ++_ledgerSeq,
                OccurredUtc: _clock.UtcNow,
                EncounterId: _activeEncounterId ?? string.Empty,
                EncounterName: _activeEncounterName ?? string.Empty,
                EncounterEntry: null,
                Message: msg);
            _ledger.Add(entry);
        }
    }

    private DndCampaignSnapshot BuildSnapshot(DndEncounterSnapshot activeEncounterState)
    {
        var party = new Dictionary<string, DndCampaignPartyMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _party.Values)
        {
            party[p.ActorId] = new DndCampaignPartyMember(
                ActorId: p.ActorId,
                Name: p.Name,
                Stats: p.Stats,
                MaxHp: p.MaxHp,
                Hp: p.Hp,
                MaxMp: p.MaxMp,
                Mp: p.Mp);
        }

        return new DndCampaignSnapshot(
            IsFailed: _failed,
            FailureReason: _failureReason ?? string.Empty,
            Party: DndCampaignModelHelpers.ToReadOnlyDictionary(party),
            ActiveEncounterId: _activeEncounterId ?? string.Empty,
            ActiveEncounterName: _activeEncounterName ?? string.Empty,
            ActiveEncounterState: activeEncounterState,
            Session: BuildSessionSnapshot());
    }

    private IEnumerable<string> SeatedActorIds()
        => _party.Values
            .Where(p => p.IsAlive && !_sessionRound.SittingOut.Contains(p.ActorId))
            .Select(p => p.ActorId);

    private bool HasSeatedPc()
        => _party.Values.Any(p =>
            p.IsAlive &&
            !DndTableRound.IsNpcActorId(p.ActorId) &&
            !_sessionRound.SittingOut.Contains(p.ActorId));

    private int CountSeatedPcs()
        => _party.Values.Count(p =>
            p.IsAlive &&
            !DndTableRound.IsNpcActorId(p.ActorId) &&
            !_sessionRound.SittingOut.Contains(p.ActorId));

    private bool SessionReadyQuorumMet()
        => _sessionRound.ReadyThisRound.Count(id =>
               !DndTableRound.IsNpcActorId(id) && _sessionRound.ActedThisRound.Contains(id))
           >= DndTableRound.QuorumNeeded(CountSeatedPcs());

    private string ActorDisplayName(string actorId)
        => _party.TryGetValue(actorId, out var p) && p != null && !string.IsNullOrWhiteSpace(p.Name)
            ? p.Name
            : actorId ?? "(none)";

    private void EndSessionTableRound(List<DndCampaignLedgerEntry> entries)
    {
        foreach (var p in _party.Values.Where(m => m.IsAlive && !DndTableRound.IsNpcActorId(m.ActorId)).ToList())
        {
            if (_sessionRound.ActedThisRound.Contains(p.ActorId) || _sessionRound.JoinedThisRound.Contains(p.ActorId))
            {
                continue;
            }

            _sessionRound.SittingOut.Add(p.ActorId);
            AppendSessionMessage($"{p.Name} sits out (no action this round).", entries);
        }

        foreach (var id in _sessionRound.JoinedThisRound.ToList())
        {
            if (!_sessionRound.ActedThisRound.Contains(id))
            {
                _sessionRound.SittingOut.Add(id);
            }
        }

        _sessionRound.AdvanceRound();
        foreach (var id in SeatedActorIds())
        {
            _sessionRound.Participating.Add(id);
        }

        AppendSessionMessage($"Table round {_sessionRound.RoundNumber} is open.", entries);
        MaybeEnterFormationFromEmptyTable();
    }

    private void EnterFormation()
    {
        if (_sessionPhase == DndGamePhase.PartyFormation)
        {
            return;
        }

        if (_sessionPhase is not (DndGamePhase.NotStarted or DndGamePhase.Complete))
        {
            _previousPhase = _sessionPhase;
        }

        _sessionPhase = DndGamePhase.PartyFormation;
        _pendingCheck = null;
        _sessionRound.CurrentActorId = string.Empty;
    }

    private void MaybeEnterFormationFromEmptyTable()
    {
        if (!_sessionStarted || _failed || _sessionPhase is DndGamePhase.Complete or DndGamePhase.Failed)
        {
            return;
        }

        if (!HasSeatedPc())
        {
            EnterFormation();
        }
    }

    private bool TryConsumeSessionAction(string actorId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(actorId))
        {
            actorId = SeatedActorIds()
                .Where(id => !DndTableRound.IsNpcActorId(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(actorId) || !_party.TryGetValue(actorId, out var member))
        {
            error = "No seated party member";
            return false;
        }

        if (!member.IsAlive)
        {
            error = "Actor is not alive";
            return false;
        }

        if (_sessionRound.SittingOut.Contains(actorId))
        {
            _sessionRound.Seat(actorId, joinedThisRound: true);
        }

        var lockId = _sessionRound.CurrentActorId;
        if (!string.IsNullOrWhiteSpace(lockId) &&
            !string.Equals(lockId, actorId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{ActorDisplayName(lockId)}'s action is still resolving";
            return false;
        }

        if (_sessionRound.ActedThisRound.Contains(actorId))
        {
            error = "Already acted this round";
            return false;
        }

        _sessionRound.Seat(actorId);
        _sessionRound.CurrentActorId = actorId;
        return true;
    }

    private bool AllSeatedPcsHaveActed()
    {
        var seated = _party.Values
            .Where(p => p.IsAlive &&
                        !DndTableRound.IsNpcActorId(p.ActorId) &&
                        !_sessionRound.SittingOut.Contains(p.ActorId))
            .ToList();
        return seated.Count > 0 &&
               seated.All(p => _sessionRound.ActedThisRound.Contains(p.ActorId));
    }

    private void MaybeEndSessionRoundIfPartyActed()
    {
        if (HasActiveEncounter || _sessionPhase == DndGamePhase.Check)
        {
            return;
        }

        if (!AllSeatedPcsHaveActed())
        {
            return;
        }

        EndSessionTableRound(new List<DndCampaignLedgerEntry>());
    }

    private bool TryConsumeSessionActionOrAdvance(string actorId, out string error)
    {
        if (TryConsumeSessionAction(actorId, out error))
        {
            return true;
        }

        if (HasActiveEncounter ||
            !error.Contains("Already acted", StringComparison.OrdinalIgnoreCase) ||
            !AllSeatedPcsHaveActed())
        {
            return false;
        }

        EndSessionTableRound(new List<DndCampaignLedgerEntry>());
        return TryConsumeSessionAction(actorId, out error);
    }
}
