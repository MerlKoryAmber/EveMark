using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JitaWatch;

public sealed class StatusDto
{
    public string State { get; init; } = "idle";
    public string Detail { get; init; } = "";
    public string? LastSnapshotAt { get; init; }
    public string? NextSnapshotAt { get; init; }
    public string? LastError { get; init; }
    public int LatestTypes { get; init; }
}

public sealed record BoardRow
{
    public int TypeId { get; init; }
    public string Name { get; init; } = "";
    public decimal BestBuy { get; init; }
    public decimal BestSell { get; init; }
    public decimal MarginIsk { get; init; }
    public decimal MarginFrac { get; init; }
    public long? DailyVolume { get; init; }
    public string? VolumeDay { get; init; }
    public decimal? Average { get; init; }
    public decimal? DailyIsk { get; init; }
    /// <summary>Медиана объёма ESI за до 7 полных дней (база для «Объём/сутки»).</summary>
    public long? VolumeMedian7 { get; init; }
    /// <summary>Среднее объёма за до 7 дней.</summary>
    public long? VolumeAvg7 { get; init; }
    /// <summary>Медиана объёма за до 30 дней.</summary>
    public long? VolumeMedian30 { get; init; }
    /// <summary>Доля захвата 1/конкуренты; конкуренты — уникальные order_id, двигавшие лучшую цену.</summary>
    public decimal? CaptureShare { get; init; }
    public int Competitors { get; init; }
    /// <summary>Станционный объём сделок (watch fills) за 24ч UTC, buy+sell.</summary>
    public long? StationVolume24h { get; init; }
    public long? StationBuyFilled24h { get; init; }
    public long? StationSellFilled24h { get; init; }
    public bool Watched { get; init; }
    public bool Anomaly { get; set; }
    /// <summary>Чистая маржа с учётом типичных перестановок (из «Дёргают»).</summary>
    public decimal? NetMarginIsk { get; set; }
    public int Hours { get; init; }
    public long BuyVolume { get; init; }
    public long SellVolume { get; init; }
    public int BuyOrders { get; init; }
    public int SellOrders { get; init; }
    public long? BuyTouch { get; init; }
    public long? SellTouch { get; init; }
    public int Relists { get; init; }
    public decimal? Capital { get; init; }
    public decimal? Days { get; init; }
    public decimal? DailyRoi { get; init; }
    public string Thin { get; init; } = "";
    public int BuyMoves { get; init; }
    public int SellMoves { get; init; }
    public decimal? MarginThen { get; init; }
}

