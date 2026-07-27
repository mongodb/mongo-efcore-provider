# Native owned single-reference sub-property predicates & projections — design

*Epic EF-322 (native LINQ query provider). Owned-data translator slice, following the owned whole-entity slices.*
*Branch (planned) `EF-322-owned-ref-subproperty-native`, stacked on the native tip `275c90e` (`origin/NativeQueryOngoing`).*
*A JIRA number should be filed; this doc will be updated with it.*

---

## 1. Problem

A query that *reaches into* an owned single-reference navigation to filter or project a **sub-property** —
`ctx.People.Where(p => p.Address.City == "NYC")` or `ctx.People.Select(p => new { p.Address.City })`
where `Address` is `OwnsOne` — currently **falls back to driver-LINQ**, even under `Native` mode.

This is a distinct gap from the owned *whole-entity* slices (`690b487`, `275c90e`), which made the
owned document materialize natively but explicitly left "owned sub-property predicates/projections"
untouched (see the `690b487` commit message and
`docs/superpowers/specs/2026-07-26-native-owned-reference-whole-entity-design.md` §7).

**Root cause — one gate.** The native translator resolves every member access through a single method,
`MongoExpressionTranslator.TryResolveMember` (`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:515`):

```csharp
if (node is not MemberExpression { Expression: ParameterExpression param } me)
    return false;
...
var resolved = scopeType.FindProperty(me.Member.Name);   // single FindProperty, no chain descent
...
fieldPath = resolved.GetElementName();                   // one un-dotted element name
```

It accepts **only** a member rooted on a bare `ParameterExpression` (top-level `p.Foo`). A nested access
`p.Address.City` has `.Expression == (p.Address)` — itself a `MemberExpression` — so the pattern fails and
the whole predicate / projection leaf falls back. There is no dotted owned-path resolution and no
`$elemMatch`/array machinery anywhere in the native layer.

Because `TryResolveMember` is the **single shared gate** for predicates, sort keys, `Contains`/regex
operands, bare-bool access, field-to-field operands, **and** — via `TryTranslateField` — projection leaves
(`NativeProjectionBinder.TryTranslateLeaf`), *one* extension there lights up predicates **and** projections
at once, with no separate binder change.

**Key insight: the path-building infrastructure already exists.** Owned data is embedded, and the provider
already knows the dotted BSON path to any nested owned entity type:
- `IReadOnlyEntityType.GetDocumentPath()` (`MongoEntityTypeExtensions.cs:172`) returns the ordered list of
  containing element names from the document root down to a nested owned entity type — the exact prefix the
  shapers and pipeline use to read/write embedded data.
- `IReadOnlyProperty.GetElementName()` gives the leaf's element name within its declaring type.

So `p.Address.City` resolves to `string.Join(".", leaf.DeclaringEntityType.GetDocumentPath()) + "." +
leaf.GetElementName()` — e.g. `"Address.City"` — using the same helpers the rest of the provider uses, so
the emitted path is guaranteed to match stored layout.

## 2. Goal & success criteria

**Goal.** Make predicates and projections over **owned single-reference** sub-properties go native, at
arbitrary owned-reference nesting depth (`p.A.B.C`), emitting the index-usable dotted query dialect for
predicates and a dotted `$project` source for projections.

**In scope.**
- **Predicates** where the member is an owned single-ref dotted path: equality / `!= null` / `== null`,
  `Contains` (`$in`), `StartsWith`/`EndsWith`/`Contains` (`$regularExpression`), bare-bool
  (`p.Address.IsActive`), and field-to-field / arithmetic operands. All of these route through the shared
  operand machinery, so extending the one gate covers them together.
- **Projections** of the form `Select(p => new { p.Address.City })` / DTO — a dotted-path leaf inside a
  terminal anonymous/`MemberInit` projection, emitting `$project: { alias: "$Address.City" }`, read back by
  the existing DOM projection shaper by alias.
- Every intermediate navigation in the chain is an owned single-reference (`IsEmbedded() && !IsCollection`)
  rooted at the query parameter; the leaf is a scalar property.

