using System.Globalization;
using System.Text.Json.Serialization;

namespace JitaWatch;

public sealed record PortfolioSettings(
    long Capital = 1_000_000_000,
    string SlotsMode = "manual",
    int Slots = 20,
    int SkillTrade = 0,
    int SkillRetail = 0,
    int SkillWholesale = 0,
    int SkillTycoon = 0,
    int SlotsUsed = 0,
    decimal HorizonDays = 2m,
    decimal MaxShareItem = 0.20m,
    decimal MaxMarketShare = 0.30m,
    long MinProfitDay = 1_000_000,
    int MinHours = 6,
    int MaxRelistsDay = 12,
    bool SourceJita = true,
    bool SourcePerimeter = true,
    IReadOnlyList<int>? Exclude = null)
{
    public int FreeSlots
    {
        get
        {
            if (string.Equals(SlotsMode, "skills", StringComparison.OrdinalIgnoreCase))
            {
                var total = FeeMath.MarketSlots(SkillTrade, SkillRetail, SkillWholesale, SkillTycoon);
                return Math.Max(0, total - Math.Max(0, SlotsUsed));
            }

            return Math.Max(0, Slots);
        }
    }

    public int SkillsTotal => FeeMath.MarketSlots(SkillTrade, SkillRetail, SkillWholesale, SkillTycoon);
}

public sealed record PortfolioExcludeRequest(int TypeId, bool Excluded);

public sealed record PortfolioCandidate(
    int TypeId,
    string Name,
    string Location,
    decimal Buy,
    decimal Sell,
    decimal Margin,
    decimal MarginFrac,
    long VolumeDay,
    int Hours,
    int BuyOrders,
    int SellOrders,
    int BuyMoves,
    int SellMoves,
    decimal CapMax,
    decimal ProfitMax,
    decimal Rate,
    long QtyMax,
    decimal UnitsDay,
    decimal Share,
    int Competitors,
    decimal RelistsDay,
    decimal RelistCostDay);

public sealed record PortfolioRow(
    [property: JsonPropertyName("type_id")] int TypeId,
    string Name,
    string Location,
    decimal Buy,
    decimal Sell,
    [property: JsonPropertyName("buy_post")] decimal BuyPost,
    [property: JsonPropertyName("sell_post")] decimal SellPost,
    decimal Margin,
    [property: JsonPropertyName("margin_pct")] decimal MarginPct,
    long Qty,
    decimal Capital,
    [property: JsonPropertyName("volume_day")] long VolumeDay,
    int Competitors,
    [property: JsonPropertyName("share_pct")] decimal SharePct,
    [property: JsonPropertyName("units_day")] decimal UnitsDay,
    [property: JsonPropertyName("relists_day")] decimal RelistsDay,
    [property: JsonPropertyName("relist_cost_day")] decimal RelistCostDay,
    [property: JsonPropertyName("profit_day")] decimal ProfitDay,
    [property: JsonPropertyName("roi_day_pct")] decimal RoiDayPct,
    [property: JsonPropertyName("turnover_days")] decimal TurnoverDays,
    int Hours,
    bool Partial);

public sealed record PortfolioTotals(
    int Positions,
    [property: JsonPropertyName("slots_used")] int SlotsUsed,
    [property: JsonPropertyName("capital_used")] decimal CapitalUsed,
    [property: JsonPropertyName("capital_left")] decimal CapitalLeft,
    [property: JsonPropertyName("profit_day")] decimal ProfitDay,
    [property: JsonPropertyName("roi_day_pct")] decimal RoiDayPct,
    [property: JsonPropertyName("avg_turnover_days")] decimal AvgTurnoverDays);

public sealed record PortfolioRejected(
    [property: JsonPropertyName("no_margin")] int NoMargin,
    [property: JsonPropertyName("low_hours")] int LowHours,
    [property: JsonPropertyName("low_profit")] int LowProfit,
    int Excluded,
    int Dedup);

public sealed record PortfolioInputs(long Capital, int Slots, [property: JsonPropertyName("horizon_days")] decimal HorizonDays);