public sealed class SignalDto
{
    public string Tone { get; init; } = "early";
    public string Banner { get; init; } = "";
    public int Snapshots { get; init; }
    public int Shown { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 200;
    public int Pages { get; init; } = 1;
    public decimal SalesTax { get; init; }
    public decimal BrokerFee { get; init; }
    public IReadOnlyList<BoardRow> Rows { get; init; } = [];
}

public static class SignalRule
{
    public const int MinSnapshots = 6;
    public const int MinHours = 6;
    /// <summary>Макс. простой между снимками живой вилки (рестарт софта), дальше серия «Часов» рвётся.</summary>
    public static readonly TimeSpan MaxHoursGap = TimeSpan.FromHours(3);
    public const decimal MinMarginFrac = 0.03m;
    public const decimal MaxMarginFrac = 1m;
    public const long MinDailyVolume = 10;
    public const decimal MinDailyIsk = 10_000_000m;
    public const long MinTouch = 2;
    public const int MinMoves = 3;
}

public sealed class BoardDto
{
    public string? TakenAt { get; init; }
    public decimal SalesTax { get; init; }
    public decimal BrokerFee { get; init; }
    public int Tracked { get; init; }
    public int Shown { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 200;
    public int Pages { get; init; } = 1;
    public IReadOnlyList<BoardRow> Rows { get; init; } = [];
}

public sealed record HaulRow
{
    public int TypeId { get; init; }
    public string Name { get; init; } = "";
    public string FromName { get; init; } = "";
    public string ToName { get; init; } = "";
    public long FromLocationId { get; init; }
    public long ToLocationId { get; init; }
    public decimal Ask { get; init; }
    public decimal Bid { get; init; }
    public decimal MarginIsk { get; init; }
    public decimal MarginFrac { get; init; }
    public long Units { get; init; }
    public long? DailyVolume { get; init; }
    public decimal? DailyIsk { get; init; }
    public int Jumps { get; init; }
    public decimal? VolumeM3 { get; init; }
    public decimal? CargoM3 { get; init; }
    public decimal ListSell { get; init; }
    public decimal ListMarginIsk { get; init; }
    public decimal ListMarginFrac { get; init; }
}

public sealed class HaulDto
{
    public string? TakenAt { get; init; }
    public decimal SalesTax { get; init; }
    public int Shown { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 200;
    public int Pages { get; init; } = 1;
    public IReadOnlyList<HaulRow> Rows { get; init; } = [];
}

public readonly record struct HaulFilter(
    string? Name,
    NumRange MarginIsk,
    NumRange MarginFrac,
    NumRange Units,
    NumRange DailyVolume,
    NumRange Jumps,
    NumRange DailyIsk,
    string? FromHub,
    string? ToHub);

public readonly record struct BoardSources(bool Jita, bool Perimeter)
{
    public static BoardSources Both { get; } = new(true, true);

    public bool Any => Jita || Perimeter;

    public static BoardSources Parse(bool? jita, bool? perimeter) =>
        new(jita ?? true, perimeter ?? true);
}

public readonly record struct NumRange(decimal? Min, decimal? Max, bool Bad)
{
    public static NumRange Parse(string? min, string? max)
    {
        var lo = Read(min);
        var hi = Read(max);
        return new NumRange(lo.Value, hi.Value, lo.Bad || hi.Bad);
    }

    public bool Fits(decimal? value)
    {
        if (Bad)
            return false;
        if (Min is null && Max is null)
            return true;
        if (value is null)
            return false;
        return (Min is null || value >= Min) && (Max is null || value <= Max);
    }

    private static (decimal? Value, bool Bad) Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, false);
        var s = text.Replace(" ", "", StringComparison.Ordinal)
            .Replace("\u00a0", "", StringComparison.Ordinal)
            .Replace(",", ".", StringComparison.Ordinal);
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (value, false)
            : (null, true);
    }
}

public sealed record ColumnFilter(
    string? Name,
    NumRange BestBuy,
    NumRange BestSell,
    NumRange MarginIsk,
    NumRange MarginFrac,
    NumRange DailyVolume,
    NumRange DailyIsk,
    NumRange Hours,
    NumRange BuyVolume,
    NumRange SellVolume,
    NumRange Average,
    NumRange BuyMoves,
    NumRange SellMoves,
    NumRange MarginThen,
    string? Thin,
    NumRange BuyTouch,
    NumRange SellTouch,
    NumRange Relists,
    NumRange Capital,
    NumRange Days,
    NumRange DailyRoi,
    NumRange NetMarginIsk = default);

public sealed partial class MarketDb
{
    private const decimal MaxBoardMarginPercent = 5000m;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public MarketDb(string path)
    {
        _path = path;
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await WithAsync(async conn =>
        {
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS snapshot (
                    id INTEGER PRIMARY KEY,
                    taken_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS quote (
                    snapshot_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    best_buy TEXT,
                    best_sell TEXT NOT NULL,
                    buy_volume INTEGER NOT NULL,
                    sell_volume INTEGER NOT NULL,
                    buy_orders INTEGER NOT NULL,
                    sell_orders INTEGER NOT NULL,
                    PRIMARY KEY (snapshot_id, type_id)
                );
                CREATE TABLE IF NOT EXISTS type_name (
                    type_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS daily_history (
                    region_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    day TEXT NOT NULL,
                    volume INTEGER NOT NULL,
                    average TEXT NOT NULL,
                    highest TEXT NOT NULL,
                    lowest TEXT NOT NULL,
                    order_count INTEGER NOT NULL,
                    PRIMARY KEY (region_id, type_id, day)
                );
                CREATE TABLE IF NOT EXISTS history_sync (
                    region_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    synced_on TEXT NOT NULL,
                    PRIMARY KEY (region_id, type_id)
                );
                CREATE TABLE IF NOT EXISTS setting (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS reject (
                    type_id INTEGER PRIMARY KEY,
                    reason TEXT NOT NULL,
                    checked_on TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS status (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    state TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    last_snapshot_at TEXT,
                    last_error TEXT,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS location_meta (
                    location_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    system_id INTEGER NOT NULL,
                    system_name TEXT NOT NULL,
                    region_id INTEGER NOT NULL,
                    hub TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS location_quote (
                    snapshot_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    location_id INTEGER NOT NULL,
                    region_id INTEGER NOT NULL,
                    best_buy TEXT,
                    best_sell TEXT NOT NULL,
                    buy_volume INTEGER NOT NULL,
                    sell_volume INTEGER NOT NULL,
                    buy_orders INTEGER NOT NULL,
                    sell_orders INTEGER NOT NULL,
                    buy_touch INTEGER NOT NULL DEFAULT 0,
                    sell_touch INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (snapshot_id, type_id, location_id)
                );
                CREATE TABLE IF NOT EXISTS route_cache (
                    from_system INTEGER NOT NULL,
                    to_system INTEGER NOT NULL,
                    jumps INTEGER NOT NULL,
                    PRIMARY KEY (from_system, to_system)
                );
                CREATE INDEX IF NOT EXISTS ix_snapshot_taken ON snapshot(taken_at);
                CREATE INDEX IF NOT EXISTS ix_quote_type ON quote(type_id, snapshot_id);
                CREATE INDEX IF NOT EXISTS ix_location_quote_snap ON location_quote(snapshot_id);
                INSERT INTO status (id, state, detail, updated_at)
                VALUES (1, 'idle', '', $now)
                ON CONFLICT(id) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('accounting', '5') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('broker', '5') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('faction', '0') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('corp', '0') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('keep_days', '120') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('tray_notify', '1') ON CONFLICT(key) DO NOTHING;
                CREATE TABLE IF NOT EXISTS watchlist (
                    type_id INTEGER NOT NULL,
                    location_id INTEGER NOT NULL,
                    region_id INTEGER NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (type_id, location_id)
                );
                CREATE TABLE IF NOT EXISTS watch_order (
                    order_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    location_id INTEGER NOT NULL,
                    is_buy INTEGER NOT NULL,
                    price TEXT NOT NULL,
                    volume_remain INTEGER NOT NULL,
                    seen_at TEXT NOT NULL,
                    PRIMARY KEY (order_id)
                );
                CREATE INDEX IF NOT EXISTS ix_watch_order_loc ON watch_order(type_id, location_id);
                CREATE TABLE IF NOT EXISTS watch_fill (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    order_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    location_id INTEGER NOT NULL,
                    is_buy INTEGER NOT NULL,
                    price TEXT NOT NULL,
                    volume INTEGER NOT NULL,
                    filled_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_watch_fill_type ON watch_fill(type_id, location_id, filled_at);
                CREATE TABLE IF NOT EXISTS price_mover (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type_id INTEGER NOT NULL,
                    location_id INTEGER NOT NULL,
                    side TEXT NOT NULL,
                    order_id INTEGER NOT NULL,
                    price TEXT NOT NULL,
                    seen_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_price_mover_seen ON price_mover(type_id, location_id, seen_at);
                CREATE TABLE IF NOT EXISTS esi_etag (
                    cache_key TEXT PRIMARY KEY,
                    etag TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """, ("$now", Stamp(DateTime.UtcNow)));
            await EnsureColumnAsync(conn, "quote", "buy_touch", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnAsync(conn, "quote", "sell_touch", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnAsync(conn, "location_quote", "best_buy_issued", "TEXT", ct);
            await EnsureColumnAsync(conn, "location_quote", "best_sell_issued", "TEXT", ct);
            await EnsureColumnAsync(conn, "type_name", "volume", "TEXT", ct);
            await MigrateHistoryRegionAsync(conn, ct);
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS type_note (
                    type_id INTEGER PRIMARY KEY,
                    note TEXT NOT NULL DEFAULT '',
                    blacklisted INTEGER NOT NULL DEFAULT 0
                );
                """);
        }, ct);
    }

    public Task SetStatusAsync(string state, string detail, string? error, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            UPDATE status
            SET state = $state, detail = $detail, last_error = $error, updated_at = $now
            WHERE id = 1
            """,
            ("$state", state),
            ("$detail", detail),
            ("$error", (object?)error ?? DBNull.Value),
            ("$now", Stamp(DateTime.UtcNow))), ct);

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT state, detail, last_snapshot_at, last_error,
                    (SELECT COUNT(*) FROM quote WHERE snapshot_id = (
                        SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1))
                FROM status WHERE id = 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new StatusDto();
            return new StatusDto
            {
                State = reader.GetString(0),
                Detail = reader.GetString(1),
                LastSnapshotAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastError = reader.IsDBNull(3) ? null : reader.GetString(3),
                LatestTypes = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)
            };
        }, ct);

    public Task<bool> GetTrayNotifyAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM setting WHERE key = 'tray_notify'";
            var value = await cmd.ExecuteScalarAsync(ct) as string;
            return value is null or "1" or "true" or "yes";
        }, ct);

    public Task SetTrayNotifyAsync(bool enabled, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO setting (key, value) VALUES ('tray_notify', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            ("$v", enabled ? "1" : "0")), ct);

    public Task<FeeSettings> GetSettingsAsync(CancellationToken ct = default) =>
        WithAsync(async conn => (await ReadSettingsAsync(conn)) with { DatabaseBytes = DatabaseBytes() }, ct);

    public Task SaveSettingsAsync(FeeSettings settings, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO setting (key, value) VALUES ('accounting', $a)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('broker', $b)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('faction', $f)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('corp', $c)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('keep_days', $k)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('filter_cosmetics', $fc)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('filter_special_edition', $fs)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('filter_blueprints', $fb)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('min_sell_price', $msp)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO setting (key, value) VALUES ('min_haul_sell_price', $mhp)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """,
            ("$a", settings.Accounting.ToString(CultureInfo.InvariantCulture)),
            ("$b", settings.BrokerRelations.ToString(CultureInfo.InvariantCulture)),
            ("$f", settings.FactionStanding.ToString(CultureInfo.InvariantCulture)),
            ("$c", settings.CorpStanding.ToString(CultureInfo.InvariantCulture)),
            ("$k", settings.KeepDays.ToString(CultureInfo.InvariantCulture)),
            ("$fc", settings.FilterCosmetics ? "1" : "0"),
            ("$fs", settings.FilterSpecialEdition ? "1" : "0"),
            ("$fb", settings.FilterBlueprints ? "1" : "0"),
            ("$msp", settings.MinSellPrice.ToString(CultureInfo.InvariantCulture)),
            ("$mhp", settings.MinHaulSellPrice.ToString(CultureInfo.InvariantCulture))), ct);

    public Task<DateTime?> GetLastSnapshotUtcAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT taken_at FROM snapshot ORDER BY taken_at DESC LIMIT 1";
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is not string text)
                return (DateTime?)null;
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }, ct);

    public Task UpsertLocationsAsync(IReadOnlyList<LocationMetaRow> rows, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO location_meta (location_id, name, system_id, system_name, region_id, hub)
                VALUES ($id, $name, $sys, $sysn, $reg, $hub)
                ON CONFLICT(location_id) DO UPDATE SET
                    name = excluded.name,
                    system_id = excluded.system_id,
                    system_name = excluded.system_name,
                    region_id = excluded.region_id,
                    hub = excluded.hub
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pSys = cmd.Parameters.Add("$sys", SqliteType.Integer);
            var pSysn = cmd.Parameters.Add("$sysn", SqliteType.Text);
            var pReg = cmd.Parameters.Add("$reg", SqliteType.Integer);
            var pHub = cmd.Parameters.Add("$hub", SqliteType.Text);
            foreach (var row in rows)
            {
                pId.Value = row.LocationId;
                pName.Value = row.Name;
                pSys.Value = row.SystemId;
                pSysn.Value = row.SystemName;
                pReg.Value = row.RegionId;
                pHub.Value = row.Hub;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);

    public Task<int?> GetRouteJumpsAsync(int fromSystem, int toSystem, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT jumps FROM route_cache WHERE from_system = $a AND to_system = $b";
            cmd.Parameters.AddWithValue("$a", fromSystem);
            cmd.Parameters.AddWithValue("$b", toSystem);
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is null || value is DBNull)
                return (int?)null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }, ct);

    public Task SaveRouteJumpsAsync(int fromSystem, int toSystem, int jumps, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO route_cache (from_system, to_system, jumps) VALUES ($a, $b, $j)
            ON CONFLICT(from_system, to_system) DO UPDATE SET jumps = excluded.jumps
            """,
            ("$a", fromSystem),
            ("$b", toSystem),
            ("$j", jumps)), ct);

    public Task<IReadOnlyList<int>> TypesMissingVolumeAsync(int limit, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT type_id FROM (
                    SELECT type_id FROM type_name
                    WHERE volume IS NULL OR volume = ''
                    UNION
                    SELECT lq.type_id
                    FROM location_quote lq
                    LEFT JOIN type_name n ON n.type_id = lq.type_id
                    WHERE n.type_id IS NULL OR n.volume IS NULL OR n.volume = ''
                )
                LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<int>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(ReadInt(reader, 0));
            return (IReadOnlyList<int>)list;
        }, ct);

    public Task SaveTypeVolumeAsync(int typeId, decimal volume, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO type_name (type_id, name, volume) VALUES ($t, $name, $v)
            ON CONFLICT(type_id) DO UPDATE SET volume = excluded.volume
            """,
            ("$t", typeId),
            ("$name", "#" + typeId.ToString(CultureInfo.InvariantCulture)),
            ("$v", Dec(volume))), ct);

    public Task<int> InsertSnapshotAsync(
        DateTime takenUtc,
        IReadOnlyList<QuoteInsert> boardRows,
        IReadOnlyList<LocationQuoteInsert> locationRows,
        CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var snap = conn.CreateCommand();
            snap.Transaction = (SqliteTransaction)tx;
            snap.CommandText = "INSERT INTO snapshot (taken_at) VALUES ($t)";
            snap.Parameters.AddWithValue("$t", Stamp(takenUtc));
            await snap.ExecuteNonQueryAsync(ct);
            snap.CommandText = "SELECT last_insert_rowid()";
            snap.Parameters.Clear();
            var id = (long)(await snap.ExecuteScalarAsync(ct) ?? 0L);

            await using var quote = conn.CreateCommand();
            quote.Transaction = (SqliteTransaction)tx;
            quote.CommandText = """
                INSERT INTO quote (
                    snapshot_id, type_id, best_buy, best_sell, buy_volume, sell_volume, buy_orders, sell_orders,
                    buy_touch, sell_touch)
                VALUES ($sid, $type, $bb, $bs, $bv, $sv, $bo, $so, $bt, $st)
                """;
            var pSid = quote.Parameters.Add("$sid", SqliteType.Integer);
            var pType = quote.Parameters.Add("$type", SqliteType.Integer);
            var pBb = quote.Parameters.Add("$bb", SqliteType.Text);
            var pBs = quote.Parameters.Add("$bs", SqliteType.Text);
            var pBv = quote.Parameters.Add("$bv", SqliteType.Integer);
            var pSv = quote.Parameters.Add("$sv", SqliteType.Integer);
            var pBo = quote.Parameters.Add("$bo", SqliteType.Integer);
            var pSo = quote.Parameters.Add("$so", SqliteType.Integer);
            var pBt = quote.Parameters.Add("$bt", SqliteType.Integer);
            var pSt = quote.Parameters.Add("$st", SqliteType.Integer);
            pSid.Value = id;
            foreach (var row in boardRows)
            {
                pType.Value = row.TypeId;
                pBb.Value = row.BestBuy is null ? DBNull.Value : Dec(row.BestBuy.Value);
                pBs.Value = Dec(row.BestSell);
                pBv.Value = row.BuyVolume;
                pSv.Value = row.SellVolume;
                pBo.Value = row.BuyOrders;
                pSo.Value = row.SellOrders;
                pBt.Value = row.BuyTouch;
                pSt.Value = row.SellTouch;
                await quote.ExecuteNonQueryAsync(ct);
            }

            await using var loc = conn.CreateCommand();
            loc.Transaction = (SqliteTransaction)tx;
            loc.CommandText = """
                INSERT INTO location_quote (
                    snapshot_id, type_id, location_id, region_id, best_buy, best_sell,
                    buy_volume, sell_volume, buy_orders, sell_orders, buy_touch, sell_touch,
                    best_buy_issued, best_sell_issued)
                VALUES ($sid, $type, $loc, $reg, $bb, $bs, $bv, $sv, $bo, $so, $bt, $st, $bbi, $bsi)
                """;
            var lSid = loc.Parameters.Add("$sid", SqliteType.Integer);
            var lType = loc.Parameters.Add("$type", SqliteType.Integer);
            var lLoc = loc.Parameters.Add("$loc", SqliteType.Integer);
            var lReg = loc.Parameters.Add("$reg", SqliteType.Integer);
            var lBb = loc.Parameters.Add("$bb", SqliteType.Text);
            var lBs = loc.Parameters.Add("$bs", SqliteType.Text);
            var lBv = loc.Parameters.Add("$bv", SqliteType.Integer);
            var lSv = loc.Parameters.Add("$sv", SqliteType.Integer);
            var lBo = loc.Parameters.Add("$bo", SqliteType.Integer);
            var lSo = loc.Parameters.Add("$so", SqliteType.Integer);
            var lBt = loc.Parameters.Add("$bt", SqliteType.Integer);
            var lSt = loc.Parameters.Add("$st", SqliteType.Integer);
            var lBbi = loc.Parameters.Add("$bbi", SqliteType.Text);
            var lBsi = loc.Parameters.Add("$bsi", SqliteType.Text);
            lSid.Value = id;
            foreach (var row in locationRows)
            {
                lType.Value = row.TypeId;
                lLoc.Value = row.LocationId;
                lReg.Value = row.RegionId;
                lBb.Value = row.BestBuy is null ? DBNull.Value : Dec(row.BestBuy.Value);
                lBs.Value = Dec(row.BestSell);
                lBv.Value = row.BuyVolume;
                lSv.Value = row.SellVolume;
                lBo.Value = row.BuyOrders;
                lSo.Value = row.SellOrders;
                lBt.Value = row.BuyTouch;
                lSt.Value = row.SellTouch;
                lBbi.Value = row.BestBuyIssued is null ? DBNull.Value : Stamp(row.BestBuyIssued.Value);
                lBsi.Value = row.BestSellIssued is null ? DBNull.Value : Stamp(row.BestSellIssued.Value);
                await loc.ExecuteNonQueryAsync(ct);
            }

            await using var touch = conn.CreateCommand();
            touch.Transaction = (SqliteTransaction)tx;
            touch.CommandText = """
                UPDATE status SET last_snapshot_at = $t, last_error = NULL, updated_at = $t WHERE id = 1
                """;
            touch.Parameters.AddWithValue("$t", Stamp(takenUtc));
            await touch.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return boardRows.Count;
        }, ct);

    public Task<IReadOnlyList<int>> LatestTypeIdsAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT type_id FROM quote
                WHERE snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return (IReadOnlyList<int>)list;
        }, ct);

