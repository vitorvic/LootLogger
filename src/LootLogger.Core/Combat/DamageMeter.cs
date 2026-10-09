using LootLogger.Core.Protocol;

namespace LootLogger.Core.Combat;

/// <summary>
/// Adds up damage, healing and damage taken by us and our party from the game's health updates.
/// Which events and parameters carry this follows AlbionOnline-StatisticsAnalysis (GPL-3.0).
/// </summary>
public sealed class DamageMeter
{
    public const int MaxSavedFights = 30;

    private readonly GameCodes _codes;
    private readonly Func<long, string?> _partyMemberName;
    private readonly Func<string> _mapName;
    private readonly Func<string?> _localName;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();

    // Object ids are only valid on the current map.
    private readonly Dictionary<long, double> _lastHealth = new();
    private readonly Dictionary<long, int> _weapons = new();
    private readonly HashSet<string> _inCombat = new(StringComparer.OrdinalIgnoreCase);

    private Fight _fight;
    private bool _combatWasOver;

    public DamageMeter(GameCodes codes, Func<long, string?> partyMemberName, Func<string> mapName, Func<string?> localName, Func<DateTime>? utcNow = null)
    {
        _codes = codes;
        _partyMemberName = partyMemberName;
        _mapName = mapName;
        _localName = localName;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _fight = new Fight(_utcNow());
    }

    /// <summary>A fight was put aside (by hand or before a reset). Fires on the capture thread.</summary>
    public event Action<FightSnapshot>? FightSaved;

    public bool ResetOnMapChange { get; set; } = true;

    /// <summary>Starts over when the party goes back into combat after everyone was out of it.</summary>
    public bool ResetBeforeCombat { get; set; }

    /// <summary>Keeps the fight in the saved list before a reset clears it.</summary>
    public bool SaveBeforeReset { get; set; } = true;

    /// <summary>True while we or someone in our party is fighting.</summary>
    public bool IsInCombat
    {
        get
        {
            lock (_lock)
            {
                return _inCombat.Count > 0;
            }
        }
    }

    public void Handle(GameMessage message)
    {
        FightSnapshot? saved = null;
        lock (_lock)
        {
            var p = message.Parameters;
            switch (message.Kind)
            {
                case MessageKind.Event when message.Code == _codes.HealthUpdate:
                    OnHealthUpdate(p);
                    break;
                case MessageKind.Event when message.Code == _codes.HealthUpdates:
                    OnHealthUpdates(p);
                    break;
                case MessageKind.Event when message.Code == _codes.InCombatStateUpdate:
                    saved = OnCombatState(p);
                    break;
                case MessageKind.Event when message.Code == _codes.NewCharacter:
                    RememberWeapon(p.GetLong(0), p.GetLongList(40));
                    break;
                case MessageKind.Event when message.Code == _codes.CharacterEquipmentChanged:
                    RememberWeapon(p.GetLong(0), p.GetLongList(2));
                    break;
                case MessageKind.Response when message.Code == _codes.ChangeCluster:
                    saved = OnMapChanged();
                    break;
            }
        }

        if (saved is not null)
        {
            FightSaved?.Invoke(saved);
        }
    }

    /// <summary>Clears the numbers, saving the fight first when that option is on.</summary>
    public void Reset()
    {
        FightSnapshot? saved;
        lock (_lock)
        {
            saved = ResetLocked();
        }

        if (saved is not null)
        {
            FightSaved?.Invoke(saved);
        }
    }

    /// <summary>Saves the fight as it is now, without clearing it. Null when nothing happened yet.</summary>
    public FightSnapshot? SaveNow()
    {
        FightSnapshot? saved;
        lock (_lock)
        {
            saved = _fight.HasData ? _fight.Snapshot(_utcNow()) : null;
        }

        if (saved is not null)
        {
            FightSaved?.Invoke(saved);
        }

        return saved;
    }

    public FightSnapshot Snapshot()
    {
        lock (_lock)
        {
            return _fight.Snapshot(_utcNow());
        }
    }

    // HealthUpdate: 0 target, 1 time, 2 health change (below zero is damage), 3 new health, 6 who caused it.
    // The game leaves a parameter out when it is zero: no change, health down to 0, or no one caused it.
    private void OnHealthUpdate(IReadOnlyDictionary<byte, object> p)
    {
        if (p.GetLong(0) is { } target)
        {
            Apply(target, p.GetDouble(2) ?? 0, p.GetDouble(3) ?? 0, p.GetLong(6) ?? 0);
        }
    }

