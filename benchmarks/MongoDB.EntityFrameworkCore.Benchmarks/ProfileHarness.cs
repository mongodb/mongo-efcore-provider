using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Steady-state loop for attaching dotnet-trace / dotnet-counters, and for fixed-cost and scaling runs.
//   --profile <shape> <config> [--seconds N] [--rows N] [--warmup N]
// config: driver | driverlinq | native. Prints one machine-readable PROFILE line.
// rows applies to FlatItems and Reviews (products fixed at 100). Extended shapes use the headline-sized data
// (orders 10,000, wides 5,000, accounts 200) unless --rows is given, in which case orders and wides = rows.
internal static class ProfileHarness
{
    public static readonly string[] Shapes =
    {
        "WhereToList", "WhereNone", "WholeEntityToList", "WholeEntityTracked", "Take1", "OrderByTake",
        "ReferenceInclude", "ComplexQuery", "WideWholeEntity", "NestedWholeEntity",
        "WhereToListAsync", "WholeEntityToListAsync", "ReferenceIncludeAsync",
    };

    public static int Run(string[] args)
    {
        var i = Array.IndexOf(args, "--profile");
        var shape = args[i + 1];
        var config = args[i + 2];
        var seconds = Int(args, "--seconds", 30);
        var rows = Int(args, "--rows", 10_000);
        var warmup = Int(args, "--warmup", 200);
        var minOps = Int(args, "--minops", 1);
        var extended = shape is "ComplexQuery" or "WideWholeEntity" or "NestedWholeEntity";
        var extRows = args.Contains("--rows") ? rows : -1;

        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        var dbName = "ef_bench_profile_" + Guid.NewGuid().ToString("N");
        var settings = MongoClientSettings.FromConnectionString(conn);
        if (args.Contains("--print-commands"))
        {
            var seen = new HashSet<string>();
            settings.ClusterConfigurator = b => b.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(e =>
            {
                if (e.CommandName is "aggregate" or "find" && seen.Add(e.Command.ToJson()))
                    Console.WriteLine("COMMAND " + e.Command.ToJson());
            });
        }
        var client = new MongoClient(settings);
        DbContextOptions<BenchmarkDbContext> Options(MongoQueryMode mode) => new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(client, dbName, o => o.UseQueryMode(mode))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
        var dl = Options(MongoQueryMode.DriverLinq);
        var nat = Options(MongoQueryMode.Native);

        try
        {
            using (var ctx = new BenchmarkDbContext(dl))
            {
                if (extended)
                    BenchmarkSeeder.SeedExtended(ctx, 200, extRows < 0 ? 10_000 : extRows, extRows < 0 ? 5_000 : extRows);
                else
                    BenchmarkSeeder.Seed(ctx, rows, 100, rows);
            }

            var db = client.GetDatabase(dbName);
            var flat = db.GetCollection<FlatItem>("FlatItems");
            var reviews = db.GetCollection<Review>("Reviews");
            var products = db.GetCollection<Product>("Products");
            var orders = db.GetCollection<Order>("Orders");
            var wides = db.GetCollection<WideEntity>("Wides");

            var opts = config == "native" ? nat : dl;
            Func<int>? sync = null;
            Func<Task<int>>? async = null;
            switch (config, shape)
            {
                case ("driver", "WhereToList"): sync = () => flat.AsQueryable().Where(f => f.Active).ToList().Count; break;
                case ("driver", "WhereNone"): sync = () => flat.AsQueryable().Where(f => f.Count < 0).ToList().Count; break;
                case ("driver", "WholeEntityToList"):
                case ("driver", "WholeEntityTracked"): sync = () => flat.AsQueryable().ToList().Count; break;
                case ("driver", "Take1"): sync = () => flat.AsQueryable().Take(1).ToList().Count; break;
                case ("driver", "OrderByTake"): sync = () => flat.AsQueryable().OrderBy(f => f.Count).Take(100).ToList().Count; break;
                case ("driver", "ReferenceInclude"): sync = () => HeadlineBenchmarks.DriverReviewInclude(reviews, products); break;
                case ("driver", "ComplexQuery"): sync = () => ExtendedBenchmarks.DriverComplexQuery(orders); break;
                case ("driver", "WideWholeEntity"): sync = () => wides.AsQueryable().ToList().Count; break;
                case ("driver", "NestedWholeEntity"): sync = () => orders.AsQueryable().ToList().Count; break;
                case ("driver", "WhereToListAsync"): async = async () => (await MongoQueryable.ToListAsync(flat.AsQueryable().Where(f => f.Active))).Count; break;
                case ("driver", "WholeEntityToListAsync"): async = async () => (await MongoQueryable.ToListAsync(flat.AsQueryable())).Count; break;

                case (_, "WhereToList"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count; }; break;
                case (_, "WhereNone"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.AsNoTracking().Where(f => f.Count < 0).ToList().Count; }; break;
                case (_, "WholeEntityToList"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.AsNoTracking().ToList().Count; }; break;
                case (_, "WholeEntityTracked"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.ToList().Count; }; break;
                case (_, "Take1"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.AsNoTracking().Take(1).ToList().Count; }; break;
                case (_, "OrderByTake"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(100).ToList().Count; }; break;
                case (_, "ReferenceInclude"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count; }; break;
                case (_, "ComplexQuery"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.Orders.AsNoTracking().Where(o => o.Shipping.Address.Zip > 10_500 && o.Code != null).Include(o => o.Account).OrderByDescending(o => o.Shipping.Address.City).ThenBy(o => o.Id).Skip(50).Take(1500).ToList().Count; }; break;
                case (_, "WideWholeEntity"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.Wides.AsNoTracking().ToList().Count; }; break;
                case (_, "NestedWholeEntity"): sync = () => { using var c = new BenchmarkDbContext(opts); return c.Orders.AsNoTracking().ToList().Count; }; break;
                case (_, "WhereToListAsync"): async = async () => { using var c = new BenchmarkDbContext(opts); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.FlatItems.AsNoTracking().Where(f => f.Active))).Count; }; break;
                case (_, "WholeEntityToListAsync"): async = async () => { using var c = new BenchmarkDbContext(opts); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.FlatItems.AsNoTracking())).Count; }; break;
                case (_, "ReferenceIncludeAsync"): async = async () => { using var c = new BenchmarkDbContext(opts); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.Reviews.AsNoTracking().Include(r => r.Product))).Count; }; break;
                default: throw new ArgumentException($"unknown shape/config {shape}/{config}; shapes: {string.Join(", ", Shapes)}");
            }

            int Once() => sync != null ? sync() : async!().GetAwaiter().GetResult();

            var result = 0;
            for (var w = 0; w < warmup; w++) result = Once();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            var bytes0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            var ops = 0;
            var limit = TimeSpan.FromSeconds(seconds);
            var perOp = new List<double>();
            while (sw.Elapsed < limit || ops < minOps)
            {
                var t = Stopwatch.GetTimestamp();
                result = Once();
                perOp.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                ops++;
            }
            sw.Stop();
            var bytes = GC.GetTotalAllocatedBytes(true) - bytes0;
            perOp.Sort();
            Console.WriteLine(
                $"PROFILE shape={shape} config={config} rows={(extended ? extRows : rows)} result={result} ops={ops} " +
                $"ms_mean={sw.Elapsed.TotalMilliseconds / ops:F3} ms_median={perOp[perOp.Count / 2]:F3} ms_min={perOp[0]:F3} " +
                $"kb_op={bytes / 1024.0 / ops:F1} gen0={(GC.CollectionCount(0) - g0) / (double)ops:F3} " +
                $"gen1={(GC.CollectionCount(1) - g1) / (double)ops:F3} gen2={(GC.CollectionCount(2) - g2) / (double)ops:F3}");
            return 0;
        }
        finally
        {
            using var ctx = new BenchmarkDbContext(dl);
            ctx.Database.EnsureDeleted();
        }
    }

    private static int Int(string[] args, string name, int dflt)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 ? int.Parse(args[i + 1]) : dflt;
    }
}
