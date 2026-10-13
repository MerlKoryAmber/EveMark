using System.Globalization;
using JitaWatch;
using Microsoft.Extensions.Options;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--test-portfolio", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(PortfolioSelfTest.Run());
            return;
        }

        if (args.Any(a => string.Equals(a, "--test-haul", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(HaulSelfTest.Run());
            return;
        }

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
        builder.Services.AddSingleton<WatchPollService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchPollService>());

        var app = builder.Build();
        var dbBoot = app.Services.GetRequiredService<MarketDb>();
        dbBoot.InitAsync().GetAwaiter().GetResult();
        dbBoot.EnsureHaulTablesAsync().GetAwaiter().GetResult();

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
        app.MapPost("/api/settings", async (FeeSettings input, MarketDb db, CollectorService collector, CancellationToken ct) =>
        {
            if (input.Accounting is < 0 or > 5 || input.BrokerRelations is < 0 or > 5)
                return Results.BadRequest(new { error = "Скиллы от 0 до 5" });
            if (input.FactionStanding is < -10 or > 10 || input.CorpStanding is < -10 or > 10)
                return Results.BadRequest(new { error = "Стендинг от −10 до 10" });
            if (input.KeepDays is < 1 or > 400)
                return Results.BadRequest(new { error = "Срок хранения от 1 до 400 дней" });
            if (input.MinSellPrice < 0 || input.MinHaulSellPrice < 0)
                return Results.BadRequest(new { error = "Пороги цен ≥ 0" });
            await db.SaveSettingsAsync(input, ct);
            await collector.ApplyNameFiltersAsync(input, ct);
            await db.PurgeAsync(input.KeepDays, shrink: true, ct);
            return Results.Ok(await db.GetSettingsAsync(ct));
        });
        app.MapPost("/api/collect", async (CollectorService collector) =>
        {
            if (!await collector.TriggerAsync())
                return Results.Conflict(new { started = false, error = "Снятие уже идёт" });
            return Results.Json(new { started = true, state = "orders", detail = "запуск съёма…" }, statusCode: StatusCodes.Status202Accepted);
        });
        app.MapGet("/api/watch", (MarketDb db, CancellationToken ct) => db.ListWatchAsync(ct));
        app.MapPost("/api/watch", async (WatchAddRequest body, MarketDb db, CancellationToken ct) =>
        {
            if (body.TypeId <= 0 || body.LocationId <= 0 || body.RegionId <= 0)
                return Results.BadRequest(new { error = "Нужны typeId, locationId, regionId" });
            await db.AddWatchAsync(body.TypeId, body.LocationId, body.RegionId, body.Note, ct);
            return Results.Ok(await db.ListWatchAsync(ct));
        });
        app.MapPost("/api/watch/batch", async (WatchBatchRequest body, MarketDb db, CancellationToken ct) =>
        {
            if (body.LocationId <= 0 || body.RegionId <= 0)
                return Results.BadRequest(new { error = "Нужны locationId, regionId" });
            var ids = (body.TypeIds ?? []).Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0)
                return Results.BadRequest(new { error = "Пустой список typeIds" });
            foreach (var id in ids)
                await db.AddWatchAsync(id, body.LocationId, body.RegionId, body.Note, ct);
            return Results.Ok(new { added = ids.Count, items = await db.ListWatchAsync(ct) });
        });
        app.MapDelete("/api/watch", async (int typeId, long locationId, MarketDb db, CancellationToken ct) =>
        {
            await db.RemoveWatchAsync(typeId, locationId, ct);
            return Results.Ok(await db.ListWatchAsync(ct));
        });
        app.MapPost("/api/watch/poll", async (WatchPollService poll, CancellationToken ct) =>
        {
            await poll.PollOnceAsync(ct);
            return Results.Accepted();
        });
        app.MapGet("/api/portfolio", async (
            MarketDb db,
            long? capital,
            string? slotsMode,
            int? slots,
            int? skillTrade,
            int? skillRetail,
            int? skillWholesale,
            int? skillTycoon,
            int? slotsUsed,
            decimal? horizonDays,
            decimal? maxShareItem,
            decimal? maxMarketShare,
            long? minProfitDay,
            int? minHours,
            int? maxRelistsDay,
            bool? jita,
            bool? perimeter,
            CancellationToken ct) =>
        {
            var saved = await db.GetPortfolioSettingsAsync(ct);
            var settings = saved with
            {
                Capital = capital ?? saved.Capital,
                SlotsMode = slotsMode ?? saved.SlotsMode,
                Slots = slots ?? saved.Slots,
                SkillTrade = skillTrade ?? saved.SkillTrade,
                SkillRetail = skillRetail ?? saved.SkillRetail,
                SkillWholesale = skillWholesale ?? saved.SkillWholesale,
                SkillTycoon = skillTycoon ?? saved.SkillTycoon,
                SlotsUsed = slotsUsed ?? saved.SlotsUsed,
                HorizonDays = horizonDays ?? saved.HorizonDays,
                MaxShareItem = maxShareItem ?? saved.MaxShareItem,
                MaxMarketShare = maxMarketShare ?? saved.MaxMarketShare,
                MinProfitDay = minProfitDay ?? saved.MinProfitDay,
                MinHours = minHours ?? saved.MinHours,
                MaxRelistsDay = maxRelistsDay ?? saved.MaxRelistsDay,
                SourceJita = jita ?? saved.SourceJita,
                SourcePerimeter = perimeter ?? saved.SourcePerimeter
            };
            if (!settings.SourceJita && !settings.SourcePerimeter)
                return Results.BadRequest(new { error = "Включите Jita или Perimeter" });
            var fees = await db.GetSettingsAsync(ct);
            var sourced = new List<(BoardRow Row, string Location)>();
            if (settings.SourceJita)
            {
                var rows = await db.LoadBoardRowsAsync(new BoardSources(true, false), ct);
                sourced.AddRange(rows.Select(r => (r, "jita")));
            }

            if (settings.SourcePerimeter)
            {
                var rows = await db.LoadBoardRowsAsync(new BoardSources(false, true), ct);
                sourced.AddRange(rows.Select(r => (r, "perimeter")));
            }

            var taken = await db.GetLastSnapshotUtcAsync(ct);
            var stamp = taken?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return Results.Ok(PortfolioBuilder.Build(settings, sourced, fees.BrokerFee, stamp));
        });
        app.MapPost("/api/portfolio/settings", async (PortfolioSettings body, MarketDb db, CancellationToken ct) =>
        {
            if (body.Capital < 0 || body.Slots < 0 || body.SlotsUsed < 0)
                return Results.BadRequest(new { error = "Капитал и слоты ≥ 0" });
            if (body.HorizonDays <= 0 || body.HorizonDays > 30)
                return Results.BadRequest(new { error = "Горизонт 0…30 суток" });
            await db.SavePortfolioSettingsAsync(body, ct);
            return Results.Ok(await db.GetPortfolioSettingsAsync(ct));
        });
        app.MapPost("/api/portfolio/exclude", async (PortfolioExcludeRequest body, MarketDb db, CancellationToken ct) =>
        {
            if (body.TypeId <= 0)
                return Results.BadRequest(new { error = "typeId" });
            await db.SetPortfolioExcludeAsync(body.TypeId, body.Excluded, ct);
            return Results.Ok(await db.GetPortfolioSettingsAsync(ct));
        });
        app.MapGet("/api/haul/settings", (MarketDb db, CancellationToken ct) => db.GetHaulSettingsAsync(ct));
        app.MapPost("/api/haul/settings", async (HaulSettings body, MarketDb db, CancellationToken ct) =>
        {
            if (body.CargoM3 <= 0)
                return Results.BadRequest(new { error = "Трюм м³" });
            await db.SaveHaulSettingsAsync(body, ct);
            return Results.Ok(await db.GetHaulSettingsAsync(ct));
        });
        app.MapGet("/api/haul/trips", async (
            MarketDb db, EsiClient esi, CancellationToken ct,
            string? from, string? to, string? shipPreset, decimal? cargoM3,
            long? budget, long? maxCargoValue, string? mode, string? routeFlag,
            decimal? listHorizonDays, decimal? maxMarketShare, long? minItemProfit,
            int? secPerJump) =>
        {
            var saved = await db.GetHaulSettingsAsync(ct);
            var s = MergeHaul(saved, from, to, shipPreset, cargoM3, budget, maxCargoValue, mode, routeFlag,
                listHorizonDays, maxMarketShare, minItemProfit, secPerJump);
            var dto = await db.BuildHaulTripsAsync(s, esi, includeRowsFor: false, null, null, ct);
            return Results.Ok(dto);
        });
        app.MapGet("/api/haul/trip", async (
            MarketDb db, EsiClient esi, CancellationToken ct,
            string from, string to, string? shipPreset, decimal? cargoM3,
            long? budget, long? maxCargoValue, string? mode, string? routeFlag,
            decimal? listHorizonDays, decimal? maxMarketShare, long? minItemProfit,
            int? secPerJump) =>
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return Results.BadRequest(new { error = "from и to" });
            var saved = await db.GetHaulSettingsAsync(ct);
            var s = MergeHaul(saved, from, to, shipPreset, cargoM3, budget, maxCargoValue, mode, routeFlag,
                listHorizonDays, maxMarketShare, minItemProfit, secPerJump);
            var dto = await db.BuildHaulTripsAsync(s, esi, includeRowsFor: true, from, to, ct);
            var trip = dto.Trips.FirstOrDefault();
            if (trip is null)
                return Results.Ok(new { dto.SnapshotTime, dto.SnapshotAgeMin, dto.Inputs, trip = (object?)null, multibuy = "", csv = "" });
            var rows = trip.Rows ?? [];
            return Results.Ok(new
            {
                dto.SnapshotTime,
                dto.SnapshotAgeMin,
                dto.Inputs,
                trip,
                multibuy = HaulPlanner.Multibuy(rows),
                csv = HaulPlanner.Csv(rows)
            });
        });
        app.MapPost("/api/haul/pack", async (HaulPackRequest body, MarketDb db, EsiClient esi, CancellationToken ct) =>
        {
            var empty = new HaulFilter(null, default, default, default, default, default, default, null, null);
            var haul = await db.GetHaulAsync(1, "marginIsk", "desc", empty, ct);
            var all = haul.Rows.ToList();
            for (var p = 2; p <= Math.Min(haul.Pages, 5); p++)
                all.AddRange((await db.GetHaulAsync(p, "marginIsk", "desc", empty, ct)).Rows);

            if (body.HighSecOnly)
            {
                var kept = new List<HaulRow>();
                foreach (var row in all.Take(80))
                {
                    var from = SystemForLocation(row.FromLocationId, row.FromName);
                    var to = SystemForLocation(row.ToLocationId, row.ToName);
                    if (from is null || to is null)
                    {
                        if (row.Jumps <= 25)
                            kept.Add(row);
                        continue;
                    }

                    var route = await esi.FetchRouteSystemsAsync(from.Value, to.Value, secure: true, ct);
                    if (route is null)
                        continue;
                    kept.Add(row with { Jumps = Math.Max(0, route.Count - 1) });
                }

                all = kept;
            }

            var pack = HaulPacker.Pack(all, body);
            if (pack.Items.Count == 0)
                return Results.Ok(pack);

            var enriched = new List<HaulPackItem>();
            foreach (var item in pack.Items)
            {
                var src = all.FirstOrDefault(r => r.TypeId == item.TypeId && r.FromName == item.FromName && r.ToName == item.ToName);
                var from = src is null ? null : SystemForLocation(src.FromLocationId, src.FromName);
                var to = src is null ? null : SystemForLocation(src.ToLocationId, src.ToName);
                IReadOnlyList<string> gank = [];
                if (from is not null && to is not null)
                {
                    var route = await esi.FetchRouteSystemsAsync(from.Value, to.Value, secure: body.HighSecOnly, ct);
                    if (route is not null)
                        gank = GankSystems.OnRoute(route);
                }

                enriched.Add(item with { GankSystems = gank });
            }

            var multibuy = string.Join("\n", enriched.Select(p => $"{p.Name} {p.Units.ToString(CultureInfo.InvariantCulture)}"));
            return Results.Ok(pack with { Items = enriched, Multibuy = multibuy });
        });
        app.MapPost("/api/refine", async (RefineRequest body, MarketDb db, CancellationToken ct) =>
        {
            if (body.OreTypeId <= 0 || body.Units < 1)
                return Results.BadRequest(new { error = "oreTypeId и units" });
            var board = await db.LoadBoardRowsAsync(BoardSources.Both, ct);
            var prices = board.ToDictionary(r => r.TypeId, r => r.BestSell);
            var dto = RefineTable.Evaluate(body.OreTypeId, body.Units, body.Efficiency <= 0 ? 0.52m : body.Efficiency, prices);
            if (dto is null)
                return Results.BadRequest(new { error = "Руда не в мини-таблице", known = RefineTable.KnownOreIds });
            return Results.Ok(dto);
        });
        app.MapGet("/api/refine/ores", () => RefineTable.KnownOreIds);
        app.MapGet("/api/contracts", async (EsiClient esi, MarketDb db, int? regionId, CancellationToken ct) =>
        {
            var region = regionId is > 0 ? regionId.Value : TradeHubs.ForgeRegionId;
            var contracts = await esi.FetchPublicContractsAsync(region, ct);
            var board = await db.LoadBoardRowsAsync(BoardSources.Both, ct);
            var sell = board.ToDictionary(r => r.TypeId, r => r.BestSell);
            var deals = new List<ContractDeal>();
            foreach (var c in contracts.Where(x => x.Type == "item_exchange").Take(30))
            {
                var items = await esi.FetchContractItemsAsync(c.ContractId, ct);
                var included = items.Where(i => i.IsIncluded && i.Quantity > 0).ToList();
                if (included.Count is < 1 or > 3)
                    continue;
                foreach (var it in included)
                {
                    if (!sell.TryGetValue(it.TypeId, out var marketSell) || marketSell <= 0)
                        continue;
                    var value = marketSell * it.Quantity;
                    var edge = value - c.Price;
                    if (edge <= 0)
                        continue;
                    var name = board.FirstOrDefault(b => b.TypeId == it.TypeId)?.Name ?? "#" + it.TypeId;
                    deals.Add(new ContractDeal(
                        c.ContractId, c.Title ?? "", c.Price, it.TypeId, name, it.Quantity, marketSell, edge, c.Price > 0 ? edge / c.Price : 0));
                }
            }

            return Results.Ok(deals.OrderByDescending(d => d.EdgeIsk).Take(50).ToList());
        });
        app.MapGet("/api/notes", (MarketDb db, CancellationToken ct) => db.ListNotesAsync(ct));
        app.MapPost("/api/notes", async (NoteRequest body, MarketDb db, CancellationToken ct) =>
        {
            if (body.TypeId <= 0)
                return Results.BadRequest(new { error = "typeId" });
            await db.SetTypeNoteAsync(body.TypeId, body.Note, body.Blacklisted, ct);
            return Results.Ok(await db.ListNotesAsync(ct));
        });
        app.MapGet("/api/backtest/shout", (MarketDb db, int? days, CancellationToken ct) =>
            db.BacktestShoutAsync(days ?? 30, ct));
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

    private sealed record WatchAddRequest(int TypeId, long LocationId, int RegionId, string? Note);
    private sealed record WatchBatchRequest(IReadOnlyList<int>? TypeIds, long LocationId, int RegionId, string? Note);
    private sealed record NoteRequest(int TypeId, string? Note, bool? Blacklisted);

    private static HaulSettings MergeHaul(
        HaulSettings saved,
        string? from, string? to, string? shipPreset, decimal? cargoM3,
        long? budget, long? maxCargoValue, string? mode, string? routeFlag,
        decimal? listHorizonDays, decimal? maxMarketShare, long? minItemProfit, int? secPerJump)
    {
        var preset = shipPreset ?? saved.ShipPreset;
        var cargo = cargoM3 ?? saved.CargoM3;
        if (cargoM3 is null && shipPreset is not null && HaulSettings.PresetM3(preset) > 0)
            cargo = HaulSettings.PresetM3(preset);
        return saved with
        {
            From = from ?? saved.From,
            To = to ?? saved.To,
            ShipPreset = preset,
            CargoM3 = cargo,
            Budget = budget ?? saved.Budget,
            MaxCargoValue = maxCargoValue ?? saved.MaxCargoValue,
            Mode = mode ?? saved.Mode,
            RouteFlag = routeFlag ?? saved.RouteFlag,
            ListHorizonDays = listHorizonDays ?? saved.ListHorizonDays,
            MaxMarketShare = maxMarketShare ?? saved.MaxMarketShare,
            MinItemProfit = minItemProfit ?? saved.MinItemProfit,
            SecPerJump = secPerJump ?? saved.SecPerJump
        };
    }

    private static int? SystemForLocation(long locationId, string? name)
    {
        var hub = TradeHubs.NpcByLocation(locationId);
        if (hub is not null)
            return hub.SystemId;
        if (TradeHubs.IsPerimeterName(name))
            return TradeHubs.PerimeterSystemId;
        return null;
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
