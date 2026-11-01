using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Queries that return one row: First/Single/FirstOrDefault (the native pipeline adds $limit 1 / $limit 2) and an enumerable query limited
// to one row. With one row the per-query fixed cost (context, compiled-query lookup, pipeline build, round trip) dominates, so these show
// what the per-row work of the one-pass materializer costs when there is nothing to amortize it over.
// Same three configs as HeadlineBenchmarks; run with --single.
[Config(typeof(BenchmarkConfig))]
public class SingleResultBenchmarks
{
    private const int N = 10_000;

    private DbContextOptions<BenchmarkDbContext> _efOptionsDriverLinq = null!;
    private DbContextOptions<BenchmarkDbContext> _efOptionsNative = null!;
    // Pooled: a benchmark op takes a context from the pool and returns it on dispose, as an application using AddDbContextPool does.
    private IDbContextFactory<BenchmarkDbContext> _driverLinqFactory = null!;
    private IDbContextFactory<BenchmarkDbContext> _nativeFactory = null!;
    private IMongoCollection<FlatItem> _flatColl = null!;
    private MongoClient _client = null!;
    private string _dbName = null!;
    private ObjectId _id;

    [GlobalSetup]
    public void Setup()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        _dbName = "ef_bench_single_" + Guid.NewGuid().ToString("N");
        _client = new MongoClient(conn);

        _efOptionsDriverLinq = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(_client, _dbName, o => o.UseQueryMode(MongoQueryMode.DriverLinq))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .EnableThreadSafetyChecks(false)
            .Options;

        _efOptionsNative = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(_client, _dbName, o => o.UseQueryMode(MongoQueryMode.Native))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .EnableThreadSafetyChecks(false)
            .Options;

        _driverLinqFactory = BenchmarkContexts.Pool(_efOptionsDriverLinq);
        _nativeFactory = BenchmarkContexts.Pool(_efOptionsNative);

        using (var ctx = new BenchmarkDbContext(_efOptionsDriverLinq))
        {
            BenchmarkSeeder.Seed(ctx, flatCount: N, productCount: 100, reviewCount: 100);
        }

        _flatColl = _client.GetDatabase(_dbName).GetCollection<FlatItem>("FlatItems");
        _id = _flatColl.Find(FilterDefinition<FlatItem>.Empty).Skip(N / 2).Limit(1).Project(f => f.Id).First();

        Validate();
    }

    private void Validate()
    {
        var expected = _flatColl.AsQueryable().First(f => f.Id == _id).Name;

        foreach (var options in new[] { _efOptionsDriverLinq, _efOptionsNative })
        {
            using var ctx = new BenchmarkDbContext(options);
            var query = ctx.FlatItems.AsNoTracking();

            if (query.First(f => f.Id == _id).Name != expected
                || query.Single(f => f.Id == _id).Name != expected
                || query.Where(f => f.Id == _id).Take(1).ToList().Single().Name != expected
                || query.Where(f => f.Active).FirstOrDefault() is null)
            {
                throw new InvalidOperationException($"Single-result mismatch ({options.Extensions.Count()} extensions).");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq);
        ctx.Database.EnsureDeleted();
    }

    // ----- First(f => f.Id == id) -----
    [Benchmark] public string FirstById_DriverOnly()
        => _flatColl.AsQueryable().First(f => f.Id == _id).Name;

    [Benchmark] public string FirstById_EF_DriverLinq()
    { using var ctx = _driverLinqFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().First(f => f.Id == _id).Name; }

    [Benchmark] public string FirstById_EF_Native()
    { using var ctx = _nativeFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().First(f => f.Id == _id).Name; }

    [Benchmark] public string FirstById_EF_DriverLinq_Tracked()
    { using var ctx = _driverLinqFactory.CreateDbContext(); return ctx.FlatItems.First(f => f.Id == _id).Name; }

    [Benchmark] public string FirstById_EF_Native_Tracked()
    { using var ctx = _nativeFactory.CreateDbContext(); return ctx.FlatItems.First(f => f.Id == _id).Name; }

    // ----- Single(f => f.Id == id) -----
    [Benchmark] public string SingleById_DriverOnly()
        => _flatColl.AsQueryable().Single(f => f.Id == _id).Name;

    [Benchmark] public string SingleById_EF_DriverLinq()
    { using var ctx = _driverLinqFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Single(f => f.Id == _id).Name; }

    [Benchmark] public string SingleById_EF_Native()
    { using var ctx = _nativeFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Single(f => f.Id == _id).Name; }

    // ----- Where(f => f.Active).FirstOrDefault() (a scan that stops at the first match) -----
    [Benchmark] public string FirstOrDefaultWhere_DriverOnly()
        => _flatColl.AsQueryable().Where(f => f.Active).FirstOrDefault()!.Name;

    [Benchmark] public string FirstOrDefaultWhere_EF_DriverLinq()
    { using var ctx = _driverLinqFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).FirstOrDefault()!.Name; }

    [Benchmark] public string FirstOrDefaultWhere_EF_Native()
    { using var ctx = _nativeFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).FirstOrDefault()!.Name; }

    // ----- Where(f => f.Active).Take(1).ToList(): an enumerable query that can return at most one row -----
    [Benchmark] public int Take1ToList_DriverOnly()
        => _flatColl.AsQueryable().Where(f => f.Active).Take(1).ToList().Count;

    [Benchmark] public int Take1ToList_EF_DriverLinq()
    { using var ctx = _driverLinqFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).Take(1).ToList().Count; }

    [Benchmark] public int Take1ToList_EF_Native()
    { using var ctx = _nativeFactory.CreateDbContext(); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).Take(1).ToList().Count; }
}
