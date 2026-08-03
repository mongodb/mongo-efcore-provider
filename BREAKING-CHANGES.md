# MongoDB EF Core Provider Breaking Changes

Please note that this provider **does not follow traditional semantic versioning** of limiting breaking changes to major version numbers. This is because the major version number is used to align with releases of Entity Framework Core.

In order to evolve the provider as we introduce new features, we will be using the minor version number for breaking and significant changes to our EF Core provider. Please bear this in mind when upgrading to newer versions of the MongoDB EF Core Provider and ensure you read the release notes and this document for the latest in breaking change information.

## Breaking changes in 8.5.0 / 9.2.0 / 10.1.0

### A missing or `null` embedded array now materializes as an empty collection, not `null`

#### Old behavior

When a stored document had no field at all for an embedded (owned) collection, or had that field explicitly set to BSON `null`, the provider created no collection for the navigation. What you observed then depended on your own class: a navigation declared as a plain `public List<Post> Posts { get; set; }` read back as `null`, while one declared `public List<Post> Posts { get; set; } = []` read back as an empty collection, because the field initializer had already supplied one.

Projecting the collection directly made the `null` unambiguous and usable, since no field initializer was involved: `Select(b => b.Posts)` returned `null` for those documents, so `posts is null` was a reliable test for "the stored array was absent or `null`", as distinct from "the stored array was present but empty".

#### New behavior

A missing or explicitly-`null` embedded array now materializes as an **empty** collection on every read path — whole-entity queries, `Include`, and projections alike — regardless of how the navigation property is declared. The collection is created through the navigation's own collection accessor, so a `HashSet<T>` or custom collection navigation gets its declared type.

The distinction between "the stored array was absent or `null`" and "the stored array was present but empty" is therefore no longer observable through a materialized collection navigation.

Nullable primitive collection *properties* (for example `List<string>? Tags`) are unaffected and still read back as `null`, because they map through a property serializer with its own nullability semantics rather than as a collection navigation.

**This can change stored documents, through a read-modify-write cycle.** The write path itself is unchanged: assigning `null` to a collection navigation and saving still persists `null`. But if you load an entity, change anything about it, and call `SaveChanges`, what gets written back for the collection navigation is now the empty collection that materialization produced, where it used to be the `null` that materialization produced. Measured, for a tracked load of one document, an edit to an unrelated scalar property, and `SaveChanges` — identical in both query modes:

| Stored `Posts` before the cycle | Written back, previous versions | Written back, this version |
|---|---|---|
| field absent | `"Posts": null` | `"Posts": []` |
| `"Posts": null` | `"Posts": null` | `"Posts": []` |
| `"Posts": []` | `"Posts": []` | `"Posts": []` |

So loading, mutating and saving entities whose embedded arrays are ragged will normalize those arrays **in the database**. Note this is not new *document-rewriting* behavior — the previous versions already replaced an absent field with `"Posts": null` on the same cycle; what changed is the value written. It follows from the read change described above, and nothing about how a collection is serialized changed.

#### Why

Empty-not-null is Entity Framework Core's contract for a collection navigation, independent of the CLR type's nullability or its field initializer. The previous behavior made the materialized result depend on the application's field initializer, which meant two models over the same documents disagreed, and different read paths over the same document could disagree with each other. It also made a projected count of such a collection throw `ArgumentNullException` instead of returning `0`.

#### Mitigations

If you need to tell an absent or `null` stored array apart from a present-but-empty one, ask the database rather than the materialized collection. A LINQ predicate cannot express it (`!b.Posts.Any()` is true for all three states), so query the documents through the driver, where the stored shape is visible:

```c#
var collection = client.GetDatabase("mydb").GetCollection<BsonDocument>("Blogs");
var absentOrNull = collection.Find(
        Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Exists("Posts", false),
            Builders<BsonDocument>.Filter.Eq("Posts", BsonNull.Value)))
    .ToList();
```

Use the same `IMongoClient` your `DbContext` is configured with, or a new `MongoClient` against the same connection string. If you were relying on `Select(b => b.Posts)` returning `null`, replace that test with the above.

