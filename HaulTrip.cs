using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace JitaWatch;

public sealed record HaulSettings(
    string From = "jita",
    string To = "any",
    string ShipPreset = "dst",
    decimal CargoM3 = 60_000m,
    long Budget = 1_000_000_000,
    long MaxCargoValue = 1_500_000_000,
    string Mode = "best",
    string RouteFlag = "secure",
    decimal ListHorizonDays = 3m,
    decimal MaxMarketShare = 0.30m,
    long MinItemProfit = 1_000_000,
    int SecPerJump = 60,
    string GankSystems = "Uedama, Sivala, Ahbazon",
    long GankWarnValue = 1_000_000_000,
    long MinUnitPrice = 500_000,
    long CourierBase = 0,
    decimal CourierPerM3 = 0,
    long CourierPerJump = 0,
    decimal CourierCollateralPct = 0,
    decimal CourierMaxM3 = 0,
    /// <summary>Ask старше N суток не якорь для list (0 = не смотреть возраст).</summary>
    decimal ListMaxAskAgeDays = 7m,
    /// <summary>Сколько дней истории региона B брать для потолка цены list.</summary>
    int ListHistDays = 14)
{
    public decimal BudgetEff => Math.Min(Budget, MaxCargoValue);
    public bool CourierEnabled =>
        CourierBase > 0 || CourierPerM3 > 0 || CourierPerJump > 0 || CourierCollateralPct > 0;

    public static decimal PresetM3(string key) => key?.ToLowerInvariant() switch
    {
        "br" => 10_000m,
        "dst" => 60_000m,
        "freighter" => 1_000_000m,
        _ => 0
    };
}

public sealed record BookLevel(decimal Price, long Remain, long MinVolume = 1);

public sealed record HaulLot(
    int TypeId,
    int LotIndex,
    long UnitsMax,
    decimal UnitCost,
    decimal UnitM3,
    decimal UnitProfit,
    decimal UnitSell,
    string Mode);

public sealed record HaulBasketPick(HaulLot Lot, long Qty);

public sealed record HaulBasket(
    IReadOnlyList<HaulBasketPick> Picks,
    decimal Profit,
    decimal Cost,
    decimal M3Used)
{
    public static HaulBasket Empty { get; } = new([], 0, 0, 0);
}

public sealed record HaulTripRow(
    [property: JsonPropertyName("type_id")] int TypeId,
    string Name,
    string Mode,
    long Qty,
    [property: JsonPropertyName("unit_m3")] decimal UnitM3,
    decimal M3,
    [property: JsonPropertyName("buy_avg")] decimal BuyAvg,
    [property: JsonPropertyName("buy_max")] decimal BuyMax,
    decimal Cost,
    [property: JsonPropertyName("sell_avg")] decimal SellAvg,
    [property: JsonPropertyName("sell_min")] decimal SellMin,
    decimal Profit,
    [property: JsonPropertyName("profit_pct")] decimal ProfitPct,
    [property: JsonPropertyName("volume_day_B")] long VolumeDayB,
    [property: JsonPropertyName("days_to_sell")] decimal DaysToSell,
    [property: JsonPropertyName("ask_age_days")] decimal? AskAgeDays = null);

public sealed record HaulTripSummary(
    string From,
    string To,
    int Jumps,
    [property: JsonPropertyName("min_sec")] decimal MinSec,
    [property: JsonPropertyName("lowsec_jumps")] int LowsecJumps,
    [property: JsonPropertyName("no_secure_path")] bool NoSecurePath,
    [property: JsonPropertyName("gank_hits")] IReadOnlyList<string> GankHits,
    [property: JsonPropertyName("gank_warning")] bool GankWarning,
    int Items,
    [property: JsonPropertyName("m3_used")] decimal M3Used,
    [property: JsonPropertyName("fill_pct")] decimal FillPct,
    decimal Cost,
    decimal Profit,
    [property: JsonPropertyName("roi_pct")] decimal RoiPct,
    [property: JsonPropertyName("isk_per_jump")] decimal IskPerJump,
    [property: JsonPropertyName("isk_per_hour")] decimal IskPerHour,
    [property: JsonPropertyName("days_to_sell")] decimal DaysToSell,
    [property: JsonPropertyName("courier_cost")] decimal CourierCost,
    [property: JsonPropertyName("profit_courier")] decimal? ProfitCourier,
    IReadOnlyList<HaulTripRow>? Rows = null);

