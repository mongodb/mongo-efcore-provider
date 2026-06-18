# Geospatial query support (driver GeoJSON types) — Design

**Date:** 2026-06-18
**Status:** Approved (design); implementation plan written
**JIRA:** EF-321

## Summary

Add MongoDB geospatial support to the EF Core provider in a tractable first release:

1. Allow MongoDB C# driver GeoJSON types (`GeoJsonPoint<T>`, `GeoJsonPolygon<T>`, …) on
   entity properties, persisted via the driver's existing GeoJSON serializers.
2. Create `2dsphere` indexes over those properties through a fluent model-building API and
   `EnsureCreated`.
3. Translate three geospatial predicates — exposed as `EF.Functions` methods — into the
   corresponding MQL geo `$match` operators.

The design is deliberately staged so a future NetTopologySuite (NTS) layer can be added on
top of a shared, type-agnostic translation core without rework.

## Background and key constraints

Two findings shaped this design and override the assumptions in the initial exploration:

1. **The driver has no LINQ geo translation.** The MongoDB C# driver (v3.9.0) ships GeoJSON
   *serializers* (`GeoJsonPointSerializer<T>`, etc.), so persisting GeoJSON values is free.
   But its geo *query* operators (`GeoWithin`, `GeoIntersects`, `NearSphere`, `GeoNear`) are
   exposed only through the filter-definition builder (`Builders<T>.Filter.GeoWithin(...)`)
   and the aggregate-fluent `GeoNear` stage — there is **no `IMongoQueryable` geo extension**.
   A user cannot write `.Where(x => x.Location.GeoWithin(poly))` in driver LINQ. Therefore the
   provider must translate geo predicates **explicitly** (emit raw geo `$match` BSON), the same
   way the VectorSearch feature is handled — not by relying on LINQ passthrough.

2. **The provider executes queries as aggregation pipelines** (`IMongoCollection.Aggregate`,
   confirmed in `Query/SerializerOverrideCollection.cs`). MongoDB forbids `$near` / `$nearSphere`
   inside an aggregation `$match`. Proximity inside a pipeline must use either `$geoWithin` with
   `$centerSphere` (radius containment, no sorting) or the `$geoNear` stage (must be first stage,
   produces a computed distance). Consequently, v1 proximity is **radius containment**, not
   distance-sorted "near". The distance-sorted / computed-distance behavior is deferred to a
   later `$geoNear` phase.

## Type-system decision

v1 exposes the **driver's GeoJSON types** directly on entities. NetTopologySuite (NTS) support —
the EF-ecosystem standard that would satisfy EF Core's NTS-based spatial conformance suite — is
deferred to a later phase. Rationale:

- Driver GeoJSON serialization is free; NTS would require a bidirectional NTS↔GeoJSON conversion
  serializer plus coordinate-system / SRID handling (NTS is planar `(X, Y)` + SRID; GeoJSON is
  spherical `[lng, lat]`).
- With driver types we control a small, invented predicate vocabulary (`EF.Functions.*`); NTS
  would require mapping NTS's larger external method surface for conformance.

The required explicit-translation core (finding 1 above) is type-agnostic and becomes the seam
the NTS phase plugs into (see "NTS seam").

## Scope

### In scope (v1)

| `EF.Functions` method | MQL emitted | Notes |
|---|---|---|
| `GeoWithin(field, area)` | `{ field: { $geoWithin: { $geometry: <area> } } }` | `area` is any GeoJSON polygon / multipolygon |
| `GeoIntersects(field, geometry)` | `{ field: { $geoIntersects: { $geometry: <geometry> } } }` | geometries overlap |
| `GeoWithinCircle(field, center, radiusMeters)` | `{ field: { $geoWithin: { $centerSphere: [ [lng,lat], radiusMeters/6378100 ] } } }` | spherical radius containment |

Plus:
- Persisting driver GeoJSON types on entity properties.
- `2dsphere` index creation via a fluent builder API and `EnsureCreated`.

### Out of scope (deferred)

- `$geoNear` aggregation stage.
- Distance projection / distance-based ordering (a true `$near`).
- `minDistance` (inner-radius exclusion) — v1 takes a single `radiusMeters`.
- Legacy `2d` indexes.
- NetTopologySuite (NTS) types and EF Core spatial spec-test conformance.

### Naming note

