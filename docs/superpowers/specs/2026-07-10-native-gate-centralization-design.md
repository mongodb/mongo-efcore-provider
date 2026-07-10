# Native query: centralize the is-native gate decision (EF-334)

**Ticket:** EF-334 — "Native query: centralize the is-native gate decision into a single predicate."
**Epic:** EF-322 (native LINQ query provider).
**Type:** Internal refactor. **No behavior change intended** (byte-identical emitted MQL and identical throw/fallback behavior).
**Stacked on:** EF-347 (`5d53633`), the current native-stack tip on `origin/NativeQueryOngoing`.

## Background

EF-332 collapsed the old mutable `IsNativeRepresentable` flag into a single computed
`MongoSelectDefinition.Route` (`NativeRoute { Fallback, WholeEntity, Projection, ScalarAggregate, GroupBy }`),
with the stated intent of "one authoritative is-native decision." In practice the decision is **not**
fully centralized: `MongoShapedQueryCompilingExpressionVisitor` reads `Route` **and** two side-channel
signals whose state does not live on `MongoSelectDefinition`, plus a third that post-dates the ticket:

1. **Vector search** — `ContainsVectorSearch(mongoQueryExpression.CapturedExpression)` scans the captured
   method chain. The `VectorSearch(...)` call is lifted out of the tree by `MongoQueryTranslationPreprocessor`
   and sits at the root, so it never reaches `NativeSlotPopulator`'s catch-all — hence the separate scan.
2. **GroupBy + Join hard-decline** — `MongoSelectDefinition.IsGroupByFallbackUnsafe`, checked directly at the
   top of `VisitShapedQuery`. Added in EF-344, after EF-334 was filed. A GroupBy combined with a Join cannot
   go native **and** its driver-LINQ fallback returns silently wrong data, so it must throw under both
   `Native` and `NativeOnly` (only explicit `DriverLinq` runs it).
3. **`$lookup` streamability** — `AllPendingLookupsAreStreamable(mongoQueryExpression)`.

The reader must currently know to consult all of these; the "single `Route` predicate" abstraction leaks,
and the leak widens as each native sub-project adds shapes.

### Two findings that refine EF-334's own premise

- **`AllPendingLookupsAreStreamable` is NOT an is-native signal.** The EF-334 ticket lists it as one of the
  two is-native side-channels. In the code it is used only at `CompileShapedQuery` (the `streaming` local) to
  decide **streaming-vs-DOM** for a pipeline that is *already* native — it does not decide native-vs-driver.
  A native **collection** Include deliberately goes native-DOM with this returning `false`. It is a **separate
  axis** and is therefore **out of scope** for this centralization: the is-native disposition and the
  streaming-eligibility predicate stay distinct.
- **There are three real is-native signals, not two.** `Route`, vector-search, and the GroupBy+Join
  hard-decline. The GroupBy-unsafe signal post-dates the ticket.

## Goal

Introduce a single classification computed in one place that every gate site consults for the
**is-native disposition**. Leave `Route` as-is (it remains "which native shape / slot representability"),
leave the streaming-eligibility predicate as-is (different axis), and change no emitted MQL or
throw/fallback behavior.

## Non-goals

- No change to `Route`'s meaning, values, or population.
- No relocation of `$lookup` state off `MongoQueryExpression` (that is EF-317, a driver-`LeftJoin`-gated
  removal — explicitly deferred; see "Relationship to EF-317").
- No change to the streaming-vs-DOM gate (`AllPendingLookupsAreStreamable`) beyond a doc clarification.
- No change to which native *builder* handles a given `Route` value (Projection / GroupBy / ScalarAggregate /
  WholeEntity routing is untouched).

## Design

### The abstraction

A private classification on `MongoShapedQueryCompilingExpressionVisitor`:

```csharp
private enum NativeDisposition
{
    Native,       // build a native pipeline (via the Route-appropriate builder)
    Fallback,     // not natively representable -> driver-LINQ; throw ONLY under NativeOnly
    HardDecline   // must throw under Native AND NativeOnly (driver-LINQ fallback is wrong-data)
}

private NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)
{
    if (mode != MongoQueryMode.DriverLinq && q.Select.IsGroupByFallbackUnsafe)
    {
        return NativeDisposition.HardDecline;
    }

    if (q.Select.Route == NativeRoute.Fallback || ContainsVectorSearch(q.CapturedExpression))
    {
        return NativeDisposition.Fallback;
    }

    return NativeDisposition.Native;
}
```

The three real is-native signals are read in exactly one place: `Route` and `IsGroupByFallbackUnsafe`
(both on `MongoSelectDefinition`) and `ContainsVectorSearch(CapturedExpression)` (the captured chain — the
one signal that genuinely cannot live on the Select, because the `VectorSearch` call is lifted out before
the Select is built).

Why a 3-way classification rather than a `bool`: the gate has three distinct outcomes today — build native,
graceful fallback (throw only under `NativeOnly`), and hard decline (throw under `Native` too). A boolean
cannot express the hard-decline outcome, which is precisely the signal EF-344 added out-of-band.

`Route` is intentionally **not** the single source of truth after this change — `ClassifyNativeDisposition`
is. This is the honest model: `Route` answers "which native shape is this / is it slot-representable,"
and the disposition is the superset that layers on the two signals that are not about slot
representability (a lifted-out vector search; a wrong-data join decline).

### Call-site mapping

