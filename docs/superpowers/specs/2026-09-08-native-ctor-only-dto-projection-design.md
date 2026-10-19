# Native translation for a ctor-only DTO projection (constructor argument, not property, driven)

*(JIRA ticket not yet filed — per explicit instruction, filing is deferred until this design is approved.)*

## Problem

`NorthwindSelectQueryMongoTest.Entity_passed_to_DTO_constructor_works` falls back to
driver-LINQ under default `Native` mode (and would throw
`NativeTranslationNotSupportedException` under `MongoQueryMode.NativeOnly`). The query,
from the upstream spec base:

```csharp
ss.Set<Customer>().Select(x => new CustomerDtoWithEntityInCtor(x));

public class CustomerDtoWithEntityInCtor(Customer customer)
{
    public string Id { get; } = customer.CustomerID;
}
```

The DTO's constructor takes the whole `Customer` entity; `Id` is computed inside the
DTO's own primary-constructor body, not via a compiler-synthesized property/parameter
mapping. The projected `NewExpression`'s `Members` is therefore `null` — the C# compiler
only populates `Members` when every constructor parameter maps 1:1 by name to a
same-named property (anonymous types, positional records). `NativeProjectionBinder`'s
only recognizer for a constructed (`NewExpression`/`MemberInitExpression`) projection
body, `ExpressionExtensionMethods.TryGetProjectionMembers`, *requires* `Members` to be
non-null and count-matched to `Arguments` (`ExpressionExtensionMethods.cs:130-172`).
This DTO's shape fails that guard, so `NativeProjectionBinder.TryPopulateNativeProjection`
falls through to the bare-body default arm, which treats the entire `NewExpression` as
one opaque leaf; `TryTranslateLeaf` has no case for "an opaque constructor call over the
entity," declines, and the whole query is marked `Select.Route = Fallback`.

## Investigation findings (why the design ends up narrower and safer than it first looked)

1. **`RebuildProjectionMembers` (`ExpressionExtensionMethods.cs:187-199`) is already
   purely positional.** Its `NewExpression` branch is `newExpression.Update(values)` —
   an ordinary `Expression.Update` keyed by argument *position*, not by `Members`. It
   does not itself require named members; only the *admission gate*
   (`TryGetProjectionMembers`) does.

2. **`DeriveWrappedLeafAlias` (`NativeProjectionBinder.cs:1084-1110`) already tolerates a
   non-property name.** Its two special cases key off the navigation's own document path
   or the leaf expression's shape, not the member name; any other leaf falls straight to
   `return memberName;`. A synthetic name flows through unchanged.

3. **The real constraint is on the *read* side, and it rules out a naive "just widen
   `TryGetProjectionMembers` everywhere" fix.** `MongoProjectionBindingExpressionVisitor`
   builds the ordinary `Select`/`Join` shaper using EF Core's own
   `Dictionary<ProjectionMember, Expression>` (`_projectionMapping`), keyed by pushing
   real `MemberInfo`s onto a `ProjectionMember` chain (`ProjectionMember.Append(MemberInfo)`
   — confirmed there is no int/string/object overload anywhere in EF Core, and no
   synthetic-`MemberInfo` trick exists anywhere in this codebase today). `VisitNew`
   (`Visitors/MongoProjectionBindingExpressionVisitor.cs:1149-1180`) only calls
   `EnterProjectionMember`/`ExitProjectionMember` when `Members != null`; for a
   `Members == null` body it visits every argument under whatever `ProjectionMember` is
   already on top of the stack — for a single argument that's the ambient/root member,
   with no collision. This is **already exercised today**, unmodified, because `VisitNew`
   is reached unconditionally for any `NewExpression`, regardless of what
   `NativeProjectionBinder` decided — it's what builds the (currently fallback-only,
   mixed-shaper-compatible) tree for this exact test today.

   Confirmed empirically that widening `TryGetProjectionMembers` to admit this shape and
   routing it through `NativeProjectionBinder`'s existing WRAPPED (named-`Members`) arm
   would **break**: that arm registers the alias via `namedAliasOverrides`, keyed by the
   synthetic member name, but `MongoQueryExpression.ApplyProjection`
   (`MongoQueryExpression.cs:182-204`) looks up the override via
   `projectionMember.Last?.Name`, which is `null` for the ambient/root member `VisitNew`
   actually uses (`Last` is `null` for an empty chain). A synthetic-name-keyed
   registration is never found by that `null`-keyed read — a hard read failure or, worse,
   a coincidental wrong-field read. This is exactly the "gate wider than what the read
   side resolves" trap this codebase has hit before (see Query `AGENTS.md`'s durable
   invariants). The bare-body arm's *existing* null-keyed alias-override mechanism
   (`bareProjectionAlias`/`MongoSelectDefinition.BareProjectionMemberKey`) is the one that
   actually matches what `VisitNew` does for a `Members == null` body — so the fix must
   route through that mechanism, not through the wrapped arm.

