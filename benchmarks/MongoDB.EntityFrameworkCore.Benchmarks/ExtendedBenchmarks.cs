using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Heavier cases (DriverOnly floor + EF current provider). One shared MongoClient (fairness).
//   Case A  ComplexQuery     - Where(nested-owned + own) + Include(Account) + OrderByDesc/ThenBy + Skip/Take
//   Case B  WideWholeEntity  - 400-property entity, whole-entity ToList
//   Case C  NestedWholeEntity- deep owned graph (owned-in-owned, collection-in-collection), whole-entity ToList
[Config(typeof(BenchmarkConfig))]
public class ExtendedBenchmarks
{
    private const int AccountN = 200;
    private const int OrderN = 10_000;
    private const int WideN = 5_000;
    private const int ZipThreshold = 10_500;

    private DbContextOptions<BenchmarkDbContext> _efOptions = null!;
    private IMongoCollection<Order> _orderColl = null!;
    private IMongoCollection<WideEntity> _wideColl = null!;
    private MongoClient _client = null!;
    private string _dbName = null!;

    // Typed POCO for DriverComplexQuery output: carries the full Order graph (all fields)
    // plus the looked-up Account, matching the same shape EF materializes via Include(o => o.Account).
    private sealed class OrderFull
    {
        [BsonId] public ObjectId Id { get; set; }
        public string Code { get; set; } = "";
        public ObjectId AccountId { get; set; }
        public ShippingInfo Shipping { get; set; } = new();
        public List<LineItem> Lines { get; set; } = new();
        // $lookup places the matched account directly into this field (after $unwind).
        [BsonElement("AccountLookup")] public Account? AccountLookup { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        _dbName = "ef_bench_ext_" + Guid.NewGuid().ToString("N");
        _client = new MongoClient(conn);
        _efOptions = new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(_client, _dbName).Options;

        using (var ctx = new BenchmarkDbContext(_efOptions))
            BenchmarkSeeder.SeedExtended(ctx, AccountN, OrderN, WideN);

        var db = _client.GetDatabase(_dbName);
        _orderColl = db.GetCollection<Order>("Orders");
        _wideColl = db.GetCollection<WideEntity>("Wides");

        Validate();
    }

    private void Validate()
    {
        var driverComplex = DriverComplexQuery();
        var driverWide = _wideColl.AsQueryable().ToList().Count;
        var driverOrders = _orderColl.AsQueryable().ToList();
        var driverLines = driverOrders.Sum(o => o.Lines.Count);
        var driverDiscounts = driverOrders.Sum(o => o.Lines.Sum(l => l.Discounts.Count));

        using var ef = new BenchmarkDbContext(_efOptions);
        var efComplex = EfComplexQuery(ef).Count;
        var efWide = ef.Wides.AsNoTracking().ToList().Count;
        var efOrders = ef.Orders.AsNoTracking().ToList();
        var efLines = efOrders.Sum(o => o.Lines.Count);
        var efDiscounts = efOrders.Sum(o => o.Lines.Sum(l => l.Discounts.Count));

        if (driverWide != WideN || efWide != WideN)
            throw new InvalidOperationException($"Wide count mismatch: driver={driverWide}, ef={efWide}, expected {WideN}.");
        if (driverOrders.Count != OrderN || efOrders.Count != OrderN)
            throw new InvalidOperationException($"Order count mismatch: driver={driverOrders.Count}, ef={efOrders.Count}, expected {OrderN}.");
        if (driverLines != efLines || efLines != OrderN * 3)
            throw new InvalidOperationException($"Line count mismatch: driver={driverLines}, ef={efLines}, expected {OrderN * 3}.");
        if (driverDiscounts != efDiscounts || efDiscounts != OrderN * 6)
            throw new InvalidOperationException($"Discount count mismatch: driver={driverDiscounts}, ef={efDiscounts}, expected {OrderN * 6}.");
        if (driverComplex != efComplex)
            throw new InvalidOperationException($"ComplexQuery count mismatch: driver={driverComplex}, ef={efComplex}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        using var ctx = new BenchmarkDbContext(_efOptions);
        ctx.Database.EnsureDeleted();
    }

    // ----- Case A: complex combined query -----
    // EF: filter (nested owned scalar + own) + reference Include + multi-sort + paging.
    private static List<Order> EfComplexQuery(BenchmarkDbContext ctx)
        => ctx.Orders.AsNoTracking()
            .Where(o => o.Shipping.Address.Zip > ZipThreshold && o.Code != null)
            .Include(o => o.Account)
            .OrderByDescending(o => o.Shipping.Address.City)
            .ThenBy(o => o.Id)
            .Skip(50).Take(1500)
            .ToList();

    // DriverOnly: same logical pipeline as EF's captured aggregate, in the SAME stage order:
    //   $match → $sort → $skip → $limit → $lookup (Account, after paging) → $unwind → typed materialization.
    // The $lookup sits after $skip/$limit, so only ~1500 documents are joined — identical to EF's behaviour.
    // OrderFull carries every Order field (Id, Code, AccountId, Shipping+Address, Lines+Meta+Discounts) plus the
    // looked-up Account, so the typed graph is fully materialized with .ToList() — same work as EF's Include.
    private int DriverComplexQuery()
    {
        var matchStage  = new BsonDocument("$match", new BsonDocument
        {
            { "Shipping.Address.Zip", new BsonDocument("$gt", ZipThreshold) },
            { "Code", new BsonDocument("$ne", BsonNull.Value) }
        });
        var sortStage   = new BsonDocument("$sort",
            new BsonDocument { { "Shipping.Address.City", -1 }, { "_id", 1 } });
        var skipStage   = new BsonDocument("$skip",  50);
        var limitStage  = new BsonDocument("$limit", 1500);
        var lookupStage = new BsonDocument("$lookup", new BsonDocument
        {
            { "from",        "Accounts" },
            { "localField",  "AccountId" },
            { "foreignField","_id" },
            { "as",          "AccountLookup" }
        });
        // $unwind replicates EF's inner-join semantics: only rows with a matched Account are returned.
        var unwindStage = new BsonDocument("$unwind",
            new BsonDocument { { "path", "$AccountLookup" }, { "preserveNullAndEmptyArrays", false } });

        var pipeline = PipelineDefinition<Order, OrderFull>.Create(
            new[] { matchStage, sortStage, skipStage, limitStage, lookupStage, unwindStage });

        return _orderColl.Aggregate(pipeline).ToList().Count;
    }

    [Benchmark] public int ComplexQuery_DriverOnly() => DriverComplexQuery();

    [Benchmark] public int ComplexQuery_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return EfComplexQuery(ctx).Count; }

    // ----- Case B: wide entity (400 props) whole-entity ToList -----
    [Benchmark] public int WideWholeEntity_DriverOnly()
        => _wideColl.AsQueryable().ToList().Count;

    [Benchmark] public int WideWholeEntity_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.Wides.AsNoTracking().ToList().Count; }

    // ----- Case C: deep owned graph whole-entity ToList -----
    [Benchmark] public int NestedWholeEntity_DriverOnly()
        => _orderColl.AsQueryable().ToList().Count;

    [Benchmark] public int NestedWholeEntity_EF()
    { using var ctx = new BenchmarkDbContext(_efOptions); return ctx.Orders.AsNoTracking().ToList().Count; }
}
