# Benchmark harness + baselines (sub-project 0) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a repeatable performance benchmark harness and commit a current-provider perf baseline (with a driver-only floor) and a Query spec-conformance snapshot on `main` — before any product-code change — so every later sub-project can prove no regression and confirm the gains.

**Architecture:** A standalone BenchmarkDotNet console project under `benchmarks/`, deliberately isolated from the repo's multi-EF-version build machinery by an empty `Directory.Build.props`, run with the **InProcess** toolchain (the default toolchain breaks on the config-conditional provider csproj). It measures two configs over stable *public* query shapes: **DriverOnly** (raw MongoDB C# driver — the allocation/time floor) and **EF current provider** (the `main` baseline). A `--smoke` mode is the correctness gate (validates the harness reads correct data before any number is trusted). A third "native" config will be added in sub-project 1 when the native path exists. A separate task records the Query spec-conformance "before".

**Tech Stack:** .NET 10 (`net10.0`), BenchmarkDotNet 0.15.8, EF Core 10, MongoDB C# driver, a project reference to `src/MongoDB.EntityFrameworkCore`.

**Reference (do NOT port — rebuild fresh; this is the proven shape to model on):** the spike benchmark project at `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/` on branch `spike/low-level-provider`. Note the spike harness is *three configs* because it ran after the native path existed; on `main` we build *two* (DriverOnly + EF current provider).

## Global Constraints

