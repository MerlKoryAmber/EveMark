using System.Globalization;
using Microsoft.Extensions.Options;

namespace JitaWatch;

public sealed class CollectorService : BackgroundService
{
    private readonly MarketDb _db;
    private readonly EsiClient _esi;
    private readonly MarketOptions _options;
    private readonly ILogger<CollectorService> _log;
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");

    private readonly SemaphoreSlim _orders = new(1, 1);
    private int _syncing;
    private int _ordersRunning;
    private DateTime? _announcedDue;

    public CollectorService(MarketDb db, EsiClient esi, IOptions<MarketOptions> options, ILogger<CollectorService> log)
    {
        _db = db;
        _esi = esi;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> TriggerAsync()
    {
        if (!await _orders.WaitAsync(0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await CollectOrdersAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ручной съём оборвался");
                await _db.SetStatusAsync("idle", "", ex.Message);
            }
            finally
            {
                _orders.Release();
            }
        });
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.PollHours));
        EnsureSync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var last = await _db.GetLastSnapshotUtcAsync(stoppingToken);
                var overdue = last is null || DateTime.UtcNow - last.Value >= interval;
                if (overdue)
                {
                    if (last is null)
                        _log.LogInformation("Снимка ещё нет, снимаем ордера");
                    else
                        _log.LogInformation("Час с прошлого снимка прошёл ({Last} МСК), снимаем", Msk(last.Value));
                    if (await _orders.WaitAsync(0, stoppingToken))
                    {
                        try
                        {
                            await CollectOrdersAsync(stoppingToken);
                        }
                        finally
                        {
                            _orders.Release();
                        }
                    }
                }

                last = await _db.GetLastSnapshotUtcAsync(stoppingToken);
                if (last is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var due = last.Value.Add(interval);
                if (_announcedDue != due)
                {
                    _announcedDue = due;
                    _log.LogInformation("Следующий снимок в {Due} МСК", Msk(due));
                }

                var delay = due - DateTime.UtcNow;
                if (delay > TimeSpan.FromMinutes(1))
                    delay = TimeSpan.FromMinutes(1);
                if (delay < TimeSpan.FromSeconds(15))
                    delay = TimeSpan.FromSeconds(15);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Цикл съёма оборвался");
                await _db.SetStatusAsync("idle", "", ex.Message, stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task CollectOrdersAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _ordersRunning, 1);
        try
        {
            await _db.SetStatusAsync("orders", "запрашиваем ордера The Forge", null, ct);
            var books = await _esi.FetchStationBooksAsync(
                _options.RegionId,
                _options.LocationId,
                detail => { _ = _db.SetStatusAsync("orders", detail, null); },
                ct);

            var rejected = await _db.RejectedIdsAsync(ct);
            var rows = new List<QuoteInsert>();
            foreach (var (typeId, book) in books)
            {
                if (!book.HasSell || book.BestSell < _options.MinSellPrice || rejected.Contains(typeId))
                    continue;
                rows.Add(new QuoteInsert(
                    typeId,
                    book.BestBuy,
                    book.BestSell,
                    book.BuyVolume,
                    book.SellVolume,
                    book.BuyOrders,
                    book.SellOrders));
            }

            var taken = DateTime.UtcNow;
            var count = await _db.InsertSnapshotAsync(taken, rows, ct);
            var keepDays = (await _db.GetSettingsAsync(ct)).KeepDays;
            await _db.PurgeAsync(keepDays, shrink: false, ct);
            _log.LogInformation("Снимок {At}: в базе {Count} предметов от {Min} ISK", taken, count, _options.MinSellPrice);
            await _db.SetStatusAsync("idle", $"в снимке {count.ToString(CultureInfo.InvariantCulture)} предметов", null, ct);
            EnsureSync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Съём ордеров не удался");
            await _db.SetStatusAsync("idle", "", ex.Message);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _ordersRunning, 0);
        }
    }

    private void EnsureSync()
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncLoopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Справочники не докачались");
                await _db.SetStatusAsync("idle", "", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _syncing, 0);
            }
        });
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cosmetics = await _db.RejectNamedAsync(IsCosmetic, "cosmetic", today, ct);
        if (cosmetics > 0)
            _log.LogInformation("Украшения убраны из загрузки: {Count}", cosmetics);
        var named = await _db.LatestTypeIdsAsync(ct);
        if (named.Count > 0)
            await FetchNamesAsync(named, ct);

        var history = await _db.TypesNeedingHistoryAsync(today, ct);
        if (history.Count > 0)
            await FetchHistoryAsync(history, today, ct);

        var recheck = await _db.RejectsToRecheckAsync(today, ct);
        if (recheck.Count > 0)
            await FetchHistoryAsync(recheck, today, ct);

        if (Volatile.Read(ref _ordersRunning) == 0)
            await _db.SetStatusAsync("idle", "имена и история обновлены", null, ct);
    }

    private async Task FetchNamesAsync(IReadOnlyList<int> typeIds, CancellationToken ct)
    {
        await _db.SetStatusAsync("sync", $"имена, {typeIds.Count.ToString(CultureInfo.InvariantCulture)} шт.", null, ct);
        var map = await _esi.FetchNamesAsync(typeIds, ct);
        var keep = new List<(int TypeId, string Name)>();
        var skins = new List<int>();
        foreach (var typeId in typeIds)
        {
            if (!map.TryGetValue(typeId, out var name) || string.IsNullOrWhiteSpace(name))
                continue;
            if (IsCosmetic(name))
                skins.Add(typeId);
            else
                keep.Add((typeId, name));
        }

        await _db.UpsertNamesAsync(keep, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await _db.RejectAsync(skins, "cosmetic", today, ct);
        _log.LogInformation("Имена записаны: {Keep}, украшений убрано: {Skins}", keep.Count, skins.Count);
    }

    private async Task FetchHistoryAsync(IReadOnlyList<int> typeIds, string today, CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        foreach (var chunk in typeIds.Chunk(8))
        {
            var results = await Task.WhenAll(chunk.Select(async typeId =>
            {
                try
                {
                    var bars = await _esi.FetchHistoryAsync(_options.RegionId, typeId, ct);
                    return (typeId, bars, ok: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "История типа {TypeId} не получена", typeId);
                    return (typeId, bars: (IReadOnlyList<HistoryBar>)[], ok: false);
                }
            }));

            foreach (var (typeId, bars, ok) in results)
            {
                done++;
                if (!ok)
                {
                    failed++;
                    continue;
                }

                if (!WasTraded(bars))
                {
                    await _db.RejectAsync([typeId], "no_trade", today, ct);
                    continue;
                }

                await _db.UnrejectAsync(typeId, ct);
                await _db.UpsertHistoryAsync(typeId, bars, today, _options.KeepDays, ct);
            }

            if (done % 20 == 0 || done == typeIds.Count)
            {
                await _db.SetStatusAsync(
                    "sync",
                    $"история {done} из {typeIds.Count}",
                    failed == 0 ? null : $"история: ошибок {failed.ToString(CultureInfo.InvariantCulture)}",
                    ct);
            }
        }
    }

    private static string Msk(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Moscow)
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

    private static readonly string[] CosmeticWords =
    [
        "Jacket", "Boots", "Suit", "Beret", "Goggles", "Glasses", "Mask", "Outfit",
        "Scarf", "Shoes", "Hoodie", "Shirt", "Pants", "Dress", "Skirt", "Blouse",
        "T-Shirt", "Camo", "Pattern", "Patterns"
    ];

    internal static bool IsCosmetic(string name)
    {
        if (name.Contains(" SKIN", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("Men's ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Women's ", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Apparel", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("Camo", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Cybernetic Arm", StringComparison.OrdinalIgnoreCase))
            return true;
        return CosmeticWords.Any(word => HasWord(name, word));
    }

    private static bool HasWord(string name, string word)
    {
        foreach (var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim('\'', '"', '(', ')', ',', '.', '-');
            if (token.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool WasTraded(IReadOnlyList<HistoryBar> bars) =>
        bars.Any(bar => bar.Volume > 0);
}
