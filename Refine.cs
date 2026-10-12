namespace JitaWatch;

/// <summary>
/// Минимальный рефайн без полного SDE: несколько распространённых руд → минералы.
/// Выход упрощённый (база × эффективность); не замена in-game калькулятора.
/// </summary>
public static class RefineTable
{
    // type_id руды → (минерал type_id, units на 100 руды при 100% базе)
    private static readonly Dictionary<int, (int MineralId, decimal Per100)[]> Recipes = new()
    {
        [18] = [(34, 415), (35, 0)], // Veldspar → Tritanium (упрощ.)
        [19] = [(34, 212), (35, 121)], // Scordite
        [20] = [(34, 134), (35, 267), (36, 134)], // Pyroxeres
        [21] = [(34, 800), (36, 100), (37, 150)], // Plagioclase approx
        [22] = [(34, 350), (35, 250), (36, 50), (37, 5)], // Omber approx
        [1223] = [(34, 1000), (35, 200), (36, 100), (37, 50), (38, 5)], // Pyroxeres compressed-ish placeholder
    };

    private static readonly Dictionary<int, string> MineralNames = new()
    {
        [34] = "Tritanium",
        [35] = "Pyerite",
        [36] = "Mexallon",
        [37] = "Isogen",
        [38] = "Nocxium",
        [39] = "Zydrine",
        [40] = "Megacyte"
    };

    public static RefineDto? Evaluate(int oreTypeId, long units, decimal efficiency, IReadOnlyDictionary<int, decimal> sellPrices)
    {
        if (!Recipes.TryGetValue(oreTypeId, out var outs) || units < 1)
            return null;
        var eff = Math.Clamp(efficiency, 0.01m, 1m);
        var lines = new List<RefineLine>();
        var value = 0m;
        foreach (var (mid, per100) in outs)
        {
            if (per100 <= 0)
                continue;
            var qty = decimal.Floor(units / 100m * per100 * eff);
            if (qty < 1)
                continue;
            sellPrices.TryGetValue(mid, out var px);
            var line = qty * px;
            value += line;
            lines.Add(new RefineLine(mid, MineralNames.GetValueOrDefault(mid, "#" + mid), (long)qty, px, line));
        }

        sellPrices.TryGetValue(oreTypeId, out var oreSell);
        var oreCost = oreSell * units;
        return new RefineDto(oreTypeId, units, eff, oreCost, value, value - oreCost, lines);
    }

    public static IReadOnlyList<int> KnownOreIds => Recipes.Keys.ToList();
}

public sealed record RefineLine(int TypeId, string Name, long Qty, decimal Sell, decimal Value);

public sealed record RefineDto(
    int OreTypeId,
    long Units,
    decimal Efficiency,
    decimal OreMarketValue,
    decimal OutputValue,
    decimal EdgeIsk,
    IReadOnlyList<RefineLine> Lines);

public sealed record RefineRequest(int OreTypeId, long Units, decimal Efficiency = 0.52m);
