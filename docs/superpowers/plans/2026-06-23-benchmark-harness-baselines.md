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
