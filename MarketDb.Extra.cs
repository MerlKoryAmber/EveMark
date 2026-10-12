using System.Globalization;
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

    public Task<IReadOnlyList<BoardRow>> LoadBoardRowsAsync(BoardSources sources, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var load = await LoadRankedAsync(conn, sources, ct);
            var rows = load.Rows
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
}
