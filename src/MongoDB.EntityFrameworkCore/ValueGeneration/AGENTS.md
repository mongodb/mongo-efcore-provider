---
area: Value generation
scope: ["src/MongoDB.EntityFrameworkCore/ValueGeneration/**"]
reviewer-agent: value-generation-reviewer
adjacent-areas: [Metadata, Storage]
---

# Value generation — AGENTS.md

## Scope

Client-side value generators for PK properties and owned-collection ordinal keys. Values are produced before
insert, so EF's change tracker sees concrete IDs ahead of `BulkWrite` — no server round-trip.

**In:** `MongoValueGeneratorSelector`, `ObjectIdValueGenerator`, `StringObjectIdValueGenerator`.
**Out:** whether a property gets generation at all (`MongoValueGenerationConvention`, in Metadata); where the
generator is invoked (EF's `ChangeTracker`).

## Key entry points

`MongoValueGeneratorSelector` extends EF's `ValueGeneratorSelector`. **Order of preference is load-bearing:**

1. Owned-collection ordinal keys (`property.IsOwnedTypeOrdinalKey()`) → EF's built-in int generator.
2. `ObjectId` properties → `ObjectIdValueGenerator`.
3. `string` stored as `ObjectId` → `StringObjectIdValueGenerator`.
4. `base.FindForType(...)` for everything else (`Guid`, sequential int, …).

Both generators call `ObjectId.GenerateNewId()` and set `GeneratesTemporaryValues = false` — the values are
permanent, not placeholders.

## Boundaries with adjacent areas

- **vs Metadata.** Metadata decides *whether* a property gets a generator; this area picks *which* one. A new
  annotation influencing selection goes in Metadata; the selector branch reading it goes here.
- **vs Storage.** Generators run before `MongoUpdate.CreateAll(...)`, so Storage only ever sees final values.

## Common pitfalls

- **Ordinal keys must be detected first.** Owned-collection element types have a synthesized `int Id`; if the
  `ObjectId` branch runs first it claims that key and inserts fail with a key conflict.
- **`base.FindForType` must stay last**, or EF's built-in selector grabs properties the provider specializes.
- **"String stored as ObjectId" needs BOTH checks** — a value converter on the property *and* the
  `BsonRepresentation` annotation. Either path can set up the mapping; checking one misses cases.
- **The selector runs on every insert.** No I/O beyond `ObjectId.GenerateNewId()`; adding async or network work
  here silently regresses insert throughput.

## How to test

- Unit: `tests/MongoDB.EntityFrameworkCore.UnitTests/ValueGeneration/`.
- Convention coverage: `…SpecificationTests/Metadata/Conventions/MongoValueGenerationConventionTests`.
- Functional: `…FunctionalTests/ValueGeneration/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ValueGeneration"
```
