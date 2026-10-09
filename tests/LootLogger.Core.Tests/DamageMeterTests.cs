using LootLogger.Core.Combat;
using LootLogger.Core.Data;
using LootLogger.Core.Protocol;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class DamageMeterTests
{
    private static readonly GameCodes Codes = new();
    private static readonly Guid LocalGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FriendGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const long Me = 1;
    private const long Friend = 900;
    private const long Stranger = 901;
    private const long Mob = 5000;

    private readonly LootTracker _tracker;
    private readonly DamageMeter _meter;
    private readonly List<FightSnapshot> _saved = [];
    private DateTime _now = new(2026, 10, 9, 14, 0, 0, DateTimeKind.Utc);

    public DamageMeterTests()
    {
        _tracker = new LootTracker(Codes, ItemDatabase.LoadBuiltIn(), ClusterDatabase.LoadBuiltIn(), new MarketValueCache(), () => _now);
        _meter = new DamageMeter(Codes, _tracker.PartyMemberName, () => _tracker.ClusterName, () => _tracker.LocalPlayer?.Name, () => _now);
        _meter.FightSaved += _saved.Add;
        Join();
    }

    [Fact]
    public void Solo_CountsOnlyOurOwnDamageHealAndDamageTaken()
    {
        Character(Stranger, "Stranger");

        Hit(Mob, -1200.4f, 3000f, Me);
        Hit(Mob, -800f, 2200f, Stranger);
        Hit(Me, -300f, 900f, Mob);
        Hit(Me, 250f, 1150f, Me);

        var me = Assert.Single(_meter.Snapshot().Players);
        Assert.Equal("Pandas139", me.Name);
        Assert.True(me.IsLocal);
        Assert.Equal(1200, me.Damage);
        Assert.Equal(1200, me.MaxHit);
        Assert.Equal(300, me.Taken);
        Assert.Equal(250, me.Heal);
        Assert.Equal("Sunfang Cliffs", _meter.Snapshot().Map);
    }

    [Fact]
    public void Party_CountsMembersButNotStrangers()
    {
        Party();
        Character(Friend, "Valniaa");
        Character(Stranger, "Stranger");

        Hit(Mob, -500f, 1000f, Friend);
        Hit(Mob, -700f, 300f, Stranger);
        Hit(Friend, -100f, 900f, Stranger);

        var friend = Assert.Single(_meter.Snapshot().Players);
        Assert.Equal("Valniaa", friend.Name);
        Assert.Equal(500, friend.Damage);
        Assert.Equal(100, friend.Taken);
    }

    [Fact]
    public void HealthUpdates_CountsEveryHitInTheList()
    {
        Party();
        Character(Friend, "Valniaa");

        _meter.Handle(Event(Codes.HealthUpdates, new()
        {
            [0] = Mob,
            [2] = new[] { -100f, -250f, -50f },
            [3] = new[] { 900f, 650f, 600f },
            [6] = new[] { Me, Friend, Me }
        }));

        var players = _meter.Snapshot().Players.ToDictionary(p => p.Name);
        Assert.Equal(150, players["Pandas139"].Damage);
        Assert.Equal(100, players["Pandas139"].MaxHit);
        Assert.Equal(250, players["Valniaa"].Damage);
    }

    [Fact]
    public void SelfDamage_IsNotDamageDealt()
    {
        Hit(Me, -200f, 800f, Me);
        Assert.Empty(_meter.Snapshot().Players);
    }

    [Fact]
    public void FinishingBlowWithoutCauser_IsNotCounted()
    {
        // The game leaves out the causer (and the new health, now 0) when it finishes someone off.
        Both(Event(Codes.HealthUpdate, new() { [0] = Me, [1] = 0L, [2] = -500f }));
        Assert.Empty(_meter.Snapshot().Players);
    }

    [Fact]
    public void HealingAFullTarget_IsOverhealNotHeal()
    {
        Hit(Me, -100f, 900f, Mob);
        Hit(Me, 100f, 1000f, Me);
        Hit(Me, 100f, 1000f, Me);

        var me = Assert.Single(_meter.Snapshot().Players);
        Assert.Equal(100, me.Heal);
        Assert.Equal(100, me.Overheal);
    }

    [Fact]
    public void Dps_UsesTimeInCombat()
    {
        Combat(Me, true);
        Hit(Mob, -1000f, 0f, Me);
        _now = _now.AddSeconds(10);
        Combat(Me, false);

        // Time out of combat does not lower the DPS.
        _now = _now.AddSeconds(50);
        Combat(Me, true);
        Hit(Mob, -1000f, 0f, Me);
        _now = _now.AddSeconds(10);

        var me = Assert.Single(_meter.Snapshot().Players);
        Assert.Equal(TimeSpan.FromSeconds(20), me.ActiveTime);
        Assert.Equal(100, me.Dps, 3);
    }

    [Fact]
    public void Timeline_GroupsOurDamageInTenSecondSlots()
    {
        Hit(Mob, -100f, 0f, Me);
        _now = _now.AddSeconds(4);
        Hit(Mob, -50f, 0f, Me);
        _now = _now.AddSeconds(21);
        Hit(Mob, -30f, 0f, Me);

        Assert.Equal(new long[] { 150, 0, 30 }, Assert.Single(_meter.Snapshot().Players).Timeline);
    }

    [Fact]
    public void MapChange_ResetsAndSavesTheFight()
    {
        Hit(Mob, -100f, 0f, Me);

        ChangeMap("0000");

        var saved = Assert.Single(_saved);
        Assert.Equal(100, saved.TotalDamage);
        Assert.Equal("Sunfang Cliffs", saved.Map);
        Assert.Empty(_meter.Snapshot().Players);
    }

    [Fact]
    public void MapChange_KeepsCountingWhenTheOptionIsOff()
    {
        _meter.ResetOnMapChange = false;
        Hit(Mob, -100f, 0f, Me);

        ChangeMap("0000");
        RejoinAfterMapChange();
        Hit(Mob, -100f, 0f, Me);

        Assert.Empty(_saved);
        Assert.Equal(200, Assert.Single(_meter.Snapshot().Players).Damage);
    }

    [Fact]
    public void ResetBeforeCombat_StartsOverWhenThePartyFightsAgain()
    {
        _meter.ResetBeforeCombat = true;
        Combat(Me, true);
        Hit(Mob, -100f, 0f, Me);
        Combat(Me, false);

        Combat(Me, true);
        Hit(Mob, -40f, 0f, Me);

        Assert.Equal(100, Assert.Single(_saved).TotalDamage);
        Assert.Equal(40, Assert.Single(_meter.Snapshot().Players).Damage);
    }

    [Fact]
    public void ResetBeforeCombat_WaitsUntilTheWholePartyLeftCombat()
    {
        _meter.ResetBeforeCombat = true;
        Party();
        Character(Friend, "Valniaa");
        Combat(Me, true);
        Combat(Friend, true);
        Hit(Mob, -100f, 0f, Me);

        // We step out and back in while the friend is still fighting: same fight.
        Combat(Me, false);
        Combat(Me, true);

        Assert.Empty(_saved);
        Assert.Equal(100, Assert.Single(_meter.Snapshot().Players).Damage);
    }

    [Fact]
    public void Reset_WithoutSaving_DropsTheFight()
    {
        _meter.SaveBeforeReset = false;
        Hit(Mob, -100f, 0f, Me);

        _meter.Reset();

        Assert.Empty(_saved);
        Assert.Empty(_meter.Snapshot().Players);
    }

    [Fact]
    public void SaveNow_KeepsCounting()
    {
        Hit(Mob, -100f, 0f, Me);

        Assert.NotNull(_meter.SaveNow());
        Hit(Mob, -100f, 0f, Me);

        Assert.Single(_saved);
        Assert.Equal(200, Assert.Single(_meter.Snapshot().Players).Damage);
        Assert.Null(new DamageMeter(Codes, _ => null, () => "", () => null).SaveNow());
    }

    [Fact]
    public void Weapon_ComesFromTheCharacterEquipment()
    {
        Party();
        _tracker.Handle(Event(Codes.NewCharacter, new() { [0] = Friend, [1] = "Valniaa", [7] = FriendGuid.ToByteArray() }));
        _meter.Handle(Event(Codes.NewCharacter, new() { [0] = Friend, [1] = "Valniaa", [40] = new short[] { 4321, 0, 55 } }));

        Hit(Mob, -100f, 0f, Friend);
        Assert.Equal(4321, Assert.Single(_meter.Snapshot().Players).WeaponIndex);

        _meter.Handle(Event(Codes.CharacterEquipmentChanged, new() { [0] = Friend, [2] = new short[] { 1234, 0 } }));
        Hit(Mob, -100f, 0f, Friend);
        Assert.Equal(1234, Assert.Single(_meter.Snapshot().Players).WeaponIndex);
    }

    private void Join()
    {
        Both(new GameMessage(MessageKind.Response, Codes.Join, new Dictionary<byte, object>
        {
            [0] = Me,
            [1] = LocalGuid.ToByteArray(),
            [2] = "Pandas139",
            [8] = "2343"
        }));
    }

    private void RejoinAfterMapChange() => Join();

    private void ChangeMap(string cluster) =>
        Both(new GameMessage(MessageKind.Response, Codes.ChangeCluster, new Dictionary<byte, object> { [0] = cluster }));

    private void Party() =>
        Both(Event(Codes.PartyJoined, new()
        {
            [8] = new object[] { LocalGuid.ToByteArray(), FriendGuid.ToByteArray() },
            [9] = new[] { "Pandas139", "Valniaa" }
        }));

    private void Character(long objectId, string name) =>
        Both(Event(Codes.NewCharacter, new() { [0] = objectId, [1] = name }));

    private void Hit(long target, float change, float newHealth, long causer) =>
        Both(Event(Codes.HealthUpdate, new() { [0] = target, [1] = 0L, [2] = change, [3] = newHealth, [6] = causer }));

    private void Combat(long objectId, bool inCombat) =>
        Both(Event(Codes.InCombatStateUpdate, new() { [0] = objectId, [1] = inCombat, [2] = false }));

    private void Both(GameMessage message)
    {
        _tracker.Handle(message);
        _meter.Handle(message);
    }

    private static GameMessage Event(short code, Dictionary<byte, object> parameters) =>
        new(MessageKind.Event, code, parameters);
}