public sealed record HaulTripsDto(
    [property: JsonPropertyName("snapshot_time")] string? SnapshotTime,
    [property: JsonPropertyName("snapshot_age_min")] int SnapshotAgeMin,
    object Inputs,
    IReadOnlyList<HaulTripSummary> Trips);

/// <summary>Построение лотов §4 и оптимизатор корзины §5 (жадность 5.2).</summary>
public static class HaulPlanner
{
    public static IReadOnlyList<HaulLot> BuildInstantLots(
        int typeId,
        decimal unitM3,
        decimal salesTax,
        IReadOnlyList<BookLevel> asksAsc,
        IReadOnlyList<BookLevel> bidsDesc)
    {
        var lots = new List<HaulLot>();
        var ai = 0;
        var bi = 0;
        var askLeft = asksAsc.Count > 0 ? asksAsc[0].Remain : 0L;
        var bidLeft = bidsDesc.Count > 0 ? bidsDesc[0].Remain : 0L;
        var lotIndex = 0;
        while (ai < asksAsc.Count && bi < bidsDesc.Count)
        {
            var ask = asksAsc[ai];
            var bid = bidsDesc[bi];
            if (askLeft <= 0)
            {
                ai++;
                if (ai >= asksAsc.Count)
                    break;
                askLeft = asksAsc[ai].Remain;
                continue;
            }

            if (bidLeft <= 0)
            {
                bi++;
                if (bi >= bidsDesc.Count)
                    break;
                bidLeft = bidsDesc[bi].Remain;
                continue;
            }

            // min_volume: ордер пропускаем, если ему нельзя отдать минимум из доступного
            var canOffer = Math.Min(askLeft, bidLeft);
            if (bid.MinVolume > canOffer)
            {
                bi++;
                if (bi >= bidsDesc.Count)
                    break;
                bidLeft = bidsDesc[bi].Remain;
                continue;
            }

            var unitProfit = bid.Price * (1m - salesTax) - ask.Price;
            if (unitProfit <= 0)
                break;

            var take = canOffer;
            if (take < 1)
                break;
            lots.Add(new HaulLot(typeId, lotIndex++, take, ask.Price, unitM3, unitProfit, bid.Price, "instant"));
            askLeft -= take;
            bidLeft -= take;
        }

        return lots;
    }

    public static long ListQtyMax(long volumeDayB, int competitors, decimal horizonDays, decimal maxShare)
    {
        var share = Math.Min(1m / (competitors + 1), maxShare);
        return (long)Math.Floor(volumeDayB * share * horizonDays);
    }

    /// <summary>§6: days_to_sell = x / (V_B × share) для list; при корректном q_list_max не больше горизонта.</summary>
    public static decimal ListDaysToSell(long qty, long volumeDayB, decimal share)
    {
        if (qty < 1 || volumeDayB < 1 || share <= 0)
            return 0;
        return qty / (volumeDayB * share);
    }

    public static decimal TripListDaysToSell(
        IEnumerable<HaulBasketPick> picks,
        Func<int, (long VolB, decimal Share)?> meta,
        decimal horizonDays)
    {
        decimal max = 0;
        foreach (var g in picks.Where(p => p.Lot.Mode == "list").GroupBy(p => p.Lot.TypeId))
        {
            var m = meta(g.Key);
            if (m is null)
                continue;
            var (volB, share) = m.Value;
            var days = ListDaysToSell(g.Sum(p => p.Qty), volB, share);
            if (days > max)
                max = days;
        }

        if (horizonDays > 0 && max > horizonDays)
            max = horizonDays;
        return max;
    }

    /// <summary>
    /// Потолок sell_post относительно закупки на A (list).
    /// Без полного стакана best_sell на B часто «космический» — рейс не должен на этом строиться.
    /// </summary>
    /// <summary>Страховка после возраста/истории/соседей. Выше — list не берём.</summary>
    public const decimal MaxListAskMultiple = 2m;

    /// <summary>Если на B есть buy: best_sell/best_buy выше этого — опорная цена для list ненадёжна.</summary>
    public const decimal MaxListLocalSpread = 2m;

    /// <summary>Минимальная медиана объёма/сутки в регионе B для режима list.</summary>
    public const long MinListVolumeDay = 10;

