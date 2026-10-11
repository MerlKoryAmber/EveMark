using System.Globalization;
using System.Text.Json.Serialization;

namespace JitaWatch;

public sealed class Book
{
    private const decimal Band = 0.01m;
    private readonly List<(bool IsBuy, decimal Price, long Volume)> _orders = [];

    public decimal? BestBuy { get; private set; }
    public decimal BestSell { get; private set; }
    public long BuyVolume { get; private set; }
    public long SellVolume { get; private set; }
    public long BuyTouch { get; private set; }
    public long SellTouch { get; private set; }
    public int BuyOrders { get; private set; }
    public int SellOrders { get; private set; }
    public bool HasSell { get; private set; }

    public void Add(bool isBuy, decimal price, long volume)
    {
        if (price <= 0 || volume <= 0)
            return;
        _orders.Add((isBuy, price, volume));
    }

    public void Finish()
    {
        foreach (var (isBuy, price, _) in _orders)
        {
            if (isBuy)
            {
                if (BestBuy is null || price > BestBuy)
                    BestBuy = price;
            }
            else if (!HasSell || price < BestSell)
            {
                BestSell = price;
                HasSell = true;
            }
        }

        foreach (var (isBuy, price, volume) in _orders)
        {
            if (isBuy && BestBuy is decimal buy && price >= buy * (1m - Band))
            {
                BuyVolume += volume;
                BuyOrders++;
            }
            else if (!isBuy && HasSell && price <= BestSell * (1m + Band))
            {
                SellVolume += volume;
                SellOrders++;
            }

            if (isBuy && BestBuy is decimal touch && price == touch)
                BuyTouch += volume;
            else if (!isBuy && HasSell && price == BestSell)
                SellTouch += volume;
        }

        _orders.Clear();
    }
}

public sealed class HistoryBar
{
    [JsonPropertyName("average")]
    public decimal Average { get; set; }

    [JsonPropertyName("date")]
    public DateOnly Date { get; set; }

    [JsonPropertyName("highest")]
    public decimal Highest { get; set; }

    [JsonPropertyName("lowest")]
    public decimal Lowest { get; set; }

    [JsonPropertyName("order_count")]
    public long OrderCount { get; set; }

    [JsonPropertyName("volume")]
    public long Volume { get; set; }
}

