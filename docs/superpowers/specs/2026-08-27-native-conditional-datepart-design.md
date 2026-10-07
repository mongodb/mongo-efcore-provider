# Native conditional expressions and date-part translation — design

**Status:** Design approved, not yet implemented.
**Ticket:** none yet — to be filed when implementation starts (see EF-215 for the precedent of how the enum-cast slice of this same investigation was ticketed and committed).

## Summary

Add two independent native-translation capabilities to the MongoDB EF Core provider's query pipeline, neither of which exists today outside the driver-LINQ fallback bridge:

1. **Conditional (ternary) expressions** (`test ? ifTrue : ifFalse`) as a computed value — usable anywhere a computed leaf already is (projection, sort key, arithmetic operand).
2. **`DateTime`/`DateTimeOffset` member access and date-part extraction** (`.Value`, `.DateTime`, `.LocalDateTime`, `.UtcDateTime`, `.Date`, `.TimeOfDay`, `.Year`, `.Month`, `.Day`, `.Hour`, `.Minute`, `.Second`, `.Millisecond`, `.DayOfWeek`, `.DayOfYear`), for both plain `DateTime` properties and `DateTimeOffset` properties (which this provider stores as a subdocument with `DateTime`/`Offset` fields).

## Motivation

`BuiltInDataTypesMongoTest.Optional_datetime_reading_null_from_database` currently falls back to driver-LINQ under the default `MongoQueryMode.Native` and hard-fails under `MongoQueryMode.NativeOnly`:

```csharp
.Select(e => new { DT = e.DateTimeOffset == null ? (DateTime?)null : e.DateTimeOffset.Value.DateTime.Date })
```

Neither the conditional nor the date-part member chain has any native representation. This is a known, accepted gap — `Query/AGENTS.md` documents it explicitly: "the computed long tail — string transforms, date parts, `Math.*` — remains unsupported and falls back." Date-part handling exists today *only* in `MongoEFToLinqTranslatingExpressionVisitor`, the driver-LINQ bridge, added for EF-218 to work around a driver serializer limitation (CSHARP-5296) — that logic has no native-translator equivalent.

This design closes both gaps for the native translator. A Jira search found no existing ticket for either capability; the closest related tickets (EF-401 computed sort keys, EF-403 cast breadth) explicitly scope date parts *out*.

## Scope

