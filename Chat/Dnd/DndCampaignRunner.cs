namespace GPT.CLI.Chat.Dnd;

public sealed class DndCampaignRunner
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

        if (definition.Party == null || definition.Party.Count == 0)
        {
            throw new ArgumentException("Party required", nameof(definition));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in definition.Party)
        {
            if (m == null)
            {
                throw new ArgumentException("Null party member", nameof(definition));
            }

            if (string.IsNullOrWhiteSpace(m.ActorId))
            {
                throw new ArgumentException("Party member ActorId required", nameof(definition));
            }

            if (!seen.Add(m.ActorId))
            {
                throw new ArgumentException($"Duplicate party member ActorId '{m.ActorId}'", nameof(definition));
            }

            if (m.MaxHp <= 0)
            {
                throw new ArgumentException($"MaxHp must be > 0 for '{m.ActorId}'", nameof(definition));
            }

            if (m.MaxMp < 0)
            {
                throw new ArgumentException($"MaxMp must be >= 0 for '{m.ActorId}'", nameof(definition));
            }

            _party[m.ActorId] = new PartyMemberState(m);
        }
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
            ActiveEncounter = _encounter?.ToState()
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

        // Active encounter.
        runner._encounter = state.ActiveEncounter == null
            ? null
            : DndEncounterRunner.FromState(state.ActiveEncounter, runner._rules, runner._dice, runner._clock);

        return runner;
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

        var partyDefs = _party.Values
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

        var boss = NormalizeEnemy(template.Boss, isBoss: true);
        var adds = (template.Adds ?? Array.Empty<DndActorDefinition>()).Select(a => NormalizeEnemy(a, isBoss: false)).ToList();

        var def = new DndEncounterDefinition(partyDefs, boss, adds);
        _encounter = new DndEncounterRunner(def, _rules, _dice, _clock);
        var res = _encounter.StartEncounter();

        var newCampaignEntries = AppendEncounterEntries(res, encounterId, _activeEncounterName);
        ReconcilePartyFromSnapshot(res.State);
        MaybeFailCampaignFromEncounter(res.State, newCampaignEntries);
        return BuildCampaignResult(res, newCampaignEntries);
    }

    public DndCampaignResult Attack(string actorId, string targetId) => RunEncounterStep(() => _encounter.DeclareAttack(actorId, targetId));
    public DndCampaignResult CastSpell(string actorId, string targetId) => RunEncounterStep(() => _encounter.DeclareCastSpell(actorId, targetId));
    public DndCampaignResult Pass(string actorId) => RunEncounterStep(() => _encounter.Pass(actorId));
    public DndCampaignResult RollAll() => RunEncounterStep(() => _encounter.RollAll());
    public DndCampaignResult RollInitiative(string actorId) => RunEncounterStep(() => _encounter.RollInitiative(actorId));
    public DndCampaignResult RollAttack(string rollId) => RunEncounterStep(() => _encounter.RollAttack(rollId));
    public DndCampaignResult RollDamage(string rollId) => RunEncounterStep(() => _encounter.RollDamage(rollId));
    public DndCampaignResult RollSpellAttack(string rollId) => RunEncounterStep(() => _encounter.RollSpellAttack(rollId));
    public DndCampaignResult RollSpellDamage(string rollId) => RunEncounterStep(() => _encounter.RollSpellDamage(rollId));

    public DndCampaignResult LongRest(bool clearFailure = false)
    {
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
        var newCampaignEntries = AppendEncounterEntries(res, _activeEncounterId, _activeEncounterName);

        if (res != null)
        {
            ReconcilePartyFromSnapshot(res.State);
            MaybeFailCampaignFromEncounter(res.State, newCampaignEntries);
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
                continue;
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

        // Defeat condition: all party members at 0 HP.
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
            ActiveEncounterState: activeEncounterState);
    }
}
