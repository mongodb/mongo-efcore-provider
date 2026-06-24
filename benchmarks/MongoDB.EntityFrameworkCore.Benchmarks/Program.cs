using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Benchmarks;

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

BenchmarkDotNet.Running.BenchmarkRunner.Run<HeadlineBenchmarks>();
