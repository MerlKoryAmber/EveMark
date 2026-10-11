namespace JitaWatch;

public static class TradeHubs
{
    public const long JitaStationId = 60003760;
    public const int JitaSystemId = 30000142;
    public const int PerimeterSystemId = 30000144;
    public const int ForgeRegionId = 10000002;

    public static readonly HubSpot Jita = new("jita", "Jita 4-4", JitaStationId, JitaSystemId, "Jita", ForgeRegionId);
    public static readonly HubSpot Amarr = new("amarr", "Amarr", 60008494, 30002187, "Amarr", 10000043);
    public static readonly HubSpot Dodixie = new("dodixie", "Dodixie", 60011866, 30002659, "Dodixie", 10000032);
    public static readonly HubSpot Rens = new("rens", "Rens", 60004588, 30002510, "Rens", 10000030);
    public static readonly HubSpot Hek = new("hek", "Hek", 60005686, 30002053, "Hek", 10000042);

    public static IReadOnlyList<HubSpot> NpcHubs { get; } = [Jita, Amarr, Dodixie, Rens, Hek];

    public static IReadOnlyList<int> MarketRegionIds { get; } =
        NpcHubs.Select(h => h.RegionId).Distinct().ToArray();

    public static bool IsPerimeterName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.StartsWith("Perimeter", StringComparison.Ordinal);

    public static HubSpot? NpcByLocation(long locationId) =>
        NpcHubs.FirstOrDefault(h => h.LocationId == locationId);
}

public sealed record HubSpot(
    string Key,
    string Title,
    long LocationId,
    int SystemId,
    string SystemName,
    int RegionId);
