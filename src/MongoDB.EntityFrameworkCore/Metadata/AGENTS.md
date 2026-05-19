---
area: Metadata, attributes & conventions
scope: ["src/MongoDB.EntityFrameworkCore/Metadata/**"]
reviewer-agent: metadata-reviewer
adjacent-areas: [Extensions, Storage, Query, Serializers, ValueGeneration]
---

# Metadata — AGENTS.md

## Scope

The model-building layer. Defines the MongoDB-specific annotation registry (`Mongo:` prefix), the attributes that surface those annotations declaratively, and the conventions that produce them automatically. Also home to `VectorIndexOptions` / `VectorIndexBuilder` and the internal index-conversion helpers.

In: `MongoAnnotationNames`, all `Mongo:*` annotation getters/setters surfaced via `Mongo*Extensions` in `Extensions/`, MongoDB-specific attributes (`[Collection]`, etc.), every convention under `Metadata/Conventions/`, `BsonRepresentationConfiguration`, `VectorIndexOptions` / `VectorIndexBuilder`, `InternalIndexExtensions`.

Out: runtime use of annotations (Query / Storage / Serializers / ValueGeneration), fluent-API entry points that *consume* the annotation getters/setters (those live in `Extensions/` and belong to the Public API area), the BSON encoder/decoder itself (Serializers area).

## Annotation registry

All annotations are prefixed `Mongo:` and live as constants on `MongoAnnotationNames`. They are *the* source-of-truth for cross-area communication: when Query needs an element name it calls `property.GetElementName()`, which is just `(string?)property[MongoAnnotationNames.ElementName]`.

Key annotations (non-exhaustive — defer to `MongoAnnotationNames.cs`):

| Annotation key | Target | Carries | Set via |
|---|---|---|---|
| `Mongo:CollectionName` | EntityType | `string?` | `[Collection]`, `.ToCollection(...)`, `[Table]`, DbSet-name convention |
| `Mongo:ElementName` | Property / EntityType (owned) | `string?` | `[BsonElement]`, `[Column]`, `.HasElementName(...)`, camelCase convention |
| `Mongo:DateTimeKind` | Property | `DateTimeKind` | `[BsonDateTimeOptions]`, `.HaveDateTimeKind(...)`, convention |
| `Mongo:BsonRepresentation` | Property | serialized `BsonRepresentationConfiguration` | `[BsonRepresentation]`, `.HasBsonRepresentation(...)` |
| `Mongo:CreateIndexOptions` | Index | `CreateIndexOptions?` | `.HasCreateIndexOptions(...)` |
| `Mongo:VectorIndexOptions` | Index | `VectorIndexOptions` | `.IsVectorIndex(...)` (with optional `VectorIndexBuilder` config) |
| `Mongo:BinaryVectorDataType` | Property | `BinaryVectorDataType?` | `[BinaryVector]`, `.HasBinaryVectorDataType(...)` |
| `Mongo:EncryptionDataKeyId` | Property / ForeignKey | `Guid?` | encryption builders |
| `Mongo:QueryableEncryptionType` | Property | `QueryableEncryptionType?` | `.IsEncrypted` / `.IsEncryptedForEquality` / `.IsEncryptedForRange` |
| `Mongo:QueryableEncryption{RangeMin,RangeMax,Contention,TrimFactor,Precision,Sparsity}` | Property | per-encryption-type config | range / equality encryption builders |
| `Mongo:NotSupportedAttributes` | Property | `string[]` (recorded by the unsupported-attribute conventions) | the `Bson*PropertyAttributeConvention` "not supported" set |

The exact list shifts; the constants in `MongoAnnotationNames.cs` are authoritative.

## Convention pipeline

The convention set is assembled by `Conventions/MongoConventionSetBuilder.cs`. Major buckets:

- **Type-attribute conventions** — apply attributes on the entity class. `CollectionAttributeConvention`, `TableAttributeConvention` (EF Core sugar), and the EF-Core base type-attribute conventions.
- **Property-attribute conventions** — recognize `[BsonElement]`, `[BsonId]`, `[BsonIgnore]`, `[BsonRepresentation]`, `[BsonRequired]`, `[BsonDateTimeOptions]`, `[BinaryVector]`, `[Column]`. Each lives in `Conventions/BsonAttributes/`.
- **Explicitly-unsupported-attribute conventions** — recognize attributes the provider deliberately doesn't honor (`[BsonDefaultValue]`, `[BsonDictionaryOptions]`, `[BsonExtraElements]`, `[BsonGuidRepresentation]`, `[BsonIgnoreIfDefault]`, `[BsonIgnoreIfNull]`, `[BsonSerializationOptions]`, `[BsonSerializer]`, `[BsonTimeSpanOptions]`) and record them under `Mongo:NotSupportedAttributes` so the model validator can fail later with a single clear error.
- **Replacements of core EF conventions:**
  - `PrimaryKeyDiscoveryConvention` replaces EF's `KeyDiscoveryConvention` — discovers `_id`-mapped PK, synthesizes ordinal keys for owned-collection elements.
  - `MongoRelationshipDiscoveryConvention` replaces EF's relationship discovery — defaults complex types to *owned* (sub-documents) rather than separate entity types.
  - `MongoValueGenerationConvention` replaces EF's value-generation convention.