Do that check **before** the documents pass through a read-modify-write cycle, because that cycle normalizes them (see *New behavior*). If you need the ragged stored shape preserved, avoid round-tripping those entities through the change tracker: update only the fields you actually want to change, either with `ExecuteUpdate` (whose setter API differs between EF Core versions — see the EF Core docs for the form your version takes) or with a driver update, which leaves the array field untouched:

```c#
collection.UpdateOne(
    Builders<BsonDocument>.Filter.Eq("_id", id),
    Builders<BsonDocument>.Update.Set("Title", "New title"));
```

Otherwise, treat the normalization as a one-time migration of the documents you save, and take whatever record of the distinction you need — a backup, or an audit query using the `Find` above — before saving.

### A join whose inner sequence applies `Skip`/`Take` to itself now throws instead of returning wrong results

#### Old behavior

`Skip`/`Take` applied to the **inner** sequence of a `Join`, `GroupJoin` or (EF Core 10) `LeftJoin` — for example `orders.Join(customers.OrderBy(c => c.City).Skip(10).Take(50), o => o.CustomerId, c => c.Id, (o, c) => new { … })` — behaved differently depending on which version of the MongoDB C# driver was resolved. With driver 3.9 (the version the previous release pinned) the query threw a driver `ExpressionNotSupportedException`. With driver 3.10 — which the previous release's `MongoDB.Driver (>= 3.9.0)` dependency permits, and which this release pins — the query **ran and returned silently wrong rows**: the driver's LINQ provider folds the uncorrelated inner's `$sort`/`$skip`/`$limit` into the correlated `$lookup` sub-pipeline, where they apply per outer document over the key-matched subset (at most one document for a unique inner key) instead of once over the whole inner sequence. Measured against the Northwind data set, one such query returned 0 rows where 453 is correct, and another returned 830 rows where 181 is correct.

#### New behavior

The provider recognizes the shape at translation time and throws, rather than executing a query it knows the driver mistranslates. Explicit `UseQueryMode(MongoQueryMode.DriverLinq)` still executes it, unchanged — that mode is the documented opt-in to driver-LINQ execution and carries this caveat.

Paging elsewhere is unaffected and still works: `Skip`/`Take` on the **outer** sequence, `Skip`/`Take` applied **after** the join, and a filtered `Include` such as `Include(c => c.Orders.OrderBy(o => o.Date).Take(5))` are all correct and are not declined.

