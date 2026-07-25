# Native arithmetic computed projections (EF-347 — general computed-projection slice 1)

## Summary

Make a terminal `Select` whose projection leaves are **numeric arithmetic** go native, emitting a
computed `$project` stage and materializing through the existing DOM shaper. Example:

```csharp
q.Select(p => new { Full = p.Price * p.Qty, Net = p.Gross - p.Tax })
// $project: { Full: { $multiply: ["$Price", "$Qty"] }, Net: { $subtract: ["$Gross", "$Tax"] }, _id: 0 }
```

Today these fall back to driver-LINQ. This is the first slice of general **computed-projection**
support — the explicitly-deferred "computed long tail" first flagged in the EF-331 (SP3) projection
pushdown sub-project, of which the EF-347 SelectMany *computed-leaf* tail item is a specialization
(delivered in the immediately-following slice, not here).

## Motivation

`NativeProjectionBinder.TryPopulateNativeProjection` (SP3) admits a terminal anonymous/DTO `Select`
only when **every leaf is a plain member access** (`TryTranslateField`) or a projected
collection-navigation `Count`. Any arithmetic in a projection leaf (`p.A * 2`, `p.Gross - p.Tax`)
makes the whole projection fall back to driver-LINQ. This slice lifts that one restriction for
numeric arithmetic, matching the arithmetic breadth the **predicate** side has had since EF-329.

## What already exists (why this slice is small)

Two of the three pieces are already built and tested:

1. **Emit side — DONE.** `MongoPipelineFactory.RenderProject` already renders each `MongoProjection`
   entry via `MongoAggregationExpressionRenderer.Render(projection.Expression, …)`, and that renderer
   already handles `$add`/`$subtract`/`$multiply`/`$divide`/`$mod` for a `MongoBinaryExpression`. An
   arithmetic projection leaf needs **no** emit-side change.

2. **Read-back side — DONE.** `MongoProjectionBindingRemovingExpressionVisitor` (the DOM projection
   shaper) already handles a **non-property** projection value: `TryResolveFieldAccess` returns a null
   `Property` for a computed `MongoBinaryExpression`, so it falls into the raw-element read
   (`BsonBinding.CreateGetElementValue(DocParameter, projection.Alias, projectionBindingExpression.Type)`)
   — "For non-property expressions (arithmetic, constants, Mql.Field) … read it raw by that alias."
   A computed value materializes through this existing branch, typed to the binding's CLR type.

3. **Missing piece — the translator has no value-expression entry point.** `MongoExpressionTranslator.TryTranslate`
   → `TranslateNode` only accepts **predicate/boolean/comparison** shapes; a bare value-typed
   `p.A * 2` hits the `default` case, `TryResolveMember` fails (it's a `BinaryExpression`, not a
   member), and it returns null. Arithmetic is reachable **only** as an *operand of a comparison*, via
   the `private TranslateOperand`. So `NativeProjectionBinder.TryTranslateLeaf` can never produce a
   computed leaf. This is exactly why computed projections were deferred.

## Approach

### 1. New value-expression entry point on the translator

Add a public `TryTranslateValue(Expression, out MongoExpression?)` that delegates to the **existing**
`TranslateOperand` machinery (making it reachable from this entry point). `TranslateOperand` already
implements precisely the rules this slice needs, and no new rules are added:

- member → `MongoFieldExpression`;
- constant/parameter → `MongoConstantExpression`/`MongoParameterExpression`;
- numeric `+ - * / %` (`MapArithmeticOperator` gated by `IsNumericType(node.Type)`) →
  `MongoBinaryExpression`, operands translated recursively;
- **string concatenation already excluded** — `"a" + b` compiles to `ExpressionType.Add` but
  `IsNumericType` is false, so it is not translated (falls back). This keeps the slice's scope to
  genuine arithmetic with no extra work, and is why `r.Tag + "!"`-style string-concat leaves stay
  deferred;
