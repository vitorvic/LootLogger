using LootLogger.Core.Data;
using LootLogger.Core.Protocol;

namespace LootLogger.Core.Tracking;

/// <summary>
/// Follows the game messages and turns them into loot and death records.
/// The flow and parameter numbers follow AlbionOnline-StatisticsAnalysis (GPL-3.0).
/// </summary>
public sealed class LootTracker
{
    public const string MobName = "MOB";
    public const string ChestName = "CHEST";

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StackWindow = TimeSpan.FromSeconds(1);

    private readonly GameCodes _codes;
    private readonly ItemDatabase _items;
    private readonly ClusterDatabase _clusters;
    private readonly MarketValueCache _values;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();

    private readonly Dictionary<string, PlayerInfo> _playersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, PlayerInfo> _playersByObjectId = new();
    private readonly Dictionary<long, string> _bodies = new();
    private readonly Dictionary<long, DiscoveredItem> _discoveredItems = new();
    private readonly Dictionary<Guid, string> _party = new();
    private readonly List<(DateTime Time, string Key, bool IsLocal)> _recentLoot = [];
    private readonly HashSet<long> _lootedItemObjects = [];

    private ItemContainer? _currentContainer;
    private (int ItemIndex, DateTime Time)? _lastStackGrowth;
    private Guid? _localInteractGuid;
    private string _clusterIndex = string.Empty;

