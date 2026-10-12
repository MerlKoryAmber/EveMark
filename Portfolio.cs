namespace JitaWatch;

public sealed record PortfolioRequest(
    decimal Capital,
    int Trade,
    int Retail,
    int Wholesale,
    int Tycoon,
    int MarginTrading,
    int MaxItems = 80);

public sealed record PortfolioPick(
    int TypeId,
    string Name,
    decimal BestBuy,
    decimal BestSell,
    decimal MarginIsk,
    long Units,
    decimal Escrow,
    decimal DailyIsk,
    int SlotsUsed);

public sealed record PortfolioDto(
    int Slots,
    decimal Capital,
    decimal EscrowUsed,
    int SlotsUsed,
    decimal DailyIskTotal,
    IReadOnlyList<PortfolioPick> Picks);

public static class PortfolioBuilder
{
    public static PortfolioDto Build(PortfolioRequest req, IReadOnlyList<BoardRow> ranked, decimal brokerFee)
    {
        var slots = FeeMath.MarketSlots(req.Trade, req.Retail, req.Wholesale, req.Tycoon);
        var capital = Math.Max(0, req.Capital);
        var picks = new List<PortfolioPick>();
        var escrowUsed = 0m;
        var slotsUsed = 0;
        var daily = 0m;

        // Жадно: лучшая оценка/сутки на единицу эскроу, по одной «стороне» (buy+sell = 2 слота) на тип.
        var candidates = ranked
            .Where(r => r.DailyIsk is > 0 && r.BestBuy > 0 && r.MarginIsk > 0)
            .Where(r => r.BuyVolume >= 2 && r.SellVolume >= 2)
            .Select(r =>
            {
                var units = Math.Max(1, Math.Min(r.BuyVolume, r.SellVolume));
                if (r.DailyVolume is long vol && vol > 0)
                    units = Math.Min(units, Math.Max(1, vol / Math.Max(1, r.Competitors)));
                var escrow = FeeMath.BuyEscrow(r.BestBuy, units, req.MarginTrading);
                // buy + sell ордер
                const int needSlots = 2;
                var score = escrow > 0 ? r.DailyIsk!.Value / escrow : 0;
                return (Row: r, Units: units, Escrow: escrow, NeedSlots: needSlots, Score: score);
            })
            .Where(c => c.Escrow > 0 && c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(Math.Clamp(req.MaxItems * 3, 30, 400))
            .ToList();

        foreach (var c in candidates)
        {
            if (picks.Count >= req.MaxItems)
                break;
            if (slotsUsed + c.NeedSlots > slots)
                continue;
            if (escrowUsed + c.Escrow > capital)
            {
                // попробовать урезать units
                var maxUnits = c.Escrow <= 0 ? 0 : (long)Math.Floor((capital - escrowUsed) / (c.Row.BestBuy * FeeMath.MarginEscrowFraction(req.MarginTrading)));
                if (maxUnits < 1)
                    continue;
                var escrow = FeeMath.BuyEscrow(c.Row.BestBuy, maxUnits, req.MarginTrading);
                if (escrowUsed + escrow > capital)
                    continue;
                picks.Add(new PortfolioPick(
                    c.Row.TypeId, c.Row.Name, c.Row.BestBuy, c.Row.BestSell, c.Row.MarginIsk,
                    maxUnits, escrow, ScaleDaily(c.Row.DailyIsk!.Value, c.Units, maxUnits), c.NeedSlots));
                escrowUsed += escrow;
                slotsUsed += c.NeedSlots;
                daily += ScaleDaily(c.Row.DailyIsk!.Value, c.Units, maxUnits);
                continue;
            }

            picks.Add(new PortfolioPick(
                c.Row.TypeId, c.Row.Name, c.Row.BestBuy, c.Row.BestSell, c.Row.MarginIsk,
                c.Units, c.Escrow, c.Row.DailyIsk!.Value, c.NeedSlots));
            escrowUsed += c.Escrow;
            slotsUsed += c.NeedSlots;
            daily += c.Row.DailyIsk!.Value;
        }

        _ = brokerFee;
        return new PortfolioDto(slots, capital, escrowUsed, slotsUsed, daily, picks);
    }

    private static decimal ScaleDaily(decimal daily, long fullUnits, long used) =>
        fullUnits <= 0 ? 0 : daily * used / fullUnits;
}
