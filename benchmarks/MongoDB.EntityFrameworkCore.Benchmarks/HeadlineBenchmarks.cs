using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Three-config headline benchmarks (EF-323: native query path):
//   DriverOnly      - raw MongoDB C# driver LINQ / aggregation, no EF Core (perf floor).
//   EF_DriverLinq   - EF provider pinned to UseQueryMode(MongoQueryMode.DriverLinq) == the
//                     pre-rebuild "EF-current" baseline (matches the numbers in perf-baseline.md).
//   EF_Native       - EF provider in UseQueryMode(MongoQueryMode.Native) == the new native path.
//                     ReferenceInclude falls back to driver-LINQ (deferred — Include is not yet
//                     native), so EF_Native ≈ EF_DriverLinq for that shape.
// All configs read the SAME documents seeded once in [GlobalSetup], via one shared MongoClient
// (fairness: a per-context client would charge connection-pool + topology startup to EF numbers).
[Config(typeof(BenchmarkConfig))]
public class HeadlineBenchmarks
{
    private const int N = 10_000;

    private DbContextOptions<BenchmarkDbContext> _efOptionsDriverLinq = null!;
    private DbContextOptions<BenchmarkDbContext> _efOptionsNative = null!;
    private IMongoCollection<FlatItem> _flatColl = null!;
    private IMongoCollection<Review> _reviewColl = null!;
    private IMongoCollection<Product> _productColl = null!;
    private MongoClient _client = null!;
    private string _dbName = null!;

    [GlobalSetup]
    public void Setup()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        _dbName = "ef_bench_headline_" + Guid.NewGuid().ToString("N");
        _client = new MongoClient(conn);

        _efOptionsDriverLinq = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(_client, _dbName, o => o.UseQueryMode(MongoQueryMode.DriverLinq))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        _efOptionsNative = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(_client, _dbName, o => o.UseQueryMode(MongoQueryMode.Native))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        // Seed using DriverLinq options (seeding is writes, unaffected by query mode).
        using (var ctx = new BenchmarkDbContext(_efOptionsDriverLinq))
        {
            BenchmarkSeeder.Seed(ctx, flatCount: N, productCount: 100, reviewCount: N);
        }

        var db = _client.GetDatabase(_dbName);
        _flatColl = db.GetCollection<FlatItem>("FlatItems");
        _reviewColl = db.GetCollection<Review>("Reviews");
        _productColl = db.GetCollection<Product>("Products");

