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

    /// <summary>Интервал опроса watchlist (минуты).</summary>
    public int WatchPollMinutes { get; set; } = 5;

    /// <summary>Окно часов для подсчёта конкурентов (уникальные order_id, двигавшие лучшую цену).</summary>
    public int CaptureHours { get; set; } = 6;
}
