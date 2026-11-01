using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// A catalogue of query shapes from trivial to complex, each run three ways like the other benchmark classes:
//   DriverOnly     - the same LINQ written directly against the driver (the floor; no EF Core).
//   EF_DriverLinq  - EF provider with UseQueryMode(DriverLinq).   (EF contexts come from a context pool, as in AddDbContextPool.)
//   EF_Native      - EF provider with UseQueryMode(Native).
// A shape is a (driver, EF) pair of delegates returning a checksum, so every shape is validated at setup: all three configs must return
// the same number before any timing is taken. Run with --shapes; narrow with --filter '*Native*shape: Group*'. Tiers, in order:
//   scalar      - Count/Any/Sum/Max/Average over FlatItems
//   filter      - Where on equality, range, prefix, substring and a 200-value IN list
//   projection  - one scalar, an anonymous pair, computed members, a DTO, a few members of a 40-property entity
//   shaping     - top-N, a deep page, Distinct, Concat
//   grouping    - GroupBy: few and many groups (up to one per row), composite and joined keys, many accumulators, a filter before and a
//                 having after, ordering and paging over the groups, and a list-valued group element
//   owned       - nested owned references and collections (Orders), filters, projections and counts over them
//   relations   - reference Include, navigation projection, explicit Join
//   tracking    - the same selective whole-entity query untracked and tracked
//   includes    - collection Include (untracked, tracked, filtered, ThenInclude), several reference Includes, Include under paging and
//                 a count over a collection navigation (Categories 100, Articles 10,000, Authors 500). Not here because the provider
//                 does not translate them: a filtered Include (Include(c => c.Articles.Where(..).Take(n))) and a projection that
//                 returns a navigation's elements (Select(c => c.Articles.Select(a => a.Title).ToList())).
// FlatItems, Reviews, Products, Orders, Accounts and Mediums are all 10,000 rows (100 products, 200 accounts).
[Config(typeof(BenchmarkConfig))]
public class QueryShapeBenchmarks
{
    private const int N = 10_000;

    private static readonly int[] InList = Enumerable.Range(0, 200).Select(i => i * 50).ToArray();

    // The hand-written $lookup targets of the Include / navigation shapes: the driver has no Include.
    public sealed class OrderWithAccounts
    {
        public ObjectId Id { get; set; }
        public string Code { get; set; } = "";
        public ObjectId AccountId { get; set; }
        public ShippingInfo Shipping { get; set; } = new();
        public List<LineItem> Lines { get; set; } = new();
        public List<Account> Accounts { get; set; } = new();
    }

