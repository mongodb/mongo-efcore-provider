---
area: Metadata, attributes & conventions
scope: ["src/MongoDB.EntityFrameworkCore/Metadata/**"]
reviewer-agent: metadata-reviewer
adjacent-areas: [Extensions, Storage, Query, Serializers, ValueGeneration]
---

# Metadata — AGENTS.md

## Scope

The model-building layer: the `Mongo:` annotation registry, attributes that surface annotations declaratively,
and the conventions that produce them automatically. Also `VectorIndexOptions`/`VectorIndexBuilder` and
`InternalIndexExtensions`.

**Out:** runtime *use* of annotations (Query/Storage/Serializers/ValueGeneration); fluent-API entry points
(`Extensions/`, Public API area); BSON encoding (Serializers).

## Annotation registry

All keys prefixed `Mongo:`, defined on `MongoAnnotationNames` — the source of truth for cross-area
communication (e.g. `property.GetElementName()` reads `Mongo:ElementName`). `MongoAnnotationNames.cs` is
authoritative; non-exhaustive highlights: `CollectionName`, `ElementName`, `DateTimeKind`, `BsonRepresentation`,
`CreateIndexOptions`, `VectorIndexOptions`, `BinaryVectorDataType`, `EncryptionDataKeyId`,
`QueryableEncryptionType` (+ range/contention/precision/sparsity), `NotSupportedAttributes`.

## Convention pipeline

Assembled by `Conventions/MongoConventionSetBuilder.cs`:

- **Type/property-attribute** — `[Collection]`/`[Table]`, and `Conventions/BsonAttributes/` (`[BsonElement]`,
  `[BsonId]`, `[BsonIgnore]`, `[BsonRepresentation]`, `[BsonRequired]`, `[BsonDateTimeOptions]`,
  `[BinaryVector]`, `[Column]`).
- **Explicitly-unsupported-attribute** — recognizes attributes the provider deliberately ignores
  (`[BsonDefaultValue]`, `[BsonDictionaryOptions]`, `[BsonExtraElements]`, etc.), recording them under
  `Mongo:NotSupportedAttributes` so `MongoModelValidator` fails clearly.
- **Core-convention replacements** — `PrimaryKeyDiscoveryConvention` (finds `_id`, synthesizes ordinal keys for
  owned-collection elements), `MongoRelationshipDiscoveryConvention` (complex types default to owned
  sub-documents), `MongoValueGenerationConvention`.
- **Model-finalizing** — `MongoDiscriminatorNamingConvention` (forces `_t`; **load-bearing**, see
  `BREAKING-CHANGES.md` 8.4.0/9.1.0/10.0.0), `IndexNamingConvention`.
- **Optional plugins** — user-added, e.g. `CamelCaseElementNameConvention`, `DateTimeKindConvention`.

## Key entry points

- `MongoAnnotationNames` — never duplicate a key or set one by string literal outside this file.
- `BsonRepresentationConfiguration` — `BsonType` + `AllowOverflow` + `AllowTruncation`, serialized so it
  round-trips through compiled models.
- `VectorIndexOptions`/`VectorIndexBuilder` — fluent vector-index configuration.
- `InternalIndexExtensions` — `IIndex` → `CreateIndexModel<BsonDocument>` (or `CreateSearchIndexModel`).
- `Conventions/MongoConventionSetBuilder` — central registration point.

## Boundaries with adjacent areas

- **vs Extensions.** Annotation = Metadata change; the builder method surfacing it = Public API change —
  usually land together, reviewed separately.
- **vs ValueGeneration.** Metadata decides *that* a property gets a generator; ValueGeneration picks *which*.
- **vs Serializers.** Serializers read metadata, never set it; a new BSON attribute needs both a convention
  here and serializer support there.
- **vs Storage/Query.** Pure consumers via public extension getters.

## Common pitfalls

- **Annotation keys are serialized into compiled models** — add new keys, never rename.
- **Discriminator element name** — `MongoDiscriminatorNamingConvention` forces `_t` unless the user set one
  explicitly; explicit config must win.
- **Configuration source precedence is Fluent > Data annotation > Convention** — attribute conventions use
  `fromDataAnnotation: true`, convention-set values `false`. Reversing lets annotations override fluent config.
- **Build vs. runtime model** — annotations are mutable during building (`IConventionModel`/`IMutableModel`),
  immutable after (`IModel`/`IRuntimeModel`). Writes belong in conventions/builders only.
- **Owned-entity ordinal keys** — `PrimaryKeyDiscoveryConvention` synthesizes a synthetic ordinal `Id` for
  owned-collection elements; a user who configures a PK there takes over that responsibility.
- **Unsupported-attribute conventions record, not no-op** — removing one silently lets the attribute through.

## How to test

- Unit: `…UnitTests/Metadata/Conventions/` (incl. `BsonAttributes/`).
- Spec: `…SpecificationTests/Metadata/`.
- Functional: `…FunctionalTests/Metadata/` (incl. `Conventions/`).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Metadata"
```