The selected scope was described as "proximity filter." Because queries run as aggregation
pipelines, a true distance-sorted `$near` is unavailable. The achievable behavior is radius
containment via `$geoWithin` + `$centerSphere`. The method is therefore named `GeoWithinCircle`
(not `Near`) so the API does not promise ordering it cannot deliver.

## Public API surface

### Storage

Users place driver GeoJSON types directly on entities:

```csharp
public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; }
public GeoJsonPolygon<GeoJson2DGeographicCoordinates> Area { get; set; }
```

### Predicates

A new `MongoDbFunctionsExtensions` static class extends `DbFunctions` (so the methods hang off
`EF.Functions`), generic over `TCoordinates : GeoJsonCoordinates`:

```csharp
public static bool GeoWithin<TCoordinates>(
    this DbFunctions _, GeoJsonGeometry<TCoordinates> field, GeoJsonGeometry<TCoordinates> area)
    where TCoordinates : GeoJsonCoordinates;

public static bool GeoIntersects<TCoordinates>(
    this DbFunctions _, GeoJsonGeometry<TCoordinates> field, GeoJsonGeometry<TCoordinates> geometry)
    where TCoordinates : GeoJsonCoordinates;

public static bool GeoWithinCircle<TCoordinates>(
    this DbFunctions _, GeoJsonPoint<TCoordinates> field, GeoJsonPoint<TCoordinates> center, double radiusMeters)
    where TCoordinates : GeoJsonCoordinates;
```

Each method body throws `InvalidOperationException` — the standard `EF.Functions`
"translation-only marker" pattern. The shared `TCoordinates` generic prevents mixing coordinate
systems at compile time.

### Index

A fluent extension in `MongoIndexBuilderExtensions`:

```csharp
modelBuilder.Entity<Place>().HasIndex(e => e.Location).Is2dSphereIndex();
```

## Architecture and files

Mirrors the structure of the existing VectorSearch feature.

### New files

- `src/MongoDB.EntityFrameworkCore/Extensions/MongoDbFunctionsExtensions.cs` — the three
  `EF.Functions` predicate markers.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoGeoTranslator.cs` — type-agnostic core:
  given (element path, GeoJSON-BSON geometry, operator kind) it builds the geo `$match` BSON.
  This is the NTS seam.
- `src/MongoDB.EntityFrameworkCore/Extensions/MongoIndexExtensions.cs` — annotation get/set for
  the geospatial index type (modeled on `MongoPropertyExtensions`; does not exist yet).
- Tests (see "Testing").

### Edited files

- `Metadata/MongoAnnotationNames.cs` — add `GeospatialIndexType` constant
  (`"Mongo:GeospatialIndexType"`).
- `Extensions/MongoIndexBuilderExtensions.cs` — add `Is2dSphereIndex()` (generic and non-generic,
  matching the existing `IsVectorIndex` pair).
- `Metadata/InternalIndexExtensions.cs` — `CreateIndexDocument()` detects the annotation and emits
  `{ "<element-path>": "2dsphere" }`.
- `Storage/MongoTypeMappingSource.cs` — recognize the `GeoJsonGeometry<T>` hierarchy and return a
  `MongoTypeMapping`.
- `Serializers/BsonSerializerFactory.cs` — for GeoJSON types, return the driver's serializer via
  `BsonSerializer.LookupSerializer(type)` (no custom serializer).
- `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` and/or
  `MongoEFToLinqTranslatingExpressionVisitor.cs` — intercept the three `EF.Functions` calls and
  route to `MongoGeoTranslator`.

## Query translation data flow

```
EF LINQ:  .Where(p => EF.Functions.GeoWithin(p.Location, area))
   |
   v  translation visitor recognizes a MongoDbFunctionsExtensions MethodCallExpression
   |    - resolve `field` argument -> element path (existing element-name machinery)
   |    - evaluate the geometry argument (constant / captured parameter) to a driver GeoJSON object
   |    - serialize the geometry -> BSON via its driver serializer
   v
MongoGeoTranslator builds the $match filter BSON for the operator kind
   |
   v  injected into the pipeline as a $match stage (where existing Where filters land)
