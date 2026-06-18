# Geospatial query support (driver GeoJSON types) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users persist MongoDB driver GeoJSON types on entities, create `2dsphere` indexes over them, and filter with three `EF.Functions` geospatial predicates that translate to MQL geo `$match` stages.

**Architecture:** Mirrors the existing VectorSearch feature. A new annotation + fluent builder configure a `2dsphere` index (flowing through the existing non-vector `CreateIndexDocument()` path). `MongoTypeMappingSource` / `BsonSerializerFactory` accept the driver's GeoJSON types (serialization is free — the driver ships the serializers). Three `EF.Functions` marker methods are intercepted in `MongoEFToLinqTranslatingExpressionVisitor` (at the same point as the VectorSearch case) and rewritten into a raw `$match` `BsonDocument` appended via `MongoQueryable.AppendStage` — the exact mechanism `ProcessVectorSearch` uses for its `$addFields` stage.

**Tech Stack:** C# / .NET, EF Core (EF8/EF9/EF10 multi-target), MongoDB C# driver v3.9.0, xUnit + FluentAssertions, TestContainers (local MongoDB; geo + 2dsphere work on non-Atlas MongoDB).

**Design doc:** `docs/agentplans/2026-06-18/geospatial-queries/geospatial-queries.design.md`

---

## File structure

**New files:**
- `src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs` — the three `EF.Functions` predicate markers + an `IsGeoPredicate`/method-info helper used by the visitor.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoGeoTranslator.cs` — type-agnostic core: builds the geo `$match` `BsonDocument` from (element path, geometry value, operator kind). The NTS seam.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/MongoGeoTranslatorTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Extensions/MongoDbFunctionsExtensionsTests.cs`
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs`
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Storage/GeospatialIndexTests.cs`

**Modified files:**
- `src/MongoDB.EntityFrameworkCore/Metadata/MongoAnnotationNames.cs` — add `GeospatialIndexType`.
- `src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexExtensions.cs` — add `Get/SetGeospatialIndexType`.
- `src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexBuilderExtensions.cs` — add `Is2dSphereIndex()`.
- `src/MongoDB.EntityFrameworkCore/Metadata/InternalIndexExtensions.cs` — `CreateIndexDocument()` + `MakeIndexName()` emit `2dsphere`.
- `src/MongoDB.EntityFrameworkCore/Storage/MongoTypeMappingSource.cs` — recognize `GeoJsonGeometry<T>`.
- `src/MongoDB.EntityFrameworkCore/Serializers/BsonSerializerFactory.cs` — serializer for `GeoJsonGeometry<T>`.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` — intercept geo `Where`.
- `README.md` — move geospatial out of "Planned" into a documented (partial) feature.
- `docs/failing-spec-tests.md` — note NTS spatial conformance remains deferred.

**Conventions to honor (from AGENTS.md):** preserve file BOMs; `<Nullable>enable</Nullable>` in `src/`; guard EF-version differences with `#if EF8 || EF9` / `#if !EF8`; tests run serially; commit messages start with the JIRA prefix `EF-321:`.

---

## Task 1: Geospatial index annotation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Metadata/MongoAnnotationNames.cs:107` (append after `VectorIndexOptions`)
- Modify: `src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexExtensions.cs:104` (append before closing brace)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Microsoft.EntityFrameworkCore;
using MongoDB.Driver.GeoJsonObjectModel;
using MongoDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata;

public class GeospatialIndexTests
{
    class Place
    {
        public int Id { get; set; }
        public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = null!;
    }

    [Fact]
    public void Get_geospatial_index_type_is_null_by_default()
    {
        using var context = new TestContext(mb => mb.Entity<Place>().HasIndex(e => e.Location));
        var index = context.Model.FindEntityType(typeof(Place))!.GetIndexes().Single();

        Assert.Null(index.GetGeospatialIndexType());
    }

    [Fact]
    public void Set_geospatial_index_type_round_trips()
    {
        using var context = new TestContext(mb =>
        {
            var index = mb.Entity<Place>().HasIndex(e => e.Location).Metadata;
            index.SetGeospatialIndexType("2dsphere");
        });
        var index = context.Model.FindEntityType(typeof(Place))!.GetIndexes().Single();

        Assert.Equal("2dsphere", index.GetGeospatialIndexType());
    }

