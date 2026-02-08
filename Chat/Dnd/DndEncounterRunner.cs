using System.Collections.ObjectModel;

namespace GPT.CLI.Chat.Dnd;

public sealed class DndEncounterRunner
{
    private sealed class ActorState
    {
        public ActorState(DndActorDefinition def)
        {
            ActorId = def.ActorId;
            Name = def.Name;
            Side = def.Side;
            IsBoss = def.IsBoss;
            Stats = def.Stats;
            MaxHp = def.MaxHp;
            Hp = def.StartingHp <= 0 ? def.MaxHp : def.StartingHp;
            MaxMp = def.MaxMp;
            Mp = def.StartingMp < 0 ? def.MaxMp : def.StartingMp;

            // Clamp after defaulting.
            Hp = Math.Clamp(Hp, 0, MaxHp);
            Mp = Math.Clamp(Mp, 0, MaxMp);
        }

        public string ActorId { get; }
        public string Name { get; }
        public DndSide Side { get; }
        public bool IsBoss { get; }
        public DndStats Stats { get; }
        public int MaxHp { get; }
        public int Hp { get; set; }
        public int MaxMp { get; }
        public int Mp { get; set; }
        public int? InitiativeTotal { get; set; }
        public bool IsAlive => Hp > 0;
    }

    private sealed class ActionState
    {
        public string ActionId { get; init; }
        public DndActionType ActionType { get; init; }
        public string ActorId { get; init; }
        public string TargetId { get; init; }
        public bool Hit { get; set; }
        public bool Critical { get; set; }
    }

    private readonly DndEncounterDefinition _definition;
    private readonly DndRuleset _rules;
    private readonly IDiceRoller _dice;
    private readonly IClock _clock;

    private readonly Dictionary<string, ActorState> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _turnOrder = new();
    private readonly Dictionary<string, int> _initiativeByActorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActionState> _actionsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DndPendingRoll> _pendingRollsById = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<DndLedgerEntry> _ledger = new();
    private int _sequence;
    private int _roundNumber;
    private int _turnIndex;
    private DndEncounterPhase _phase;
    private bool _completed;
    private string _completionReason = string.Empty;

    public DndEncounterRunner(
        DndEncounterDefinition definition,
        DndRuleset ruleset = null,
        IDiceRoller diceRoller = null,
        IClock clock = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _rules = ruleset ?? DndRuleset.Default;
        _dice = diceRoller ?? new RandomDiceRoller();
        _clock = clock ?? new SystemClock();

        ValidateAndInitializeActors(definition);
        _phase = DndEncounterPhase.NotStarted;
    }

    public bool IsCompleted => _completed;

    public DndEncounterSnapshot GetState() => BuildSnapshot();

    public IReadOnlyList<DndLedgerEntry> GetLedger() => _ledger.ToList().AsReadOnly();