    // HealthUpdates: several hits on one target; parameters 2, 3 and 6 are lists, one entry per hit.
    private void OnHealthUpdates(IReadOnlyDictionary<byte, object> p)
    {
        if (p.GetLong(0) is not { } target)
        {
            return;
        }

        var changes = p.GetDoubleList(2);
        var healths = p.GetDoubleList(3);
        var causers = p.GetDoubleList(6);
        for (var i = 0; i < Math.Min(changes.Count, causers.Count); i++)
        {
            if (changes[i] is { } change && causers[i] is { } causer)
            {
                Apply(target, change, i < healths.Count ? healths[i] ?? 0 : 0, (long) causer);
            }
        }
    }

    private void Apply(long target, double change, double newHealth, long causer)
    {
        var now = _utcNow();
        var amount = (long) Math.Round(Math.Abs(change), MidpointRounding.AwayFromZero);
        var previousHealth = _lastHealth.TryGetValue(target, out var last) ? last : (double?) null;
        _lastHealth[target] = newHealth;

        // No causer is the game finishing someone off (what was left of their health), not a hit from anyone.
        if (amount == 0 || causer == 0)
        {
            return;
        }

        if (change < 0)
        {
            // Damage someone does to themselves (blood magic, say) is not damage dealt.
            if (target == causer)
            {
                return;
            }

            if (_partyMemberName(causer) is { } attacker)
            {
                var c = Combatant(attacker, causer, now);
                c.Damage += amount;
                c.MaxHit = Math.Max(c.MaxHit, amount);
                c.AddToTimeline(_fight.Started, now, amount);
            }

            if (_partyMemberName(target) is { } victim)
            {
                Combatant(victim, target, now).Taken += amount;
            }

            return;
        }

        if (_partyMemberName(causer) is not { } healer)
        {
            return;
        }

        // Healing someone whose health did not move (already full) does nothing.
        var c2 = Combatant(healer, causer, now);
        if (previousHealth == newHealth)
        {
            c2.Overheal += amount;
        }
        else
        {
            c2.Heal += amount;
        }
    }

    // InCombatStateUpdate: 0 object id, 1 attacking, 2 being attacked.
    private FightSnapshot? OnCombatState(IReadOnlyDictionary<byte, object> p)
    {
        if (p.GetLong(0) is not { } objectId || _partyMemberName(objectId) is not { } name)
        {
            return null;
        }

        var now = _utcNow();
        var inCombat = p.GetBool(1) || p.GetBool(2);
        FightSnapshot? saved = null;
        if (inCombat)
        {
            if (_inCombat.Count == 0 && _combatWasOver && ResetBeforeCombat && _fight.HasData)
            {
                saved = ResetLocked();
            }

            _combatWasOver = false;
            _inCombat.Add(name);
            Combatant(name, objectId, now).EnterCombat(now);
        }
        else
        {
            _inCombat.Remove(name);
            if (_fight.Players.TryGetValue(name, out var c))
            {
                c.LeaveCombat(now);
            }

            if (_inCombat.Count == 0)
            {
                _combatWasOver = true;
            }
        }

        return saved;
    }

    private FightSnapshot? OnMapChanged()
    {
        _lastHealth.Clear();
        _weapons.Clear();
        var now = _utcNow();
        foreach (var c in _fight.Players.Values)
        {
            c.LeaveCombat(now);
        }

        _inCombat.Clear();
        _combatWasOver = true;
        return ResetOnMapChange ? ResetLocked() : null;
    }

    private FightSnapshot? ResetLocked()
    {
        var now = _utcNow();
        var saved = SaveBeforeReset && _fight.HasData ? _fight.Snapshot(now) : null;
        _fight = new Fight(now);

        // Whoever is still fighting carries on in the new fight.
        foreach (var name in _inCombat)
        {
            _fight.Get(name, IsLocal(name), now).EnterCombat(now);
        }

        return saved;
    }

    // NewCharacter 40 / CharacterEquipmentChanged 2: equipped item numbers, main hand first.
    private void RememberWeapon(long? objectId, List<long> equipment)
    {
        if (objectId is { } id && equipment.Count > 0 && equipment[0] > 0)
        {
            _weapons[id] = (int) equipment[0];
        }
    }