**The decline cannot tell a genuinely-affected query apart from a merely-suspicious one.** The check is shape-based — "does the inner sequence apply `Skip`/`Take` to itself?" — not cardinality-aware, so it cannot tell whether the fold the driver performs would actually change the result. `Join(inner.Take(n))` where `n` is greater than or equal to the inner sequence's own row count is a case where the driver's fold is a genuine no-op: on driver 3.10 that query returns **correct** results today. It is an example, not the whole class — any paging that cannot remove a row is in the same position, including `Skip(0)` and `Take(int.MaxValue)`, and all of it is declined alike. (On driver 3.9 it already threw — measured: a bare `Take`, with no `OrderBy`/`Skip`, on a join's inner still throws driver `ExpressionNotSupportedException` there, the same as every other paged-inner shape — so a user pinned to 3.9 sees no change beyond the exception type.) On driver 3.10, though, that query will now throw where it previously succeeded. If you have a query of this shape running against driver 3.10 today, it will start throwing after this upgrade even though nothing about its results was ever wrong — that is the most likely way an existing, correctly-behaving query is affected by this change.

#### Why

The underlying defect is in the MongoDB C# driver (CSHARP-6017), not in the provider. Until it is fixed, the only two options for this shape are a clean failure or silently wrong data. A clean failure is the safe one. The guard is temporary and will be removed when the driver stops folding.

#### Mitigations

Materialize the paged inner sequence first. A `Join` between a `DbSet` and a local, already-materialized sequence is not itself translatable, so pull the matching outer rows down with a `Contains` filter on the paged inner's keys, then join the two materialized sequences in memory:

```c#
var page = db.Customers.OrderBy(c => c.City).Skip(10).Take(50).ToList();
var keys = page.Select(c => c.Id).ToList();
var orders = db.Orders.Where(o => keys.Contains(o.CustomerId)).ToList();
var result = orders.Join(page, o => o.CustomerId, c => c.Id, (o, c) => new { o, c }).ToList();
```

Or, if you need the previous behavior including its incorrect results, opt in explicitly:

```c#
options.UseMongoDB(connectionString, databaseName, b => b.UseQueryMode(MongoQueryMode.DriverLinq));
```

### Entity types with their own `DbSet` are no longer embedded when reached by a navigation

#### Old behavior

When an entity type was reached by a reference or collection navigation, it was always configured as an *owned* (embedded) type — stored as a sub-document inside the principal document — even when that same type had its own `DbSet<T>` on the context. A navigation to such a type therefore produced a nested/embedded document rather than a relationship between two collections.

#### New behavior

When the navigated type is already registered in the model as an independent (non-owned) entity — typically because it has its own `DbSet<T>` — it is no longer auto-configured as owned. It is instead treated as a separate entity stored in its own collection, with a foreign key introduced on the dependent. This is what enables cross-collection `Include` / navigation support.

Types that do **not** have their own `DbSet`, and types you explicitly configure with `OwnsOne` / `OwnsMany`, continue to be embedded exactly as before.

The public helper `MongoRelationshipDiscoveryConvention.ShouldBeOwnedType(Type, IConventionModel)` reflects this change: it now returns `false` for a type that is already present in the model as a non-owned (root) entity.

#### Why

To support relationships that span MongoDB collections (the cross-collection `Include` feature). A type that has its own `DbSet` is, by the application's own configuration, a root collection; also embedding it is ambiguous and prevents querying it as a separate collection.

#### Mitigations

If you relied on the previous embed-by-default behavior for a type that *also* has a `DbSet`, configure the relationship explicitly as owned so it continues to be embedded:

```c#
modelBuilder.Entity<Order>().OwnsOne(o => o.ShippingAddress);
```

(or `OwnsMany` for collection navigations). If you want the new separate-collection behavior, no change is required. Models whose embedded types never had their own `DbSet` are unaffected, and the stored documents for those types are unchanged.

## Breaking changes in 8.4.0 / 9.1.0 / 10.0.0

### The element name for discriminators may have changed

#### Old behavior

Discriminator properties not explicitly configured with an element name may not be mapped to `_t`. For example, `modelBuilder.Entity<Foo>().HasDiscriminator<string>("_t")` when used with `CamelCaseElementNameConvention` would result in an a discriminator field called `T` in BSON documents. This means that you may get exceptions because EF Core assumes the discriminator is mapped to `_t`.

#### New behavior

Discriminator properties are always mapped to `_t` unless an explicit element name is provided.

#### Why

By convention, discriminators in MongoDB document should be called `_t` to ensure interoperability between different tools, etc. This is similar to how the document ID is always in `_id`.

#### Mitigations

Consider updating discriminators in all documents to `_t` to conform with convention. If this is not possible, then the element name for a discriminator property can be explicitly configured. For example:

```c#
modelBuilder.Entity<Foo>().Property<string>("_t").HasElementName("T");
```

Note that, by default, discriminator properties in shadow state are called "Discriminator", so you may need to do this:

```c#
modelBuilder.Entity<Foo>().Property<string>("Discriminator").HasElementName("discriminator");
```

## Breaking changes in 8.3.0 / 9.0.0

Nullable properties configured with an alternative BSON representation either by the `[BsonRepresentation]` attribute or `HasBsonRepresentation()` fluent API were not being applied in previous versions. This has been fixed but you will remedy the discrepancy one of two ways:

### Continue to use the default representation

If it is not critical you use the alternative BSON representation you can do this by simply removing the configuration from your application before it starts.

or

### Update affected elements to new representation

As part of your upgrade process you can use the [updateMany](https://www.mongodb.com/docs/manual/reference/method/db.collection.updateMany/) method per affected MongoDB collection to rewrite any affected nullable properties/elements into the desired BSON representation using the `$convert`  operation.

An example of converting a `dateOfBirth` element in a collection named `people` from a BSON `date` into a BSON `int` representation would look like this:

```js
db.people.updateMany(
   { dateOfBirth: { $type: "date" } },
   [
      {
         $set: {
            dateOfBirth: {
               $convert: {
                  input: "$dateOfBirth",
                  to: "int",
                  onNull: null
               }
            }
         }
      }
   ]
)
```

## Breaking changes in 8.2.0

No explicit breaking changes are intended in this EF Core Provider release but the underlying [MongoDB.Driver has many breaking changes in the 3.0 release](https://www.mongodb.com/docs/drivers/csharp/v3.0/upgrade/v3/#version-3.0-breaking-changes). If you are using the MongoDB C# Driver explicitly you will likely be affected there and even if not you should ensure compatibilty with your application and data.

## Breaking changes in 8.1.0

This release sees a number of breaking changes deemed necessary to implement the new features and provide for a robust provider experience. They are:

- MongoDB transactions are now required by default
- Guid binary format is changing
- CreateDatabase recommended at start-up
- IMongoClientWrapper interface changes
- Convention tweaks

Please see the following sections for more details.

### MongoDB transactions are now required by default

The MongoDB EF Core Provider 8.1.0 introduces optimistic concurrency support and automatic transactions inside `SaveChanges` and `SaveChangesAsync` to ensure all changes commit together or rollback together as part of the "unit of work" philosophy of Entity Framework Core.

To ensure data integrity transactions are enabled by default in 8.1.0 which means a MongoDB server configuration that supports transactions is required.

If you are already running MongoDB 5.0 or above in load balanced, sharded, or replica set configurations you should be unaffected by this change.

If, however, you are running MongoDB Server:

- With a version prior to 5.0 you will need to upgrade, this provider is not supported with MongoDB versions prior to 5.0
- In standalone mode (perhaps for local development) you can reconfigure your standalone server to a single-instance replica set
- In a container environment switch to a single-instance replica set container if one is available

To reconfigure a standalone server please follow the [Convert a Standalone MongoDB to a Replica Set guide](https://www.mongodb.com/docs/manual/tutorial/convert-standalone-to-replica-set/).

Alternatively, if you are absolutely sure you do not wish to use transactions, (and therefore not use optimistic concurrency) then you can disable automatic transactions inside `SaveChanges` and `SaveChangesAsync` by setting `Database.AutoTransactionBehavior = AutoTransactionBehavior.Never` on your `DbContext` subclass as part of the setup.

### Guid binary format is changing

The default version for Guid storage in the MongoDB .NET/C# Driver and in prior versions of EF Core Provider is the `CSharpLegacy` binary format which has a number of issues when being read by different drivers. To alleviate this problem we are switching to the `Standard` format which does not suffer from these problems.

If your database exists specifically for your EF Core provider application and has data already in use we recommend you write a script to convert the Guids from the `CSharpLegacy` to `Standard` format. If your database is already using Guids and is shared with other non-EF Core Provider applications we recommend you switch them all to the `Standard` Guid format and not rely on any other kind of binary serialization format to avoid such incompatibilities.

Please also note that because the EF Core Provider relies on the MongoDB .NET/C# Driver to perform the low-level operations that the `BsonDefaults.GuidRepresentationMode` will be set to `GuidRepresentationMode.V3` when using the new 8.1.0 or later versions of this provider. Using the MongoDB C# Driver in your application at the same time must also use this mode.

### CreateDatabase recommended at start-up

Previous versions of this provider did not actually create either the database or collections inside `CreateDatabase` or `CreateDatabaseAsync` and instead left them to be implicitly created as documents were written.

With the move to transactional `SaveChanges` this is no longer recommended as it may cause snapshot issues in some server configurations.

Instead it is recommended you call `EnsureCreated` or `EnsureCreatedAsync` during your application start-up. This will call `CreateDatabase` (even if it already exists) which will create any missing expected collections based on the configured metadata for your `DbContext` subclass. This will not affect any existing collections or data and is recommended to avoid a `SaveChanges` or `SaveChangesAsync` operation from causing schema changes during a transaction which can result in snapshot exceptions on some configurations.

### IMongoClientWrapper interface changes

Transactions and database creation work has meant that the `IMongoClientWrapper` interface has changed. It is not recommended you implement this interface yourself as it exists solely to provide EF service registration for the concrete implementation `MongoClientWrapper` class.

### Convention tweaks

- The previously undocumented `CamelCaseElementNameConvention` was incorrectly using the class name and not the property name
- Some other conventions were not sealed and had virtual methods that made no sense, these have been corrected
- Some conventions took unnecessary constructor arguments for unneeded dependencies, these have been removed


