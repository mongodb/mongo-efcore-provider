---
area: Metadata, attributes & conventions
scope: ["src/MongoDB.EntityFrameworkCore/Metadata/**"]
reviewer-agent: metadata-reviewer
adjacent-areas: [Extensions, Storage, Query, Serializers, ValueGeneration]
---

# Metadata — AGENTS.md

## Scope

The model-building layer: the `Mongo:` annotation registry, the attributes that surface those annotations
declaratively, and the conventions that produce them automatically. Also `VectorIndexOptions` /
`VectorIndexBuilder` and `InternalIndexExtensions`.

**Out:** runtime *use* of annotations (Query/Storage/Serializers/ValueGeneration); the fluent-API entry points
that consume the getters/setters (those live in `Extensions/` and belong to the Public API area); the BSON
encoder itself (Serializers).

## Annotation registry

All annotations are prefixed `Mongo:` and live as constants on `MongoAnnotationNames` — *the* source of truth
for cross-area communication. When Query needs an element name it calls `property.GetElementName()`, which is
just `(string?)property[MongoAnnotationNames.ElementName]`.

| Annotation key | Target | Carries | Set via |
|---|---|---|---|
| `Mongo:CollectionName` | EntityType | `string?` | `[Collection]`, `.ToCollection(...)`, `[Table]`, DbSet-name convention |
| `Mongo:ElementName` | Property / owned EntityType | `string?` | `[BsonElement]`, `[Column]`, `.HasElementName(...)`, camelCase convention |
| `Mongo:DateTimeKind` | Property | `DateTimeKind` | `[BsonDateTimeOptions]`, `.HaveDateTimeKind(...)`, convention |
| `Mongo:BsonRepresentation` | Property | serialized `BsonRepresentationConfiguration` | `[BsonRepresentation]`, `.HasBsonRepresentation(...)` |
| `Mongo:CreateIndexOptions` | Index | `CreateIndexOptions?` | `.HasCreateIndexOptions(...)` |
| `Mongo:VectorIndexOptions` | Index | `VectorIndexOptions` | `.IsVectorIndex(...)` |
| `Mongo:BinaryVectorDataType` | Property | `BinaryVectorDataType?` | `[BinaryVector]`, `.HasBinaryVectorDataType(...)` |
| `Mongo:EncryptionDataKeyId` | Property / ForeignKey | `Guid?` | encryption builders |
| `Mongo:QueryableEncryptionType` | Property / ForeignKey | `QueryableEncryptionType?` | `.IsEncrypted*` builders |
| `Mongo:QueryableEncryption{RangeMin,RangeMax,Contention,TrimFactor,Precision,Sparsity}` | Property | per-type config | range/equality encryption builders |
| `Mongo:NotSupportedAttributes` | Property | `string[]` | the `Bson*` "not supported" conventions |

Non-exhaustive — `MongoAnnotationNames.cs` is authoritative.

## Convention pipeline

Assembled by `Conventions/MongoConventionSetBuilder.cs`. Buckets:

- **Type-attribute** — `CollectionAttributeConvention`, `TableAttributeConvention`, plus EF's own.
- **Property-attribute** (`Conventions/BsonAttributes/`) — `[BsonElement]`, `[BsonId]`, `[BsonIgnore]`,
  `[BsonRepresentation]`, `[BsonRequired]`, `[BsonDateTimeOptions]`, `[BinaryVector]`, `[Column]`.
- **Explicitly-unsupported-attribute** — recognize attributes the provider deliberately doesn't honor
  (`[BsonDefaultValue]`, `[BsonDictionaryOptions]`, `[BsonExtraElements]`, `[BsonGuidRepresentation]`,
  `[BsonIgnoreIfDefault]`, `[BsonIgnoreIfNull]`, `[BsonSerializationOptions]`, `[BsonSerializer]`,
  `[BsonTimeSpanOptions]`) and record them under `Mongo:NotSupportedAttributes` so the model validator can
  fail once, clearly.
