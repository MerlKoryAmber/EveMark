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
    private string? _syncUtcDay;

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

        await _db.SetStatusAsync("orders", "запуск съёма…", null);
        _log.LogInformation("Ручной съём запущен");
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

                var utcDay = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!string.Equals(_syncUtcDay, utcDay, StringComparison.Ordinal))
                {
                    _syncUtcDay = utcDay;
                    _log.LogInformation("Новый UTC-день {Day}, обновляем историю", utcDay);
                    EnsureSync();
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
            await _db.SetStatusAsync("orders", "запуск съёма…", null, ct);
            var rejected = await _db.RejectedIdsAsync(ct);
            var board = new Dictionary<int, BoardMerge>();
            var locationRows = new List<LocationQuoteInsert>();
            var meta = new Dictionary<long, LocationMetaRow>();
            foreach (var hub in TradeHubs.NpcHubs)
            {
                meta[hub.LocationId] = new LocationMetaRow(
                    hub.LocationId, hub.Title, hub.SystemId, hub.SystemName, hub.RegionId, hub.Key);
            }

            foreach (var regionId in TradeHubs.MarketRegionIds)
            {
                var label = regionId switch
                {
                    TradeHubs.ForgeRegionId => "The Forge",
                    10000043 => "Domain",
                    10000032 => "Sinq Laison",
                    10000030 => "Heimatar",
                    10000042 => "Metropolis",
                    _ => regionId.ToString(CultureInfo.InvariantCulture)
                };
                await _db.SetStatusAsync("orders", $"ордера {label}", null, ct);
                var scan = await _esi.FetchRegionBooksAsync(
                    regionId,
                    label,
                    detail => { _ = _db.SetStatusAsync("orders", detail, null); },
                    ct);

                HashSet<long>? perimeterIds = null;
                if (regionId == TradeHubs.ForgeRegionId)
                {
                    var unknownStations = scan.ByLocation.Keys
                        .Where(id => TradeHubs.NpcByLocation(id) is null && id <= int.MaxValue)
                        .ToArray();
                    if (unknownStations.Length > 0)
                    {
                        await _db.SetStatusAsync("orders", "станции Perimeter", null, ct);
                        perimeterIds = [];
                        foreach (var locationId in unknownStations)
                        {
                            try
                            {
                                var station = await _esi.FetchStationAsync(locationId, ct);
                                if (station is null || station.SystemId != TradeHubs.PerimeterSystemId)
                                    continue;
                                perimeterIds.Add(locationId);
                                meta[locationId] = new LocationMetaRow(
                                    locationId,
                                    station.Name,
                                    TradeHubs.PerimeterSystemId,
                                    "Perimeter",
                                    TradeHubs.ForgeRegionId,
                                    "perimeter");
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Станция {Id} не разобрана", locationId);
                            }
                        }

                        _log.LogInformation(
                            "Perimeter NPC-станций в снимке: {Count}. Структуры игроков ESI без логина не отдаёт.",
                            perimeterIds.Count);
                    }
                }

                foreach (var (locationId, books) in scan.ByLocation)
                {
                    var keep = TradeHubs.NpcByLocation(locationId) is not null
                        || (perimeterIds is not null && perimeterIds.Contains(locationId));
                    if (!keep)
                        continue;

                    var boardSpot = locationId == TradeHubs.JitaStationId
                        || (perimeterIds is not null && perimeterIds.Contains(locationId));

                    foreach (var (typeId, book) in books)
                    {
                        if (!book.HasSell || rejected.Contains(typeId))
                            continue;
                        if (book.BestSell >= _options.MinHaulSellPrice)
                        {
                            locationRows.Add(new LocationQuoteInsert(
                                typeId,
                                locationId,
                                regionId,
                                book.BestBuy,
                                book.BestSell,
                                book.BuyVolume,
                                book.SellVolume,
                                book.BuyOrders,
                                book.SellOrders,
                                book.BuyTouch,
                                book.SellTouch));
                        }

                        if (!boardSpot || book.BestSell < _options.MinSellPrice)
                            continue;
                        if (!board.TryGetValue(typeId, out var merged))
                        {
                            merged = new BoardMerge();
                            board[typeId] = merged;
                        }

                        merged.Add(book);
                    }
                }
            }

            var boardRows = board.Select(kv => kv.Value.ToInsert(kv.Key)).ToList();

            await _db.UpsertLocationsAsync(meta.Values.ToList(), ct);
            await WarmRoutesAsync(meta.Values.Select(m => m.SystemId).Distinct().ToArray(), ct);

            var taken = DateTime.UtcNow;
            var count = await _db.InsertSnapshotAsync(taken, boardRows, locationRows, ct);
            var keepDays = (await _db.GetSettingsAsync(ct)).KeepDays;
            await _db.PurgeAsync(keepDays, shrink: false, ct);
            _log.LogInformation(
                "Снимок {At}: доска {Board}, локаций {Loc}, строк развоза {Haul}",
                taken, count, meta.Count, locationRows.Count);
            await _db.SetStatusAsync(
                "idle",
                $"в снимке {count.ToString(CultureInfo.InvariantCulture)} предметов (Jita+Perimeter)",
                null,
                ct);
            var needVol = await _db.TypesMissingVolumeAsync(200, ct);
            if (needVol.Count > 0)
                await FetchVolumesAsync(needVol, ct);
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

    private async Task WarmRoutesAsync(IReadOnlyList<int> systems, CancellationToken ct)
    {
        for (var i = 0; i < systems.Count; i++)
        {
            for (var j = 0; j < systems.Count; j++)
            {
                var from = systems[i];
                var to = systems[j];
                if (await _db.GetRouteJumpsAsync(from, to, ct) is not null)
                    continue;
                try
                {
                    var jumps = await _esi.FetchRouteJumpsAsync(from, to, ct);
                    await _db.SaveRouteJumpsAsync(from, to, jumps, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Маршрут {From}->{To} не получен", from, to);
                    await _db.SaveRouteJumpsAsync(from, to, from == to ? 0 : 99, ct);
                }
            }
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
        var needNames = await _db.TypesNeedingNamesAsync(ct);
        if (needNames.Count > 0)
            await FetchNamesAsync(needNames, ct);

        var history = await _db.TypesNeedingHistoryAsync(today, ct);
        if (history.Count > 0)
            await FetchHistoryAsync(history, today, ct);

        var recheck = await _db.RejectsToRecheckAsync(today, ct);
        if (recheck.Count > 0)
        {
            var forgeRecheck = recheck
                .Select(id => new HistoryNeed(TradeHubs.ForgeRegionId, id))
                .ToList();
            await FetchHistoryAsync(forgeRecheck, today, ct);
        }

        var needVol = await _db.TypesMissingVolumeAsync(400, ct);
        if (needVol.Count > 0)
            await FetchVolumesAsync(needVol, ct);

        // объём мог создать строки с заглушкой #id — добираем имена развоза
        needNames = await _db.TypesNeedingNamesAsync(ct);
        if (needNames.Count > 0)
            await FetchNamesAsync(needNames, ct);

        if (Volatile.Read(ref _ordersRunning) == 0)
            await _db.SetStatusAsync("idle", "имена и история обновлены", null, ct);
    }

    private async Task FetchVolumesAsync(IReadOnlyList<int> typeIds, CancellationToken ct)
    {
        await _db.SetStatusAsync("sync", $"объём м³, {typeIds.Count.ToString(CultureInfo.InvariantCulture)} шт.", null, ct);
        foreach (var typeId in typeIds)
        {
            try
            {
                var volume = await _esi.FetchTypeVolumeAsync(typeId, ct);
                if (volume is decimal m3 && m3 > 0)
                    await _db.SaveTypeVolumeAsync(typeId, m3, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Объём типа {TypeId} не получен", typeId);
            }
        }
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

    private async Task FetchHistoryAsync(IReadOnlyList<HistoryNeed> jobs, string today, CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        foreach (var chunk in jobs.Chunk(8))
        {
            var results = await Task.WhenAll(chunk.Select(async job =>
            {
                try
                {
                    var bars = await _esi.FetchHistoryAsync(job.RegionId, job.TypeId, ct);
                    return (job.RegionId, job.TypeId, bars, ok: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "История типа {TypeId} в регионе {RegionId} не получена", job.TypeId, job.RegionId);
                    return (job.RegionId, job.TypeId, bars: (IReadOnlyList<HistoryBar>)[], ok: false);
                }
            }));

            foreach (var (regionId, typeId, bars, ok) in results)
            {
                done++;
                if (!ok)
                {
                    failed++;
                    continue;
                }

                if (!WasTraded(bars))
                {
                    if (regionId == TradeHubs.ForgeRegionId)
                        await _db.RejectAsync([typeId], "no_trade", today, ct);
                    else
                        await _db.UpsertHistoryAsync(regionId, typeId, bars, today, _options.KeepDays, ct);
                    continue;
                }

                if (regionId == TradeHubs.ForgeRegionId)
                    await _db.UnrejectAsync(typeId, ct);
                await _db.UpsertHistoryAsync(regionId, typeId, bars, today, _options.KeepDays, ct);
            }

            if (done % 20 == 0 || done == jobs.Count)
            {
                await _db.SetStatusAsync(
                    "sync",
                    $"история {done} из {jobs.Count}",
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

    private sealed class BoardMerge
    {
        public decimal? BestBuy { get; private set; }
        public decimal BestSell { get; private set; }
        public bool HasSell { get; private set; }
        public long BuyVolume { get; private set; }
        public long SellVolume { get; private set; }
        public int BuyOrders { get; private set; }
        public int SellOrders { get; private set; }
        public long BuyTouch { get; private set; }
        public long SellTouch { get; private set; }

        public void Add(Book book)
        {
            if (book.BestBuy is decimal buy && (BestBuy is null || buy > BestBuy))
                BestBuy = buy;
            if (book.HasSell && (!HasSell || book.BestSell < BestSell))
            {
                BestSell = book.BestSell;
                HasSell = true;
            }

            BuyVolume += book.BuyVolume;
            SellVolume += book.SellVolume;
            BuyOrders += book.BuyOrders;
            SellOrders += book.SellOrders;
            BuyTouch += book.BuyTouch;
            SellTouch += book.SellTouch;
        }

        public QuoteInsert ToInsert(int typeId) =>
            new(typeId, BestBuy, BestSell, BuyVolume, SellVolume, BuyOrders, SellOrders, BuyTouch, SellTouch);
    }
}
