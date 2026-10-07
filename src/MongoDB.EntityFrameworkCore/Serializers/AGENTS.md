---
area: Serialization & ChangeTracking
scope: ["src/MongoDB.EntityFrameworkCore/Serializers/**", "src/MongoDB.EntityFrameworkCore/ChangeTracking/**"]
reviewer-agent: serialization-reviewer
adjacent-areas: [Storage (ValueConversion), Metadata, Query, "C# driver BSON serializer registry"]
---

# Serialization & ChangeTracking — AGENTS.md

`Serializers/` bridges EF metadata to the driver's `IBsonSerializer<T>` (`BsonSerializerFactory` is the entry
point; `EntitySerializer<T>`, `ValueConverterSerializer<,>`, `MongoEFDiscriminator`). `ChangeTracking/` holds
`ValueComparer<T>`s for collections and string-keyed dictionaries where BCL defaults don't work. Value converters
themselves live in Storage/ValueConversion.

## Invariants

- **Query and others get serializers only from `BsonSerializerFactory`** (never instantiate). It composes driver
  serializers: never call `BsonSerializer.RegisterSerializer(...)` (no mutating global driver state);
  `BsonClassMap.LookupClassMap` (read-only) is fine. Don't invent serializers the driver already covers.
- **Converter vs comparer vs serializer**: a `ValueConverter` is a pure model↔provider transform, a `ValueComparer`
  is equality/snapshot semantics, `ValueConverterSerializer` runs the converter at BSON time. Don't mix them.
- **`ValueConverterSerializer` rejects `TActual = Nullable<>`** — wrap the storage type in `NullableSerializer<>`
  outside the converter.
- **`Mongo:BsonRepresentation` is applied via `ApplyBsonRepresentation`**, recursing through `INullableSerializer`.
- **Only string dictionary keys and rank-1 arrays** are supported (the factory throws otherwise).
- **Entity serializers are cached per `IReadOnlyEntityType`**; mutating annotations after one is built leaves it
  stale (a trap for tests that build models on the fly).
- **Comparer signatures differ between EF8/EF9 and EF10** (`StringDictionaryComparer` has `#if` variants); new
  comparer work must compile on all three.
- Serializers read annotations, never write them.

## Testing

`tests/…UnitTests/Serializers/`, `…UnitTests/ChangeTracking/`, `…FunctionalTests/Mapping/`, `…/Serialization/`,
`…SpecificationTests/Query/` (e.g. `NorthwindChangeTrackingQueryMongoTest`); filter
`FullyQualifiedName~Serializ|FullyQualifiedName~ChangeTracking|FullyQualifiedName~Mapping`.
