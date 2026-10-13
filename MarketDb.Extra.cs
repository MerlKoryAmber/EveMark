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
                Math.Max(0, ParseInt(map, "pf_max_relists_day", 12)),
                ParseBool(map, "pf_source_jita", true),
                ParseBool(map, "pf_source_perimeter", true),
                exclude);
        }, ct);

    public Task SavePortfolioSettingsAsync(PortfolioSettings s, CancellationToken ct = default) =>
        WithAsync(async conn =>
        {
            var exclude = string.Join(",", (s.Exclude ?? []).Distinct().OrderBy(x => x));
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
            await UpsertSettingAsync(conn, "pf_max_relists_day", s.MaxRelistsDay.ToString(CultureInfo.InvariantCulture));
            await UpsertSettingAsync(conn, "pf_source_jita", s.SourceJita ? "1" : "0");
            await UpsertSettingAsync(conn, "pf_source_perimeter", s.SourcePerimeter ? "1" : "0");
            await UpsertSettingAsync(conn, "pf_exclude", exclude);
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
