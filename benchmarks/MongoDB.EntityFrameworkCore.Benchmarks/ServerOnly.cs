using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// --server-only: time the ReferenceInclude pipelines without any EF or POCO materialization (raw documents, discarded),
// and print explain("executionStats") totals. Isolates server + wire cost from client-side materialization cost.
internal static class ServerOnly
{
    // --server-only-articles: the MultipleReferenceIncludes pipelines (two reference Includes off Articles) timed on the server alone, as raw
    // documents: the driver's two plain lookups, the lookup + $match + $set per reference the provider emitted before a required reference was
    // filtered on the cursor, and the lookup + $match form that leaves each reference as a one-element array.
    public static int RunArticles()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        var dbName = "ef_bench_serveronly_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(conn);
        var opts = new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(client, dbName).Options;
        try
        {
            using (var ctx = new BenchmarkDbContext(opts)) BenchmarkSeeder.SeedIncludes(ctx, categoryCount: 100, authorCount: 500, articleCount: 10_000);
            var articles = client.GetDatabase(dbName).GetCollection<BsonDocument>("Articles");
            BsonDocument Lookup(string from, string local, string alias) => new("$lookup", new BsonDocument { { "from", from }, { "localField", local }, { "foreignField", "_id" }, { "as", alias } });
            BsonDocument Match(string alias) => new("$match", new BsonDocument(alias + ".0", new BsonDocument("$exists", true)));
            BsonDocument First(string alias) => new("$set", new BsonDocument(alias, new BsonDocument("$arrayElemAt", new BsonArray { "$" + alias, 0 })));
            BsonDocument Unwind(string alias) => new("$unwind", "$" + alias);

            long Raw(BsonDocument[] stages)
            {
                var pipe = PipelineDefinition<BsonDocument, RawBsonDocument>.Create(stages);
                long bytes = 0;
                using var cur = articles.Aggregate(pipe);
                while (cur.MoveNext()) foreach (var d in cur.Current) bytes += d.Slice.Length;
                return bytes;
            }
            void Time(string label, Func<long> f)
            {
                for (var i = 0; i < 5; i++) f();
                var t = new List<double>();
                long b = 0;
                for (var i = 0; i < 30; i++) { var s = Stopwatch.GetTimestamp(); b = f(); t.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds); }
                t.Sort();
                Console.WriteLine($"SERVER {label}: median {t[t.Count / 2]:F2} ms, min {t[0]:F2} ms, bytes {b:N0}");
            }

