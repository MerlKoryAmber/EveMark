using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace JitaWatch;

public sealed record TypeNote(int TypeId, string Name, string Note, bool Blacklisted);

public sealed record ContractDeal(
    int ContractId,
    string Title,
    decimal Price,
    int TypeId,
    string ItemName,
    long Quantity,
    decimal MarketSell,
    decimal EdgeIsk,
    decimal EdgeFrac);

public sealed record BacktestDto(int Days, int Hits, decimal SumDailyIsk, IReadOnlyList<string> Notes);

public sealed partial class MarketDb
{
    public Task EnsureExtraTablesAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS type_note (
                    type_id INTEGER PRIMARY KEY,
                    note TEXT NOT NULL DEFAULT '',
                    blacklisted INTEGER NOT NULL DEFAULT 0
                );
                """);
        }, ct);

    public Task<PortfolioSettings> GetPortfolioSettingsAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var map = await ReadSettingMapAsync(conn);
            var exclude = ParseExclude(map.GetValueOrDefault("pf_exclude", ""));
            return new PortfolioSettings(
                ParseLong(map, "pf_capital", 1_000_000_000),
                map.GetValueOrDefault("pf_slots_mode", "manual"),
                ParseInt(map, "pf_slots", 20),
                Math.Clamp(ParseInt(map, "pf_skill_trade", 0), 0, 5),
                Math.Clamp(ParseInt(map, "pf_skill_retail", 0), 0, 5),
                Math.Clamp(ParseInt(map, "pf_skill_wholesale", 0), 0, 5),
                Math.Clamp(ParseInt(map, "pf_skill_tycoon", 0), 0, 5),
                Math.Max(0, ParseInt(map, "pf_slots_used", 0)),
                Math.Clamp(ParseDec(map, "pf_horizon_days", 2m), 0.1m, 30m),
                Math.Clamp(ParseDec(map, "pf_max_share_item", 0.20m), 0, 1),
                Math.Clamp(ParseDec(map, "pf_max_market_share", 0.30m), 0, 1),
                Math.Max(0, ParseLong(map, "pf_min_profit_day", 1_000_000)),
                Math.Max(0, ParseInt(map, "pf_min_hours", 6)),
                Math.Max(0, ParseLong(map, "pf_min_volume_day", 10)),
                Math.Max(0, ParseInt(map, "pf_max_relists_day", 12)),
                ParseBool(map, "pf_source_jita", true),
                ParseBool(map, "pf_source_perimeter", true),
                exclude);
        }, ct);

    public Task SavePortfolioSettingsAsync(PortfolioSettings s, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await UpsertSettingAsync(conn, "pf_capital", s.Capital.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_slots_mode",
                string.Equals(s.SlotsMode, "skills", StringComparison.OrdinalIgnoreCase) ? "skills" : "manual");
            await UpsertSettingAsync(conn, "pf_slots", s.Slots.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_skill_trade", Math.Clamp(s.SkillTrade, 0, 5).ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_skill_retail", Math.Clamp(s.SkillRetail, 0, 5).ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_skill_wholesale", Math.Clamp(s.SkillWholesale, 0, 5).ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_skill_tycoon", Math.Clamp(s.SkillTycoon, 0, 5).ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_slots_used", Math.Max(0, s.SlotsUsed).ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_horizon_days", s.HorizonDays.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_max_share_item", s.MaxShareItem.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_max_market_share", s.MaxMarketShare.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_min_profit_day", s.MinProfitDay.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_min_hours", s.MinHours.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_min_volume_day", s.MinVolumeDay.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_max_relists_day", s.MaxRelistsDay.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_source_jita", s.SourceJita ? "1" : "0");
            await UpsertSettingAsync(conn, "pf_source_perimeter", s.SourcePerimeter ? "1" : "0");
            // Exclude только через SetPortfolioExcludeAsync — иначе «Подобрать»/сохранение настроек затирает список.
            if (s.Exclude != null)
            {
                await UpsertSettingAsync(conn, "pf_exclude",
                    string.Join(",", s.Exclude.Distinct().OrderBy(x => x)));
            }
        }, ct);

    public Task SetPortfolioExcludeAsync(int typeId, bool excluded, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var map = await ReadSettingMapAsync(conn);
            var set = ParseExclude(map.GetValueOrDefault("pf_exclude", "")).ToHashSet();
            if (excluded)
                set.Add(typeId);
            else
                set.Remove(typeId);
            await UpsertSettingAsync(conn, "pf_exclude",
                string.Join(",", set.OrderBy(x => x)));
        }, ct);

    private static async Task<Dictionary<string, string>> ReadSettingMapAsync(SqliteConnection conn)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM setting";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    private static Task UpsertSettingAsync(SqliteConnection conn, string key, string value) =>
        ExecAsync(conn, """
            INSERT INTO setting (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, ("$k", key), ("$v", value));

