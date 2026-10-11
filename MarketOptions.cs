namespace JitaWatch;

public sealed class MarketOptions
{
    public int RegionId { get; set; } = 10000002;

    public long LocationId { get; set; } = 60003760;

    public decimal MinSellPrice { get; set; } = 1_000_000m;

    /// <summary>Минимальная цена sell для строк развоза (location_quote).</summary>
    public decimal MinHaulSellPrice { get; set; } = 500_000m;

    public int PollHours { get; set; } = 1;

    public int KeepDays { get; set; } = 120;
}