public sealed record PortfolioResponse(
    [property: JsonPropertyName("snapshot_time")] string? SnapshotTime,
    PortfolioInputs Inputs,
    PortfolioTotals Totals,
    IReadOnlyList<PortfolioRow> Rows,
    [property: JsonPropertyName("rejected_count")] PortfolioRejected RejectedCount,
    PortfolioSettings Settings);

/// <summary>Кандидат оптимизатора с уже посчитанными profit_max / cap_max (для тестов и продакшена).</summary>
public sealed class OptCand
{
    public int TypeId { get; init; }
    public decimal ProfitMax { get; set; }
    public decimal CapMax { get; set; }
    public decimal Rate => CapMax > 0 ? ProfitMax / CapMax : 0;
    public decimal Cap { get; set; }
    public PortfolioCandidate? Source { get; init; }
}

public static class PortfolioBuilder
{
    public static PortfolioResponse Build(
        PortfolioSettings settings,
        IReadOnlyList<(BoardRow Row, string Location)> sourced,
        decimal brokerFee,
        string? snapshotTime)
    {
        var exclude = new HashSet<int>(settings.Exclude ?? []);
        var rejected = new PortfolioRejected(0, 0, 0, 0, 0);
        var built = new List<PortfolioCandidate>();
        var noMargin = 0;
        var lowHours = 0;
        var lowProfit = 0;
        var excluded = 0;

        foreach (var (row, loc) in sourced)
        {
            if (exclude.Contains(row.TypeId))
            {
                excluded++;
                continue;
            }

            if (row.BestBuy <= 0 || row.BestSell <= 0 || row.MarginIsk <= 0
                || row.BuyVolume < 1 || row.SellVolume < 1)
            {
                noMargin++;
                continue;
            }

            if (row.MarginFrac * 100m > 5000m)
            {
                noMargin++;
                continue;
            }

            var v = row.VolumeMedian7 ?? row.DailyVolume;
            if (v is not > 0)
            {
                noMargin++;
                continue;
            }

            if (row.Hours < settings.MinHours)
            {
                lowHours++;
                continue;
            }

            var cand = BuildCandidate(row, loc, settings, brokerFee, v.Value);
            if (cand is null)
            {
                lowProfit++;
                continue;
            }

            if (cand.ProfitMax < settings.MinProfitDay)
            {
                lowProfit++;
                continue;
            }

            built.Add(cand);
        }

        // дедуп type_id: оставляем больший profit_max
        var dedupDropped = 0;
        var byType = new Dictionary<int, PortfolioCandidate>();
        foreach (var c in built.OrderBy(x => x.TypeId).ThenByDescending(x => x.ProfitMax))
        {
            if (byType.TryGetValue(c.TypeId, out var prev))
            {
                dedupDropped++;
                if (c.ProfitMax > prev.ProfitMax)
                    byType[c.TypeId] = c;
            }
            else
                byType[c.TypeId] = c;
        }

        rejected = new PortfolioRejected(noMargin, lowHours, lowProfit, excluded, dedupDropped);
        var candidates = byType.Values
            .Select(c => new OptCand
            {
                TypeId = c.TypeId,
                ProfitMax = c.ProfitMax,
                CapMax = c.CapMax,
                Source = c
            })
            .ToList();

        var capital = (decimal)Math.Max(0, settings.Capital);
        var slots = settings.FreeSlots;
        var picked = Optimize(candidates, capital, slots, settings.MinProfitDay);
        var rows = picked
            .Where(p => p.Source is not null)
            .Select(p => ToRow(p, settings.HorizonDays))
            .OrderByDescending(r => r.ProfitDay)
            .ThenBy(r => r.TypeId)
            .ToList();

        var capUsed = rows.Sum(r => r.Capital);
        var profit = rows.Sum(r => r.ProfitDay);
        var slotsUsed = rows.Count * 2;
        var avgTurn = rows.Count == 0 ? 0 : rows.Average(r => r.TurnoverDays);
        var totals = new PortfolioTotals(
            rows.Count,
            slotsUsed,
            capUsed,
            Math.Max(0, capital - capUsed),
            profit,
            capUsed > 0 ? profit / capUsed * 100m : 0,
            avgTurn);

        return new PortfolioResponse(
            snapshotTime,
            new PortfolioInputs(settings.Capital, slots, settings.HorizonDays),
            totals,
            rows,
            rejected,
            settings);
    }

