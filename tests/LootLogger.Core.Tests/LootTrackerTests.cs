using LootLogger.Core.Data;
using LootLogger.Core.Protocol;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class LootTrackerTests
{
    private static readonly GameCodes Codes = new();
    private static readonly ItemDatabase Items = ItemDatabase.LoadBuiltIn();
    private static readonly ClusterDatabase Clusters = ClusterDatabase.LoadBuiltIn();

    private static readonly Guid LocalGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LocalInteract = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContainerGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly AlbionMessageParser _parser = new();
    private readonly LootTracker _tracker;
    private readonly List<LootEntry> _loot = [];
    private readonly List<KillEntry> _kills = [];
    private DateTime _now = new(2026, 10, 6, 2, 49, 41, DateTimeKind.Utc);

    public LootTrackerTests()
    {
        _tracker = new LootTracker(Codes, Items, Clusters, new MarketValueCache(), () => _now);
        _parser.MessageReceived += _tracker.Handle;
        _tracker.LootAdded += _loot.Add;
        _tracker.KillAdded += _kills.Add;
    }

    [Fact]
    public void Join_IdentifiesPlayerAndMap()
    {
        LocalPlayer? identified = null;
        _tracker.PlayerIdentified += p => identified = p;

        Join();

        Assert.Equal(new LocalPlayer("Pandas139", "Take Care", ""), identified);
        Assert.Equal("Sunfang Cliffs", _tracker.ClusterName);
    }

    [Fact]
    public void OtherGrabbedLoot_RecordsLootWithGuildsItemAndMap()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewCharacter, new() { [0] = 900L, [1] = "XAgiota", [8] = "PlVAS" }));
        Receive(PhotonPackets.Event(Codes.NewCharacter, new() { [0] = 901L, [1] = "Valniaa", [8] = "TU MAMMA", [51] = "ALLY" }));

        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "XAgiota", [2] = "Valniaa", [4] = 3787, [5] = 1 }));

        var entry = Assert.Single(_loot);
        Assert.Equal("Valniaa", entry.LootedByName);
        Assert.Equal("TU MAMMA", entry.LootedByGuild);
        Assert.Equal("ALLY", entry.LootedByAlliance);
        Assert.Equal("XAgiota", entry.LootedFromName);
        Assert.Equal("PlVAS", entry.LootedFromGuild);
        Assert.Equal("T6_ARMOR_PLATE_HELL@3", entry.ItemId);
        Assert.Equal("Master's Demon Armor", entry.ItemNameEnglish);
        Assert.Equal("Sunfang Cliffs", entry.Cluster);
    }

    [Fact]
    public void OtherGrabbedLoot_IgnoresSilver()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [0] = 1L, [2] = "Valniaa", [3] = true, [5] = 15000 }));
        Assert.Empty(_loot);
    }

    [Fact]
    public void LocalPlayerTakingFromBag_IsRecordedOnceWithMarketValue()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 77L, [3] = "XAgiota" }));
        Receive(PhotonPackets.Event(Codes.NewSimpleItem, new() { [0] = 500L, [1] = 570, [2] = 4, [4] = 124_860_000L }));
        Receive(PhotonPackets.Event(Codes.AttachItemContainer, new() { [0] = 77L, [1] = ContainerGuid.ToByteArray(), [3] = new long[] { 500, 0 } }));

        Receive(PhotonPackets.Request(Codes.InventoryMoveItem, new() { [0] = 0, [1] = ContainerGuid.ToByteArray(), [4] = LocalInteract.ToByteArray() }));
        // The server also broadcasts the same pickup.
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "XAgiota", [2] = "Pandas139", [4] = 570, [5] = 4 }));

        var entry = Assert.Single(_loot);
        Assert.Equal("Pandas139", entry.LootedByName);
        Assert.Equal("Take Care", entry.LootedByGuild);
        Assert.Equal("T7_POTION_REVIVE", entry.ItemId);
        Assert.Equal(4, entry.Quantity);
        Assert.Equal(12486, entry.UnitValue);
    }

    [Fact]
    public void TrashItems_AreHidden()
    {
        Join();
        Assert.Equal("T1_TRASH", Items.Get(2043)?.UniqueName);
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "A", [2] = "B", [4] = 2043, [5] = 1 }));
        Assert.Empty(_loot);
    }

    [Fact]
    public void SamePickupLater_IsNotTreatedAsDuplicate()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "A", [2] = "B", [4] = 570, [5] = 1 }));
        _now = _now.AddSeconds(5);
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "A", [2] = "B", [4] = 570, [5] = 1 }));
        Assert.Equal(2, _loot.Count);
    }

    [Fact]
    public void LootFromMob_IsNamedMob()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "@MOB_KEEPER_BRUTE", [2] = "Pandas139", [4] = 570, [5] = 1 }));
        Assert.Equal(LootTracker.MobName, Assert.Single(_loot).LootedFromName);
    }

    [Fact]
    public void PartyOnly_DropsLootFromStrangers()
    {
        Join();
        _tracker.PartyOnly = true;
        var friend = Guid.NewGuid();
        Receive(PhotonPackets.Event(Codes.PartyJoined, new()
        {
            [8] = new object[] { LocalGuid.ToByteArray(), friend.ToByteArray() },
            [9] = new[] { "Pandas139", "Valniaa" }
        }));

        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "Stranger1", [2] = "Stranger2", [4] = 570, [5] = 1 }));
        Receive(PhotonPackets.Event(Codes.OtherGrabbedLoot, new() { [1] = "Stranger1", [2] = "Valniaa", [4] = 3787, [5] = 1 }));

        Assert.Equal("Valniaa", Assert.Single(_loot).LootedByName);
        Assert.Contains("Valniaa", _tracker.PartyMembers);
    }

    [Fact]
    public void Died_RecordsKillWithGuilds()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.Died, new() { [1] = 900L, [2] = "XAgiota", [3] = "PlVAS", [9] = 1L, [10] = "Pandas139", [11] = "Take Care" }));

        var kill = Assert.Single(_kills);
        Assert.Equal("XAgiota", kill.Died);
        Assert.Equal("PlVAS", kill.DiedGuild);
        Assert.Equal("Pandas139", kill.KilledBy);
        Assert.Equal("Take Care", kill.KilledByGuild);
        Assert.Equal("Sunfang Cliffs", kill.Cluster);
    }

    [Fact]
    public void ChangeCluster_UpdatesMapName()
    {
        Join();
        Receive(PhotonPackets.Response(Codes.ChangeCluster, new() { [0] = "2310" }));
        Assert.Equal("Sunfang Ravine", _tracker.ClusterName);
    }

    // Taken from a real recording with ExitLag on: the player's own requests never show up, only the server's replies.
    private static readonly Guid InventoryGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private void OpenBag(long bagId, params (long Id, int Item, int Qty)[] items)
    {
        foreach (var (id, item, qty) in items)
        {
            Receive(PhotonPackets.Event(Codes.NewSimpleItem, new() { [0] = id, [1] = item, [2] = qty }));
        }

        Receive(PhotonPackets.Event(Codes.AttachItemContainer, new() { [0] = bagId, [1] = ContainerGuid.ToByteArray(), [3] = items.Select(i => i.Id).ToArray() }));
    }

    [Fact]
    public void ItemMovedFromBagIntoInventory_IsRecordedWithoutTheRequest()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 99114L, [3] = "@MOB_UNDEAD_ARCHER_STANDARD" }));
        OpenBag(99114, (99115, 2204, 1));

        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 99115L, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));

        var entry = Assert.Single(_loot);
        Assert.Equal("Pandas139", entry.LootedByName);
        Assert.Equal(LootTracker.MobName, entry.LootedFromName);
        Assert.Equal("T2_OFF_BOOK", entry.ItemId);
    }

    [Fact]
    public void ItemsStackingOntoOneAlreadyInInventory_AreEachRecorded()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 49215L, [3] = "Pandas139" }));
        OpenBag(49215, (49208, 105, 1), (49207, 105, 1), (49206, 105, 1));

        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 49208L, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));
        _now = _now.AddSeconds(1);
        Receive(PhotonPackets.Event(Codes.NewSimpleItem, new() { [0] = 49208L, [1] = 105, [2] = 2 }));
        Receive(PhotonPackets.Event(Codes.InventoryDeleteItem, new() { [0] = 49207L, [1] = 3 }));
        _now = _now.AddSeconds(1);
        Receive(PhotonPackets.Event(Codes.NewSimpleItem, new() { [0] = 49208L, [1] = 105, [2] = 3 }));
        Receive(PhotonPackets.Event(Codes.InventoryDeleteItem, new() { [0] = 49206L, [1] = 4 }));

        Assert.Equal(3, _loot.Count);
        Assert.All(_loot, l => Assert.Equal("T3_FARM_CHICKEN_BABY", l.ItemId));
    }

    [Fact]
    public void ItemTakenFromBagBySomeoneElse_IsNotCountedAsOurs()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 77L, [3] = "XAgiota" }));
        OpenBag(77, (500, 105, 1));

        Receive(PhotonPackets.Event(Codes.InventoryDeleteItem, new() { [0] = 500L, [1] = 0 }));

        Assert.Empty(_loot);
    }

    [Fact]
    public void ChestLoot_IsRecordedAsChest()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLootChest, new() { [0] = 113462L, [3] = "TREASURE_COFFER_SOLO" }));
        OpenBag(113462, (114042, 2005, 3));

        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 114042L, [1] = 1, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));

        Assert.Equal(LootTracker.ChestName, Assert.Single(_loot).LootedFromName);
    }

    [Fact]
    public void BankOrOtherContainers_AreNotLoot()
    {
        Join();
        OpenBag(6, (724841, 105, 1));

        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 724841L, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));

        Assert.Empty(_loot);
    }

    [Fact]
    public void BodiesSentBeforeTheJoinResponse_AreKept()
    {
        // On a map change the game sends the bodies first and the player's own info right after.
        Receive(PhotonPackets.Response(Codes.ChangeCluster, new() { [0] = "2343" }));
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 77L, [3] = "XAgiota" }));
        Join();
        OpenBag(77, (500, 105, 1));

        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 500L, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));

        Assert.Equal("XAgiota", Assert.Single(_loot).LootedFromName);
    }

    [Fact]
    public void WithoutExitLag_RequestsAndRepliesCountEachItemOnce()
    {
        Join();
        Receive(PhotonPackets.Event(Codes.NewLoot, new() { [0] = 77L, [3] = "XAgiota" }));
        OpenBag(77, (500, 105, 1), (501, 2204, 1));

        Receive(PhotonPackets.Request(Codes.InventoryMoveItem, new() { [0] = 0, [1] = ContainerGuid.ToByteArray(), [4] = LocalInteract.ToByteArray() }));
        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 500L, [2] = InventoryGuid.ToByteArray(), [3] = 2 }));
        Receive(PhotonPackets.Request(Codes.InventoryMoveItem, new() { [0] = 1, [1] = ContainerGuid.ToByteArray(), [4] = LocalInteract.ToByteArray() }));
        Receive(PhotonPackets.Event(Codes.InventoryPutItem, new() { [0] = 501L, [2] = InventoryGuid.ToByteArray(), [3] = 3 }));

        Assert.Equal(["T3_FARM_CHICKEN_BABY", "T2_OFF_BOOK"], _loot.Select(l => l.ItemId));
    }

    private void Join()
    {
        Receive(PhotonPackets.Response(Codes.Join, new()
        {
            [0] = 1L,
            [1] = LocalGuid.ToByteArray(),
            [2] = "Pandas139",
            [8] = "2343",
            [54] = LocalInteract.ToByteArray(),
            [58] = "Take Care"
        }));
    }

    private void Receive(byte[] packet) => _parser.ReceivePacket(packet);
}
