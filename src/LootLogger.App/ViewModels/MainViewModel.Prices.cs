using System.Windows.Threading;
using LootLogger.Core.Network;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

/// <summary>
/// Items the game never priced (someone else looted them and the player never saw them) get a reserve price
/// from the Albion Data Project. Requests are grouped: a fight drops many items within seconds.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan PriceRetryAfterError = TimeSpan.FromMinutes(5);

    private readonly HashSet<int> _pricesWanted = [];
    private DispatcherTimer _priceTimer = null!;
    private bool _priceRequestRunning;
    private DateTime _priceRetryUtc;

    private void InitPrices()
    {
        _priceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _priceTimer.Tick += async (_, _) => await FetchReservePricesAsync();
    }

    private void AskReservePrice(LootEntry entry)
    {
        if (entry.UnitValue > 0 || !_service.Values.NeedsReserve(entry.ItemIndex, DateTime.UtcNow))
        {
            return;
        }

        _pricesWanted.Add(entry.ItemIndex);
        if (!_priceTimer.IsEnabled)
        {
            _priceTimer.Start();
        }
    }

    private async Task FetchReservePricesAsync()
    {
        var now = DateTime.UtcNow;
        if (_priceRequestRunning || now < _priceRetryUtc || Server == ServerRegion.Unknown)
        {
            // Tries again on the next tick; the server is known a few seconds after the game starts talking.
            return;
        }

        _priceTimer.Stop();
        var wanted = _pricesWanted.Where(i => _service.Values.NeedsReserve(i, now))
            .Select(i => (Index: i, Id: _service.Items.Get(i)?.UniqueName))
            .Where(w => w.Id is not null)
            .ToList();
        _pricesWanted.Clear();
        if (wanted.Count == 0)
        {
            ApplyKnownPrices();
            return;
        }

        _priceRequestRunning = true;
        try
        {
            var prices = await _service.FetchReservePricesAsync(Server, wanted.Select(w => w.Id!).ToList());
            if (prices is null)
            {
                foreach (var w in wanted)
                {
                    _pricesWanted.Add(w.Index);
                }

                _priceRetryUtc = DateTime.UtcNow + PriceRetryAfterError;
                _priceTimer.Start();
                return;
            }

            foreach (var w in wanted)
            {
                _service.Values.SetReserve(w.Index, prices.GetValueOrDefault(w.Id!), DateTime.UtcNow);
            }

            ApplyKnownPrices();
        }
        finally
        {
            _priceRequestRunning = false;
            if (_pricesWanted.Count > 0 && !_priceTimer.IsEnabled)
            {
                _priceTimer.Start();
            }
        }
    }

    /// <summary>Puts newly known prices on rows that had none, and the game's own price on rows that had a reserve one.</summary>
    private void ApplyKnownPrices()
    {
        if (_session.UpdateLoot(WithKnownPrice) == 0)
        {
            return;
        }

        foreach (var row in LootRows)
        {
            if (WithKnownPrice(row.Entry) is { } updated)
            {
                row.UpdateEntry(updated);
            }
        }

        _lootStatsDirty = true;
        _chestDirty = true;
        if (ShowLootStats)
        {
            RefreshLootStats();
        }

        RefreshTiles();
        OnPropertyChanged(nameof(LootSummary));
    }

    private LootEntry? WithKnownPrice(LootEntry entry)
    {
        var (value, isReserve) = _service.Values.GetWithReserve(entry.ItemIndex);
        var better = value > 0 && (entry.UnitValue == 0 || (entry.IsReservePrice && !isReserve));
        return better ? entry with { UnitValue = value, IsReservePrice = isReserve } : null;
    }
}
