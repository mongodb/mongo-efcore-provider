using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Steady-state loop for attaching dotnet-trace / dotnet-counters, and for fixed-cost and scaling runs.
//   --profile <shape> <config> [--seconds N] [--rows N] [--warmup N] [--nopool]
// config: driver | driverlinq | native. Prints one machine-readable PROFILE line.
// rows applies to FlatItems and Reviews (products fixed at 100). Extended shapes use the headline-sized data
// (orders 10,000, wides 5,000, accounts 200) unless --rows is given, in which case orders and wides = rows.
internal static class ProfileHarness
{
    public static readonly string[] Shapes =
    {
        "WhereToList", "WhereNone", "WholeEntityToList", "WholeEntityTracked", "Take1", "OrderByTake",
        "ReferenceInclude", "ComplexQuery", "WideWholeEntity", "MediumWholeEntity", "NestedWholeEntity",
        "WhereToListAsync", "WholeEntityToListAsync", "ReferenceIncludeAsync",
        "FirstById", "SingleById", "FirstOrDefaultWhere", "FirstByIdTracked", "ContextOnly",
        "ProjectScalar", "ProjectPair", "ProjectDto", "ProjectComputed", "NavigationProject", "IncludeFiltered",
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
        var catalogShape = Shapes.Contains(shape) ? null : QueryShapeBenchmarks.AllShapes.FirstOrDefault(x => x.Name == shape);
        var extended = shape is "ComplexQuery" or "WideWholeEntity" or "MediumWholeEntity" or "NestedWholeEntity" or "IncludeFiltered";
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
        var useConnectionString = args.Contains("--connstr");
        DbContextOptions<BenchmarkDbContext> Options(MongoQueryMode mode) => (useConnectionString
                ? new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(conn, dbName, o => o.UseQueryMode(mode))
                : new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(client, dbName, o => o.UseQueryMode(mode)))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .EnableThreadSafetyChecks(args.Contains("--threadsafety")).Options;
        var dl = Options(MongoQueryMode.DriverLinq);
        var nat = Options(MongoQueryMode.Native);

        try
        {
            using (var ctx = new BenchmarkDbContext(dl))
            {
                if (catalogShape != null)
                {
                    // A catalogue shape may read any of the catalogue's collections: seed them all, as QueryShapeBenchmarks does.
                    BenchmarkSeeder.Seed(ctx, rows, 100, rows);
                    BenchmarkSeeder.SeedExtended(ctx, accountCount: 200, orderCount: 10_000, wideCount: 0, mediumCount: 10_000);
                    BenchmarkSeeder.SeedIncludes(ctx, categoryCount: 100, authorCount: 500, articleCount: 10_000);
                }
                else if (extended)
                    BenchmarkSeeder.SeedExtended(ctx, 200, extRows < 0 ? 10_000 : extRows, extRows < 0 ? 5_000 : extRows, extRows < 0 ? 10_000 : extRows);
                else
                    BenchmarkSeeder.Seed(ctx, rows, 100, rows);
            }

            var db = client.GetDatabase(dbName);
            var flat = db.GetCollection<FlatItem>("FlatItems");
            var reviews = db.GetCollection<Review>("Reviews");
            var products = db.GetCollection<Product>("Products");
            var orders = db.GetCollection<Order>("Orders");
            var accounts = db.GetCollection<Account>("Accounts");
            var wides = db.GetCollection<WideEntity>("Wides");
            var mediums = db.GetCollection<MediumEntity>("Mediums");
            var catalogCollections = new QueryShapeBenchmarks.Collections
            {
                Flat = flat, Reviews = reviews, Products = products, Orders = orders, Accounts = accounts, Mediums = mediums,
                Categories = db.GetCollection<Category>("Categories"),
                Articles = db.GetCollection<Article>("Articles"),
                Authors = db.GetCollection<Author>("Authors"),
            };

            var firstId = flat.Find(FilterDefinition<FlatItem>.Empty).Skip(rows / 2).Limit(1).Project(f => f.Id).FirstOrDefault();
            var opts = config == "native" ? nat : dl;
            // Pooled like AddDbContextPool unless --nopool; disposing a pooled context returns it to the pool.
            var pool = args.Contains("--nopool") ? null : BenchmarkContexts.Pool(opts);
            BenchmarkDbContext NewContext() => pool?.CreateDbContext() ?? new BenchmarkDbContext(opts);
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
                case ("driver", "MediumWholeEntity"): sync = () => mediums.AsQueryable().ToList().Count; break;
                case ("driver", "NestedWholeEntity"): sync = () => orders.AsQueryable().ToList().Count; break;
                case ("driver", "FirstById"): sync = () => flat.AsQueryable().First(f => f.Id == firstId).Count; break;
                case ("driver", "SingleById"): sync = () => flat.AsQueryable().Single(f => f.Id == firstId).Count; break;
                case ("driver", "FirstOrDefaultWhere"): sync = () => flat.AsQueryable().Where(f => f.Active).FirstOrDefault()!.Count; break;
                case ("driver", "ProjectScalar"): sync = () => flat.AsQueryable().Select(f => f.Count).ToList().Count; break;
                case ("driver", "ProjectPair"): sync = () => flat.AsQueryable().Select(f => new { f.Id, f.Name }).ToList().Count; break;
                case ("driver", "ProjectDto"): sync = () => flat.AsQueryable().Select(f => new QueryShapeBenchmarks.FlatDto { Id = f.Id, Name = f.Name, Rate = f.Rate }).ToList().Count; break;
                case ("driver", "ProjectComputed"): sync = () => flat.AsQueryable().Select(f => new { f.Id, Score = f.Count * 2L + f.Big, f.Name.Length }).ToList().Count; break;
                case ("driver", "NavigationProject"): sync = () => HeadlineBenchmarks.DriverReviewInclude(reviews, products); break;
                case ("driver", "IncludeFiltered"): sync = () => orders.Aggregate().Match(o => o.Shipping.Address.Zip < 10500)
                    .Lookup<Order, Account, QueryShapeBenchmarks.OrderWithAccounts>(accounts, o => o.AccountId, a => a.Id, o => o.Accounts).ToList().Count(o => o.Accounts.Count > 0); break;
                case ("driver", "WhereToListAsync"): async = async () => (await MongoQueryable.ToListAsync(flat.AsQueryable().Where(f => f.Active))).Count; break;
                case ("driver", "WholeEntityToListAsync"): async = async () => (await MongoQueryable.ToListAsync(flat.AsQueryable())).Count; break;

                case (_, "FirstById"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().First(f => f.Id == firstId).Count; }; break;
                case (_, "FirstByIdTracked"): sync = () => { using var c = NewContext(); return c.FlatItems.First(f => f.Id == firstId).Count; }; break;
                case (_, "SingleById"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Single(f => f.Id == firstId).Count; }; break;
                case (_, "FirstOrDefaultWhere"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Where(f => f.Active).FirstOrDefault()!.Count; }; break;
                case (_, "ContextOnly"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking() != null ? 1 : 0; }; break;
                case (_, "ProjectScalar"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Select(f => f.Count).ToList().Count; }; break;
                case (_, "ProjectPair"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Select(f => new { f.Id, f.Name }).ToList().Count; }; break;
                case (_, "ProjectDto"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Select(f => new QueryShapeBenchmarks.FlatDto { Id = f.Id, Name = f.Name, Rate = f.Rate }).ToList().Count; }; break;
                case (_, "ProjectComputed"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Select(f => new { f.Id, Score = f.Count * 2L + f.Big, f.Name.Length }).ToList().Count; }; break;
                case (_, "NavigationProject"): sync = () => { using var c = NewContext(); return c.Reviews.AsNoTracking().Select(r => new { r.Stars, r.Product!.Title }).ToList().Count; }; break;
                case (_, "IncludeFiltered"): sync = () => { using var c = NewContext(); return c.Orders.AsNoTracking().Where(o => o.Shipping.Address.Zip < 10500).Include(o => o.Account).ToList().Count(o => o.Account != null); }; break;
                case (_, "WhereToList"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count; }; break;
                case (_, "WhereNone"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Where(f => f.Count < 0).ToList().Count; }; break;
                case (_, "WholeEntityToList"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().ToList().Count; }; break;
                case (_, "WholeEntityTracked"): sync = () => { using var c = NewContext(); return c.FlatItems.ToList().Count; }; break;
                case (_, "Take1"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().Take(1).ToList().Count; }; break;
                case (_, "OrderByTake"): sync = () => { using var c = NewContext(); return c.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(100).ToList().Count; }; break;
                case (_, "ReferenceInclude"): sync = () => { using var c = NewContext(); return c.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count; }; break;
                case (_, "ComplexQuery"): sync = () => { using var c = NewContext(); return c.Orders.AsNoTracking().Where(o => o.Shipping.Address.Zip > 10_500 && o.Code != null).Include(o => o.Account).OrderByDescending(o => o.Shipping.Address.City).ThenBy(o => o.Id).Skip(50).Take(1500).ToList().Count; }; break;
                case (_, "WideWholeEntity"): sync = () => { using var c = NewContext(); return c.Wides.AsNoTracking().ToList().Count; }; break;
                case (_, "MediumWholeEntity"): sync = () => { using var c = NewContext(); return c.Mediums.AsNoTracking().ToList().Count; }; break;
                case (_, "NestedWholeEntity"): sync = () => { using var c = NewContext(); return c.Orders.AsNoTracking().ToList().Count; }; break;
                case (_, "WhereToListAsync"): async = async () => { using var c = NewContext(); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.FlatItems.AsNoTracking().Where(f => f.Active))).Count; }; break;
                case (_, "WholeEntityToListAsync"): async = async () => { using var c = NewContext(); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.FlatItems.AsNoTracking())).Count; }; break;
                case (_, "ReferenceIncludeAsync"): async = async () => { using var c = NewContext(); return (await EntityFrameworkQueryableExtensions.ToListAsync(c.Reviews.AsNoTracking().Include(r => r.Product))).Count; }; break;
                case ("driver", _) when catalogShape?.Driver != null: sync = () => catalogShape.Driver(catalogCollections); break;
                case (_, _) when catalogShape != null: sync = () => { using var c = NewContext(); return catalogShape.Ef(c); }; break;
                default: throw new ArgumentException($"unknown shape/config {shape}/{config}; shapes: {string.Join(", ", Shapes)}, or any name in QueryShapeBenchmarks.AllShapes");
            }

            int Once() => sync != null ? sync() : async!().GetAwaiter().GetResult();

            var result = 0;
            for (var w = 0; w < warmup; w++) result = Once();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            using var sampler = args.Contains("--alloc-types") ? new AllocationSampler() : null;
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
            var line =
                $"PROFILE shape={shape} config={config} rows={(extended ? extRows : rows)} result={result} ops={ops} " +
                $"ms_mean={sw.Elapsed.TotalMilliseconds / ops:F3} ms_median={perOp[perOp.Count / 2]:F3} ms_min={perOp[0]:F3} " +
                $"kb_op={bytes / 1024.0 / ops:F1} gen0={(GC.CollectionCount(0) - g0) / (double)ops:F3} " +
                $"gen1={(GC.CollectionCount(1) - g1) / (double)ops:F3} gen2={(GC.CollectionCount(2) - g2) / (double)ops:F3}";
            Console.WriteLine(line);
            sampler?.Report(Int(args, "--alloc-top", 25));
            if (Environment.GetEnvironmentVariable("PROFILE_OUT") is { Length: > 0 } outFile)
                File.AppendAllText(outFile, line + Environment.NewLine);
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

// In-process allocation attribution: the runtime raises GCAllocationTick roughly every 100 KB allocated, naming the type that crossed the
// threshold, so summing the tick size by type over many operations approximates where the bytes go.
internal sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
{
    private readonly Dictionary<string, long> _bytes = new();
    private long _total;

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (!e.EventName!.StartsWith("GCAllocationTick") || e.Payload is null)
            return;
        var names = e.PayloadNames!;
        var type = (string)e.Payload[names.IndexOf("TypeName")]!;
        var amount = Convert.ToInt64(e.Payload[names.IndexOf("AllocationAmount64")]);
        lock (_bytes)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            _total += amount;
        }
    }

    public void Report(int top)
    {
        lock (_bytes)
        {
            Console.WriteLine($"ALLOC sampled total={_total / 1024} KB");
            foreach (var (type, bytes) in _bytes.OrderByDescending(kv => kv.Value).Take(top))
                Console.WriteLine($"ALLOC {bytes * 100.0 / _total,5:F1}% {type}");
        }
    }
}