4. **`GroupBy`/`SelectMany` result-selector shaping is architecturally different and
   does not share constraint #3.** `QMTEV`'s `TryBuildGroupResultShaper` /
   `BuildSelectManyResultShaper` hand-build their own shaper: each bound member is
   registered via `AddToProjection(value, alias)` (alias = an ordinary string) and read
   back via EF's *index*-based `ProjectionBindingExpression(Expression, int, Type)`
   constructor — not the `ProjectionMember`-chain one `VisitNew` uses. This mechanism has
   no arity ceiling and no naming constraint; a synthetic per-argument name is exactly as
   valid here as a real member name, for any number of arguments.

## Scope

**In scope:**

- **Ordinary `Select`/`Join` projection** — a projected body that is a `NewExpression`
  with `Members == null` and **exactly one** constructor argument. The argument may be:
  - the selector's own root parameter (the whole entity) — e.g. `new
    CustomerDtoWithEntityInCtor(x)`;
  - any other leaf `TryTranslateLeaf` already recognizes with `allowWholeRootEntityLeaf:
    true` — a scalar/computed leaf (e.g. `new IdWrapper(x.CustomerID)`), etc.
- **`GroupBy`/`SelectMany` result-selector projection** — a result-selector body that is a
  `NewExpression` with `Members == null` and **any number ≥ 1** of constructor arguments,
  each independently a shape `BindGroupMember`/`BindResultMember` already knows how to
  translate for a *named* member today (e.g. `.GroupBy(o => o.CustomerID).Select(g => new
  GroupDto(g.Key, g.Count()))` where `GroupDto`'s constructor takes `(string, int)` with
  no matching property names).

**Deliberately asymmetric arity cap, and why:** ordinary `Select`/`Join` is capped at one
argument because its read side is EF Core's `ProjectionMember`/`MemberInfo`-chain
dictionary, which has no safe way to distinguish multiple unnamed arguments without
fabricating a fake `MemberInfo` (a real hack against an EF Core internal contract, with
unenumerable risk — rejected). `GroupBy`/`SelectMany` has no such constraint (finding 4),
so capping it at one argument there would be an arbitrary, unjustified restriction — it
supports full arity.

**Out of scope (deferred, no ticket filed unless requested):**

- `MemberInitExpression` whose constructor itself takes arguments (`new Foo(1) { Bar = 2
  }`) — rare, adds meaningfully different logic to `TryGetProjectionMembers`'s
  `MemberInit` branch, no failing test drives it.
- Two-or-more-argument ctor-only DTOs in ordinary `Select`/`Join` — see the arity-cap
  rationale above. A follow-up would need a `MemberInfo`-fabrication design explicitly
  reviewed for the risk called out in finding 3, not folded into this one.
- Nested ctor-only DTOs as a constructor argument's own value (e.g. `new
  Outer(new Inner(x))`) inside either family — no test drives it; the recognizers below
  should decline (not crash) rather than silently mishandle it, but building explicit
  support is separate work.
- Owned single-reference nav-entity ctor-wrap in ordinary `Select`/`Join` (e.g. `new
  AddressDto(x.Address)`). Originally scoped in-scope (above), this was descoped during
  implementation: admitting it through sub-case 2's `TryBindAsBareProjection` path would
  need to reuse `TryTranslateLeaf`'s owned-nav-entity-leaf arm, which is gated by an
  `alias == ownedNavElementName` conjunct — but sub-case 2 hands that leaf a placeholder
  alias, which never satisfies that gate. Reworking the shared gate to also admit a
  ctor-wrap alias would risk the already-shipped EF-441 WRAPPED-path feature that gate
  also serves, and deserves its own design review rather than folding into this ticket.
  See the ledger at `.superpowers/sdd/2026-09-08-native-ctor-only-dto-projection/progress.md`
  (Task 5 entries) for the full rationale. The shape falls through to `Route == Fallback`
  exactly as it did before this ticket — throws under `NativeOnly`, falls back correctly
  under the default `Native` mode. A follow-up ticket's work, not this one's.

## Design

### A. Ordinary `Select`/`Join`: new recognizer in `NativeProjectionBinder`

A new arm in `NativeProjectionBinder.TryPopulateNativeProjection`'s `switch
(selector.Body)`, placed alongside the existing WRAPPED (`TryGetProjectionMembers`) and
bare-body (`default`) arms — matching `NewExpression { Members: null, Arguments.Count: 1
}`:

- **Sub-case 1 — the sole argument literally *is* the selector's root parameter**
  (`ReferenceEquals(ctorOnly.Arguments[0], selector.Parameters[0])`, the same identity
  check the existing whole-root-entity leaf already uses): register **nothing** to
  `Select.Projection`. Guarded the same way the bare-body arm already is
  (`mongoQ.Select.Projection.Count > 0` → decline; this guard is additionally already
  enforced at the top of the method for any `NewExpression`/`MemberInitExpression` body,
  per the existing re-entrancy guard at lines 44-75). With no projection entries added,
  `MongoSelectDefinition.Route` resolves to the pre-existing `NativeRoute.WholeEntity` —
  the same route a plain `Set<Customer>()` takes, so every existing entity-materialization
  concern (discriminator narrowing, key handling, `Include` fix-up) applies unchanged, and
  `VisitNew`'s existing unmodified `Members == null` handling (finding 3) produces the
  correct shaper without needing to know anything happened here.
- **Sub-case 2 — anything else**: run `ctorOnly.Arguments[0]` through *exactly* the
  bare-body arm's own machinery — `TryTranslateLeaf(mongoQ, translator,
  selector.Parameters[0], ctorOnly.Arguments[0], provisionalAlias, pendingLookups,
  pendingReducerLeaves, out var leaf, out var isArrayLeaf, out _,
  allowWholeRootEntityLeaf: true)`, then `TryDeriveDocumentPathAlias`/
  `TryDeriveSyntheticAlias` in the same tier order, registering via the same
  `bareProjectionAlias`/`bareProjectionTier` mechanism the true bare-body arm uses. This
  is the same alias-override convention `VisitNew`'s ambient/root-member read already
  resolves correctly (finding 3) — no new read-side code. `allowWholeRootEntityLeaf: true`
  is passed here so this leaf is evaluated under the same conjuncts the WRAPPED case uses;
  in practice this does NOT admit the owned single-reference nav-entity leaf for this
  ctor-wrap shape — see "Out of scope" above for why (a placeholder alias never satisfies
  that leaf's `alias == ownedNavElementName` gate), so `new AddressDto(x.Address)` still
  declines here exactly as a true bare body does.
  - If `TryTranslateLeaf` declines, or neither alias tier derives one: decline the whole
    projection (`return false`), same as today — falls back, no behavior change for
    unsupported shapes.

**Implementation note:** the cleanest structure is likely to factor the bare-body arm's
body (lines ~193-259 today) into a small local function taking "the expression to treat
as bare" as a parameter, called once with `selector.Body` (today's default arm, unchanged
behavior) and once with `ctorOnly.Arguments[0]` (sub-case 2, `allowWholeRootEntityLeaf:
true`) — rather than duplicating the tier-derivation logic. Confirm during implementation
whether this factoring is clean given the surrounding locals (`provisionalAlias`'s
`MaterializeCollectionNavigationExpression` special case is bare-body-specific and
probably should NOT extend to the ctor-wrap sub-case — a ctor wrapping a collection
navigation directly, e.g. `new PostsDto(x.Posts)`, is not driving any currently-failing
test and should fall through to whatever `TryTranslateLeaf` naturally decides, most likely
a decline).

No changes to `ExpressionExtensionMethods.TryGetProjectionMembers`/
`RebuildProjectionMembers`, `MongoProjectionBindingExpressionVisitor`, or
`NativeJoinScopeProjectionBinder` for this family — the fix is local to
`NativeProjectionBinder.cs`.

*(`NativeJoinScopeProjectionBinder.TryBindProjection` presumably feeds the same
`ProjectionMember`-keyed read mechanism as ordinary `Select` — this needs confirming
during implementation; if so, an equivalent recognizer arm should be added there too,
mirroring §A exactly. Treat as in-scope but unverified until the file is read directly.)*

### B. `GroupBy`/`SelectMany`: widen `TryGetProjectionMembers`, opt-in

Add a parameter to `TryGetProjectionMembers`, defaulting to today's behavior:

```csharp
internal static bool TryGetProjectionMembers(
    this Expression body,
    out IReadOnlyList<(string MemberName, Expression Value)> members,
    bool allowPositionalConstructorArguments = false)
