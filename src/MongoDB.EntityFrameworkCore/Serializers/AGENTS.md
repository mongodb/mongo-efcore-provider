---
area: Serialization & ChangeTracking
scope: ["src/MongoDB.EntityFrameworkCore/Serializers/**", "src/MongoDB.EntityFrameworkCore/ChangeTracking/**"]
reviewer-agent: serialization-reviewer
adjacent-areas: [Storage (ValueConversion), Metadata, Query, "C# driver BSON serializer registry"]
---

# Serialization & ChangeTracking — AGENTS.md

Covers two sibling dirs (`ChangeTracking/CLAUDE.md` re-points here):

- `Serializers/` — bridges EF property/entity metadata to the driver's `IBsonSerializer<T>` framework.
- `ChangeTracking/` — `ValueComparer<T>` instances for collections/dictionaries where BCL defaults don't work.

**Out:** BSON wire encoding (`MongoDB.Bson`); primitive/collection `IBsonSerializer<T>` impls (driver-supplied,
factory composes them); annotation storage (Metadata); `ValueConverter<TModel, TProvider>` types themselves
(Storage/ValueConversion — the serializer layer wraps them).

## Key entry points

### Serializers

- `BsonSerializerFactory` — `IReadOnlyProperty`/CLR type → `IBsonSerializer`, caching entity serializers by
  `IReadOnlyEntityType`. Handles primitives, `ObjectId`, `Decimal128`, `BinaryVector*`, arrays,
  `IEnumerable<T>`, `ReadOnlyCollection<T>`, string-keyed dictionaries, `Nullable<T>`, enums, value-type
  entities (`BsonClassMap`). Applies `Mongo:BsonRepresentation` via `ApplyBsonRepresentation`, recursing into
  `INullableSerializer` so it lands on the underlying serializer for `Nullable<T>`.
- `EntitySerializer<T>` — `IBsonDocumentSerializer`; per-member entity↔BSON mapping, incl. owned nesting.
- `ValueConverterSerializer<TActual, TStorage>` — wraps an EF `ValueConverter` around a storage serializer.
- `MongoEFDiscriminator` — `IScalarDiscriminatorConvention` over EF's discriminator model, so the driver's LINQ
  provider can generate `{ _t: <value> }` filters.

### ChangeTracking

`ListOfValueTypesComparer<,>`, `ListOfNullableValueTypesComparer<,>`, `ListOfReferenceTypesComparer<,>`
(typed as `ValueComparer<object>`) — EF8-path collection comparers. `StringDictionaryComparer<,>` for
string-keyed dictionaries in two variants (EF8/EF9 legacy vs. EF10 expression-based, selected by `#if`), plus
`NullableStringDictionaryComparer<,>`/`NullableEqualityComparer<T>`.

## Boundaries with adjacent areas

- **vs Storage/ValueConversion.** A `ValueConverter` is a pure model↔provider transform; a `ValueComparer` is
  equality/snapshot semantics; `ValueConverterSerializer` runs the converter at BSON read/write time. Don't mix
  equality logic into a converter or conversion into a comparer.
- **vs Metadata.** Annotations are read here, never written.
- **vs Query.** Query asks the factory for serializers / `MongoEFDiscriminator`; never instantiates directly.
- **vs the driver's registry.** The factory composes driver serializers — never call
  `BsonSerializer.RegisterSerializer(...)` (a provider must not mutate global driver state);
  `BsonClassMap.LookupClassMap(type)` (read-only) is fine.

## Common pitfalls

- **`ValueConverterSerializer` rejects `TActual = Nullable<>`** — wrap the storage type in
  `NullableSerializer<>` outside the converter, not inside.
- **Only `string` dictionary keys are supported** — the only shape a BSON document takes.
- **Only rank-1 arrays** — the factory throws for higher ranks.
- **Entity serializers are cached per `IReadOnlyEntityType`** — mutating annotations after one is built leaves
  it stale (a trap in tests building models on the fly, not in normal use).
- **Comparer signatures moved between EF8/EF9 and EF10** — new comparer work must compile on all three.
- **Don't invent parallel serializer types** for things the driver already covers — wrap and compose instead.

## How to test

- Unit: `…UnitTests/Serializers/`, `…UnitTests/ChangeTracking/`.
- Functional: `…FunctionalTests/Mapping/`, `…/Serialization/`.
- Cross-cutting: `…SpecificationTests/Query/` (e.g. `NorthwindChangeTrackingQueryMongoTest`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Serializ|FullyQualifiedName~ChangeTracking|FullyQualifiedName~Mapping"
```
