# Native "nav-null-check ternary" Select projections over join scopes — design

**Ticket:** EF-322 follow-up (working title: native join-scope conditional projection). Motivated by
`NorthwindMiscellaneousQueryMongoTest.Manual_expression_tree_typed_null_equality`, which today falls back to
driver-LINQ and throws `NativeTranslationNotSupportedException` under `MongoQueryMode.NativeOnly`.

## Problem

The EF Core spec test builds (by hand-crafted expression tree, though a normal C# ternary compiles to the
identical shape) a query equivalent to:

```csharp
Orders.Where(o => o.OrderID < 10300)
      .Select(o => o.Customer != null ? o.Customer.City : (string)null)
```

After nav-expansion this becomes a `LeftJoin` producing a `TransparentIdentifier(Outer, Inner)` scope, with a
trailing bare (non-wrapped) `Select` body:

```csharp
ti => ti.Inner != null ? ti.Inner.City : (string)null
```

Confirmed live (`MONGODB_EF_NATIVE_ONLY=1`) that this throws `NativeTranslationNotSupportedException: Query
projects a non-entity result`.

**This is not a typed-vs-literal-null issue.** `Expression.Constant(null, typeof(Customer))` and an ordinary
literal `null` produce byte-identical trees, and every null-recognition site in the translator (`TryMatchInnerNullCheck`'s
`ConstantExpression { Value: null }` pattern, `TranslateValue`'s generic `ConstantExpression` case) is agnostic
to `.Type`. The real gap is structural, and has three independent parts (traced by reading
`NativeJoinScopeProjectionBinder.cs`, `NativeJoinScopeTranslator.cs`, `MongoAggregationExpressionRenderer.cs`,
and `MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` directly):

1. **`NativeJoinScopeProjectionBinder.TryBindProjection` only handles a wrapped (`new {}`/`MemberInit`) selector
   body** — gated on `selector.Body.TryGetProjectionMembers(out members)`. A bare `ConditionalExpression` body
   has no projection members, so this binder is never reached for our shape; it falls through to the generic,
   join-scope-**unaware** `NativeProjectionBinder`, whose translator is rooted at the plain entity type and
   cannot resolve `ti.Outer`/`ti.Inner` at all.
2. **No existing recognizer builds a lookup-null-check for anything but the `Where`-position, depth-1-only
   shape.** `NativeSlotPopulator`'s `Where` arm (gated `mongoQ.Joins.Count == 1`) is the sole production call
   site of `MongoLookupNullCheckExpression`, via `NativeJoinScopeTranslator.TryMatchInnerNullCheck`, which
   matches the null operand structurally (`ReferenceEquals(member.Expression, rootParam)` + a bare `"Inner"`
   member name) — a flat, depth-1-only shape with no chain generalization.
3. **`MongoAggregationExpressionRenderer` has no case for `MongoLookupNullCheckExpression` at all.**
   `MongoLookupNullCheckExpression`'s only renderer today is `MongoQueryLanguageRenderer.RenderLookupNullCheck`
   — pure query-dialect (`$match`-body) output. The node's own doc-comment states this is deliberate: *"there
   is nothing else this node needs to express"* — true until a Select-position (`$expr`/`$project`) consumer
   exists. Dropped unchanged into `$project`, it hits `MongoAggregationExpressionRenderer`'s catch-all and
   throws `NativeTranslationNotSupportedException` at render time, not a decline at translate time.

## Why generalizing the null-check match is safe for chains (and why the existing one isn't reused as-is)

`NativeJoinScopeTranslator.TryMatchInnerNullCheck`'s `IsBareInnerAccess` resolves the null-checked operand by
`ReferenceEquals(member.Expression, rootParam)` plus a member-name check — safe only because it is Where-only
and restricted to depth 1 (the same class of flat, type/name-comparing resolution that `TryTranslateCore`'s own
"RESIDUAL GAP" remarks warn is unsafe once a chain is involved: a chained second join's own flat
`TransparentIdentifier` can coincidentally satisfy the same shape as the first join's).

`NativeJoinScopeTranslator.TryTranslateSingleScope` (added by the
`2026-09-18-native-chained-join-scalar-projection` design) already solves the general "resolve to exactly one
scope in a chain" problem the *safe* way — via `TryRerootToSingleScope`/`MongoTransparentScopeResolver`, which
walks the actual `"Outer"`/`"Inner"` member-name hop chain rather than comparing CLR types or using
`ReferenceEquals` against a single expected parameter shape. This design's null-check matcher reuses that same
safe mechanism instead of extending the hazardous flat one: an operand of the `==`/`!=` reroots (via
`TryRerootToSingleScope`) to a **bare** synthetic scope parameter (no further member access) at some
`scopeIndex`, and the other operand is `ConstantExpression { Value: null }`.

## Scope

**In:**

- A bare (non-wrapped) `Select` body that is exactly `Conditional(Test, IfTrue, IfFalse)`, where `Test` is a
  scope null-check (`ti.Inner != null` / `== null`, or the equivalent at any single level of a chain) and
  `IfTrue`/`IfFalse` each resolve to a single scope (root or any one join's Inner side) via the existing
  `TryTranslateSingleScope`. Depth 1 and any chain depth are both in scope — the null-check matcher and the
  branch translator are both already depth-agnostic building blocks (§ above); this design only wires them
  together for the new Select shape.
- Both operand orders of the null-check (`ti.Inner != null` and `null != ti.Inner`), both `==` and `!=`.
- Only a level whose join `IsLeftOuter == true` and whose navigation is not a collection is a *meaningful* null
  check (mirrors the existing `Where`-arm's `Navigation.IsCollection: false` conjunct — an inner `Join` never
  produces a null Inner after `$unwind(preserveNullAndEmptyArrays: false)`, and a collection nav is a different
  shape entirely). A chain with mixed `Join`/`LeftJoin` levels must apply this check **per level**, not once for
  the whole chain.

**Out (explicitly deferred, not silently unsupported):**

- A `Test` that isn't a single null-check against one scope level (e.g. comparing two different scopes, or a
  compound `&&`/`||` condition) — declines cleanly (falls back; throws only under `NativeOnly`).
- `IfTrue`/`IfFalse` bodies that are themselves wrapped (`new {}`) or span more than one scope — declines
  cleanly, same as the existing ordinary-leaf arm's `CrossScope` rejection.
- Any Select shape other than a single bare top-level `ConditionalExpression` (e.g. a wrapped leaf containing a
  nested conditional member) — out of scope for this design; the existing wrapped-leaf binder is unchanged.

## Design

### Component 1 — `NativeJoinScopeTranslator`: depth-agnostic null-check matcher

Add a new entry point alongside `TryMatchInnerNullCheck`, built on `TryRerootToSingleScope` rather than
`IsBareInnerAccess`:

```csharp
/// <summary>
/// Depth-agnostic generalization of <see cref="TryMatchInnerNullCheck"/>: recognizes
/// <c>rootParam.«Outer/Inner hop chain» == null</c> / <c>!= null</c> (either operand order) at the TOP of a
/// Select-side Conditional's Test, for ANY single scope level (never the root — only an Inner side can be
/// missing after a left-outer $lookup). Resolves the null-checked operand via
/// <see cref="TryRerootToSingleScope"/> (the same safe, member-name-chain-based mechanism
/// <see cref="TryTranslateSingleScope"/> uses), never by CLR-type or ReferenceEquals comparison — closing the
/// same RESIDUAL GAP <c>TryTranslateSingleScope</c> already closed for ordinary leaves. Structural recognition
/// only: callers must separately verify the resolved level's IsLeftOuter/non-collection eligibility.
/// </summary>
public static bool TryMatchScopeNullCheck(
    MongoJoinScope scope, ParameterExpression rootParam, Expression test,
    out int scopeIndex, out bool isNotNull)
{
    scopeIndex = -1;
    isNotNull = false;

    if (test is not BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } binary)
        return false;

    var leftIsBareScope = TryRerootToBareScope(scope, rootParam, binary.Left, out var leftIndex);
    var rightIsBareScope = TryRerootToBareScope(scope, rootParam, binary.Right, out var rightIndex);

    if (leftIsBareScope == rightIsBareScope)
        return false; // neither side, or both sides (self-compare) — decline alike

    var (matchedIndex, otherSide) = leftIsBareScope ? (leftIndex, binary.Right) : (rightIndex, binary.Left);

    if (matchedIndex == 0 || otherSide is not ConstantExpression { Value: null })
        return false;

    scopeIndex = matchedIndex;
    isNotNull = binary.NodeType == ExpressionType.NotEqual;
    return true;
}

// Reroots `node` via the existing TryRerootToSingleScope and additionally requires the rewritten
// expression to be the BARE synthetic scope parameter itself — no further member access — i.e. `node` was
// exactly `rootParam.Outer*.Inner?` with no trailing `.Something`.
private static bool TryRerootToBareScope(
    MongoJoinScope scope, ParameterExpression rootParam, Expression node, out int scopeIndex)
{
    scopeIndex = -1;
    if (!TryRerootToSingleScope(scope, rootParam, node, out var index, out var rewritten)
        || rewritten is not ParameterExpression)
    {
        return false;
    }

    scopeIndex = index;
    return true;
}
```

`TryRerootToSingleScope` is already `private static` in this file (extracted by the
`2026-09-18-native-chained-join-scalar-projection` design) — no signature change needed there.

### Component 2 — a new bare-Conditional recognizer

New method, e.g. `NativeJoinScopeProjectionBinder.TryBindConditionalProjection(mongoQ, selector, joinInfo)`,
called from `TranslateSelect` as a new sibling arm (see Component 3), following the file's existing
stage-then-commit-once pattern:

1. Early decline (mirrors `TryBindProjection`'s own guard): `mongoQ.Select.JoinScope is not {} scope`,
   `joinInfo.Lookup is null`, `mongoQ.Select.Projection.Count > 0`, `selector.Parameters.Count != 1`, or
   `selector.Body is not ConditionalExpression conditional`.
2. `NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, rootParam, conditional.Test, out var scopeIndex, out var isNotNull)`
   — decline if it fails.
3. Resolve `var level = scope.Levels[scopeIndex - 1]`; decline unless `level.IsLeftOuter && !level's navigation is a collection`
   (this eligibility is already recorded on `mongoQ.Joins[scopeIndex - 1]`, mirroring the existing `Where` arm's
   `Lookup: { Navigation.IsCollection: false }` conjunct — re-derive it from that join, not from `scope.Levels`
   alone, since `MongoJoinScopeLevel` may not itself carry the navigation).
4. Translate both branches: `NativeJoinScopeTranslator.TryTranslateSingleScope(scope, rootParam, conditional.IfTrue, valueMode: true, out var ifTrue)`
   and the same for `IfFalse` — decline (no partial commit) if either fails.
5. Stage `var testExpr = new MongoLookupNullCheckExpression(level.InnerPrefix, isNotNull);` and
   `var leaf = new MongoConditionalExpression(testExpr, ifTrue, ifFalse);` into a local — do not mutate `mongoQ`
   yet.
6. Commit: stage the single projection under the same synthetic bare-projection alias the generic (non-join)
   bare-computed-leaf path already uses (`NativeProjectionBinder`'s `_v` convention — reuse the same constant,
   don't restate it), call `mongoQ.Select.AddProjection(new MongoProjection(alias, leaf))`, then
   `ConfirmEntireChain(mongoQ, scope)` (already depth-agnostic and correct for both depth-1 and chains — no
   change needed there), and return `true`.

### Component 3 — `TranslateSelect`: new routing arm

Add a new `else if` sibling to the existing wrapped-projection arm (`TranslateSelect`, the block calling
`NativeJoinScopeProjectionBinder.TryBindProjection`), gated the same way
(`IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var joinInfo)`), calling
`NativeJoinScopeProjectionBinder.TryBindConditionalProjection` before falling through to the generic
`NativeProjectionBinder` path. On success, follow the same shaping convention the generic bare-`_v`-alias path
already uses for a non-join computed Select leaf (no new shaper logic — this is exactly the shape that path
already knows how to read back, just staged via a join-scope-aware translator instead of the plain one).

### Component 4 — `MongoAggregationExpressionRenderer`: render `MongoLookupNullCheckExpression`

Add the missing case:

```csharp
MongoLookupNullCheckExpression lookupNullCheck
    => new BsonDocument(lookupNullCheck.IsNotNull ? "$ne" : "$eq",
        new BsonArray { "$" + lookupNullCheck.LookupAlias, BsonNull.Value }),
```

placed beside the file's other `$eq`/`$ne`-against-a-field-path cases (match the existing field-path-rendering
convention there, e.g. however a plain `MongoFieldExpression` gets its `"$" + path` prefix, so this doesn't
invent a second convention). Also add the corresponding `CanRender` arm if any caller in this feature's path
consults it.

While touching this file, fix the stale doc-comment on `MongoLookupNullCheckExpression` (references a
nonexistent `TryTranslateReferenceIncludeNullCheck`; the actual producers are `TryMatchInnerNullCheck` and the
new `TryMatchScopeNullCheck`).

## Data flow (target query)

```
Where(o.OrderID < 10300)          → ordinary root-scope predicate, unaffected by this design
LeftJoin(Order ⋈ Customer)         → TranslateJoinCore: eligible → JoinScope = 1-level chain (unchanged)
[pending selector]
Select(ti => ti.Inner != null ? ti.Inner.City : (string)null):
    TranslateSelect → IsSingleEligibleNativeJoinScope (Levels.Count == Joins.Count == 1) →
    NativeJoinScopeProjectionBinder.TryBindConditionalProjection (NEW):
        - selector.Body is ConditionalExpression ✓
        - TryMatchScopeNullCheck(Test = "ti.Inner != null") → scopeIndex = 1, isNotNull = true
        - Joins[0].IsLeftOuter && !IsCollection ✓ (Customer is a reference nav)
        - TryTranslateSingleScope(IfTrue = "ti.Inner.City") → MongoFieldExpression(prefix + "City")
        - TryTranslateSingleScope(IfFalse = constant null) → MongoConstantExpression(null)
        - stage MongoConditionalExpression(MongoLookupNullCheckExpression(level.InnerPrefix, true), ..., ...)
          under "_v"; commit; ConfirmEntireChain (registers the $lookup, confirms the join)
Lower → MongoSelectLowerer: $match, $lookup, $unwind(preserveNullAndEmptyArrays: true), then
        $project: { _v: { $cond: { if: { $ne: ["$<prefix>", null] }, then: "$<prefix>.City", else: null } } }
```

For a chained scope, the identical flow applies at whichever `scopeIndex` the Test resolves to — each level
already has its own independent `$lookup`+`$unwind(preserveNullAndEmptyArrays: <that level's IsLeftOuter>)`
pair (`MongoSelectLowerer.AppendLookupStages` emits one pair per registered `LookupExpression`, confirmed by
direct inspection — nothing here needs a lowerer change), so the null-check and dereference at level *k* need
nothing extra from levels other than *k*.

## Durable invariants this design must not violate

(See `Query/AGENTS.md` for the full list; most load-bearing here:)

- **Scope resolution by parameter identity, never member name/CLR type.** `TryMatchScopeNullCheck` resolves via
  `TryRerootToSingleScope`, not `ReferenceEquals`/type comparison — no restated shortcut.
- **A recognizer must not mutate then decline.** Component 2 stages `testExpr`/`leaf` into locals and commits
  only after every step (test match, eligibility, both branches) succeeds — mirrors
  `NativeJoinScopeProjectionBinder`'s existing commit pattern exactly.
- **MQL shape cannot prove nativity.** Verification for this feature must run under `MONGODB_EF_NATIVE_ONLY=1`,
  not by inspecting the fallback's (possibly identical-looking) MQL baseline.
- **The RESIDUAL GAP this design must not reintroduce.** `TryMatchScopeNullCheck` must never fall back to
  `IsBareInnerAccess`-style type/`ReferenceEquals` matching for `scopeIndex > 0` in a chain — it exists
  specifically to avoid that.

## Testing

- Unit tests under `tests/.../Query/NativeTranslation/`: depth-1 `LeftJoin` with the null-check ternary (both
  `!=`/`==`, both operand orders); depth-2 chain with the null-check at level 1 and at level 2 independently; a
  plain (inner) `Join` at the checked level declining cleanly; a collection navigation declining cleanly; a
  cross-scope `Test`/branch declining cleanly; the new `MongoAggregationExpressionRenderer` case in isolation.
- Prove nativity via `MONGODB_EF_NATIVE_ONLY=1` on `Manual_expression_tree_typed_null_equality` (both EF10 and
  any EF8/EF9-reachable equivalent) and regenerate its baseline per the SpecificationTests `AGENTS.md`.