    /// <summary>Минимум sell-ордеров на станции B для list (не один одиночный ask).</summary>
    public const int MinListSellOrders = 3;

    public static bool ListMarketOk(long volumeDayB, long sellOrders, decimal bestSellB, decimal? bestBuyB)
    {
        if (volumeDayB < MinListVolumeDay || sellOrders < MinListSellOrders || bestSellB <= 0)
            return false;
        if (bestBuyB is decimal bid && bid > 0 && bestSellB > bid * MaxListLocalSpread)
            return false;
        return true;
    }

    public static decimal? AskAgeDays(DateTime? issuedUtc, DateTime utcNow)
    {
        if (issuedUtc is null)
            return null;
        var days = (decimal)(utcNow.ToUniversalTime() - issuedUtc.Value.ToUniversalTime()).TotalDays;
        return days < 0 ? 0 : days;
    }

    /// <summary>
    /// Опорная цена list: min(best_sell−tick, histCap, neighborCap).
    /// Старый ask (возраст &gt; maxAge) не якорь — min(история, соседние кроме A/B); без истории list нет.
    /// </summary>
    public static decimal? ResolveListSellPost(
        decimal bestSellB,
        decimal? histCap,
        decimal? neighborCap,
        decimal? askAgeDays,
        decimal maxAskAgeDays)
    {
        if (bestSellB <= 0)
            return null;
        var fromAsk = bestSellB - FeeMath.PriceTick(bestSellB);
        if (fromAsk <= 0)
            return null;
        var stale = maxAskAgeDays > 0 && askAgeDays is decimal age && age > maxAskAgeDays;
        if (stale)
        {
            if (histCap is not decimal h || h <= 0)
                return null;
            var stalePost = h;
            if (neighborCap is decimal nStale && nStale > 0)
                stalePost = Math.Min(stalePost, nStale);
            return stalePost;
        }
        var post = fromAsk;
        if (histCap is decimal cap && cap > 0)
            post = Math.Min(post, cap);
        if (neighborCap is decimal n && n > 0)
            post = Math.Min(post, n);
        return post;
    }