        Validate();
    }

    private void Validate()
    {
        var driverAll = _flatColl.AsQueryable().ToList().Count;
        var driverWhere = _flatColl.AsQueryable().Where(f => f.Active).ToList().Count;
        var driverReview = DriverReviewInclude();

        // Validate EF_DriverLinq
        using (var efDl = new BenchmarkDbContext(_efOptionsDriverLinq))
        {
            var efAll = efDl.FlatItems.AsNoTracking().ToList().Count;
            var efWhere = efDl.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count;
            var efReview = efDl.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count(r => r.Product != null);

            if (driverAll != N || efAll != N)
                throw new InvalidOperationException($"FlatItem count mismatch (DriverLinq): driver={driverAll}, ef={efAll}, expected {N}.");
            if (driverWhere != efWhere)
                throw new InvalidOperationException($"Where count mismatch (DriverLinq): driver={driverWhere}, ef={efWhere}.");
            if (driverReview != N || efReview != N)
                throw new InvalidOperationException($"Review+Product mismatch (DriverLinq): driver={driverReview}, ef={efReview}, expected {N}.");
        }

        // Parity: EF_Native must return the same counts as EF_DriverLinq (correctness gate).
        using (var efN = new BenchmarkDbContext(_efOptionsNative))
        {
            var efAllNative = efN.FlatItems.AsNoTracking().ToList().Count;
            var efWhereNative = efN.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count;
            var efReviewNative = efN.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count(r => r.Product != null);

            if (driverAll != efAllNative)
                throw new InvalidOperationException($"FlatItem count mismatch (Native): driver={driverAll}, efNative={efAllNative}, expected {N}.");
            if (driverWhere != efWhereNative)
                throw new InvalidOperationException($"Where count mismatch (Native): driver={driverWhere}, efNative={efWhereNative}.");
            if (efReviewNative != N)
                throw new InvalidOperationException($"Review+Product mismatch (Native): efNative={efReviewNative}, expected {N}.");
        }
    }

    // Hand-written $lookup + $unwind equivalent of Reviews.Include(r => r.Product).
    private int DriverReviewInclude()
    {
        var joined = _reviewColl
            .Aggregate()
            .Lookup<Review, Product, ReviewWithProduct>(
                foreignCollection: _productColl,
                localField: r => r.ProductId,
                foreignField: p => p.Id,
                @as: rp => rp.Products)
            .ToList();

        var count = 0;
        foreach (var rp in joined)
        {
            if (rp.Products.FirstOrDefault() != null) count++;
        }
        return count;
    }

    private sealed class ReviewWithProduct
    {
        public ObjectId Id { get; set; }
        public int Stars { get; set; }
        public ObjectId ProductId { get; set; }
        public List<Product> Products { get; set; } = new();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq);
        ctx.Database.EnsureDeleted();
    }

    // ----- Where(x => x.Active).ToList() -----
    [Benchmark] public int WhereToList_DriverOnly()
        => _flatColl.AsQueryable().Where(f => f.Active).ToList().Count;

    [Benchmark] public int WhereToList_EF_DriverLinq()
    { using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count; }

    [Benchmark] public int WhereToList_EF_Native()
    { using var ctx = new BenchmarkDbContext(_efOptionsNative); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count; }

    // ----- whole-entity ToList() -----
    [Benchmark] public int WholeEntityToList_DriverOnly()
        => _flatColl.AsQueryable().ToList().Count;

    [Benchmark] public int WholeEntityToList_EF_DriverLinq_NoTracking()
    { using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq); return ctx.FlatItems.AsNoTracking().ToList().Count; }

    [Benchmark] public int WholeEntityToList_EF_DriverLinq_Tracked()
    { using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq); return ctx.FlatItems.ToList().Count; }

    [Benchmark] public int WholeEntityToList_EF_Native_NoTracking()
    { using var ctx = new BenchmarkDbContext(_efOptionsNative); return ctx.FlatItems.AsNoTracking().ToList().Count; }

    [Benchmark] public int WholeEntityToList_EF_Native_Tracked()
    { using var ctx = new BenchmarkDbContext(_efOptionsNative); return ctx.FlatItems.ToList().Count; }

    // ----- OrderBy(x => x.Count).Take(100).ToList() -----
    [Benchmark] public int OrderByTake_DriverOnly()
        => _flatColl.AsQueryable().OrderBy(f => f.Count).Take(100).ToList().Count;

    [Benchmark] public int OrderByTake_EF_DriverLinq()
    { using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq); return ctx.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(100).ToList().Count; }

    [Benchmark] public int OrderByTake_EF_Native()
    { using var ctx = new BenchmarkDbContext(_efOptionsNative); return ctx.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(100).ToList().Count; }

    // ----- Reviews.Include(r => r.Product).ToList() -----
    // Note: ReferenceInclude falls back to driver-LINQ even in Native mode (Include is deferred —
    // not yet native). EF_Native ≈ EF_DriverLinq for this shape.
    [Benchmark] public int ReferenceInclude_DriverOnly()
        => DriverReviewInclude();

    [Benchmark] public int ReferenceInclude_EF_DriverLinq()
    { using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq); return ctx.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count; }

    [Benchmark] public int ReferenceInclude_EF_Native()
    { using var ctx = new BenchmarkDbContext(_efOptionsNative); return ctx.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count; }
}
