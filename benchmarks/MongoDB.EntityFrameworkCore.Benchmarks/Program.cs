using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Benchmarks;
using MongoDB.EntityFrameworkCore.Infrastructure;

if (args.Contains("--verify-native"))
{
    var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    var dbName = "ef_bench_nativeverify_" + Guid.NewGuid().ToString("N");
    var client = new MongoClient(conn);

    var seedOptions = new DbContextOptionsBuilder<BenchmarkDbContext>()
        .UseMongoDB(client, dbName, o => o.UseQueryMode(MongoQueryMode.DriverLinq)).Options;
    var nativeOnlyOptions = new DbContextOptionsBuilder<BenchmarkDbContext>()
        .UseMongoDB(client, dbName, o => o.UseQueryMode(MongoQueryMode.NativeOnly)).Options;

    try
    {
        using (var ctx = new BenchmarkDbContext(seedOptions))
            BenchmarkSeeder.Seed(ctx, flatCount: 100, productCount: 20, reviewCount: 100);

        Console.WriteLine("=== NativeOnly verification ===");

        static void RunShape(string label, Action test, bool expectFallback = false)
        {
            try
            {
                test();
                if (expectFallback)
                    Console.WriteLine($"{label}: NATIVE OK <-- UNEXPECTED, should fall back!");
            }
            catch (Exception ex) when (ex.GetType().Name == "NativeTranslationNotSupportedException")
            {
                if (expectFallback)
                    Console.WriteLine($"{label}: FALLBACK CONFIRMED (NativeTranslationNotSupportedException)");
                else
                    Console.WriteLine($"{label}: FALLBACK (unexpected) — {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: ERROR — {ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        // WhereToList — should be NATIVE OK
        RunShape("WhereToList:      ", () =>
        {
            using var ctx = new BenchmarkDbContext(nativeOnlyOptions);
            var count = ctx.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count;
            Console.WriteLine($"WhereToList:       NATIVE OK (count={count})");
        });

        // WholeEntityToList — should be NATIVE OK
        RunShape("WholeEntityToList:", () =>
        {
            using var ctx = new BenchmarkDbContext(nativeOnlyOptions);
            var count = ctx.FlatItems.AsNoTracking().ToList().Count;
            Console.WriteLine($"WholeEntityToList: NATIVE OK (count={count})");
        });

        // OrderByTake — should be NATIVE OK
        RunShape("OrderByTake:      ", () =>
        {
            using var ctx = new BenchmarkDbContext(nativeOnlyOptions);
            var count = ctx.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(10).ToList().Count;
            Console.WriteLine($"OrderByTake:       NATIVE OK (count={count})");
        });

        // ReferenceInclude — expected to throw NativeTranslationNotSupportedException (Include deferred)
        RunShape("ReferenceInclude: ", () =>
        {
            using var ctx = new BenchmarkDbContext(nativeOnlyOptions);
            var count = ctx.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count;
        }, expectFallback: true);

        Console.WriteLine("=== verification done ===");
    }
    finally
    {
        using var ctx = new BenchmarkDbContext(seedOptions);
        ctx.Database.EnsureDeleted();
    }
    return;
}

if (args.Contains("--extended-smoke"))
{
    var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    var dbName = "ef_bench_extsmoke_" + Guid.NewGuid().ToString("N");
    var options = new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(conn, dbName).Options;
    try
    {
        using (var ctx = new BenchmarkDbContext(options))
            BenchmarkSeeder.SeedExtended(ctx, accountCount: 10, orderCount: 50, wideCount: 100);

        using (var ctx = new BenchmarkDbContext(options))
        {
            var orders = ctx.Orders.AsNoTracking().Include(o => o.Account).ToList();
            var totalLines = orders.Sum(o => o.Lines.Count);
            var totalDiscounts = orders.Sum(o => o.Lines.Sum(l => l.Discounts.Count));
            var withAccount = orders.Count(o => o.Account != null);
            var nestedZipOk = orders.All(o => o.Shipping.Address.Zip >= 10_000);
            var wides = ctx.Wides.AsNoTracking().ToList();

            Console.WriteLine(
                $"EXT SMOKE OK: orders={orders.Count}, lines={totalLines}, discounts={totalDiscounts}, " +
                $"withAccount={withAccount}, wides={wides.Count}");

            if (orders.Count != 50) throw new InvalidOperationException($"expected 50 orders, got {orders.Count}");
            if (totalLines != 150) throw new InvalidOperationException($"expected 150 lines, got {totalLines}");
            if (totalDiscounts != 300) throw new InvalidOperationException($"expected 300 discounts, got {totalDiscounts}");
            if (withAccount != 50) throw new InvalidOperationException($"expected 50 with Account, got {withAccount}");
            if (!nestedZipOk) throw new InvalidOperationException("nested owned Shipping.Address.Zip did not round-trip");

            var w0 = wides.Single(w => w.Prop001 == 1);       // Fill(0): Prop001 = 0 + 1
            if (w0.Prop005 != "p005-0") throw new InvalidOperationException($"WideEntity scalar wrong: Prop005={w0.Prop005}");
        }
    }
    finally
    {
        using var ctx = new BenchmarkDbContext(options);
        ctx.Database.EnsureDeleted();
    }
    return;
}

if (args.Contains("--smoke"))
{
    var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    var dbName = "ef_bench_smoke_" + Guid.NewGuid().ToString("N");
    var options = new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(conn, dbName).Options;

    try
    {
        using (var ctx = new BenchmarkDbContext(options))
        {
            BenchmarkSeeder.Seed(ctx, flatCount: 100, productCount: 20, reviewCount: 100);
        }

        using (var ctx = new BenchmarkDbContext(options))
        {
            var flat = ctx.FlatItems.AsNoTracking().ToList();
            var flatActive = ctx.FlatItems.AsNoTracking().Where(f => f.Active).ToList();
            var tracked = ctx.FlatItems.ToList();
            var reviews = ctx.Reviews.AsNoTracking().Include(r => r.Product).ToList();
            var withProduct = reviews.Count(r => r.Product != null);

            Console.WriteLine(
                $"SMOKE OK: flat={flat.Count}, flatActive={flatActive.Count}, tracked={tracked.Count}, " +
                $"reviews={reviews.Count}, withProduct={withProduct}");

            if (flat.Count != 100) throw new InvalidOperationException($"expected 100 flat, got {flat.Count}");
            if (flatActive.Count != 50) throw new InvalidOperationException($"expected 50 active, got {flatActive.Count}");
            if (reviews.Count != 100) throw new InvalidOperationException($"expected 100 reviews, got {reviews.Count}");
            if (withProduct != 100) throw new InvalidOperationException($"expected 100 with Product, got {withProduct}");

            // Verify scalar materialization, not just counts.
            var f0 = flat.Single(f => f.Count == 0);
            if (f0.Name != "flat-0" || f0.Big != 1_000_000_000L || f0.Active != true || f0.Rate != 0.0)
                throw new InvalidOperationException($"FlatItem[0] scalars wrong: Name={f0.Name}, Big={f0.Big}, Active={f0.Active}, Rate={f0.Rate}");
        }
    }
    finally
    {
        using var ctx = new BenchmarkDbContext(options);
        ctx.Database.EnsureDeleted();
    }
    return;
}

if (args.Contains("--extended"))
{
    BenchmarkDotNet.Running.BenchmarkRunner.Run<ExtendedBenchmarks>();
    return;
}

BenchmarkDotNet.Running.BenchmarkRunner.Run<HeadlineBenchmarks>();