    public static PortfolioCandidate? BuildCandidate(
        BoardRow row, string location, PortfolioSettings settings, decimal brokerFee, long volumeDay)
    {
        var b = row.BestBuy;
        var s = row.BestSell;
        var m = row.MarginIsk;
        var competitors = Math.Max(1, Math.Max(row.BuyOrders, row.SellOrders));
        var share = Math.Min(1m / (competitors + 1), Math.Clamp(settings.MaxMarketShare, 0, 1));
        var u = volumeDay * share;
        var horizon = settings.HorizonDays <= 0 ? 2m : settings.HorizonDays;
        var qMax = (long)Math.Floor(u * horizon);
        if (qMax < 1)
            return null;

        var maxShare = Math.Clamp(settings.MaxShareItem, 0, 1);
        var capMax = qMax * b;
        var capCap = maxShare * settings.Capital;
        if (capCap > 0)
            capMax = Math.Min(capMax, capCap);
        qMax = b > 0 ? (long)Math.Floor(capMax / b) : 0;
        if (qMax < 1 || capMax <= 0)
            return null;

        var uEff = Math.Min(u, qMax / horizon);
        var window = Math.Max(1, Math.Max(row.Hours, Math.Max(row.BuyMoves, row.SellMoves)));
        var shiftsPerHour = Math.Max(row.BuyMoves, row.SellMoves) / (decimal)window;
        var relistsDay = Math.Min(shiftsPerHour * 24m, settings.MaxRelistsDay);
        var relistCost = relistsDay * (
            FeeMath.RelistFeePerMove(b, brokerFee) + FeeMath.RelistFeePerMove(s, brokerFee));
        var profitMax = m * uEff - relistCost;
        if (profitMax <= 0)
            return null;

        return new PortfolioCandidate(
            row.TypeId, row.Name, location, b, s, m, row.MarginFrac, volumeDay, row.Hours,
            row.BuyOrders, row.SellOrders, row.BuyMoves, row.SellMoves,
            capMax, profitMax, profitMax / capMax, qMax, u, share, competitors, relistsDay, relistCost);
    }

    public static List<OptCand> Optimize(
        IReadOnlyList<OptCand> candidates, decimal capital, int freeSlots, long minProfitDay)
    {
        var k = freeSlots / 2;
        if (k <= 0 || candidates.Count == 0)
            return [];

        var list = candidates.Select(Clone).ToList();
        var sel = Pick(list, 0, k);
        var sumCap = sel.Sum(c => c.CapMax);
        if (sumCap <= capital)
            return Finalize(sel, list, capital - sumCap, freeSlots, minProfitDay);

        var lo = 0m;
        var hi = list.Count == 0 ? 0m : list.Max(c => c.Rate);
        if (hi <= 0)
            hi = 1m;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2m;
            if (Pick(list, mid, k).Sum(c => c.CapMax) <= capital)
                hi = mid;
            else
                lo = mid;
        }