    public sealed class FlatDto
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public double Rate { get; set; }
    }

    // Hand-written $lookup targets of the include shapes: the driver has no Include.
    public sealed class ArticleWithRefs
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Views { get; set; }
        public ObjectId CategoryId { get; set; }
        public ObjectId AuthorId { get; set; }
        public List<Category> Categories { get; set; } = new();
        public List<Author> Authors { get; set; } = new();
    }

    public sealed class CategoryWithArticleRefs
    {
        public ObjectId Id { get; set; }
        public int Rank { get; set; }
        public string Name { get; set; } = "";
        public List<ArticleWithRefs> Articles { get; set; } = new();
    }

    public sealed class FlatWrap(FlatItem item)
    {
        public FlatItem Item { get; } = item;
    }

    public sealed class CountRow
    {
        public string Name { get; set; } = "";
        public int N { get; set; }
    }

    public sealed class Collections
    {
        public IMongoCollection<Category> Categories = null!;
        public IMongoCollection<Article> Articles = null!;
        public IMongoCollection<Author> Authors = null!;
        public IMongoCollection<FlatItem> Flat = null!;
        public IMongoCollection<Review> Reviews = null!;
        public IMongoCollection<Product> Products = null!;
        public IMongoCollection<Order> Orders = null!;
        public IMongoCollection<Account> Accounts = null!;
        public IMongoCollection<MediumEntity> Mediums = null!;
    }

    public sealed class Shape
    {
        public Shape(string tier, string name, Func<Collections, int>? driver, Func<BenchmarkDbContext, int> ef)
        {
            Tier = tier;
            Name = name;
            Driver = driver;
            Ef = ef;
        }

        public string Tier { get; }
        public string Name { get; }
        public Func<Collections, int>? Driver { get; }
        public Func<BenchmarkDbContext, int> Ef { get; }

        public override string ToString() => Name;
    }

    private static IQueryable<FlatItem> Flat(BenchmarkDbContext c) => c.FlatItems.AsNoTracking();
    private static IQueryable<Order> Orders(BenchmarkDbContext c) => c.Orders.AsNoTracking();

    public static readonly Shape[] AllShapes =
    {
        // ----- scalar -----
        new("scalar", "CountAll", c => c.Flat.AsQueryable().Count(), c => Flat(c).Count()),
        new("scalar", "CountWhere", c => c.Flat.AsQueryable().Count(f => f.Active && f.Count > 2500), c => Flat(c).Count(f => f.Active && f.Count > 2500)),
        new("scalar", "AnyMatch", c => c.Flat.AsQueryable().Any(f => f.Count == 9999) ? 1 : 0, c => Flat(c).Any(f => f.Count == 9999) ? 1 : 0),
        new("scalar", "SumRate", c => (int)c.Flat.AsQueryable().Sum(f => f.Rate), c => (int)Flat(c).Sum(f => f.Rate)),
        new("scalar", "MaxCount", c => c.Flat.AsQueryable().Max(f => f.Count), c => Flat(c).Max(f => f.Count)),
        new("scalar", "AverageWhere", c => (int)c.Flat.AsQueryable().Where(f => f.Active).Average(f => f.Rate), c => (int)Flat(c).Where(f => f.Active).Average(f => f.Rate)),

        // ----- filter (whole entity) -----
        new("filter", "FilterEquals", c => c.Flat.AsQueryable().Where(f => f.Count == 4242).ToList().Count, c => Flat(c).Where(f => f.Count == 4242).ToList().Count),
        new("filter", "FilterRange", c => c.Flat.AsQueryable().Where(f => f.Count >= 2000 && f.Count < 3000 && f.Active).ToList().Count,
            c => Flat(c).Where(f => f.Count >= 2000 && f.Count < 3000 && f.Active).ToList().Count),
        new("filter", "FilterPrefix", c => c.Flat.AsQueryable().Where(f => f.Name.StartsWith("flat-12")).ToList().Count,
            c => Flat(c).Where(f => f.Name.StartsWith("flat-12")).ToList().Count),
        new("filter", "FilterContains", c => c.Flat.AsQueryable().Where(f => f.Name.Contains("99")).ToList().Count,
            c => Flat(c).Where(f => f.Name.Contains("99")).ToList().Count),
        new("filter", "FilterInList", c => c.Flat.AsQueryable().Where(f => InList.Contains(f.Count)).ToList().Count,
            c => Flat(c).Where(f => InList.Contains(f.Count)).ToList().Count),

        // ----- projection -----
        new("projection", "ProjectScalar", c => c.Flat.AsQueryable().Select(f => f.Count).ToList().Count, c => Flat(c).Select(f => f.Count).ToList().Count),
        new("projection", "ProjectPair", c => c.Flat.AsQueryable().Select(f => new { f.Id, f.Name }).ToList().Count,
            c => Flat(c).Select(f => new { f.Id, f.Name }).ToList().Count),
        new("projection", "ProjectComputed", c => c.Flat.AsQueryable().Select(f => new { f.Id, Score = f.Count * 2L + f.Big, f.Name.Length }).ToList().Count,
            c => Flat(c).Select(f => new { f.Id, Score = f.Count * 2L + f.Big, f.Name.Length }).ToList().Count),
        new("projection", "ProjectDto", c => c.Flat.AsQueryable().Select(f => new FlatDto { Id = f.Id, Name = f.Name, Rate = f.Rate }).ToList().Count,
            c => Flat(c).Select(f => new FlatDto { Id = f.Id, Name = f.Name, Rate = f.Rate }).ToList().Count),
        new("projection", "MediumProjectFew", c => c.Mediums.AsQueryable().Select(m => new { m.Id, m.Prop001, m.Prop005 }).ToList().Count,
            c => c.Mediums.AsNoTracking().Select(m => new { m.Id, m.Prop001, m.Prop005 }).ToList().Count),
        new("projection", "MediumFilterProject", c => c.Mediums.AsQueryable().Where(m => m.Prop001 > 5000 && m.Prop004).Select(m => new { m.Id, m.Prop003, m.Prop010 }).ToList().Count,
            c => c.Mediums.AsNoTracking().Where(m => m.Prop001 > 5000 && m.Prop004).Select(m => new { m.Id, m.Prop003, m.Prop010 }).ToList().Count),

        // ----- shaping -----
        new("shaping", "TopTen", c => c.Flat.AsQueryable().OrderByDescending(f => f.Rate).Take(10).ToList().Count,
            c => Flat(c).OrderByDescending(f => f.Rate).Take(10).ToList().Count),
        new("shaping", "DeepPage", c => c.Flat.AsQueryable().Where(f => f.Active).OrderBy(f => f.Count).Skip(1000).Take(20).ToList().Count,
            c => Flat(c).Where(f => f.Active).OrderBy(f => f.Count).Skip(1000).Take(20).ToList().Count),
        new("shaping", "Distinct", c => c.Flat.AsQueryable().Select(f => f.Count % 100).Distinct().ToList().Count,
            c => Flat(c).Select(f => f.Count % 100).Distinct().ToList().Count),
        new("shaping", "ConcatFilters", c => c.Flat.AsQueryable().Where(f => f.Count < 100).Select(f => f.Name).Concat(c.Flat.AsQueryable().Where(f => f.Count > 9900).Select(f => f.Name)).ToList().Count,
            c => Flat(c).Where(f => f.Count < 100).Select(f => f.Name).Concat(Flat(c).Where(f => f.Count > 9900).Select(f => f.Name)).ToList().Count),

        // ----- grouping -----
        new("grouping", "GroupByCount", c => c.Flat.AsQueryable().GroupBy(f => f.Count % 10).Select(g => new { g.Key, N = g.Count(), Avg = g.Average(x => x.Rate) }).ToList().Count,
            c => Flat(c).GroupBy(f => f.Count % 10).Select(g => new { g.Key, N = g.Count(), Avg = g.Average(x => x.Rate) }).ToList().Count),
        new("grouping", "GroupByOwned", c => c.Orders.AsQueryable().GroupBy(o => o.Shipping.Address.City).Select(g => new { City = g.Key, N = g.Count(), MaxZip = g.Max(o => o.Shipping.Address.Zip) }).ToList().Count,
            c => Orders(c).GroupBy(o => o.Shipping.Address.City).Select(g => new { City = g.Key, N = g.Count(), MaxZip = g.Max(o => o.Shipping.Address.Zip) }).ToList().Count),
        new("grouping", "GroupByTopThree", c => c.Orders.AsQueryable().GroupBy(o => o.Shipping.Address.City).Select(g => new { City = g.Key, N = g.Count() }).OrderByDescending(x => x.N).ThenBy(x => x.City).Take(3).ToList().Count,
            c => Orders(c).GroupBy(o => o.Shipping.Address.City).Select(g => new { City = g.Key, N = g.Count() }).OrderByDescending(x => x.N).ThenBy(x => x.City).Take(3).ToList().Count),

        // Result-row counts matter for a reader: the first three above return 3-50 rows, which cannot show a per-row cost, so these add
        // high-cardinality, composite-key, many-accumulator, filtered, having, joined-key and list-valued groupings.
        new("grouping", "GroupByThousandKeys",
            c => c.Flat.AsQueryable().GroupBy(f => f.Count % 1000).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N),
            c => Flat(c).GroupBy(f => f.Count % 1000).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N)),
        new("grouping", "GroupByUniqueKey",
            c => c.Flat.AsQueryable().GroupBy(f => f.Count).Select(g => new { g.Key, Total = g.Sum(x => x.Rate) }).ToList().Count,
            c => Flat(c).GroupBy(f => f.Count).Select(g => new { g.Key, Total = g.Sum(x => x.Rate) }).ToList().Count),
        new("grouping", "GroupByCompositeKey",
            c => c.Flat.AsQueryable().GroupBy(f => new { f.Active, B = f.Count % 100 })
                .Select(g => new { g.Key.Active, g.Key.B, N = g.Count(), Max = g.Max(x => x.Rate) }).ToList().Sum(r => r.N),
            c => Flat(c).GroupBy(f => new { f.Active, B = f.Count % 100 })
                .Select(g => new { g.Key.Active, g.Key.B, N = g.Count(), Max = g.Max(x => x.Rate) }).ToList().Sum(r => r.N)),
        new("grouping", "GroupByManyAccumulators",
            c => c.Flat.AsQueryable().GroupBy(f => f.Count % 10)
                .Select(g => new { g.Key, N = g.Count(), Sum = g.Sum(x => x.Big), Min = g.Min(x => x.Count), Max = g.Max(x => x.Count), Avg = g.Average(x => x.Rate) })
                .ToList().Sum(r => r.N),
            c => Flat(c).GroupBy(f => f.Count % 10)
                .Select(g => new { g.Key, N = g.Count(), Sum = g.Sum(x => x.Big), Min = g.Min(x => x.Count), Max = g.Max(x => x.Count), Avg = g.Average(x => x.Rate) })
                .ToList().Sum(r => r.N)),
        new("grouping", "GroupByFiltered",
            c => c.Flat.AsQueryable().Where(f => f.Active).GroupBy(f => f.Count % 100).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N),
            c => Flat(c).Where(f => f.Active).GroupBy(f => f.Count % 100).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N)),
        new("grouping", "GroupByHaving",
            c => c.Flat.AsQueryable().GroupBy(f => f.Count % 1000).Where(g => g.Sum(x => x.Rate) > 25_000).Select(g => new { g.Key, Total = g.Sum(x => x.Rate) })
                .ToList().Count,
            c => Flat(c).GroupBy(f => f.Count % 1000).Where(g => g.Sum(x => x.Rate) > 25_000).Select(g => new { g.Key, Total = g.Sum(x => x.Rate) })
                .ToList().Count),
        new("grouping", "GroupByOwnedHighCardinality",
            c => c.Orders.AsQueryable().GroupBy(o => o.Shipping.Address.Zip).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N),
            c => Orders(c).GroupBy(o => o.Shipping.Address.Zip).Select(g => new { g.Key, N = g.Count() }).ToList().Sum(r => r.N)),
        new("grouping", "GroupByJoinedKey",
            c => c.Articles.AsQueryable().Join(c.Categories.AsQueryable(), a => a.CategoryId, ca => ca.Id, (a, ca) => new { a.Views, ca.Name })
                .GroupBy(x => x.Name).Select(g => new { g.Key, N = g.Count(), Views = g.Sum(x => x.Views) }).ToList().Sum(r => r.N),
            c => c.Articles.AsNoTracking().GroupBy(a => a.Category!.Name).Select(g => new { g.Key, N = g.Count(), Views = g.Sum(a => a.Views) })
                .ToList().Sum(r => r.N)),
        new("grouping", "GroupByThenOrderPage",
            c => c.Flat.AsQueryable().GroupBy(f => f.Count % 1000).Select(g => new { g.Key, N = g.Count(), Total = g.Sum(x => x.Rate) })
                .OrderByDescending(r => r.Total).ThenBy(r => r.Key).Skip(100).Take(100).ToList().Count,
            c => Flat(c).GroupBy(f => f.Count % 1000).Select(g => new { g.Key, N = g.Count(), Total = g.Sum(x => x.Rate) })
                .OrderByDescending(r => r.Total).ThenBy(r => r.Key).Skip(100).Take(100).ToList().Count),
        new("grouping", "GroupByWithElementList",
            c => c.Flat.AsQueryable().Where(f => f.Count < 2000).GroupBy(f => f.Count % 20).Select(g => new { g.Key, Names = g.Select(x => x.Name).ToList() })
                .ToList().Sum(r => r.Names.Count),
            c => Flat(c).Where(f => f.Count < 2000).GroupBy(f => f.Count % 20).Select(g => new { g.Key, Names = g.Select(x => x.Name).ToList() })
                .ToList().Sum(r => r.Names.Count)),

        // ----- owned -----
        new("owned", "OwnedFilter", c => c.Orders.AsQueryable().Where(o => o.Shipping.Carrier == "air" && o.Shipping.Address.Zip > 11000).ToList().Count,
            c => Orders(c).Where(o => o.Shipping.Carrier == "air" && o.Shipping.Address.Zip > 11000).ToList().Count),
        new("owned", "OwnedProject", c => c.Orders.AsQueryable().Select(o => new { o.Code, o.Shipping.Address.City, o.Shipping.Address.Zip }).ToList().Count,
            c => Orders(c).Select(o => new { o.Code, o.Shipping.Address.City, o.Shipping.Address.Zip }).ToList().Count),
        new("owned", "CollectionAny", c => c.Orders.AsQueryable().Where(o => o.Lines.Any(l => l.Qty >= 3) && o.Shipping.Address.Zip < 10200).ToList().Count,
            c => Orders(c).Where(o => o.Lines.Any(l => l.Qty >= 3) && o.Shipping.Address.Zip < 10200).ToList().Count),
        new("owned", "CollectionCount", c => c.Orders.AsQueryable().Select(o => new { o.Code, N = o.Lines.Count }).ToList().Count,
            c => Orders(c).Select(o => new { o.Code, N = o.Lines.Count }).ToList().Count),
        new("owned", "NestedCollectionAny", c => c.Orders.AsQueryable().Where(o => o.Lines.Any(l => l.Discounts.Count > 1) && o.Shipping.Address.Zip < 10100).ToList().Count,
            c => Orders(c).Where(o => o.Lines.Any(l => l.Discounts.Count > 1) && o.Shipping.Address.Zip < 10100).ToList().Count),
        new("owned", "FilterSortPageProject", c => c.Orders.AsQueryable().Where(o => o.Shipping.Address.Zip > 10500 && o.Shipping.Carrier == "air" && o.Code.StartsWith("order-"))
                .OrderBy(o => o.Shipping.Address.Zip).ThenBy(o => o.Id).Skip(100).Take(100).Select(o => new { o.Code, o.Shipping.Address.City }).ToList().Count,
            c => Orders(c).Where(o => o.Shipping.Address.Zip > 10500 && o.Shipping.Carrier == "air" && o.Code.StartsWith("order-"))
                .OrderBy(o => o.Shipping.Address.Zip).ThenBy(o => o.Id).Skip(100).Take(100).Select(o => new { o.Code, o.Shipping.Address.City }).ToList().Count),

        // ----- non-scalar projections: sub-documents, arrays, owned and joined entities, nested constructions, whole entity in a construction -----
        new("nonscalar", "ProjectOwnedReference",
            c => c.Orders.AsQueryable().Select(o => new { o.Code, o.Shipping }).ToList().Sum(r => r.Shipping.Address.Zip > 0 ? 1 : 0),
            c => Orders(c).Select(o => new { o.Code, o.Shipping }).ToList().Sum(r => r.Shipping.Address.Zip > 0 ? 1 : 0)),
        new("nonscalar", "ProjectOwnedCollection",
            c => c.Orders.AsQueryable().Select(o => new { o.Code, o.Lines }).ToList().Sum(r => r.Lines.Count),
            c => Orders(c).Select(o => new { o.Code, o.Lines }).ToList().Sum(r => r.Lines.Count)),
        new("nonscalar", "ProjectOwnedCollectionSelect",
            c => c.Orders.AsQueryable().Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() }).ToList().Sum(r => r.Skus.Count),
            c => Orders(c).Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() }).ToList().Sum(r => r.Skus.Count)),
        new("nonscalar", "ProjectNestedConstruction",
            c => c.Orders.AsQueryable().Select(o => new { o.Code, Where = new { o.Shipping.Address.City, o.Shipping.Address.Zip } }).ToList().Sum(r => r.Where.Zip > 0 ? 1 : 0),
            c => Orders(c).Select(o => new { o.Code, Where = new { o.Shipping.Address.City, o.Shipping.Address.Zip } }).ToList().Sum(r => r.Where.Zip > 0 ? 1 : 0)),
        new("nonscalar", "ProjectWholeEntityAndScalar",
            c => c.Flat.AsQueryable().Select(f => new { Item = f, f.Name.Length }).ToList().Sum(r => r.Length > 0 ? 1 : 0),
            c => Flat(c).Select(f => new { Item = f, f.Name.Length }).ToList().Sum(r => r.Length > 0 ? 1 : 0)),
        new("nonscalar", "ProjectWholeEntityAndScalarTracked",
            c => c.Flat.AsQueryable().Select(f => new { Item = f, f.Name.Length }).ToList().Sum(r => r.Length > 0 ? 1 : 0),
            c => c.FlatItems.Select(f => new { Item = f, f.Name.Length }).ToList().Sum(r => r.Length > 0 ? 1 : 0)),
        new("nonscalar", "ProjectJoinedEntityTracked",
            c => c.Reviews.Aggregate().Lookup<Review, Product, HeadlineBenchmarks.ReviewWithProduct>(c.Products, r => r.ProductId, p => p.Id, rp => rp.Products)
                .ToList().Count(rp => rp.Products.Count > 0),
            c => c.Reviews.Select(r => new { r.Stars, r.Product }).ToList().Count(r => r.Product != null)),
        new("nonscalar", "ProjectCtorWrappedEntity",
            c => c.Flat.AsQueryable().ToList().Select(f => new FlatWrap(f)).ToList().Count,
            c => Flat(c).Select(f => new FlatWrap(f)).ToList().Count),
        new("nonscalar", "ProjectCtorWrappedEntityTracked",
            c => c.Flat.AsQueryable().ToList().Select(f => new FlatWrap(f)).ToList().Count,
            c => c.FlatItems.Select(f => new FlatWrap(f)).ToList().Count),
        new("nonscalar", "ProjectJoinedEntity",
            c => c.Reviews.Aggregate().Lookup<Review, Product, HeadlineBenchmarks.ReviewWithProduct>(c.Products, r => r.ProductId, p => p.Id, rp => rp.Products)
                .ToList().Count(rp => rp.Products.Count > 0),
            c => c.Reviews.AsNoTracking().Select(r => new { r.Stars, r.Product }).ToList().Count(r => r.Product != null)),
        new("nonscalar", "ProjectCorrelatedMax",
            c => c.Categories.Aggregate(PipelineDefinition<Category, CountRow>.Create(new[]
                {
                    BsonDocument.Parse("{ $lookup: { from: 'Articles', localField: '_id', foreignField: 'CategoryId', as: 'A' } }"),
                    BsonDocument.Parse("{ $project: { Name: 1, N: { $max: '$A.Views' }, _id: 0 } }")
                })).ToList().Sum(r => r.N),
            c => c.Categories.AsNoTracking().Select(x => new { x.Name, N = x.Articles.Max(a => a.Views) }).ToList().Sum(r => r.N)),

        // ----- relations -----
        new("relations", "IncludeFiltered", c => c.Orders.Aggregate().Match(o => o.Shipping.Address.Zip < 10500)
                .Lookup<Order, Account, OrderWithAccounts>(c.Accounts, o => o.AccountId, a => a.Id, o => o.Accounts).ToList().Count(o => o.Accounts.Count > 0),
            c => Orders(c).Where(o => o.Shipping.Address.Zip < 10500).Include(o => o.Account).ToList().Count(o => o.Account != null)),
        new("relations", "NavigationProject", c => c.Reviews.Aggregate().Lookup<Review, Product, HeadlineBenchmarks.ReviewWithProduct>(c.Products, r => r.ProductId, p => p.Id, rp => rp.Products)
                .ToList().Count(rp => rp.Products.Count > 0),
            c => c.Reviews.AsNoTracking().Select(r => new { r.Stars, r.Product!.Title }).ToList().Count),
        new("relations", "ExplicitJoin", c => c.Orders.AsQueryable().Join(c.Accounts.AsQueryable(), o => o.AccountId, a => a.Id, (o, a) => new { o.Code, a.Name }).ToList().Count,
            c => Orders(c).Join(c.Accounts.AsNoTracking(), o => o.AccountId, a => a.Id, (o, a) => new { o.Code, a.Name }).ToList().Count),

        // ----- includes: collection, several references, a second level, paging, tracking -----
        new("includes", "CollectionInclude",
            c => c.Categories.Aggregate().Lookup<Category, Article, Category>(c.Articles, x => x.Id, a => a.CategoryId, x => x.Articles)
                .ToList().Sum(x => x.Articles.Count),
            c => c.Categories.AsNoTracking().Include(x => x.Articles).ToList().Sum(x => x.Articles.Count)),
        new("includes", "CollectionIncludeTracked",
            c => c.Categories.Aggregate().Lookup<Category, Article, Category>(c.Articles, x => x.Id, a => a.CategoryId, x => x.Articles)
                .ToList().Sum(x => x.Articles.Count),
            c => c.Categories.Include(x => x.Articles).ToList().Sum(x => x.Articles.Count)),
        new("includes", "CollectionIncludeFiltered",
            c => c.Categories.Aggregate().Match(x => x.Rank < 50)
                .Lookup<Category, Article, Category>(c.Articles, x => x.Id, a => a.CategoryId, x => x.Articles)
                .ToList().Sum(x => x.Articles.Count),
            c => c.Categories.AsNoTracking().Where(x => x.Rank < 50).Include(x => x.Articles).ToList().Sum(x => x.Articles.Count)),
        new("includes", "ThenInclude",
            c => c.Categories.Aggregate(PipelineDefinition<Category, CategoryWithArticleRefs>.Create(new[]
                {
                    BsonDocument.Parse("{ $lookup: { from: 'Articles', localField: '_id', foreignField: 'CategoryId', as: 'Articles', "
                        + "pipeline: [ { $lookup: { from: 'Authors', localField: 'AuthorId', foreignField: '_id', as: 'Authors' } } ] } }")
                })).ToList().Sum(x => x.Articles.Count(a => a.Authors.Count > 0)),
            c => c.Categories.AsNoTracking().Include(x => x.Articles).ThenInclude(a => a.Author)
                .ToList().Sum(x => x.Articles.Count(a => a.Author != null))),
        new("includes", "MultipleReferenceIncludes",
            c => c.Articles.Aggregate()
                .Lookup<Article, Category, ArticleWithRefs>(c.Categories, a => a.CategoryId, x => x.Id, r => r.Categories)
                .Lookup<ArticleWithRefs, Author, ArticleWithRefs>(c.Authors, r => r.AuthorId, au => au.Id, r => r.Authors)
                .ToList().Count(r => r.Categories.Count > 0 && r.Authors.Count > 0),
            c => c.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author)
                .ToList().Count(a => a.Category != null && a.Author != null)),
        new("includes", "ReferenceIncludeTracked",
            c => c.Articles.Aggregate().Lookup<Article, Author, ArticleWithRefs>(c.Authors, a => a.AuthorId, au => au.Id, r => r.Authors)
                .ToList().Count(r => r.Authors.Count > 0),
            c => c.Articles.Include(a => a.Author).ToList().Count(a => a.Author != null)),
        new("includes", "IncludePaged",
            c => c.Articles.Aggregate().SortBy(a => a.Views).ThenBy(a => a.Id).Skip(1000).Limit(100)
                .Lookup<Article, Author, ArticleWithRefs>(c.Authors, a => a.AuthorId, au => au.Id, r => r.Authors)
                .ToList().Count(r => r.Authors.Count > 0),
            c => c.Articles.AsNoTracking().OrderBy(a => a.Views).ThenBy(a => a.Id).Skip(1000).Take(100).Include(a => a.Author)
                .ToList().Count(a => a.Author != null)),
        new("includes", "CollectionCountProject",
            c => c.Categories.Aggregate(PipelineDefinition<Category, CountRow>.Create(new[]
                {
                    BsonDocument.Parse("{ $lookup: { from: 'Articles', localField: '_id', foreignField: 'CategoryId', as: 'A' } }"),
                    BsonDocument.Parse("{ $project: { Name: 1, N: { $size: '$A' }, _id: 0 } }")
                })).ToList().Sum(r => r.N),
            c => c.Categories.AsNoTracking().Select(x => new { x.Name, N = x.Articles.Count }).ToList().Sum(r => r.N)),

        // ----- tracking: the same selective query untracked and tracked (the driver floor is the same for both) -----
        new("tracking", "SelectiveUntracked", c => c.Flat.AsQueryable().Where(f => f.Count < 1000).ToList().Count, c => Flat(c).Where(f => f.Count < 1000).ToList().Count),
        new("tracking", "SelectiveTracked", c => c.Flat.AsQueryable().Where(f => f.Count < 1000).ToList().Count, c => c.FlatItems.Where(f => f.Count < 1000).ToList().Count),
    };

    public IEnumerable<object> Shapes() => AllShapes;

    private DbContextOptions<BenchmarkDbContext> _efOptionsDriverLinq = null!;
    private DbContextOptions<BenchmarkDbContext> _efOptionsNative = null!;
    private IDbContextFactory<BenchmarkDbContext> _driverLinqFactory = null!;
    private IDbContextFactory<BenchmarkDbContext> _nativeFactory = null!;
    private Collections _collections = null!;
    private string _dbName = null!;

    [GlobalSetup]
    public void Setup()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        _dbName = "ef_bench_shapes_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(conn);

        _efOptionsDriverLinq = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(client, _dbName, o => o.UseQueryMode(MongoQueryMode.DriverLinq))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .EnableThreadSafetyChecks(false)
            .Options;
        _efOptionsNative = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseMongoDB(client, _dbName, o => o.UseQueryMode(MongoQueryMode.Native))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .EnableThreadSafetyChecks(false)
            .Options;

        _driverLinqFactory = BenchmarkContexts.Pool(_efOptionsDriverLinq);
        _nativeFactory = BenchmarkContexts.Pool(_efOptionsNative);

        using (var ctx = new BenchmarkDbContext(_efOptionsDriverLinq))
        {
            BenchmarkSeeder.Seed(ctx, flatCount: N, productCount: 100, reviewCount: N);
            BenchmarkSeeder.SeedExtended(ctx, accountCount: 200, orderCount: N, wideCount: 0, mediumCount: N);
            BenchmarkSeeder.SeedIncludes(ctx, categoryCount: 100, authorCount: 500, articleCount: N);
        }

        var db = client.GetDatabase(_dbName);
        _collections = new Collections
        {
            Flat = db.GetCollection<FlatItem>("FlatItems"),
            Reviews = db.GetCollection<Review>("Reviews"),
            Products = db.GetCollection<Product>("Products"),
            Orders = db.GetCollection<Order>("Orders"),
            Accounts = db.GetCollection<Account>("Accounts"),
            Mediums = db.GetCollection<MediumEntity>("Mediums"),
            Categories = db.GetCollection<Category>("Categories"),
            Articles = db.GetCollection<Article>("Articles"),
            Authors = db.GetCollection<Author>("Authors"),
        };

        Validate();
    }

    // Every shape must agree across the three configs, and none may be trivially empty (an empty result would make a shape measure nothing).
    private void Validate()
    {
        var problems = new List<string>();
        foreach (var shape in AllShapes)
        {
            try
            {
                int driverDl, native;
                using (var ctx = _driverLinqFactory.CreateDbContext()) driverDl = shape.Ef(ctx);
                using (var ctx = _nativeFactory.CreateDbContext()) native = shape.Ef(ctx);
                var driver = shape.Driver?.Invoke(_collections) ?? driverDl;

                if (driver != driverDl || driver != native)
                {
                    problems.Add($"{shape.Name}: driver={driver}, driverLinq={driverDl}, native={native}");
                }
                else if (driver == 0)
                {
                    problems.Add($"{shape.Name}: all three configs returned 0");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{shape.Name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Shape validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        using var ctx = new BenchmarkDbContext(_efOptionsDriverLinq);
        ctx.Database.EnsureDeleted();
    }

    [Benchmark]
    [ArgumentsSource(nameof(Shapes))]
    public int DriverOnly(Shape shape)
        => (shape.Driver ?? throw new InvalidOperationException(shape.Name)).Invoke(_collections);

    [Benchmark]
    [ArgumentsSource(nameof(Shapes))]
    public int EF_DriverLinq(Shape shape)
    {
        using var ctx = _driverLinqFactory.CreateDbContext();
        return shape.Ef(ctx);
    }

    [Benchmark]
    [ArgumentsSource(nameof(Shapes))]
    public int EF_Native(Shape shape)
    {
        using var ctx = _nativeFactory.CreateDbContext();
        return shape.Ef(ctx);
    }
}