- **Model-finalizing conventions** — run last:
  - `MongoDiscriminatorNamingConvention` enforces the `_t` discriminator element name by convention. **This is load-bearing** — see the 8.4.0/9.1.0/10.0.0 breaking change in `BREAKING-CHANGES.md`.
  - `IndexNamingConvention` names indexes per MongoDB convention.
- **Optional plugins** — user-added via `modelBuilder.UseCamelCaseElementNameConvention()` or similar: `CamelCaseElementNameConvention`, `DateTimeKindConvention`.

## Key entry points

- `MongoAnnotationNames` — annotation-key constants. Treat as a *registry*; never duplicate keys, never set annotations by string-literal key from outside this file.
- `BsonRepresentationConfiguration` — `BsonType` + `AllowOverflow` + `AllowTruncation`, stored under `Mongo:BsonRepresentation` as a serialized dictionary so it round-trips through compiled models.
- `VectorIndexOptions` (record struct) and `VectorIndexBuilder` — fluent vector-index configuration.
- `InternalIndexExtensions` — converts `IIndex` metadata into `CreateIndexModel<BsonDocument>` (or `CreateSearchIndexModel` for vector indexes); resolves element-name paths.
- `Conventions/MongoConventionSetBuilder` — central registration point. Adding a new convention means editing this file.
- `Conventions/BsonAttributes/*` — the per-`Bson*` attribute conventions.
- `Attributes/CollectionAttribute` — `[Collection("name")]`.

## Boundaries with adjacent areas

- **vs Extensions / Public API.** The fluent extension methods (`.ToCollection(...)`, `.HasElementName(...)`, `.IsVectorIndex(...)`, encryption builders) *live* under `Extensions/` — they are the public surface — but they read and write annotations defined here. Adding a new annotation is a Metadata change; surfacing it via a builder method is a Public API change. The two land together in practice but are reviewed by different reviewers.
- **vs ValueGeneration.** `MongoValueGenerationConvention` replaces EF's value-generation discovery and decides *that* a property gets a generator. The actual generator implementations (`ObjectIdValueGenerator`, `StringObjectIdValueGenerator`) and the selector that picks them live in ValueGeneration.
- **vs Serializers.** Serializers consume metadata (`GetBsonRepresentation`, `GetBinaryVectorDataType`, `GetElementName`, discriminator config) but never set it. New BSON attributes that need to influence serialization need both a property-attribute convention (here) and corresponding serializer support (Serializers area).
- **vs Storage / Query.** Pure consumers — both call `IReadOnlyProperty.GetElementName()` and friends. They don't depend on Metadata's internal shape, only on the public extension getters.

## Common pitfalls

- **Discriminator element name.** Pre-8.4.0/9.1.0/10.0.0, `CamelCaseElementNameConvention` could camelCase the discriminator and produce `T` instead of `_t`. `MongoDiscriminatorNamingConvention` runs at model-finalizing and forces `_t` unless the user explicitly set an element name (via `Property<string>("Discriminator").HasElementName(...)`). Don't reverse the precedence — explicit configuration must still win.
- **Build vs. runtime model.** Annotations are mutable during model building (`IConventionModel` / `IMutableModel`) and immutable after (`IModel` / `IRuntimeModel`). Code that writes annotations belongs in conventions or builder extensions; code in Query / Storage / Serializers must read through the immutable interfaces.
- **Owned-entity ordinal keys.** Owned collections need a synthetic ordinal `Id` to distinguish array elements. `PrimaryKeyDiscoveryConvention` synthesizes it; if a user explicitly configures a PK on an owned-collection element type, they take ownership of providing the ordinal.
- **Configuration source precedence.** Fluent API > Data annotation > Convention. `SetXxx` overloads with `fromDataAnnotation: true` are used by attribute conventions; convention-set values use `fromDataAnnotation: false`. Reversing this lets data annotations override fluent — silent regressions ensue.
- **Annotation keys are serialized into compiled models.** Renaming a `Mongo:*` key is a breaking change for users who have generated compiled models. Add new keys; don't rename.
- **Unsupported-attribute conventions are *not* no-ops.** They record the offense under `Mongo:NotSupportedAttributes`. `MongoModelValidator` reads that and fails. Removing one of these conventions silently lets the attribute through.

## How to test

- Unit tests (convention logic): `tests/MongoDB.EntityFrameworkCore.UnitTests/Metadata/Conventions/` (incl. `BsonAttributes/`).
- Spec tests: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Metadata/`.
- Functional tests (end-to-end annotation effects): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Metadata/` (incl. `Conventions/`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Metadata"
```
