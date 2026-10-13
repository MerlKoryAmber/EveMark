using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JitaWatch;

public sealed partial class MarketDb
{
    public Task EnsureHaulTablesAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS route_path (
                    from_system INTEGER NOT NULL,
                    to_system INTEGER NOT NULL,
                    flag TEXT NOT NULL,
                    jumps INTEGER NOT NULL,
                    min_sec TEXT NOT NULL DEFAULT '1',
                    lowsec_jumps INTEGER NOT NULL DEFAULT 0,
                    systems_json TEXT NOT NULL DEFAULT '[]',
                    fetched_at TEXT NOT NULL,
                    PRIMARY KEY (from_system, to_system, flag)
                );
                CREATE TABLE IF NOT EXISTS system_sec (
                    system_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    security_status TEXT NOT NULL
                );
                """);
        }, ct);

    public Task<HaulSettings> GetHaulSettingsAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var map = await ReadSettingMapAsync(conn);
            var preset = map.GetValueOrDefault("hl_ship_preset", "dst");
            var presetM3 = HaulSettings.PresetM3(preset);
            var cargo = ParseDec(map, "hl_cargo_m3", presetM3 > 0 ? presetM3 : 60_000m);
            return new HaulSettings(
                map.GetValueOrDefault("hl_from", "jita"),
                map.GetValueOrDefault("hl_to", "any"),
                preset,
                cargo,
                ParseLong(map, "hl_budget", 1_000_000_000),
                ParseLong(map, "hl_max_cargo_value", 1_500_000_000),
                map.GetValueOrDefault("hl_mode", "best"),
                map.GetValueOrDefault("hl_route_flag", "secure"),
                Math.Clamp(ParseDec(map, "hl_list_horizon_days", 3m), 0.1m, 30m),
                Math.Clamp(ParseDec(map, "hl_max_market_share", 0.30m), 0, 1),
                Math.Max(0, ParseLong(map, "hl_min_item_profit", 1_000_000)),
                Math.Clamp(ParseInt(map, "hl_sec_per_jump", 60), 10, 600),
                map.GetValueOrDefault("hl_gank_systems", "Uedama, Sivala, Ahbazon"),
                Math.Max(0, ParseLong(map, "hl_gank_warn_value", 1_000_000_000)),
                Math.Max(0, ParseLong(map, "hl_min_unit_price", 500_000)),
                Math.Max(0, ParseLong(map, "hl_courier_base", 0)),
                Math.Max(0, ParseDec(map, "hl_courier_per_m3", 0)),
                Math.Max(0, ParseLong(map, "hl_courier_per_jump", 0)),
                Math.Max(0, ParseDec(map, "hl_courier_collateral_pct", 0)),
                Math.Max(0, ParseDec(map, "hl_courier_max_m3", 0)),
                Math.Clamp(ParseDec(map, "hl_list_max_ask_age_days", 7m), 0, 365),
                Math.Clamp(ParseInt(map, "hl_list_hist_days", 14), 1, 90));
        }, ct);

    public Task SaveHaulSettingsAsync(HaulSettings s, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await UpsertSettingAsync(conn, "hl_from", s.From);
            await UpsertSettingAsync(conn, "hl_to", s.To);
            await UpsertSettingAsync(conn, "hl_ship_preset", s.ShipPreset);
            await UpsertSettingAsync(conn, "hl_cargo_m3", s.CargoM3.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_budget", s.Budget.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_max_cargo_value", s.MaxCargoValue.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_mode", s.Mode);
            await UpsertSettingAsync(conn, "hl_route_flag", s.RouteFlag);
            await UpsertSettingAsync(conn, "hl_list_horizon_days", s.ListHorizonDays.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_max_market_share", s.MaxMarketShare.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_min_item_profit", s.MinItemProfit.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_sec_per_jump", s.SecPerJump.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_gank_systems", s.GankSystems);
            await UpsertSettingAsync(conn, "hl_gank_warn_value", s.GankWarnValue.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_min_unit_price", s.MinUnitPrice.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_courier_base", s.CourierBase.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_courier_per_m3", s.CourierPerM3.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_courier_per_jump", s.CourierPerJump.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_courier_collateral_pct", s.CourierCollateralPct.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_courier_max_m3", s.CourierMaxM3.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_list_max_ask_age_days", s.ListMaxAskAgeDays.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "hl_list_hist_days", s.ListHistDays.ToString(CultureInfo.InvariantCulture));
        }, ct);

    public async Task<HaulTripsDto> BuildHaulTripsAsync(
        HaulSettings settings,
        EsiClient esi,
        bool includeRowsFor,
        string? onlyFrom,
        string? onlyTo,
        CancellationToken ct = default)
    {
        await EnsureHaulTablesAsync(ct);
        return await WithAsync(async conn =>
        {
            var fees = await ReadSettingsAsync(conn);
            await using var latest = conn.CreateCommand();
            latest.CommandText = "SELECT id, taken_at FROM snapshot ORDER BY taken_at DESC LIMIT 1";
            await using var latestReader = await latest.ExecuteReaderAsync(ct);
            if (!await latestReader.ReadAsync(ct))
            {
                return new HaulTripsDto(null, 0, Inputs(settings), []);
            }

            var snapshotId = latestReader.GetInt64(0);
            var takenAt = latestReader.GetString(1);
            await latestReader.DisposeAsync();
            var ageMin = 0;
            if (DateTime.TryParse(takenAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var taken))
                ageMin = (int)Math.Max(0, (DateTime.UtcNow - taken.ToUniversalTime()).TotalMinutes);

            var metas = await LoadMetasAsync(conn, ct);
            var books = await LoadHubBooksAsync(conn, snapshotId, metas, ct);
            var hubBestSell = BuildHubBestSellIndex(books);
            var volumes = await LoadTypeVolumesAsync(conn, ct);
            var names = await LoadTypeNamesAsync(conn, ct);
            var skipTypes = await LoadHaulSkipTypesAsync(conn, ct);
            var volDay = await LoadVolumeMedian7Async(conn, ct);
            var histCap = await LoadHistoryAvgCapAsync(conn, settings.ListHistDays, ct);
            var utcNow = DateTime.UtcNow;
            var gankNames = ParseGankNames(settings.GankSystems);
            var gankIds = GankSystems.Names
                .Where(kv => gankNames.Contains(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var hubKeys = books.Keys
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fromFilter = string.IsNullOrWhiteSpace(onlyFrom) ? settings.From : onlyFrom!;
            var toFilter = string.IsNullOrWhiteSpace(onlyTo) ? settings.To : onlyTo!;
            var trips = new List<HaulTripSummary>();
            var cargoCap = settings.CargoM3;
            if (settings.CourierMaxM3 > 0)
                cargoCap = Math.Min(cargoCap, settings.CourierMaxM3);
            var budget = settings.BudgetEff;
            var mode = settings.Mode.ToLowerInvariant();

            foreach (var fromKey in hubKeys)
            {
                if (!HubMatch(fromFilter, fromKey))
                    continue;
                foreach (var toKey in hubKeys)
                {
                    if (string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!HubMatch(toFilter, toKey))
                        continue;
                    if (!string.IsNullOrWhiteSpace(onlyFrom) && !string.Equals(fromKey, onlyFrom, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrWhiteSpace(onlyTo) && !string.Equals(toKey, onlyTo, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fromBooks = books[fromKey];
                    var toBooks = books[toKey];
                    var fromMeta = fromBooks.Values.First().Meta;
                    var toMeta = toBooks.Values.First().Meta;
                    var route = await ResolveRouteAsync(
                        conn, esi, fromMeta.SystemId, toMeta.SystemId, settings.RouteFlag, gankIds, ct);

                    var lots = new List<HaulLot>();
                    var metaByType = new Dictionary<int, (string Name, long VolB, decimal Share, decimal UnitM3, decimal? AskAgeDays)>();
                    foreach (var (typeId, fromBook) in fromBooks)
                    {
                        if (skipTypes.Contains(typeId) || !toBooks.TryGetValue(typeId, out var toBook))
                            continue;
                        if (fromBook.BestSell < settings.MinUnitPrice)
                            continue;
                        if (!volumes.TryGetValue(typeId, out var m3) || m3 <= 0)
                            continue;
                        names.TryGetValue(typeId, out var name);
                        name ??= "#" + typeId.ToString(CultureInfo.InvariantCulture);
                        volDay.TryGetValue((typeId, toMeta.RegionId), out var vB);
                        histCap.TryGetValue((typeId, toMeta.RegionId), out var histAvg);
                        var askAge = HaulPlanner.AskAgeDays(toBook.BestSellIssued, utcNow);
                        var neighborCap = HaulPlanner.NeighborSellCap(hubBestSell, typeId, toKey);
                        var competitors = Math.Max(0, (int)toBook.SellOrders - 1);
                        if (competitors < 0)
                            competitors = 0;
                        var share = Math.Min(1m / (competitors + 1), settings.MaxMarketShare);
                        // list: живой рынок на B (объём, ≥3 sell, вилка не космическая)
                        var qList = HaulPlanner.ListMarketOk(vB, toBook.SellOrders, toBook.BestSell, toBook.BestBuy)
                            ? HaulPlanner.ListQtyMax(vB, competitors, settings.ListHorizonDays, settings.MaxMarketShare)
                            : 0L;
                        // глубина у лучшей цены — sell_touch/buy_touch (как на доске), не весь sell_volume
                        var askRemain = fromBook.SellTouch > 0 ? fromBook.SellTouch : fromBook.SellVolume;
                        var bidRemain = toBook.BuyTouch > 0 ? toBook.BuyTouch : toBook.BuyVolume;
                        var asks = askRemain > 0
                            ? new[] { new BookLevel(fromBook.BestSell, askRemain) }
                            : Array.Empty<BookLevel>();
                        var bids = toBook.BestBuy is decimal bid && bid > 0 && bidRemain > 0
                            ? new[] { new BookLevel(bid, bidRemain) }
                            : Array.Empty<BookLevel>();
                        decimal? hist = histAvg > 0 ? histAvg : null;
                        IReadOnlyList<HaulLot> built = mode switch
                        {
                            "instant" => HaulPlanner.BuildInstantLots(typeId, m3, fees.SalesTax, asks, bids),
                            "list" => HaulPlanner.BuildListLots(typeId, m3, fees.SalesTax, fees.BrokerFee, toBook.BestSell, toBook.BestBuy, qList, asks,
                                hist, askAge, settings.ListMaxAskAgeDays, neighborCap),
                            _ => HaulPlanner.BuildBestLots(typeId, m3, fees.SalesTax, fees.BrokerFee, toBook.BestSell, toBook.BestBuy, qList, asks, bids,
                                hist, askAge, settings.ListMaxAskAgeDays, neighborCap)
                        };
                        if (built.Count == 0)
                            continue;
                        lots.AddRange(built);
                        metaByType[typeId] = (name, vB, share, m3, askAge);
                    }

                    var basket = HaulPlanner.OptimizeWithMinProfit(lots, cargoCap, budget, settings.MinItemProfit);
                    if (basket.Picks.Count == 0)
                        continue;

                    // §4.1: list-штуки не дальше горизонта (q_list_max); страховка после жадности
                    basket = TrimListToHorizon(basket, metaByType, settings.ListHorizonDays);
                    if (basket.Picks.Count == 0)
                        continue;

                    IReadOnlyList<HaulTripRow>? rows = null;
                    if (includeRowsFor || (!string.IsNullOrWhiteSpace(onlyFrom) && !string.IsNullOrWhiteSpace(onlyTo)))
                    {
                        rows = BuildRows(basket, metaByType, settings.ListHorizonDays);
                    }

                    var days = HaulPlanner.TripListDaysToSell(
                        basket.Picks,
                        typeId => metaByType.TryGetValue(typeId, out var m) ? (m.VolB, m.Share) : null,
                        settings.ListHorizonDays);

                    trips.Add(HaulPlanner.Summarize(
                        fromKey, toKey, basket, settings,
                        route.Jumps, route.MinSec, route.LowsecJumps, route.NoSecurePath,
                        route.GankHits, days, rows));
                }
            }

            trips = trips
                .OrderByDescending(t => t.Profit)
                .ThenByDescending(t => t.IskPerJump)
                .ThenBy(t => t.From, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.To, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new HaulTripsDto(takenAt, ageMin, Inputs(settings), trips);
        }, ct);
    }

    private static object Inputs(HaulSettings s) => new
    {
        cargo_m3 = s.CargoM3,
        budget_eff = s.BudgetEff,
        mode = s.Mode,
        route_flag = s.RouteFlag
    };

    private static bool HubMatch(string filter, string hub) =>
        string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase)
        || string.Equals(filter, hub, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseGankNames(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обрезать list-позиции, чтобы days_to_sell ≤ горизонта (§4.1 / §6).</summary>
    private static HaulBasket TrimListToHorizon(
        HaulBasket basket,
        Dictionary<int, (string Name, long VolB, decimal Share, decimal UnitM3, decimal? AskAgeDays)> meta,
        decimal horizonDays)
    {
        if (horizonDays <= 0 || basket.Picks.Count == 0)
            return basket;

        var picks = new List<HaulBasketPick>();
        foreach (var g in basket.Picks.GroupBy(p => (p.Lot.TypeId, p.Lot.Mode)))
        {
            if (g.Key.Mode != "list")
            {
                picks.AddRange(g);
                continue;
            }

            if (!meta.TryGetValue(g.Key.TypeId, out var m) || m.VolB < 1 || m.Share <= 0)
                continue;

            var maxQty = (long)Math.Floor(m.VolB * m.Share * horizonDays);
            if (maxQty < 1)
                continue;

            long left = maxQty;
            foreach (var p in g.OrderBy(x => x.Lot.LotIndex).ThenBy(x => x.Lot.TypeId))
            {
                if (left < 1)
                    break;
                var take = Math.Min(p.Qty, left);
                if (take < 1)
                    continue;
                picks.Add(p with { Qty = take });
                left -= take;
            }
        }

        if (picks.Count == 0)
            return HaulBasket.Empty;

        var profit = picks.Sum(p => p.Lot.UnitProfit * p.Qty);
        var cost = picks.Sum(p => p.Lot.UnitCost * p.Qty);
        var m3 = picks.Sum(p => p.Lot.UnitM3 * p.Qty);
        return new HaulBasket(picks, profit, cost, m3);
    }

    private static List<HaulTripRow> BuildRows(
        HaulBasket basket,
        Dictionary<int, (string Name, long VolB, decimal Share, decimal UnitM3, decimal? AskAgeDays)> meta,
        decimal horizonDays)
    {
        return basket.Picks
            .GroupBy(p => (p.Lot.TypeId, p.Lot.Mode))
            .Select(g =>
            {
                var typeId = g.Key.TypeId;
                var qty = g.Sum(p => p.Qty);
                var cost = g.Sum(p => p.Lot.UnitCost * p.Qty);
                var profit = g.Sum(p => p.Lot.UnitProfit * p.Qty);
                var m3 = g.Sum(p => p.Lot.UnitM3 * p.Qty);
                var buyMax = g.Max(p => p.Lot.UnitCost);
                var buyAvg = qty > 0 ? cost / qty : 0;
                var sellMin = g.Min(p => p.Lot.UnitSell);
                var sellAvg = qty > 0 ? g.Sum(p => p.Lot.UnitSell * p.Qty) / qty : 0;
                meta.TryGetValue(typeId, out var m);
                var days = g.Key.Mode == "list"
                    ? HaulPlanner.ListDaysToSell(qty, m.VolB, m.Share)
                    : 0;
                if (horizonDays > 0 && days > horizonDays)
                    days = horizonDays;
                return new HaulTripRow(
                    typeId, m.Name ?? "#" + typeId, g.Key.Mode, qty, m.UnitM3, m3,
                    buyAvg, buyMax, cost, sellAvg, sellMin, profit,
                    cost > 0 ? profit / cost * 100m : 0, m.VolB, days,
                    g.Key.Mode == "list" ? m.AskAgeDays : null);
            })
            .OrderByDescending(r => r.Profit)
            .ThenBy(r => r.TypeId)
            .ToList();
    }

    private sealed record HubBook(
        LocationMetaRow Meta,
        decimal BestSell,
        decimal? BestBuy,
        long SellVolume,
        long BuyVolume,
        long SellTouch,
        long BuyTouch,
        long SellOrders,
        DateTime? BestSellIssued,
        DateTime? BestBuyIssued);

    private static async Task<Dictionary<long, LocationMetaRow>> LoadMetasAsync(SqliteConnection conn, CancellationToken ct)
    {
        var metas = new Dictionary<long, LocationMetaRow>();
        await using var metaCmd = conn.CreateCommand();
        metaCmd.CommandText = "SELECT location_id, name, system_id, system_name, region_id, hub FROM location_meta";
        await using var metaReader = await metaCmd.ExecuteReaderAsync(ct);
        while (await metaReader.ReadAsync(ct))
        {
            var id = metaReader.GetInt64(0);
            metas[id] = new LocationMetaRow(
                id, metaReader.GetString(1), metaReader.GetInt32(2), metaReader.GetString(3),
                metaReader.GetInt32(4), metaReader.GetString(5));
        }

        return metas;
    }

    private static async Task<Dictionary<string, Dictionary<int, HubBook>>> LoadHubBooksAsync(
        SqliteConnection conn, long snapshotId, Dictionary<long, LocationMetaRow> metas, CancellationToken ct)
    {
        var books = new Dictionary<string, Dictionary<int, HubBook>>(StringComparer.OrdinalIgnoreCase);
        await using var q = conn.CreateCommand();
        q.CommandText = """
            SELECT type_id, location_id, best_buy, best_sell, buy_volume, sell_volume,
                   buy_touch, sell_touch, sell_orders, best_sell_issued, best_buy_issued
            FROM location_quote WHERE snapshot_id = $sid
            """;
        q.Parameters.AddWithValue("$sid", snapshotId);
        await using var reader = await q.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var locationId = reader.GetInt64(1);
            if (!metas.TryGetValue(locationId, out var meta))
                continue;
            var typeId = ReadInt(reader, 0);
            if (!books.TryGetValue(meta.Hub, out var byType))
            {
                byType = new Dictionary<int, HubBook>();
                books[meta.Hub] = byType;
            }

            DateTime? sellIssued = null;
            DateTime? buyIssued = null;
            if (!reader.IsDBNull(9)
                && DateTime.TryParse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var si))
                sellIssued = si.ToUniversalTime();
            if (!reader.IsDBNull(10)
                && DateTime.TryParse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var bi))
                buyIssued = bi.ToUniversalTime();

            var book = new HubBook(
                meta,
                ReadDec(reader, 3),
                reader.IsDBNull(2) ? null : ReadDec(reader, 2),
                ReadLong(reader, 5),
                ReadLong(reader, 4),
                ReadLong(reader, 7),
                ReadLong(reader, 6),
                ReadLong(reader, 8),
                sellIssued,
                buyIssued);
            if (!byType.TryGetValue(typeId, out var prev) || book.BestSell < prev.BestSell)
                byType[typeId] = book;
        }

        return books;
    }

    private static Dictionary<string, Dictionary<int, decimal>> BuildHubBestSellIndex(
        Dictionary<string, Dictionary<int, HubBook>> books)
    {
        var map = new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hub, byType) in books)
        {
            var sells = new Dictionary<int, decimal>();
            foreach (var (typeId, book) in byType)
            {
                if (book.BestSell > 0)
                    sells[typeId] = book.BestSell;
            }

            map[hub] = sells;
        }

        return map;
    }

    /// <summary>Макс. average за последние N дней с объёмом &gt; 0 — потолок цены list в регионе.</summary>
    private static async Task<Dictionary<(int TypeId, int RegionId), decimal>> LoadHistoryAvgCapAsync(
        SqliteConnection conn, int histDays, CancellationToken ct)
    {
        var map = new Dictionary<(int, int), decimal>();
        if (histDays < 1)
            return map;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cut = today.AddDays(-histDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todayStr = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_id, region_id, average, volume FROM daily_history
            WHERE day >= $cut AND day < $today AND volume > 0
            """;
        cmd.Parameters.AddWithValue("$cut", cut);
        cmd.Parameters.AddWithValue("$today", todayStr);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = (ReadInt(reader, 0), ReadInt(reader, 1));
            var avg = ReadDec(reader, 2);
            if (avg <= 0)
                continue;
            if (!map.TryGetValue(key, out var prev) || avg > prev)
                map[key] = avg;
        }

        return map;
    }

    private static async Task<Dictionary<int, decimal>> LoadTypeVolumesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var volumes = new Dictionary<int, decimal>();
        await using var nameCmd = conn.CreateCommand();
        nameCmd.CommandText = "SELECT type_id, volume FROM type_name WHERE volume IS NOT NULL";
        await using var nameReader = await nameCmd.ExecuteReaderAsync(ct);
        while (await nameReader.ReadAsync(ct))
        {
            if (decimal.TryParse(nameReader.GetString(1), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol) && vol > 0)
                volumes[ReadInt(nameReader, 0)] = vol;
        }

        return volumes;
    }

    private static async Task<Dictionary<int, string>> LoadTypeNamesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type_id, name FROM type_name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names[ReadInt(reader, 0)] = reader.GetString(1);
        return names;
    }

    private static async Task<HashSet<int>> LoadHaulSkipTypesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var set = new HashSet<int>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_id FROM type_note WHERE blacklisted = 1
            UNION
            SELECT type_id FROM reject
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            set.Add(ReadInt(reader, 0));
        return set;
    }

    private static async Task<Dictionary<(int TypeId, int RegionId), long>> LoadVolumeMedian7Async(
        SqliteConnection conn, CancellationToken ct)
    {
        var raw = new Dictionary<(int, int), List<long>>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_id, region_id, volume FROM daily_history
            WHERE day < $today
            ORDER BY day DESC
            """;
        cmd.Parameters.AddWithValue("$today", today);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = (ReadInt(reader, 0), ReadInt(reader, 1));
            if (!raw.TryGetValue(key, out var list))
            {
                list = [];
                raw[key] = list;
            }

            if (list.Count >= 7)
                continue;
            list.Add(ReadLong(reader, 2));
        }

        var result = new Dictionary<(int, int), long>();
        foreach (var (key, list) in raw)
        {
            if (list.Count == 0)
                continue;
            var sorted = list.OrderBy(x => x).ToList();
            result[key] = sorted[sorted.Count / 2];
        }

        return result;
    }

    private sealed record RouteDetail(int Jumps, decimal MinSec, int LowsecJumps, bool NoSecurePath, IReadOnlyList<string> GankHits);

    private static async Task<RouteDetail> ResolveRouteAsync(
        SqliteConnection conn,
        EsiClient esi,
        int fromSystem,
        int toSystem,
        string flag,
        Dictionary<int, string> gankIds,
        CancellationToken ct)
    {
        var secure = string.Equals(flag, "secure", StringComparison.OrdinalIgnoreCase);
        var flagKey = secure ? "secure" : "shortest";
        if (fromSystem == toSystem)
            return new RouteDetail(0, 1m, 0, false, []);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT jumps, min_sec, lowsec_jumps, systems_json, fetched_at
                FROM route_path WHERE from_system = $a AND to_system = $b AND flag = $f
                """;
            cmd.Parameters.AddWithValue("$a", fromSystem);
            cmd.Parameters.AddWithValue("$b", toSystem);
            cmd.Parameters.AddWithValue("$f", flagKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var fetched = reader.GetString(4);
                if (DateTime.TryParse(fetched, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ft)
                    && DateTime.UtcNow - ft.ToUniversalTime() < TimeSpan.FromDays(7))
                {
                    var jumps = reader.GetInt32(0);
                    var minSec = decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                    var low = reader.GetInt32(2);
                    var systems = JsonSerializer.Deserialize<List<int>>(reader.GetString(3)) ?? [];
                    var hits = systems.Where(gankIds.ContainsKey).Select(id => gankIds[id]).Distinct().ToList();
                    var noSecure = secure && low > 0;
                    return new RouteDetail(jumps, minSec, low, noSecure, hits);
                }
            }
        }

        // fallback: старый кэш прыжков
        var cachedJumps = 99;
        await using (var rc = conn.CreateCommand())
        {
            rc.CommandText = "SELECT jumps FROM route_cache WHERE from_system = $a AND to_system = $b";
            rc.Parameters.AddWithValue("$a", fromSystem);
            rc.Parameters.AddWithValue("$b", toSystem);
            var v = await rc.ExecuteScalarAsync(ct);
            if (v is not null and not DBNull)
                cachedJumps = Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        IReadOnlyList<int>? systemsLive = null;
        try
        {
            systemsLive = await esi.FetchRouteSystemsAsync(fromSystem, toSystem, secure, ct);
        }
        catch
        {
            /* offline */
        }

        if (systemsLive is null || systemsLive.Count == 0)
        {
            return new RouteDetail(cachedJumps, 0.5m, 0, false, []);
        }

        var jumpsLive = Math.Max(0, systemsLive.Count - 1);
        var minSecLive = 1m;
        var lowsec = 0;
        foreach (var sys in systemsLive)
        {
            var sec = await GetSystemSecAsync(conn, esi, sys, ct);
            if (sec < minSecLive)
                minSecLive = sec;
            if (sec < 0.45m)
                lowsec++;
        }

        var hitsLive = systemsLive.Where(gankIds.ContainsKey).Select(id => gankIds[id]).Distinct().ToList();
        var noSecureLive = secure && lowsec > 0;
        var json = JsonSerializer.Serialize(systemsLive);
        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO route_path (from_system, to_system, flag, jumps, min_sec, lowsec_jumps, systems_json, fetched_at)
                VALUES ($a, $b, $f, $j, $s, $l, $sys, $t)
                ON CONFLICT(from_system, to_system, flag) DO UPDATE SET
                    jumps = excluded.jumps, min_sec = excluded.min_sec, lowsec_jumps = excluded.lowsec_jumps,
                    systems_json = excluded.systems_json, fetched_at = excluded.fetched_at
                """;
            ins.Parameters.AddWithValue("$a", fromSystem);
            ins.Parameters.AddWithValue("$b", toSystem);
            ins.Parameters.AddWithValue("$f", flagKey);
            ins.Parameters.AddWithValue("$j", jumpsLive);
            ins.Parameters.AddWithValue("$s", minSecLive.ToString(CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("$l", lowsec);
            ins.Parameters.AddWithValue("$sys", json);
            ins.Parameters.AddWithValue("$t", Stamp(DateTime.UtcNow));
            await ins.ExecuteNonQueryAsync(ct);
        }

        return new RouteDetail(jumpsLive, minSecLive, lowsec, noSecureLive, hitsLive);
    }

    private static async Task<decimal> GetSystemSecAsync(
        SqliteConnection conn, EsiClient esi, int systemId, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT security_status FROM system_sec WHERE system_id = $id";
            cmd.Parameters.AddWithValue("$id", systemId);
            var v = await cmd.ExecuteScalarAsync(ct);
            if (v is string s && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var known))
                return known;
        }

        try
        {
            var info = await esi.FetchSystemAsync(systemId, ct);
            if (info is null)
                return 0.5m;
            await using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO system_sec (system_id, name, security_status) VALUES ($id, $n, $s)
                ON CONFLICT(system_id) DO UPDATE SET name = excluded.name, security_status = excluded.security_status
                """;
            ins.Parameters.AddWithValue("$id", systemId);
            ins.Parameters.AddWithValue("$n", info.Value.Name);
            ins.Parameters.AddWithValue("$s", info.Value.Security.ToString(CultureInfo.InvariantCulture));
            await ins.ExecuteNonQueryAsync(ct);
            return info.Value.Security;
        }
        catch
        {
            return 0.5m;
        }
    }
}
