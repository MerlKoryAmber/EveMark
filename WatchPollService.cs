using System.Globalization;
using Microsoft.Extensions.Options;

namespace JitaWatch;

/// <summary>Опрос watchlist каждые N минут: ордера с ETag, дельта volume_remain → сделки.</summary>
public sealed class WatchPollService : BackgroundService
{
    private readonly MarketDb _db;
    private readonly EsiClient _esi;
    private readonly MarketOptions _options;
    private readonly ILogger<WatchPollService> _log;
    private int _running;

    public WatchPollService(
        MarketDb db,
        EsiClient esi,
        IOptions<MarketOptions> options,
        ILogger<WatchPollService> log)
    {
        _db = db;
        _esi = esi;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Clamp(_options.WatchPollMinutes, 1, 60);
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Опрос watchlist оборвался");
            }

            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        try
        {
            var items = await _db.ListWatchAsync(ct);
            if (items.Count == 0)
                return;

            _log.LogInformation("Watchlist: опрос {Count} позиций", items.Count);
            foreach (var group in items.GroupBy(i => (i.RegionId, i.TypeId)))
            {
                ct.ThrowIfCancellationRequested();
                var regionId = group.Key.RegionId;
                var typeId = group.Key.TypeId;
                var locations = group.Select(g => g.LocationId).ToHashSet();
                var cacheKey = $"orders:{regionId.ToString(CultureInfo.InvariantCulture)}:{typeId.ToString(CultureInfo.InvariantCulture)}";
                var etag = await _db.GetEtagAsync(cacheKey, ct);
                TypeOrdersResult result;
                try
                {
                    result = await _esi.FetchTypeOrdersAsync(regionId, typeId, etag, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Watch type {TypeId} region {RegionId}", typeId, regionId);
                    continue;
                }

                if (result.NotModified)
                    continue;

                if (!string.IsNullOrEmpty(result.Etag))
                    await _db.SetEtagAsync(cacheKey, result.Etag, ct);

                foreach (var locationId in locations)
                {
                    var atLoc = result.Orders.Where(o => o.LocationId == locationId).ToList();
                    await _db.ApplyWatchBookAsync(typeId, locationId, atLoc, ct);
                }
            }

            await _db.PurgeWatchNoiseAsync(_options.KeepDays, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
