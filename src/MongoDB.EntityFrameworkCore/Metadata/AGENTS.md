---
area: Metadata, attributes & conventions
scope: ["src/MongoDB.EntityFrameworkCore/Metadata/**"]
reviewer-agent: metadata-reviewer
adjacent-areas: [Extensions, Storage, Query, Serializers, ValueGeneration]
---

# Metadata — AGENTS.md

The `Mongo:` annotation registry (`MongoAnnotationNames`), attribute conventions, and the conventions that produce
annotations (assembled in `Conventions/MongoConventionSetBuilder.cs`). Also `VectorIndexOptions`/`VectorIndexBuilder`,
`BsonRepresentationConfiguration` and `InternalIndexExtensions` (`IIndex` → `CreateIndexModel`/`CreateSearchIndexModel`).
Runtime *use* of annotations belongs to Query/Storage/Serializers/ValueGeneration; fluent entry points to `Extensions/`.

## Invariants

- **Annotation keys are serialized into compiled models** — add new keys, never rename or duplicate; never use a
  `Mongo:` string literal outside `MongoAnnotationNames`.
- **Configuration source precedence is Fluent > Data annotation > Convention.** Attribute conventions pass
  `fromDataAnnotation: true`, convention-set values `false`; reversing lets annotations override fluent config.
- **Discriminator element name**: `MongoDiscriminatorNamingConvention` forces `_t` unless the user set one
  explicitly (explicit must win). It is load-bearing for stored data (`BREAKING-CHANGES.md`).
- **Unsupported-attribute conventions record, not no-op** (`[BsonDefaultValue]`, `[BsonDictionaryOptions]`,
  `[BsonExtraElements]`, ... go under `Mongo:NotSupportedAttributes` so `MongoModelValidator` fails clearly).
  Removing one silently lets the attribute through.
- **Owned-collection ordinal keys**: `PrimaryKeyDiscoveryConvention` synthesizes an ordinal `Id`; a user who
  configures a PK there takes over that responsibility.
- **Write annotations only in conventions/builders** (mutable during model building, immutable on `IModel`/
  `IRuntimeModel`).
- Serializers read metadata, never set it. A new BSON attribute needs a convention here and serializer support.
- Adding an annotation usually adds a builder method in `Extensions/`; they land together, reviewed separately.

## Testing

`tests/…UnitTests/Metadata/Conventions/` (incl. `BsonAttributes/`), `…SpecificationTests/Metadata/`,
`…FunctionalTests/Metadata/`; filter `FullyQualifiedName~Metadata`.
