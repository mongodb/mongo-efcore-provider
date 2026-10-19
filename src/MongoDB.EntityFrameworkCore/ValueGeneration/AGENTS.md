---
area: Value generation
scope: ["src/MongoDB.EntityFrameworkCore/ValueGeneration/**"]
reviewer-agent: value-generation-reviewer
adjacent-areas: [Metadata, Storage]
---

# Value generation — AGENTS.md

## Scope

Client-side value generators for PK properties and owned-collection ordinal keys, produced before insert so
EF's change tracker sees concrete IDs ahead of `BulkWrite` — no server round-trip.

**In:** `MongoValueGeneratorSelector`, `ObjectIdValueGenerator`, `StringObjectIdValueGenerator`.
**Out:** whether a property gets generation at all (`MongoValueGenerationConvention`, in Metadata); where the
generator is invoked (EF's `ChangeTracker`).

## Key entry points

`MongoValueGeneratorSelector` extends EF's `ValueGeneratorSelector`. **Order of preference is load-bearing:**

1. Owned-collection ordinal keys (`IsOwnedTypeOrdinalKey()`) → EF's built-in int generator.
2. `ObjectId` properties → `ObjectIdValueGenerator`.
3. `string` stored as `ObjectId` → `StringObjectIdValueGenerator`.
4. `base.FindForType(...)` for everything else (`Guid`, sequential int, …).

Both generators call `ObjectId.GenerateNewId()` and set `GeneratesTemporaryValues = false` — values are
permanent.

## Boundaries with adjacent areas

- **vs Metadata.** Metadata decides *whether* a property gets a generator; this picks *which* one.
- **vs Storage.** Generators run before `MongoUpdate.CreateAll(...)`, so Storage only sees final values.

## Common pitfalls

- **Ordinal keys must be detected first** — if the `ObjectId` branch runs first it claims that key and inserts
  fail with a key conflict.
- **`base.FindForType` must stay last**, or EF's built-in selector grabs properties this provider specializes.
- **"String stored as ObjectId" needs both checks** — a value converter AND the `BsonRepresentation`
  annotation; checking only one misses cases.
- **The selector runs on every insert** — no I/O beyond `ObjectId.GenerateNewId()`.

## How to test

- Unit: `tests/MongoDB.EntityFrameworkCore.UnitTests/ValueGeneration/`.
- Convention coverage: `…SpecificationTests/Metadata/Conventions/MongoValueGenerationConventionTests`.
- Functional: `…FunctionalTests/ValueGeneration/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ValueGeneration"
```