**Success bar.**
- The shapes go native (succeed under `MongoQueryMode.NativeOnly`).
- **`Native` results equal `DriverLinq` results** (there is a driver-LINQ oracle for these shapes) across:
  present owned sub-doc, absent/null owned sub-doc (`p.Address.City == "x"` and `== null`), nested depth ≥ 2,
  bare-bool, `Contains`, and field-to-field predicates; and DTO projections (present + absent-owned-ref).
- No-tracking **and** tracked both correct.
- Zero regressions across EF8 / EF9 / EF10.

**This changes the native/streaming eligibility set** (new query shapes go native) — see §6.

## 3. Approach

**B — extracted owned-path resolver (recommended).** Keep the bare-parameter fast path in
`TryResolveMember` untouched; when it does not match, fall through to a new focused private helper
`TryResolveOwnedFieldPath(MemberExpression, out IProperty, out string fieldPath)` that:
1. walks the member chain from the outer member inward to the root, collecting member names;
2. confirms the root is the (single-scope) query parameter;
3. for each non-leaf member, resolves an owned single-reference navigation
   (`FindNavigation(name)` with `IsEmbedded() && !IsCollection`) and advances the scope entity type to its
   target; declines otherwise;
4. resolves the leaf `IProperty` on the final scope type;
5. builds `fieldPath` from `leaf.DeclaringEntityType.GetDocumentPath()` + `leaf.GetElementName()`.

Isolated, unit-testable, and easy to scope-gate. Matches the codebase's preference for focused units.

**A — inline extension.** Same behavior, but the walk is inlined into `TryResolveMember`'s pattern. Rejected:
clutters the hot path and is harder to scope-gate and unit-test in isolation.

**C — rewrite `p.Address.City` at the binder level** before it reaches the translator. Rejected: spreads
logic across binder and translator and bypasses the single shared gate that makes predicates and projections
light up together.

## 4. Guards (correctness — mirroring existing native slices)

- **Single-scope only.** Engage the owned walk only when `_outerParam is null` and `_innerPrefix is null`
  (i.e. **not** inside a two-scope `SelectMany` unwind). Nested owned access inside a two-scope scope stays
  fallback — deferred (§7), and declining is safe (the shape simply falls back as today). This avoids any
  interaction between `GetDocumentPath()` and the unwind-scope prefixing that could silently mis-address a
  field.
- **Composite-PK leaf.** Keep the existing rejection of a composite-primary-key component (stored under
  `_id.<name>`, not addressable by top-level element name).
- **Converter / non-default `BsonRepresentation` — the asymmetry that must be verified, not assumed.**
  - *Predicates* serialize the compared value **through the leaf property's serializer**
    (`TranslateValue(..., property)`), so a value converter / non-default representation is applied on the
    query side and results match driver-LINQ — no extra guard needed **(to be confirmed by parity test in
    the spike, §5).**
  - *Projections* read the field back **raw** via the DOM shaper by alias, so a converted / non-default
    leaf would diverge. If the existing plain-member projection path does not already guard this, the
    projection leaf must reject a non-default-serialized owned leaf — reusing
    `NativeGroupByBinder.HasDefaultKeySerialization` exactly as the arithmetic / GroupBy / Distinct / OfType
    slices do. The spike settles whether the guard is already covered upstream or must be added here.
- **Naturally excluded (no code needed).** A cross-collection reference intermediate fails `IsEmbedded()`;
  an owned *collection* intermediate is not a plain member chain (`Any`/`Select`), so it never reaches this
  helper.

## 5. Slice 0 — throwaway de-risking spike

A throwaway branch, discarded after a written findings doc. Per the project's spike-first practice for
silent-wrong-data risk, it must settle:

- **Predicate parity with converters/representation:** does `Where(p => p.Address.<convertedProp> == v)` go
  native and return data equal to driver-LINQ **without** a converter guard on the predicate side? (Confirm
  the "serialize through the property serializer" assumption end-to-end.)