- **numeric casts already rejected** — a type-changing `Convert` operand returns null (the existing
  `TranslateOperand` remark: the driver's shape-dependent numeric-promotion rule is not reproduced),
  so `(double)p.IntA + p.Score` falls back.

`TryTranslateValue` is purely additive; `TryTranslate`, `TryTranslateField`, and `TranslateOperand`'s
existing behavior are byte-unchanged.

### 2. Wire it into the projection binder

In `NativeProjectionBinder.TryTranslateLeaf`, after the existing bare-`MemberExpression` field check
and the projected-collection-`Count` check both fail, call `translator.TryTranslateValue(leaf, out expr)`
and, on success (subject to the guards below), accept `expr` as the leaf's `MongoExpression`. Nothing
else in `TryPopulateNativeProjection` changes — the alias de-duplication, the `NewExpression`/
`MemberInitExpression` dispatch, the pending-lookup staging, and the `Route → NativeRoute.Projection`
resolution are all unchanged.

### 3. Correctness guards (the substance of the slice)

These prevent a native computed value from diverging from the driver-LINQ oracle:

- **Integer-division guard.** MongoDB `$divide` returns a floating result (`7 / 2 → 3.5`), but C#
  integer division truncates (`→ 3`, result type `int`). Reject a computed leaf whose **result CLR
  type is an integer type** *and* whose top operator is `Divide` — fall back gracefully (there is a
  driver-LINQ oracle). Kept native: `+ - * %` for all numeric types, and `/` for floating/decimal
  result types (where MongoDB `$divide` matches C# semantics). **The spike (Task 1) must confirm how
  the driver-LINQ oracle renders integer division**, to fix the exact guard boundary (e.g. whether
  `%` on integers, or division producing a nullable-integer result, also needs guarding).

- **Value-converter / non-default `BsonRepresentation` operand guard.** An operand property carrying a
  value converter or a non-default `BsonRepresentation` computes over the **stored** representation and
  can diverge from CLR arithmetic — reject any arithmetic leaf whose operands include such a property
  (reuse the existing `HasDefaultKeySerialization`-style check the GroupBy/Distinct key guards use).
  A plain member-access leaf is **not** affected — this guard applies only to the new computed path.

- **Nullable / overflow.** Nullable arithmetic (`int? A * 2`) and unchecked-overflow edges are noted as
  known interactions; where the spike shows the driver-LINQ oracle produces identical results, they go
  native, otherwise they are covered by graceful fallback. No bespoke handling beyond the two guards
  above unless the spike surfaces a concrete divergence.

## Behavior, oracle, and testing

- **Graceful-fallback shape — unlike the SelectMany no-oracle cases.** A plain-`Select` computed
  projection **has a driver-LINQ oracle** (the driver translates `Select(p => p.A * 2)`), so:
  - accepted shapes are proven native by succeeding under `MongoQueryMode.NativeOnly` **and** asserting
    `Native == DriverLinq` parity;
  - every guard-decline / out-of-scope leaf **declines to native and falls back to the driver-LINQ path**
    under `Native`, and throws only under `NativeOnly` — but for the integer-division guard specifically
    this is NOT "falls back with correct results" (see the corrected disposition immediately below); for
    every OTHER guard/out-of-scope leaf (string concat, converter/representation guard, the computed
    long tail) the driver-LINQ fallback genuinely does return correct results, matching the pre-existing
    behavior these leaves already had.
  - **Integer-division corrected disposition (live Task-4 finding, superseding an earlier wrong assumption
    in this doc's original draft):** an integer-result `Divide` is guarded out of native (above), but the
    driver-LINQ path is not a safe landing spot either — MongoDB's `$divide` is non-truncating (always
    returns a double) and the C# driver's own BSON deserializer rejects a non-exact-integer double when
    reading it back into an `int`/`long` result field. So for a NON-exact quotient (e.g. `7 / 2`), BOTH
    `Native` and `DriverLinq` throw `MongoDB.Bson.TruncationException` at materialization time (not just
    `NativeOnly`, which throws `NativeTranslationNotSupportedException` earlier, at translation time). An
    EXACT quotient (e.g. `10 / 2 == 5.0`) round-trips fine in every mode, since the double is integral.
    This is NOT the "graceful fallback with correct values" pattern the rest of this guard family has —
    it declines to native AND the driver-LINQ path itself can fail on the identical input.
- **Additive, not a break.** The shape fell back before; it goes native now (or still falls back for a
  guarded/out-of-scope leaf). Results are unchanged; the emitted MQL for a shape that was already native
  is untouched. Per the versioning rubric, neither the new native path nor the emitted MQL is a break.
- **Spec impact.** Northwind arithmetic-projection tests currently asserting a driver-LINQ shape should
  flip to native; the full 3-version `/test-all` plus the `NativeOnly` spec sweep confirm zero
  regressions.

## Scope boundary

**In scope (this slice):** numeric arithmetic (`+ - * %`, and `/` for floating/decimal) in a terminal
anonymous/DTO member-access `Select`. The primary target is the plain-`Select` path, but because the
change lives in the shared `NativeProjectionBinder` (used wherever a terminal projection is bound), a
set-op **trailing** projection (`Union(A,B).Select(i => new { X = i.V * 2 })`) and a set-op **operand**
projection (`A.Select(i => new { X = i.V * 2 }).Union(B…)`) with an arithmetic leaf now go native too,
incidentally — see the flipped `NativeSetOpsTests` cases. The `MongoProjectionBindingExpressionVisitor`
arithmetic case is gated on `Route == NativeRoute.Projection`, so a projection that is NOT bound native
(e.g. a mixed whole-entity + computed leaf) is unaffected and keeps its prior behavior.

**Deferred (unchanged):**
- **SelectMany computed-leaf** — reuses this slice's `TryTranslateValue`, but applied in the separate
  two-scope `NativeSelectManyBinder.TryBindTransparentIdentifierProjection`; delivered as the
  immediately-following slice to keep this one a single-mechanism squashed commit.
- Integer-result division (guarded out, above).
- The rest of the computed long tail: string concatenation and string methods (`ToUpper`/`Substring`/…),
  date-part extraction, `Math.*`, type-changing/numeric casts, unary negation.
- Bare-scalar projections (`Select(p => p.A * 2)` with no anonymous wrapper — SP3 never pushes a
  bare-scalar projection down; unchanged).

## Files (anticipated)

- `Query/NativeTranslation/MongoExpressionTranslator.cs` — new `TryTranslateValue` entry point
  delegating to the existing `TranslateOperand`; the two guards (integer-division, converter/representation).
- `Query/NativeTranslation/NativeProjectionBinder.cs` — call `TryTranslateValue` in `TryTranslateLeaf`.
- `Query/AGENTS.md` — as-built note; update the "computed leaves … still fall back" wording in the
  as-built scope paragraph.
- Tests: `UnitTests/Query/NativeTranslation/` (translator value entry + binder); `FunctionalTests/Query/`
  (end-to-end native + parity + NativeOnly + guard fallbacks); Northwind spec override(s) that flip.

## Open questions for the spike (Task 1)

1. Exactly how does the driver-LINQ oracle render **integer division** (and integer modulo)? Confirms
   the integer-division guard boundary.
2. Does the **forward** projection-binding visitor (`MongoProjectionBindingExpressionVisitor` /
   `ProjectionAnalyzer.CanPushDown`) route a computed arithmetic leaf (no `Convert`) to the native
   visitor, or pre-empt it to the LINQ-v3 push-down / mixed path? Confirms the read-back branch is
   actually reached for the native computed projection.
3. Confirm nullable-operand arithmetic (`int? * 2`, and a null field) produces identical results native
   vs. oracle, or is caught by graceful fallback.
