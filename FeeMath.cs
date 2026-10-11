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
}