- **Projection converter behavior:** does the existing plain-member `$project` path already reject or
  correctly handle a converted / non-default-`BsonRepresentation` leaf, or must the owned projection leaf
  add the `HasDefaultKeySerialization` guard? Determine the narrowest correct placement.
- **DOM shaper reads the dotted alias:** confirm `$project: { alias: "$Address.City" }` materializes
  correctly through `MongoProjectionBindingRemovingExpressionVisitor` (alias is top-level in the projected
  doc, so expected to just work) — including the absent-owned-ref case (`Address` missing → leaf null).
- **Blast radius:** enumerate the spec/functional tests that flip from asserting fallback to asserting
  native for owned sub-property predicates/projections.

**Gate:** if the converter/representation asymmetry cannot be made safe with the guards in §4, narrow the
slice (e.g. predicates only, or default-serialized leaves only) and re-scope.

## 6. This changes the eligibility set — handling the flips

This makes **new query shapes native**, so the `NativeOnly` spec pass-set **will change** (owned
sub-property predicate/projection shapes move fallback→native; pass count rises, fallback count drops). Per
the provider's versioning rubric this is **not a breaking change** — a hard-throw/graceful-fallback shape
becoming native with unchanged results, and the emitted MQL, are explicitly non-contract. It must still be
handled deliberately:
- Every flipped test is updated to assert the new native behavior **and** verified to return correct data
  (the `Native↔DriverLinq` oracle makes this checkable).
- The `NativeOnly` sweep is re-baselined (record the new pass/fail/skip; confirm the delta is exactly the
  owned sub-property shapes, nothing unexpected).

## 7. Non-goals (deferred — separate slices)

- **Owned-collection sub-property predicates** (`Where(p => p.Addresses.Any(a => a.City == …))`) — needs
  `$elemMatch`/array machinery that does not exist yet.
- **Owned-collection sub-property projections** (`Select(p => p.Addresses.Select(a => a.City))`,
  `Select(p => p.Addresses.Count)` over an *embedded* collection).
- **Bare-scalar projection** `Select(p => p.Address.City)` — a bare scalar never enters the native
  projection binder regardless of owned-ness (same pre-existing limitation as `Select(p => p.Name)`);
  out of scope, unchanged.
- **Two-scope (SelectMany-unwind) nested owned access** — declined by the single-scope guard (§4).
- **Owned-reference sub-property in a sort key beyond what the shared gate gives for free** — `OrderBy`
  routes through the same `TryTranslateField`, so it comes along automatically; no dedicated work, but only
  the single-scope shapes are claimed.

## 8. Testing & verification

- **Parity + edge cases:** `Native == DriverLinq` for owned single-ref sub-property predicates (equality,
  `== null` / `!= null`, `Contains`, `StartsWith`/`EndsWith`, bare-bool, field-to-field) and DTO
  projections, across present / absent-owned-ref / explicit-null / nested-depth-≥2; no-track and tracked.
- **Proves native:** `NativeOnly` succeeds for each shape (routing proof).
- **Unit tests** for `TryResolveOwnedFieldPath`: correct dotted-path resolution, nested depth, decline on
  non-embedded / collection / cross-scope / composite-PK, and (if added) the converter/representation guard.
- **Flips handled:** updated tests assert native with verified-correct data; the `NativeOnly` spec sweep
  re-baselined with the delta explained.
- **Full `/test-all` EF8/EF9/EF10 green** (foreground, per-version isolated testcontainers).
- All touched types `internal`; `#if`-clean across EF8/EF9/EF10; not a break (fallback→native, results
  unchanged).

## 9. Open questions (resolved by the spike)

- Whether the predicate side needs a converter/representation guard, or the property-serializer path already
  makes it correct.
- Whether the existing plain-member projection path already guards converters/representation, or the owned
  projection leaf must add the `HasDefaultKeySerialization` guard, and the narrowest placement.
- Whether `GetDocumentPath()` + `GetElementName()` is the right canonical path source in every owned-nesting
  configuration (default element name, `HasElementName`-overridden, shared-type owned), confirmed against the
  driver-LINQ oracle.