    private Combatant Combatant(string name, long objectId, DateTime now)
    {
        if (!_fight.HasData)
        {
            _fight.Map = _mapName();
        }

        var c = _fight.Get(name, IsLocal(name), now);
        if (_weapons.TryGetValue(objectId, out var weapon))
        {
            c.WeaponIndex = weapon;
        }

        c.Touch(now);
        return c;
    }

    private bool IsLocal(string name) => string.Equals(name, _localName(), StringComparison.OrdinalIgnoreCase);

    private sealed class Fight(DateTime started)
    {
        public DateTime Started { get; } = started;
        public string Map { get; set; } = string.Empty;
        public Dictionary<string, Combatant> Players { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasData => Players.Values.Any(c => c.Damage + c.Heal + c.Taken > 0);

        public Combatant Get(string name, bool isLocal, DateTime now)
        {
            if (!Players.TryGetValue(name, out var c))
            {
                c = new Combatant(name, isLocal, now);
                Players[name] = c;
            }

            return c;
        }

        public FightSnapshot Snapshot(DateTime now)
        {
            var players = Players.Values
                .Where(c => c.Damage + c.Heal + c.Overheal + c.Taken > 0)
                .Select(c => c.Stats(now))
                .ToList();
            var duration = players.Count == 0 ? TimeSpan.Zero : players.Max(p => p.ActiveTime);
            return new FightSnapshot(Started, now, Map, duration, players);
        }
    }

    private sealed class Combatant(string name, bool isLocal, DateTime firstSeen)
    {
        private const int MaxTimelineSlots = 360;

        private readonly List<long> _timeline = [];
        private TimeSpan _combatTime;
        private DateTime? _inCombatSince;
        private DateTime _firstActivity = firstSeen;
        private DateTime _lastActivity = firstSeen;
        private bool _active;

        public long Damage { get; set; }
        public long Heal { get; set; }
        public long Overheal { get; set; }
        public long Taken { get; set; }
        public long MaxHit { get; set; }
        public int? WeaponIndex { get; set; }

        public void Touch(DateTime now)
        {
            if (!_active)
            {
                _active = true;
                _firstActivity = now;
            }

            _lastActivity = now;
        }

        public void EnterCombat(DateTime now) => _inCombatSince ??= now;

        public void LeaveCombat(DateTime now)
        {
            if (_inCombatSince is { } since)
            {
                _combatTime += now - since;
                _inCombatSince = null;
            }
        }

        public void AddToTimeline(DateTime fightStart, DateTime now, long amount)
        {
            var slot = Math.Clamp((int) ((now - fightStart).TotalSeconds / FightSnapshot.TimelineSlotSeconds), 0, MaxTimelineSlots - 1);
            while (_timeline.Count <= slot)
            {
                _timeline.Add(0);
            }

            _timeline[slot] += amount;
        }

        public CombatantStats Stats(DateTime now)
        {
            var combat = _combatTime + (_inCombatSince is { } since ? now - since : TimeSpan.Zero);

            // Without combat messages (rare), the time between the first and last hit stands in.
            var active = combat > TimeSpan.Zero ? combat : _lastActivity - _firstActivity;
            return new CombatantStats(name, isLocal, WeaponIndex, Damage, Heal, Overheal, Taken, MaxHit, active, _timeline.ToArray());
        }
    }
}

/// <summary>One player's numbers in a fight.</summary>
public sealed record CombatantStats(
    string Name,
    bool IsLocal,
    int? WeaponIndex,
    long Damage,
    long Heal,
    long Overheal,
    long Taken,
    long MaxHit,
    TimeSpan ActiveTime,
    long[] Timeline)
{
    private double Seconds => Math.Max(1, ActiveTime.TotalSeconds);

    public double Dps => Damage / Seconds;

    public double Hps => Heal / Seconds;
}

/// <summary>A fight as it stood at one moment; also what the saved-fights list keeps.</summary>
public sealed record FightSnapshot(DateTime StartedUtc, DateTime TakenUtc, string Map, TimeSpan Duration, IReadOnlyList<CombatantStats> Players)
{
    public const int TimelineSlotSeconds = 10;

    public long TotalDamage => Players.Sum(p => p.Damage);

    public long TotalHeal => Players.Sum(p => p.Heal);

    public double GroupDps => TotalDamage / Math.Max(1, Duration.TotalSeconds);
}