MQL:  { $match: { "location": { $geoWithin: { $geometry: { ... } } } } }
```

The geometry argument is a captured value, not a column: it is evaluated to a concrete GeoJSON
object at translation time and serialized, exactly as other constant/parameter arguments are
handled today. `radiusMeters` is read as a value and converted to radians (`radiusMeters / 6378100`,
using the equatorial Earth radius in meters that MongoDB documents for `$centerSphere`).

## Index creation (`2dsphere`)

`Is2dSphereIndex()` sets `Mongo:GeospatialIndexType = "2dsphere"` on the `IMutableIndex`. During
`EnsureCreated`, `CreateIndexDocument()` checks for the annotation and builds a standard
`CreateIndexModel<BsonDocument>` whose key document is `{ "<element-path>": "2dsphere" }` instead
of the normal ascending/descending keys. Existing `CreateIndexOptions` plumbing (name, sparse,
partial filter, etc.) is reused. This is a standard index — unlike the vector feature's Atlas
search index — so no `CreateSearchIndexModel` path is involved.

`$geoWithin` and `$geoIntersects` do not strictly require the index (they run faster with it), so
queries still return correct results if the user omits `Is2dSphereIndex()`. The index becomes a
correctness requirement only for the deferred `$geoNear` phase.

## Type mapping and serialization

GeoJSON serialization is free: the driver ships `GeoJsonPointSerializer<T>`,
`GeoJsonPolygonSerializer<T>`, etc. `MongoTypeMappingSource` only needs to recognize the
`GeoJsonGeometry<T>` hierarchy as mappable (so EF does not reject the property), and
`BsonSerializerFactory` returns `BsonSerializer.LookupSerializer(clrType)` for those types. From
EF's perspective these properties are simple scalars (a single BSON sub-document); no owned-entity
modeling is involved.

## The NTS seam

`MongoGeoTranslator` is written against **(element path, GeoJSON-BSON geometry, operator kind)** —
not against driver types. The only driver-specific step is "evaluate the `EF.Functions` argument
and serialize it to GeoJSON BSON." When NTS is added later it contributes:

1. An NTS↔GeoJSON BSON serializer (with coordinate-system / SRID handling).
2. Recognition of NTS's native methods (`Within`, `Intersects`, `IsWithinDistance`) that feed the
   **same** `MongoGeoTranslator` emission.

No rework of the pipeline-injection core is required. The driver-GeoJSON `EF.Functions.*` vocabulary
and the future NTS native-method vocabulary coexist without collision.

## Error handling and unsupported cases

- `EF.Functions` methods executed client-side throw `InvalidOperationException` (standard marker
  behavior).
- Mismatched coordinate systems are prevented at compile time by the shared `TCoordinates` generic.
- A geo function used where translation cannot bind the field to an element path throws a clear
  translation `NotSupportedException` naming the offending expression. Per `AGENTS.md`, the
  exception type for an *unsupported* feature is not part of the public contract, so it is free to
  evolve.
- `radiusMeters <= 0` is rejected with a clear argument-validation message during translation.

## Testing strategy

- **Unit tests** (`UnitTests`, no database): annotation get/set round-trip; `Is2dSphereIndex()`
  sets the correct annotation; `CreateIndexDocument()` emits the correct `2dsphere` key BSON;
  `EF.Functions` methods throw when invoked directly.
- **Functional tests** (`FunctionalTests`, real MongoDB via TestContainers): store/round-trip
  `GeoJsonPoint` / `GeoJsonPolygon`; `EnsureCreated` creates a `2dsphere` index (asserted via
  `listIndexes`); end-to-end `GeoWithin` / `GeoIntersects` / `GeoWithinCircle` queries return the
  correct rows; MQL assertions on the emitted `$match` using the existing MQL-logging harness.
- **Spec tests:** EF Core's `SpatialQueryTestBase` is NTS-based and stays unimplemented in v1 (as
  today); conformance is an explicit deliverable of the future NTS phase, noted in
  `docs/failing-spec-tests.md`. No XUnit `Skip` is introduced.
- **Multi-version:** the API is plain `EF.Functions` extensions plus annotations, expected to
  compile identically on EF8 / EF9 / EF10. The implementation plan includes a `/test-all` run to
  confirm no `#if` divergence is required.

## Open questions / risks

- Exact interception point: whether the `EF.Functions` calls are most cleanly intercepted in
  `MongoQueryableMethodTranslatingExpressionVisitor` or `MongoEFToLinqTranslatingExpressionVisitor`
  is to be confirmed during implementation (both are candidates; the element-path resolution lives
  near the latter).
- Confirm `BsonSerializer.LookupSerializer` returns the GeoJSON serializers without any explicit
  registration step in the provider's serializer factory path.