    public IReadOnlyList<DndPendingRoll> GetPendingRolls()
        => _pendingRollsById.Values.OrderBy(r => r.Kind).ThenBy(r => r.RollId, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();

    public DndEncounterRunnerState ToState()
    {
        var state = new DndEncounterRunnerState
        {
            SequenceCounter = _sequence,
            RoundNumber = _roundNumber,
            TurnIndex = _turnIndex,
            Phase = _phase,
            Completed = _completed,
            CompletionReason = _completionReason ?? string.Empty,
            TurnOrder = _turnOrder.ToList(),
            InitiativeByActorId = new Dictionary<string, int>(_initiativeByActorId, StringComparer.OrdinalIgnoreCase),
            PendingRolls = GetPendingRolls().ToList(),
            Actions = _actionsById.Values.Select(a => new DndEncounterActionState
            {
                ActionId = a.ActionId ?? string.Empty,
                ActionType = a.ActionType,
                ActorId = a.ActorId ?? string.Empty,
                TargetId = a.TargetId ?? string.Empty,
                Hit = a.Hit,
                Critical = a.Critical
            }).ToList(),
            Ledger = _ledger.ToList()
        };

        foreach (var a in _actors.Values)
        {
            var def = new DndActorDefinition(
                ActorId: a.ActorId,
                Name: a.Name,
                Side: a.Side,
                IsBoss: a.IsBoss,
                Stats: a.Stats,
                MaxHp: a.MaxHp,
                MaxMp: a.MaxMp,
                StartingHp: a.Hp,
                StartingMp: a.Mp);

            state.Actors.Add(new DndEncounterActorState
            {
                Definition = def,
                Hp = a.Hp,
                Mp = a.Mp,
                InitiativeTotal = a.InitiativeTotal
            });
        }

        return state;
    }

    public static DndEncounterRunner FromState(
        DndEncounterRunnerState state,
        DndRuleset ruleset = null,
        IDiceRoller diceRoller = null,
        IClock clock = null)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var actors = (state.Actors ?? new List<DndEncounterActorState>())
            .Where(a => a != null && a.Definition != null && !string.IsNullOrWhiteSpace(a.Definition.ActorId))
            .ToList();
        if (actors.Count == 0)
        {
            throw new ArgumentException("Encounter state missing actors", nameof(state));
        }

        var partyDefs = actors
            .Where(a => a.Definition.Side == DndSide.Party)
            .Select(a => a.Definition with { StartingHp = a.Hp, StartingMp = a.Mp })
            .ToList();
        if (partyDefs.Count == 0)
        {
            throw new ArgumentException("Encounter state missing party", nameof(state));
        }

        var bossDef = actors
            .Where(a => a.Definition.Side == DndSide.Enemy && a.Definition.IsBoss)
            .Select(a => a.Definition with { StartingHp = a.Hp, StartingMp = a.Mp })
            .FirstOrDefault();
        bossDef ??= actors
            .Where(a => a.Definition.Side == DndSide.Enemy)
            .Select(a => a.Definition with { StartingHp = a.Hp, StartingMp = a.Mp })
            .FirstOrDefault();
        if (bossDef == null)
        {
            throw new ArgumentException("Encounter state missing boss", nameof(state));
        }

        var addsDefs = actors
            .Where(a => a.Definition.Side == DndSide.Enemy && !string.Equals(a.Definition.ActorId, bossDef.ActorId, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Definition with { StartingHp = a.Hp, StartingMp = a.Mp })
            .ToList();

        var def = new DndEncounterDefinition(partyDefs, bossDef, addsDefs);
        var runner = new DndEncounterRunner(def, ruleset, diceRoller, clock);

        // Overwrite runtime fields to match exactly.
        runner._sequence = state.SequenceCounter;
        runner._roundNumber = state.RoundNumber;
        runner._turnIndex = state.TurnIndex;
        runner._phase = state.Phase;
        runner._completed = state.Completed;
        runner._completionReason = state.CompletionReason ?? string.Empty;

        runner._turnOrder.Clear();
        if (state.TurnOrder is { Count: > 0 })
        {
            runner._turnOrder.AddRange(state.TurnOrder.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        runner._initiativeByActorId.Clear();
        foreach (var kvp in state.InitiativeByActorId ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                runner._initiativeByActorId[kvp.Key] = kvp.Value;
            }
        }

        // Actor HP/MP/initiative totals.
        foreach (var a in state.Actors ?? new List<DndEncounterActorState>())
        {
            if (a?.Definition == null || string.IsNullOrWhiteSpace(a.Definition.ActorId))
            {
                continue;
            }

            if (!runner._actors.TryGetValue(a.Definition.ActorId, out var st))
            {
                continue;
            }

            st.Hp = Math.Clamp(a.Hp, 0, st.MaxHp);
            st.Mp = Math.Clamp(a.Mp, 0, st.MaxMp);
            st.InitiativeTotal = a.InitiativeTotal;
        }

        // Actions.
        runner._actionsById.Clear();
        foreach (var a in state.Actions ?? new List<DndEncounterActionState>())
        {
            if (a == null || string.IsNullOrWhiteSpace(a.ActionId))
            {
                continue;
            }

            runner._actionsById[a.ActionId] = new ActionState
            {
                ActionId = a.ActionId,
                ActionType = a.ActionType,
                ActorId = a.ActorId ?? string.Empty,
                TargetId = a.TargetId ?? string.Empty,
                Hit = a.Hit,
                Critical = a.Critical
            };
        }

        // Pending rolls (keyed by id).
        runner._pendingRollsById.Clear();
        foreach (var pr in state.PendingRolls ?? new List<DndPendingRoll>())
        {
            if (pr == null || string.IsNullOrWhiteSpace(pr.RollId))
            {
                continue;
            }

            runner._pendingRollsById[pr.RollId] = pr;
        }

        // Ledger.
        runner._ledger.Clear();
        if (state.Ledger is { Count: > 0 })
        {
            runner._ledger.AddRange(state.Ledger.Where(e => e != null));
        }

        return runner;
    }

    public DndTurnResult StartEncounter()
    {
        if (_completed)
        {
            return DndTurnResult.ErrorResult("Encounter completed", BuildSnapshot(), GetPendingRolls());
        }

        if (_phase != DndEncounterPhase.NotStarted)
        {
            return DndTurnResult.ErrorResult("Encounter already started", BuildSnapshot(), GetPendingRolls());
        }

        var newEntries = new List<DndLedgerEntry>();
        _phase = DndEncounterPhase.NeedInitiative;
        _roundNumber = 0;
        _turnIndex = 0;
        _turnOrder.Clear();
        _initiativeByActorId.Clear();
        _actionsById.Clear();
        _pendingRollsById.Clear();

        newEntries.Add(AppendLedger(
            DndLedgerKind.EncounterStarted,
            "Encounter started. Initiative required.",
            DetailsForSystem()));

        foreach (var actor in _actors.Values.Where(a => a.IsAlive))
        {
            EnqueueRoll(new DndPendingRoll(
                RollId: NewId("r"),
                Kind: DndPendingRollKind.Initiative,
                ActorId: actor.ActorId,
                TargetId: string.Empty,
                ActionId: string.Empty,
                DiceCount: 1,
                DiceSides: 20,
                Modifier: _rules.GetInitiativeModifier(actor.Stats)), newEntries);
        }

        return BuildOkResult(newEntries);
    }

    public DndTurnResult DeclareAttack(string actorId, string targetId)
        => DeclareAction(actorId, targetId, DndActionType.Attack);

    public DndTurnResult DeclareCastSpell(string actorId, string targetId)
        => DeclareAction(actorId, targetId, DndActionType.CastSpell);

    public DndTurnResult Pass(string actorId)
    {
        if (!TryEnsureCanAct(actorId, out var err))
        {
            return DndTurnResult.ErrorResult(err, BuildSnapshot(), GetPendingRolls());
        }

        var newEntries = new List<DndLedgerEntry>();
        var actionId = NewId("a");
        newEntries.Add(AppendLedger(
            DndLedgerKind.ActionDeclared,
            $"{ActorName(actorId)} passes.",
            DetailsForAction(actorId, targetId: string.Empty, actionId, DndActionType.Pass)));
        newEntries.Add(AppendLedger(
            DndLedgerKind.ActionResolved,
            $"{ActorName(actorId)} ends their turn.",
            DetailsForAction(actorId, targetId: string.Empty, actionId, DndActionType.Pass)));

        AdvanceTurnAndAutoRunEnemies(newEntries);
        return BuildOkResult(newEntries);
    }

    public DndTurnResult RollInitiative(string actorId, int? natural = null)
    {
        var roll = _pendingRollsById.Values.FirstOrDefault(r =>
            r.Kind == DndPendingRollKind.Initiative &&
            string.Equals(r.ActorId, actorId, StringComparison.OrdinalIgnoreCase));
        if (roll == null)
        {
            return DndTurnResult.ErrorResult("No pending initiative roll for actor", BuildSnapshot(), GetPendingRolls());
        }

        return ResolveRoll(roll.RollId, natural, naturalSum: null);
    }

    public DndTurnResult RollAttack(string rollId, int? natural = null) => ResolveRoll(rollId, natural, naturalSum: null);
    public DndTurnResult RollDamage(string rollId, int? naturalSum = null) => ResolveRoll(rollId, natural: null, naturalSum: naturalSum);
    public DndTurnResult RollSpellAttack(string rollId, int? natural = null) => ResolveRoll(rollId, natural, naturalSum: null);
    public DndTurnResult RollSpellDamage(string rollId, int? naturalSum = null) => ResolveRoll(rollId, natural: null, naturalSum: naturalSum);

    public DndTurnResult RollAll()
    {
        if (_completed)
        {
            return DndTurnResult.ErrorResult("Encounter completed", BuildSnapshot(), GetPendingRolls());
        }

        var newEntries = new List<DndLedgerEntry>();

        // Resolve until no pending rolls remain, or we hit a safety cap.
        for (var i = 0; i < 128; i++)
        {
            if (_pendingRollsById.Count == 0)
            {
                break;
            }

            // Initiative first to unblock turn order setup.
            var next = _pendingRollsById.Values
                .OrderBy(r => r.Kind == DndPendingRollKind.Initiative ? 0 : 1)
                .ThenBy(r => r.RollId, StringComparer.OrdinalIgnoreCase)
                .First();

            // Resolve using dice roller.
            var result = ResolveRollInternal(next, naturalOverride: null, naturalSumOverride: null, newEntries);
            if (!result)
            {
                break;
            }
        }

        // If we're in combat and it's enemy's turn, keep simulating enemies until a player is up.
        if (!_completed)
        {
            AdvanceTurnAndAutoRunEnemiesIfNeeded(newEntries);
        }

        return BuildOkResult(newEntries);
    }

    private DndTurnResult DeclareAction(string actorId, string targetId, DndActionType actionType)
    {
        if (!TryEnsureCanAct(actorId, out var err))
        {
            return DndTurnResult.ErrorResult(err, BuildSnapshot(), GetPendingRolls());
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            return DndTurnResult.ErrorResult("Target required", BuildSnapshot(), GetPendingRolls());
        }

        if (!_actors.TryGetValue(targetId, out var target) || !target.IsAlive)
        {
            return DndTurnResult.ErrorResult("Target not found or not alive", BuildSnapshot(), GetPendingRolls());
        }

        var actor = _actors[actorId];
        if (actor.Side == target.Side)
        {
            return DndTurnResult.ErrorResult("Cannot target ally", BuildSnapshot(), GetPendingRolls());
        }

        var newEntries = new List<DndLedgerEntry>();
        var actionId = NewId("a");
        _actionsById[actionId] = new ActionState
        {
            ActionId = actionId,
            ActionType = actionType,
            ActorId = actorId,
            TargetId = targetId
        };

        newEntries.Add(AppendLedger(
            DndLedgerKind.ActionDeclared,
            $"{ActorName(actorId)} declares {actionType} vs {ActorName(targetId)}.",
            DetailsForAction(actorId, targetId, actionId, actionType)));

        if (actionType == DndActionType.CastSpell)
        {
            if (actor.Mp < _rules.SpellMpCost)
            {
                _actionsById.Remove(actionId);
                return DndTurnResult.ErrorResult("Not enough MP", BuildSnapshot(), GetPendingRolls());
            }

            actor.Mp = Math.Max(0, actor.Mp - _rules.SpellMpCost);
            newEntries.Add(AppendLedger(
                DndLedgerKind.MpSpent,
                $"{ActorName(actorId)} spends {_rules.SpellMpCost} MP.",
                DetailsForMpSpend(actorId, actionId, -_rules.SpellMpCost)));
        }

        var rollKind = actionType == DndActionType.CastSpell ? DndPendingRollKind.SpellToHit : DndPendingRollKind.AttackToHit;
        var mod = actionType == DndActionType.CastSpell
            ? _rules.GetSpellToHitModifier(actor.Stats)
            : _rules.GetAttackToHitModifier(actor.Stats);

        EnqueueRoll(new DndPendingRoll(
            RollId: NewId("r"),
            Kind: rollKind,
            ActorId: actorId,
            TargetId: targetId,
            ActionId: actionId,
            DiceCount: 1,
            DiceSides: 20,
            Modifier: mod), newEntries);

        return BuildOkResult(newEntries);
    }

    private DndTurnResult ResolveRoll(string rollId, int? natural, int? naturalSum)
    {
        if (_completed)
        {
            return DndTurnResult.ErrorResult("Encounter completed", BuildSnapshot(), GetPendingRolls());
        }

        if (string.IsNullOrWhiteSpace(rollId))
        {
            return DndTurnResult.ErrorResult("rollId required", BuildSnapshot(), GetPendingRolls());
        }

        if (!_pendingRollsById.TryGetValue(rollId, out var roll))
        {
            return DndTurnResult.ErrorResult("Pending roll not found", BuildSnapshot(), GetPendingRolls());
        }

        var newEntries = new List<DndLedgerEntry>();
        var ok = ResolveRollInternal(roll, naturalOverride: natural, naturalSumOverride: naturalSum, newEntries);
        if (!ok)
        {
            return DndTurnResult.ErrorResult("Failed to resolve roll", BuildSnapshot(), GetPendingRolls());
        }

        AdvanceTurnAndAutoRunEnemiesIfNeeded(newEntries);
        return BuildOkResult(newEntries);
    }

    private bool ResolveRollInternal(DndPendingRoll roll, int? naturalOverride, int? naturalSumOverride, List<DndLedgerEntry> newEntries)
    {
        if (roll.Kind == DndPendingRollKind.Initiative)
        {
            var (rolls, total, nat) = RollDice(roll.DiceCount, roll.DiceSides, roll.Modifier, naturalOverride, naturalSumOverride);
            _pendingRollsById.Remove(roll.RollId);
            _initiativeByActorId[roll.ActorId] = total;
            _actors[roll.ActorId].InitiativeTotal = total;

            newEntries.Add(AppendLedger(
                DndLedgerKind.InitiativeRolled,
                $"{ActorName(roll.ActorId)} rolls initiative: {nat}{(roll.Modifier == 0 ? "" : (roll.Modifier > 0 ? $"+{roll.Modifier}" : roll.Modifier.ToString()))} = {total}.",
                DetailsForRoll(
                    actorId: roll.ActorId,
                    targetId: string.Empty,
                    actionId: string.Empty,
                    actionType: DndActionType.Unknown,
                    roll,
                    rolls,
                    total,
                    hit: false,
                    crit: nat == 20)));

            if (_phase == DndEncounterPhase.NeedInitiative && AllAliveActorsHaveInitiative())
            {
                EstablishTurnOrder(newEntries);
            }

            return true;
        }

        if (roll.Kind == DndPendingRollKind.AttackToHit || roll.Kind == DndPendingRollKind.SpellToHit)
        {
            if (!_actionsById.TryGetValue(roll.ActionId, out var action))
            {
                _pendingRollsById.Remove(roll.RollId);
                return true;
            }

            var (rolls, total, nat) = RollDice(roll.DiceCount, roll.DiceSides, roll.Modifier, naturalOverride, naturalSumOverride);
            _pendingRollsById.Remove(roll.RollId);

            var target = _actors[roll.TargetId];
            var defense = _rules.GetDefenseTarget(target.Stats);
            var hit = total >= defense;
            var crit = nat == 20;
            action.Hit = hit;
            action.Critical = crit;

            newEntries.Add(AppendLedger(
                DndLedgerKind.ActionResolved,
                $"{ActorName(action.ActorId)} {(action.ActionType == DndActionType.CastSpell ? "casts" : "attacks")} {ActorName(action.TargetId)}: roll {nat}{FormatMod(roll.Modifier)} = {total} vs DEF {defense} => {(hit ? "HIT" : "MISS")}{(crit ? " (CRIT)" : "")}.",
                DetailsForRoll(
                    actorId: action.ActorId,
                    targetId: action.TargetId,
                    actionId: action.ActionId,
                    actionType: action.ActionType,
                    roll,
                    rolls,
                    total,
                    hit,
                    crit)));

            if (!hit)
            {
                _actionsById.Remove(action.ActionId);
                AdvanceTurn(newEntries);
                return true;
            }

            var dmgKind = action.ActionType == DndActionType.CastSpell ? DndPendingRollKind.SpellDamage : DndPendingRollKind.AttackDamage;
            var diceSides = action.ActionType == DndActionType.CastSpell ? _rules.SpellDamageDiceSides : _rules.AttackDamageDiceSides;
            var baseDice = 1;
            var diceCount = crit ? baseDice + 1 : baseDice;

            var dmgMod = action.ActionType == DndActionType.CastSpell
                ? _rules.GetStatMod(_actors[action.ActorId].Stats.SpellPower)
                : _rules.GetStatMod(_actors[action.ActorId].Stats.Str);

            EnqueueRoll(new DndPendingRoll(
                RollId: NewId("r"),
                Kind: dmgKind,
                ActorId: action.ActorId,
                TargetId: action.TargetId,
                ActionId: action.ActionId,
                DiceCount: diceCount,
                DiceSides: diceSides,
                Modifier: dmgMod), newEntries);

            return true;
        }

        if (roll.Kind == DndPendingRollKind.AttackDamage || roll.Kind == DndPendingRollKind.SpellDamage)
        {
            if (!_actionsById.TryGetValue(roll.ActionId, out var action))
            {
                _pendingRollsById.Remove(roll.RollId);
                return true;
            }

            var (rolls, total, _) = RollDice(roll.DiceCount, roll.DiceSides, roll.Modifier, naturalOverride: null, naturalSumOverride);
            _pendingRollsById.Remove(roll.RollId);

            var target = _actors[action.TargetId];
            var before = target.Hp;
            var dmg = Math.Max(0, total);
            target.Hp = Math.Max(0, target.Hp - dmg);
            var actualDelta = target.Hp - before; // negative or zero

            newEntries.Add(AppendLedger(
                DndLedgerKind.DamageApplied,
                $"{ActorName(action.ActorId)} deals {dmg} damage to {ActorName(action.TargetId)} (HP {before}->{target.Hp}).",
                DetailsForDamage(action.ActorId, action.TargetId, action.ActionId, action.ActionType, roll, rolls, total, hit: true, crit: action.Critical, targetHpDelta: actualDelta)));

            _actionsById.Remove(action.ActionId);

            if (CheckCompletion(newEntries))
            {
                return true;
            }

            AdvanceTurn(newEntries);
            return true;
        }

        return false;
    }

    private void EstablishTurnOrder(List<DndLedgerEntry> newEntries)
    {
        _turnOrder.Clear();

        var alive = _actors.Values.Where(a => a.IsAlive).ToList();
        var ordered = alive
            .OrderByDescending(a => _initiativeByActorId.TryGetValue(a.ActorId, out var v) ? v : int.MinValue)
            .ThenByDescending(a => _rules.GetStatMod(a.Stats.Dex))
            .ThenBy(a => a.ActorId, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.ActorId)
            .ToList();

        _turnOrder.AddRange(ordered);
        _phase = DndEncounterPhase.InCombat;
        _roundNumber = 1;
        _turnIndex = 0;

        newEntries.Add(AppendLedger(
            DndLedgerKind.TurnAdvanced,
            $"Combat begins. Round 1. Current turn: {ActorName(CurrentActorId())}.",
            DetailsForSystem()));
    }

    private void AdvanceTurnAndAutoRunEnemies(List<DndLedgerEntry> newEntries)
    {
        AdvanceTurn(newEntries);
        AdvanceTurnAndAutoRunEnemiesIfNeeded(newEntries);
    }

    private void AdvanceTurnAndAutoRunEnemiesIfNeeded(List<DndLedgerEntry> newEntries)
    {
        if (_completed || _phase != DndEncounterPhase.InCombat)
        {
            return;
        }

        // If current actor is enemy, simulate enemies until we hit a party turn or completion.
        for (var safety = 0; safety < 64; safety++)
        {
            if (_completed)
            {
                return;
            }

            if (_pendingRollsById.Count > 0)
            {
                return;
            }

            var currentId = CurrentActorId();
            if (string.IsNullOrWhiteSpace(currentId))
            {
                return;
            }

            var current = _actors[currentId];
            if (!current.IsAlive)
            {
                AdvanceTurn(newEntries);
                continue;
            }

            if (current.Side == DndSide.Party)
            {
                return;
            }

            SimulateEnemyTurn(current, newEntries);
        }
    }

    private void SimulateEnemyTurn(ActorState enemy, List<DndLedgerEntry> newEntries)
    {
        var target = ChooseEnemyTarget();
        if (target == null)
        {
            // Everyone is dead; mark defeat.
            Complete("defeat", newEntries);
            return;
        }

        var actionId = NewId("a");
        _actionsById[actionId] = new ActionState
        {
            ActionId = actionId,
            ActionType = DndActionType.Attack,
            ActorId = enemy.ActorId,
            TargetId = target.ActorId
        };

        newEntries.Add(AppendLedger(
            DndLedgerKind.ActionDeclared,
            $"{ActorName(enemy.ActorId)} attacks {ActorName(target.ActorId)}.",
            DetailsForAction(enemy.ActorId, target.ActorId, actionId, DndActionType.Attack)));

        var toHit = new DndPendingRoll(
            RollId: NewId("r"),
            Kind: DndPendingRollKind.AttackToHit,
            ActorId: enemy.ActorId,
            TargetId: target.ActorId,
            ActionId: actionId,
            DiceCount: 1,
            DiceSides: 20,
            Modifier: _rules.GetAttackToHitModifier(enemy.Stats));

        _pendingRollsById[toHit.RollId] = toHit;

        // Auto-resolve immediately.
        ResolveRollInternal(toHit, naturalOverride: null, naturalSumOverride: null, newEntries);

        // If that enqueued damage, resolve that too.
        if (_pendingRollsById.Values.FirstOrDefault(r => string.Equals(r.ActionId, actionId, StringComparison.OrdinalIgnoreCase)) is { } dmg)
        {
            ResolveRollInternal(dmg, naturalOverride: null, naturalSumOverride: null, newEntries);
        }

        // Completion might have happened, otherwise ensure turn advances at least once.
        if (!_completed && _phase == DndEncounterPhase.InCombat)
        {
            // ResolveRollInternal on miss/damage already advances turn; if somehow not, force it.
            if (string.Equals(CurrentActorId(), enemy.ActorId, StringComparison.OrdinalIgnoreCase))
            {
                AdvanceTurn(newEntries);
            }
        }
    }

    private ActorState ChooseEnemyTarget()
    {
        // Deterministic: pick the first alive party member in current turn order.
        foreach (var id in _turnOrder)
        {
            if (!_actors.TryGetValue(id, out var a) || !a.IsAlive || a.Side != DndSide.Party)
            {
                continue;
            }

            return a;
        }

        // Fallback.
        return _actors.Values.Where(a => a.Side == DndSide.Party && a.IsAlive).OrderBy(a => a.ActorId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private void AdvanceTurn(List<DndLedgerEntry> newEntries)
    {
        if (_phase != DndEncounterPhase.InCombat || _turnOrder.Count == 0 || _completed)
        {
            return;
        }

        // Move to next alive actor. If none alive on either side, complete.
        for (var i = 0; i < _turnOrder.Count + 1; i++)
        {
            _turnIndex++;
            if (_turnIndex >= _turnOrder.Count)
            {
                _turnIndex = 0;
                _roundNumber++;
                newEntries.Add(AppendLedger(
                    DndLedgerKind.TurnAdvanced,
                    $"Round {_roundNumber} begins.",
                    DetailsForSystem()));
            }

            var id = CurrentActorId();
            if (string.IsNullOrWhiteSpace(id) || !_actors.TryGetValue(id, out var a))
            {
                continue;
            }

            if (!a.IsAlive)
            {
                continue;
            }

            newEntries.Add(AppendLedger(
                DndLedgerKind.TurnAdvanced,
                $"Turn: {ActorName(id)}.",
                DetailsForSystem()));
            return;
        }

        Complete("stalled", newEntries);
    }

    private bool CheckCompletion(List<DndLedgerEntry> newEntries)
    {
        if (_completed)
        {
            return true;
        }

        var enemiesAlive = _actors.Values.Any(a => a.Side == DndSide.Enemy && a.IsAlive);
        var partyAlive = _actors.Values.Any(a => a.Side == DndSide.Party && a.IsAlive);

        if (!partyAlive)
        {
            Complete("defeat", newEntries);
            return true;
        }

        if (!enemiesAlive)
        {
            Complete("victory", newEntries);
            return true;
        }

        return false;
    }

    private void Complete(string reason, List<DndLedgerEntry> newEntries)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completionReason = reason ?? string.Empty;
        _phase = DndEncounterPhase.Completed;
        _pendingRollsById.Clear();
        _actionsById.Clear();

        newEntries.Add(AppendLedger(
            DndLedgerKind.EncounterCompleted,
            $"Encounter completed: {_completionReason}.",
            DetailsForSystem()));
    }

    private bool TryEnsureCanAct(string actorId, out string error)
    {
        error = string.Empty;

        if (_completed)
        {
            error = "Encounter completed";
            return false;
        }

        if (_phase == DndEncounterPhase.NotStarted)
        {
            error = "Encounter not started";
            return false;
        }

        if (_phase == DndEncounterPhase.NeedInitiative)
        {
            error = "Initiative required";
            return false;
        }

        if (_phase != DndEncounterPhase.InCombat)
        {
            error = "Encounter not in combat";
            return false;
        }

        if (_pendingRollsById.Count > 0)
        {
            error = "Pending rolls must be resolved first";
            return false;
        }

        if (string.IsNullOrWhiteSpace(actorId))
        {
            error = "actorId required";
            return false;
        }

        if (!_actors.TryGetValue(actorId, out var actor) || !actor.IsAlive)
        {
            error = "Actor not found or not alive";
            return false;
        }

        if (!string.Equals(CurrentActorId(), actorId, StringComparison.OrdinalIgnoreCase))
        {
            error = "Not actor's turn";
            return false;
        }

        if (actor.Side != DndSide.Party)
        {
            error = "Only party can declare actions";
            return false;
        }

        return true;
    }

    private bool AllAliveActorsHaveInitiative()
    {
        foreach (var actor in _actors.Values)
        {
            if (!actor.IsAlive)
            {
                continue;
            }

            if (!_initiativeByActorId.ContainsKey(actor.ActorId))
            {
                return false;
            }
        }

        return true;
    }

    private DndTurnResult BuildOkResult(List<DndLedgerEntry> newEntries)
    {
        var state = BuildSnapshot();
        var pending = GetPendingRolls();
        return new DndTurnResult(
            Ok: true,
            Error: string.Empty,
            State: state,
            NewLedgerEntries: (newEntries ?? new List<DndLedgerEntry>()).ToList().AsReadOnly(),
            PendingRolls: pending,
            NextRequest: DndTurnResult.BuildNextRequest(state, pending));
    }

    private void EnqueueRoll(DndPendingRoll roll, List<DndLedgerEntry> newEntries)
    {
        _pendingRollsById[roll.RollId] = roll;
        var who = ActorName(roll.ActorId);
        var msg = roll.Kind switch
        {
            DndPendingRollKind.Initiative => $"{who} needs initiative roll (d20{FormatMod(roll.Modifier)}).",
            DndPendingRollKind.AttackToHit => $"{who} needs attack roll (d20{FormatMod(roll.Modifier)}).",
            DndPendingRollKind.SpellToHit => $"{who} needs spell attack roll (d20{FormatMod(roll.Modifier)}).",
            DndPendingRollKind.AttackDamage => $"{who} needs damage roll ({roll.DiceCount}d{roll.DiceSides}{FormatMod(roll.Modifier)}).",
            DndPendingRollKind.SpellDamage => $"{who} needs spell damage roll ({roll.DiceCount}d{roll.DiceSides}{FormatMod(roll.Modifier)}).",
            _ => $"{who} needs roll."
        };

        newEntries.Add(AppendLedger(
            DndLedgerKind.ActionResolved,
            msg,
            DetailsForRoll(
                actorId: roll.ActorId,
                targetId: roll.TargetId ?? string.Empty,
                actionId: roll.ActionId ?? string.Empty,
                actionType: _actionsById.TryGetValue(roll.ActionId ?? string.Empty, out var a) ? a.ActionType : DndActionType.Unknown,
                roll: roll,
                rolls: Array.Empty<int>(),
                total: 0,
                hit: false,
                crit: false)));
    }

    private (IReadOnlyList<int> Rolls, int Total, int NaturalFirstDie) RollDice(
        int diceCount,
        int diceSides,
        int modifier,
        int? naturalOverride,
        int? naturalSumOverride)
    {
        diceCount = Math.Max(1, diceCount);
        diceSides = Math.Max(2, diceSides);

        var rolls = new List<int>(diceCount);

        if (diceCount == 1 && naturalOverride.HasValue)
        {
            var n = Clamp(naturalOverride.Value, 1, diceSides);
            rolls.Add(n);
        }
        else if (naturalSumOverride.HasValue)
        {
            // Treat as a single bucketed number but keep it as one roll entry for ledger clarity.
            var n = Math.Max(1, naturalSumOverride.Value);
            rolls.Add(n);
        }
        else
        {
            for (var i = 0; i < diceCount; i++)
            {
                rolls.Add(_dice.RollDie(diceSides));
            }
        }

        var naturalTotal = rolls.Sum();
        var total = naturalTotal + modifier;
        return (rolls.AsReadOnly(), total, rolls.Count > 0 ? rolls[0] : 0);
    }

    private DndEncounterSnapshot BuildSnapshot()
    {
        var actors = new Dictionary<string, DndActorSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _actors.Values)
        {
            actors[a.ActorId] = new DndActorSnapshot(
                ActorId: a.ActorId,
                Name: a.Name,
                Side: a.Side,
                IsBoss: a.IsBoss,
                Stats: a.Stats,
                MaxHp: a.MaxHp,
                Hp: a.Hp,
                MaxMp: a.MaxMp,
                Mp: a.Mp,
                IsAlive: a.IsAlive,
                InitiativeTotal: a.InitiativeTotal);
        }

        return new DndEncounterSnapshot(
            Phase: _phase,
            RoundNumber: _roundNumber,
            CurrentActorId: CurrentActorId(),
            TurnOrder: _turnOrder.ToList().AsReadOnly(),
            Actors: new ReadOnlyDictionary<string, DndActorSnapshot>(actors),
            IsCompleted: _completed,
            CompletionReason: _completionReason ?? string.Empty);
    }

    private string CurrentActorId()
    {
        if (_turnOrder.Count == 0 || _phase != DndEncounterPhase.InCombat)
        {
            return string.Empty;
        }

        if (_turnIndex < 0 || _turnIndex >= _turnOrder.Count)
        {
            _turnIndex = 0;
        }

        return _turnOrder[_turnIndex] ?? string.Empty;
    }

    private string ActorName(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return "(none)";
        }

        return _actors.TryGetValue(actorId, out var a) ? a.Name : actorId;
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

    private string NewId(string prefix) => $"{prefix}{Interlocked.Increment(ref _sequence):D6}";

    private DndLedgerEntry AppendLedger(DndLedgerKind kind, string message, DndLedgerDetails details)
    {
        var entry = new DndLedgerEntry(
            Sequence: _ledger.Count + 1,
            OccurredUtc: _clock.UtcNow,
            Kind: kind,
            Message: message ?? string.Empty,
            Details: details);
        _ledger.Add(entry);
        return entry;
    }

    private static string FormatMod(int mod)
    {
        if (mod == 0)
        {
            return string.Empty;
        }

        return mod > 0 ? $"+{mod}" : mod.ToString();
    }

    private DndLedgerDetails DetailsForSystem()
    {
        return new DndLedgerDetails(
            ActorId: string.Empty,
            TargetId: string.Empty,
            ActionId: string.Empty,
            ActionType: DndActionType.Unknown,
            RollId: string.Empty,
            RollKind: null,
            DiceCount: 0,
            DiceSides: 0,
            Rolls: Array.Empty<int>(),
            Modifier: 0,
            Total: 0,
            Hit: false,
            Critical: false,
            ActorHpDelta: 0,
            ActorMpDelta: 0,
            TargetHpDelta: 0,
            TargetMpDelta: 0);
    }

    private DndLedgerDetails DetailsForAction(string actorId, string targetId, string actionId, DndActionType type)
    {
        return new DndLedgerDetails(
            ActorId: actorId ?? string.Empty,
            TargetId: targetId ?? string.Empty,
            ActionId: actionId ?? string.Empty,
            ActionType: type,
            RollId: string.Empty,
            RollKind: null,
            DiceCount: 0,
            DiceSides: 0,
            Rolls: Array.Empty<int>(),
            Modifier: 0,
            Total: 0,
            Hit: false,
            Critical: false,
            ActorHpDelta: 0,
            ActorMpDelta: 0,
            TargetHpDelta: 0,
            TargetMpDelta: 0);
    }

    private DndLedgerDetails DetailsForMpSpend(string actorId, string actionId, int mpDelta)
    {
        return new DndLedgerDetails(
            ActorId: actorId ?? string.Empty,
            TargetId: string.Empty,
            ActionId: actionId ?? string.Empty,
            ActionType: DndActionType.CastSpell,
            RollId: string.Empty,
            RollKind: null,
            DiceCount: 0,
            DiceSides: 0,
            Rolls: Array.Empty<int>(),
            Modifier: 0,
            Total: 0,
            Hit: false,
            Critical: false,
            ActorHpDelta: 0,
            ActorMpDelta: mpDelta,
            TargetHpDelta: 0,
            TargetMpDelta: 0);
    }

    private DndLedgerDetails DetailsForRoll(
        string actorId,
        string targetId,
        string actionId,
        DndActionType actionType,
        DndPendingRoll roll,
        IReadOnlyList<int> rolls,
        int total,
        bool hit,
        bool crit)
    {
        return new DndLedgerDetails(
            ActorId: actorId ?? string.Empty,
            TargetId: targetId ?? string.Empty,
            ActionId: actionId ?? string.Empty,
            ActionType: actionType,
            RollId: roll?.RollId ?? string.Empty,
            RollKind: roll?.Kind,
            DiceCount: roll?.DiceCount ?? 0,
            DiceSides: roll?.DiceSides ?? 0,
            Rolls: rolls ?? Array.Empty<int>(),
            Modifier: roll?.Modifier ?? 0,
            Total: total,
            Hit: hit,
            Critical: crit,
            ActorHpDelta: 0,
            ActorMpDelta: 0,
            TargetHpDelta: 0,
            TargetMpDelta: 0);
    }

    private DndLedgerDetails DetailsForDamage(
        string actorId,
        string targetId,
        string actionId,
        DndActionType actionType,
        DndPendingRoll roll,
        IReadOnlyList<int> rolls,
        int total,
        bool hit,
        bool crit,
        int targetHpDelta)
    {
        return new DndLedgerDetails(
            ActorId: actorId ?? string.Empty,
            TargetId: targetId ?? string.Empty,
            ActionId: actionId ?? string.Empty,
            ActionType: actionType,
            RollId: roll?.RollId ?? string.Empty,
            RollKind: roll?.Kind,
            DiceCount: roll?.DiceCount ?? 0,
            DiceSides: roll?.DiceSides ?? 0,
            Rolls: rolls ?? Array.Empty<int>(),
            Modifier: roll?.Modifier ?? 0,
            Total: total,
            Hit: hit,
            Critical: crit,
            ActorHpDelta: 0,
            ActorMpDelta: 0,
            TargetHpDelta: targetHpDelta,
            TargetMpDelta: 0);
    }

    private void ValidateAndInitializeActors(DndEncounterDefinition definition)
    {
        if (definition.Party == null || definition.Party.Count == 0)
        {
            throw new ArgumentException("Party required", nameof(definition));
        }

        if (definition.Boss == null)
        {
            throw new ArgumentException("Boss required", nameof(definition));
        }

        var all = new List<DndActorDefinition>();
        all.AddRange(definition.Party);
        all.Add(definition.Boss);
        if (definition.Adds != null && definition.Adds.Count > 0)
        {
            all.AddRange(definition.Adds);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in all)
        {
            if (a == null)
            {
                throw new ArgumentException("Null actor definition", nameof(definition));
            }

            if (string.IsNullOrWhiteSpace(a.ActorId))
            {
                throw new ArgumentException("ActorId required", nameof(definition));
            }

            if (!seen.Add(a.ActorId))
            {
                throw new ArgumentException($"Duplicate ActorId '{a.ActorId}'", nameof(definition));
            }

            if (a.MaxHp <= 0)
            {
                throw new ArgumentException($"MaxHp must be > 0 for '{a.ActorId}'", nameof(definition));
            }

            if (a.MaxMp < 0)
            {
                throw new ArgumentException($"MaxMp must be >= 0 for '{a.ActorId}'", nameof(definition));
            }

            if (a.StartingHp > 0 && a.StartingHp > a.MaxHp)
            {
                throw new ArgumentException($"StartingHp must be <= MaxHp for '{a.ActorId}'", nameof(definition));
            }

            if (a.StartingMp >= 0 && a.StartingMp > a.MaxMp)
            {
                throw new ArgumentException($"StartingMp must be <= MaxMp for '{a.ActorId}'", nameof(definition));
            }

            _actors[a.ActorId] = new ActorState(a);
        }
    }
}
