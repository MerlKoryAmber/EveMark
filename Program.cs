using System.Globalization;
using JitaWatch;
using Microsoft.Extensions.Options;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var home = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var dbPath = Path.Combine(home, "data", "jita.db");
        if (!File.Exists(dbPath) && !AgreeToCreateDatabase(dbPath))
            return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = home
        });
        builder.WebHost.UseUrls("http://127.0.0.1:8741");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLogProvider(Path.Combine(home, "data", "jita.log")));
        builder.Services.Configure<MarketOptions>(builder.Configuration.GetSection("Market"));

        builder.Services.AddSingleton(sp =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JitaWatch/0.1 (local market desk)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return new EsiClient(http, sp.GetRequiredService<ILogger<EsiClient>>());
        });
        builder.Services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IWebHostEnvironment>();
            return new MarketDb(Path.Combine(env.ContentRootPath, "data", "jita.db"));
        });
        builder.Services.AddSingleton<CollectorService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorService>());

        var app = builder.Build();
        app.Services.GetRequiredService<MarketDb>().InitAsync().GetAwaiter().GetResult();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/status", async (MarketDb db, IOptions<MarketOptions> options, CancellationToken ct) =>
        {
            var status = await db.GetStatusAsync(ct);
            string? next = null;
            if (status.LastSnapshotAt is not null
                && DateTime.TryParse(status.LastSnapshotAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
            {
                var due = last.ToUniversalTime().AddHours(Math.Max(1, options.Value.PollHours));
                next = due.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }

            return new StatusDto
            {
                State = status.State,
                Detail = status.Detail,
                LastSnapshotAt = status.LastSnapshotAt,
                NextSnapshotAt = next,
                LastError = status.LastError,
                LatestTypes = status.LatestTypes
            };
        });
        app.MapGet("/api/settings", (MarketDb db, CancellationToken ct) => db.GetSettingsAsync(ct));
        app.MapPost("/api/settings", async (FeeSettings input, MarketDb db, CancellationToken ct) =>
        {
            if (input.Accounting is < 0 or > 5 || input.BrokerRelations is < 0 or > 5)
                return Results.BadRequest(new { error = "Скиллы от 0 до 5" });
            if (input.FactionStanding is < -10 or > 10 || input.CorpStanding is < -10 or > 10)
                return Results.BadRequest(new { error = "Стендинг от −10 до 10" });
            if (input.KeepDays is < 1 or > 400)
                return Results.BadRequest(new { error = "Срок хранения от 1 до 400 дней" });
            await db.SaveSettingsAsync(input, ct);
            await db.PurgeAsync(input.KeepDays, shrink: true, ct);
            return Results.Ok(await db.GetSettingsAsync(ct));
        });
        app.MapPost("/api/collect", async (CollectorService collector) =>
            await collector.TriggerAsync()
                ? Results.Accepted()
                : Results.Conflict(new { error = "Снятие уже идёт" }));
        app.MapGet("/api/signals", (
            MarketDb db,
            int? page,
            string? sort,
            string? dir,
            string? name,
            string? bestBuyMin,
            string? bestBuyMax,
            string? bestSellMin,
            string? bestSellMax,
            string? marginIskMin,
            string? marginIskMax,
            string? marginFracMin,
            string? marginFracMax,
            string? dailyVolumeMin,
            string? dailyVolumeMax,
            string? dailyIskMin,
            string? dailyIskMax,
            string? kind,
            string? hoursMin,
            string? hoursMax,
            string? averageMin,
            string? averageMax,
            string? buyMovesMin,
            string? buyMovesMax,
            string? sellMovesMin,
            string? sellMovesMax,
            string? marginThenMin,
            string? marginThenMax,
            string? thin,
            CancellationToken ct) =>
            db.GetSignalsAsync(
                page ?? 1,
                sort,
                dir,
                kind,
                new ColumnFilter(
                    name,
                    NumRange.Parse(bestBuyMin, bestBuyMax),
                    NumRange.Parse(bestSellMin, bestSellMax),
                    NumRange.Parse(marginIskMin, marginIskMax),
                    NumRange.Parse(marginFracMin, marginFracMax),
                    NumRange.Parse(dailyVolumeMin, dailyVolumeMax),
                    NumRange.Parse(dailyIskMin, dailyIskMax),
                    NumRange.Parse(hoursMin, hoursMax),
                    default,
                    default,
                    NumRange.Parse(averageMin, averageMax),
                    NumRange.Parse(buyMovesMin, buyMovesMax),
                    NumRange.Parse(sellMovesMin, sellMovesMax),
                    NumRange.Parse(marginThenMin, marginThenMax),
                    thin,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default),
                ct));
        app.MapGet("/api/haul", (
            MarketDb db,
            int? page,
            string? sort,
            string? dir,
            string? name,
            string? marginIskMin,
            string? marginIskMax,
            string? marginFracMin,
            string? marginFracMax,
            string? unitsMin,
            string? unitsMax,
            string? dailyVolumeMin,
            string? dailyVolumeMax,
            string? jumpsMin,
            string? jumpsMax,
            string? dailyIskMin,
            string? dailyIskMax,
            string? fromHub,
            string? toHub,
            CancellationToken ct) =>
            db.GetHaulAsync(
                page ?? 1,
                sort,
                dir,
                new HaulFilter(
                    name,
                    NumRange.Parse(marginIskMin, marginIskMax),
                    NumRange.Parse(marginFracMin, marginFracMax),
                    NumRange.Parse(unitsMin, unitsMax),
                    NumRange.Parse(dailyVolumeMin, dailyVolumeMax),
                    NumRange.Parse(jumpsMin, jumpsMax),
                    NumRange.Parse(dailyIskMin, dailyIskMax),
                    fromHub,
                    toHub),
                ct));
        app.MapGet("/api/recommendations", (
            MarketDb db,
            int? page,
            string? sort,
            string? dir,
            string? name,
            string? bestBuyMin,
            string? bestBuyMax,
            string? bestSellMin,
            string? bestSellMax,
            string? marginIskMin,
            string? marginIskMax,
            string? marginFracMin,
            string? marginFracMax,
            string? dailyVolumeMin,
            string? dailyVolumeMax,
            string? dailyIskMin,
            string? dailyIskMax,
            string? hoursMin,
            string? hoursMax,
            string? buyVolumeMin,
            string? buyVolumeMax,
            string? sellVolumeMin,
            string? sellVolumeMax,
            string? buyTouchMin,
            string? buyTouchMax,
            string? sellTouchMin,
            string? sellTouchMax,
            string? relistsMin,
            string? relistsMax,
            string? capitalMin,
            string? capitalMax,
            string? daysMin,
            string? daysMax,
            string? dailyRoiMin,
            string? dailyRoiMax,
            bool? jita,
            bool? perimeter,
            CancellationToken ct) =>
            db.GetBoardAsync(
                page ?? 1,
                sort,
                dir,
                new ColumnFilter(
                    name,
                    NumRange.Parse(bestBuyMin, bestBuyMax),
                    NumRange.Parse(bestSellMin, bestSellMax),
                    NumRange.Parse(marginIskMin, marginIskMax),
                    NumRange.Parse(marginFracMin, marginFracMax),
                    NumRange.Parse(dailyVolumeMin, dailyVolumeMax),
                    NumRange.Parse(dailyIskMin, dailyIskMax),
                    NumRange.Parse(hoursMin, hoursMax),
                    NumRange.Parse(buyVolumeMin, buyVolumeMax),
                    NumRange.Parse(sellVolumeMin, sellVolumeMax),
                    default,
                    default,
                    default,
                    default,
                    null,
                    NumRange.Parse(buyTouchMin, buyTouchMax),
                    NumRange.Parse(sellTouchMin, sellTouchMax),
                    NumRange.Parse(relistsMin, relistsMax),
                    NumRange.Parse(capitalMin, capitalMax),
                    NumRange.Parse(daysMin, daysMax),
                    NumRange.Parse(dailyRoiMin, dailyRoiMax)),
                BoardSources.Parse(jita, perimeter),
                ct));

        app.Lifetime.ApplicationStarted.Register(TrayContext.OpenPanel);

        var server = Task.Run(async () =>
        {
            await app.StartAsync();
            await app.WaitForShutdownAsync();
        });

        Application.Run(new TrayContext(app));
        app.StopAsync().GetAwaiter().GetResult();
        server.Wait(TimeSpan.FromSeconds(8));
    }

    private static bool AgreeToCreateDatabase(string dbPath)
    {
        var answer = MessageBox.Show(
            "Рядом с программой нет базы. Создать новую?",
            "MK Jitawatch",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return false;

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = dbPath + suffix;
            if (File.Exists(side))
                File.Delete(side);
        }

        return true;
    }
}