public sealed class EsiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EsiClient> _log;

    public EsiClient(HttpClient http, ILogger<EsiClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<Dictionary<int, Book>> FetchStationBooksAsync(
        int regionId,
        long locationId,
        Action<string> progress,
        CancellationToken ct)
    {
        var books = new Dictionary<int, Book>();
        var page = 1;
        var pages = 1;
        do
        {
            var url =
                $"https://esi.evetech.net/latest/markets/{regionId}/orders/?datasource=tranquility&order_type=all&page={page.ToString(CultureInfo.InvariantCulture)}";
            using var resp = await SendAsync(url, ct);
            if (page == 1 && resp.Headers.TryGetValues("X-Pages", out var values) &&
                int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                pages = Math.Max(1, parsed);
            }

            var orders = await resp.Content.ReadFromJsonAsync<List<OrderDto>>(cancellationToken: ct) ?? [];
            foreach (var order in orders)
            {
                if (order.LocationId != locationId || order.VolumeRemain <= 0 || order.Price <= 0)
                    continue;

                if (!books.TryGetValue(order.TypeId, out var book))
                {
                    book = new Book();
                    books[order.TypeId] = book;
                }

                book.Add(order.IsBuyOrder, order.Price, order.VolumeRemain);
            }

            progress($"ордера, страница {page} из {pages}");
            page++;
        }
        while (page <= pages);

        foreach (var book in books.Values)
            book.Finish();

        _log.LogInformation("Ордера The Forge разобраны, типов на станции: {Count}", books.Count);
        return books;
    }

    public async Task<Dictionary<int, string>> FetchNamesAsync(IReadOnlyList<int> typeIds, CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        foreach (var chunk in typeIds.Distinct().Chunk(500))
            await ReadNamesAsync(chunk, names, ct);
        return names;
    }

    private async Task ReadNamesAsync(IReadOnlyList<int> typeIds, Dictionary<int, string> names, CancellationToken ct)
    {
        if (typeIds.Count == 0)
            return;

        using var resp = await PostNamesAsync(typeIds, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (typeIds.Count == 1)
            {
                _log.LogWarning("ESI не отдал имя {TypeId}: {Body}", typeIds[0], body);
                return;
            }

            var mid = typeIds.Count / 2;
            await ReadNamesAsync(typeIds.Take(mid).ToArray(), names, ct);
            await ReadNamesAsync(typeIds.Skip(mid).ToArray(), names, ct);
            return;
        }

        resp.EnsureSuccessStatusCode();
        var rows = await resp.Content.ReadFromJsonAsync<List<NameDto>>(cancellationToken: ct) ?? [];
        foreach (var row in rows)
        {
            if (row.Id > 0 && !string.IsNullOrWhiteSpace(row.Name))
                names[row.Id] = row.Name;
        }
    }

    public async Task<string?> FetchTypeNameAsync(int typeId, CancellationToken ct)
    {
        var url =
            $"https://esi.evetech.net/latest/universe/types/{typeId.ToString(CultureInfo.InvariantCulture)}/?datasource=tranquility&language=ru";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode)
        {
            await PauseIfNeededAsync(resp, ct);
            resp.EnsureSuccessStatusCode();
        }

        await PauseIfNeededAsync(resp, ct);
        var dto = await resp.Content.ReadFromJsonAsync<TypeDto>(cancellationToken: ct);
        return string.IsNullOrWhiteSpace(dto?.Name) ? null : dto.Name;
    }

    public async Task<IReadOnlyList<HistoryBar>> FetchHistoryAsync(int regionId, int typeId, CancellationToken ct)
    {
        var url =
            $"https://esi.evetech.net/latest/markets/{regionId}/history/?datasource=tranquility&type_id={typeId.ToString(CultureInfo.InvariantCulture)}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        if ((int)resp.StatusCode is 420 or 429 or 502 or 503 or 504)
        {
            using var retried = await SendAsync(url, ct);
            return await retried.Content.ReadFromJsonAsync<List<HistoryBar>>(cancellationToken: ct) ?? [];
        }

        resp.EnsureSuccessStatusCode();
        await PauseIfNeededAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<HistoryBar>>(cancellationToken: ct) ?? [];
    }

    private async Task<HttpResponseMessage> PostNamesAsync(IReadOnlyList<int> typeIds, CancellationToken ct)
    {
        var url = "https://esi.evetech.net/latest/universe/names/?datasource=tranquility";
        var json = System.Text.Json.JsonSerializer.Serialize(typeIds);
        for (var attempt = 1; ; attempt++)
        {
            using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, body, ct);
            if ((int)resp.StatusCode is 420 or 429 or 502 or 503 or 504 && attempt < 6)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            await PauseIfNeededAsync(resp, ct);
            return resp;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode is 420 or 429 or 502 or 503 or 504 && attempt < 6)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                resp.Dispose();
                _log.LogWarning("ESI {Status}, пауза {Seconds} с", (int)resp.StatusCode, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            await PauseIfNeededAsync(resp, ct);
            return resp;
        }
    }

    private static async Task PauseIfNeededAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Headers.TryGetValues("X-Esi-Error-Limit-Remain", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var remain) &&
            remain < 20)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class OrderDto
    {
        [JsonPropertyName("is_buy_order")]
        public bool IsBuyOrder { get; set; }

        [JsonPropertyName("location_id")]
        public long LocationId { get; set; }

        [JsonPropertyName("price")]
        public decimal Price { get; set; }

        [JsonPropertyName("type_id")]
        public int TypeId { get; set; }

        [JsonPropertyName("volume_remain")]
        public long VolumeRemain { get; set; }
    }

    private sealed class NameDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TypeDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