        var pickHi = Pick(list, hi, k);
        var optA = Finalize(pickHi, list, capital - pickHi.Sum(c => c.CapMax), freeSlots, minProfitDay);
        var optB = Trim(Pick(list, lo, k), capital, list, freeSlots, minProfitDay);
        var profitA = optA.Sum(ProfitOf);
        var profitB = optB.Sum(ProfitOf);
        return profitB > profitA ? optB : optA;
    }

    private static List<OptCand> Pick(IReadOnlyList<OptCand> candidates, decimal lambda, int k)
    {
        return candidates
            .Select(c => (C: c, Score: c.ProfitMax - lambda * c.CapMax))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.C.TypeId)
            .Take(k)
            .Select(x =>
            {
                var c = Clone(x.C);
                c.Cap = c.CapMax;
                return c;
            })
            .ToList();
    }

    private static List<OptCand> Trim(
        List<OptCand> sel, decimal capital, IReadOnlyList<OptCand> all, int freeSlots, long minProfitDay)
    {
        foreach (var c in sel)
            c.Cap = c.CapMax;
        var excess = sel.Sum(c => c.Cap) - capital;
        foreach (var c in sel.OrderBy(x => x.Rate).ThenBy(x => x.TypeId))
        {
            if (excess <= 0)
                break;
            var cut = Math.Min(c.Cap, excess);
            c.Cap -= cut;
            excess -= cut;
        }

        sel = sel.Where(c => c.Cap > 0 && ProfitOf(c) >= minProfitDay).ToList();
        var used = sel.Sum(c => c.Cap);
        return Finalize(sel, all, capital - used, freeSlots, minProfitDay);
    }

    private static List<OptCand> Finalize(
        List<OptCand> sel, IReadOnlyList<OptCand> all, decimal leftover, int freeSlots, long minProfitDay)
    {
        foreach (var c in sel)
        {
            if (c.Cap <= 0)
                c.Cap = c.CapMax;
        }

        var chosen = new HashSet<int>(sel.Select(c => c.TypeId));
        var slotsLeft = freeSlots - sel.Count * 2;
        while (slotsLeft >= 2 && leftover > 0)
        {
            OptCand? best = null;
            decimal bestProfit = 0;
            foreach (var c in all)
            {
                if (chosen.Contains(c.TypeId))
                    continue;
                var cap = Math.Min(c.CapMax, leftover);
                if (cap <= 0)
                    continue;
                var profit = ProfitLinear(c, cap);
                if (profit < minProfitDay)
                    continue;
                if (best is null || profit > bestProfit
                    || (profit == bestProfit && c.TypeId < best.TypeId))
                {
                    best = c;
                    bestProfit = profit;
                }
            }

            if (best is null)
                break;
            var add = Clone(best);
            add.Cap = Math.Min(best.CapMax, leftover);
            sel.Add(add);
            chosen.Add(add.TypeId);
            leftover -= add.Cap;
            slotsLeft -= 2;
        }

        return sel.Where(c => c.Cap > 0).OrderByDescending(ProfitOf).ThenBy(c => c.TypeId).ToList();
    }

    private static decimal ProfitOf(OptCand c) => ProfitLinear(c, c.Cap > 0 ? c.Cap : c.CapMax);

    private static decimal ProfitLinear(OptCand c, decimal cap)
    {
        if (c.CapMax <= 0 || cap <= 0)
            return 0;
        // прибыль линейна по капиталу (как в тестах §9); у реального кандидата
        // relist_cost уже вычтен из ProfitMax при полном объёме — при частичном
        // масштабируем только маржевую часть пропорционально, cost полный в Source.
        if (c.Source is null)
            return c.ProfitMax * (cap / c.CapMax);

        var src = c.Source;
        var q = src.Buy > 0 ? (long)Math.Floor(cap / src.Buy) : 0;
        if (q < 1)
            return 0;
        var horizon = src.QtyMax > 0 && src.UnitsDay > 0
            ? src.QtyMax / src.UnitsDay
            : 2m;
        if (horizon <= 0)
            horizon = 2m;
        var uEff = Math.Min(src.UnitsDay, q / horizon);
        return src.Margin * uEff - src.RelistCostDay;
    }

    private static OptCand Clone(OptCand c) => new()
    {
        TypeId = c.TypeId,
        ProfitMax = c.ProfitMax,
        CapMax = c.CapMax,
        Cap = c.Cap,
        Source = c.Source
    };

    private static PortfolioRow ToRow(OptCand p, decimal horizonDays)
    {
        var s = p.Source!;
        var cap = p.Cap > 0 ? p.Cap : p.CapMax;
        var q = s.Buy > 0 ? (long)Math.Floor(cap / s.Buy) : 0;
        var h = horizonDays <= 0 ? 2m : horizonDays;
        var unitsDay = Math.Min(s.UnitsDay, q / h);
        var profit = s.Margin * unitsDay - s.RelistCostDay;
        var partial = cap + 0.0001m < s.CapMax;
        var tickBuy = FeeMath.PriceTick(s.Buy);
        var tickSell = FeeMath.PriceTick(s.Sell);
        return new PortfolioRow(
            s.TypeId, s.Name, s.Location, s.Buy, s.Sell,
            s.Buy + tickBuy, Math.Max(0, s.Sell - tickSell),
            s.Margin, s.MarginFrac * 100m, q, cap, s.VolumeDay, s.Competitors,
            s.Share * 100m, unitsDay, s.RelistsDay, s.RelistCostDay, profit,
            cap > 0 ? profit / cap * 100m : 0,
            unitsDay > 0 ? q / unitsDay : 0,
            s.Hours, partial);
    }
}

