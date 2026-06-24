using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Two-config headline benchmarks (on main, before the native path exists):
//   DriverOnly  - raw MongoDB C# driver LINQ / aggregation, no EF Core (perf floor).
//   EF          - the current EF provider (driver-LINQ delegation == main baseline).
// A third config (EF-Native) is added in sub-project 1. All configs read the SAME documents
// seeded once in [GlobalSetup], via one shared MongoClient (fairness: a per-context client would
// charge connection-pool + topology startup to the EF numbers).
[Config(typeof(BenchmarkConfig))]
public class HeadlineBenchmarks
{
    private const int N = 10_000;

    private DbContextOptions<BenchmarkDbContext> _efOptions = null!;
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

        _efOptions = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(_client, _dbName).Options;

        using (var ctx = new BenchmarkDbContext(_efOptions))
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

        using var ef = new BenchmarkDbContext(_efOptions);
        var efAll = ef.FlatItems.AsNoTracking().ToList().Count;
        var efWhere = ef.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count;
        var efReview = ef.Reviews.AsNoTracking().Include(r => r.Product).Count(r => r.Product != null);

        if (driverAll != N || efAll != N)
            throw new InvalidOperationException($"FlatItem count mismatch: driver={driverAll}, ef={efAll}, expected {N}.");
        if (driverWhere != efWhere)
            throw new InvalidOperationException($"Where count mismatch: driver={driverWhere}, ef={efWhere}.");
        if (driverReview != N || efReview != N)
            throw new InvalidOperationException($"Review+Product mismatch: driver={driverReview}, ef={efReview}, expected {N}.");
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
        using var ctx = new BenchmarkDbContext(_efOptions);
        ctx.Database.EnsureDeleted();
    }

    // ----- Where(x => x.Active).ToList() -----
    [Benchmark] public int WhereToList_DriverOnly()
        => _flatColl.AsQueryable().Where(f => f.Active).ToList().Count;

    [Benchmark] public int WhereToList_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.FlatItems.AsNoTracking().Where(f => f.Active).ToList().Count; }

    // ----- whole-entity ToList() -----
    [Benchmark] public int WholeEntityToList_DriverOnly()
        => _flatColl.AsQueryable().ToList().Count;

    [Benchmark] public int WholeEntityToList_EF_NoTracking()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.FlatItems.AsNoTracking().ToList().Count; }

    [Benchmark] public int WholeEntityToList_EF_Tracked()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.FlatItems.ToList().Count; }

    // ----- OrderBy(x => x.Count).Take(100).ToList() -----
    [Benchmark] public int OrderByTake_DriverOnly()
        => _flatColl.AsQueryable().OrderBy(f => f.Count).Take(100).ToList().Count;

    [Benchmark] public int OrderByTake_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.FlatItems.AsNoTracking().OrderBy(f => f.Count).Take(100).ToList().Count; }

    // ----- Reviews.Include(r => r.Product).ToList() -----
    [Benchmark] public int ReferenceInclude_DriverOnly()
        => DriverReviewInclude();

    [Benchmark] public int ReferenceInclude_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.Reviews.AsNoTracking().Include(r => r.Product).ToList().Count; }
}