    private static List<int> ParseExclude(string text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                list.Add(id);
        }

        return list;
    }

    private static long ParseLong(Dictionary<string, string> map, string key, long fallback)
    {
        if (!map.TryGetValue(key, out var text))
            return fallback;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public Task<IReadOnlyList<BoardRow>> LoadBoardRowsAsync(BoardSources sources, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var load = await LoadRankedAsync(conn, sources, ct);
            // Портфель всегда только плюсовая маржа — галка закупа сюда не тянется.
            var rows = load.Rows
                .Where(r => r.MarginIsk > 0)
                .Where(r => r.MarginFrac * 100m <= 5000m)
                .Where(r => !IsBlacklisted(conn, r.TypeId))
                .ToList();
            await FillHoursAsync(conn, rows, load.Fees, ct);
            return (IReadOnlyList<BoardRow>)rows;
        }, ct);

    private static bool IsBlacklisted(SqliteConnection conn, int typeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT blacklisted FROM type_note WHERE type_id = $t";
        cmd.Parameters.AddWithValue("$t", typeId);
        var v = cmd.ExecuteScalar();
        return v is not null and not DBNull && Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
    }

    public Task SetTypeNoteAsync(int typeId, string? note, bool? blacklisted, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO type_note (type_id, note, blacklisted) VALUES ($t, $n, $b)
                ON CONFLICT(type_id) DO UPDATE SET
                    note = COALESCE($n, type_note.note),
                    blacklisted = COALESCE($b, type_note.blacklisted)
                """;
            cmd.Parameters.AddWithValue("$t", typeId);
            cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", blacklisted is null ? DBNull.Value : blacklisted.Value ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task<IReadOnlyList<TypeNote>> ListNotesAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<TypeNote>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.type_id, IFNULL(n.name, '#' || t.type_id), t.note, t.blacklisted
                FROM type_note t
                LEFT JOIN type_name n ON n.type_id = t.type_id
                ORDER BY t.blacklisted DESC, n.name COLLATE NOCASE
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new TypeNote(
                    ReadInt(reader, 0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3) != 0));
            }

            return (IReadOnlyList<TypeNote>)list;
        }, ct);

    public Task<BacktestDto> BacktestShoutAsync(int days, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var fees = await ReadSettingsAsync(conn);
            var since = Stamp(DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 120)));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshot WHERE taken_at >= $s";
            cmd.Parameters.AddWithValue("$s", since);
            var snaps = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture);
            var load = await LoadRankedAsync(conn, BoardSources.Both, ct);
            var hits = load.Rows.Count(r =>
                r.MarginFrac >= SignalRule.MinMarginFrac
                && r.MarginFrac <= SignalRule.MaxMarginFrac
                && r.DailyVolume >= SignalRule.MinDailyVolume
                && r.DailyIsk >= SignalRule.MinDailyIsk
                && r.Hours >= SignalRule.MinHours);
            var sum = load.Rows
                .Where(r => r.DailyIsk >= SignalRule.MinDailyIsk && r.Hours >= SignalRule.MinHours)
                .Sum(r => r.DailyIsk ?? 0);
            var notes = new List<string>
            {
                $"Снимков за период: {snaps.ToString(CultureInfo.InvariantCulture)}",
                $"Грубый прокси: текущий снимок × фильтр Крик (не поминутный бэктест).",
                $"Налог {fees.SalesTax.ToString("P2", CultureInfo.InvariantCulture)}, брокер {fees.BrokerFee.ToString("P2", CultureInfo.InvariantCulture)}."
            };
            return new BacktestDto(days, hits, sum, notes);
        }, ct);

    /// <summary>Карточка предмета: текущие цены + дневная история региона (простым языком для UI).</summary>
    public Task<TypeProbeDto?> GetTypeProbeAsync(int typeId, int? regionId, int days, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            if (typeId <= 0)
                return null;
            var region = regionId is > 0 ? regionId.Value : TradeHubs.ForgeRegionId;
            days = Math.Clamp(days, 1, 90);
            var name = "#" + typeId.ToString(CultureInfo.InvariantCulture);
            await using (var ncmd = conn.CreateCommand())
            {
                ncmd.CommandText = "SELECT name FROM type_name WHERE type_id = $t";
                ncmd.Parameters.AddWithValue("$t", typeId);
                var raw = await ncmd.ExecuteScalarAsync(ct) as string;
                if (!string.IsNullOrWhiteSpace(raw))
                    name = raw;
            }

            decimal? bestBuy = null, bestSell = null;
            long buyVol = 0, sellVol = 0, buyTouch = 0, sellTouch = 0;
            int hours = 0;
            await using (var latest = conn.CreateCommand())
            {
                latest.CommandText = "SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1";
                var sid = await latest.ExecuteScalarAsync(ct);
                if (sid is long snapshotId)
                {
                    await using var q = conn.CreateCommand();
                    q.CommandText = """
                        SELECT best_buy, best_sell, buy_volume, sell_volume, buy_touch, sell_touch
                        FROM location_quote
                        WHERE snapshot_id = $s AND type_id = $t AND location_id = $loc
                        """;
                    q.Parameters.AddWithValue("$s", snapshotId);
                    q.Parameters.AddWithValue("$t", typeId);
                    q.Parameters.AddWithValue("$loc", TradeHubs.Jita.LocationId);
                    await using var reader = await q.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        if (!reader.IsDBNull(0))
                            bestBuy = ReadDec(reader, 0);
                        bestSell = ReadDec(reader, 1);
                        buyVol = ReadLong(reader, 2);
                        sellVol = ReadLong(reader, 3);
                        buyTouch = reader.IsDBNull(4) ? 0 : ReadLong(reader, 4);
                        sellTouch = reader.IsDBNull(5) ? 0 : ReadLong(reader, 5);
                    }
                }
            }

            var fees = await ReadSettingsAsync(conn);
            decimal? margin = null, marginFrac = null;
            if (bestBuy is decimal b && bestSell is decimal s && b > 0 && s > 0)
            {
                var m = FeeMath.Margin(b, s, fees.SalesTax, fees.BrokerFee);
                margin = m.Margin;
                marginFrac = m.Fraction;
                var row = new BoardRow
                {
                    TypeId = typeId,
                    Name = name,
                    BestBuy = b,
                    BestSell = s,
                    MarginIsk = m.Margin,
                    MarginFrac = m.Fraction,
                    BuyVolume = buyVol,
                    SellVolume = sellVol,
                    BuyTouch = buyTouch > 0 ? buyTouch : null,
                    SellTouch = sellTouch > 0 ? sellTouch : null
                };
                var list = new List<BoardRow> { row };
                await FillHoursAsync(conn, list, fees, ct);
                hours = list[0].Hours;
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var from = today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var bars = new List<TypeProbeDay>();
            await using (var h = conn.CreateCommand())
            {
                h.CommandText = """
                    SELECT day, volume, average, highest, lowest
                    FROM daily_history
                    WHERE region_id = $r AND type_id = $t AND day < $today AND day >= $from
                    ORDER BY day DESC
                    """;
                h.Parameters.AddWithValue("$r", region);
                h.Parameters.AddWithValue("$t", typeId);
                h.Parameters.AddWithValue("$today", todayText);
                h.Parameters.AddWithValue("$from", from);
                await using var reader = await h.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    bars.Add(new TypeProbeDay(
                        reader.GetString(0),
                        ReadLong(reader, 1),
                        ReadDec(reader, 2),
                        ReadDec(reader, 3),
                        ReadDec(reader, 4)));
                }
            }

            decimal? lastAvg = bars.Count > 0 ? bars[0].Average : null;
            long? lastVol = bars.Count > 0 ? bars[0].Volume : null;
            string? lastDay = bars.Count > 0 ? bars[0].Day : null;
            string verdict;
            string avgPlace;
            if (lastAvg is null || bestBuy is not decimal buy || bestSell is not decimal sell || buy <= 0 || sell <= 0)
            {
                verdict = bars.Count == 0
                    ? "В базе нет дневной истории по этому предмету. Сначала нужен снимок, который подтянул историю рынка."
                    : "Нет текущих цен покупки и продажи на Jita 4-4 в последнем снимке.";
                avgPlace = "unknown";
            }
            else if (lastAvg >= buy && lastAvg <= sell)
            {
                avgPlace = "inside";
                verdict =
                    "В последний полный день средняя цена сделок в регионе была между текущей покупкой и продажей. " +
                    "Значит, в этом диапазоне цены рынок хотя бы вчера торговал — не только «висящие» ордера.";
            }
            else if (lastAvg < buy)
            {
                avgPlace = "below_buy";
                verdict =
                    "В последний полный день средняя цена сделок была ниже текущей покупки. " +
                    "Рынок вчера шёл дешевле, чем «лучшая покупка» сейчас — купить дёшево и продать дорого по сегодняшним ордерам может не получиться.";
            }
            else
            {
                avgPlace = "above_sell";
                verdict =
                    "В последний полный день средняя цена сделок была выше текущей продажи. " +
                    "Рынок вчера шёл дороже, чем «лучшая продажа» сейчас.";
            }

            if (bestBuy is decimal bb && bestSell is decimal ss && bb > 0 && ss >= bb * 3m
                && avgPlace is "inside" or "below_buy" or "above_sell")
            {
                var times = ss / bb;
                verdict +=
                    " Разница цен сейчас очень большая: продажа примерно в "
                    + times.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))
                    + " раза дороже покупки. Такое бывает и при реальных сделках, и когда ордера просто висят.";
            }

            long? stationBuy = null, stationSell = null;
            var watched = false;
            await using (var w = conn.CreateCommand())
            {
                w.CommandText = "SELECT 1 FROM watchlist WHERE type_id = $t LIMIT 1";
                w.Parameters.AddWithValue("$t", typeId);
                watched = await w.ExecuteScalarAsync(ct) is not null and not DBNull;
            }

            var stationDays = new List<TypeProbeStationDay>();
            if (watched)
            {
                var since24 = Stamp(DateTime.UtcNow.AddHours(-24));
                await using (var f = conn.CreateCommand())
                {
                    f.CommandText = """
                        SELECT is_buy, SUM(volume) FROM watch_fill
                        WHERE type_id = $t AND filled_at >= $since
                        GROUP BY is_buy
                        """;
                    f.Parameters.AddWithValue("$t", typeId);
                    f.Parameters.AddWithValue("$since", since24);
                    await using var reader = await f.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var isBuy = reader.GetInt64(0) != 0;
                        var vol = ReadLong(reader, 1);
                        if (isBuy) stationBuy = vol;
                        else stationSell = vol;
                    }
                }

                var sinceDays = Stamp(DateTime.UtcNow.AddDays(-days));
                var byDay = new Dictionary<string, (long Buy, long Sell)>(StringComparer.Ordinal);
                await using (var f = conn.CreateCommand())
                {
                    f.CommandText = """
                        SELECT substr(filled_at, 1, 10), is_buy, SUM(volume)
                        FROM watch_fill
                        WHERE type_id = $t AND filled_at >= $since
                        GROUP BY substr(filled_at, 1, 10), is_buy
                        ORDER BY 1 DESC
                        """;
                    f.Parameters.AddWithValue("$t", typeId);
                    f.Parameters.AddWithValue("$since", sinceDays);
                    await using var reader = await f.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var day = reader.GetString(0);
                        var isBuy = reader.GetInt64(1) != 0;
                        var vol = ReadLong(reader, 2);
                        byDay.TryGetValue(day, out var cur);
                        byDay[day] = isBuy ? (cur.Buy + vol, cur.Sell) : (cur.Buy, cur.Sell + vol);
                    }
                }

                foreach (var day in byDay.Keys.OrderByDescending(d => d, StringComparer.Ordinal))
                {
                    var (dayBuy, daySell) = byDay[day];
                    stationDays.Add(new TypeProbeStationDay(day, dayBuy, daySell));
                }
            }

            // Последний снимок Jita на каждый UTC-день — покупка и продажа рядом.
            var quoteDays = new List<TypeProbeQuoteDay>();
            var quoteFrom = Stamp(DateTime.UtcNow.AddDays(-days));
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = """
                    SELECT substr(s.taken_at, 1, 10) AS day, lq.best_buy, lq.best_sell
                    FROM location_quote lq
                    JOIN snapshot s ON s.id = lq.snapshot_id
                    WHERE lq.type_id = $t AND lq.location_id = $loc AND s.taken_at >= $from
                    ORDER BY s.taken_at DESC
                    """;
                q.Parameters.AddWithValue("$t", typeId);
                q.Parameters.AddWithValue("$loc", TradeHubs.Jita.LocationId);
                q.Parameters.AddWithValue("$from", quoteFrom);
                await using var reader = await q.ExecuteReaderAsync(ct);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync(ct))
                {
                    var day = reader.GetString(0);
                    if (!seen.Add(day))
                        continue;
                    decimal? dayBuy = reader.IsDBNull(1) ? null : ReadDec(reader, 1);
                    var daySell = ReadDec(reader, 2);
                    quoteDays.Add(new TypeProbeQuoteDay(day, dayBuy, daySell));
                }
            }

            var regionName = region == TradeHubs.ForgeRegionId ? "The Forge (Jita и окрестности)" : region.ToString(CultureInfo.InvariantCulture);
            return new TypeProbeDto(
                typeId, name, region, regionName, days,
                bestBuy, bestSell, margin, marginFrac, hours,
                buyTouch, sellTouch, buyVol, sellVol,
                lastDay, lastAvg, lastVol, avgPlace, verdict,
                watched, stationBuy, stationSell, bars, quoteDays, stationDays);
        }, ct);
}

public sealed record TypeProbeDay(string Day, long Volume, decimal Average, decimal Highest, decimal Lowest);

public sealed record TypeProbeQuoteDay(
    string Day,
    [property: JsonPropertyName("best_buy")] decimal? BestBuy,
    [property: JsonPropertyName("best_sell")] decimal BestSell);

public sealed record TypeProbeStationDay(
    string Day,
    [property: JsonPropertyName("buy")] long Buy,
    [property: JsonPropertyName("sell")] long Sell);

public sealed record TypeProbeDto(
    int TypeId,
    string Name,
    int RegionId,
    [property: JsonPropertyName("region_name")] string RegionName,
    int Days,
    [property: JsonPropertyName("best_buy")] decimal? BestBuy,
    [property: JsonPropertyName("best_sell")] decimal? BestSell,
    [property: JsonPropertyName("margin_isk")] decimal? MarginIsk,
    [property: JsonPropertyName("margin_frac")] decimal? MarginFrac,
    int Hours,
    [property: JsonPropertyName("buy_touch")] long BuyTouch,
    [property: JsonPropertyName("sell_touch")] long SellTouch,
    [property: JsonPropertyName("buy_volume")] long BuyVolume,
    [property: JsonPropertyName("sell_volume")] long SellVolume,
    [property: JsonPropertyName("last_day")] string? LastDay,
    [property: JsonPropertyName("last_avg")] decimal? LastAvg,
    [property: JsonPropertyName("last_volume")] long? LastVolume,
    [property: JsonPropertyName("avg_place")] string AvgPlace,
    string Verdict,
    bool Watched,
    [property: JsonPropertyName("station_buy_24h")] long? StationBuy24h,
    [property: JsonPropertyName("station_sell_24h")] long? StationSell24h,
    IReadOnlyList<TypeProbeDay> History,
    [property: JsonPropertyName("quotes")] IReadOnlyList<TypeProbeQuoteDay> Quotes,
    [property: JsonPropertyName("station_days")] IReadOnlyList<TypeProbeStationDay> StationDays);