            Time("driver: lookup, lookup", () => Raw([Lookup("Categories", "CategoryId", "Categories"), Lookup("Authors", "AuthorId", "Authors")]));
            Time("former provider: (lookup, $match, $set) x2", () => Raw([
                Lookup("Authors", "AuthorId", "_lookup_Author"), Match("_lookup_Author"), First("_lookup_Author"),
                Lookup("Categories", "CategoryId", "_lookup_Category"), Match("_lookup_Category"), First("_lookup_Category")]));
            Time("(lookup, $match) x2, arrays kept", () => Raw([
                Lookup("Authors", "AuthorId", "_lookup_Author"), Match("_lookup_Author"),
                Lookup("Categories", "CategoryId", "_lookup_Category"), Match("_lookup_Category")]));
            Time("lookup x2, $match x2 at the end, arrays kept", () => Raw([
                Lookup("Authors", "AuthorId", "_lookup_Author"), Lookup("Categories", "CategoryId", "_lookup_Category"),
                Match("_lookup_Author"), Match("_lookup_Category")]));
            BsonDocument[] L2() => [Lookup("Authors", "AuthorId", "_lookup_Author"), Lookup("Categories", "CategoryId", "_lookup_Category")];
            Time("lookup x2 alone (provider aliases)", () => Raw(L2()));
            Time("lookup x2 + $set {z:1}", () => Raw([.. L2(), new BsonDocument("$set", new BsonDocument("z", 1))]));
            Time("lookup x2 + $match {_id:{$exists}}", () => Raw([.. L2(), new BsonDocument("$match", new BsonDocument("_id", new BsonDocument("$exists", true)))]));
            Time("lookup x2 + one $match as.0", () => Raw([.. L2(), Match("_lookup_Author")]));
            Time("lookup x2 + $match as:{$ne:[]} x2", () => Raw([.. L2(),
                new BsonDocument("$match", new BsonDocument("_lookup_Author", new BsonDocument("$ne", new BsonArray()))),
                new BsonDocument("$match", new BsonDocument("_lookup_Category", new BsonDocument("$ne", new BsonArray())))]));
            Time("lookup x2 + $match $expr size>0 x2", () => Raw([.. L2(),
                new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$_lookup_Author"), 0 }))),
                new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$_lookup_Category"), 0 })))]));
            Time("lookup x2 + $set first x2 (no $match)", () => Raw([.. L2(), First("_lookup_Author"), First("_lookup_Category")]));
            Time("lookup x2 + $set {n: $size as}", () => Raw([.. L2(), new BsonDocument("$set", new BsonDocument("n", new BsonDocument("$size", "$_lookup_Author")))]));
            Time("lookup x1 alone", () => Raw([L2()[0]]));
            Time("lookup x1 + $match as.0", () => Raw([L2()[0], Match("_lookup_Author")]));
            Time("(lookup, $unwind) x2", () => Raw([
                Lookup("Authors", "AuthorId", "_lookup_Author"), Unwind("_lookup_Author"),
                Lookup("Categories", "CategoryId", "_lookup_Category"), Unwind("_lookup_Category")]));
            return 0;
        }
        finally { client.DropDatabase(dbName); }
    }

    public static int Run()
    {
        var conn = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
        var dbName = "ef_bench_serveronly_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(conn);
        var opts = new DbContextOptionsBuilder<BenchmarkDbContext>().UseMongoDB(client, dbName).Options;
        try
        {
            using (var ctx = new BenchmarkDbContext(opts)) BenchmarkSeeder.Seed(ctx, 10_000, 100, 10_000);
            var db = client.GetDatabase(dbName);
            var reviews = db.GetCollection<BsonDocument>("Reviews");
            BsonDocument UnwindP(string a) => new("$unwind", new BsonDocument { { "path", "$" + a }, { "preserveNullAndEmptyArrays", true } });
            BsonDocument Lookup(string alias) => new("$lookup", new BsonDocument { { "from", "Products" }, { "localField", "ProductId" }, { "foreignField", "_id" }, { "as", alias } });
            var unwind = new BsonDocument("$unwind", new BsonDocument { { "path", "$_lookup_Product" }, { "preserveNullAndEmptyArrays", false } });
            var lookupOnly = new[] { Lookup("Products") };
            var lookupUnwind = new[] { Lookup("_lookup_Product"), unwind };

            long Raw(BsonDocument[] stages)
            {
                var pipe = PipelineDefinition<BsonDocument, RawBsonDocument>.Create(stages);
                long bytes = 0;
                using var cur = reviews.Aggregate(pipe);
                while (cur.MoveNext()) foreach (var d in cur.Current) bytes += d.Slice.Length;
                return bytes;
            }
            void Time(string label, Func<long> f)
            {
                for (var i = 0; i < 5; i++) f();
                var t = new List<double>();
                long b = 0;
                for (var i = 0; i < 30; i++) { var s = Stopwatch.GetTimestamp(); b = f(); t.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds); }
                t.Sort();
                Console.WriteLine($"SERVER {label}: median {t[t.Count / 2]:F2} ms, min {t[0]:F2} ms, bytes {b:N0}");
            }
            var al = "_lookup_Product";
            var matchNe = new BsonDocument("$match", new BsonDocument(al, new BsonDocument("$ne", new BsonArray())));
            var matchSize = new BsonDocument("$match", new BsonDocument(al + ".0", new BsonDocument("$exists", true)));
            var setFirst = new BsonDocument("$set", new BsonDocument(al, new BsonDocument("$arrayElemAt", new BsonArray { "$" + al, 0 })));
            var matchGt = new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + al), 0 })));
            Time("lookup only (raw docs)", () => Raw(new[] { Lookup(al) }));
            Time("lookup + $match {as:{$ne:[]}}", () => Raw(new[] { Lookup(al), matchNe }));
            Time("lookup + $match {as.0:{$exists}}", () => Raw(new[] { Lookup(al), matchSize }));
            Time("lookup + $match $expr size>0", () => Raw(new[] { Lookup(al), matchGt }));
            Time("lookup + $match {as.0} + $set first", () => Raw(new[] { Lookup(al), matchSize, setFirst }));
            Time("lookup + $set first (optional form)", () => Raw(new[] { Lookup(al), setFirst }));
            var unwindPreserve = UnwindP(al);
            Time("lookup + $unwind preserve=true", () => Raw(new[] { Lookup(al), unwindPreserve }));
            // A filter on a joined field after the join: $lookup+$unwind+$match is coalesced into the lookup on the server.
            var filter = new BsonDocument("$match", new BsonDocument(al + ".Title", "product-5"));
            Time("FILTER lookup+unwind+$match(joined field)", () => Raw(new[] { Lookup(al), unwind, filter }));
            Time("FILTER lookup+$match+$set first+$match(joined field)", () => Raw(new[] { Lookup(al), matchSize, setFirst, filter }));
            var sortStage = new BsonDocument("$sort", new BsonDocument(al + ".Title", 1));
            var limit = new BsonDocument("$limit", 100);
            Time("SORT lookup+unwind+$sort(joined)+$limit", () => Raw(new[] { Lookup(al), unwind, sortStage, limit }));
            Time("SORT lookup+$match+$set first+$sort(joined)+$limit", () => Raw(new[] { Lookup(al), matchSize, setFirst, sortStage, limit }));
            Time("lookup (as Products) only (raw docs)", () => Raw(lookupOnly));
            Time("lookup+unwind (raw docs)", () => Raw(lookupUnwind));

            var admin = db;
            foreach (var (label, stages) in new[] { ("lookup only", lookupOnly), ("lookup+unwind", lookupUnwind) })
            {
                var cmd = new BsonDocument { { "aggregate", "Reviews" }, { "pipeline", new BsonArray(stages) }, { "cursor", new BsonDocument() } };
                var explain = admin.RunCommand<BsonDocument>(new BsonDocument { { "explain", cmd }, { "verbosity", "executionStats" } });
                Console.WriteLine($"EXPLAIN {label}: " + explain.ToJson().Length + " chars; executionTimeMillis=" +
                    (explain.Contains("stages") ? "see stages" : explain.GetValue("executionStats", BsonNull.Value).ToString()!.Length.ToString()));
                Console.WriteLine(explain.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = false }).Replace("\"", "").Substring(0, Math.Min(2500, explain.ToJson().Length)));
            }
            return 0;
        }
        finally { client.DropDatabase(dbName); }
    }
}
