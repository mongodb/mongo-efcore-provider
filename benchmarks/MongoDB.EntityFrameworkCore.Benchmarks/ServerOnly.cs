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
            var setFirst = new BsonDocument("$set", new BsonDocument(al, new BsonDocument("$first", "$" + al)));
            var matchGt = new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + al), 0 })));
            Time("lookup only (raw docs)", () => Raw(new[] { Lookup(al) }));
            Time("lookup + $match {as:{$ne:[]}}", () => Raw(new[] { Lookup(al), matchNe }));
            Time("lookup + $match {as.0:{$exists}}", () => Raw(new[] { Lookup(al), matchSize }));
            Time("lookup + $match $expr size>0", () => Raw(new[] { Lookup(al), matchGt }));
            Time("lookup + $match {as.0} + $set first", () => Raw(new[] { Lookup(al), matchSize, setFirst }));
            Time("lookup + $set first (optional form)", () => Raw(new[] { Lookup(al), setFirst }));
            var unwindPreserve = UnwindP(al);
            Time("lookup + $unwind preserve=true", () => Raw(new[] { Lookup(al), unwindPreserve }));
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