The refactor routes each existing decision through `ClassifyNativeDisposition`, preserving each site's
**exact current effective condition**. It does not add, remove, or broaden any condition.

| Site (current line) | Today | After |
|---|---|---|
| `VisitShapedQuery` (~145) | `mode != DriverLinq && Select.IsGroupByFallbackUnsafe` → `throw NativeTranslationNotSupportedException` | `ClassifyNativeDisposition(...) == HardDecline` → same throw |
| `TryBuildNativeFactory` (~487) | `Route is Fallback or ScalarAggregate \|\| ContainsVectorSearch(...)` → `ThrowIfNativeOnlyForbidsFallback`; `return null` (graceful) | `disposition != Native \|\| Route == ScalarAggregate` → same graceful fallback |
| `VisitProjectedQuery` (~184) | `Route == Fallback && ShaperExpression is GroupByShaperExpression` → `ThrowIfNativeOnlyForbidsFallback` | unchanged — a *which-native-shape* coverage decision, not the disposition |
| `VisitProjectedQuery` (~258) | `ThrowIfNativeOnlyForbidsFallback("...non-entity result")` | unchanged — reached only after the `Route`-value native routes decline; coverage decision |

**The `ScalarAggregate` nuance is preserved locally.** `ScalarAggregate` is *native* at the query level
(built by `TryBuildAggregateFactory`), but `TryBuildNativeFactory` — the whole-entity/reducer pipeline
builder — must still decline it so control falls through to the aggregate factory. So that site's condition
becomes `disposition != Native || Route == ScalarAggregate`, keeping the "handled by the other factory"
carve-out exactly where it is today rather than pushing it into the shared classification.

**The which-native-builder routing is untouched.** The `Route == GroupBy` / `Route == Projection` /
`Route == ScalarAggregate` switches in `VisitProjectedQuery` remain; they only ever run when the disposition
is `Native`, and they select the builder, not native-vs-driver.

### The correctness-critical invariant

The three is-native signals have **different reach per site** today (most importantly,
`ContainsVectorSearch` is checked *only* inside `TryBuildNativeFactory`). The refactor must **not** broaden a
signal to a site that did not previously check it — doing so would silently change fallback/throw behavior.
The mapping above is deliberately conservative: only the two sites that already combine these signals adopt
the classification; the projected-path coverage throws (which never consulted vector-search or GroupBy-unsafe)
stay as-is. This is the single most important property to verify (see Verification).

## Relationship to EF-317

EF-334's original text says "coordinate with EF-317 (which owns the `$lookup`-state placement)." EF-317 as
filed is **not** a prep refactor — it is "use native driver `LeftJoin` to replace the cross-collection
`$lookup` workaround," a large removal **gated on the C# driver shipping native `LeftJoin` support**. It is
therefore explicitly **out of scope** here and this centralization does not touch `$lookup` state. Because
lookup-streamability turned out to be a streaming-axis signal (not an is-native signal), this centralization
has **no dependency on EF-317** — they are cleanly decoupled.

## Files touched

- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — add
  `NativeDisposition` + `ClassifyNativeDisposition`; route the two combining sites through it. Single
  production file.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — correct the side-channel wording (lookup-streamability
  is the streaming axis, not is-native), document the 3-way disposition behind one method, and that
  `Route` = representability / disposition = native-eligibility.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` — unit tests for the classification.

## Verification

Same bar as the EF-330 and EF-332 behavior-preserving refactors:

1. **Byte-identical MQL.** Capture the `MONGODB_EF_NATIVE_ONLY=1` spec-suite output on the pre-refactor tip
   (`5d53633`), then confirm the post-refactor output is byte-identical. This is the primary regression net
   for the "no behavior change" and "no broadened-signal-reach" claims.
2. **Full 3-version `/test-all`** green — EF8/EF9/EF10, all three assemblies (UnitTests, FunctionalTests,
   SpecificationTests).
3. The `QueryModeGate*` functional tests and every `NativeOnly` succeed/throw assertion unchanged.

Note (from the Query AGENTS.md testing guidance): MQL shape alone cannot prove a query went native for
filter/sort/paging — the reliable "goes native" signal is `NativeOnly` mode (native-capable ⇒ succeeds;
otherwise ⇒ throws). The classification's behavior is therefore exercised through the `NativeOnly`
succeed/throw assertions in addition to its own unit tests.

## Testing

New unit tests under `tests/.../Query/NativeTranslation/` assert `ClassifyNativeDisposition` returns the
correct disposition for each class:

- whole-entity filter/sort/paging → `Native`;
- a projection / GroupBy / scalar-aggregate route → `Native` (disposition is native; builder chosen by `Route`);
- an unsupported shape (`Route == Fallback`) → `Fallback`;
- a query containing `VectorSearch` → `Fallback`;
- GroupBy + Join (`IsGroupByFallbackUnsafe`) under `Native`/`NativeOnly` → `HardDecline`; under `DriverLinq` →
  not `HardDecline` (it runs).

Behavioral coverage already exists via the gate functional tests; the byte-identical spec sweep is the
regression net for the call-site rewrite.

## Follow-ups / ticket cleanup

- On close, note on EF-334 that its premise was refined: lookup-streamability is a separate (streaming) axis
  and stayed out; the third real is-native signal (GroupBy-unsafe) was folded in.
- EF-317 remains open and driver-gated; unaffected by this change.
