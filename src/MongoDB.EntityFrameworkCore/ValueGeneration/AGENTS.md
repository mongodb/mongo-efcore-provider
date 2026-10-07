---
area: Value generation
scope: ["src/MongoDB.EntityFrameworkCore/ValueGeneration/**"]
reviewer-agent: value-generation-reviewer
adjacent-areas: [Metadata, Storage]
---

# Value generation — AGENTS.md

Client-side generators (`MongoValueGeneratorSelector`, `ObjectIdValueGenerator`, `StringObjectIdValueGenerator`)
so the change tracker has concrete IDs before `BulkWrite`. Whether a property gets generation at all is decided by
`MongoValueGenerationConvention` (Metadata); this area only picks *which* generator.

## Invariants

`MongoValueGeneratorSelector` order is load-bearing:

1. Owned-collection ordinal keys (`IsOwnedTypeOrdinalKey()`) → EF's int generator. Must be first, or the
   `ObjectId` branch claims the key and inserts fail with a key conflict.
2. `ObjectId` → `ObjectIdValueGenerator`.
3. `string` stored as `ObjectId` → `StringObjectIdValueGenerator`. Requires both a value converter AND the
   `BsonRepresentation` annotation; checking one misses cases.
4. `base.FindForType(...)` last, or EF's selector grabs types this provider specializes.

- Generated values are permanent (`GeneratesTemporaryValues = false`).
- The selector runs on every insert: no I/O beyond `ObjectId.GenerateNewId()`.

## Testing

`tests/…UnitTests/ValueGeneration/`, `…SpecificationTests/Metadata/Conventions/MongoValueGenerationConventionTests`,
`…FunctionalTests/ValueGeneration/`; filter `FullyQualifiedName~ValueGeneration`.
