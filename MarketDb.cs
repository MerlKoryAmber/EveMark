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
    public int Hours { get; init; }
    public long BuyVolume { get; init; }
    public long SellVolume { get; init; }
    public int BuyOrders { get; init; }
    public int SellOrders { get; init; }
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
    string? Thin);

public sealed class MarketDb
{
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
                    type_id INTEGER NOT NULL,
                    day TEXT NOT NULL,
                    volume INTEGER NOT NULL,
                    average TEXT NOT NULL,
                    highest TEXT NOT NULL,
                    lowest TEXT NOT NULL,
                    order_count INTEGER NOT NULL,
                    PRIMARY KEY (type_id, day)
                );
                CREATE TABLE IF NOT EXISTS history_sync (
                    type_id INTEGER PRIMARY KEY,
                    synced_on TEXT NOT NULL
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
                CREATE INDEX IF NOT EXISTS ix_snapshot_taken ON snapshot(taken_at);
                CREATE INDEX IF NOT EXISTS ix_quote_type ON quote(type_id, snapshot_id);
                INSERT INTO status (id, state, detail, updated_at)
                VALUES (1, 'idle', '', $now)
                ON CONFLICT(id) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('accounting', '5') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('broker', '5') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('faction', '0') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('corp', '0') ON CONFLICT(key) DO NOTHING;
                INSERT INTO setting (key, value) VALUES ('keep_days', '120') ON CONFLICT(key) DO NOTHING;
                """, ("$now", Stamp(DateTime.UtcNow)));
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
            """,
            ("$a", settings.Accounting.ToString(CultureInfo.InvariantCulture)),
            ("$b", settings.BrokerRelations.ToString(CultureInfo.InvariantCulture)),
            ("$f", settings.FactionStanding.ToString(CultureInfo.InvariantCulture)),
            ("$c", settings.CorpStanding.ToString(CultureInfo.InvariantCulture)),
            ("$k", settings.KeepDays.ToString(CultureInfo.InvariantCulture))), ct);

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

    public Task<int> InsertSnapshotAsync(DateTime takenUtc, IReadOnlyList<QuoteInsert> rows, CancellationToken ct = default) =>
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
                INSERT INTO quote (snapshot_id, type_id, best_buy, best_sell, buy_volume, sell_volume, buy_orders, sell_orders)
                VALUES ($sid, $type, $bb, $bs, $bv, $sv, $bo, $so)
                """;
            var pSid = quote.Parameters.Add("$sid", SqliteType.Integer);
            var pType = quote.Parameters.Add("$type", SqliteType.Integer);
            var pBb = quote.Parameters.Add("$bb", SqliteType.Text);
            var pBs = quote.Parameters.Add("$bs", SqliteType.Text);
            var pBv = quote.Parameters.Add("$bv", SqliteType.Integer);
            var pSv = quote.Parameters.Add("$sv", SqliteType.Integer);
            var pBo = quote.Parameters.Add("$bo", SqliteType.Integer);
            var pSo = quote.Parameters.Add("$so", SqliteType.Integer);
            pSid.Value = id;
            foreach (var row in rows)
            {
                pType.Value = row.TypeId;
                pBb.Value = row.BestBuy is null ? DBNull.Value : Dec(row.BestBuy.Value);
                pBs.Value = Dec(row.BestSell);
                pBv.Value = row.BuyVolume;
                pSv.Value = row.SellVolume;
                pBo.Value = row.BuyOrders;
                pSo.Value = row.SellOrders;
                await quote.ExecuteNonQueryAsync(ct);
            }

            await using var touch = conn.CreateCommand();
            touch.Transaction = (SqliteTransaction)tx;
            touch.CommandText = """
                UPDATE status SET last_snapshot_at = $t, last_error = NULL, updated_at = $t WHERE id = 1
                """;
            touch.Parameters.AddWithValue("$t", Stamp(takenUtc));
            await touch.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return rows.Count;
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

    public Task<IReadOnlyList<int>> TypesNeedingHistoryAsync(string todayUtc, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT q.type_id
                FROM quote q
                LEFT JOIN history_sync h ON h.type_id = q.type_id
                WHERE q.snapshot_id = (SELECT id FROM snapshot ORDER BY taken_at DESC LIMIT 1)
                  AND q.type_id NOT IN (SELECT type_id FROM reject)
                  AND (h.synced_on IS NULL OR h.synced_on < $today)
                ORDER BY (CAST(q.best_sell AS REAL) - IFNULL(CAST(q.best_buy AS REAL), 0)) DESC
                """;
            cmd.Parameters.AddWithValue("$today", todayUtc);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            return (IReadOnlyList<int>)list;
        }, ct);

    public Task UpsertHistoryAsync(int typeId, IReadOnlyList<HistoryBar> bars, string todayUtc, int keepDays, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var cut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-keepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO daily_history (type_id, day, volume, average, highest, lowest, order_count)
                VALUES ($id, $day, $vol, $avg, $hi, $lo, $cnt)
                ON CONFLICT(type_id, day) DO UPDATE SET
                    volume = excluded.volume,
                    average = excluded.average,
                    highest = excluded.highest,
                    lowest = excluded.lowest,
                    order_count = excluded.order_count
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pDay = cmd.Parameters.Add("$day", SqliteType.Text);
            var pVol = cmd.Parameters.Add("$vol", SqliteType.Integer);
            var pAvg = cmd.Parameters.Add("$avg", SqliteType.Text);
            var pHi = cmd.Parameters.Add("$hi", SqliteType.Text);
            var pLo = cmd.Parameters.Add("$lo", SqliteType.Text);
            var pCnt = cmd.Parameters.Add("$cnt", SqliteType.Integer);
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
                INSERT INTO history_sync (type_id, synced_on) VALUES ($id, $today)
                ON CONFLICT(type_id) DO UPDATE SET synced_on = excluded.synced_on
                """;
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
                .Where(row => row.MarginFrac >= SignalRule.MinMarginFrac
                    && row.MarginFrac <= SignalRule.MaxMarginFrac
                    && row.DailyVolume >= SignalRule.MinDailyVolume)
                .ToList();
            await FillHoursAsync(conn, candidates, load.Fees, ct);
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
                Banner = SectionBanner(section, shout, signals.Count > 0),
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

    public Task<BoardDto> GetBoardAsync(int page, string? sort, string? dir, ColumnFilter filter, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var load = await LoadRankedAsync(conn, ct);
            var fees = load.Fees;
            var ranked = load.Rows;
            if (load.TakenAt is null)
            {
                return new BoardDto { SalesTax = fees.SalesTax, BrokerFee = fees.BrokerFee };
            }

            var takenAt = load.TakenAt;
            var rawCount = load.Tracked;

            var filtered = ranked.Where(row => MatchesColumns(row, filter)).ToList();
            var sortKey = SortKey(sort);
            var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
            if (sortKey == "hours")
                await FillHoursAsync(conn, filtered, fees, ct);
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
            if (sortKey != "hours")
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

    private async Task<RankedLoad> LoadRankedAsync(SqliteConnection conn, CancellationToken ct)
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

        var raw = new List<RawQuote>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT q.type_id, IFNULL(n.name, '#' || q.type_id), q.best_buy, q.best_sell,
                       q.buy_volume, q.sell_volume, q.buy_orders, q.sell_orders
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
                    ReadInt(reader, 7)));
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var history = new Dictionary<int, Hist>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT h.type_id, h.day, h.volume, h.average
                FROM daily_history h
                JOIN (
                    SELECT type_id, MAX(day) AS day
                    FROM daily_history
                    WHERE day < $today
                    GROUP BY type_id
                ) m ON m.type_id = h.type_id AND m.day = h.day
                """;
            cmd.Parameters.AddWithValue("$today", today);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                history[ReadInt(reader, 0)] = new Hist(reader.GetString(1), ReadLong(reader, 2), ReadDec(reader, 3));
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
            var tradedYesterday = hist is not null && hist.Day == yesterday;
            var depth = tradedYesterday
                ? Math.Min(hist!.Volume, Math.Min(quote.BuyVolume, quote.SellVolume))
                : (long?)null;
            ranked.Add(new BoardRow
            {
                TypeId = quote.TypeId,
                Name = quote.Name,
                BestBuy = buy,
                BestSell = quote.BestSell,
                MarginIsk = margin,
                MarginFrac = fraction,
                DailyVolume = tradedYesterday ? hist!.Volume : null,
                VolumeDay = tradedYesterday ? hist!.Day : null,
                Average = tradedYesterday ? hist!.Average : null,
                DailyIsk = depth is null ? null : margin * depth.Value,
                Hours = 0,
                BuyVolume = quote.BuyVolume,
                SellVolume = quote.SellVolume,
                BuyOrders = quote.BuyOrders,
                SellOrders = quote.SellOrders
            });
        }

        return new RankedLoad(fees, takenAt, raw.Count, snapshots, ranked);
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
            _ => null
        };
        if (missing is null)
            return 0;
        var other = sortKey switch
        {
            "dailyVolume" => b.DailyVolume is null,
            "average" => b.Average is null,
            "marginThen" => b.MarginThen is null,
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
            or "average" or "buyMoves" or "sellMoves" or "marginThen" or "thin" => sort,
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
            && filter.BuyVolume.Fits(row.BuyVolume)
            && filter.SellVolume.Fits(row.SellVolume)
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

    private static int Streak(List<Point>? points, FeeSettings fees)
    {
        if (points is null || points.Count == 0)
            return 0;
        DateTime? prev = null;
        var hours = 0;
        foreach (var point in points)
        {
            if (prev is not null && prev.Value - point.Time > TimeSpan.FromMinutes(90))
                break;
            var (margin, _) = FeeMath.Margin(point.Buy, point.Sell, fees.SalesTax, fees.BrokerFee);
            if (margin <= 0 || point.BuyVolume < SignalRule.MinTouch || point.SellVolume < SignalRule.MinTouch)
                break;
            hours++;
            prev = point.Time;
        }

        return hours;
    }

    private static async Task<FeeSettings> ReadSettingsAsync(SqliteConnection conn)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM setting";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            map[reader.GetString(0)] = reader.GetString(1);
        return new FeeSettings(
            ParseInt(map, "accounting", 5),
            ParseInt(map, "broker", 5),
            ParseDec(map, "faction", 0),
            ParseDec(map, "corp", 0),
            Math.Clamp(ParseInt(map, "keep_days", 120), 1, 400));
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
        int SellOrders);

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
}

public sealed record QuoteInsert(
    int TypeId,
    decimal? BestBuy,
    decimal BestSell,
    long BuyVolume,
    long SellVolume,
    int BuyOrders,
    int SellOrders);