```

When `allowPositionalConstructorArguments` is `true`, additionally accept a
`NewExpression` with `Members == null` and `Arguments.Count > 0`, synthesizing
`_ctorArg0`, `_ctorArg1`, ... as the pseudo `MemberName` for each positional argument (an
underscore-prefixed, self-documenting synthetic key — consistent with this codebase's
existing synthetic-alias conventions, e.g. `_v`, `MongoSelectDefinition
.BareProjectionMemberKey`). No change to the `MemberInitExpression` branch (stays
out of scope per above).

Pass `allowPositionalConstructorArguments: true` at exactly the 5 call sites in this
family: `NativeGroupByBinder.TryBind`, `NativeSelectManyBinder.TryBind` (unwind-`Select`
recognition), `NativeSelectManyBinder`'s bare-body check, and QMTEV's
`TryBuildGroupResultShaper` / `BuildSelectManyResultShaper`. Leave the 3 call sites in
family A (`NativeProjectionBinder` ×2 — the WRAPPED arm and the document-construction
leaf — and `NativeJoinScopeProjectionBinder.TryBindProjection`) at the default `false`,
unchanged — **this is the load-bearing boundary of this design**; widening those three
would reintroduce finding 3's read-side mismatch.

`RebuildProjectionMembers` needs no change (finding 1) — `newExpression.Update(values)`
already reconstructs correctly for any arity, named or synthetic. `BindGroupMember`/
`BindResultMember` need no change to their own leaf-translation logic — they already
translate whatever expression they're handed for a named member; a synthetic name is
just a different string flowing through the same `AddToProjection(value, alias)` call.

## Decline matrix

- **Ordinary `Select`/`Join`, 2+ constructor arguments with `Members == null`** —
  declines (falls back), per the arity-cap rationale. No behavior change from today.
- **Ordinary `Select`/`Join`, single argument `TryTranslateLeaf` doesn't recognize even
  with `allowWholeRootEntityLeaf: true`** — declines, same as today.
- **`MemberInitExpression` with a non-empty-arg constructor, either family** — declines
  (out of scope), same as today (already declines via `TryGetProjectionMembers`'s
  existing `NewExpression.Arguments.Count: 0` requirement on that branch, untouched).
- **A ctor-only DTO nested as another leaf's own value** — declines rather than
  recursing, unless `TryTranslateLeaf`'s existing recursion already happens to reach it
  naturally; confirm empirically, don't assume either way.

## Testing

- New unit tests (`UnitTests/Query/NativeTranslation/`) for family A: whole-entity
  ctor-wrap → `NativeRoute.WholeEntity`, no `$project` stage; scalar-argument ctor-wrap →
  bare-alias `$project` entry; owned single-reference nav-entity ctor-wrap declines (proves
  the descope above — throws under `NativeOnly`, falls back correctly under `Native`);
  2-argument ctor-only DTO still declines; a ctor-only DTO alongside a prior `Select` in
  the chain still declines (existing re-entrancy guard).
- New unit tests for family B: `GroupBy` result-selector with a 2-argument ctor-only DTO
  (`(Key, Count)`-shaped); `SelectMany` result-selector with a ctor-only DTO; confirm
  `allowPositionalConstructorArguments: false` (the 3 family-A call sites) is unaffected
  by a table-driven or reflection-based test iterating all 8 `TryGetProjectionMembers`
  call sites, if such coverage already exists for this helper (check
  `MongoExpressionNodeCoverageTests`-style patterns before adding a new one).
- Flip `Entity_passed_to_DTO_constructor_works` (and its EF9/EF10 fixture-parameterized
  duplicate) from fallback-passing to a `MONGODB_EF_NATIVE_ONLY=1` pass. Expect the MQL
  baseline to remain the bare `Customers.` scan (sub-case 1 of §A adds no `$project`) —
  same shape, now genuinely native (MQL shape alone cannot prove this — assert under
  `NativeOnly`, per Query `AGENTS.md`'s stated pitfall).
- `MONGODB_EF_NATIVE_ONLY=1` full spec-suite run before/after: expect this test (plus any
  other currently-failing test sharing this exact shape) to flip `Failed → Passed`, zero
  `Passed → Failed`.
- Regression: confirm `MongoQueryMode.DriverLinq` explicitly is byte-for-byte unaffected.
- Multi-EF-version: no `#if` expected; confirm during implementation.

## Residual risks / open items to confirm empirically before/during implementation

1. Whether `NativeJoinScopeProjectionBinder.TryBindProjection` genuinely feeds the same
   `ProjectionMember`-keyed read as ordinary `Select` (assumed, not yet read directly) —
   determines whether §A's recognizer needs a join-scope twin.
2. Whether factoring the bare-body arm into a shared local function (§A's implementation
   note) is clean given `provisionalAlias`'s `MaterializeCollectionNavigationExpression`
   special case, or whether that case needs explicit exclusion from the ctor-wrap
   sub-case.
3. Whether any existing reflection/table-driven test already enumerates all
   `TryGetProjectionMembers` call sites (the coverage pattern `NativeProjectionBinder.cs`'s
   own `AGENTS.md` invariant list mentions for `MongoExpression` node kinds) — if so, it
   needs updating to include the new parameter; if not, worth adding one so a future call
   site can't silently default to the wrong boolean.
4. Whether a ctor-only DTO nested as another leaf's own value should decline cleanly or
   happens to recurse correctly through existing machinery — verify, don't assume.
