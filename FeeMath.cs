namespace JitaWatch;

public sealed record FeeSettings(int Accounting, int BrokerRelations, decimal FactionStanding, decimal CorpStanding, int KeepDays)
{
    public long DatabaseBytes { get; init; }

    public decimal SalesTax => FeeMath.SalesTax(Accounting);

    public decimal BrokerFee => FeeMath.BrokerFee(BrokerRelations, FactionStanding, CorpStanding);
}

public static class FeeMath
{
    public static decimal SalesTax(int accounting) =>
        0.075m * (1m - 0.11m * accounting);

    public static decimal BrokerFee(int brokerRelations, decimal factionStanding, decimal corpStanding)
    {
        var fee = 0.03m
            - 0.003m * brokerRelations
            - 0.0003m * factionStanding
            - 0.0002m * corpStanding;
        return fee < 0.01m ? 0.01m : fee;
    }

    public static (decimal Margin, decimal Fraction) Margin(decimal buy, decimal sell, decimal salesTax, decimal brokerFee)
    {
        var cost = buy * (1m + brokerFee);
        var proceeds = sell * (1m - brokerFee - salesTax);
        var margin = proceeds - cost;
        var fraction = cost == 0 ? 0 : margin / cost;
        return (margin, fraction);
    }

    /// <summary>Мгновенный развоз: купить чужой sell, продать в чужой buy. Брокерская не берётся.</summary>
    public static (decimal Margin, decimal Fraction) InstantHaul(decimal ask, decimal bid, decimal salesTax)
    {
        var margin = bid * (1m - salesTax) - ask;
        var fraction = ask == 0 ? 0 : margin / ask;
        return (margin, fraction);
    }

    /// <summary>Купили по ask, на месте назначения выставили sell. Брокерская и налог с продажи.</summary>
    public static (decimal Margin, decimal Fraction) HaulAndList(
        decimal ask, decimal listSell, decimal salesTax, decimal brokerFee)
    {
        var proceeds = listSell * (1m - brokerFee - salesTax);
        var margin = proceeds - ask;
        var fraction = ask == 0 ? 0 : margin / ask;
        return (margin, fraction);
    }

    public static int Relists(decimal margin, decimal sell, decimal brokerFee)
    {
        var fee = RelistRate(brokerFee) * sell;
        if (fee <= 0 || margin <= 0)
            return 0;
        var count = decimal.Floor(margin / fee);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static decimal RelistRate(decimal brokerFee)
    {
        const decimal advancedBrokerRelations = 0m;
        var kept = 1m - (0.50m + 0.06m * advancedBrokerRelations);
        return kept < 0m ? 0m : kept * brokerFee;
    }

    /// <summary>Доля эскроу buy-ордера при Margin Trading (L0=1, L5=0.75).</summary>
    public static decimal MarginEscrowFraction(int marginTradingLevel)
    {
        var level = Math.Clamp(marginTradingLevel, 0, 5);
        return 1m - 0.05m * level;
    }

    public static decimal BuyEscrow(decimal buyPrice, long units, int marginTradingLevel) =>
        buyPrice * units * MarginEscrowFraction(marginTradingLevel);

    /// <summary>Слоты рыночных ордеров: 5 + Trade×4 + Retail×8 + Wholesale×16 + Tycoon×32.</summary>
    public static int MarketSlots(int trade, int retail, int wholesale, int tycoon) =>
        5
        + 4 * Math.Clamp(trade, 0, 5)
        + 8 * Math.Clamp(retail, 0, 5)
        + 16 * Math.Clamp(wholesale, 0, 5)
        + 32 * Math.Clamp(tycoon, 0, 5);

    public static decimal RelistFeePerMove(decimal sell, decimal brokerFee) =>
        RelistRate(brokerFee) * sell;

    /// <summary>Чистая маржа с учётом типичных перестановок в день.</summary>
    public static decimal NetMarginAfterRelists(decimal margin, decimal sell, decimal brokerFee, int movesPerDay) =>
        margin - RelistFeePerMove(sell, brokerFee) * Math.Max(0, movesPerDay);
}