    public LootTracker(GameCodes codes, ItemDatabase items, ClusterDatabase clusters, MarketValueCache values, Func<DateTime>? utcNow = null)
    {
        _codes = codes;
        _items = items;
        _clusters = clusters;
        _values = values;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public event Action<LootEntry>? LootAdded;
    public event Action<KillEntry>? KillAdded;
    public event Action<LocalPlayer>? PlayerIdentified;
    /// <summary>Map name and tier (0 when unknown).</summary>
    public event Action<string, int>? ClusterChanged;
    public event Action? PartyChanged;

    /// <summary>When on, only loot taken by or from party members is recorded.</summary>
    public bool PartyOnly { get; set; }

    public LocalPlayer? LocalPlayer { get; private set; }

    public string ClusterName => _clusters.DisplayName(_clusterIndex);

    public int ClusterTier => _clusters.Tier(_clusterIndex);

    public IReadOnlyCollection<string> PartyMembers
    {
        get
        {
            lock (_lock)
            {
                return _party.Values.ToList();
            }
        }
    }

    /// <summary>Name of the player with this object id on the current map, when it is us or someone in our party.</summary>
    public string? PartyMemberName(long objectId)
    {
        lock (_lock)
        {
            if (!_playersByObjectId.TryGetValue(objectId, out var player))
            {
                return null;
            }

            return IsInParty(player.Name) || string.Equals(player.Name, LocalPlayer?.Name, StringComparison.OrdinalIgnoreCase)
                ? player.Name
                : null;
        }
    }

    public void Handle(GameMessage message)
    {
        List<Action> notifications = [];
        lock (_lock)
        {
            Dispatch(message, notifications);
        }

        // Raise events outside the lock so listeners can read tracker state.
        foreach (var notify in notifications)
        {
            notify();
        }
    }

    private void Dispatch(GameMessage m, List<Action> notify)
    {
        var p = m.Parameters;
        switch (m.Kind)
        {
            case MessageKind.Response when m.Code == _codes.Join:
                OnJoin(p, notify);
                break;
            case MessageKind.Response when m.Code == _codes.ChangeCluster:
                OnChangeCluster(p, notify);
                break;
            case MessageKind.Request when m.Code == _codes.InventoryMoveItem:
                OnLocalMoveItem(p, notify);
                break;
            case MessageKind.Request when m.Code == _codes.InventoryMoveGivenItems:
                OnLocalMoveGivenItems(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.NewCharacter:
                OnNewCharacter(p);
                break;
            case MessageKind.Event when m.Code == _codes.NewEquipmentItem || m.Code == _codes.NewSimpleItem:
                OnNewItem(p);
                break;
            case MessageKind.Event when m.Code == _codes.NewLoot:
                OnNewLoot(p);
                break;
            case MessageKind.Event when m.Code == _codes.NewLootChest:
                OnNewLootChest(p);
                break;
            case MessageKind.Event when m.Code == _codes.AttachItemContainer:
                OnAttachContainer(p);
                break;
            case MessageKind.Event when m.Code == _codes.DetachItemContainer:
                _currentContainer = null;
                break;
            case MessageKind.Event when m.Code == _codes.InventoryPutItem:
                OnInventoryPut(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.InventoryDeleteItem:
                OnInventoryDelete(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.OtherGrabbedLoot:
                OnOtherGrabbedLoot(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.Died:
                OnDied(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.PartyJoined:
                OnPartyJoined(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.PartyPlayerJoined:
                OnPartyPlayerJoined(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.PartyPlayerLeft:
                OnPartyPlayerLeft(p, notify);
                break;
            case MessageKind.Event when m.Code == _codes.PartyDisbanded:
                _party.Clear();
                AddLocalPlayerToParty();
                notify.Add(() => PartyChanged?.Invoke());
                break;
        }
    }

    // Join response: 0 object id, 1 guid, 2 name, 8 map, 54 interact guid, 58 guild, 79 alliance.
    private void OnJoin(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var name = p.GetString(2);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // The new map's bodies and players arrive just before this response, so keep them.
        var player = new PlayerInfo(name, p.GetString(58) ?? string.Empty, p.GetString(79) ?? string.Empty) { Guid = p.GetGuid(1) };
        RememberPlayer(player, p.GetLong(0));
        _localInteractGuid = p.GetGuid(54);
        LocalPlayer = new LocalPlayer(player.Name, player.Guild, player.Alliance);
        AddLocalPlayerToParty();

        var cluster = p.GetString(8);
        if (!string.IsNullOrEmpty(cluster))
        {
            _clusterIndex = cluster;
        }

        var local = LocalPlayer;
        var clusterName = ClusterName;
        var clusterTier = ClusterTier;
        notify.Add(() => PlayerIdentified?.Invoke(local));
        notify.Add(() => ClusterChanged?.Invoke(clusterName, clusterTier));
    }

    private void OnChangeCluster(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var cluster = p.GetString(0);
        if (string.IsNullOrEmpty(cluster))
        {
            return;
        }

        _clusterIndex = cluster;
        ResetMapState();
        var clusterName = ClusterName;
        var clusterTier = ClusterTier;
        notify.Add(() => ClusterChanged?.Invoke(clusterName, clusterTier));
    }

    // NewCharacter: 0 object id, 1 name, 7 guid, 8 guild, 51 alliance.
    private void OnNewCharacter(IReadOnlyDictionary<byte, object> p)
    {
        var name = p.GetString(1);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        RememberPlayer(new PlayerInfo(name, p.GetString(8) ?? string.Empty, p.GetString(51) ?? string.Empty) { Guid = p.GetGuid(7) }, p.GetLong(0));
    }

    // NewEquipmentItem / NewSimpleItem: 0 object id, 1 item number, 2 amount, 4 estimated value.
    private void OnNewItem(IReadOnlyDictionary<byte, object> p)
    {
        var objectId = p.GetLong(0);
        var itemIndex = p.GetInt(1);
        if (objectId is null || itemIndex is null)
        {
            return;
        }

        var value = p.GetLong(4) ?? 0;
        _values.SetFromGame(itemIndex.Value, value);
        var quantity = Math.Max(1, p.GetInt(2) ?? 1);

        // A stack outside the open bag growing is how a pickup that merges into the bag shows up.
        if (_discoveredItems.TryGetValue(objectId.Value, out var known) && known.ItemIndex == itemIndex.Value && quantity > known.Quantity
            && _currentContainer?.SlotObjectIds.Contains(objectId.Value) != true)
        {
            _lastStackGrowth = (itemIndex.Value, _utcNow());
        }

        _discoveredItems[objectId.Value] = new DiscoveredItem(itemIndex.Value, quantity);
    }

    // NewLoot: 0 object id of the bag, 3 name of whoever it belonged to.
    private void OnNewLoot(IReadOnlyDictionary<byte, object> p)
    {
        var objectId = p.GetLong(0);
        var body = p.GetString(3);
        if (objectId is not null && !string.IsNullOrEmpty(body))
        {
            _bodies[objectId.Value] = body;
        }
    }

    // NewLootChest: 0 object id. Chests opened like bags (treasure coffers, dungeon chests).
    private void OnNewLootChest(IReadOnlyDictionary<byte, object> p)
    {
        if (p.GetLong(0) is { } objectId)
        {
            _bodies[objectId] = ChestName;
        }
    }

    // InventoryPutItem: 0 item object id, 1 slot, 2 container guid it went into.
    // Seen for every pickup, unlike the player's own requests, which ExitLag routes out of sight.
    private void OnInventoryPut(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var objectId = p.GetLong(0);
        if (objectId is null || !TryGetOpenLootBag(out var container, out var body) || p.GetGuid(2) == container.Guid
            || !container.SlotObjectIds.Contains(objectId.Value))
        {
            return;
        }

        AddLocalLoot(objectId.Value, body, notify);

        // It now lives in our bag; later items can stack onto it. Keep the slot so slot numbers still line up.
        container.SlotObjectIds[container.SlotObjectIds.IndexOf(objectId.Value)] = 0;
    }

    // InventoryDeleteItem: 0 item object id. A bag item that vanished as one of our stacks grew was merged into it.
    private void OnInventoryDelete(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var objectId = p.GetLong(0);
        if (objectId is null || !TryGetOpenLootBag(out var container, out var body) || !container.SlotObjectIds.Contains(objectId.Value)
            || !_discoveredItems.TryGetValue(objectId.Value, out var item))
        {
            return;
        }

        if (_lastStackGrowth is { } growth && growth.ItemIndex == item.ItemIndex && _utcNow() - growth.Time <= StackWindow)
        {
            _lastStackGrowth = null;
            AddLocalLoot(objectId.Value, body, notify);
        }
    }

    private bool TryGetOpenLootBag(out ItemContainer container, out string body)
    {
        container = null!;
        body = string.Empty;
        if (LocalPlayer is null || _currentContainer is not { } current || !_bodies.TryGetValue(current.ObjectId, out var name))
        {
            return false;
        }

        container = current;
        body = name;
        return true;
    }

    // AttachItemContainer: 0 object id, 1 container guid, 3 item object id per slot.
    private void OnAttachContainer(IReadOnlyDictionary<byte, object> p)
    {
        var objectId = p.GetLong(0);
        var guid = p.GetGuid(1);
        if (objectId is null || guid is null)
        {
            return;
        }

        _currentContainer = new ItemContainer(objectId.Value, guid.Value, p.GetLongList(3));
    }

    // OtherGrabbedLoot: 1 looted from, 2 looted by, 3 is silver, 4 item number, 5 amount.
    private void OnOtherGrabbedLoot(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        if (p.GetBool(3))
        {
            return;
        }

        var lootedBy = p.GetString(2);
        var itemIndex = p.GetInt(4);
        if (string.IsNullOrEmpty(lootedBy) || itemIndex is null)
        {
            return;
        }

        AddLoot(lootedBy, p.GetString(1) ?? string.Empty, itemIndex.Value, Math.Max(1, p.GetInt(5) ?? 1), notify);
    }

    // InventoryMoveItem request (local player drags one slot): 0 slot, 1 container guid, 4 interact guid.
    private void OnLocalMoveItem(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var slot = p.GetInt(0);
        if (slot is null || !TryGetLocalLootBody(p.GetGuid(1), p.GetGuid(4), out var container, out var body))
        {
            return;
        }

        if (slot.Value >= 0 && slot.Value < container.SlotObjectIds.Count)
        {
            AddLocalLoot(container.SlotObjectIds[slot.Value], body, notify);
        }
    }

    // InventoryMoveGivenItems request (local player takes several): 0 container guid, 2 interact guid, 4 item object ids.
    private void OnLocalMoveGivenItems(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        if (!TryGetLocalLootBody(p.GetGuid(0), p.GetGuid(2), out var container, out var body))
        {
            return;
        }

        foreach (var objectId in p.GetLongList(4).Distinct())
        {
            if (container.SlotObjectIds.Contains(objectId))
            {
                AddLocalLoot(objectId, body, notify);
            }
        }
    }

    // Died: 1 died object id, 2 died, 3 died guild, 9 killer object id, 10 killed by, 11 killer guild.
    private void OnDied(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var died = p.GetString(2);
        if (string.IsNullOrEmpty(died))
        {
            return;
        }

        var killedBy = p.GetString(10) ?? string.Empty;
        var diedInfo = FindPlayer(p.GetLong(1), died);
        var killerInfo = FindPlayer(p.GetLong(9), killedBy);

        var entry = new KillEntry(
            _utcNow(),
            died,
            NonEmpty(p.GetString(3), diedInfo?.Guild),
            diedInfo?.Alliance ?? string.Empty,
            killedBy,
            NonEmpty(p.GetString(11), killerInfo?.Guild),
            killerInfo?.Alliance ?? string.Empty,
            ClusterName);
        notify.Add(() => KillAdded?.Invoke(entry));
    }

    // PartyJoined: 8 member guids, 9 member names.
    private void OnPartyJoined(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var guids = p.GetGuidList(8);
        var names = p.GetStringList(9);
        _party.Clear();
        for (var i = 0; i < Math.Min(guids.Count, names.Count); i++)
        {
            _party[guids[i]] = names[i];
        }

        AddLocalPlayerToParty();
        notify.Add(() => PartyChanged?.Invoke());
    }

    // PartyPlayerJoined: 1 guid, 2 name.
    private void OnPartyPlayerJoined(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        var guid = p.GetGuid(1);
        var name = p.GetString(2);
        if (guid is not null && !string.IsNullOrEmpty(name))
        {
            _party[guid.Value] = name;
            notify.Add(() => PartyChanged?.Invoke());
        }
    }

    // PartyPlayerLeft: 1 guid.
    private void OnPartyPlayerLeft(IReadOnlyDictionary<byte, object> p, List<Action> notify)
    {
        if (p.GetGuid(1) is { } guid && _party.Remove(guid))
        {
            notify.Add(() => PartyChanged?.Invoke());
        }
    }

    private bool TryGetLocalLootBody(Guid? containerGuid, Guid? interactGuid, out ItemContainer container, out string body)
    {
        container = null!;
        body = string.Empty;
        if (LocalPlayer is null || containerGuid is null || interactGuid is null || interactGuid != _localInteractGuid)
        {
            return false;
        }

        if (_currentContainer is not { } current || current.Guid != containerGuid || !_bodies.TryGetValue(current.ObjectId, out var name))
        {
            return false;
        }

        container = current;
        body = name;
        return true;
    }

    private void AddLocalLoot(long itemObjectId, string body, List<Action> notify)
    {
        // Several messages can report the same pickup; each item object counts once.
        if (LocalPlayer is null || !_discoveredItems.TryGetValue(itemObjectId, out var item) || !_lootedItemObjects.Add(itemObjectId))
        {
            return;
        }

        AddLoot(LocalPlayer.Name, body, item.ItemIndex, item.Quantity, notify, isLocal: true);
    }

    private void AddLoot(string lootedBy, string lootedFrom, int itemIndex, int quantity, List<Action> notify, bool isLocal = false)
    {
        // Broken leftovers of destroyed gear are worth almost nothing; other loggers hide them too.
        var item = _items.Get(itemIndex);
        if (item?.UniqueName.EndsWith("_TRASH", StringComparison.Ordinal) == true)
        {
            return;
        }

        var isMob = lootedFrom.Contains("@MOB", StringComparison.OrdinalIgnoreCase);
        var fromName = isMob ? MobName : lootedFrom;

        if (PartyOnly && !IsInParty(lootedBy) && !IsInParty(fromName))
        {
            return;
        }

        var now = _utcNow();
        if (IsDuplicate(now, $"{lootedBy}|{fromName}|{itemIndex}|{quantity}", isLocal))
        {
            return;
        }

        var by = _playersByName.GetValueOrDefault(lootedBy);
        var from = isMob ? null : _playersByName.GetValueOrDefault(fromName);

        var entry = new LootEntry(
            now,
            lootedBy,
            by?.Guild ?? string.Empty,
            by?.Alliance ?? string.Empty,
            itemIndex,
            item?.UniqueName ?? itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item?.EnglishName ?? item?.UniqueName ?? itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quantity,
            fromName,
            from?.Guild ?? string.Empty,
            from?.Alliance ?? string.Empty,
            _values.Get(itemIndex),
            ClusterName);

        notify.Add(() => LootAdded?.Invoke(entry));
    }

    /// <summary>
    /// The same pickup can arrive twice: once as our own move and once as a broadcast.
    /// Two identical ones from the same source are two pickups (two equal stacks taken at once, say):
    /// our own moves are counted per item, and messages the game sent again are dropped when they are read.
    /// </summary>
    private bool IsDuplicate(DateTime now, string key, bool isLocal)
    {
        _recentLoot.RemoveAll(r => now - r.Time > DuplicateWindow);
        if (_recentLoot.Any(r => r.Key == key && r.IsLocal != isLocal))
        {
            return true;
        }

        _recentLoot.Add((now, key, isLocal));
        return false;
    }

    private bool IsInParty(string name) => _party.Values.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private void AddLocalPlayerToParty()
    {
        if (LocalPlayer is not null && _playersByName.TryGetValue(LocalPlayer.Name, out var info) && info.Guid is { } guid)
        {
            _party[guid] = LocalPlayer.Name;
        }
    }

    private void RememberPlayer(PlayerInfo player, long? objectId)
    {
        if (_playersByName.TryGetValue(player.Name, out var known))
        {
            // Keep what we already know when the new message leaves a field empty.
            player = player with
            {
                Guild = NonEmpty(player.Guild, known.Guild),
                Alliance = NonEmpty(player.Alliance, known.Alliance),
                Guid = player.Guid ?? known.Guid
            };
        }

        _playersByName[player.Name] = player;
        if (objectId is not null)
        {
            _playersByObjectId[objectId.Value] = player;
        }
    }

    private PlayerInfo? FindPlayer(long? objectId, string name)
    {
        if (objectId is not null && _playersByObjectId.TryGetValue(objectId.Value, out var byId))
        {
            return byId;
        }

        return string.IsNullOrEmpty(name) ? null : _playersByName.GetValueOrDefault(name);
    }

    private void ResetMapState()
    {
        _bodies.Clear();
        _discoveredItems.Clear();
        _playersByObjectId.Clear();
        _currentContainer = null;
        _lastStackGrowth = null;
        _lootedItemObjects.Clear();
    }

    private static string NonEmpty(string? first, string? second) => !string.IsNullOrEmpty(first) ? first : second ?? string.Empty;

    private sealed record PlayerInfo(string Name, string Guild, string Alliance)
    {
        public Guid? Guid { get; init; }
    }

    private sealed record DiscoveredItem(int ItemIndex, int Quantity);

    private sealed record ItemContainer(long ObjectId, Guid Guid, List<long> SlotObjectIds);
}
