---
area: Serialization & ChangeTracking
scope: ["src/MongoDB.EntityFrameworkCore/Serializers/**", "src/MongoDB.EntityFrameworkCore/ChangeTracking/**"]
reviewer-agent: serialization-reviewer
adjacent-areas: [Storage (ValueConversion), Metadata, Query, "C# driver BSON serializer registry"]
---

# Serialization & ChangeTracking — AGENTS.md

This `AGENTS.md` covers two sibling directories:
- `src/MongoDB.EntityFrameworkCore/Serializers/` — bridges EF property/entity metadata to the C# driver's `IBsonSerializer<T>` framework.
- `src/MongoDB.EntityFrameworkCore/ChangeTracking/` — supplies the `ValueComparer<T>` instances EF needs to detect mutations in collections and dictionaries (the BCL-default comparers aren't suitable for many of these).

`src/MongoDB.EntityFrameworkCore/ChangeTracking/CLAUDE.md` re-points at this file.

## Scope

In: serializer factory + entity / value-converter / discriminator serializers; the family of `ValueComparer<T>` types for `IEnumerable<T>` / `IList<T>` / `Dictionary<string, T>` (with and without nullable element types).

Out: BSON wire encoding itself (lives in `MongoDB.Bson` in the driver); `IBsonSerializer<T>` implementations for primitives, collections, and dictionaries (the driver supplies those; the factory composes them); annotation storage (Metadata area); `ValueConverter<TModel, TProvider>` *types* themselves (Storage / ValueConversion area — but the serializer layer *wraps* them via `ValueConverterSerializer`).

## Key entry points

### Serializers

- `BsonSerializerFactory` — the central factory. Given an `IReadOnlyProperty` or a CLR type, returns a suitable `IBsonSerializer`. Caches entity serializers by `IReadOnlyEntityType`. Knows about primitives, `ObjectId`, `Decimal128`, `BinaryVectorFloat32` / `Int8` / `PackedBit`, arrays, `IEnumerable<T>`, `ReadOnlyCollection<T>`, `IDictionary<string, T>` / `IReadOnlyDictionary<string, T>`, `Nullable<T>`, enums, and value-type entities (via `BsonClassMap`). Applies the `Mongo:BsonRepresentation` annotation via `ApplyBsonRepresentation` (`IRepresentationConfigurable.WithRepresentation` / `IRepresentationConverterConfigurable.WithConverter`). Recursive into `INullableSerializer` so a `BsonRepresentation` on a `Nullable<T>` does the right thing.
- `EntitySerializer<T>` — implements `IBsonDocumentSerializer`; bridges `IReadOnlyEntityType` to per-member serialization info. Property-by-property entity↔BSON mapping, including owned-entity nesting.
- `ValueConverterSerializer<TActual, TStorage>` — wraps an EF `ValueConverter<TActual, TStorage>` around a storage serializer. The C# driver knows nothing about EF; this is the adapter. Nullable model types throw — see the explicit guard in the constructor.
- `MongoEFDiscriminator` — implements the driver's `IScalarDiscriminatorConvention` for EF's discriminator-property model. The driver's LINQ provider uses this to generate `{ _t: <value> }` filters for polymorphic queries.

### ChangeTracking

- `ListOfValueTypesComparer<,>` (EF8 only path; superseded in EF9+ by EF's own equivalent for some shapes) — `ValueComparer<IEnumerable<TElement>>` for non-nullable structs. Element-wise equals, hash, and deep snapshot.
- `ListOfNullableValueTypesComparer<,>` (EF8) — same, for `IEnumerable<TElement?>` where `TElement : struct`.
- `ListOfReferenceTypesComparer<,>` (EF8) — typed as `ValueComparer<object>` because reference-type element comparers can't be uniformly typed.
- `StringDictionaryComparer<,>` — for `IDictionary<string, T>` / `IReadOnlyDictionary<string, T>`. Two file variants: an EF8/EF9 legacy and an EF10 redesign with expression-based composition; pick the right one via `#if`.
- `NullableStringDictionaryComparer<,>` (EF8/EF9) — value-type-nullable dictionary values.
- `NullableEqualityComparer<T>` (EF8/EF9 helper) — composes an element comparer through `T?`.

## Boundaries with adjacent areas

- **vs Storage/ValueConversion.** A `ValueConverter<TModel, TProvider>` (Storage) is a pure expression-based model↔provider transform. A `ValueComparer<T>` (here) is equality + snapshot semantics. A `ValueConverterSerializer<TActual, TStorage>` (here) is the bridge that runs the converter at BSON read/write time. Easy mistake: putting equality logic into a `ValueConverter` or conversion into a `ValueComparer`. They aren't interchangeable.
- **vs Metadata.** Metadata annotations are read here — `GetBsonRepresentation`, `GetBinaryVectorDataType`, discriminator config — and turned into serializer choices. The serializer never sets annotations.
- **vs Query.** Query asks `BsonSerializerFactory.CreateTypeSerializer(...)` for the serializer it needs for a projection result and a `MongoEFDiscriminator` for polymorphic filters. Query does not instantiate serializers directly.
- **vs the driver's serializer registry.** The factory composes driver serializers (`Int32Serializer`, `DictionaryInterfaceImplementerSerializer<,,>`, `NullableSerializer<T>`, etc.). It does **not** call `BsonSerializer.RegisterSerializer(...)` — global driver state must not be mutated from a provider. For value-type entities the factory does call `BsonClassMap.LookupClassMap(type)`, which is read-only.

## Common pitfalls

- **Nullable model types in `ValueConverterSerializer`.** The constructor rejects `TActual = Nullable<>`. The serializer must wrap the storage type in `NullableSerializer<>` *outside* the converter, not inside.
- **Dictionary key type.** Only `string` keys are supported (it's the only shape MongoDB documents take). Non-string-keyed dictionaries return `null` from the type-mapping path. Don't quietly extend this — MongoDB's wire format actually constrains it.
- **Multi-dim arrays.** Only rank-1 arrays are supported; the factory throws for higher ranks.
- **Entity serializers are cached per `IReadOnlyEntityType`.** Mutating annotations after a serializer has been built means the cached serializer is stale. This shouldn't happen in normal usage — the runtime model is immutable — but is a trap in tests that build models on the fly.
- **EF8 vs. EF10 comparer signatures.** `StringDictionaryComparer<,>` was reworked between EF8/EF9 (`<TElement, TCollection>`) and EF10 (`<TDictionary, TElement>`, expression-based). New comparer work must compile on all three EF targets; `#if` carefully.
- **`MongoEFDiscriminator` is for the *driver's* LINQ.** It produces values that the driver's LINQ-v3 provider can match against `_t`. EF's own discriminator infrastructure operates on the property level; bridging them lives here.
- **`IEnumerableSerializer<TItem>` / `IGroupingSerializer<TKey,TElement>` analogues** exist in the C# driver under different names. Don't invent parallel serializer types here for things the driver already covers — wrap/compose.

## How to test

- Unit tests (factory output, comparers): `tests/MongoDB.EntityFrameworkCore.UnitTests/Serializers/` and `tests/MongoDB.EntityFrameworkCore.UnitTests/ChangeTracking/`.
- Functional tests (round-trips, dictionary change tracking): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Mapping/` and `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Serialization/`.
- Cross-cutting representative tests live under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/` (change-tracking in queries, e.g. `NorthwindChangeTrackingQueryMongoTest`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Serializ|FullyQualifiedName~ChangeTracking|FullyQualifiedName~Mapping"
```