    /// <summary>
    /// Мин. best_sell на хабах кроме A и B. A нельзя включать: иначе post ≤ askA и после налога list всегда ≤ 0.
    /// </summary>
    public static decimal? NeighborSellCap(
        IReadOnlyDictionary<string, Dictionary<int, decimal>> hubBestSell,
        int typeId,
        string fromHub,
        string toHub)
    {
        decimal? min = null;
        foreach (var (hub, byType) in hubBestSell)
        {
            if (string.Equals(hub, fromHub, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hub, toHub, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!byType.TryGetValue(typeId, out var sell) || sell <= 0)
                continue;
            min = min is null ? sell : Math.Min(min.Value, sell);
        }

        return min;
    }

    public static IReadOnlyList<HaulLot> BuildListLots(
        int typeId,
        decimal unitM3,
        decimal salesTax,
        decimal brokerFee,
        decimal bestSellB,
        decimal? bestBuyB,
        long qListMax,
        IReadOnlyList<BookLevel> asksAsc,
        decimal? histCap = null,
        decimal? askAgeDays = null,
        decimal maxAskAgeDays = 7m,
        decimal? neighborCap = null)
    {
        if (qListMax < 1 || asksAsc.Count == 0)
            return [];
        if (bestBuyB is decimal bid && bid > 0 && bestSellB > bid * MaxListLocalSpread)
            return [];
        var sellPost = ResolveListSellPost(bestSellB, histCap, neighborCap, askAgeDays, maxAskAgeDays);
        if (sellPost is not decimal post || post <= 0)
            return [];
        // Одиночный / завышенный sell на B — не считаем list.
        if (post > asksAsc[0].Price * MaxListAskMultiple)
            return [];
        var unitNet = post * (1m - brokerFee - salesTax);
        var lots = new List<HaulLot>();
        var left = qListMax;
        var lotIndex = 0;
        foreach (var ask in asksAsc)
        {
            if (left < 1)
                break;
            if (post > ask.Price * MaxListAskMultiple)
                break;
            var unitProfit = unitNet - ask.Price;
            if (unitProfit <= 0)
                break;
            var take = Math.Min(ask.Remain, left);
            if (take < 1)
                continue;
            lots.Add(new HaulLot(typeId, lotIndex++, take, ask.Price, unitM3, unitProfit, post, "list"));
            left -= take;
        }

        return lots;
    }

    public static IReadOnlyList<HaulLot> BuildBestLots(
        int typeId,
        decimal unitM3,
        decimal salesTax,
        decimal brokerFee,
        decimal bestSellB,
        decimal? bestBuyB,
        long qListMax,
        IReadOnlyList<BookLevel> asksAsc,
        IReadOnlyList<BookLevel> bidsDesc,
        decimal? histCap = null,
        decimal? askAgeDays = null,
        decimal maxAskAgeDays = 7m,
        decimal? neighborCap = null)
    {
        var instant = BuildInstantLots(typeId, unitM3, salesTax, asksAsc, bidsDesc);
        var list = BuildListLots(typeId, unitM3, salesTax, brokerFee, bestSellB, bestBuyB, qListMax, asksAsc,
            histCap, askAgeDays, maxAskAgeDays, neighborCap);
        var pInst = instant.Sum(l => l.UnitProfit * l.UnitsMax);
        var pList = list.Sum(l => l.UnitProfit * l.UnitsMax);
        if (pList > pInst)
            return list;
        return instant;
    }

    public static HaulBasket Optimize(IReadOnlyList<HaulLot> lots, decimal cargoM3, decimal budget)
    {
        if (cargoM3 <= 0 || budget <= 0 || lots.Count == 0)
            return HaulBasket.Empty;

        var a = Greedy(lots, cargoM3, budget, DensityDominant);
        var b = Greedy(lots, cargoM3, budget, l => l.UnitM3 > 0 ? l.UnitProfit / l.UnitM3 : 0);
        var c = Greedy(lots, cargoM3, budget, l => l.UnitCost > 0 ? l.UnitProfit / l.UnitCost : 0);
        return BestOf(a, b, c);
    }

    public static HaulBasket OptimizeWithMinProfit(
        IReadOnlyList<HaulLot> lots, decimal cargoM3, decimal budget, long minItemProfit)
    {
        var basket = Optimize(lots, cargoM3, budget);
        if (minItemProfit <= 0 || basket.Picks.Count == 0)
            return basket;

        var byType = basket.Picks.GroupBy(p => p.Lot.TypeId)
            .Where(g => g.Sum(p => p.Lot.UnitProfit * p.Qty) >= minItemProfit)
            .Select(g => g.Key)
            .ToHashSet();
        var keptLots = lots.Where(l => byType.Contains(l.TypeId)).ToList();
        return Optimize(keptLots, cargoM3, budget);
    }

    private static decimal DensityDominant(HaulLot l, decimal m, decimal b)
    {
        var dm = m > 0 ? l.UnitM3 / m : 0;
        var db = b > 0 ? l.UnitCost / b : 0;
        var den = Math.Max(dm, db);
        return den > 0 ? l.UnitProfit / den : 0;
    }

    private static HaulBasket Greedy(
        IReadOnlyList<HaulLot> lots,
        decimal cargoM3,
        decimal budget,
        Func<HaulLot, decimal> score)
        => Greedy(lots, cargoM3, budget, (l, _, _) => score(l));

    private static HaulBasket Greedy(
        IReadOnlyList<HaulLot> lots,
        decimal cargoM3,
        decimal budget,
        Func<HaulLot, decimal, decimal, decimal> score)
    {
        var ordered = lots
            .OrderByDescending(l => score(l, cargoM3, budget))
            .ThenBy(l => l.TypeId)
            .ThenBy(l => l.LotIndex)
            .ToList();
        var remain = ordered.ToDictionary(l => (l.TypeId, l.LotIndex), l => l.UnitsMax);
        var picks = new List<HaulBasketPick>();
        var m3 = 0m;
        var cost = 0m;
        var profit = 0m;
        foreach (var lot in ordered)
        {
            var key = (lot.TypeId, lot.LotIndex);
            var left = remain[key];
            if (left < 1 || lot.UnitM3 <= 0 || lot.UnitCost <= 0)
                continue;
            var remM3 = cargoM3 - m3;
            var remIsk = budget - cost;
            if (remM3 < lot.UnitM3 || remIsk < lot.UnitCost)
                continue;
            var maxM3 = remM3 >= (decimal)long.MaxValue * lot.UnitM3
                ? long.MaxValue
                : (long)Math.Floor(remM3 / lot.UnitM3);
            var maxIsk = remIsk >= (decimal)long.MaxValue * lot.UnitCost
                ? long.MaxValue
                : (long)Math.Floor(remIsk / lot.UnitCost);
            var x = Math.Min(left, Math.Min(maxM3, maxIsk));
            if (x < 1)
                continue;
            picks.Add(new HaulBasketPick(lot, x));
            remain[key] = left - x;
            m3 += lot.UnitM3 * x;
            cost += lot.UnitCost * x;
            profit += lot.UnitProfit * x;
        }

        return new HaulBasket(picks, profit, cost, m3);
    }

    private static HaulBasket BestOf(params HaulBasket[] baskets) =>
        baskets.OrderByDescending(b => b.Profit)
            .ThenBy(b => b.Cost)
            .ThenBy(b => b.M3Used)
            .First();

    public static decimal CourierCost(HaulSettings s, decimal m3Used, decimal cargoCost, int jumps) =>
        s.CourierBase
        + s.CourierPerM3 * m3Used
        + s.CourierPerJump * jumps
        + s.CourierCollateralPct / 100m * cargoCost;

    public static HaulTripSummary Summarize(
        string from, string to,
        HaulBasket basket,
        HaulSettings settings,
        int jumps,
        decimal minSec,
        int lowsecJumps,
        bool noSecurePath,
        IReadOnlyList<string> gankHits,
        decimal daysToSell,
        IReadOnlyList<HaulTripRow>? rows = null)
    {
        var m = Math.Max(settings.CargoM3, 0.0001m);
        var tripHours = jumps * settings.SecPerJump / 3600m;
        var courier = CourierCost(settings, basket.M3Used, basket.Cost, jumps);
        var gankWarn = gankHits.Count > 0 && basket.Cost > settings.GankWarnValue;
        return new HaulTripSummary(
            from, to, jumps, minSec, lowsecJumps, noSecurePath, gankHits, gankWarn,
            rows?.Count ?? basket.Picks.Select(p => p.Lot.TypeId).Distinct().Count(),
            basket.M3Used,
            basket.M3Used / m * 100m,
            basket.Cost,
            basket.Profit,
            basket.Cost > 0 ? basket.Profit / basket.Cost * 100m : 0,
            basket.Profit / Math.Max(jumps, 1),
            basket.Profit / Math.Max(tripHours, 1m / 60m),
            daysToSell,
            courier,
            settings.CourierEnabled ? basket.Profit - courier : null,
            rows);
    }

    public static string Multibuy(IEnumerable<HaulTripRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.Name).Append('\t').Append(r.Qty.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    public static string Csv(IEnumerable<HaulTripRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,type_id,mode,qty,unit_m3,m3,buy_avg,buy_max,cost,sell_avg,sell_min,profit,profit_pct,volume_day_B,days_to_sell,ask_age_days");
        foreach (var r in rows)
        {
            sb.Append(Escape(r.Name)).Append(',')
                .Append(r.TypeId).Append(',')
                .Append(r.Mode).Append(',')
                .Append(r.Qty).Append(',')
                .Append(Inv(r.UnitM3)).Append(',')
                .Append(Inv(r.M3)).Append(',')
                .Append(Inv(r.BuyAvg)).Append(',')
                .Append(Inv(r.BuyMax)).Append(',')
                .Append(Inv(r.Cost)).Append(',')
                .Append(Inv(r.SellAvg)).Append(',')
                .Append(Inv(r.SellMin)).Append(',')
                .Append(Inv(r.Profit)).Append(',')
                .Append(Inv(r.ProfitPct)).Append(',')
                .Append(r.VolumeDayB).Append(',')
                .Append(Inv(r.DaysToSell)).Append(',')
                .Append(r.AskAgeDays is decimal a ? Inv(a) : "").Append('\n');
        }

        return sb.ToString();
    }

    private static string Inv(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    private static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

public static class HaulSelfTest
{
    public static string Run()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail)
        {
            if (!ok)
                fails.Add(name + ": " + detail);
        }

        // K1
        {
            var lots = new[]
            {
                new HaulLot(1, 0, 5, 1, 10, 50, 51, "instant"),
                new HaulLot(2, 0, 100, 1, 1, 4, 5, "instant")
            };
            var b = HaulPlanner.Optimize(lots, 100, decimal.MaxValue);
            Check("K1", b.Profit == 450 && b.Picks.Sum(p => p.Lot.TypeId == 1 ? p.Qty : 0) == 5
                && b.Picks.Sum(p => p.Lot.TypeId == 2 ? p.Qty : 0) == 50,
                $"profit={b.Profit}");
        }

        // K2
        {
            var lots = new[]
            {
                new HaulLot(1, 0, 5, 100, 1, 30, 130, "instant"),
                new HaulLot(2, 0, 100, 10, 1, 2, 12, "instant")
            };
            var b = HaulPlanner.Optimize(lots, decimal.MaxValue, 1000);
            Check("K2", b.Profit == 250, $"profit={b.Profit} cost={b.Cost}");
        }

        // K3
        {
            var lots = new[]
            {
                new HaulLot(3, 0, 4, 1, 1, 10, 11, "instant"),
                new HaulLot(3, 1, 6, 1, 1, 3, 4, "instant"),
                new HaulLot(4, 0, 10, 1, 1, 5, 6, "instant")
            };
            var b = HaulPlanner.Optimize(lots, 8, decimal.MaxValue);
            var z1 = b.Picks.Where(p => p.Lot.TypeId == 3 && p.Lot.LotIndex == 0).Sum(p => p.Qty);
            var z2 = b.Picks.Where(p => p.Lot.TypeId == 3 && p.Lot.LotIndex == 1).Sum(p => p.Qty);
            var w = b.Picks.Where(p => p.Lot.TypeId == 4).Sum(p => p.Qty);
            Check("K3", b.Profit == 60 && z1 == 4 && z2 == 0 && w == 4,
                $"profit={b.Profit} z1={z1} z2={z2} w={w}");
        }

        // K4
        Check("K4", HaulPlanner.Optimize([new HaulLot(1, 0, 5, 1, 1, 10, 11, "i")], 0, 1000).Picks.Count == 0, "empty");

        // K5
        {
            var b = HaulPlanner.Optimize([new HaulLot(1, 0, 3, 1, 4, 10, 11, "i")], 10, decimal.MaxValue);
            Check("K5", b.Profit == 20 && b.Picks.Sum(p => p.Qty) == 2, $"profit={b.Profit}");
        }

        // K6
        {
            var b = HaulPlanner.Optimize([new HaulLot(1, 0, 10, 100, 1, 20, 120, "i")], decimal.MaxValue, 500);
            Check("K6", b.Profit == 100 && b.Cost == 500, $"profit={b.Profit} cost={b.Cost}");
        }

        // tick
        Check("tick1", FeeMath.PriceTick(1234.56m) == 1m, FeeMath.PriceTick(1234.56m).ToString(CultureInfo.InvariantCulture));
        Check("tick2", FeeMath.PriceTick(15.5m) == 0.01m, FeeMath.PriceTick(15.5m).ToString(CultureInfo.InvariantCulture));
        Check("tick3", FeeMath.PriceTick(2_500_000m) == 1000m, FeeMath.PriceTick(2_500_000m).ToString(CultureInfo.InvariantCulture));

        // list qty max
        Check("q_list", HaulPlanner.ListQtyMax(10, 1, 3, 0.3m) == 9, HaulPlanner.ListQtyMax(10, 1, 3, 0.3m).ToString());

        // list: космический / завышенный best_sell на B отсекается
        {
            var fat = HaulPlanner.BuildListLots(1, 1, 0, 0, 9_990_000_000m, null, 5, [new BookLevel(3_500_000m, 5)]);
            var mid = HaulPlanner.BuildListLots(1, 1, 0, 0, 2_109_000_000m, null, 5, [new BookLevel(853_000_000m, 5)]);
            var ok = HaulPlanner.BuildListLots(1, 1, 0, 0, 4_000_000m, 3_500_000m, 5, [new BookLevel(3_500_000m, 5)]);
            var wide = HaulPlanner.BuildListLots(1, 1, 0, 0, 10_000_000m, 1_000_000m, 5, [new BookLevel(3_500_000m, 5)]);
            Check("list_fat", fat.Count == 0 && mid.Count == 0 && ok.Count == 1 && wide.Count == 0,
                $"fat={fat.Count} mid={mid.Count} ok={ok.Count} wide={wide.Count}");
            Check("list_mkt", !HaulPlanner.ListMarketOk(2, 5, 2_109_000_000m, 100_000_000m)
                && HaulPlanner.ListMarketOk(20, 5, 4_000_000m, 3_500_000m), "market gate");
            var stale = HaulPlanner.ResolveListSellPost(2_110_000_000m, 1_674_000_000m, 677_100_000m, 40m, 7m);
            var fresh = HaulPlanner.ResolveListSellPost(900_000_000m, 1_674_000_000m, 677_100_000m, 1m, 7m);
            var staleNoHist = HaulPlanner.ResolveListSellPost(2_110_000_000m, null, 677_100_000m, 40m, 7m);
            // stale: hist ∧ neighbor (ask не якорь, но соседний потолок остаётся)
            Check("list_age", stale == 677_100_000m && fresh == 677_100_000m && staleNoHist is null,
                $"stale={stale} fresh={fresh} noHist={staleNoHist}");
            // A=rens B=hek — потолок с jita/dodixie, не с A
            var neigh = HaulPlanner.NeighborSellCap(
                new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hek"] = new() { [1] = 2_110_000_000m },
                    ["rens"] = new() { [1] = 500_000_000m },
                    ["jita"] = new() { [1] = 677_100_000m },
                    ["dodixie"] = new() { [1] = 853_800_000m }
                }, 1, "rens", "hek");
            Check("list_neigh", neigh == 677_100_000m, neigh?.ToString(CultureInfo.InvariantCulture) ?? "null");
            var neighOnlyAb = HaulPlanner.NeighborSellCap(
                new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hek"] = new() { [1] = 2_110_000_000m },
                    ["rens"] = new() { [1] = 500_000_000m }
                }, 1, "rens", "hek");
            Check("list_neigh2", neighOnlyAb is null, neighOnlyAb?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        // days_to_sell ≤ горизонта при qty ≤ q_list_max
        {
            var q = HaulPlanner.ListQtyMax(10, 1, 3, 0.3m);
            var share = Math.Min(1m / 2, 0.3m);
            var days = HaulPlanner.ListDaysToSell(q, 10, share);
            Check("days_h", days <= 3m && days > 0, $"q={q} days={days}");
            var tripDays = HaulPlanner.TripListDaysToSell(
                [new HaulBasketPick(new HaulLot(1, 0, q, 1, 1, 1, 2, "list"), q)],
                _ => (10L, share),
                3m);
            Check("days_trip", tripDays <= 3m, tripDays.ToString(CultureInfo.InvariantCulture));
        }

        // instant min_volume
        {
            var lots = HaulPlanner.BuildInstantLots(1, 1, 0,
                [new BookLevel(100, 5)],
                [new BookLevel(200, 100, 10), new BookLevel(150, 100, 1)]);
            Check("min_vol", lots.Count == 1 && lots[0].UnitSell == 150 && lots[0].UnitsMax == 5,
                lots.Count == 0 ? "empty" : $"sell={lots[0].UnitSell} u={lots[0].UnitsMax}");
        }

        // instant two pointer
        {
            var lots = HaulPlanner.BuildInstantLots(1, 1, 0,
                [new BookLevel(100, 3), new BookLevel(120, 5)],
                [new BookLevel(130, 4), new BookLevel(110, 10)]);
            var units = lots.Sum(l => l.UnitsMax);
            var profit = lots.Sum(l => l.UnitProfit * l.UnitsMax);
            Check("2ptr", units == 4 && profit == 100, $"u={units} p={profit}");
        }

        // determinism
        {
            var lots = new[]
            {
                new HaulLot(2, 0, 10, 5, 2, 3, 8, "i"),
                new HaulLot(1, 0, 10, 5, 2, 3, 8, "i")
            };
            var a = HaulPlanner.Optimize(lots, 20, 100);
            var b = HaulPlanner.Optimize(lots, 20, 100);
            Check("det", a.Profit == b.Profit && a.Cost == b.Cost
                && a.Picks.Select(p => (p.Lot.TypeId, p.Qty)).SequenceEqual(b.Picks.Select(p => (p.Lot.TypeId, p.Qty))),
                "mismatch");
        }

        return fails.Count == 0
            ? "OK haul " + 12
            : "FAIL\n" + string.Join("\n", fails);
    }
}