**In:**
- General `ConditionalExpression` translation (any boolean `Test`, any computed-value `IfTrue`/`IfFalse`) — not narrowed to the null-check shape the motivating test happens to use.
- The full `DateTimeOffsetComponentMembers` set (mirrored from the driver bridge) for `DateTimeOffset`, plus the equivalent set for plain `DateTime` (which needs no offset reconstruction — MongoDB's native date operators apply directly to the stored UTC `BsonType.DateTime` value).
- Wiring both into the single existing choke point (`MongoExpressionTranslator.TryTranslateValue`), so they're automatically available wherever a computed value already is: projection leaves, computed sort keys, arithmetic operands.

**Out (explicitly deferred):**
- `DateOnly`/`TimeOnly` member access (not exercised by the motivating test or the current spec suite; a separate slice if demand appears).
- `Math.*` functions and string transforms — the other items named in the same `Query/AGENTS.md` "long tail" sentence. Unrelated capability, separate design.
- Widening `MongoExpressionTranslator`'s comparison/predicate machinery to accept a `ConditionalExpression`/date-part node as the *left-hand side of a query-dialect `$match` comparison* — these nodes have no query-dialect form (see "Durable invariants" below) and always render via `$expr`, exactly like `MongoConvertExpression` today. No new query-dialect work is needed or wanted.

## New IR node types (`Expressions/`)

Three new dialect-neutral node types, following the codebase's established "sealed sibling type, not a flag" convention (each needs distinct renderer handling, so each gets its own type):

- **`MongoConditionalExpression(MongoExpression Test, MongoExpression IfTrue, MongoExpression IfFalse)`** — a ternary. `Test` is always resolved and rendered as a boolean-typed *aggregation-expression* value, never the query/`$match` dialect (see "Dialect forcing" below).
- **`MongoDatePartExpression(MongoExpression Operand, MongoDatePart Part)`** — extracts one component (`MongoDatePart`: `Year`/`Month`/`Day`/`Hour`/`Minute`/`Second`/`Millisecond`/`DayOfWeek`/`DayOfYear`/`Date`/`TimeOfDay`) from a datetime-valued `Operand`.
- **`MongoDateTimeOffsetLocalExpression(MongoExpression Operand)`** — reconstructs the "local" `DateTime` from a stored `DateTimeOffset` operand (adds its stored UTC value and offset-minutes value together), mirroring `MongoEFToLinqTranslatingExpressionVisitor`'s existing driver-bridge reconstruction re-expressed as a native aggregation-expression node.

Two existing pieces of machinery are reused, not duplicated:
- **`.UtcDateTime`** needs no new node: it's the existing `MongoElementRefExpression` (already used by `GroupBy`/`Distinct` flattening) pointed at the operand's `.DateTime` subfield path — no reconstruction, since it's already UTC.
- **`.Value`** (on `Nullable<DateTime>`/`Nullable<DateTimeOffset>`) needs no new node: it's already peeled generically by `TryResolveMember`/`TryResolveFieldAccess` (EF-402).

## Translation (`MongoExpressionTranslator`)

**Date-part member chains.** A new resolver, structurally parallel to the existing `TryResolveOwnedFieldPath` walker, peels one member hop at a time off a `DateTime`/`DateTimeOffset`/`Nullable<...>`-typed receiver, working outside-in (recursing into the receiver first):

| Member | Receiver type | Translation |
|---|---|---|
| `.Value` | `Nullable<DateTime>`/`Nullable<DateTimeOffset>` | Peeled (existing, unchanged) |
| `.DateTime` / `.LocalDateTime` | `DateTimeOffset` | `MongoDateTimeOffsetLocalExpression(receiver)` |
| `.UtcDateTime` | `DateTimeOffset` | `MongoElementRefExpression(receiver's path + ".DateTime")` |
| `.Year`/`.Month`/`.Day`/`.Hour`/`.Minute`/`.Second`/`.Millisecond`/`.DayOfWeek`/`.DayOfYear`/`.Date`/`.TimeOfDay` | `DateTime` (bare, or already-reconstructed via the row above) | `MongoDatePartExpression(receiver, Part)` |

This plugs into `MongoExpressionTranslator.TryTranslateValue` as a new case, at the same tier as the existing arithmetic/cast cases.

**Conditional expressions.** `TryTranslateValue` gets a new `ConditionalExpression` case:
- `Test` is translated via the *existing* predicate translator (`TryTranslate` — the same one `Where` uses; `x == null` already works there per `Query/AGENTS.md`'s documented predicate breadth).
- `IfTrue`/`IfFalse` are each translated recursively via `TryTranslateValue`, so a branch may itself be a cast, arithmetic, nested conditional, or date-part expression. `(DateTime?)null` constant-folds to a plain `MongoConstantExpression`, needing no special case.

**Dialect forcing for `Test`.** A `$cond.if` lives inside `$project`'s expression context — same hard rule as `$expr` inside `$elemMatch`: `Test` must never render via the query (`$match`) dialect. Since `MongoAggregationExpressionRenderer` already renders boolean sub-expressions as values (comparisons, `$in`, `$not` — the EF-413/EF-396 work `Query/AGENTS.md` documents), `MongoConditionalExpression`'s renderer arm calls `MongoAggregationExpressionRenderer.Render` directly on `Test`, bypassing `MongoQueryLanguageRenderer.RenderNode`'s dual-dialect dispatch entirely. This is a rendering-site decision — no new translate-time restriction is needed.

## Rendering (`MongoAggregationExpressionRenderer`)

- `MongoConditionalExpression` → `{ $cond: { if: Render(Test), then: Render(IfTrue), else: Render(IfFalse) } }`.
- `MongoDateTimeOffsetLocalExpression` → `{ $dateAdd: { startDate: "<field>.DateTime", unit: "minute", amount: "<field>.Offset" } }`.
- `MongoDatePartExpression` → the matching operator per `Part`: `$year`/`$month`/`$dayOfMonth`/`$hour`/`$minute`/`$second`/`$millisecond`/`$dayOfWeek`/`$dayOfYear`, or `$dateTrunc: {unit: "day"}` for `.Date`, or a subtraction-from-day-truncation form for `.TimeOfDay`.

## Projection-binder wiring

- `NativeProjectionBinder.TryTranslateLeaf` gets a new arm admitting a top-node `ConditionalExpression` or date-part member chain, gated the same way the existing arithmetic/cast leaf arm is (calling into the same `TryTranslateValue` entry point — the gate is a thin admit-check on top of the shared translator, not a second copy of the acceptance logic, per the codebase's "gate must call the same structural predicate a companion rewrite relies on" invariant).
- `MongoProjectionBindingExpressionVisitor.Visit` gets one new case (mirroring the existing arithmetic/cast-leaf case) to register the *whole* `ConditionalExpression`/date-member node in `_projectionMapping`, gated on `Route == NativeRoute.Projection` exactly like its siblings — otherwise the default recursive walk would visit the `Test`/`IfTrue`/`IfFalse` children independently and silently produce a wrong shaper.
- **No read-side (`MongoProjectionBindingRemovingExpressionVisitor`) change is needed.** `$cond`/date-part results have no backing `IProperty`, so they fall into the existing generic raw-alias read path the arithmetic/cast leaf already uses.

## Risk areas to settle empirically during implementation

1. **Null handling in date operators.** MongoDB's `$dateAdd`/`$year`/etc. may hard-error rather than propagate null for a missing/null date input — the same class of issue the existing `$ifNull`-around-`$size` invariant documents for arrays (`Query/AGENTS.md`: "`$ifNull` around `$size`/`$filter` is mandatory, not defensive"). Measure actual server behavior for each operator during implementation and add `$ifNull` guards only where measurement shows they're needed — do not add them defensively everywhere without measuring, and do not assume the array-operator precedent transfers unchanged to date operators.
2. **`$elemMatch` exclusion.** Neither new node has a query-dialect form. `MongoQueryLanguageRenderer.IsQueryDialectRenderable` must keep rejecting them (true by default — they are simply absent from that classifier's admitted set, requiring no new code, only a regression test) and `MongoExpressionNegator` must decline (return `false`) rather than attempt to negate them. This is the same class of hazard the "`$expr` inside `$elemMatch` is a hard server error, not a slow path" invariant already warns about, and needs its own explicit test given this codebase's documented history of near-misses in this exact area.

## Testing plan

- **Unit tests** (`tests/.../UnitTests/Query/NativeTranslation/`): the date-member resolver and conditional translator in isolation, plus `NativeProjectionBinder` leaf-admission tests for both new shapes (mirroring `NativeProjectionBinderBareBodyTests`/`NativeProjectionBinderEnumCastTests`).
- **Functional, real-server, differential** (`tests/.../FunctionalTests/Query/`): `Native == DriverLinq == in-memory LINQ` oracle, following the established `NativeCastTests` pattern, across:
  - Positive and negative UTC offsets (offset reconstruction sign correctness).
  - Null vs. non-null branches of a conditional.
  - Each date part, including `.Date`/`.TimeOfDay` truncation boundaries.
- **The motivating spec test itself**: `BuiltInDataTypesMongoTest.Optional_datetime_reading_null_from_database` green under `MONGODB_EF_NATIVE_ONLY=1` on EF8/EF9/EF10.
- **Explicit non-goal regression tests**: a conditional/date-part expression reached from inside an `$elemMatch`-bound quantifier predicate still declines cleanly (not a crash) — per the "durable invariants" testing convention this codebase already follows for the negator/dialect-classifier pair.
- **Full three-EF-version regression**: `/test-all` (or the three individual `dotnet test` invocations) must stay green with zero `Passed → Failed` transitions, per this repo's standing convention for every native-translation change.

## Open questions for the implementer to resolve, not blocking design approval

- Exact `$dateTrunc`/subtraction form for `.TimeOfDay` (MongoDB has no single built-in "time of day" extractor — needs to be composed from `$dateTrunc` + a subtraction, or `$dateDiff`).
- Whether `MongoDatePart.Millisecond` needs any interaction with the existing "`DateTime`" subfield's own millisecond truncation note already on record in `MongoEFToLinqTranslatingExpressionVisitor` (sub-ms ticks are lost in the stored representation either way — confirm this doesn't change per-part behavior).