    class TestContext(Action<ModelBuilder> onModelCreating) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseMongoDB("mongodb://localhost:27017", "GeospatialIndexTests");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => onModelCreating(modelBuilder);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests"`
Expected: FAIL — compile error, `GetGeospatialIndexType` / `SetGeospatialIndexType` not defined.

- [ ] **Step 3: Add the annotation constant**

In `MongoAnnotationNames.cs`, after the `VectorIndexOptions` constant (line 107), before the closing brace:

```csharp

    /// <summary>
    /// Annotation for the geospatial index type (e.g. "2dsphere") of an index.
    /// </summary>
    public const string GeospatialIndexType = Prefix + nameof(GeospatialIndexType);
```

- [ ] **Step 4: Add the getter/setter**

In `MongoIndexExtensions.cs`, before the final closing brace (line 104), mirroring the `VectorIndexOptions` accessors:

```csharp

    /// <summary>
    /// Returns the geospatial index type (e.g. "2dsphere") for the index, or <see langword="null" /> if it is not a geospatial index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns>The geospatial index type, or <see langword="null" /> if none is set.</returns>
    public static string? GetGeospatialIndexType(this IReadOnlyIndex index)
        => (string?)index[MongoAnnotationNames.GeospatialIndexType];

    /// <summary>
    /// Sets the geospatial index type (e.g. "2dsphere") for the index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="geospatialIndexType">The geospatial index type, or <see langword="null" /> to clear it.</param>
    public static void SetGeospatialIndexType(this IMutableIndex index, string? geospatialIndexType)
        => index.SetAnnotation(MongoAnnotationNames.GeospatialIndexType, geospatialIndexType);

    /// <summary>
    /// Sets the geospatial index type (e.g. "2dsphere") for the index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="geospatialIndexType">The geospatial index type, or <see langword="null" /> to clear it.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static string? SetGeospatialIndexType(
        this IConventionIndex index,
        string? geospatialIndexType,
        bool fromDataAnnotation = false)
        => (string?)index.SetAnnotation(MongoAnnotationNames.GeospatialIndexType, geospatialIndexType, fromDataAnnotation)?.Value;
```

The `using Microsoft.EntityFrameworkCore.Metadata;` already present covers `IReadOnlyIndex` / `IMutableIndex` / `IConventionIndex`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Metadata/MongoAnnotationNames.cs \
        src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexExtensions.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs
git commit -m "EF-321: Add Mongo:GeospatialIndexType annotation and accessors"
```

---

## Task 2: `Is2dSphereIndex()` fluent builder API

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexBuilderExtensions.cs:188` (append before final brace)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `GeospatialIndexTests.cs`:

```csharp
    [Fact]
    public void Is2dSphereIndex_sets_annotation()
    {
        using var context = new TestContext(mb =>
            mb.Entity<Place>().HasIndex(e => e.Location).Is2dSphereIndex());
        var index = context.Model.FindEntityType(typeof(Place))!.GetIndexes().Single();

        Assert.Equal("2dsphere", index.GetGeospatialIndexType());
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests.Is2dSphereIndex_sets_annotation"`
Expected: FAIL — `Is2dSphereIndex` not defined.

- [ ] **Step 3: Add the builder methods**

In `MongoIndexBuilderExtensions.cs`, before the final closing brace (line 188), mirroring `HasCreateIndexOptions`:

```csharp

    /// <summary>
    /// Configures the index as a MongoDB <c>2dsphere</c> geospatial index over a GeoJSON property.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder Is2dSphereIndex(this IndexBuilder indexBuilder)
    {
        indexBuilder.Metadata.SetGeospatialIndexType("2dsphere");
        return indexBuilder;
    }

    /// <summary>
    /// Configures the index as a MongoDB <c>2dsphere</c> geospatial index over a GeoJSON property.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being configured.</typeparam>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder<TEntity> Is2dSphereIndex<TEntity>(this IndexBuilder<TEntity> indexBuilder)
        => (IndexBuilder<TEntity>)Is2dSphereIndex((IndexBuilder)indexBuilder);
```

`using MongoDB.EntityFrameworkCore.Extensions;` is the same namespace, so `SetGeospatialIndexType` resolves directly.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexBuilderExtensions.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs
git commit -m "EF-321: Add Is2dSphereIndex fluent builder extension"
```

---

## Task 3: `2dsphere` index document generation

`2dsphere` indexes are *not* vector indexes, so `MongoDatabaseCreator` already routes them through `CreateIndexDocument()` (it filters `!GetVectorIndexOptions().HasValue`). We change the key value from `1`/`-1` to the string `"2dsphere"` and give the index a server-style name.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Metadata/InternalIndexExtensions.cs:176-193` (`CreateIndexDocument`) and `:28-48` (`MakeIndexName`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `GeospatialIndexTests.cs` (add `using MongoDB.Bson;`, `using MongoDB.EntityFrameworkCore.Metadata;` at the top):

```csharp
    [Fact]
    public void CreateIndexDocument_emits_2dsphere_key()
    {
        using var context = new TestContext(mb =>
            mb.Entity<Place>().HasIndex(e => e.Location).Is2dSphereIndex());
        var index = context.Model.FindEntityType(typeof(Place))!.GetIndexes().Single();

        var model = index.CreateIndexDocument();
        var keysDoc = model.Keys.Render(
            new MongoDB.Driver.RenderArgs<BsonDocument>(
                BsonDocumentSerializer.Instance,
                BsonSerializer.SerializerRegistry));

        Assert.Equal("2dsphere", keysDoc["Location"].AsString);
    }
```

> Note: `index.CreateIndexDocument()` is `internal`; the UnitTests project already has `InternalsVisibleTo`. Add `using MongoDB.Bson.Serialization;` and `using MongoDB.Bson.Serialization.Serializers;` for `BsonSerializer` / `BsonDocumentSerializer`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests.CreateIndexDocument_emits_2dsphere_key"`
Expected: FAIL — current code writes `1` (Int32), so `["Location"].AsString` throws / assertion fails.

- [ ] **Step 3: Update `CreateIndexDocument`**

In `InternalIndexExtensions.cs`, replace the body of `CreateIndexDocument` (lines 176-193) with:

```csharp
    public static CreateIndexModel<BsonDocument> CreateIndexDocument(this IIndex index)
    {
        var path = index.DeclaringEntityType.GetDocumentPath();
        var geospatialIndexType = index.GetGeospatialIndexType();

        var doc = new BsonDocument();
        var propertyIndex = 0;

        foreach (var property in index.Properties)
        {
            var key = string.Join('.', path.Append(property.GetElementName()));
            doc.Add(key, geospatialIndexType is not null
                ? (BsonValue)geospatialIndexType
                : index.GetDescending(propertyIndex++) ? -1 : 1);
        }

        var options = index.GetCreateIndexOptions() ?? new CreateIndexOptions<BsonDocument>();
        options.Name ??= index.Name!;

        // A 2dsphere index is never a uniqueness constraint; only carry Unique for ordinary indexes.
        if (geospatialIndexType is null)
        {
            options.Unique ??= index.IsUnique;
        }

        return new CreateIndexModel<BsonDocument>(doc, options);
    }
```

- [ ] **Step 4: Update `MakeIndexName` for geospatial indexes**

In `InternalIndexExtensions.cs`, in `MakeIndexName(this IReadOnlyIndex index)` (lines 28-48), after the existing vector-index early return (line 31-34), add:

```csharp
        // 2dsphere indexes follow the server convention "<field>_2dsphere".
        var geospatialIndexType = index.GetGeospatialIndexType();
        if (geospatialIndexType is not null)
        {
            var geoParts = index.DeclaringEntityType.GetDocumentPath()
                .Concat(index.Properties.Select(p => p.GetElementName()))
                .Append(geospatialIndexType);
            return string.Join('_', geoParts);
        }
```

(`System.Linq` is already imported.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Metadata/InternalIndexExtensions.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/GeospatialIndexTests.cs
git commit -m "EF-321: Emit 2dsphere key and name in CreateIndexDocument"
```

---

## Task 4: GeoJSON type mapping and serialization

The driver ships GeoJSON serializers, so we only need EF to (a) accept `GeoJsonGeometry<T>` properties as mapped scalars and (b) hand back the driver serializer.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Storage/MongoTypeMappingSource.cs:60-76` (`FindPrimitiveMapping`)
- Modify: `src/MongoDB.EntityFrameworkCore/Serializers/BsonSerializerFactory.cs:62-105` (`CreateTypeSerializer`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs` (round-trip)

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver.GeoJsonObjectModel;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class GeospatialQueryTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Place
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = null!;
    }

    private static GeoJsonPoint<GeoJson2DGeographicCoordinates> Point(double lng, double lat)
        => new(new GeoJson2DGeographicCoordinates(lng, lat));

    [Fact]
    public void Can_round_trip_GeoJsonPoint()
    {
        var collection = database.CreateCollection<Place>();
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new Place { Id = 1, Name = "Origin", Location = Point(-0.12, 51.5) });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var place = db.Entities.Single();
            place.Location.Coordinates.Longitude.Should().BeApproximately(-0.12, 1e-9);
            place.Location.Coordinates.Latitude.Should().BeApproximately(51.5, 1e-9);
        }
    }

    [Fact]
    public void Can_update_GeoJsonPoint()
    {
        var collection = database.CreateCollection<Place>();
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new Place { Id = 1, Name = "Origin", Location = Point(-0.12, 51.5) });
            db.SaveChanges();
        }

        // Assign a new geometry instance (GeoJSON values are immutable) and persist the change.
        using (var db = SingleEntityDbContext.Create(collection))
        {
            var place = db.Entities.Single();
            place.Location = Point(2.35, 48.85);
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var place = db.Entities.Single();
            place.Location.Coordinates.Longitude.Should().BeApproximately(2.35, 1e-9);
            place.Location.Coordinates.Latitude.Should().BeApproximately(48.85, 1e-9);
        }
    }
}
```

> `SingleEntityDbContext`, `TemporaryDatabaseFixture`, and `database.CreateCollection<T>()` are existing helpers (see `Storage/IndexTests.cs` and other `Query/` tests for usage). `SingleEntityDbContext.Create(collection)` exposes the set as `db.Entities`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialQueryTests.Can_round_trip_GeoJsonPoint|FullyQualifiedName~GeospatialQueryTests.Can_update_GeoJsonPoint"`
Expected: FAIL (both) — EF rejects the `GeoJsonPoint<>` property (no type mapping) at model build, or no serializer is found.

- [ ] **Step 3: Recognize GeoJSON types in the type-mapping source**

In `MongoTypeMappingSource.cs`, add a static helper and extend `FindPrimitiveMapping`. Add near the top of the class (after the `SupportedDictionaryInterfaces` array, ~line 45):

```csharp
    private static bool IsGeoJsonType(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType!)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GeoJsonGeometry<>))
            {
                return true;
            }
        }

        return false;
    }
```

Add `using MongoDB.Driver.GeoJsonObjectModel;` to the using block. Then in `FindPrimitiveMapping` (line 64), add `IsGeoJsonType(clrType)` to the condition:

```csharp
        if (clrType is { IsValueType: true }
            || clrType == typeof(string)
            || clrType == typeof(BinaryVectorFloat32)
            || clrType == typeof(BinaryVectorInt8)
            || clrType == typeof(BinaryVectorPackedBit)
            || IsGeoJsonType(clrType)
            || clrType.TryGetItemType(typeof(ReadOnlyMemory<>)) != null
            || clrType.TryGetItemType(typeof(Memory<>)) != null)
        {
            return new MongoTypeMapping(clrType);
        }
```

- [ ] **Step 4: Return the driver serializer for GeoJSON types**

In `BsonSerializerFactory.cs`, add a `using MongoDB.Driver.GeoJsonObjectModel;` and add a switch arm in `CreateTypeSerializer(Type type, ...)` immediately before the `_ when type.IsEnum` arm (line 91):

```csharp
            _ when IsGeoJsonType(type) => BsonSerializer.LookupSerializer(type),
```

Add the same `IsGeoJsonType` helper as a private static method on `BsonSerializerFactory` (it is needed independently of the type-mapping source):

```csharp
    private static bool IsGeoJsonType(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType!)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GeoJsonGeometry<>))
            {
                return true;
            }
        }

        return false;
    }
```

> `BsonSerializer.LookupSerializer` is read-only (it does not mutate global driver state) — consistent with the factory's existing `BsonClassMap.LookupClassMap` usage and the Serializers AGENTS.md rule against `RegisterSerializer`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialQueryTests.Can_round_trip_GeoJsonPoint|FullyQualifiedName~GeospatialQueryTests.Can_update_GeoJsonPoint"`
Expected: PASS (both — insert round-trip and update-existing).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Storage/MongoTypeMappingSource.cs \
        src/MongoDB.EntityFrameworkCore/Serializers/BsonSerializerFactory.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs
git commit -m "EF-321: Map and serialize driver GeoJSON types"
```

---

## Task 5: `EF.Functions` geospatial predicate markers

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Extensions/MongoDbFunctionsExtensionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Extensions/MongoDbFunctionsExtensionsTests.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver.GeoJsonObjectModel;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public class MongoDbFunctionsExtensionsTests
{
    private static GeoJsonPoint<GeoJson2DGeographicCoordinates> Point(double lng, double lat)
        => new(new GeoJson2DGeographicCoordinates(lng, lat));

    [Fact]
    public void GeoWithin_throws_when_evaluated_directly()
        => Assert.Throws<InvalidOperationException>(
            () => EF.Functions.GeoWithin(Point(0, 0), Point(0, 0)));

    [Fact]
    public void GeoIntersects_throws_when_evaluated_directly()
        => Assert.Throws<InvalidOperationException>(
            () => EF.Functions.GeoIntersects(Point(0, 0), Point(0, 0)));

    [Fact]
    public void GeoWithinCircle_throws_when_evaluated_directly()
        => Assert.Throws<InvalidOperationException>(
            () => EF.Functions.GeoWithinCircle(Point(0, 0), Point(0, 0), 100));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoDbFunctionsExtensionsTests"`
Expected: FAIL — `GeoWithin` etc. not defined on `DbFunctions`.

- [ ] **Step 3: Create the extension class**

Create `src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using MongoDB.Driver.GeoJsonObjectModel;

// ReSharper disable once CheckNamespace (extensions should be in the EF Core namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific geospatial extension methods on <see cref="DbFunctions"/>. These methods can only be used inside a
/// LINQ query translated by the MongoDB EF Core Provider; calling them directly throws.
/// </summary>
public static class MongoDbFunctionsExtensions
{
    private const string ClientEvalMessage =
        "The MongoDB geospatial function '{0}' is for use in LINQ queries translated by the MongoDB EF Core Provider only "
        + "and cannot be evaluated client-side.";

    /// <summary>
    /// Matches documents whose <paramref name="field"/> geometry is entirely within <paramref name="area"/>
    /// (MongoDB <c>$geoWithin</c>). Use only inside a single <c>Where</c> predicate.
    /// </summary>
    /// <typeparam name="TCoordinates">The GeoJSON coordinate system.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="field">The mapped GeoJSON property to test.</param>
    /// <param name="area">The GeoJSON geometry (e.g. polygon) to test containment within.</param>
    /// <returns>This method always throws; it is a translation marker.</returns>
    public static bool GeoWithin<TCoordinates>(
        this DbFunctions _,
        GeoJsonGeometry<TCoordinates> field,
        GeoJsonGeometry<TCoordinates> area)
        where TCoordinates : GeoJsonCoordinates
        => throw new InvalidOperationException(string.Format(ClientEvalMessage, nameof(GeoWithin)));

    /// <summary>
    /// Matches documents whose <paramref name="field"/> geometry intersects <paramref name="geometry"/>
    /// (MongoDB <c>$geoIntersects</c>). Use only inside a single <c>Where</c> predicate.
    /// </summary>
    /// <typeparam name="TCoordinates">The GeoJSON coordinate system.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="field">The mapped GeoJSON property to test.</param>
    /// <param name="geometry">The GeoJSON geometry to test intersection with.</param>
    /// <returns>This method always throws; it is a translation marker.</returns>
    public static bool GeoIntersects<TCoordinates>(
        this DbFunctions _,
        GeoJsonGeometry<TCoordinates> field,
        GeoJsonGeometry<TCoordinates> geometry)
        where TCoordinates : GeoJsonCoordinates
        => throw new InvalidOperationException(string.Format(ClientEvalMessage, nameof(GeoIntersects)));

    /// <summary>
    /// Matches documents whose <paramref name="field"/> point lies within <paramref name="radiusMeters"/> of
    /// <paramref name="center"/> on a sphere (MongoDB <c>$geoWithin</c> + <c>$centerSphere</c>). This is a containment
    /// filter and does not order results by distance. Use only inside a single <c>Where</c> predicate.
    /// </summary>
    /// <typeparam name="TCoordinates">The GeoJSON coordinate system.</typeparam>
    /// <param name="_">The <see cref="DbFunctions"/> instance.</param>
    /// <param name="field">The mapped GeoJSON point property to test.</param>
    /// <param name="center">The center point to measure from.</param>
    /// <param name="radiusMeters">The radius in meters.</param>
    /// <returns>This method always throws; it is a translation marker.</returns>
    public static bool GeoWithinCircle<TCoordinates>(
        this DbFunctions _,
        GeoJsonPoint<TCoordinates> field,
        GeoJsonPoint<TCoordinates> center,
        double radiusMeters)
        where TCoordinates : GeoJsonCoordinates
        => throw new InvalidOperationException(string.Format(ClientEvalMessage, nameof(GeoWithinCircle)));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoDbFunctionsExtensionsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Extensions/MongoDbFunctionsExtensionsTests.cs
git commit -m "EF-321: Add EF.Functions geospatial predicate markers"
```

---

## Task 6: `MongoGeoTranslator` — geo `$match` builder

This is the type-agnostic NTS seam: it turns (element path, GeoJSON BSON, operator + args) into a `$match` `BsonDocument`. Keeping it free of EF/driver-LINQ types makes it unit-testable in isolation and reusable by a future NTS layer.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoGeoTranslator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/MongoGeoTranslatorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/MongoGeoTranslatorTests.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using MongoDB.Bson;
using MongoDB.Driver.GeoJsonObjectModel;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

public class MongoGeoTranslatorTests
{
    private static GeoJsonPoint<GeoJson2DGeographicCoordinates> Point(double lng, double lat)
        => new(new GeoJson2DGeographicCoordinates(lng, lat));

    [Fact]
    public void Builds_geoWithin_match()
    {
        var polygon = new GeoJsonPolygon<GeoJson2DGeographicCoordinates>(
            new GeoJsonPolygonCoordinates<GeoJson2DGeographicCoordinates>(
                new GeoJsonLinearRingCoordinates<GeoJson2DGeographicCoordinates>(
                    new[] { Point(0, 0).Coordinates, Point(0, 1).Coordinates, Point(1, 1).Coordinates, Point(0, 0).Coordinates })));

        var match = MongoGeoTranslator.BuildGeoWithin("loc", polygon);

        Assert.Equal("Polygon", match["$match"]["loc"]["$geoWithin"]["$geometry"]["type"].AsString);
    }

    [Fact]
    public void Builds_geoIntersects_match()
    {
        var match = MongoGeoTranslator.BuildGeoIntersects("loc", Point(2, 3));

        Assert.Equal("Point", match["$match"]["loc"]["$geoIntersects"]["$geometry"]["type"].AsString);
    }

    [Fact]
    public void Builds_geoWithinCircle_match_with_radians()
    {
        var match = MongoGeoTranslator.BuildGeoWithinCircle("loc", Point(10, 20), 6378100.0);
        var centerSphere = match["$match"]["loc"]["$geoWithin"]["$centerSphere"].AsBsonArray;

        Assert.Equal(10.0, centerSphere[0][0].AsDouble);  // longitude
        Assert.Equal(20.0, centerSphere[0][1].AsDouble);  // latitude
        Assert.Equal(1.0, centerSphere[1].AsDouble, 9);   // 6378100 m / earth radius == 1 radian
    }

    [Fact]
    public void GeoWithinCircle_rejects_non_positive_radius()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => MongoGeoTranslator.BuildGeoWithinCircle("loc", Point(0, 0), 0));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoGeoTranslatorTests"`
Expected: FAIL — `MongoGeoTranslator` not defined.

- [ ] **Step 3: Implement the translator**

Create `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoGeoTranslator.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.GeoJsonObjectModel;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Builds MongoDB geospatial <c>$match</c> stages. Deliberately free of EF Core and driver-LINQ types so it can be
/// unit-tested in isolation and reused by a future NetTopologySuite layer: given an element path, a geometry, and an
/// operator it produces the raw <see cref="BsonDocument"/> for the pipeline stage.
/// </summary>
internal static class MongoGeoTranslator
{
    // Equatorial Earth radius in meters, per MongoDB's $centerSphere documentation.
    private const double EarthRadiusMeters = 6378100.0;

    public static BsonDocument BuildGeoWithin(string elementPath, object area)
        => BuildMatch(elementPath, "$geoWithin", new BsonDocument("$geometry", ToGeometryBson(area)));

    public static BsonDocument BuildGeoIntersects(string elementPath, object geometry)
        => BuildMatch(elementPath, "$geoIntersects", new BsonDocument("$geometry", ToGeometryBson(geometry)));

    public static BsonDocument BuildGeoWithinCircle<TCoordinates>(
        string elementPath, GeoJsonPoint<TCoordinates> center, double radiusMeters)
        where TCoordinates : GeoJsonCoordinates
    {
        if (radiusMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusMeters), radiusMeters, "The geospatial radius must be a positive number of meters.");
        }

        var values = center.Coordinates.Values; // [longitude, latitude, ...]
        var centerArray = new BsonArray { values[0], values[1] };
        var radians = radiusMeters / EarthRadiusMeters;

        return BuildMatch(elementPath, "$geoWithin",
            new BsonDocument("$centerSphere", new BsonArray { centerArray, radians }));
    }

    private static BsonDocument BuildMatch(string elementPath, string op, BsonDocument operand)
        => new("$match", new BsonDocument(elementPath, new BsonDocument(op, operand)));

    private static BsonValue ToGeometryBson(object geometry)
    {
        var serializer = BsonSerializer.LookupSerializer(geometry.GetType());
        return geometry.ToBsonDocument(geometry.GetType(), serializer);
    }
}
```

> `object.ToBsonDocument(Type, IBsonSerializer)` is the driver's `BsonExtensionMethods.ToBsonDocument` overload (namespace `MongoDB.Bson`). It renders the GeoJSON value to `{ type, coordinates }`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoGeoTranslatorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoGeoTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/MongoGeoTranslatorTests.cs
git commit -m "EF-321: Add MongoGeoTranslator for geo \$match construction"
```

---

## Task 7: Query interception — geo `Where` → appended `$match`

This is the highest-risk task. It hooks `MongoEFToLinqTranslatingExpressionVisitor` so that a `Where` whose predicate body is exactly one `EF.Functions` geo call is rewritten into the visited source plus a `MongoQueryable.AppendStage` of the geo `$match` — the same `AppendStage` mechanism `ProcessVectorSearch` uses (lines 506-536). A `Where` mixing a geo call with other conditions throws a clear "use a separate Where" error.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs` (add internal method-info recognition)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (new `Visit` case + `ProcessGeoWhere`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs` (MQL assertion)

- [ ] **Step 1: Add geo-method recognition helpers to `MongoDbFunctionsExtensions`**

Append inside `MongoDbFunctionsExtensions` (after the three public methods), so the visitor can match by open generic `MethodInfo` (reference-equality discipline, per Query AGENTS.md):

```csharp
    internal static readonly System.Reflection.MethodInfo GeoWithinMethod =
        typeof(MongoDbFunctionsExtensions).GetMethod(nameof(GeoWithin))!;

    internal static readonly System.Reflection.MethodInfo GeoIntersectsMethod =
        typeof(MongoDbFunctionsExtensions).GetMethod(nameof(GeoIntersects))!;

    internal static readonly System.Reflection.MethodInfo GeoWithinCircleMethod =
        typeof(MongoDbFunctionsExtensions).GetMethod(nameof(GeoWithinCircle))!;

    internal static bool IsGeoPredicate(System.Reflection.MethodInfo method)
        => method.IsGenericMethod
           && method.GetGenericMethodDefinition() is var def
           && (def == GeoWithinMethod || def == GeoIntersectsMethod || def == GeoWithinCircleMethod);
```

> `GetMethod(name)` is unambiguous here — each name has a single overload.

- [ ] **Step 2: Write the failing query tests**

Add to `GeospatialQueryTests.cs` (these assert on returned results rather than captured MQL, so no MQL-capture helper is needed):

```csharp
    [Fact]
    public void GeoWithinCircle_emits_centerSphere_match()
    {
        var collection = database.CreateCollection<Place>();
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new Place { Id = 1, Name = "Near", Location = Point(-0.12, 51.5) });
            db.Entities.Add(new Place { Id = 2, Name = "Far", Location = Point(100, 0) });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var origin = Point(-0.12, 51.5);
            var results = db.Entities
                .Where(p => EF.Functions.GeoWithinCircle(p.Location, origin, 1000))
                .ToList();

            results.Should().ContainSingle().Which.Name.Should().Be("Near");
        }
    }

    [Fact]
    public void GeoWithin_returns_points_inside_polygon()
    {
        var collection = database.CreateCollection<Place>();
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new Place { Id = 1, Name = "Inside", Location = Point(0.5, 0.5) });
            db.Entities.Add(new Place { Id = 2, Name = "Outside", Location = Point(5, 5) });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var box = new GeoJsonPolygon<GeoJson2DGeographicCoordinates>(
                new GeoJsonPolygonCoordinates<GeoJson2DGeographicCoordinates>(
                    new GeoJsonLinearRingCoordinates<GeoJson2DGeographicCoordinates>(
                        new[]
                        {
                            new GeoJson2DGeographicCoordinates(0, 0),
                            new GeoJson2DGeographicCoordinates(0, 1),
                            new GeoJson2DGeographicCoordinates(1, 1),
                            new GeoJson2DGeographicCoordinates(1, 0),
                            new GeoJson2DGeographicCoordinates(0, 0)
                        })));

            var results = db.Entities
                .Where(p => EF.Functions.GeoWithin(p.Location, box))
                .ToList();

            results.Should().ContainSingle().Which.Name.Should().Be("Inside");
        }
    }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialQueryTests.GeoWithin"`
Expected: FAIL — the geo `Where` is not translated (EF translation failure / `InvalidOperationException` from the marker method).

- [ ] **Step 4: Add the interception in the visitor**

In `MongoEFToLinqTranslatingExpressionVisitor.cs`, inside the big `Visit(Expression expression)` switch, add a new case immediately before `case MethodCallExpression methodCallExpression: return VisitMethodCall(methodCallExpression);` (line 383), mirroring the VectorSearch case placement (line 379):

```csharp
            // Handle a Where(...) whose predicate is a single EF.Functions geospatial call.
            case MethodCallExpression whereCall
                when IsGeoWhere(whereCall, out var geoCall):
                return ProcessGeoWhere(whereCall, geoCall);
```

Then add these members to the class (place them next to `ProcessVectorSearch`'s supporting members, after the `AddScoreField` field near line 558). They reuse the `ParamValue<T>` helper pattern and the `AppendStage` construction from `ProcessVectorSearch`:

```csharp
    private static bool IsGeoWhere(MethodCallExpression node, out MethodCallExpression geoCall)
    {
        geoCall = null!;
        if (node.Method.DeclaringType != typeof(Queryable)
            || node.Method.Name != nameof(Queryable.Where)
            || node.Arguments.Count != 2)
        {
            return false;
        }

        var lambda = node.Arguments[1].UnwrapLambdaFromQuote();
        if (lambda.Body is MethodCallExpression body
            && body.Method.DeclaringType == typeof(MongoDbFunctionsExtensions)
            && MongoDbFunctionsExtensions.IsGeoPredicate(body.Method))
        {
            geoCall = body;
            return true;
        }

        return false;
    }

    private Expression ProcessGeoWhere(MethodCallExpression whereCall, MethodCallExpression geoCall)
    {
        // Arg 0 is the EF.Functions instance; arg 1 is the field member access; remaining args are the operands.
        var fieldExpression = geoCall.Arguments[1];
        var elementPath = GetGeoFieldElementPath(fieldExpression);

        BsonDocument matchStage = geoCall.Method.Name switch
        {
            nameof(MongoDbFunctionsExtensions.GeoWithin)
                => MongoGeoTranslator.BuildGeoWithin(elementPath, EvaluateGeometry(geoCall.Arguments[2])),
            nameof(MongoDbFunctionsExtensions.GeoIntersects)
                => MongoGeoTranslator.BuildGeoIntersects(elementPath, EvaluateGeometry(geoCall.Arguments[2])),
            nameof(MongoDbFunctionsExtensions.GeoWithinCircle)
                => BuildGeoWithinCircleStage(elementPath, geoCall),
            _ => throw new InvalidOperationException($"Unsupported geospatial function '{geoCall.Method.Name}'.")
        };

        var entityType = _queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType()!)!;
        var clrType = entityType.ClrType;

        var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
            .MakeGenericMethod(clrType, clrType);
        var serializerType = typeof(IBsonSerializer<>).MakeGenericType(clrType);

        return Expression.Call(
            null,
            appendStageMethod,
            Visit(whereCall.Arguments[0])!,
            Expression.New(
                typeof(BsonDocumentPipelineStageDefinition<,>)
                    .MakeGenericType(clrType, clrType)
                    .GetConstructor([typeof(BsonDocument), serializerType])!,
                Expression.Constant(matchStage),
                Expression.Constant(null, serializerType)),
            Expression.Constant(null, serializerType));

        BsonDocument BuildGeoWithinCircleStage(string path, MethodCallExpression call)
        {
            var center = EvaluateGeometry(call.Arguments[2]);
            var radiusMeters = EvaluateScalar<double>(call.Arguments[3]);
            var method = typeof(MongoGeoTranslator)
                .GetMethod(nameof(MongoGeoTranslator.BuildGeoWithinCircle))!
                .MakeGenericMethod(center.GetType().BaseType!.GetGenericArguments()[0]);
            return (BsonDocument)method.Invoke(null, [path, center, radiusMeters])!;
        }
    }

    private string GetGeoFieldElementPath(Expression fieldExpression)
    {
        // The field argument is a bare member access (e.g. `p.Location`), possibly wrapped in a Convert —
        // not a lambda, so we read the member directly rather than via GetMemberAccess.
        var expr = fieldExpression;
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            expr = convert.Operand;
        }

        var entityType = _queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType()!);
        var property = expr is MemberExpression member ? entityType?.FindProperty(member.Member.Name) : null;

        if (property == null)
        {
            throw new InvalidOperationException(
                $"Could not translate the geospatial predicate on '{(entityType?.ClrType ?? _source.Type).ShortDisplayName()}'. "
                + "The first argument must be a mapped GeoJSON property directly on the queried entity.");
        }

        var path = entityType!.GetDocumentPath();
        return string.Join('.', path.Append(property.GetElementName()));
    }

    private object EvaluateGeometry(Expression expression)
        => EvaluateScalar<object>(expression)
           ?? throw new InvalidOperationException("A geospatial predicate geometry argument cannot be null.");

    private TValue? EvaluateScalar<TValue>(Expression expression)
    {
#if EF8 || EF9
        if (expression is ParameterExpression p)
        {
            return (TValue?)_queryContext.ParameterValues[p.Name!];
        }
#else
        if (expression is QueryParameterExpression qp)
        {
            return (TValue?)_queryContext.Parameters[qp.Name];
        }
#endif
        var value = Expression.Lambda(expression).Compile().DynamicInvoke();
        return (TValue?)value;
    }
```

Add any missing `using`s at the top of the file: `MongoDB.EntityFrameworkCore.Extensions` (for `MongoDbFunctionsExtensions` — note it is in the `Microsoft.EntityFrameworkCore` namespace, so it resolves without a using), `MongoDB.Bson`, `MongoDB.Bson.Serialization`, `MongoDB.Driver`, `MongoDB.Driver.Linq` (for `MongoQueryable`). Confirm against the existing usings — most are already present because `ProcessVectorSearch` uses them.

> **Field-path note:** `GetGeoFieldElementPath` handles the common case of a direct mapped property on the queried entity. Nested-document GeoJSON fields (geometry on an owned sub-document) are out of scope for v1 and fall into the "could not translate" branch — acceptable per the design's unsupported-case handling.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialQueryTests"`
Expected: PASS (round-trip + GeoWithinCircle + GeoWithin = 3+ tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs
git commit -m "EF-321: Translate EF.Functions geo predicates to \$match stages"
```

---

## Task 8: Unsupported-shape guard + GeoIntersects coverage

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs`
- Modify (if needed): `MongoEFToLinqTranslatingExpressionVisitor.cs` `IsGeoWhere`

- [ ] **Step 1: Write the failing tests**

Add to `GeospatialQueryTests.cs`:

```csharp
    [Fact]
    public void GeoIntersects_returns_intersecting_geometry()
    {
        var collection = database.CreateCollection<Place>();
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new Place { Id = 1, Name = "Hit", Location = Point(0.5, 0.5) });
            db.Entities.Add(new Place { Id = 2, Name = "Miss", Location = Point(9, 9) });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var box = new GeoJsonPolygon<GeoJson2DGeographicCoordinates>(
                new GeoJsonPolygonCoordinates<GeoJson2DGeographicCoordinates>(
                    new GeoJsonLinearRingCoordinates<GeoJson2DGeographicCoordinates>(
                        new[]
                        {
                            new GeoJson2DGeographicCoordinates(0, 0),
                            new GeoJson2DGeographicCoordinates(0, 1),
                            new GeoJson2DGeographicCoordinates(1, 1),
                            new GeoJson2DGeographicCoordinates(1, 0),
                            new GeoJson2DGeographicCoordinates(0, 0)
                        })));

            var results = db.Entities
                .Where(p => EF.Functions.GeoIntersects(p.Location, box))
                .ToList();

            results.Should().ContainSingle().Which.Name.Should().Be("Hit");
        }
    }

    [Fact]
    public void Mixing_geo_with_other_predicate_in_same_Where_throws()
    {
        var collection = database.CreateCollection<Place>();
        using var db = SingleEntityDbContext.Create(collection);

        var origin = Point(0, 0);
        var act = () => db.Entities
            .Where(p => p.Name == "x" && EF.Functions.GeoWithinCircle(p.Location, origin, 100))
            .ToList();

        act.Should().Throw<InvalidOperationException>();
    }
```

The mixing test passes already because `IsGeoWhere` only matches when the lambda body *is* the geo call (an `AndAlso` body does not match, so the marker method throws at evaluation). The intersects test exercises the `GeoIntersects` arm.

- [ ] **Step 2: Run tests to verify status**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialQueryTests"`
Expected: `GeoIntersects` PASSES; `Mixing...throws` PASSES. If `Mixing` fails (e.g. produces wrong results instead of throwing), add an explicit guard in `ProcessGeoWhere`/`IsGeoWhere` that throws `InvalidOperationException` with a "place the geospatial function in its own Where clause" message when a geo call is found nested inside a compound predicate.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/GeospatialQueryTests.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs
git commit -m "EF-321: Cover GeoIntersects and reject mixed geo predicates"
```

---

## Task 9: End-to-end `2dsphere` index creation test

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Storage/GeospatialIndexTests.cs`

- [ ] **Step 1: Write the failing test**

Create the file (model on `Storage/IndexTests.cs`, but `2dsphere` works on non-Atlas MongoDB so use the ordinary `TemporaryDatabaseFixture` and `EnsureCreated`, then read `listIndexes`):

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class GeospatialIndexTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Place
    {
        public int Id { get; set; }
        public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = null!;
    }

    [Fact]
    public void EnsureCreated_creates_2dsphere_index()
    {
        var collection = database.CreateCollection<Place>();
        using var db = SingleEntityDbContext.Create(collection,
            b => b.Entity<Place>().HasIndex(e => e.Location).Is2dSphereIndex());

        db.Database.EnsureCreated();

        var indexes = collection.Indexes.List().ToList();
        var geoIndex = indexes.Single(i => i["name"].AsString == "Location_2dsphere");
        geoIndex["key"]["Location"].AsString.Should().Be("2dsphere");
    }
}
```

> `database.CreateCollection<Place>()` returns the underlying `IMongoCollection<Place>`; `.Indexes.List()` returns the raw index documents. Confirm the accessor name against `Storage/IndexTests.cs`'s `ValidateIndex` helper, which already reads `collection.Indexes`.

- [ ] **Step 2: Run test to verify it fails, then passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~GeospatialIndexTests"`
Expected: PASS (the index plumbing from Tasks 1-3 already supports it; this is the end-to-end proof). If it fails because `MakeIndexName` produced a different name, align the expected name with the actual emitted name.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Storage/GeospatialIndexTests.cs
git commit -m "EF-321: End-to-end 2dsphere index creation test"
```

---

## Task 10: Docs and multi-EF verification

**Files:**
- Modify: `README.md:104-110`
- Modify: `docs/failing-spec-tests.md`

- [ ] **Step 1: Update README limitations**

In `README.md`, remove `Geospatial` from the "Planned for future releases" list (line ~107) and add a short "Geospatial" subsection under features describing: supported types (driver GeoJSON), `Is2dSphereIndex()`, and the three `EF.Functions` predicates, plus the v1 limitations (no distance ordering / `$geoNear`, no `minDistance`, no NTS, geo predicate must be its own `Where`).

```markdown
### Geospatial (partial)

- Store MongoDB GeoJSON types (`GeoJsonPoint<T>`, `GeoJsonPolygon<T>`, ...) on entity properties.
- Create `2dsphere` indexes with `entity.HasIndex(e => e.Location).Is2dSphereIndex()`.
- Filter with `EF.Functions.GeoWithin`, `EF.Functions.GeoIntersects`, and `EF.Functions.GeoWithinCircle`
  (each as its own `Where` clause).

Not yet supported: distance-sorted `$near` / `$geoNear`, distance projection, `minDistance`,
NetTopologySuite types, and EF Core's NTS-based spatial conformance suite.
```

- [ ] **Step 2: Note deferred NTS conformance**

In `docs/failing-spec-tests.md`, add a line under the appropriate section recording that EF Core's NTS-based `SpatialQueryTestBase` remains unimplemented and is deferred to a future NTS phase (no XUnit `Skip` added — the suite is simply not inherited, as today).

- [ ] **Step 3: Commit docs**

```bash
git add README.md docs/failing-spec-tests.md
git commit -m "EF-321: Document geospatial support and deferred NTS conformance"
```

- [ ] **Step 4: Run the full multi-EF suite**

Invoke the `/test-all` skill to build and test EF8, EF9, and EF10 in parallel. Resolve any `#if`-related compile differences (the `EvaluateScalar` helper already guards EF8/EF9 vs EF10 parameter access; confirm `QueryParameterExpression` / `_queryContext.Parameters` names match the EF10 build and `ParameterExpression` / `_queryContext.ParameterValues` match EF8/EF9 — copy the exact pattern from `ProcessVectorSearch.ParamValue`).

Run (per version, if not using the skill):
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"  && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8"  --no-build --filter "FullyQualifiedName~Geo"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"  && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9"  --no-build --filter "FullyQualifiedName~Geo"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Geo"
```
Expected: all geospatial tests green on all three EF versions.

- [ ] **Step 5: Final review**

Run the `/review-ef-core-provider` skill over the branch to get the area reviewers (query, storage, metadata, serialization, public-api, api-stability, ef-conformance) on the diff. Address findings.

---

## Notes for the implementer

- **Highest-risk task is Task 7.** The mechanism (build a `$match` `BsonDocument`, append via `MongoQueryable.AppendStage`) is copied directly from `ProcessVectorSearch` (`MongoEFToLinqTranslatingExpressionVisitor.cs:506-536`). If the `AppendStage` rendering misbehaves, compare against how the VectorSearch `$addFields` stage is appended (lines 526-536, 557-558) — that is the known-good reference. The `EvaluateScalar` parameter-access path differs by EF version; the EF8/EF9 vs EF10 split mirrors `ProcessVectorSearch.ParamValue` exactly.
- **Element-path resolution** mirrors `ProcessVectorSearch`'s use of `GetMemberAccess` + `entityType.FindMember`/`FindProperty` and `InternalIndexExtensions.CreateIndexDocument`'s `GetDocumentPath().Append(GetElementName())`.
- **Do not call `BsonSerializer.RegisterSerializer`.** Only `LookupSerializer` / `LookupClassMap`, consistent with the Serializers AGENTS.md rule.
- **Coordinate order** is `[longitude, latitude]` everywhere (GeoJSON + `$centerSphere`), matching `GeoJsonCoordinates.Values`.
```