- Target framework `net10.0`; the project declares `<Configurations>Debug EF10;Release EF10</Configurations>` and is built/run with the **quoted** config name `"Release EF10"` (it contains a space). Benchmarks are an EF10-only harness.
- **InProcess toolchain** (`InProcessEmitToolchain.Instance`) — the default BenchmarkDotNet toolchain fails to build the config-conditional provider csproj.
- An **empty `Directory.Build.props`** in the benchmark project directory — stops MSBuild from inheriting the repo-root multi-config props (which would otherwise force the EF8/EF9/EF10 machinery onto this project).
- The benchmark project is **NOT** added to `MongoDB.EFCoreProvider.sln` — it is built and run standalone from its own directory.
- Running anything that touches the database (`--smoke`, the benchmarks) requires a **MongoDB replica set** (the provider's `SaveChanges` uses transactions) via `MONGODB_URI`. Local setup: `docker run -d --name ef-bench-mongo -p 27017:27017 mongo:8 --replSet rs0` then `rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})`.
- `src/` norms: `<Nullable>enable</Nullable>`; preserve BOMs on any file that has one. (Benchmark files are new and need no BOM.)
- The EF version is pinned via `Versions.props` (`$(EF10Version)`), imported by the csproj.

---

## File Structure

All paths under `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/` unless noted.

- `benchmarks/.gitignore` — ignore `BenchmarkDotNet.Artifacts/`.
- `Directory.Build.props` — **empty** project element; isolates from the repo build machinery.
- `MongoDB.EntityFrameworkCore.Benchmarks.csproj` — the console project (configs, package refs, provider project ref).
- `BenchmarkConfig.cs` — `ManualConfig`: InProcess toolchain, warmup/iteration counts, `MemoryDiagnoser`.
- `Model.cs` — `FlatItem`, `Review`, `Product` entities + `BenchmarkDbContext`.
- `BenchmarkSeeder.cs` — deterministic seeding shared by `--smoke` and the benchmarks.
- `Program.cs` — arg dispatch (`--smoke` → correctness check; default → `HeadlineBenchmarks`).
- `HeadlineBenchmarks.cs` — the two-config benchmark methods.
- `results/2026-06-23-baseline.md` — captured perf baseline (created by Task 5).
- `results/2026-06-23-query-conformance-baseline.md` — Query spec-conformance snapshot (created by Task 6).

---

### Task 1: Project scaffold + InProcess config

**Files:**
- Create: `benchmarks/.gitignore`
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Directory.Build.props`
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj`
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/BenchmarkConfig.cs`
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs` (temporary minimal body)

**Interfaces:**
- Produces: `BenchmarkConfig` (a `BenchmarkDotNet.Configs.ManualConfig`) consumed by later benchmark classes via `[Config(typeof(BenchmarkConfig))]`.

- [ ] **Step 1: Create `benchmarks/.gitignore`**

```gitignore
BenchmarkDotNet.Artifacts/
```

- [ ] **Step 2: Create the empty `Directory.Build.props`**

```xml
<Project>
</Project>
```

- [ ] **Step 3: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Import Project="..\..\Versions.props" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <AssemblyName>MongoDB.EntityFrameworkCore.Benchmarks</AssemblyName>
    <Configurations>Debug EF10;Release EF10</Configurations>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Release EF10'">
    <Optimize>true</Optimize>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="$(EF10Version)" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MongoDB.EntityFrameworkCore\MongoDB.EntityFrameworkCore.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create `BenchmarkConfig.cs`**

```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(10));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
```

- [ ] **Step 5: Create a temporary minimal `Program.cs`**

```csharp
Console.WriteLine("benchmarks: no mode selected (use --smoke or run with no args for headline)");
```

- [ ] **Step 6: Build to verify the scaffold compiles**

Run: `dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"`
Expected: `Build succeeded`, 0 errors. (The provider project builds transitively under `Release EF10`.)

- [ ] **Step 7: Commit**

```bash
git add benchmarks/
git commit -m "EF-324: benchmark project scaffold (InProcess config, isolated build)"
```

---

### Task 2: Model — entities, DbContext, seeder

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.cs`
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/BenchmarkSeeder.cs`

**Interfaces:**
- Produces:
  - `FlatItem { ObjectId Id; int Count; long Big; string Name; bool Active; double Rate; }`
  - `Review { ObjectId Id; int Stars; ObjectId ProductId; Product? Product; }`, `Product { ObjectId Id; string Title; }`
  - `BenchmarkDbContext(DbContextOptions options)` with `DbSet<FlatItem> FlatItems`, `DbSet<Review> Reviews`, `DbSet<Product> Products`. EF names the collections after the DbSet properties: `FlatItems`, `Reviews`, `Products`.
  - `BenchmarkSeeder.Seed(BenchmarkDbContext ctx, int flatCount, int productCount, int reviewCount)` — deterministic; sets up `EnsureCreated`, inserts the rows, wires each Review to a Product by FK.

- [ ] **Step 1: Create `Model.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

public class FlatItem
{
    public ObjectId Id { get; set; }
    public int Count { get; set; }
    public long Big { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public double Rate { get; set; }
}

public class Review
{
    public ObjectId Id { get; set; }
    public int Stars { get; set; }
    public ObjectId ProductId { get; set; }   // FK on the dependent (Review)
    public Product? Product { get; set; }      // reference navigation to the principal
}

public class Product
{
    public ObjectId Id { get; set; }
    public string Title { get; set; } = "";
}

public class BenchmarkDbContext : DbContext
{
    public BenchmarkDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<FlatItem> FlatItems => Set<FlatItem>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FlatItem>();
        modelBuilder.Entity<Review>().HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId);
    }
}
```

- [ ] **Step 2: Create `BenchmarkSeeder.cs`**

```csharp
namespace MongoDB.EntityFrameworkCore.Benchmarks;

public static class BenchmarkSeeder
{
    // Deterministic: same inputs → same documents, so DriverOnly and EF read identical data.
    public static void Seed(BenchmarkDbContext ctx, int flatCount, int productCount, int reviewCount)
    {
        ctx.Database.EnsureCreated();

        for (var i = 0; i < flatCount; i++)
        {
            ctx.FlatItems.Add(new FlatItem
            {
                Count = i,
                Big = 1_000_000_000L + i,
                Name = "flat-" + i,
                Active = (i % 2) == 0,
                Rate = i * 0.5
            });
        }
        ctx.SaveChanges();

        var products = new List<Product>(productCount);
        for (var i = 0; i < productCount; i++)
        {
            var product = new Product { Title = "product-" + i };
            products.Add(product);
            ctx.Products.Add(product);
        }
        ctx.SaveChanges();

        for (var i = 0; i < reviewCount; i++)
        {
            ctx.Reviews.Add(new Review
            {
                Stars = (i % 5) + 1,
                ProductId = products[i % products.Count].Id
            });
        }
        ctx.SaveChanges();
    }
}
```

- [ ] **Step 3: Build to verify the model compiles**

Run: `dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/BenchmarkSeeder.cs
git commit -m "EF-324: benchmark model (FlatItem, Review/Product) + deterministic seeder"
```

---

### Task 3: Smoke correctness gate

This is the correctness gate for the whole harness: before trusting any number, prove the seeded
documents round-trip and the Include resolves. No benchmark numbers here.

**Files:**
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs`

**Interfaces:**
- Consumes: `BenchmarkDbContext`, `BenchmarkSeeder.Seed`, `UseMongoDB(connectionString, databaseName)`.

- [ ] **Step 1: Replace `Program.cs` with the `--smoke` dispatch**

```csharp
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

Console.WriteLine("benchmarks: pass --smoke for the correctness check (headline benchmarks land in a later task)");
```

- [ ] **Step 2: Build**

Run: `dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Run the smoke check against a replica set**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10" -- --smoke`
Expected: a line `SMOKE OK: flat=100, flatActive=50, tracked=100, reviews=100, withProduct=100` and exit code 0. (If it throws, the harness/model is wrong — fix before proceeding; no benchmark is trustworthy until this passes.)

- [ ] **Step 4: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs
git commit -m "EF-324: --smoke correctness gate (seed + round-trip + Include validation)"
```

---

### Task 4: Headline benchmarks (DriverOnly + EF current provider)

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs`
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs` (default branch runs the benchmarks)

**Interfaces:**
- Consumes: `BenchmarkConfig`, `BenchmarkDbContext`, `BenchmarkSeeder.Seed`, `UseMongoDB(IMongoClient, databaseName)`.
- Produces: `HeadlineBenchmarks` with `[Benchmark]` methods `WhereToList_DriverOnly` / `WhereToList_EF`, `WholeEntityToList_DriverOnly` / `_EF_NoTracking` / `_EF_Tracked`, `OrderByTake_DriverOnly` / `_EF`, `ReferenceInclude_DriverOnly` / `_EF`. *(The native config — `*_EF_Native` — is added in sub-project 1.)*

- [ ] **Step 1: Create `HeadlineBenchmarks.cs`**

```csharp
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
```

- [ ] **Step 2: Update `Program.cs` default branch to run the benchmarks**

Replace the final `Console.WriteLine(...)` line from Task 3 with:

```csharp
BenchmarkDotNet.Running.BenchmarkRunner.Run<HeadlineBenchmarks>();
```

- [ ] **Step 3: Build**

Run: `dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Quick single-shape run to confirm the harness produces a table**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10" -- --filter "*WhereToList*"`
Expected: BenchmarkDotNet runs `WhereToList_DriverOnly` and `WhereToList_EF`, `[GlobalSetup]`'s `Validate()` does not throw, and a summary table with `Mean` and `Allocated` columns prints. (BenchmarkDotNet CLI `--filter` still applies under the `[Config]`.)

- [ ] **Step 5: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs
git commit -m "EF-324: two-config headline benchmarks (DriverOnly + EF current provider)"
```

---

### Task 5: Capture + commit the perf baseline

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-23-baseline.md`

- [ ] **Step 1: Run the full headline set**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"`
Expected: a BenchmarkDotNet summary table for all eight methods with `Mean`, `Error`, `StdDev`, and `Allocated`.

- [ ] **Step 2: Record the baseline**

Create `results/2026-06-23-baseline.md` with: the date, the run environment (BenchmarkDotNet's printed host block — OS, CPU, .NET SDK), `N = 10_000`, and a table of the eight methods with `Mean` and `Allocated` copied from the summary. Add a one-line note: "current-provider + driver-only floor on `main`; the EF-Native column is added in sub-project 1." Use this skeleton (fill the numbers from the actual run — do not invent them):

```markdown
# Perf baseline — main (current provider) — 2026-06-23

Env: <paste BenchmarkDotNet host block>
N = 10,000. Config: Release EF10, InProcessEmitToolchain, 3 warmup / 10 iterations, MemoryDiagnoser.

| Shape | Config | Mean | Allocated |
|---|---|---|---|
| WhereToList | DriverOnly | … | … |
| WhereToList | EF | … | … |
| WholeEntityToList | DriverOnly | … | … |
| WholeEntityToList | EF (no-track) | … | … |
| WholeEntityToList | EF (tracked) | … | … |
| OrderByTake | DriverOnly | … | … |
| OrderByTake | EF | … | … |
| ReferenceInclude | DriverOnly | … | … |
| ReferenceInclude | EF | … | … |

Note: current-provider baseline + driver-only floor. The EF-Native column is added in sub-project 1.
```

- [ ] **Step 3: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-23-baseline.md
git commit -m "EF-324: record current-provider perf baseline (Release EF10)"
```

---

### Task 6: Conformance baseline snapshot

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-23-query-conformance-baseline.md`

This records the spec/functional Query "before" — no product code, just a recorded, repeatable snapshot.

- [ ] **Step 1: Run the Query specification tests (EF10)**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"`
Expected: a final summary line with `Passed: <p>`, `Failed: <f>`, `Skipped: <s>`.

- [ ] **Step 2: Run the Query functional tests (EF10)**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"`
Expected: a final summary line with `Passed`/`Failed`/`Skipped`.

- [ ] **Step 3: Record the snapshot**

Create `results/2026-06-23-query-conformance-baseline.md` capturing, for each of the two suites: the Passed / Failed / Skipped counts from this run, the exact commands above (so it is repeatable), and the commit SHA of `main` they were taken at (`git rev-parse HEAD`). This is the conformance "before" that sub-project 1's "no shrink / zero regressions" bar is measured against.

```markdown
# Query conformance baseline — main — 2026-06-23

main @ <git rev-parse HEAD>. Config: Debug EF10. Filter: FullyQualifiedName~Query.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| SpecificationTests (Query) | … | … | … |
| FunctionalTests (Query) | … | … | … |

Repro: the two `dotnet test … --filter "FullyQualifiedName~Query"` commands in the plan (Task 6).
```

- [ ] **Step 4: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-23-query-conformance-baseline.md
git commit -m "EF-324: record Query spec/functional conformance baseline on main"
```

---

## Notes for the implementer

- **Run order matters for the gates:** Task 3's `--smoke` must pass before Task 4/5 numbers mean anything; if `Validate()` in Task 4 throws, the DriverOnly and EF reads diverge (usually a collection-name or mapping mismatch) — fix that, don't record numbers.
- **Collection names:** EF maps each entity to a collection named after its `DbSet` property (`FlatItems`, `Reviews`, `Products`). The DriverOnly methods open those exact names; `Validate()` is what catches a mismatch.
- **Fairness:** one shared `MongoClient` across DriverOnly and EF (already in `[GlobalSetup]`). Do not construct a client per context.
- **No solution entry:** never add this project to `MongoDB.EFCoreProvider.sln`; the `/test-all` flow and the multi-config build must not pick it up.

---

# Extended cases (added 2026-06-24 — EF-324 follow-on)

Three heavier benchmark cases, in a **separate** `ExtendedBenchmarks` class run via `--extended`,
leaving the headline set (and its baseline) untouched:

- **Case A — complex combined query** over `Order`: filter (nested-owned scalar + own prop) +
  reference `Include(Account)` + multi-key `OrderByDescending`/`ThenBy` + `Skip`/`Take`.
- **Case B — wide entity** (`WideEntity`, 400 scalar properties): whole-entity `ToList`.
- **Case C — deep owned nesting** (`Order`): whole-entity `ToList` materializing owned-within-owned
  and a collection-within-a-collection-element.

All cases follow the SP0 discipline: a DriverOnly arm (perf floor) + an EF arm, a `Validate()`
cross-check in `[GlobalSetup]`, an `--extended` smoke gate, and a committed baseline.

**"Currently works" is verified, not assumed:** Task 7's `--extended-smoke` is the support probe. If a
shape (especially the collection-within-collection `Discounts`, or filter/sort on a nested-owned
scalar) does not translate on the current provider, the smoke throws — the implementer reports BLOCKED
with the error and the controller adjusts the shape (do **not** weaken assertions to force a pass).

### Task 7: Extended model + extended-smoke (support gate)

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.Extended.cs` (Account + Order graph)
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/WideEntity.cs` (generated — 400 props + `Fill`)
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.cs` (add DbSets + `OnModelCreating` config)
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/BenchmarkSeeder.cs` (add `SeedExtended`)
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs` (add `--extended-smoke`)

**Interfaces produced (later tasks depend on these exact names):**
- Entities below; `BenchmarkDbContext` gains `DbSet<Account> Accounts`, `DbSet<Order> Orders`, `DbSet<WideEntity> Wides`.
- `BenchmarkSeeder.SeedExtended(BenchmarkDbContext ctx, int accountCount, int orderCount, int wideCount)`.
- `WideEntity` has `ObjectId Id`, `Prop001..Prop400`, and `WideEntity Fill(int i)` (sets all 400 deterministically, returns `this`).

- [ ] **Step 1: Create `Model.Extended.cs`**

```csharp
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

public class Account                       // principal for Order's reference navigation
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
}

public class Order
{
    public ObjectId Id { get; set; }
    public string Code { get; set; } = "";
    public ObjectId AccountId { get; set; }            // FK to Account
    public Account? Account { get; set; }              // reference navigation (Case A Include)
    public ShippingInfo Shipping { get; set; } = new();// owned
    public List<LineItem> Lines { get; set; } = new(); // owned collection
}

public class ShippingInfo                  // owned
{
    public string Carrier { get; set; } = "";
    public OrderAddress Address { get; set; } = new(); // nested owned
}

public class OrderAddress                  // owned (nested under ShippingInfo)
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public int Zip { get; set; }
}

public class LineItem                      // owned-collection element
{
    public string Sku { get; set; } = "";
    public int Qty { get; set; }
    public ItemMeta Meta { get; set; } = new();          // nested owned (inside collection element)
    public List<Discount> Discounts { get; set; } = new(); // nested owned collection (inside element)
}

public class ItemMeta { public string Category { get; set; } = ""; public double Weight { get; set; } }
public class Discount { public string Kind { get; set; } = ""; public decimal Amount { get; set; } }
```

- [ ] **Step 2: Generate `WideEntity.cs` (400 properties + Fill)**

Do not hand-type 400 properties. Generate the file with this exact recipe, then commit the generated
`.cs` (not the script). Run from the repo root:

```bash
python3 - <<'PY' > benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/WideEntity.cs
print("using MongoDB.Bson;\n")
print("namespace MongoDB.EntityFrameworkCore.Benchmarks;\n")
print("// Generated: 400 scalar properties cycling int/long/double/bool/string. Do not edit by hand.")
print("public class WideEntity")
print("{")
print("    public ObjectId Id { get; set; }")
types = ["int","long","double","bool","string"]
for i in range(1,401):
    t = types[(i-1)%5]
    init = ' = "";' if t == "string" else ""
    print(f"    public {t} Prop{i:03d} {{ get; set; }}{init}")
print()
print("    public WideEntity Fill(int i)")
print("    {")
for i in range(1,401):
    t = types[(i-1)%5]
    n = f"Prop{i:03d}"
    if t == "int":    print(f"        {n} = i + {i};")
    elif t == "long": print(f"        {n} = 1_000_000_000L + i + {i};")
    elif t == "double": print(f"        {n} = (i + {i}) * 0.5;")
    elif t == "bool": print(f"        {n} = ((i + {i}) % 2) == 0;")
    else:             print(f"        {n} = \"p{i:03d}-\" + i;")
print("        return this;")
print("    }")
print("}")
PY
```

- [ ] **Step 3: Extend `BenchmarkDbContext` in `Model.cs`**

Add three DbSet properties and the owned/relationship config. Insert the DbSets next to the existing ones:

```csharp
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<WideEntity> Wides => Set<WideEntity>();
```

And add to the existing `OnModelCreating` body (after the existing `FlatItem`/`Review` config):

```csharp
        modelBuilder.Entity<Account>();
        modelBuilder.Entity<WideEntity>();
        modelBuilder.Entity<Order>(b =>
        {
            b.HasOne(o => o.Account).WithMany().HasForeignKey(o => o.AccountId);
            b.OwnsOne(o => o.Shipping, s => s.OwnsOne(si => si.Address));
            b.OwnsMany(o => o.Lines, l =>
            {
                l.OwnsOne(li => li.Meta);
                l.OwnsMany(li => li.Discounts);
            });
        });
```

- [ ] **Step 4: Add `SeedExtended` to `BenchmarkSeeder.cs`**

```csharp
    public static void SeedExtended(BenchmarkDbContext ctx, int accountCount, int orderCount, int wideCount)
    {
        ctx.Database.EnsureCreated();

        var accounts = new List<Account>(accountCount);
        for (var i = 0; i < accountCount; i++)
        {
            var a = new Account { Name = "account-" + i, Tier = (i % 3) == 0 ? "gold" : "standard" };
            accounts.Add(a);
            ctx.Accounts.Add(a);
        }
        ctx.SaveChanges();

        for (var i = 0; i < orderCount; i++)
        {
            var order = new Order
            {
                Code = "order-" + i,
                AccountId = accounts[i % accounts.Count].Id,
                Shipping = new ShippingInfo
                {
                    Carrier = (i % 2) == 0 ? "air" : "ground",
                    Address = new OrderAddress { Street = "street-" + i, City = "city-" + (i % 50), Zip = 10_000 + (i % 2000) }
                }
            };
            for (var j = 0; j < 3; j++)
            {
                var line = new LineItem
                {
                    Sku = $"sku-{i}-{j}",
                    Qty = j + 1,
                    Meta = new ItemMeta { Category = "cat-" + (j % 4), Weight = (j + 1) * 0.25 }
                };
                for (var k = 0; k < 2; k++)
                {
                    line.Discounts.Add(new Discount { Kind = "k" + k, Amount = 0.5m * (k + 1) });
                }
                order.Lines.Add(line);
            }
            ctx.Orders.Add(order);
        }

        for (var i = 0; i < wideCount; i++)
        {
            ctx.Wides.Add(new WideEntity().Fill(i));
        }
        ctx.SaveChanges();
    }
```

- [ ] **Step 5: Add `--extended-smoke` to `Program.cs`**

Add this branch BEFORE the existing `--smoke` branch (so `--extended-smoke` matches first), mirroring the existing smoke structure. It seeds small, reads back in fresh contexts, and validates every new shape — this is the support probe:

```csharp
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
```

- [ ] **Step 6: Build, then run the extended smoke (the support gate)**

Build: `dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10"` → Build succeeded.

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10" -- --extended-smoke`
Expected: `EXT SMOKE OK: orders=50, lines=150, discounts=300, withAccount=50, wides=100` and exit 0.
**If it throws** (e.g. the nested owned `Discounts` collection or a nested-owned filter isn't supported by the current provider), STOP and report **BLOCKED** with the exact exception — do not weaken the model or the assertions. The controller will adjust the shape.

- [ ] **Step 7: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.Extended.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/WideEntity.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Model.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/BenchmarkSeeder.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs
git commit -m "EF-324: extended benchmark model (wide entity, deep-owned Order) + --extended-smoke gate"
```

### Task 8: Extended benchmarks (Cases A/B/C)

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/ExtendedBenchmarks.cs`
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs` (add `--extended` → run `ExtendedBenchmarks`)

**Interfaces consumed:** `BenchmarkConfig`, `BenchmarkDbContext`, `BenchmarkSeeder.SeedExtended`, `UseMongoDB(IMongoClient, databaseName)`, the entities + `WideEntity` from Task 7.

Counts: `ACCOUNT_N = 200`, `ORDER_N = 10_000`, `WIDE_N = 5_000` (400 props × rows is heavy — fewer rows keeps runtime bounded; recorded in the baseline doc).

- [ ] **Step 1: Create `ExtendedBenchmarks.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
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
    private IMongoCollection<Account> _accountColl = null!;
    private IMongoCollection<WideEntity> _wideColl = null!;
    private MongoClient _client = null!;
    private string _dbName = null!;

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
        _accountColl = db.GetCollection<Account>("Accounts");
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

    // DriverOnly: same logical pipeline as a hand-written aggregate (match → lookup Account → sort → skip → limit).
    private int DriverComplexQuery()
    {
        var filter = Builders<Order>.Filter.Gt("Shipping.Address.Zip", ZipThreshold)
                   & Builders<Order>.Filter.Ne<string?>("Code", null);
        var sort = Builders<Order>.Sort.Descending("Shipping.Address.City").Ascending("_id");

        var page = _orderColl.Aggregate()
            .Match(filter)
            .Lookup<Order, Account, OrderWithAccount>(
                foreignCollection: _accountColl,
                localField: o => o.AccountId,
                foreignField: a => a.Id,
                @as: x => x.Accounts)
            .Sort(sort)
            .Skip(50)
            .Limit(1500)
            .ToList();
        return page.Count;
    }

    private sealed class OrderWithAccount
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public List<Account> Accounts { get; set; } = new();
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
```

- [ ] **Step 2: Add `--extended` dispatch to `Program.cs`**

Add before the final headline `BenchmarkRunner.Run<HeadlineBenchmarks>()` line:

```csharp
if (args.Contains("--extended"))
{
    BenchmarkDotNet.Running.BenchmarkRunner.Run<ExtendedBenchmarks>();
    return;
}
```

- [ ] **Step 3: Build, then quick single-shape run**

Build: `dotnet build ... -c "Release EF10"` → succeeded.
Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10" -- --extended --filter "*WideWholeEntity*"`
Expected: `Validate()` does not throw (it runs all cross-checks in GlobalSetup), and a summary table for the two `WideWholeEntity` methods prints. If `Validate()` throws, a DriverOnly/EF divergence exists — investigate; do not weaken it. If the driver aggregate fluent API in `DriverComplexQuery` needs a syntax fix to compile/run, fix it so it produces the same count EF does (that is the gate); report what you changed.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/ExtendedBenchmarks.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs
git commit -m "EF-324: extended benchmarks (complex query, wide entity, deep owned nesting)"
```

### Task 9: Capture extended baseline

**Files:**
- Create: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-24-extended-baseline.md`

- [ ] **Step 1: Run the full extended set**

Run: `MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet run --project benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/MongoDB.EntityFrameworkCore.Benchmarks.csproj -c "Release EF10" -- --extended`
Expected: a BenchmarkDotNet summary for all six methods (ComplexQuery, WideWholeEntity, NestedWholeEntity × DriverOnly/EF).

- [ ] **Step 2: Record the baseline** (same rules as Task 5 — REAL numbers, no fabrication)

Create `results/2026-06-24-extended-baseline.md` with the host block, the counts (`ACCOUNT_N=200, ORDER_N=10,000, WIDE_N=5,000`, and the `Order` shape: 3 lines × 2 discounts), the config line, and a table of the six methods (Mean + Allocated copied from the run). Add the note: "current-provider + driver-only floor; EF-Native columns added as later sub-projects gain support for these shapes."

- [ ] **Step 3: Commit**

```bash
git add benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-06-24-extended-baseline.md
git commit -m "EF-324: record extended-case perf baseline (Release EF10)"
```