    /// <summary>Типы закупа и развоза без нормального имени (нет строки или заглушка #id).</summary>
    public Task<IReadOnlyList<int>> TypesNeedingNamesAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT type_id FROM (
                    SELECT q.type_id AS type_id
                    FROM quote q
                    LEFT JOIN type_name n ON n.type_id = q.type_id
                    WHERE q.snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                      AND q.type_id NOT IN (SELECT type_id FROM reject)
                      AND (n.type_id IS NULL OR IFNULL(n.name, '') = '' OR n.name LIKE '#%')
                    UNION
                    SELECT lq.type_id
                    FROM location_quote lq
                    LEFT JOIN type_name n ON n.type_id = lq.type_id
                    WHERE lq.snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                      AND lq.type_id NOT IN (SELECT type_id FROM reject)
                      AND (n.type_id IS NULL OR IFNULL(n.name, '') = '' OR n.name LIKE '#%')
                )
                ORDER BY type_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return (IReadOnlyList<int>)list;
        }, ct);

    public Task<HashSet<int>> RejectedIdsAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var set = new HashSet<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type_id FROM reject";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                set.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return set;
        }, ct);

    public Task RejectAsync(IReadOnlyList<int> typeIds, string reason, string todayUtc, CancellationToken ct = default)
    {
        if (typeIds.Count == 0)
            return Task.CompletedTask;
        return WithAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var chunk in typeIds.Chunk(400))
            {
                var parameters = string.Join(", ", chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)));
                await using var delQuote = conn.CreateCommand();
                delQuote.Transaction = (SqliteTransaction)tx;
                delQuote.CommandText = $"DELETE FROM quote WHERE type_id IN ({parameters})";
                await using var delHist = conn.CreateCommand();
                delHist.Transaction = (SqliteTransaction)tx;
                delHist.CommandText = $"DELETE FROM daily_history WHERE type_id IN ({parameters})";
                await using var delSync = conn.CreateCommand();
                delSync.Transaction = (SqliteTransaction)tx;
                delSync.CommandText = $"DELETE FROM history_sync WHERE type_id IN ({parameters})";
                for (var i = 0; i < chunk.Length; i++)
                {
                    var value = chunk[i];
                    delQuote.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), value);
                    delHist.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), value);
                    delSync.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), value);
                }

                await delQuote.ExecuteNonQueryAsync(ct);
                await delHist.ExecuteNonQueryAsync(ct);
                await delSync.ExecuteNonQueryAsync(ct);
            }

            await using var mark = conn.CreateCommand();
            mark.Transaction = (SqliteTransaction)tx;
            mark.CommandText = """
                INSERT INTO reject (type_id, reason, checked_on) VALUES ($id, $reason, $day)
                ON CONFLICT(type_id) DO UPDATE SET reason = excluded.reason, checked_on = excluded.checked_on
                """;
            var pId = mark.Parameters.Add("$id", SqliteType.Integer);
            var pReason = mark.Parameters.Add("$reason", SqliteType.Text);
            var pDay = mark.Parameters.Add("$day", SqliteType.Text);
            pReason.Value = reason;
            pDay.Value = todayUtc;
            foreach (var typeId in typeIds)
            {
                pId.Value = typeId;
                await mark.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }, ct);
    }

    public async Task<int> RejectNamedAsync(Func<string, bool> match, string reason, string todayUtc, CancellationToken ct = default)
    {
        var ids = await WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT n.type_id, n.name
                FROM type_name n
                LEFT JOIN reject r ON r.type_id = n.type_id
                WHERE r.reason IS NULL OR r.reason = 'no_trade'
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (match(name))
                    list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            }

            return list;
        }, ct);
        await RejectAsync(ids, reason, todayUtc, ct);
        return ids.Count;
    }

    public Task UnrejectAsync(int typeId, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, "DELETE FROM reject WHERE type_id = $id AND reason = 'no_trade'", ("$id", typeId)), ct);

    public Task ClearRejectReasonsAsync(IReadOnlyList<string> reasons, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            foreach (var reason in reasons)
            {
                await ExecAsync(conn, "DELETE FROM reject WHERE reason = $r", ("$r", reason));
            }
        }, ct);

    public Task<IReadOnlyList<int>> RejectsToRecheckAsync(string todayUtc, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT type_id FROM reject
                WHERE reason = 'no_trade' AND checked_on < $today
                """;
            cmd.Parameters.AddWithValue("$today", todayUtc);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return (IReadOnlyList<int>)list;
        }, ct);

    public Task<IReadOnlyList<int>> MissingNamesAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT q.type_id
                FROM quote q
                LEFT JOIN type_name n ON n.type_id = q.type_id
                WHERE n.type_id IS NULL
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return (IReadOnlyList<int>)list;
        }, ct);

    public Task UpsertNamesAsync(IReadOnlyList<(int TypeId, string Name)> names, CancellationToken ct = default)
    {
        if (names.Count == 0)
            return Task.CompletedTask;
        return WithAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO type_name (type_id, name) VALUES ($id, $name)
                ON CONFLICT(type_id) DO UPDATE SET name = excluded.name
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            foreach (var (typeId, name) in names)
            {
                pId.Value = typeId;
                pName.Value = name;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }, ct);
    }

    public Task<IReadOnlyList<HistoryNeed>> TypesNeedingHistoryAsync(string todayUtc, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<HistoryNeed>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT type_id, region_id FROM (
                    SELECT q.type_id AS type_id, $forge AS region_id,
                        (CAST(q.best_sell AS REAL) - IFNULL(CAST(q.best_buy AS REAL), 0)) AS score
                    FROM quote q
                    LEFT JOIN history_sync h ON h.type_id = q.type_id AND h.region_id = $forge
                    WHERE q.snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                      AND q.type_id NOT IN (SELECT type_id FROM reject)
                      AND (h.synced_on IS NULL OR h.synced_on < $today)
                    UNION
                    SELECT lq.type_id, lq.region_id,
                        (CAST(lq.best_sell AS REAL) - IFNULL(CAST(lq.best_buy AS REAL), 0)) AS score
                    FROM location_quote lq
                    LEFT JOIN history_sync h ON h.type_id = lq.type_id AND h.region_id = lq.region_id
                    WHERE lq.snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                      AND lq.type_id NOT IN (SELECT type_id FROM reject)
                      AND (h.synced_on IS NULL OR h.synced_on < $today)
                )
                GROUP BY type_id, region_id
                ORDER BY MAX(score) DESC
                """;
            cmd.Parameters.AddWithValue("$today", todayUtc);
            cmd.Parameters.AddWithValue("$forge", TradeHubs.ForgeRegionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new HistoryNeed(
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)));
            }

            return (IReadOnlyList<HistoryNeed>)list;
        }, ct);

    public Task UpsertHistoryAsync(
        int regionId,
        int typeId,
        IReadOnlyList<HistoryBar> bars,
        string todayUtc,
        int keepDays,
        CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var cut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-keepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO daily_history (region_id, type_id, day, volume, average, highest, lowest, order_count)
                VALUES ($reg, $id, $day, $vol, $avg, $hi, $lo, $cnt)
                ON CONFLICT(region_id, type_id, day) DO UPDATE SET
                    volume = excluded.volume,
                    average = excluded.average,
                    highest = excluded.highest,
                    lowest = excluded.lowest,
                    order_count = excluded.order_count
                """;
            var pReg = cmd.Parameters.Add("$reg", SqliteType.Integer);
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pDay = cmd.Parameters.Add("$day", SqliteType.Text);
            var pVol = cmd.Parameters.Add("$vol", SqliteType.Integer);
            var pAvg = cmd.Parameters.Add("$avg", SqliteType.Text);
            var pHi = cmd.Parameters.Add("$hi", SqliteType.Text);
            var pLo = cmd.Parameters.Add("$lo", SqliteType.Text);
            var pCnt = cmd.Parameters.Add("$cnt", SqliteType.Integer);
            pReg.Value = regionId;
            pId.Value = typeId;
            foreach (var bar in bars)
            {
                var day = bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(day, cut) < 0)
                    continue;
                pDay.Value = day;
                pVol.Value = bar.Volume;
                pAvg.Value = Dec(bar.Average);
                pHi.Value = Dec(bar.Highest);
                pLo.Value = Dec(bar.Lowest);
                pCnt.Value = bar.OrderCount;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var sync = conn.CreateCommand();
            sync.Transaction = (SqliteTransaction)tx;
            sync.CommandText = """
                INSERT INTO history_sync (region_id, type_id, synced_on) VALUES ($reg, $id, $today)
                ON CONFLICT(region_id, type_id) DO UPDATE SET synced_on = excluded.synced_on
                """;
            sync.Parameters.AddWithValue("$reg", regionId);
            sync.Parameters.AddWithValue("$id", typeId);
            sync.Parameters.AddWithValue("$today", todayUtc);
            await sync.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }, ct);

    public Task PurgeAsync(int keepDays, bool shrink, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var days = Math.Clamp(keepDays, 1, 400);
            var cut = Stamp(DateTime.UtcNow.AddDays(-days));
            var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await ExecAsync(conn, """
                DELETE FROM location_quote WHERE snapshot_id IN (SELECT id FROM snapshot WHERE taken_at < $cut);
                DELETE FROM quote WHERE snapshot_id IN (SELECT id FROM snapshot WHERE taken_at < $cut);
                DELETE FROM snapshot WHERE taken_at < $cut;
                DELETE FROM daily_history WHERE day < $day;
                """,
                ("$cut", cut),
                ("$day", day));
            await ExecAsync(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
            if (shrink)
                await ExecAsync(conn, "VACUUM;");
        }, ct);

    private long DatabaseBytes()
    {
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = new FileInfo(_path + suffix);
            if (file.Exists)
                bytes += file.Length;
        }

        return bytes;
    }

    public Task<SignalDto> GetSignalsAsync(int page, string? sort, string? dir, string? kind, ColumnFilter filter, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var section = kind switch
            {
                "off" or "churn" or "squeeze" => kind,
                _ => "shout"
            };
            var need = section is "churn" or "squeeze" ? 4 : SignalRule.MinSnapshots;
            var load = await LoadRankedAsync(conn, ct);
            if (load.Snapshots < need)
            {
                return new SignalDto
                {
                    Tone = "early",
                    Banner = EarlyBanner(section, load.Snapshots, need),
                    Snapshots = load.Snapshots,
                    SalesTax = load.Fees.SalesTax,
                    BrokerFee = load.Fees.BrokerFee
                };
            }

            var candidates = load.Rows
                .Where(row => !IsBlacklisted(conn, row.TypeId))
                .Where(row => row.MarginFrac >= SignalRule.MinMarginFrac
                    && row.MarginFrac <= SignalRule.MaxMarginFrac
                    && row.DailyVolume >= SignalRule.MinDailyVolume)
                .ToList();
            await FillHoursAsync(conn, candidates, load.Fees, ct);
            if (section is "churn" or "shout")
            {
                foreach (var row in candidates)
                {
                    var moves = Math.Max(row.BuyMoves, row.SellMoves);
                    row.NetMarginIsk = FeeMath.NetMarginAfterRelists(
                        row.MarginIsk, row.BestSell, load.Fees.BrokerFee, moves);
                    row.Anomaly = Anomaly.IsSuspicious(row, null);
                }
            }

            var signals = candidates.Where(row => InSection(row, section)).ToList();
            var shout = section == "shout" && signals.Count > 0;
            var filtered = signals.Where(row => MatchesColumns(row, filter)).ToList();
            var sortKey = SortKey(sort);
            var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
            filtered.Sort((a, b) =>
            {
                var empty = NullsLast(a, b, sortKey);
                if (empty != 0)
                    return empty;
                var byKey = CompareRows(a, b, sortKey);
                if (byKey == 0)
                    byKey = CompareName(a.Name, b.Name);
                return descending ? -byKey : byKey;
            });

            const int pageSize = 200;
            var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
            var current = Math.Clamp(page, 1, pages);
            return new SignalDto
            {
                Tone = shout ? "shout" : signals.Count > 0 ? "list" : "quiet",
                Banner = section == "shout" && filtered.Count(r => r.Anomaly) > 0
                    ? SectionBanner(section, shout, signals.Count > 0)
                      + $" Подозрительных: {filtered.Count(r => r.Anomaly).ToString(CultureInfo.InvariantCulture)} (внизу списка)."
                    : SectionBanner(section, shout, signals.Count > 0),
                Snapshots = load.Snapshots,
                Shown = filtered.Count,
                Page = current,
                PageSize = pageSize,
                Pages = filtered.Count == 0 ? 1 : pages,
                SalesTax = load.Fees.SalesTax,
                BrokerFee = load.Fees.BrokerFee,
                Rows = filtered.Skip((current - 1) * pageSize).Take(pageSize).ToList()
            };
        }, ct);

    public Task<HaulDto> GetHaulAsync(int page, string? sort, string? dir, HaulFilter filter, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var fees = await ReadSettingsAsync(conn);
            await using var latest = conn.CreateCommand();
            latest.CommandText = "SELECT id, taken_at FROM snapshot ORDER BY taken_at DESC LIMIT 1";
            await using var latestReader = await latest.ExecuteReaderAsync(ct);
            if (!await latestReader.ReadAsync(ct))
                return new HaulDto { SalesTax = fees.SalesTax };

            var snapshotId = latestReader.GetInt64(0);
            var takenAt = latestReader.GetString(1);
            await latestReader.DisposeAsync();

            var metas = new Dictionary<long, LocationMetaRow>();
            await using var metaCmd = conn.CreateCommand();
            metaCmd.CommandText = "SELECT location_id, name, system_id, system_name, region_id, hub FROM location_meta";
            await using (var metaReader = await metaCmd.ExecuteReaderAsync(ct))
            {
                while (await metaReader.ReadAsync(ct))
                {
                    var id = metaReader.GetInt64(0);
                    metas[id] = new LocationMetaRow(
                        id,
                        metaReader.GetString(1),
                        metaReader.GetInt32(2),
                        metaReader.GetString(3),
                        metaReader.GetInt32(4),
                        metaReader.GetString(5));
                }
            }

            var routes = new Dictionary<(int, int), int>();
            await using var routeCmd = conn.CreateCommand();
            routeCmd.CommandText = "SELECT from_system, to_system, jumps FROM route_cache";
            await using (var routeReader = await routeCmd.ExecuteReaderAsync(ct))
            {
                while (await routeReader.ReadAsync(ct))
                    routes[(routeReader.GetInt32(0), routeReader.GetInt32(1))] = routeReader.GetInt32(2);
            }

            var volumes = new Dictionary<int, decimal>();
            var names = new Dictionary<int, string>();
            await using var nameCmd = conn.CreateCommand();
            nameCmd.CommandText = "SELECT type_id, name, volume FROM type_name";
            await using (var nameReader = await nameCmd.ExecuteReaderAsync(ct))
            {
                while (await nameReader.ReadAsync(ct))
                {
                    var typeId = ReadInt(nameReader, 0);
                    names[typeId] = nameReader.GetString(1);
                    if (!nameReader.IsDBNull(2) && decimal.TryParse(nameReader.GetString(2), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                        volumes[typeId] = vol;
                }
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dayVolume = new Dictionary<(int TypeId, int RegionId), long>();
            await using (var histCmd = conn.CreateCommand())
            {
                // последний полный день ESI (< сегодня UTC); «вчера» часто ещё не опубликовано
                histCmd.CommandText = """
                    SELECT h.type_id, h.region_id, h.day, h.volume
                    FROM daily_history h
                    JOIN (
                        SELECT type_id, region_id, MAX(day) AS day
                        FROM daily_history
                        WHERE day < $today
                        GROUP BY type_id, region_id
                    ) m ON m.type_id = h.type_id AND m.region_id = h.region_id AND m.day = h.day
                    """;
                histCmd.Parameters.AddWithValue("$today", today);
                await using var histReader = await histCmd.ExecuteReaderAsync(ct);
                while (await histReader.ReadAsync(ct))
                    dayVolume[(ReadInt(histReader, 0), ReadInt(histReader, 1))] = ReadLong(histReader, 3);
            }

            var rejected = new HashSet<int>();
            await using var rej = conn.CreateCommand();
            rej.CommandText = "SELECT type_id FROM reject";
            await using (var rejReader = await rej.ExecuteReaderAsync(ct))
            {
                while (await rejReader.ReadAsync(ct))
                    rejected.Add(ReadInt(rejReader, 0));
            }

            var byType = new Dictionary<int, List<LocBook>>();
            await using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT type_id, location_id, best_buy, best_sell, buy_volume, sell_volume, buy_touch, sell_touch
                FROM location_quote WHERE snapshot_id = $sid
                """;
            q.Parameters.AddWithValue("$sid", snapshotId);
            await using (var reader = await q.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var typeId = ReadInt(reader, 0);
                    if (rejected.Contains(typeId))
                        continue;
                    var locationId = reader.GetInt64(1);
                    if (!metas.ContainsKey(locationId))
                        continue;
                    if (!byType.TryGetValue(typeId, out var list))
                    {
                        list = [];
                        byType[typeId] = list;
                    }

                    list.Add(new LocBook(
                        locationId,
                        reader.IsDBNull(2) ? null : ReadDec(reader, 2),
                        ReadDec(reader, 3),
                        ReadLong(reader, 4),
                        ReadLong(reader, 5),
                        ReadLong(reader, 6),
                        ReadLong(reader, 7)));
                }
            }

            var rows = new List<HaulRow>();
            var minHaul = fees.MinHaulSellPrice > 0 ? fees.MinHaulSellPrice : 500_000m;
            foreach (var (typeId, spots) in byType)
            {
                for (var i = 0; i < spots.Count; i++)
                {
                    var from = spots[i];
                    if (from.SellVolume < 1 || from.BestSell < minHaul)
                        continue;
                    for (var j = 0; j < spots.Count; j++)
                    {
                        if (i == j)
                            continue;
                        var to = spots[j];
                        if (to.BestBuy is not decimal bid || bid <= 0 || to.BuyVolume < 1)
                            continue;
                        var (margin, frac) = FeeMath.InstantHaul(from.BestSell, bid, fees.SalesTax);
                        if (margin <= 0)
                            continue;
                        var listSell = to.BestSell;
                        var (listMargin, listFrac) = FeeMath.HaulAndList(
                            from.BestSell, listSell, fees.SalesTax, fees.BrokerFee);
                        var fromMeta = metas[from.LocationId];
                        var toMeta = metas[to.LocationId];
                        if (!string.IsNullOrWhiteSpace(filter.FromHub)
                            && !string.Equals(fromMeta.Hub, filter.FromHub, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(filter.ToHub)
                            && !string.Equals(toMeta.Hub, filter.ToHub, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!routes.TryGetValue((fromMeta.SystemId, toMeta.SystemId), out var jumps))
                            jumps = fromMeta.SystemId == toMeta.SystemId ? 0 : 99;
                        var sellDepth = from.SellTouch > 0 ? from.SellTouch : from.SellVolume;
                        var buyDepth = to.BuyTouch > 0 ? to.BuyTouch : to.BuyVolume;
                        var units = Math.Min(sellDepth, buyDepth);
                        if (units < 1)
                            continue;
                        volumes.TryGetValue(typeId, out var m3);
                        dayVolume.TryGetValue((typeId, toMeta.RegionId), out var traded);
                        var row = new HaulRow
                        {
                            TypeId = typeId,
                            Name = ResolveTypeName(names, typeId),
                            FromName = fromMeta.Name,
                            ToName = toMeta.Name,
                            FromLocationId = from.LocationId,
                            ToLocationId = to.LocationId,
                            Ask = from.BestSell,
                            Bid = bid,
                            MarginIsk = margin,
                            MarginFrac = frac,
                            Units = units,
                            DailyVolume = traded > 0 ? traded : null,
                            DailyIsk = margin * units,
                            Jumps = jumps,
                            VolumeM3 = m3 > 0 ? m3 : null,
                            CargoM3 = m3 > 0 ? m3 * units : null,
                            ListSell = listSell,
                            ListMarginIsk = listMargin,
                            ListMarginFrac = listFrac
                        };
                        if (!Hits(filter.Name, row.Name)
                            || !filter.MarginIsk.Fits(row.MarginIsk)
                            || !filter.MarginFrac.Fits(row.MarginFrac * 100m)
                            || !filter.Units.Fits(row.Units)
                            || !filter.DailyVolume.Fits(row.DailyVolume)
                            || !filter.Jumps.Fits(row.Jumps)
                            || !filter.DailyIsk.Fits(row.DailyIsk))
                            continue;
                        rows.Add(row);
                    }
                }
            }

            var sortKey = sort switch
            {
                "name" or "fromName" or "toName" or "ask" or "bid" or "marginIsk" or "marginFrac"
                    or "units" or "dailyVolume" or "dailyIsk" or "jumps" or "volumeM3" or "cargoM3"
                    or "listSell" or "listMarginIsk" or "listMarginFrac" => sort,
                _ => "dailyIsk"
            };
            var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
            rows.Sort((a, b) =>
            {
                var cmp = sortKey switch
                {
                    "name" => CompareName(a.Name, b.Name),
                    "fromName" => CompareName(a.FromName, b.FromName),
                    "toName" => CompareName(a.ToName, b.ToName),
                    "ask" => a.Ask.CompareTo(b.Ask),
                    "bid" => a.Bid.CompareTo(b.Bid),
                    "marginIsk" => a.MarginIsk.CompareTo(b.MarginIsk),
                    "marginFrac" => a.MarginFrac.CompareTo(b.MarginFrac),
                    "units" => a.Units.CompareTo(b.Units),
                    "dailyVolume" => Nullable.Compare(a.DailyVolume, b.DailyVolume),
                    "jumps" => a.Jumps.CompareTo(b.Jumps),
                    "volumeM3" => Nullable.Compare(a.VolumeM3, b.VolumeM3),
                    "cargoM3" => Nullable.Compare(a.CargoM3, b.CargoM3),
                    "listSell" => a.ListSell.CompareTo(b.ListSell),
                    "listMarginIsk" => a.ListMarginIsk.CompareTo(b.ListMarginIsk),
                    "listMarginFrac" => a.ListMarginFrac.CompareTo(b.ListMarginFrac),
                    _ => Nullable.Compare(a.DailyIsk, b.DailyIsk)
                };
                if (cmp == 0)
                    cmp = CompareName(a.Name, b.Name);
                return descending ? -cmp : cmp;
            });

            const int pageSize = 200;
            var pages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
            var current = Math.Clamp(page, 1, pages);
            return new HaulDto
            {
                TakenAt = takenAt,
                SalesTax = fees.SalesTax,
                Shown = rows.Count,
                Page = current,
                PageSize = pageSize,
                Pages = rows.Count == 0 ? 1 : pages,
                Rows = rows.Skip((current - 1) * pageSize).Take(pageSize).ToList()
            };
        }, ct);

    public Task<BoardDto> GetBoardAsync(
        int page, string? sort, string? dir, ColumnFilter filter, BoardSources sources, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var load = await LoadRankedAsync(conn, sources, ct);
            var fees = load.Fees;
            var ranked = load.Rows;
            if (load.TakenAt is null)
            {
                return new BoardDto { SalesTax = fees.SalesTax, BrokerFee = fees.BrokerFee };
            }

            var takenAt = load.TakenAt;
            var rawCount = load.Tracked;

            var sortKey = SortKey(sort);
            var hoursFilterOn = filter.Hours.Min is not null || filter.Hours.Max is not null || filter.Hours.Bad;
            // Часы считаются отдельным проходом; до FillHours у строк Hours=0 — фильтр/сорт по часам только после.
            if (hoursFilterOn || sortKey == "hours")
                await FillHoursAsync(conn, ranked, fees, ct);
            var filtered = ranked
                .Where(row => row.MarginFrac * 100m <= MaxBoardMarginPercent && MatchesColumns(row, filter))
                .ToList();
            var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
            filtered.Sort((a, b) =>
            {
                var empty = NullsLast(a, b, sortKey);
                if (empty != 0)
                    return empty;
                var byKey = CompareRows(a, b, sortKey);
                if (byKey == 0)
                    byKey = CompareName(a.Name, b.Name);
                return descending ? -byKey : byKey;
            });

            const int pageSize = 200;
            var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
            var current = Math.Clamp(page, 1, pages);
            var rows = filtered.Skip((current - 1) * pageSize).Take(pageSize).ToList();
            if (!hoursFilterOn && sortKey != "hours")
                await FillHoursAsync(conn, rows, fees, ct);
            return new BoardDto
            {
                TakenAt = takenAt,
                SalesTax = fees.SalesTax,
                BrokerFee = fees.BrokerFee,
                Tracked = rawCount,
                Shown = filtered.Count,
                Page = current,
                PageSize = pageSize,
                Pages = filtered.Count == 0 ? 1 : pages,
                Rows = rows
            };
        }, ct);

    private async Task<RankedLoad> LoadRankedAsync(SqliteConnection conn, CancellationToken ct) =>
        await LoadRankedAsync(conn, BoardSources.Both, ct);

    private async Task<RankedLoad> LoadRankedAsync(SqliteConnection conn, BoardSources sources, CancellationToken ct)
    {
        var fees = await ReadSettingsAsync(conn);
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM snapshot";
        var snapshots = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture);

        await using var latest = conn.CreateCommand();
        latest.CommandText = "SELECT id, taken_at FROM snapshot ORDER BY taken_at DESC LIMIT 1";
        await using var latestReader = await latest.ExecuteReaderAsync(ct);
        if (!await latestReader.ReadAsync(ct))
            return new RankedLoad(fees, null, 0, snapshots, []);

        var snapshotId = latestReader.GetInt64(0);
        var takenAt = latestReader.GetString(1);
        await latestReader.DisposeAsync();

        var raw = sources.Any
            ? await LoadBoardQuotesAsync(conn, snapshotId, sources, ct)
            : [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from30 = today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var history = new Dictionary<int, Hist>();
        var histWindows = new Dictionary<int, List<(string Day, long Vol, decimal Avg)>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT type_id, day, volume, average
                FROM daily_history
                WHERE region_id = $forge AND day < $today AND day >= $from30
                ORDER BY type_id, day
                """;
            cmd.Parameters.AddWithValue("$today", todayText);
            cmd.Parameters.AddWithValue("$from30", from30);
            cmd.Parameters.AddWithValue("$forge", TradeHubs.ForgeRegionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var typeId = ReadInt(reader, 0);
                var day = reader.GetString(1);
                var vol = ReadLong(reader, 2);
                var avg = ReadDec(reader, 3);
                if (!histWindows.TryGetValue(typeId, out var list))
                {
                    list = [];
                    histWindows[typeId] = list;
                }

                list.Add((day, vol, avg));
            }
        }

        foreach (var (typeId, bars) in histWindows)
        {
            if (bars.Count == 0)
                continue;
            var last = bars[^1];
            history[typeId] = new Hist(last.Day, last.Vol, last.Avg);
        }

        var watchedTypes = new HashSet<int>();
        await using (var wcmd = conn.CreateCommand())
        {
            wcmd.CommandText = "SELECT DISTINCT type_id FROM watchlist";
            await using var wr = await wcmd.ExecuteReaderAsync(ct);
            while (await wr.ReadAsync(ct))
                watchedTypes.Add(ReadInt(wr, 0));
        }

        var fills = new Dictionary<int, (long Buy, long Sell)>();
        var sinceFill = Stamp(DateTime.UtcNow.AddHours(-24));
        await using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = """
                SELECT type_id, is_buy, SUM(volume)
                FROM watch_fill WHERE filled_at >= $since
                GROUP BY type_id, is_buy
                """;
            fcmd.Parameters.AddWithValue("$since", sinceFill);
            await using var fr = await fcmd.ExecuteReaderAsync(ct);
            while (await fr.ReadAsync(ct))
            {
                var tid = ReadInt(fr, 0);
                var isBuy = fr.GetInt64(1) != 0;
                var vol = ReadLong(fr, 2);
                fills.TryGetValue(tid, out var cur);
                fills[tid] = isBuy ? (cur.Buy + vol, cur.Sell) : (cur.Buy, cur.Sell + vol);
            }
        }

        var competitors = new Dictionary<int, int>();
        var sinceMove = Stamp(DateTime.UtcNow.AddHours(-6));
        await using (var ccmd = conn.CreateCommand())
        {
            ccmd.CommandText = """
                SELECT type_id, COUNT(DISTINCT order_id)
                FROM price_mover
                WHERE seen_at >= $since AND order_id > 0
                GROUP BY type_id
                """;
            ccmd.Parameters.AddWithValue("$since", sinceMove);
            await using var cr = await ccmd.ExecuteReaderAsync(ct);
            while (await cr.ReadAsync(ct))
                competitors[ReadInt(cr, 0)] = ReadInt(cr, 1);
        }

        var ranked = new List<BoardRow>();
        foreach (var quote in raw)
        {
            if (quote.BestBuy is not decimal buy || buy <= 0)
                continue;
            var (margin, fraction) = FeeMath.Margin(buy, quote.BestSell, fees.SalesTax, fees.BrokerFee);
            if (margin <= 0)
                continue;
            history.TryGetValue(quote.TypeId, out var hist);
            histWindows.TryGetValue(quote.TypeId, out var bars);
            var vols7 = bars?.TakeLast(7).Select(b => b.Vol).ToList() ?? [];
            var vols30 = bars?.Select(b => b.Vol).ToList() ?? [];
            var median7 = MedianLong(vols7);
            var avg7 = vols7.Count > 0 ? (long)vols7.Average() : (long?)null;
            var median30 = MedianLong(vols30);
            var volumeForEstimate = median7 ?? (hist?.Volume);
            var volumeDay = median7 is not null ? "median7" : hist?.Day;
            var pile = Math.Min(quote.BuyVolume, quote.SellVolume);
            competitors.TryGetValue(quote.TypeId, out var movers);
            var rivals = movers > 0
                ? movers
                : Math.Max(1, Math.Max(quote.BuyOrders, quote.SellOrders));
            if (rivals < 1)
                rivals = 1;
            var share = 1m / rivals;
            long? captureVol = volumeForEstimate is long v
                ? Math.Max(1, (long)Math.Floor(v * (double)share))
                : null;
            var depth = captureVol is long cap
                ? Math.Min(cap, pile)
                : (long?)null;
            var capital = pile > 0 ? buy * pile : (decimal?)null;
            fills.TryGetValue(quote.TypeId, out var fill);
            var stationBuy = fill.Buy > 0 ? fill.Buy : (long?)null;
            var stationSell = fill.Sell > 0 ? fill.Sell : (long?)null;
            long? stationTotal = stationBuy is null && stationSell is null
                ? null
                : (stationBuy ?? 0) + (stationSell ?? 0);
            ranked.Add(new BoardRow
            {
                TypeId = quote.TypeId,
                Name = quote.Name,
                BestBuy = buy,
                BestSell = quote.BestSell,
                MarginIsk = margin,
                MarginFrac = fraction,
                DailyVolume = volumeForEstimate,
                VolumeDay = volumeDay,
                Average = hist?.Average,
                DailyIsk = depth is null ? null : margin * depth.Value,
                VolumeMedian7 = median7,
                VolumeAvg7 = avg7,
                VolumeMedian30 = median30,
                CaptureShare = share,
                Competitors = rivals,
                StationVolume24h = stationTotal,
                StationBuyFilled24h = stationBuy,
                StationSellFilled24h = stationSell,
                Watched = watchedTypes.Contains(quote.TypeId),
                Hours = 0,
                BuyVolume = quote.BuyVolume,
                SellVolume = quote.SellVolume,
                BuyOrders = quote.BuyOrders,
                SellOrders = quote.SellOrders,
                BuyTouch = quote.BuyTouch > 0 ? quote.BuyTouch : null,
                SellTouch = quote.SellTouch > 0 ? quote.SellTouch : null,
                Relists = FeeMath.Relists(margin, quote.BestSell, fees.BrokerFee),
                Capital = capital,
                Days = volumeForEstimate is > 0 && pile > 0 ? pile / (decimal)volumeForEstimate.Value : null,
                DailyRoi = capital is > 0 && depth is not null ? margin * depth.Value / capital.Value : null
            });
        }

        return new RankedLoad(fees, takenAt, raw.Count, snapshots, ranked);
    }

    private static long? MedianLong(List<long> values)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static async Task<List<RawQuote>> LoadBoardQuotesAsync(
        SqliteConnection conn, long snapshotId, BoardSources sources, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM location_quote WHERE snapshot_id = $id";
        probe.Parameters.AddWithValue("$id", snapshotId);
        var locCount = Convert.ToInt32(await probe.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture);
        if (locCount == 0)
        {
            // Старый снимок без разбивки по локациям — только полная доска.
            if (!sources.Jita || !sources.Perimeter)
                return [];
            return await LoadMergedQuoteTableAsync(conn, snapshotId, ct);
        }

        var merge = new Dictionary<int, (string Name, BoardAcc Acc)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lq.type_id, IFNULL(n.name, '#' || lq.type_id), lq.best_buy, lq.best_sell,
                   lq.buy_volume, lq.sell_volume, lq.buy_orders, lq.sell_orders, lq.buy_touch, lq.sell_touch
            FROM location_quote lq
            JOIN location_meta m ON m.location_id = lq.location_id
            LEFT JOIN type_name n ON n.type_id = lq.type_id
            WHERE lq.snapshot_id = $id
              AND (
                    ($jita = 1 AND m.hub = 'jita')
                 OR ($peri = 1 AND m.hub = 'perimeter')
              )
              AND lq.type_id NOT IN (SELECT type_id FROM reject)
              AND IFNULL(n.name, '') NOT LIKE '% SKIN'
            """;
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.Parameters.AddWithValue("$jita", sources.Jita ? 1 : 0);
        cmd.Parameters.AddWithValue("$peri", sources.Perimeter ? 1 : 0);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var typeId = ReadInt(reader, 0);
            var name = reader.GetString(1);
            if (!merge.TryGetValue(typeId, out var entry))
            {
                entry = (name, new BoardAcc());
                merge[typeId] = entry;
            }

            entry.Acc.Add(
                reader.IsDBNull(2) ? null : ReadDec(reader, 2),
                ReadDec(reader, 3),
                ReadLong(reader, 4),
                ReadLong(reader, 5),
                ReadInt(reader, 6),
                ReadInt(reader, 7),
                ReadLong(reader, 8),
                ReadLong(reader, 9));
        }

        return merge
            .Where(kv => kv.Value.Acc.HasSell)
            .Select(kv => kv.Value.Acc.ToRaw(kv.Key, kv.Value.Name))
            .ToList();
    }

    private static async Task<List<RawQuote>> LoadMergedQuoteTableAsync(
        SqliteConnection conn, long snapshotId, CancellationToken ct)
    {
        var raw = new List<RawQuote>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT q.type_id, IFNULL(n.name, '#' || q.type_id), q.best_buy, q.best_sell,
                   q.buy_volume, q.sell_volume, q.buy_orders, q.sell_orders, q.buy_touch, q.sell_touch
            FROM quote q
            LEFT JOIN type_name n ON n.type_id = q.type_id
            WHERE q.snapshot_id = $id
              AND q.type_id NOT IN (SELECT type_id FROM reject)
              AND IFNULL(n.name, '') NOT LIKE '% SKIN'
            """;
        cmd.Parameters.AddWithValue("$id", snapshotId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            raw.Add(new RawQuote(
                ReadInt(reader, 0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : ReadDec(reader, 2),
                ReadDec(reader, 3),
                ReadLong(reader, 4),
                ReadLong(reader, 5),
                ReadInt(reader, 6),
                ReadInt(reader, 7),
                ReadLong(reader, 8),
                ReadLong(reader, 9)));
        }

        return raw;
    }

    private sealed class BoardAcc
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

        public void Add(
            decimal? bestBuy, decimal bestSell, long buyVolume, long sellVolume,
            int buyOrders, int sellOrders, long buyTouch, long sellTouch)
        {
            if (bestBuy is decimal buy && (BestBuy is null || buy > BestBuy))
                BestBuy = buy;
            if (!HasSell || bestSell < BestSell)
            {
                BestSell = bestSell;
                HasSell = true;
            }

            BuyVolume += buyVolume;
            SellVolume += sellVolume;
            BuyOrders += buyOrders;
            SellOrders += sellOrders;
            BuyTouch += buyTouch;
            SellTouch += sellTouch;
        }

        public RawQuote ToRaw(int typeId, string name) =>
            new(typeId, name, BestBuy, BestSell, BuyVolume, SellVolume, BuyOrders, SellOrders, BuyTouch, SellTouch);
    }

    private sealed record RankedLoad(FeeSettings Fees, string? TakenAt, int Tracked, int Snapshots, List<BoardRow> Rows);

    private static async Task FillHoursAsync(SqliteConnection conn, List<BoardRow> rows, FeeSettings fees, CancellationToken ct)
    {
        if (rows.Count == 0)
            return;
        var since = Stamp(DateTime.UtcNow.AddDays(-14));
            var series = new Dictionary<int, List<Point>>();
            if (rows.Count > 0)
            {
                await using var temp = conn.CreateCommand();
                temp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS pick (type_id INTEGER PRIMARY KEY); DELETE FROM pick;";
                await temp.ExecuteNonQueryAsync(ct);
                await using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO pick (type_id) VALUES ($id)";
                var p = ins.Parameters.Add("$id", SqliteType.Integer);
                foreach (var row in rows)
                {
                    p.Value = row.TypeId;
                    await ins.ExecuteNonQueryAsync(ct);
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT s.taken_at, q.type_id, q.best_buy, q.best_sell, q.buy_volume, q.sell_volume
                    FROM quote q
                    JOIN snapshot s ON s.id = q.snapshot_id
                    JOIN pick p ON p.type_id = q.type_id
                    WHERE s.taken_at >= $since AND q.best_buy IS NOT NULL
                    ORDER BY s.taken_at DESC
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var typeId = ReadInt(reader, 1);
                    if (!series.TryGetValue(typeId, out var list))
                    {
                        list = [];
                        series[typeId] = list;
                    }

                    list.Add(new Point(
                        DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        ReadDec(reader, 2),
                        ReadDec(reader, 3),
                        ReadLong(reader, 4),
                        ReadLong(reader, 5)));
                }
            }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var points = series.GetValueOrDefault(row.TypeId);
            var (buyMoves, sellMoves) = Moves(points);
            rows[i] = row with
            {
                Hours = Streak(points, fees),
                Thin = ThinSide(row.BuyVolume, row.SellVolume),
                BuyMoves = buyMoves,
                SellMoves = sellMoves,
                MarginThen = MarginBeforeDrop(points, fees)
            };
        }
    }

    private static int NullsLast(BoardRow a, BoardRow b, string sortKey)
    {
        bool? missing = sortKey switch
        {
            "dailyVolume" => a.DailyVolume is null,
            "dailyIsk" => a.DailyIsk is null,
            "average" => a.Average is null,
            "marginThen" => a.MarginThen is null,
            "buyTouch" => a.BuyTouch is null,
            "sellTouch" => a.SellTouch is null,
            "capital" => a.Capital is null,
            "days" => a.Days is null,
            "dailyRoi" => a.DailyRoi is null,
            "captureShare" => a.CaptureShare is null,
            "stationVolume24h" => a.StationVolume24h is null,
            "netMarginIsk" => a.NetMarginIsk is null,
            _ => null
        };
        if (missing is null)
            return 0;
        var other = sortKey switch
        {
            "dailyVolume" => b.DailyVolume is null,
            "average" => b.Average is null,
            "marginThen" => b.MarginThen is null,
            "buyTouch" => b.BuyTouch is null,
            "sellTouch" => b.SellTouch is null,
            "capital" => b.Capital is null,
            "days" => b.Days is null,
            "dailyRoi" => b.DailyRoi is null,
            "captureShare" => b.CaptureShare is null,
            "stationVolume24h" => b.StationVolume24h is null,
            "netMarginIsk" => b.NetMarginIsk is null,
            _ => b.DailyIsk is null
        };
        if (missing == other)
            return 0;
        return missing.Value ? 1 : -1;
    }

    private static int CompareName(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(
            a,
            b,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreNonSpace);

    private static int CompareRows(BoardRow a, BoardRow b, string sortKey) => sortKey switch
    {
        "name" => CompareName(a.Name, b.Name),
        "bestBuy" => a.BestBuy.CompareTo(b.BestBuy),
        "bestSell" => a.BestSell.CompareTo(b.BestSell),
        "marginIsk" => a.MarginIsk.CompareTo(b.MarginIsk),
        "marginFrac" => a.MarginFrac.CompareTo(b.MarginFrac),
        "dailyVolume" => Nullable.Compare(a.DailyVolume, b.DailyVolume),
        "dailyIsk" => Nullable.Compare(a.DailyIsk, b.DailyIsk),
        "hours" => a.Hours.CompareTo(b.Hours),
        "buyVolume" => a.BuyVolume.CompareTo(b.BuyVolume),
        "sellVolume" => a.SellVolume.CompareTo(b.SellVolume),
        "buyTouch" => Nullable.Compare(a.BuyTouch, b.BuyTouch),
        "sellTouch" => Nullable.Compare(a.SellTouch, b.SellTouch),
        "relists" => a.Relists.CompareTo(b.Relists),
        "capital" => Nullable.Compare(a.Capital, b.Capital),
        "days" => Nullable.Compare(a.Days, b.Days),
        "dailyRoi" => Nullable.Compare(a.DailyRoi, b.DailyRoi),
        "competitors" => a.Competitors.CompareTo(b.Competitors),
        "captureShare" => Nullable.Compare(a.CaptureShare, b.CaptureShare),
        "stationVolume24h" => Nullable.Compare(a.StationVolume24h, b.StationVolume24h),
        "netMarginIsk" => Nullable.Compare(a.NetMarginIsk, b.NetMarginIsk),
        "average" => Nullable.Compare(a.Average, b.Average),
        "buyMoves" => a.BuyMoves.CompareTo(b.BuyMoves),
        "sellMoves" => a.SellMoves.CompareTo(b.SellMoves),
        "marginThen" => Nullable.Compare(a.MarginThen, b.MarginThen),
        "thin" => string.Compare(a.Thin, b.Thin, StringComparison.CurrentCultureIgnoreCase),
        _ => Nullable.Compare(a.DailyIsk, b.DailyIsk)
    };

    private static string SortKey(string? sort) => sort switch
    {
        "name" or "bestBuy" or "bestSell" or "marginIsk" or "marginFrac"
            or "dailyVolume" or "dailyIsk" or "hours" or "buyVolume" or "sellVolume"
            or "average" or "buyMoves" or "sellMoves" or "marginThen" or "thin"
            or "buyTouch" or "sellTouch" or "relists" or "capital" or "days" or "dailyRoi"
            or "competitors" or "captureShare" or "stationVolume24h" or "netMarginIsk" => sort,
        _ => "dailyIsk"
    };

    private static bool MatchesColumns(BoardRow row, ColumnFilter filter)
    {
        return Hits(filter.Name, row.Name)
            && filter.BestBuy.Fits(row.BestBuy)
            && filter.BestSell.Fits(row.BestSell)
            && filter.MarginIsk.Fits(row.MarginIsk)
            && filter.MarginFrac.Fits(row.MarginFrac * 100m)
            && filter.DailyVolume.Fits(row.DailyVolume)
            && filter.DailyIsk.Fits(row.DailyIsk)
            && filter.Hours.Fits(row.Hours)
            && filter.NetMarginIsk.Fits(row.NetMarginIsk)
            && filter.BuyVolume.Fits(row.BuyVolume)
            && filter.SellVolume.Fits(row.SellVolume)
            && filter.BuyTouch.Fits(row.BuyTouch)
            && filter.SellTouch.Fits(row.SellTouch)
            && filter.Relists.Fits(row.Relists)
            && filter.Capital.Fits(row.Capital)
            && filter.Days.Fits(row.Days)
            && filter.DailyRoi.Fits(row.DailyRoi * 100m)
            && filter.Average.Fits(row.Average)
            && filter.BuyMoves.Fits(row.BuyMoves)
            && filter.SellMoves.Fits(row.SellMoves)
            && filter.MarginThen.Fits(row.MarginThen)
            && Hits(filter.Thin, row.Thin);
    }

    private static bool Hits(string? filter, string? value)
    {
        var needle = Fold(filter);
        return needle.Length == 0 || Fold(value).Contains(needle, StringComparison.Ordinal);
    }

    private static string Num(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Fold(string? value) =>
        (value ?? "").ToLower(CultureInfo.GetCultureInfo("ru-RU"))
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("\u00a0", "", StringComparison.Ordinal);

    /// <summary>
    /// Часы жизни вилки по стенам часов: от самого свежего до самого старого снимка
    /// непрерывной серии (разрыв &gt; <see cref="SignalRule.MaxHoursGap"/> или мёртвая вилка рвут). Не число волн.
    /// </summary>
    private static int Streak(List<Point>? points, FeeSettings fees)
    {
        if (points is null || points.Count == 0)
            return 0;
        DateTime? newest = null;
        DateTime? oldest = null;
        DateTime? prev = null;
        foreach (var point in points)
        {
            if (prev is not null && prev.Value - point.Time > SignalRule.MaxHoursGap)
                break;
            var (margin, _) = FeeMath.Margin(point.Buy, point.Sell, fees.SalesTax, fees.BrokerFee);
            if (margin <= 0 || point.BuyVolume < SignalRule.MinTouch || point.SellVolume < SignalRule.MinTouch)
                break;
            newest ??= point.Time;
            oldest = point.Time;
            prev = point.Time;
        }

        if (newest is null || oldest is null)
            return 0;
        return (int)Math.Floor((newest.Value - oldest.Value).TotalHours);
    }

    private static async Task<FeeSettings> ReadSettingsAsync(SqliteConnection conn)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM setting";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            map[reader.GetString(0)] = reader.GetString(1);
        var minSell = ParseDec(map, "min_sell_price", 1_000_000m);
        var minHaul = ParseDec(map, "min_haul_sell_price", 500_000m);
        return new FeeSettings(
            ParseInt(map, "accounting", 5),
            ParseInt(map, "broker", 5),
            ParseDec(map, "faction", 0),
            ParseDec(map, "corp", 0),
            Math.Clamp(ParseInt(map, "keep_days", 120), 1, 400),
            ParseBool(map, "filter_cosmetics", true),
            ParseBool(map, "filter_special_edition", true),
            ParseBool(map, "filter_blueprints", true),
            minSell < 0 ? 1_000_000m : minSell,
            minHaul < 0 ? 500_000m : minHaul);
    }

    private static bool ParseBool(Dictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
            return fallback;
        return text is "1" or "true" or "True" or "yes";
    }

    private async Task WithAsync(Func<SqliteConnection, Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await work(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            return await work(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ReadDec(SqliteDataReader reader, int ordinal) =>
        decimal.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static int ReadInt(SqliteDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long ReadLong(SqliteDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int ParseInt(Dictionary<string, string> map, string key, int fallback) =>
        map.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static decimal ParseDec(Dictionary<string, string> map, string key, decimal fallback) =>
        map.TryGetValue(key, out var text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private sealed record RawQuote(
        int TypeId,
        string Name,
        decimal? BestBuy,
        decimal BestSell,
        long BuyVolume,
        long SellVolume,
        int BuyOrders,
        int SellOrders,
        long BuyTouch,
        long SellTouch);

    private static string ResolveTypeName(Dictionary<int, string> names, int typeId)
    {
        if (names.TryGetValue(typeId, out var name)
            && !string.IsNullOrWhiteSpace(name)
            && !name.StartsWith("#", StringComparison.Ordinal))
            return name;
        return "#" + typeId.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        if (!await HasColumnAsync(conn, table, column, ct))
            await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var info = conn.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await info.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task MigrateHistoryRegionAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (await HasColumnAsync(conn, "daily_history", "region_id", ct)
            && await HasColumnAsync(conn, "history_sync", "region_id", ct))
            return;

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await HasColumnAsync(conn, "daily_history", "region_id", ct))
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    CREATE TABLE daily_history_v2 (
                        region_id INTEGER NOT NULL,
                        type_id INTEGER NOT NULL,
                        day TEXT NOT NULL,
                        volume INTEGER NOT NULL,
                        average TEXT NOT NULL,
                        highest TEXT NOT NULL,
                        lowest TEXT NOT NULL,
                        order_count INTEGER NOT NULL,
                        PRIMARY KEY (region_id, type_id, day)
                    );
                    INSERT INTO daily_history_v2 (region_id, type_id, day, volume, average, highest, lowest, order_count)
                    SELECT $forge, type_id, day, volume, average, highest, lowest, order_count FROM daily_history;
                    DROP TABLE daily_history;
                    ALTER TABLE daily_history_v2 RENAME TO daily_history;
                    """;
                cmd.Parameters.AddWithValue("$forge", TradeHubs.ForgeRegionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (!await HasColumnAsync(conn, "history_sync", "region_id", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                CREATE TABLE history_sync_v2 (
                    region_id INTEGER NOT NULL,
                    type_id INTEGER NOT NULL,
                    synced_on TEXT NOT NULL,
                    PRIMARY KEY (region_id, type_id)
                );
                INSERT INTO history_sync_v2 (region_id, type_id, synced_on)
                SELECT $forge, type_id, synced_on FROM history_sync;
                DROP TABLE history_sync;
                ALTER TABLE history_sync_v2 RENAME TO history_sync;
                """;
            cmd.Parameters.AddWithValue("$forge", TradeHubs.ForgeRegionId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static bool InSection(BoardRow row, string section) => section switch
    {
        "off" => row.Hours >= SignalRule.MinHours
            && row.DailyIsk >= SignalRule.MinDailyIsk
            && row.Average is decimal average
            && (average < row.BestBuy || average > row.BestSell),
        "churn" => row.BuyMoves >= SignalRule.MinMoves || row.SellMoves >= SignalRule.MinMoves,
        "squeeze" => row.MarginThen is not null,
        _ => row.Hours >= SignalRule.MinHours
            && row.DailyIsk >= SignalRule.MinDailyIsk
            && row.Average is decimal inside
            && inside >= row.BestBuy
            && inside <= row.BestSell
    };

    private static string EarlyBanner(string section, int have, int need) => section switch
    {
        "churn" => $"Рано смотреть дёрганье. Нужно минимум {need} часовых снимков, сейчас {have}.",
        "squeeze" => $"Рано смотреть сжатие. Нужно минимум {need} часовых снимков, сейчас {have}.",
        _ => $"Рано кричать. Нужно минимум {need} часовых снимков, сейчас {have}."
    };

    private static string SectionBanner(string section, bool shout, bool any) => section switch
    {
        "off" => any
            ? "Вчерашняя средняя цена вне спреда. Это не вход."
            : "Таких нет. Средняя вчера либо внутри спреда, либо её не было.",
        "churn" => any
            ? "Цену двигают каждый час. Перестановка съест маржу."
            : "Пока никого не дёргают три часа подряд.",
        "squeeze" => any
            ? "Маржа падает три часа подряд. Снимай ордера, не ставь."
            : "Сжатия нет. Маржа не падает три часа подряд.",
        _ => shout
            ? "Есть шанс заработать. Иди покупай."
            : "Пока тихо. Нет спреда, который держится, даёт объём и сидит вокруг вчерашней средней."
    };

    private static string ThinSide(long buy, long sell)
    {
        if (buy <= 0 || sell <= 0)
            return "";
        if (buy * 2 < sell)
            return "покупка";
        if (sell * 2 < buy)
            return "продажа";
        return "";
    }

    private static (int Buy, int Sell) Moves(List<Point>? points)
    {
        if (points is null || points.Count < 2)
            return (0, 0);
        var buy = 0;
        var sell = 0;
        var buyOpen = true;
        var sellOpen = true;
        var prev = points[0];
        var steps = 0;
        for (var i = 1; i < points.Count && steps < 5 && (buyOpen || sellOpen); i++)
        {
            var point = points[i];
            if (prev.Time - point.Time > TimeSpan.FromMinutes(90))
                break;
            steps++;
            if (buyOpen && point.Buy != prev.Buy)
                buy++;
            else
                buyOpen = false;
            if (sellOpen && point.Sell != prev.Sell)
                sell++;
            else
                sellOpen = false;
            prev = point;
        }

        return (buy, sell);
    }

    private static decimal? MarginBeforeDrop(List<Point>? points, FeeSettings fees)
    {
        if (points is null || points.Count < 4)
            return null;
        decimal newer = 0;
        var drops = 0;
        decimal then = 0;
        DateTime? prev = null;
        foreach (var point in points)
        {
            if (prev is not null && prev.Value - point.Time > TimeSpan.FromMinutes(90))
                break;
            var (margin, _) = FeeMath.Margin(point.Buy, point.Sell, fees.SalesTax, fees.BrokerFee);
            if (margin <= 0)
                break;
            if (prev is null)
            {
                newer = margin;
                prev = point.Time;
                continue;
            }

            if (margin <= newer)
                break;
            drops++;
            then = margin;
            newer = margin;
            prev = point.Time;
            if (drops >= 3)
                return then;
        }

        return null;
    }

    private sealed record Hist(string Day, long Volume, decimal Average);

    private readonly record struct Point(DateTime Time, decimal Buy, decimal Sell, long BuyVolume, long SellVolume);

    private sealed record LocBook(
        long LocationId,
        decimal? BestBuy,
        decimal BestSell,
        long BuyVolume,
        long SellVolume,
        long BuyTouch,
        long SellTouch);
}

public sealed record QuoteInsert(
    int TypeId,
    decimal? BestBuy,
    decimal BestSell,
    long BuyVolume,
    long SellVolume,
    int BuyOrders,
    int SellOrders,
    long BuyTouch,
    long SellTouch);

public sealed record LocationQuoteInsert(
    int TypeId,
    long LocationId,
    int RegionId,
    decimal? BestBuy,
    decimal BestSell,
    long BuyVolume,
    long SellVolume,
    int BuyOrders,
    int SellOrders,
    long BuyTouch,
    long SellTouch,
    DateTime? BestBuyIssued = null,
    DateTime? BestSellIssued = null);

public sealed record HistoryNeed(int RegionId, int TypeId);

public sealed record LocationMetaRow(
    long LocationId,
    string Name,
    int SystemId,
    string SystemName,
    int RegionId,
    string Hub);