public static class PortfolioSelfTest
{
    public static string Run()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail)
        {
            if (!ok)
                fails.Add(name + ": " + detail);
        }

        OptCand A() => new() { TypeId = 1, ProfitMax = 10_000_000m, CapMax = 50_000_000m };
        OptCand B() => new() { TypeId = 2, ProfitMax = 8_000_000m, CapMax = 20_000_000m };
        OptCand C() => new() { TypeId = 3, ProfitMax = 6_000_000m, CapMax = 10_000_000m };

        // 1
        {
            var r = PortfolioBuilder.Optimize([A(), B(), C()], 100_000_000m, 4, 0);
            var profit = r.Sum(x => x.ProfitMax * (x.Cap / x.CapMax));
            var cap = r.Sum(x => x.Cap);
            Check("t1", r.Count == 2 && Math.Abs(profit - 18_000_000m) < 1 && Math.Abs(cap - 70_000_000m) < 1,
                $"n={r.Count} profit={profit} cap={cap}");
        }

        // 2
        {
            var r = PortfolioBuilder.Optimize([A(), B(), C()], 30_000_000m, 6, 0);
            var profit = r.Sum(x => x.ProfitMax * (x.Cap / x.CapMax));
            var cap = r.Sum(x => x.Cap);
            Check("t2", Math.Abs(profit - 14_000_000m) < 1 && Math.Abs(cap - 30_000_000m) < 1,
                $"profit={profit} cap={cap} n={r.Count}");
        }

        // 3 ключевой
        {
            var r = PortfolioBuilder.Optimize([A(), B(), C()], 60_000_000m, 4, 0);
            var profit = r.Sum(x => x.ProfitMax * (x.Cap / x.CapMax));
            var cap = r.Sum(x => x.Cap);
            Check("t3", Math.Abs(profit - 16_000_000m) < 1 && Math.Abs(cap - 60_000_000m) < 1,
                $"profit={profit} cap={cap} ids={string.Join(',', r.Select(x => x.TypeId))}");
        }

        // 4
        {
            var r = PortfolioBuilder.Optimize([A(), B(), C()], 100_000_000m, 1, 0);
            Check("t4", r.Count == 0, $"n={r.Count}");
        }

        // 5 max share 0.1 → cap_max ≤ 10M each (pretend already capped)
        {
            var a = new OptCand { TypeId = 1, ProfitMax = 2_000_000m, CapMax = 10_000_000m };
            var b = new OptCand { TypeId = 2, ProfitMax = 4_000_000m, CapMax = 10_000_000m };
            var c = new OptCand { TypeId = 3, ProfitMax = 6_000_000m, CapMax = 10_000_000m };
            var r = PortfolioBuilder.Optimize([a, b, c], 100_000_000m, 6, 0);
            var profit = r.Sum(x => x.ProfitMax * (x.Cap / x.CapMax));
            Check("t5", Math.Abs(profit - 12_000_000m) < 1, $"profit={profit}");
        }

        Check("slots305", FeeMath.MarketSlots(5, 5, 5, 5) == 305, FeeMath.MarketSlots(5, 5, 5, 5).ToString());
        Check("slots5", FeeMath.MarketSlots(0, 0, 0, 0) == 5, FeeMath.MarketSlots(0, 0, 0, 0).ToString());
        var s = new PortfolioSettings(SlotsMode: "skills", SkillTrade: 0, SkillRetail: 0, SkillWholesale: 0, SkillTycoon: 0,SlotsUsed: 10);
        Check("slotsUsed", s.FreeSlots == 0, s.FreeSlots.ToString());

        return fails.Count == 0 ? "OK " + 7 : "FAIL\n" + string.Join("\n", fails);
    }
}