- **Replacements of core EF conventions** — `PrimaryKeyDiscoveryConvention` (discovers the `_id`-mapped PK and
  synthesizes ordinal keys for owned-collection elements), `MongoRelationshipDiscoveryConvention` (defaults
  complex types to *owned* sub-documents rather than separate entity types), `MongoValueGenerationConvention`.
- **Model-finalizing** — `MongoDiscriminatorNamingConvention` (forces the `_t` element name; **load-bearing**,
  see the 8.4.0/9.1.0/10.0.0 entry in `BREAKING-CHANGES.md`) and `IndexNamingConvention`.
- **Optional plugins** — user-added, e.g. `CamelCaseElementNameConvention`, `DateTimeKindConvention`.

## Key entry points

- `MongoAnnotationNames` — the key registry. Never duplicate a key; never set an annotation by string literal
  from outside this file.
- `BsonRepresentationConfiguration` — `BsonType` + `AllowOverflow` + `AllowTruncation`, stored as a serialized
  dictionary so it round-trips through compiled models.
- `VectorIndexOptions` (record struct) + `VectorIndexBuilder` — fluent vector-index configuration.
- `InternalIndexExtensions` — `IIndex` → `CreateIndexModel<BsonDocument>` (or `CreateSearchIndexModel`);
  resolves element-name paths.
- `Conventions/MongoConventionSetBuilder` — the central registration point; adding a convention means editing
  this file.

## Boundaries with adjacent areas

- **vs Extensions / Public API.** The fluent methods live under `Extensions/` (they are the public surface) but
  read/write the annotations defined here. Adding an annotation is a Metadata change; surfacing it via a
  builder method is a Public API change. They usually land together but are reviewed separately.
- **vs ValueGeneration.** `MongoValueGenerationConvention` decides *that* a property gets a generator; the
  generators and the selector that picks them live in ValueGeneration.
- **vs Serializers.** Serializers consume metadata but never set it. A new BSON attribute that must influence
  serialization needs both a property-attribute convention (here) and serializer support (there).
- **vs Storage / Query.** Pure consumers, via the public extension getters — they don't depend on Metadata's
  internal shape.

## Common pitfalls

- **Annotation keys are serialized into compiled models.** Renaming a `Mongo:*` key breaks users with
  generated compiled models. Add new keys; don't rename.
- **Discriminator element name.** `MongoDiscriminatorNamingConvention` forces `_t` at model-finalizing unless
  the user explicitly set an element name. Don't reverse that precedence — explicit configuration must win.
  (Before 8.4.0/9.1.0/10.0.0, camelCasing could turn it into `T`.)
- **Configuration source precedence is Fluent > Data annotation > Convention.** Attribute conventions use
  `fromDataAnnotation: true`; convention-set values use `false`. Reversing it lets data annotations override
  fluent config — a silent regression.
- **Build vs. runtime model.** Annotations are mutable during model building (`IConventionModel`/
  `IMutableModel`) and immutable after (`IModel`/`IRuntimeModel`). Writes belong in conventions or builder
  extensions; Query/Storage/Serializers must read through the immutable interfaces.
- **Owned-entity ordinal keys.** Owned collections need a synthetic ordinal `Id` to distinguish array elements;
  `PrimaryKeyDiscoveryConvention` synthesizes it. A user who explicitly configures a PK on an owned-collection
  element type takes ownership of providing it.
- **Unsupported-attribute conventions are not no-ops** — they record the offense for `MongoModelValidator` to
  fail on. Removing one silently lets the attribute through.

## How to test

- Unit (convention logic): `…UnitTests/Metadata/Conventions/` (incl. `BsonAttributes/`).
- Spec: `…SpecificationTests/Metadata/`.
- Functional (end-to-end annotation effects): `…FunctionalTests/Metadata/` (incl. `Conventions/`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Metadata"
```
