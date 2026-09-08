---
area: Serialization & ChangeTracking
scope: ["src/MongoDB.EntityFrameworkCore/Serializers/**", "src/MongoDB.EntityFrameworkCore/ChangeTracking/**"]
reviewer-agent: serialization-reviewer
adjacent-areas: [Storage (ValueConversion), Metadata, Query, "C# driver BSON serializer registry"]
---

# Serialization & ChangeTracking — AGENTS.md

Covers two sibling directories (`ChangeTracking/CLAUDE.md` re-points here):

- `Serializers/` — bridges EF property/entity metadata to the driver's `IBsonSerializer<T>` framework.
- `ChangeTracking/` — the `ValueComparer<T>` instances EF needs to detect mutation in collections and
  dictionaries, where the BCL defaults are unsuitable.

**Out:** BSON wire encoding (the driver's `MongoDB.Bson`); the primitive/collection/dictionary
`IBsonSerializer<T>` implementations (the driver supplies them, the factory *composes* them); annotation
storage (Metadata); `ValueConverter<TModel, TProvider>` types themselves (Storage/ValueConversion — though the
serializer layer wraps them).

## Key entry points

### Serializers

- `BsonSerializerFactory` — the central factory: `IReadOnlyProperty` or CLR type → `IBsonSerializer`, caching
  entity serializers by `IReadOnlyEntityType`. Handles primitives, `ObjectId`, `Decimal128`, the
  `BinaryVector*` types, arrays, `IEnumerable<T>`, `ReadOnlyCollection<T>`, string-keyed dictionaries,
  `Nullable<T>`, enums, and value-type entities (via `BsonClassMap`). Applies `Mongo:BsonRepresentation`
  through `ApplyBsonRepresentation`, recursing into `INullableSerializer` so a representation on a
  `Nullable<T>` lands on the underlying serializer.
- `EntitySerializer<T>` — `IBsonDocumentSerializer`; per-member entity↔BSON mapping, including owned nesting.
- `ValueConverterSerializer<TActual, TStorage>` — wraps an EF `ValueConverter` around a storage serializer. The
  driver knows nothing about EF; this is the adapter.
- `MongoEFDiscriminator` — the driver's `IScalarDiscriminatorConvention` over EF's discriminator model, so the
  driver's LINQ provider can generate `{ _t: <value> }` filters.

### ChangeTracking

`ListOfValueTypesComparer<,>`, `ListOfNullableValueTypesComparer<,>`, `ListOfReferenceTypesComparer<,>` (the
last typed as `ValueComparer<object>`, since reference-element comparers can't be uniformly typed) — EF8-path
collection comparers. `StringDictionaryComparer<,>` for string-keyed dictionaries, in two variants: an EF8/EF9
legacy and an EF10 expression-based redesign, selected by `#if`. Plus `NullableStringDictionaryComparer<,>` and
`NullableEqualityComparer<T>` (EF8/EF9 helpers).

## Boundaries with adjacent areas

- **vs Storage/ValueConversion.** A `ValueConverter` is a pure model↔provider transform; a `ValueComparer` is
  equality + snapshot semantics; a `ValueConverterSerializer` runs the converter at BSON read/write time. The
  easy mistake is putting equality logic in a converter or conversion in a comparer — they aren't
  interchangeable.
- **vs Metadata.** Annotations are read here (`GetBsonRepresentation`, `GetBinaryVectorDataType`, discriminator
  config) and turned into serializer choices; never written.
- **vs Query.** Query asks the factory for serializers and `MongoEFDiscriminator` for polymorphic filters; it
  never instantiates serializers itself.
- **vs the driver's registry.** The factory *composes* driver serializers. It must **not** call
  `BsonSerializer.RegisterSerializer(...)` — a provider must not mutate global driver state.
  `BsonClassMap.LookupClassMap(type)` is fine (read-only).

## Common pitfalls

- **Nullable model types in `ValueConverterSerializer`.** The constructor rejects `TActual = Nullable<>`; the
  storage type must be wrapped in `NullableSerializer<>` *outside* the converter, not inside.
- **Only `string` dictionary keys are supported** — it's the only shape a BSON document takes. Non-string keys
  return `null` from the type-mapping path. Don't quietly extend this; the wire format constrains it.
- **Only rank-1 arrays** — the factory throws for higher ranks.
- **Entity serializers are cached per `IReadOnlyEntityType`.** Mutating annotations after one is built leaves it
  stale. Not reachable in normal use (the runtime model is immutable), but a trap in tests that build models on
  the fly.
- **Comparer signatures moved between EF8/EF9 and EF10** (`StringDictionaryComparer<,>` was reworked to be
  expression-based). New comparer work must compile on all three targets.
- **Don't invent parallel serializer types** for things the driver already covers — wrap and compose instead.

## How to test

- Unit: `…UnitTests/Serializers/` and `…UnitTests/ChangeTracking/`.
- Functional (round-trips, dictionary change tracking): `…FunctionalTests/Mapping/`, `…/Serialization/`.
- Cross-cutting: `…SpecificationTests/Query/` (e.g. `NorthwindChangeTrackingQueryMongoTest`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Serializ|FullyQualifiedName~ChangeTracking|FullyQualifiedName~Mapping"
```
