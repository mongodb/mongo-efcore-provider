---
area: Value generation
scope: ["src/MongoDB.EntityFrameworkCore/ValueGeneration/**"]
reviewer-agent: value-generation-reviewer
adjacent-areas: [Metadata, Storage]
---

# Value generation — AGENTS.md

## Scope

Client-side value generators for primary-key properties (and for owned-collection ordinal keys). The selector picks a generator based on property metadata; the generators produce fresh values before insert so EF Core's change tracker sees concrete IDs ahead of `BulkWrite` (no round-trip to the server).

In: `MongoValueGeneratorSelector`, `ObjectIdValueGenerator`, `StringObjectIdValueGenerator`.

Out: the convention that *decides* whether a property gets generation at all (`MongoValueGenerationConvention` in Metadata); the place where the generator is invoked (EF Core's `ChangeTracker`).

## Key entry points

- `MongoValueGeneratorSelector` extends EF Core's `ValueGeneratorSelector`. Order of preference:
  1. Owned-collection ordinal keys (`property.IsOwnedTypeOrdinalKey()` — the synthesized `Id` on owned-collection element types) → EF's built-in `TemporaryIntValueGenerator`-style generator.
  2. `ObjectId` properties → `ObjectIdValueGenerator`.
  3. `string` properties stored as `ObjectId` (detected by value-converter chain or `BsonRepresentation` annotation) → `StringObjectIdValueGenerator`.
  4. Fall through to `base.FindForType(...)` for everything else (e.g. `Guid`, sequential int).
- `ObjectIdValueGenerator` and `StringObjectIdValueGenerator` — both call `ObjectId.GenerateNewId()` (client-side, deterministic-ish, time-ordered) and set `GeneratesTemporaryValues = false` (values are permanent, not placeholders).

## Boundaries with adjacent areas

- **vs Metadata.** Metadata's `MongoValueGenerationConvention` decides *whether* a property gets a generator at all. This area picks *which* generator. New annotations that should influence selection (e.g. a hypothetical "use server-side `_id`") belong in Metadata; the new selector branch belongs here.
- **vs Storage.** Generators run before `MongoUpdate.CreateAll(...)` — they populate IDs while the entity is still tracked. Storage sees the final values and writes them.

## Common pitfalls

- **The selector runs on every insert.** No I/O, no synchronous calls beyond `ObjectId.GenerateNewId()`. Adding async work or network calls here will silently regress insert throughput.
- **Detecting "string stored as ObjectId" requires checking *both* a value converter on the property *and* `BsonRepresentation` annotation.** Either path can set up the mapping; missing one path misses the selection.
- **Ordinal keys must be detected first.** Owned-collection element types have a synthesized `Id` (`int`); if the ObjectId branch runs before the ordinal-key branch, the ordinal key gets the wrong generator and inserts fail with a key conflict.
- **`base.FindForType` last.** Calling it before custom branches lets EF's built-in selector grab properties the provider should specialize.

## How to test

- Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/ValueGeneration/` (`ObjectIdValueGeneratorTests`, `StringObjectIdValueGeneratorTests`).
- Convention coverage: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Metadata/Conventions/MongoValueGenerationConventionTests`.
- Functional: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/ValueGeneration/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~ValueGeneration"
```
