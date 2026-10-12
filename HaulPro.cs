using System.Globalization;

namespace JitaWatch;

public static class GankSystems
{
    // Известные хайсек-ганк коридоры (system_id).
    public static readonly HashSet<int> Ids = new()
    {
        30000139, // Uedama
        30000140, // Sivala
        30003792, // Ahbazon (Genesis) — уточнять по SDE при необходимости
        30002787, // Niarja
    };

    public static readonly Dictionary<int, string> Names = new()
    {
        [30000139] = "Uedama",
        [30000140] = "Sivala",
        [30003792] = "Ahbazon",
        [30002787] = "Niarja"
    };

    public static IReadOnlyList<string> OnRoute(IReadOnlyList<int> systems) =>
        systems.Where(Ids.Contains).Where(id => Names.ContainsKey(id)).Select(id => Names[id]).Distinct().ToList();
}

public static class CourierRates
{
    // Упрощённые оценки для UI (не официальный прайс курьеров).
    public const decimal RedFrogPerM3 = 350m;
    public const decimal RedFrogCollateralPct = 0.01m;
    public const decimal PushXPerM3 = 400m;
    public const decimal PushXCollateralPct = 0.01m;
    public const decimal Floor = 1_000_000m;

    public static decimal Estimate(decimal m3, decimal collateral, decimal perM3, decimal collPct) =>
        Math.Max(Floor, perM3 * m3) + collateral * collPct;
}

public sealed record HaulPackRequest(
    decimal CargoM3,
    decimal Budget,
    decimal CollateralBudget,
    bool HighSecOnly,
    int MaxItems = 40);

public sealed record HaulPackItem(
    int TypeId,
    string Name,
    string FromName,
    string ToName,
    long Units,
    decimal Ask,
    decimal MarginIsk,
    decimal VolumeM3,
    decimal CargoM3,
    decimal Collateral,
    int Jumps,
    IReadOnlyList<string> GankSystems,
    decimal RedFrogIsk,
    decimal PushXIsk,
    decimal? DaysToSell);

public sealed record HaulPackDto(
    decimal CargoUsed,
    decimal BudgetUsed,
    decimal DailyMargin,
    IReadOnlyList<HaulPackItem> Items,
    string Multibuy,
    string Csv);

public static class HaulPacker
{
    public static HaulPackDto Pack(IReadOnlyList<HaulRow> rows, HaulPackRequest req)
    {
        var cargo = Math.Max(0, req.CargoM3);
        var budget = Math.Max(0, req.Budget);
        var collCap = req.CollateralBudget > 0 ? req.CollateralBudget : budget * 2;
        var picks = new List<HaulPackItem>();
        var usedM3 = 0m;
        var usedIsk = 0m;
        var daily = 0m;

        var ordered = rows
            .Where(r => r.MarginIsk > 0 && r.VolumeM3 is > 0 && r.Units > 0)
            .Select(r =>
            {
                var m3 = r.VolumeM3!.Value;
                var score = m3 > 0 ? r.MarginIsk / m3 : 0;
                return (Row: r, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        foreach (var (row, _) in ordered)
        {
            if (picks.Count >= req.MaxItems)
                break;
            var m3Each = row.VolumeM3!.Value;
            if (m3Each <= 0)
                continue;
            var maxByCargo = (long)Math.Floor((cargo - usedM3) / m3Each);
            var maxByBudget = row.Ask > 0 ? (long)Math.Floor((budget - usedIsk) / row.Ask) : 0;
            var units = Math.Min(row.Units, Math.Min(maxByCargo, maxByBudget));
            if (units < 1)
                continue;
            var cargoLine = m3Each * units;
            var cost = row.Ask * units;
            var collateral = cost;
            if (usedIsk + cost > budget)
                continue;
            if (collateral > collCap)
            {
                units = (long)Math.Floor(collCap / row.Ask);
                if (units < 1)
                    continue;
                cargoLine = m3Each * units;
                cost = row.Ask * units;
                collateral = cost;
            }

            decimal? days = row.DailyVolume is > 0 ? units / (decimal)row.DailyVolume.Value : null;
            var rf = CourierRates.Estimate(cargoLine, collateral, CourierRates.RedFrogPerM3, CourierRates.RedFrogCollateralPct);
            var px = CourierRates.Estimate(cargoLine, collateral, CourierRates.PushXPerM3, CourierRates.PushXCollateralPct);
            picks.Add(new HaulPackItem(
                row.TypeId, row.Name, row.FromName, row.ToName, units, row.Ask, row.MarginIsk * units,
                m3Each, cargoLine, collateral, row.Jumps, [], rf, px, days));
            usedM3 += cargoLine;
            usedIsk += cost;
            daily += row.MarginIsk * units;
        }

        var multibuy = string.Join("\n", picks.Select(p => $"{p.Name} {p.Units.ToString(CultureInfo.InvariantCulture)}"));
        var csv = "typeId,name,from,to,units,ask,marginIsk,cargoM3,jumps,redFrog,pushX\n"
            + string.Join("\n", picks.Select(p =>
                string.Join(",",
                    p.TypeId.ToString(CultureInfo.InvariantCulture),
                    EscapeCsv(p.Name),
                    EscapeCsv(p.FromName),
                    EscapeCsv(p.ToName),
                    p.Units.ToString(CultureInfo.InvariantCulture),
                    p.Ask.ToString(CultureInfo.InvariantCulture),
                    p.MarginIsk.ToString(CultureInfo.InvariantCulture),
                    p.CargoM3.ToString(CultureInfo.InvariantCulture),
                    p.Jumps.ToString(CultureInfo.InvariantCulture),
                    p.RedFrogIsk.ToString(CultureInfo.InvariantCulture),
                    p.PushXIsk.ToString(CultureInfo.InvariantCulture))));
        return new HaulPackDto(usedM3, usedIsk, daily, picks, multibuy, csv);
    }

    private static string EscapeCsv(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
