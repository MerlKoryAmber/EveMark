using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JitaWatch;

public sealed record WatchItem(
    int TypeId,
    long LocationId,
    int RegionId,
    string LocationName,
    string Name,
    string Note,
    string CreatedAt);

public sealed record WatchOrderSnap(
    long OrderId,
    int TypeId,
    long LocationId,
    bool IsBuy,
    decimal Price,
    long VolumeRemain);

public sealed partial class MarketDb
{
    public Task<IReadOnlyList<WatchItem>> ListWatchAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var list = new List<WatchItem>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT w.type_id, w.location_id, w.region_id, w.note, w.created_at,
                       IFNULL(n.name, '#' || w.type_id),
                       IFNULL(m.name, CAST(w.location_id AS TEXT))
                FROM watchlist w
                LEFT JOIN type_name n ON n.type_id = w.type_id
                LEFT JOIN location_meta m ON m.location_id = w.location_id
                ORDER BY n.name COLLATE NOCASE, w.type_id, w.location_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new WatchItem(
                    ReadInt(reader, 0),
                    reader.GetInt64(1),
                    ReadInt(reader, 2),
                    reader.GetString(6),
                    reader.GetString(5),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.GetString(4)));
            }

            return (IReadOnlyList<WatchItem>)list;
        }, ct);

    public Task AddWatchAsync(int typeId, long locationId, int regionId, string? note, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO watchlist (type_id, location_id, region_id, note, created_at)
            VALUES ($t, $l, $r, $n, $at)
            ON CONFLICT(type_id, location_id) DO UPDATE SET
                region_id = excluded.region_id,
                note = excluded.note
            """,
            ("$t", typeId),
            ("$l", locationId),
            ("$r", regionId),
            ("$n", note ?? ""),
            ("$at", Stamp(DateTime.UtcNow))), ct);

    public Task RemoveWatchAsync(int typeId, long locationId, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            DELETE FROM watchlist WHERE type_id = $t AND location_id = $l;
            DELETE FROM watch_order WHERE type_id = $t AND location_id = $l;
            """,
            ("$t", typeId),
            ("$l", locationId)), ct);

    public Task<HashSet<(int TypeId, long LocationId)>> WatchKeysAsync(CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var set = new HashSet<(int, long)>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type_id, location_id FROM watchlist";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                set.Add((ReadInt(reader, 0), reader.GetInt64(1)));
            return set;
        }, ct);

    public Task<string?> GetEtagAsync(string cacheKey, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT etag FROM esi_etag WHERE cache_key = $k";
            cmd.Parameters.AddWithValue("$k", cacheKey);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }, ct);

    public Task SetEtagAsync(string cacheKey, string etag, CancellationToken ct = default) =>
        WithAsync(conn => ExecAsync(conn, """
            INSERT INTO esi_etag (cache_key, etag, updated_at) VALUES ($k, $e, $at)
            ON CONFLICT(cache_key) DO UPDATE SET etag = excluded.etag, updated_at = excluded.updated_at
            """,
            ("$k", cacheKey),
            ("$e", etag),
            ("$at", Stamp(DateTime.UtcNow))), ct);

    public Task ApplyWatchBookAsync(
        int typeId,
        long locationId,
        IReadOnlyList<WatchOrderSnap> orders,
        CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var now = Stamp(DateTime.UtcNow);
            var prev = new Dictionary<long, (bool IsBuy, decimal Price, long Vol)>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT order_id, is_buy, price, volume_remain
                    FROM watch_order WHERE type_id = $t AND location_id = $l
                    """;
                cmd.Parameters.AddWithValue("$t", typeId);
                cmd.Parameters.AddWithValue("$l", locationId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    prev[reader.GetInt64(0)] = (
                        reader.GetInt64(1) != 0,
                        ReadDec(reader, 2),
                        ReadLong(reader, 3));
                }
            }

            decimal? oldBestBuy = null, oldBestSell = null;
            long oldBuyOrder = 0, oldSellOrder = 0;
            foreach (var (id, row) in prev)
            {
                if (row.IsBuy)
                {
                    if (oldBestBuy is null || row.Price > oldBestBuy)
                    {
                        oldBestBuy = row.Price;
                        oldBuyOrder = id;
                    }
                }
                else if (oldBestSell is null || row.Price < oldBestSell)
                {
                    oldBestSell = row.Price;
                    oldSellOrder = id;
                }
            }

            decimal? newBestBuy = null, newBestSell = null;
            long newBuyOrder = 0, newSellOrder = 0;
            foreach (var o in orders)
            {
                if (o.IsBuy)
                {
                    if (newBestBuy is null || o.Price > newBestBuy)
                    {
                        newBestBuy = o.Price;
                        newBuyOrder = o.OrderId;
                    }
                }
                else if (newBestSell is null || o.Price < newBestSell)
                {
                    newBestSell = o.Price;
                    newSellOrder = o.OrderId;
                }
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var o in orders)
            {
                if (prev.TryGetValue(o.OrderId, out var before)
                    && before.Price == o.Price
                    && o.VolumeRemain < before.Vol)
                {
                    var filled = before.Vol - o.VolumeRemain;
                    if (filled > 0)
                    {
                        await using var fill = conn.CreateCommand();
                        fill.Transaction = (SqliteTransaction)tx;
                        fill.CommandText = """
                            INSERT INTO watch_fill (order_id, type_id, location_id, is_buy, price, volume, filled_at)
                            VALUES ($o, $t, $l, $b, $p, $v, $at)
                            """;
                        fill.Parameters.AddWithValue("$o", o.OrderId);
                        fill.Parameters.AddWithValue("$t", typeId);
                        fill.Parameters.AddWithValue("$l", locationId);
                        fill.Parameters.AddWithValue("$b", o.IsBuy ? 1 : 0);
                        fill.Parameters.AddWithValue("$p", Dec(o.Price));
                        fill.Parameters.AddWithValue("$v", filled);
                        fill.Parameters.AddWithValue("$at", now);
                        await fill.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            if (newBestBuy is decimal nb && (oldBestBuy is null || nb != oldBestBuy) && newBuyOrder > 0)
            {
                await using var mv = conn.CreateCommand();
                mv.Transaction = (SqliteTransaction)tx;
                mv.CommandText = """
                    INSERT INTO price_mover (type_id, location_id, side, order_id, price, seen_at)
                    VALUES ($t, $l, 'buy', $o, $p, $at)
                    """;
                mv.Parameters.AddWithValue("$t", typeId);
                mv.Parameters.AddWithValue("$l", locationId);
                mv.Parameters.AddWithValue("$o", newBuyOrder);
                mv.Parameters.AddWithValue("$p", Dec(nb));
                mv.Parameters.AddWithValue("$at", now);
                await mv.ExecuteNonQueryAsync(ct);
            }

            if (newBestSell is decimal ns && (oldBestSell is null || ns != oldBestSell) && newSellOrder > 0)
            {
                await using var mv = conn.CreateCommand();
                mv.Transaction = (SqliteTransaction)tx;
                mv.CommandText = """
                    INSERT INTO price_mover (type_id, location_id, side, order_id, price, seen_at)
                    VALUES ($t, $l, 'sell', $o, $p, $at)
                    """;
                mv.Parameters.AddWithValue("$t", typeId);
                mv.Parameters.AddWithValue("$l", locationId);
                mv.Parameters.AddWithValue("$o", newSellOrder);
                mv.Parameters.AddWithValue("$p", Dec(ns));
                mv.Parameters.AddWithValue("$at", now);
                await mv.ExecuteNonQueryAsync(ct);
            }

            await using (var del = conn.CreateCommand())
            {
                del.Transaction = (SqliteTransaction)tx;
                del.CommandText = "DELETE FROM watch_order WHERE type_id = $t AND location_id = $l";
                del.Parameters.AddWithValue("$t", typeId);
                del.Parameters.AddWithValue("$l", locationId);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var o in orders)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = """
                    INSERT INTO watch_order (order_id, type_id, location_id, is_buy, price, volume_remain, seen_at)
                    VALUES ($o, $t, $l, $b, $p, $v, $at)
                    """;
                ins.Parameters.AddWithValue("$o", o.OrderId);
                ins.Parameters.AddWithValue("$t", typeId);
                ins.Parameters.AddWithValue("$l", locationId);
                ins.Parameters.AddWithValue("$b", o.IsBuy ? 1 : 0);
                ins.Parameters.AddWithValue("$p", Dec(o.Price));
                ins.Parameters.AddWithValue("$v", o.VolumeRemain);
                ins.Parameters.AddWithValue("$at", now);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }, ct);

    public Task<Dictionary<int, (long Buy, long Sell)>> StationFills24hAsync(
        IReadOnlyList<long>? locationFilter,
        CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var since = Stamp(DateTime.UtcNow.AddHours(-24));
            var map = new Dictionary<int, (long Buy, long Sell)>();
            await using var cmd = conn.CreateCommand();
            if (locationFilter is { Count: > 0 })
            {
                var ps = string.Join(",", locationFilter.Select((_, i) => "$l" + i.ToString(CultureInfo.InvariantCulture)));
                cmd.CommandText = $"""
                    SELECT type_id, is_buy, SUM(volume)
                    FROM watch_fill
                    WHERE filled_at >= $since AND location_id IN ({ps})
                    GROUP BY type_id, is_buy
                    """;
                for (var i = 0; i < locationFilter.Count; i++)
                    cmd.Parameters.AddWithValue("$l" + i.ToString(CultureInfo.InvariantCulture), locationFilter[i]);
            }
            else
            {
                cmd.CommandText = """
                    SELECT type_id, is_buy, SUM(volume)
                    FROM watch_fill
                    WHERE filled_at >= $since
                    GROUP BY type_id, is_buy
                    """;
            }

            cmd.Parameters.AddWithValue("$since", since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var typeId = ReadInt(reader, 0);
                var isBuy = reader.GetInt64(1) != 0;
                var vol = ReadLong(reader, 2);
                map.TryGetValue(typeId, out var cur);
                map[typeId] = isBuy ? (cur.Buy + vol, cur.Sell) : (cur.Buy, cur.Sell + vol);
            }

            return map;
        }, ct);

    public Task<Dictionary<int, int>> CompetitorsAsync(int hours, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var since = Stamp(DateTime.UtcNow.AddHours(-Math.Max(1, hours)));
            var map = new Dictionary<int, int>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT type_id, COUNT(DISTINCT order_id)
                FROM price_mover
                WHERE seen_at >= $since AND order_id > 0
                GROUP BY type_id
                """;
            cmd.Parameters.AddWithValue("$since", since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                map[ReadInt(reader, 0)] = ReadInt(reader, 1);
            return map;
        }, ct);

    public Task PurgeWatchNoiseAsync(int keepDays, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var cut = Stamp(DateTime.UtcNow.AddDays(-Math.Clamp(keepDays, 1, 400)));
            await ExecAsync(conn, """
                DELETE FROM watch_fill WHERE filled_at < $cut;
                DELETE FROM price_mover WHERE seen_at < $cut;
                """,
                ("$cut", cut));
        }, ct);
}
