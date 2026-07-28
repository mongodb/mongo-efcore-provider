# EF-322 step 3a — the bare-projection boundary — design

*Originally written 2026-08-06 against `EF-322-step3a` @ `7af6e0da`, **deliberately unmeasured** (no build, no
test). **REVISED 2026-08-06 at `d5795324`, after the slice's own Task 0 measured the complete change and
refuted five of this document's claims — including one that would have shipped silent wrong data.** The
corrections are made **in place**, not annotated around: where a claim was wrong, the wrong text is replaced
and the replacement says what was believed, what was measured, and what is now true. The design's conclusion
has changed shape as a result: 3a is now **variant D** (§4.6), it touches **one file the original design listed
as "not changed"**, and the fail-loud invariant the original design offered as its safety story has been
**removed** because it was measured to catch nothing.*

*Inputs, in the order read: `docs/superpowers/specs/2026-08-06-step3a-task0-spike-findings.md` (**this slice's
Task 0 — authoritative on every number and every disposition in this document**),
`docs/superpowers/specs/2026-08-06-step3-projection-spike-findings.md` (the step-3 spike that sized 3a, and
carries the owner rulings — §0 rulings box below), `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` in full,
`docs/superpowers/specs/2026-08-06-vectorsearch-slice-design.md`, `docs/native-query-status-EF-322.md`
§4/§5/§9.*

**Tagging convention, applied strictly.** Provenance tags describe *how* a fact was established; verdict tags
describe *whether it held*. Every substantive claim below carries at least one of each where both apply.

Provenance:

- **READ** — established by reading source at `7af6e0da`/`d5795324`; no execution.
- **MEASURED(T0)** — executed by this slice's Task 0 (`…-step3a-task0-spike-findings.md`); the section of that
  document is cited inline.
- **CITED** — measured by the step-3 spike, by an earlier slice, or recorded in `Query/AGENTS.md`; used, not
  re-derived.

Verdict:

- **VERIFIED** — Task 0 measured it and it held. The supporting measurement is named.
- **REFUTED** — Task 0 measured it and it did **not** hold. The original claim is quoted, then replaced.
- **INFERRED** — drawn from READ/CITED facts, not itself observed, and no measurement bears on it.
- **UNVERIFIED** — nobody has established it. Every remaining instance is assigned to a task's verification
  step in §12's table; there are no unassigned ones.

**Nothing in this document is MEASURED by its author.** Every number is MEASURED(T0) or CITED.

> **The read side is FOUR alias-derivation sites, not three — and the original version of this box said
> three. Corrected here.** The prompt that commissioned the design described the read side as
> *"`ApplyProjection`'s `AddToProjection(expr, projectionMember.Last?.Name)` yields a null alias, which
> `MongoProjectionBindingRemovingExpressionVisitor` reads as 'the BsonDoc itself is the value'"* — correct, and
> the step-3 spike's Q1 site 2, but not the whole read side. The design then found a third site
> (`MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection`, the one the bare-ARRAY case needs)
> and called the inventory complete. **MEASURED(T0) §4: there is a FOURTH**,
> `MongoMixedProjectionBindingRemovingExpressionVisitor.cs:91`, which independently derives
> `projectionBindingExpression.ProjectionMember.Last?.Name`. It needs **no edit** — see §2.2, which now lists
> all four and explains why site D inherits site A for free — but an inventory that says "three" is wrong as an
> inventory, and "a half-moved site is exactly the failure mode this slice exists to prevent" is this document's
> own argument. §2.2 also records two READ facts about site D that the original design omitted and that are
> load-bearing for anyone widening it.

---

## 0. Executive summary

| | |
|---|---|
| **Target — CORRECTED. The original "78 from tier 1, up to 100 with tier 2" is REFUTED, three separate ways.** | **MEASURED(T0) §2.2/§2.3: 74 tier-1 + 6 tier-2 = 80** EF10 `MONGODB_EF_NATIVE_ONLY=1` cases Failed→Passed, 0 Passed→Failed, **1** residual default-`Native` failure. Not 78, not 100. Three corrections in one: (a) **tier 2's yield is +6, not +22** — the step-3 spike's family-B sizing does not convert, because most family-B bodies are constants, captured parameters, or leaf kinds `TryTranslateLeaf` still declines; (b) tier 1 alone is **74** (72 without the §4.6 fallback strip); (c) **nothing "turns green" without re-baselining** — see the row below. Of the 80, **4** are `VectorSearch_with_projection` (VERIFIED — MEASURED(T0) §2.3, the `NativeOnly` VectorSearch residual moves 20 → 16, and the remaining 16 are exactly 4 `complex_pre_filter` (EF-382) + 12 entity-leaf (3b)). Plus the bare owned/primitive array and bare `.Count` shapes, which Northwind does not cover at all and which are provable **only** functionally. |
| **"Turn green" was the wrong verb — and this is the most consequential measurement instrument in the slice.** | **MEASURED(T0) §2.1: the spec suite runs `AssertMql` under `MONGODB_EF_NATIVE_ONLY=1` too.** A case that stops declining now *reaches* its committed MQL assertion and fails **there** instead. Of the 78 cases the original design promised as green, **76 become MQL string diffs**. Tier 1 rewrites **78 `AssertMql` baselines across 13 spec files** — uniformly `{$project: {_v: "$X", _id: 0}}` → `{$project: {X: "$X", _id: 0}}`. Per the versioning rubric emitted MQL is not contract, so these are legitimate re-baselines — but the original Task-2 acceptance criterion (*"**zero** `AssertMql` baseline diffs … any diff is a finding"*) is **unmeetable** and is replaced in §8. |
| **The one new mechanism** | A **projection-alias override table** on `MongoSelectDefinition`, written once by the binder and read by *every* site that would otherwise derive an alias from `ProjectionMember.Last?.Name`. Not a new expression node, not a new stage, not a new shaper. §3. |
| **The SECOND mechanism the original design did not have, and without which 3a ships silent wrong data** | A **leaf-kind-conditional strip of the pushed-down bare `Select`** on `CompileShapedQuery`'s native-factory-failure fallback, reusing the existing `StripPushedDownSelect`. Strip for a path-addressable **tier-1** leaf (the fallback then yields whole documents, which tier 1 reads correctly); do **not** strip for a **tier-2 `_v`** leaf (whose fallback alias genuinely *is* `_v`). This is **variant D**, MEASURED(T0) §6: 80 wins, 1 residual, no silent wrong data. §4.6, Task 1b. |
| **How the sites are kept in sync, and what the ACTUAL protection is** | They stop being independent alias spaces: every site reads **one field the emit side wrote**, and if the binder declined the field is empty and all four sites behave byte-identically to today. **But the original design's stronger claim — "the measured-worse state is not merely un-committed, it is UNREPRESENTABLE" — is REFUTED** (MEASURED(T0) §5.2: two independent live shapes reach emit-admitted / read-declined). The real protection is three concrete things, not an impossibility argument: the two narrowings of §7.2/§7.3, the Task-1b fallback strip, and a test plan whose functional legs actually exercise the late fallback. §3.3. |
| **The alias scheme** | **Tier 1 — the alias IS the leaf's root-relative document path** (`Title`, `_id`, `Posts`, `Tags`). Alias-addressed read and document-path read are then literally the same read, so the shaper is correct against a projected document *and* against a whole one. VERIFIED for every tier-1 shape probed (MEASURED(T0) §5.4). **Tier 2 — a computed leaf** (count, filtered count, arithmetic, `__score`) has no document path and gets the reserved synthetic alias `_v`. §4. |
| **The array-leaf constraint (step-3 spike Q3)** | Falls out of tier 1 rather than being bolted on: tier 1 *defines* the alias as the element name, which is exactly what `IsNativeArrayProjectionLeaf`'s alias conjunct demands. Stated honestly, and MEASURED(T0) §6.1 variant B: for a bare body that conjunct is **vacuous** (we choose the alias, so it always agrees) and the work is done by its `DeclaringEntityType == rootEntityType` sibling — overriding the alias to `_v` anyway produces **five rows of silently empty collections**. §4.3. |
| **EF-362** | The scheme subsumes the alias/path *decoupling*: EF-362 is the *same* override table with a non-empty member key (`"Notes" → "Home.Notes"`). **But the dotted READ does not work today** — READ, and unchanged by Task 0: `BsonBinding.GetElementValue`/`GetPropertyValueAtElement` build a `BsonSerializationInfo` with a null `ElementPath`, so a dotted name is a *literal key* lookup while MongoDB's `$project: {"Home.Notes": …}` emits a *nested* document. `TryReadElementValue` already contains the path walk, so the missing piece is small and contained — but it is **EF-362's piece, not 3a's**. §4.5. |
| **Loud or silent?** | **Silent, VERIFIED twice by mutation** (MEASURED(T0) §5.1). Mutation 1 (deliberate: force the bare alias to `_v` while leaving the array shaper site behind) → five rows of silently empty collections under the **default** mode. Mutation 2 (accidental, and therefore more convincing: the design's own unmutated tier-1 alias on the late-fallback route) → nullable string `null`, nullable int `null`, owned array `[]`. So the §6.1 loud-vs-silent table is VERIFIED as written. **§6.2's fail-loud invariant is REFUTED and has been REMOVED** — it never fired once across twelve runs, including every silent-wrong-data run, because it compares two facts the same block writes. §6.2 now says plainly where the protection is instead. |
| **Blast radius** | Opening `TryPopulateNativeProjection`'s `default:` arm opens it for **every** call site of that method (CITED `Query/AGENTS.md`). §7 lists the incidental widenings and specifies **two deliberate narrowings** (bare set-op operand, bare `Distinct`). **Both are MEASURED(T0) §5.3 to be load-bearing:** dropping them costs **+24** default-`Native` Passed→Failed and flips `Intersect_non_entity`/`Except_non_entity` to *succeeding* where the spec asserts a throw, on the two operators with no driver-LINQ oracle. |
| **Breaking change** | **Expected none.** `Infrastructure/MongoQueryMode.cs` does not exist at `v10.0.2`/`v9.1.2`/`v8.4.2` (CITED VectorSearch design §10, `git ls-tree`-verified there), every file 3a touches is `internal`, and the rubric carves out both "which internal execution path a supported LINQ query takes" and the emitted MQL. **Verify per member against the release TAGS in the final task; do not take it from here.** §11. |
| **The one measured cost, and the owner should see it** | **MEASURED(T0) §3.3/§6.2, no longer a prediction:** under explicit `DriverLinq`, a bare `Select(b => b.Posts.Count)` on a **missing** or explicitly-**null** stored array now aborts with `MongoCommandException` (the driver emits a bare `$size` with no `$ifNull`) where the pre-3a client-side fold answered `0`. `Native` and `NativeOnly` are correct for every array state. Not a break (`MongoQueryMode` exists at no release tag) and it makes the bare spelling agree with the already-documented *wrapped* spelling rather than disagree with it — but it is a real behaviour change to a documented escape hatch. §7.4. |

### 0.1 Owner rulings — carried forward, unchanged

*Settled by the owner on the step-3 spike (`…-step3-projection-spike-findings.md`, "Owner rulings — ANSWERED
2026-08-06"). Treat as decisions, not suggestions. Nothing Task 0 measured disturbs any of them.*

1. **Proceed with 3a.** The boundary is a genuine structural prerequisite: 3d's 104 cases depend on it, it
   carries 4 of the 16 `VectorSearch` residuals, and it has to happen regardless of how the rest is ordered.
   (The ranking for step 4 onward is *not* trustworthy and is to be re-derived separately.)
2. **EF-356 is fixed in 3b, not here.** 3b opens the mixed shaper, which is where EF-356's silent-wrong-data
   path lives; shipping new code over a known-broken path is not acceptable. 3a does not touch it. §10 item 1.
3. **3a's alias scheme is designed with EF-362 in view** — the same alias/path decoupling, designed once rather
   than twice. §4.5 records exactly how far that gets EF-362 and what it still needs.

---

## 1. What changes, in one table

| # | File | Change | Task |
|---|---|---|---|
| 1 | `Query/Expressions/MongoSelectDefinition.cs` | The alias-override table: `ProjectionAliasOverrides` + `AddProjectionAliasOverride(string memberName, string alias, ProjectionAliasTier tier)` + `TryGetProjectionAlias(string? memberName, out string alias)` + `IsBareProjection` + `BareProjectionTier`, keyed by a **sentinel constant** for the bare body (§3.1 — a `null` `Dictionary` key throws at run time, and the original design's printed code was wrong here). The **tier** is carried as data on the override because Task 1b's strip is tier-conditional. **One writer** (the binder); **three readers** — site A, site C, and Task 1b's fallback strip. | 1 |
| 2 | `Query/Expressions/MongoQueryExpression.cs` | `ApplyProjection` derives its alias through `Select.TryGetProjectionAlias(projectionMember.Last?.Name)`, falling back to today's `Last?.Name`. **No invariant check** — the one the original design specified here was measured to be a tautology and has been removed (§6.2). | 1 |
| 3 | `Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` | `TryBindNativeArrayProjection` derives its alias the same way (site C). | 1 |
| 4 | **`Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`** | **The file the original design listed as "not changed".** In `VisitProjectedQuery`'s `Route == Projection` branch, when `TryBuildNativeFactory` returns `null`, strip the pushed-down bare `Select` from the expression handed to the driver-LINQ bridge — **conditional on the bare leaf's tier** (strip for tier 1, not for tier 2), reusing the existing `StripPushedDownSelect` (already in this file, used by the mixed path at `:365`). Inert until Task 2 registers an override. §4.6. | **1b** |
| 5 | `Query/NativeTranslation/NativeProjectionBinder.cs` | `TryPopulateNativeProjection`'s `default: return false` becomes the bare-body arm: derive the alias, route the body through the **existing** `TryTranslateLeaf`, admit by node kind, register the override **with its tier**. | 2 (tier 1) / 3 (tier 2) |
| 6 | `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | `IsPlainProjectedSelect` gains a `!IsBareProjection` conjunct — the deliberate set-op-operand narrowing (§7.2). | 2 |
| 7 | `Query/NativeTranslation/NativeGroupByBinder.cs` | `TryBindDistinctFromProjection` declines a bare projection — the deliberate `Distinct` narrowing (§7.3). | 2 |
| 8 | `tests/.../UnitTests/Query/NativeTranslation/` | Alias-table unit tests (write-once/read-many, `Route`-gating, tier round-trip); bare-body binder admit/decline tests. | 1, 2, 3 |
| 9 | `tests/.../FunctionalTests/Query/NativeBareProjectionTests.cs` **(new)** | The behavioural net — the bare array and bare `.Count` wins are invisible to Northwind, **and the late-fallback route is invisible to a constant-only `Where`**, which is why §9.3 now requires a parameterized-`Where` leg per leaf kind. §9. | 1b, 2, 3 |
| 10 | 13 spec files (`AssertMql` baselines) | **78 re-baselines**, `_v` → the tier-1 alias. MEASURED(T0) §5.4. Legitimate per the versioning rubric; enumerated in Task 2. | 2 |
| 11 | `Query/AGENTS.md`, `docs/native-query-status-EF-322.md` | As-built note; §4/§5/§9.1 corrections (bare scalar / bare array / bare count entries all move); the `Multiple_queries` residual; site D; the incidental widening of bare-`Select`-then-`Count()`/`First()`. | 5 |

**Not changed, deliberately:** the `projection.Alias is null ⇒ return DocParameter` branch in
`MongoProjectionBindingRemovingExpressionVisitor` (site B — §2.3 explains why touching it is the wrong edit);
`MongoMixedProjectionBindingRemovingExpressionVisitor` (site D — **it inherits site A for free; §2.2 records
why, rather than leaving it a bare "not changed"**, and MEASURED(T0) §4 confirms no measurement required it to
change); `MongoSelectLowerer`; `MongoPipelineFactory.RenderProject`; `ProjectionAnalyzer`; anything under
`Storage/` (except as an explicitly deferred EF-362 item, §4.5); anything under `Metadata/`.

---

## 2. The boundary, mechanically — what I READ at `7af6e0da`

### 2.1 Emit side

`NativeProjectionBinder.TryPopulateNativeProjection` (`NativeProjectionBinder.cs:62-102`) switches on
`selector.Body` with exactly two arms — `NewExpression` (with matching `Members`) and `MemberInitExpression` —
and a `default: return false`. The caller, `TranslateSelect`'s non-grouped projection branch
(`MongoQueryableMethodTranslatingExpressionVisitor.cs:278-281`), turns that `false` into
`MarkNotNativelyRepresentable()`, so `Route` is `Fallback`. **READ**, and CITED as MEASURED by the step-3 spike:
every bare-body case in the 881 reports `site=…TranslateSelect:280`.

A bare body reaches that branch: READ, none of the four pass-through predicates matches it —
`IsSingleLevelReferenceIncludeSelector` and `IsSingleLevelCollectionIncludeSelector` require an
`IncludeExpression` body, `IsOwnedEmbeddedIncludeSelector` likewise, and
`IsTransparentIdentifierMemberAccessSelector` requires the lambda parameter's own *type name* to start with
`TransparentIdentifier` (`:467-471`) — so an ordinary `b => b.Title` / `b => b.Posts` is not caught by it.

### 2.2 Read side — alias derivation, FOUR sites

*The original design's table listed three and described site D only as "not changed". MEASURED(T0) §4: there
are four. Site D is a genuine, independent fourth derivation of the same fact — `grep -rn 'Last?\.Name' src/`
returns three hits, and the fourth arrives by a different route, which is exactly why it was missed.*

| # | Site | Today | Consequence for a bare body | Edited? |
|---|---|---|---|---|
| A | `MongoQueryExpression.ApplyProjection` (`:105-108`) | `AddToProjection(expression, projectionMember.Last?.Name)` | the empty `ProjectionMember` has `Last == null`; `AddToProjection`'s own fallback `(expression as IAccessExpression)?.Name` is also null for a `MemberExpression`/`MethodCallExpression` ⇒ **`ProjectionExpression.Alias == null`** | **yes**, Task 1 |
| B | `MongoProjectionBindingRemovingExpressionVisitor.VisitExtension`, `ProjectionBindingExpression` case (`:81-85`) | `if (projection.Alias is null) return DocParameter;` | the shaper's value **is the whole `BsonDocument`** ⇒ `QueryingEnumerable<BsonDocument,BsonDocument>` ⇒ EF rejects it against the query's real return type (CITED: the step-3 spike's 97 `ArgumentException`s) | no — §2.3 |
| C | `MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection` (`:996-1007`) | `IsNativeArrayProjectionLeaf(nav, root, arrayProjectionMember.Last?.Name)` | `alias is not null` fails ⇒ the array leaf binds to the document-path `ObjectArrayProjectionExpression` instead of the alias-addressed `ArrayAliasProjectionExpression` | **yes**, Task 1 |
| **D** | **`MongoMixedProjectionBindingRemovingExpressionVisitor` (`:91`)** | `alias = projectionBindingExpression.ProjectionMember.Last?.Name` | it is the `else` arm of a `mappedExpression is ConstantExpression { Value: int }` test, i.e. reached only *before* `ApplyProjection` has rewritten that member. The `if` arm reads `projection.Alias` and therefore **inherits site A for free**. | **no — and here is why** |

**Why site D needs no edit, stated rather than asserted (READ + MEASURED(T0) §4).** Once site A supplies a
non-null alias, the member has been rewritten to `Constant(index)` by `ApplyProjection`, so the mixed visitor
takes its `if` arm and reads `projection.Alias` — site A's answer. The `else` arm's own
`ProjectionMember.Last?.Name` derivation is therefore unreachable for a *bound* bare projection. MEASURED(T0):
**no measurement in the whole spike required site D to change** — every mixed-path (`DriverLinq`) result came
out correct with site D untouched.

Two related READ facts the original design omitted, both load-bearing for anyone who later widens site D:

- The mixed visitor's `alias is null` branch does **not** return `DocParameter` unconditionally — it first
  tries `TryResolveFieldAccess` and only then falls back. That is the mechanism by which a bare scalar already
  materializes correctly on the mixed path today.
- Once site A supplies a non-null alias, the mixed visitor's tail reads
  `CreateGetValueExpression(_docParameter, alias, …)` — an alias read off the **whole** document. Correct for
  tier 1; a **missing element for a tier-2 `_v`**. MEASURED(T0) not reachable today (every tier-2 shape pushes
  down under `DriverLinq` with an identity shaper, §4.4), but it is one `CanPushDown` change away from
  mattering, and it is the same hazard §4.6's fallback strip exists to handle on the other route.

Site B is downstream of site A: **give site A a non-null alias and site B is never reached**, and everything
below it (property-serializer resolution via `TryResolveFieldAccess`, the raw
`BsonBinding.CreateGetElementValue` path for computed leaves, and the `CollectionShaperExpression` array
machinery) is the code that already carries every *wrapped* native projection. That is the whole argument for
editing site A rather than site B. Originally INFERRED from READ of all four; now **VERIFIED** — MEASURED(T0)
§6.2, variant D reaches 80 `NativeOnly` wins with site B untouched.

### 2.3 Why NOT to edit site B

READ: the `Alias is null ⇒ DocParameter` branch is **live for plain whole-entity queries**. A query with no
`Select` never calls `MongoProjectionBindingExpressionVisitor.Translate`, so `_projectionMapping[empty]` keeps
the `EntityProjectionExpression` the `MongoQueryExpression` constructor installed (`:38-40`);
`ApplyProjection` then calls `AddToProjection(entityProjection, null)` and `EntityProjectionExpression.Name`
for a root is null (it is `(parentAccessExpression as IAccessExpression)?.Name` over a
`RootReferenceExpression`), so the alias is null and site B hands the entity materializer the whole document.
Putting a bare-projection special case *inside* site B would put a new conditional on the hottest path in the
provider and would have to re-implement property resolution, raw-element reads and array reads that the
non-null-alias path already has. Site A is a one-expression change that reaches all of it.

**This also fixes the sequencing problem for free** (§8): site A's change is inert while the override table is
empty, so it can land in a commit of its own with **zero** behaviour change, before the emit gate opens.
**VERIFIED — MEASURED(T0) §1, the inertness control:** with the emit-side arm switched off but all read-side
edits present, the `NativeOnly` failing-name set is **byte-identical to base** (2241/2352/17, `diff` empty).
This is the one claim of the original design that Task 0 confirmed exactly as written.

---

## 3. The lockstep mechanism — one fact, written once, read wherever it is needed

### 3.1 The carrier

**CORRECTED — the original design printed `Dictionary<string?, string>` with `null` as the bare-body key, and
that code does not run.** REFUTED, MEASURED(T0) §3.1: `Dictionary<TKey,TValue>` throws `ArgumentNullException`
on a null key, so both `AddProjectionAliasOverride(null, alias)` and `ContainsKey(null)` throw. The spike used a
**sentinel constant** and so does this design. Trivial, but it means the original block could not be typed in as
written — and the sentinel has to be a string no real member can be called, which is why it carries a space.

The override also carries its **tier** as data, because Task 1b's fallback strip is tier-conditional (§4.6) and
sniffing the alias string for `"_v"` — what Task 0 did as an expedient, and flags as one — is not something to ship.

```csharp
// Query/Expressions/MongoSelectDefinition.cs  (new members)

/// <summary>
/// The override-table key standing in for a BARE selector body, which has no member name at all.
/// A LITERAL SENTINEL, not <see langword="null"/>: Dictionary&lt;string, …&gt; rejects a null key
/// (ArgumentNullException on both Add and ContainsKey), and the leading space makes it
/// unrepresentable as a real CLR member name, so it cannot collide with an EF-362 member key.
/// </summary>
internal const string BareProjectionMemberKey = " bare";

/// <summary>Which alias family a registered override belongs to — read by the late-fallback strip.</summary>
internal enum ProjectionAliasTier
{
    /// <summary>The alias IS the leaf's root-relative document path, so a whole-document read is correct.</summary>
    DocumentPath,

    /// <summary>A computed leaf aliased <c>_v</c>; NOT whole-document-readable.</summary>
    Synthetic
}

private Dictionary<string, (string Alias, ProjectionAliasTier Tier)>? _projectionAliasOverrides;

/// <summary>
/// Overrides the $project OUTPUT ELEMENT NAME (and therefore the name the DOM shaper reads by) for a
/// projection member, keyed by the member's own name — <see cref="BareProjectionMemberKey"/> for a BARE
/// selector body. Written ONLY by NativeProjectionBinder, at the same moment it commits the matching
/// MongoProjection; read by every site that would otherwise derive an alias from
/// ProjectionMember.Last?.Name. Empty ⇒ every one of those sites behaves exactly as it did before 3a.
/// </summary>
internal void AddProjectionAliasOverride(string memberName, string alias, ProjectionAliasTier tier);

internal bool TryGetProjectionAlias(string? memberName, out string alias);

/// <summary>True when a BARE selector body populated <see cref="Projection"/> (EF-322 step 3a).</summary>
internal bool IsBareProjection => _projectionAliasOverrides?.ContainsKey(BareProjectionMemberKey) == true;

/// <summary>
/// The tier of the bare-body override, or <see langword="null"/> if there is no bare-body override.
/// Read by MongoShapedQueryCompilingExpressionVisitor's late-fallback strip (EF-322 step 3a, §4.6):
/// a DocumentPath alias is readable off a WHOLE document, a Synthetic one is not.
/// </summary>
internal ProjectionAliasTier? BareProjectionTier { get; }
```

`TryGetProjectionAlias` keeps a `string?` parameter so the alias-reading sites can pass
`projectionMember.Last?.Name` straight through; it maps `null` onto `BareProjectionMemberKey` internally. That
one asymmetry is deliberate — it keeps the null-handling in exactly one place instead of at every call site.

The map shape (rather than a single `string? BareProjectionAlias`) is **deliberate and is the owner's ruling 3
discharged in code**: EF-362 needs exactly the same decoupling for a *named* member (`"Notes" → "Home.Notes"`),
and designing it once means EF-362 adds binder logic and touches none of the four read sites. See §4.5. For
3a itself the map holds **at most one entry**, always keyed `BareProjectionMemberKey`.

### 3.2 The readers — two edited, two that need none

```csharp
// A. MongoQueryExpression.ApplyProjection
var memberName = projectionMember.Last?.Name;
var alias = Select.Route == NativeRoute.Projection && Select.TryGetProjectionAlias(memberName, out var o)
    ? o
    : memberName;
result[projectionMember] = Constant(AddToProjection(expression, alias));

// C. MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection
var memberName = arrayProjectionMember.Last?.Name;
var alias = _queryExpression.Select.TryGetProjectionAlias(memberName, out var o) ? o : memberName;
… IsNativeArrayProjectionLeaf(nav, rootEntityType, alias) …

// The THIRD reader, added by this revision — MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery,
// on the native-factory-failure fallback (Task 1b, section 4.6). It reads the TIER, never the alias string.
if (select.BareProjectionTier == ProjectionAliasTier.DocumentPath)
{
    fallbackSource = StripPushedDownSelect(fallbackSource);   // whole documents; the tier-1 alias reads them
}
// Synthetic (tier 2) => do NOT strip; the driver's own bare-projection alias IS `_v`.
```

(Sites B and D need no edit — with A supplying a non-null alias, B is not reached and D takes its
`projection.Alias` arm. §2.2.)

### 3.3 What the carrier DOES buy — and the "unrepresentable" claim, REFUTED

**The original claim, quoted so the correction is legible:** *"The measured-worse state (emit gate open, read
side unchanged) is not merely un-committed — it is **unrepresentable**."* **REFUTED. MEASURED(T0) §5.2:** two
independent live shapes reach exactly that state.

**Instance 1 — a `Route` flip after the emit side has committed.** With the §7.3 narrowing removed,
`Select(o => o.Country).Distinct()` reproduces the step-3 spike's finding-3 crash exactly:

```
NorthwindAggregateOperatorsQueryMongoTest.Distinct_Scalar   (async: False/True)
NorthwindAggregateOperatorsQueryMongoTest.OrderBy_Distinct  (async: False/True)
  System.ArgumentException : Expression of type
  'QueryingEnumerable`2[BsonDocument,BsonDocument]' cannot be used …
```

— **four cases that PASS at base under default `Native`.** Mechanism (READ + MEASURED(T0)):
`TryBindDistinctFromProjection` clears `Projection`, installs a `Grouping`, and `Route` flips to
`NativeRoute.GroupBy`. **Site A's own `Route == NativeRoute.Projection` conjunct then reverts the alias to
`null`** — after the emit side has already committed — site B returns `DocParameter`, and the shaper's return
type becomes `BsonDocument`. So **the conjunct the original §3.3 presented as the ordering safeguard is what
manufactures the divergence.** Two facts still exist; they are just written and read at different *times*, and
"one field" does not make a write-then-invalidate sequence atomic.

**Instance 2 — a pipeline supplied by a different component entirely.** §4.2/§4.6: on a late
native-factory failure the fallback keeps the pushed-down `$project`, keyed by the driver's own `_v`, while the
shaper reads the tier-1 alias. Emit committed, shaper alias-addressed, pipeline neither side chose.

**So state the protection narrowly and concretely, because that is what actually holds.** The carrier buys three
real properties, and they are worth having; it does not buy impossibility.

- **The binder admits a bare body** ⇒ it calls `AddProjectionAliasOverride(BareProjectionMemberKey, alias, tier)`
  in the *same commit block* as `AddProjection(projection)`, after every `return false` above it. So "emit gate
  open" and "the override exists" are the same event, not two events to keep ordered. **VERIFIED — MEASURED(T0)
  §1**, the inertness control: read side present, arm off ⇒ byte-identical failing-name set.
- **The binder declines** ⇒ the map is empty ⇒ site A yields `null` exactly as today ⇒ site B's `DocParameter`
  branch ⇒ `MarkNotNativelyRepresentable()` had already set `Route = Fallback` anyway. Byte-identical to
  `7af6e0da`. **VERIFIED**, same measurement.
- **A future edit deletes the binder arm** ⇒ degrades to the graceful fallback, never to the crash or to wrong
  data. Same property the VectorSearch slice bought with `hasUnboundVectorSearch` (CITED, that design §6.2).

**And then three separate mechanisms — not one invariant — keep the two divergences above out of the shipped
tree. This is the slice's real safety story:**

1. **The §7.3 `Distinct` narrowing** closes instance 1 by never letting a bare projection reach
   `TryBindDistinctFromProjection` at all. **MEASURED(T0) §5.3: dropping it hard-fails those 4 cases in `Native`
   AND `NativeOnly`.** It is not a scope preference; it is load-bearing for correctness.
2. **The §7.2 set-op-operand narrowing** closes the analogous composition hazard on the set-op route, and is
   equally load-bearing (§7.2, MEASURED(T0) §5.3 — 12 MQL diffs plus two spec cases flipping from *throws* to
   *answers*).
3. **The Task-1b fallback strip (§4.6)** closes instance 2 by making the fallback hand the shaper a shape the
   tier-1 alias can read: whole documents.

Anything **beyond** these three — a `Route` flip through some future path nobody has enumerated — is not
prevented by design, and this document no longer claims it is. What it is caught by is the §9.3 test plan's
parameterized-`Where` legs (which exercise the late fallback) plus the §7.2/§7.3 tripwire tests (which fail
loudly if either narrowing is removed).

---

## 4. The alias scheme

### 4.1 The rule, in one sentence

> **The projection alias is the leaf's own root-relative document path when it has one, and the reserved
> synthetic name `_v` when it does not.**

### 4.2 Why "the document path" and not a uniform `_v`

Because the shaper is built at **translation** time and is alias-addressed from then on, while whether a
`$project` is actually emitted is decided **later**. That is `Query/AGENTS.md`'s *GOVERNING HAZARD* for owned
array projections, and it applies verbatim here (CITED). When alias == document path, "read top-level element
`<alias>`" and "read the leaf at its document path" are **literally the same read**, so the shaper is correct
against a projected document *and* against the whole documents an `aggregate([])` fallback yields. A uniform
`_v` would be correct only on the native path.

Three fallback routes are reachable for a `Route == Projection` bare query. READ, with the third row's
disposition **CORRECTED by measurement**:

| Route | Reached when | Which shaper runs | What pipeline the shaper is handed | Alias sensitivity |
|---|---|---|---|---|
| push-down (`ExecuteProjectedQuery`) | `queryMode == DriverLinq` (the `Route == Projection` branch in `VisitProjectedQuery` is gated `queryMode != DriverLinq`, `:290-300`) **and** `ProjectionAnalyzer.CanPushDown` is true | identity (`(_, e) => e`, `:1025`) | the driver's own `$project`, aliased `_v` | **none** — the alias is never read |
| mixed (`MongoMixedProjectionBindingRemovingExpressionVisitor`) | same, but `CanPushDown` false — which any entity/collection leaf forces (`ProjectionAnalyzer.ContainsEntityReference`) | alias-addressed, via the **base** class's `CollectionShaperExpression` case (the mixed visitor overrides only `ProjectionBindingExpression`) | `aggregate([])` — `StripPushedDownSelect` removes the `Select` | **total**, and tier 1 is correct here — VERIFIED, MEASURED(T0) §3.3: the bare owned array under `DriverLinq` emits `aggregate([])` and returns correct values for all four array states |
| native-factory build failure under `Native` | `TryBuildNativeFactory` returns null inside `CompileShapedQuery` | the same alias-addressed DOM shaper | **CORRECTED: the driver's own `$project`, aliased `_v` — NOT `aggregate([])`** | **total, and MEASURED(T0) to be WRONG without §4.6** |

> **The third row is the design's most consequential error, and it is corrected here rather than annotated.**
> The original table said this route runs *"the same alias-addressed DOM shaper, **over `aggregate([])`**"*.
> **REFUTED — MEASURED(T0) §2.4, end to end.** `Route == NativeRoute.Projection` is decided at translation
> time, so `VisitProjectedQuery` takes the native branch and `CompileShapedQuery` builds the alias-addressed DOM
> shaper. `TryBuildNativeFactory` then fails (e.g. a renderer decline), is caught because mode ≠ `NativeOnly`,
> and the runtime helper translates `CapturedExpression` — **including the bare `Select`** — through the
> driver-LINQ bridge. **The fallback KEEPS the pushed-down `$project`, and the driver aliases a bare projection
> `_v`.** Captured MQL:
>
> ```
> aggregate([{ "$match" : { "Title" : { "$regularExpression" : … } } },
>            { "$project" : { "_v" : "$Title", "_id" : 0 } }])
> ```
>
> while the shaper reads element `Title`. **The read misses.** So tier 1's entire justification — "alias ==
> document path, so the shaper is correct against a whole document too" — was aimed at a row this fallback never
> hands it. §4.6 is the fix.
>
> **How ordinary the trigger is, because this is the reason it matters:** any `Where` the native *renderer*
> cannot emit. The measured one is a **parameterized `string.StartsWith`** (`"Only constant regex terms are
> natively representable"`) — captured local, ordinary code. MEASURED(T0) §2.4, default `Native`, ragged
> 5-row seed, design-as-originally-written: non-nullable `string` → **throws**; **nullable `string` → `null`**;
> **nullable `int` → `null`**; **owned array → `[]`**. Three of those four are **silent wrong data under the
> default mode**. The six spec cases that caught it are loud only because Northwind's `CustomerID` is
> non-nullable. **Control, MEASURED(T0): the hazard is NOT pre-existing** — the same query with a *wrapped*
> projection is correct at base and at head in all three modes, because the driver aliases a wrapped projection
> by member name, which coincides with the native alias. **Only a bare body gets `_v`.** So 3a introduces this,
> for exactly the shape the whole slice is about.

So the bare **array** case — the one the step-3 spike singles out — goes through the alias-sensitive *mixed*
route, and tier 1 is what makes it correct there (VERIFIED above). The third row needed a second mechanism.

### 4.3 Tier 1 — path-addressable leaves

Admitted when the translated leaf is:

- a `MongoFieldExpression` whose `ElementName` contains **no** `.` ⇒ `alias = field.ElementName`;
- a `MongoElementRefExpression` whose `Path` contains **no** `.` ⇒ `alias = ref.Path`. This covers the owned
  array leaf (whose path is `GetContainingElementName()`) and, incidentally, the VectorSearch `__score` leaf
  (whose path names an element the `$addFields` companion really writes — READ,
  `MongoVectorSearchScoreStage.ScoreField`).

Concretely: `Select(b => b.Title)` → `{ $project: { Title: "$Title", _id: 0 } }`;
`Select(o => o.OrderID)` where the PK's element name is `_id` → `{ $project: { _id: "$_id" } }` (READ,
`MongoPipelineFactory.RenderProject:317-329` adds the default `_id: 0` **only** when the body does not already
contain `_id`, so this is well-formed — and it matters, because `Select_scalar_primitive`, the step-3 spike's own
site-B probe, is exactly this shape).

**All of tier 1 is VERIFIED — MEASURED(T0) §5.4**, probe in all three modes: `Select(b => b.Title)` →
`{$project: {Title: "$Title", _id: 0}}`; `Select(b => b.Id)` (single-prop PK) → `{$project: {_id: "$_id"}}`,
well-formed with **no** `_id: 0` (so §4.3's `RenderProject` reading is VERIFIED); `Select(b => b.Tags)`
(primitive collection) correct for all four array states; `Select(b => b.Posts)` (owned collection) →
`{$project: {Posts: "$Posts", _id: "$_id"}}`, correct for all four array states, matching `DriverLinq`.

**The array-leaf constraint (step-3 spike Q3), stated honestly — and now with the mutation that proves it.**
`IsNativeArrayProjectionLeaf` requires `alias == navigation.TargetEntityType.GetContainingElementName()`
(READ, `NativeProjectionBinder.cs:562`). Under tier 1 we *choose* the alias to be that element name, so for a
bare body **that conjunct is vacuously satisfied** — it can never fail, because there is no user-chosen alias to
disagree with it. It is not therefore useless: it is what keeps the *wrapped* renamed-alias bug closed, and
reusing the same predicate for the bare case is what keeps emit and shaper admitting the same set. **What
actually does the work for a bare array is the sibling conjunct
`navigation.DeclaringEntityType == rootEntityType`** — a single top-level hop, hence a non-dotted path, hence
whole-document-readable. Say this in the as-built note; a reader who assumes the alias conjunct is protecting the
bare case will draw the wrong conclusion when widening it.

**And the cost of getting it wrong is MEASURED, not argued — MEASURED(T0) §5.1, mutation 1.** Force every bare
alias to `_v` while leaving `IsNativeArrayProjectionLeaf`'s alias conjunct in place (i.e. move the emit side,
leave the array shaper site behind), fully native, MQL `{$project: {_v: "$Posts", _id: "$_id"}}`:

```
Select(b => b.Posts)  [Native]     => [];[];[];[];[]        (correct: [h1|h2];[];[];[];[h9])
Select(b => b.Posts)  [NativeOnly] => [];[];[];[];[]
Select(b => b.Posts)  [DriverLinq] => [h1|h2];[];[];[];[h9]  (correct — different route)
```

**Five rows of silently empty collections, no exception, in the default mode** — the EF-358 coalesce turning the
missed `TypeAs` into an empty collection exactly as §6.1 predicts. This is why tier 1's alias is not a free
choice, and it is also why variant B (a uniform `_v` for every bare leaf) cannot admit the array at all (§4.6).

Ordering consequence inside the binder: the array branch of `TryTranslateLeaf` needs the alias as an *input*,
so for a bare body the alias must be derived **before** the call for that one shape. The design is:

1. provisional alias — `body is MaterializeCollectionNavigationExpression m` ⇒
   `(m.Navigation as INavigation)?.TargetEntityType.GetContainingElementName()`, else `_v`;
2. `TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], body, provisional, pendingLookups, out leaf,
   out isArrayLeaf)`;
3. **final** alias, derived from the *translated leaf*, not from the syntax:
   `MongoFieldExpression f => f.ElementName`, `MongoElementRefExpression r => r.Path`, anything else ⇒ `_v`.

Step 3 makes step 1 irrelevant except as the array branch's gate input, and the two agree by construction for
that branch (an `ArrayAliasProjectionExpression`'s path *is* the containing element name).

The rest of the commit block is unchanged from the wrapped path: `hasArrayLeaf` still drives the mandatory
owner-`_id` projection (READ, `:148-159` — an owned element with a shadow key reads its owner's key out of the
row it is handed, so a `$project` with no `_id` fails per row) and still sets `HasArrayProjectionLeaf`.

### 4.4 Tier 2 — computed leaves, its measured yield, and its one measured price

A count (`MongoSizeExpression`), a filtered count (`MongoFilteredSizeExpression`), an arithmetic leaf
(`MongoBinaryExpression`) — none is backed by a document element, so none has a path. Alias `_v`, matching the
driver-LINQ push-down's own name for a bare scalar (CITED: the `{ "$project" : { "_id" : 0, "_v" : null } }`
stage the step-3 spike quotes).

**Yield — CORRECTED. The original design said "up to +22"; the measured figure is +6.** MEASURED(T0) §2.3, by
failing-test-name set with tier 2 switched off: tier 1 alone is 74, tier 1 + tier 2 is 80. **Tier 2's whole
contribution is 6 cases, and they are identifiable by name** — 4 × `NorthwindSelectQueryMongoTest
.Projecting_count_of_navigation_which_is_generic_collection` / `…_generic_list` (a bare *reference*-collection
`.Count`) and 2 × `…Explicit_cast_in_arithmetic_operation_is_preserved` (a bare arithmetic leaf). The step-3
spike's family-B sizing (56 in the bucket, 22 sole-cause) does not convert at anything like that rate, because
most family-B bodies are constants, captured parameters, or leaf kinds `TryTranslateLeaf` still declines. **So
the trade on offer is 80 vs 74, not 100 vs 78.**

Price, stated rather than buried: **a tier-2 leaf is not whole-document-readable**, so on any alias-sensitive
fallback route it reads a missing element. Dispositions, all now MEASURED:

1. **`CanPushDown` is true for every tier-2 shape — antecedent VERIFIED, but the mitigation it was supposed to
   buy is REFUTED.** MEASURED(T0) §3.2: bare `.Count` under explicit `DriverLinq` emits
   `aggregate([{$project: {_v: {$size: "$Posts"}, _id: 0}}])`, i.e. the driver push-down with the identity
   shaper — so `CanPushDown` *is* true and the provider's `_v` alias *is* never read on that route. **But the
   dangerous route was never `DriverLinq`.** It is default `Native` with a **late native-factory failure**
   (§4.2's third row), where `CanPushDown` is never consulted at all. The original design offered mitigation 1 as
   the answer to tier 2's price; it answers a different question. The actual answer is §4.6's tier-conditional
   strip: **do not strip for a tier-2 leaf**, because on that route the driver's own alias genuinely *is* `_v`,
   so leaving the pushed-down `$project` in place is what makes the read hit. MEASURED(T0) §6.1 variant C: strip
   unconditionally and a tier-2 bare `.Count` behind a parameterized `Where` throws
   `InvalidOperationException: Document element '_v' is missing but required.`
2. **A collision guard.** Decline tier 2 when the root entity type has any property whose element name is `_v`.
   **Keep it, but record it honestly as defence-in-depth, not as a measured need** — MEASURED(T0) §3.2/§9
   "Known limitations": no fixture in Task 0 had a property stored at `_v`, so the guard was never exercised
   by anything measured. UNVERIFIED that it is reachable; Task 3 builds the fixture that exercises it.
3. **An explicit three-mode assertion** in every tier-2 functional test (`Native`, `DriverLinq`, `NativeOnly`),
   never a `Native`-only one — **and, added by this revision, a parameterized-`Where` leg too** (§9.3), because
   a constant-only `Where` never reaches the route in item 1.

**The one measured cost, and it is exactly what §7.4 predicted.** MEASURED(T0) §3.3/§6.2, on a ragged fixture
(populated / empty / **missing** / explicit **BSON null** / populated): under explicit `DriverLinq`,
`Select(b => b.Posts.Count)` throws `MongoCommandException` ("The argument to `$size` must be an array, but was
of type: missing") where the pre-3a client-side fold answered `0`. The driver renders a **bare** `$size`; native
renders `{$size: {$ifNull: ["$Posts", []]}}` and answers `0`. `Native` and `NativeOnly` are correct for every
array state. This is tier 2's **only** measured cost once §4.6 is in place, it is confined to the explicit escape
hatch, it is not a break (`MongoQueryMode` exists at no release tag — §11), and it makes the bare spelling agree
with the already-documented *wrapped* spelling (`Wrapped_count_projection_under_DriverLinq_works_for_present
_arrays_and_aborts_on_a_missing_array`) rather than disagree with it. **Record it; do not paper over it.**

**Recommendation, MEASURED(T0) §7: KEEP tier 2**, as part of variant D. It contributes 6 cases and introduces
zero silent wrong data, zero default-`Native` failures and zero `NativeOnly` regressions under variant D; the
genuine risk in this slice is entirely in tier 1's alias scheme and in the late fallback, both of which must be
fixed for tier 1 regardless. **The escape hatch still exists** if the owner rejects the `DriverLinq` change:
drop tier 2 and keep the strip **unconditional** — that is variant C, MEASURED(T0) at **74 wins, 1 residual, no
silence**. Dropping tier 2 buys no safety, only six fewer wins.

### 4.5 Does this really subsume EF-362? — yes for the scheme, no for one mechanism

**EF-362** (CITED, status §5 item 4 and `Query/AGENTS.md`'s slice-8 deferral list) is
`Select(b => new { b.Title, b.Home.Notes })`: an owned-collection array leaf reached through an `OwnsOne` hop.
It is declined because *"for a hop the alias is necessarily FLAT (`"Notes"`) while the document path is NESTED
(`"Home.Notes"`), so the alias-agreement conjunct is satisfied yet the invariant CANNOT hold"*. Its recorded
fix is *"a path-preserving `$project` emitting `{"Home.Notes": "$Home.Notes"}` … plus keeping the
document-path read"*.

Restate that in this design's vocabulary and it is **one override-table entry**:
`AddProjectionAliasOverride("Notes", "Home.Notes")`. Site A then aliases the `ProjectionExpression`
`"Home.Notes"`, `RenderProject` emits `{"Home.Notes": "$Home.Notes"}`, and site C hands
`IsNativeArrayProjectionLeaf` the string `"Home.Notes"` so the alias-agreement conjunct can be re-expressed
against the *full path* rather than the containing element name — which is the widening EF-362 wants, and it
is a binder-side change with **no further edit to any of the four read sites**. That is the concrete sense in
which the scheme is designed once.

**What is NOT delivered by 3a, READ and stated so nobody discovers it late:**

1. **The dotted read does not work today.** `BsonBinding.GetElementValue<T>` constructs
   `new BsonSerializationInfo(elementName, …)` — a single-segment name — and `TryReadElementValue` walks a
   path *only* when `ElementPath != null` (`BsonBinding.cs:260-290`). `CreateGetBsonArray` →
   `GetBsonArray(document, name)` is a bare `TryGetValue` (`:139-148`). So an alias containing a `.` would be
   looked up as a **literal key**, while MongoDB's `$project: {"Home.Notes": …}` produces a **nested**
   document. The good news is that `TryReadElementValue`'s path branch already exists; the missing piece is
   building the serialization info with an `ElementPath` when the alias is dotted. Small, contained, and
   EF-362's own work.
2. **UNVERIFIED:** that MongoDB renders `{"Home.Notes": "$Home.Notes"}` as nested output. This is ordinary
   `$project` semantics and I am confident of it, but I did not execute it, and neither did the step-3 spike (its
   Q4b says so in as many words) nor Task 0. Assigned to Task 4 (§12.3).
3. Therefore **3a's tier 1 explicitly requires a non-dotted path** and declines a bare `Select(b => b.Home.City)`.
   That decline is a *tripwire*, not an oversight: it is the shape EF-362 flips.

> **TASK 4 EXECUTED THE WIDENING. It works, and it SHIPPED — but two claims in this section were measured
> false and are corrected here rather than annotated around.**
>
> **(a) "EF-362 reduces to ONE override entry … with no further edit to any of the four read sites" — HALF
> TRUE, and the missing half was silent wrong data.** The four *alias-derivation* sites needed no edit, exactly
> as claimed. But the alias carrier has a **FIFTH reader** this document itself added — Task 1b's late-fallback
> strip — and §4.6's own reasoning was aimed only at the bare body, so its unit test pinned a named override as
> deliberately NOT stripping ("the driver's own alias for it is the member name, not `_v`, so there is nothing
> to strip"). The driver's alias BEING the member name is precisely the problem: on a late native-factory
> decline the driver keys the projection `Notes` while the shaper reads `Home.Notes`. MEASURED on a
> captured-local `string.StartsWith`: **empty collections, silently, under the default `Native` mode**, with the
> `DriverLinq` and `NativeOnly` legs of the same query correct. The fix is one line —
> `ShouldStripBareProjectionOnFallback` reads `HasDocumentPathAliasOverride` (any `DocumentPath`-tier override)
> instead of `BareProjectionTier` — but it is a fifth edit, and the design predicted zero. So the honest
> statement is: **EF-362 reduces to one override entry PLUS a tier-widening of the strip.**
>
> **(b) Item 1's "the missing piece is building the serialization info with an `ElementPath` when the alias is
> dotted" — TRUE FOR THE SCALAR READ, AND NOT THE ONE THE ARRAY LEAF USES.** An array leaf never reaches
> `GetElementValue`/`GetPropertyValueAtElement` at all: it goes through `CreateGetBsonArray` → `GetBsonArray`, a
> bare `TryGetValue` with no `BsonSerializationInfo` anywhere. So EF-362's array half needed a segment walk in
> `GetBsonArray`, not an `ElementPath`. The `ElementPath` gap this section describes is real and still open —
> it is what keeps a dotted owned SCALAR (`b.Home.City`) declining, and what makes
> `Select(b => new { b.Home.City, b.Home.Notes })` return a null `City` on the fallback it declines onto (a
> PRE-EXISTING shape, measured byte-identical at `f8464860`).
>
> **Item 2 (UNVERIFIED — does MongoDB render `{"Home.Notes": "$Home.Notes"}` as nested output?) is now
> VERIFIED:** it does. Measured directly, four rows: a present array nests as `{Home: {Notes: [...]}}`; a
> missing `Notes` yields `{Home: {}}`; a missing `Home` also yields `{Home: {}}`; an explicit null yields
> `{Home: {Notes: null}}`. That is what makes one dotted read correct against a projected document and an
> un-projected one alike.
>
> **Item 3's tripwire flipped as designed**, and the BARE dotted spelling (`Select(b => b.Home.Notes)`) is
> deliberately still declined — `TryDeriveDocumentPathAlias` keeps its non-dotted requirement, so tier 1's
> bare arm is untouched by EF-362.

**Net answer to owner ruling 3 (§0.1), and Task 0 leaves it standing exactly as written:** the alias/path
*decoupling* is designed once and delivered by 3a — **EF-362 reduces to one override entry** (corrected by
Task 4 above: one override entry *plus* the strip's tier widening) — while the
dotted-path *read* is one further, well-located change that belongs to EF-362. MEASURED(T0) §5.4 confirms the
tripwire half: a bare `Select(b => b.Home.City)` **declines**, correct under `Native`/`DriverLinq`, throws under
`NativeOnly` — i.e. the deferral is real and observable, and Task 4 has something to flip. The shaper-side gap
(the null `ElementPath`) is READ and unchanged by any measurement; it is **EF-362's gap, not 3a's**. I would
rather say that than claim EF-362 is covered and have the next agent find `TryGetValue("Home.Notes")` returning
nothing.

### 4.6 The late-fallback strip — variant D, and why the three alternatives lose

**This section is new. The original design did not have it, and without it 3a ships silent wrong data (§4.2).**

**The defect (READ + MEASURED(T0) §6).** `CompileShapedQuery` builds the shaper **first** and decides
native-vs-driver **second**. When `TryBuildNativeFactory` returns `null`, its runtime helper translates the
**full** `CapturedExpression` through `MongoEFToLinqTranslatingExpressionVisitor` and keeps the shaper that was
built for the *native* `$project`. Today that is harmless because every `Route == Projection` shape's native
aliases are member names, which is also what the driver picks. **A bare body is the first shape where the two
disagree** — the driver picks `_v`.

`StripPushedDownSelect` **already exists in the same file** (used by the mixed path, `:365`) and does exactly
what is needed.

**The fix:** in `VisitProjectedQuery`'s `Route == Projection` branch, on native-factory failure, strip the
pushed-down bare `Select` **iff the bare leaf is path-addressable** — i.e. iff
`Select.BareProjectionTier == ProjectionAliasTier.DocumentPath`.

- **Tier 1 ⇒ strip.** The fallback then yields whole documents, which the tier-1 alias reads correctly (that is
  the whole point of tier 1, §4.2).
- **Tier 2 ⇒ do not strip.** The fallback stays on the driver push-down, whose alias genuinely *is* `_v`, which
  is what the tier-2 shaper expects.

**Read the tier off the override, not off the alias string.** The spike's conditional was `alias != "_v"`, an
expedient it flags in its own known-limitations list. Sniffing the string re-creates a second, independently
derived copy of a fact the emit side already knows — which is precisely the failure mode §3 exists to remove.

**All four variants MEASURED(T0) §6.1, EF10 spec suite, both axes, re-baselined:**

| Variant | `NativeOnly` wins | default-`Native` residual | silent wrong data | bare array native |
|---|---:|---:|---|---|
| **A** — the original design (tier 1 doc-path + tier 2 `_v`, no strip) | 80 | **7** | **YES** (nullable scalar, nullable int, owned array) | yes |
| **B** — uniform `_v` for every bare leaf, array declined | 69 | 3 | no | **no** |
| **C** — A + strip unconditionally (tier 1 only) | 74 | 1 | no | yes — **but tier 2 throws** |
| **D** — A + strip only for a path-addressable leaf | **80** | **1** | **no** | **yes** |

Why the others lose:

- **B** is what §4.2 considered and rejected. Safe for scalars (MEASURED: the late fallback comes out correct
  because `_v` coincides with the driver's alias) but the bare **array** cannot be aliased `_v` at all —
  `IsNativeArrayProjectionLeaf` demands `alias == GetContainingElementName()`, and overriding it anyway is §4.3's
  mutation 1, five rows of silently empty collections. So B must decline the array, costing the array win and 11
  spec cases.
- **C** fixes tier 1 completely (MEASURED: nullable string, nullable int and owned array all correct on the
  late-fallback route) but breaks tier 2 — a `_v` alias has no document path to read off whole documents, so
  `Select(b => b.Posts.Count)` behind a parameterized `Where` throws `InvalidOperationException: Document element
  '_v' is missing but required.` Loud, because `int` is non-nullable, but still a working query turned into a
  throw. C remains the **escape hatch** if the owner drops tier 2 (§4.4).
- **D** is the reconciliation, and is the design.

**Variant D in full, MEASURED(T0) §6.2:** 80 `NativeOnly` Failed→Passed, 0 Passed→Failed, **1** default-`Native`
failure, 78 MQL baselines re-written across 13 files, **0** `#if` lines added or removed under `src/`. Every
functional probe correct in every mode across the ragged/late-fallback matrix, with the single exception of
§4.4's `DriverLinq` bare-`.Count` abort. **Two spec cases additionally flip usefully under D:**
`OrderBy_ThenBy_same_column_different_direction` returns to its committed EF-253 `"Duplicate element name
'_id'"` assertion (variants A/B turn it into `ArgumentOutOfRangeException`), so D also removes an
exception-shape change A/B would have had to re-baseline.

**The one residual, named so Task 2 is not surprised by it:** `NorthwindCompiledQueryMongoTest
.Multiple_queries` — `Assert.Contains("Unsupported cross-DbSet query between")` now sees `"Operation is not valid
due to the current state"`. That is an exception-**message** change on an **unsupported** cross-DbSet compiled
query (`AssertNoMultiCollectionQuerySupport`), so per the versioning rubric it is not contract and it is a
spec-override edit rather than a defect. **UNVERIFIED: why the message changes.** Assigned to Task 2, which must
understand it before landing, and to Task 5's as-built note.

---

## 5. What routes through `TryTranslateLeaf`, and what still declines

The bare-body arm calls the **existing** `TryTranslateLeaf` unchanged, so every leaf kind it already knows
becomes reachable from a bare body for free. Disposition after 3a:

| Bare body | Leaf kind `TryTranslateLeaf` yields | 3a | Alias | Verdict |
|---|---|---|---|---|
| `Select(b => b.Title)` | `MongoFieldExpression`, non-dotted | **native**, tier 1 | `Title` | **VERIFIED** (T0 §5.4) |
| `Select(o => o.OrderID)` (single-property PK) | `MongoFieldExpression`, `_id` | **native**, tier 1 | `_id` | **VERIFIED** — `{$project: {_id: "$_id"}}`, no `_id: 0` (T0 §5.4) |
| `Select(b => b.Tags)` (primitive collection — a mapped *property*, so a plain member access) | `MongoFieldExpression` | **native**, tier 1 | `Tags` | **VERIFIED**, all four array states (T0 §5.4) |
| `Select(b => b.Posts)` (owned entity collection) | `MongoElementRefExpression` via the array branch | **native**, tier 1 | `Posts` (+ mandatory `_id`) | **VERIFIED**, all four array states, matches `DriverLinq` (T0 §5.4) |
| `Select(e => EF.Property<double>(e, "__score"))` on a vector query | `MongoElementRefExpression("__score")` | **native**, tier 1 | `__score` | INFERRED at leaf level; the 4 `VectorSearch_with_projection` wins are a *bare field*, not the score leaf — VERIFIED as part of the 80 (T0 §2.3) |
| `Select(b => b.Posts.Count)` / `.Count()` / `.LongCount()` | `MongoSizeExpression` | **native**, tier 2 | `_v` | **VERIFIED** `Native`/`NativeOnly` all array states; `DriverLinq` **aborts** on missing/null — §4.4 (T0 §3.3) |
| `Select(b => b.Posts.Count(p => …))` | `MongoFilteredSizeExpression` | **native**, tier 2 | `_v` | **VERIFIED**, correct in all modes (T0 §5.4) |
| `Select(o => o.Price * o.Qty)` | `MongoBinaryExpression` (arithmetic top node) | **native**, tier 2 | `_v` | **VERIFIED**, correct in all modes (T0 §5.4) |
| `Select(c => c.Orders.Count)` (reference collection) | `MongoSizeExpression` over a `_lookup_<Nav>` + a pending lookup | **native**, tier 2 | `_v` | **VERIFIED** — 4 of tier 2's 6 spec wins are exactly this shape (T0 §2.3) |
| `Select(b => b.Home.City)` (owned hop) | `MongoFieldExpression`, **dotted** | **declines** | — (EF-362) | **VERIFIED** — correct under `Native`/`DriverLinq`, throws under `NativeOnly` (T0 §5.4) |
| `Select(b => b.Home.Notes)` (owned-hop array) | array branch, but `DeclaringEntityType != root` | **declines** (pre-existing) | — (EF-362) | INFERRED (not probed separately) |
| `Select(o => o.Customer.City)` | translator declines the nav hop | **declines** | — (joins) | INFERRED |
| `Select(o => 5)` / `Select(o => 0)` / a captured parameter | translates, but not an admitted node kind | **declines** | — (deliberate: CITED, a falsy constant *aborts* the aggregate with `Cannot do exclusion on field X in inclusion projection`) | **VERIFIED** — declines, correct values via fallback (T0 §5.4) |
| `Select(o => o.Name.ToUpper())`, date parts, `Convert`, `??`/`?:`, `ToList()`, array literals | translator declines | **declines** | — (3c) | INFERRED |
| `Select(x => x)` (bare entity) | never reaches here | **already native** — do not touch | — | unchanged; see below |
| **`Select(b => b.Title).Count()` / `.First()`** | n/a — a bare projection then a cardinality operator | **native — an INCIDENTAL WIDENING the original design assigned to 3d** | tier 1 | **VERIFIED — MEASURED(T0) §5.4: succeeds natively, correct.** §10 item 3 said this was 3d's. It is not; it arrives with 3a and needs a test (§9.3 test 17) |

**Bare entities: the explicit non-change.** CITED (step-3 spike Q3, MEASURED): `Select(x => x)` and
`Where(...).Select(x => x)` already succeed under `NativeOnly`, because `TranslateSelect`'s very first line
returns `source` unchanged for `selector.Body == selector.Parameters[0]`
(`MongoQueryableMethodTranslatingExpressionVisitor.cs:171-175`, READ) — the binder is never called, the
result CLR type is an entity, and `VisitShapedQuery` never enters `VisitProjectedQuery`. There are **zero**
bare-entity cases in the 881. 3a adds no arm that can match a bare `ParameterExpression` (READ, re-confirmed by
Task 0), and Task 2's verification includes a positive control asserting the bare-entity shape still goes native
(§9, test 14). **Task 0 could not settle this one positively** — MEASURED(T0) §5.4: its own fixture's
whole-entity query fails on an unrelated shadow-FK seeding gap, **identically at base and at head**, so the
positive control was inconclusive *from that fixture*. The step-3 spike's own MEASURED finding stands
unchallenged, and §9.3 test 14 must therefore use its **own clean flat fixture**, not the ragged one.

---

## 6. Alias mismatch: loud or silent, and where the protection actually is

### 6.1 What a mismatch does today — READ, and VERIFIED by two mutations

If the emitted `$project` key and the alias the shaper reads ever diverge:

| Leaf | What the read does | Verdict |
|---|---|---|
| non-nullable value type (`int` count, `int` scalar) | `BsonBinding.GetElementValue<T>` / `GetPropertyValueAtElement` throws `InvalidOperationException("Document element '<x>' is missing but required.")` (`BsonBinding.cs:264-269`) | **LOUD** |
| nullable value type or reference type (`string`, `int?`) | the same call returns `null` — the `type.IsNullableType()` arm | **SILENT** |
| array leaf (`CollectionShaperExpression`) | `TypeAs` yields null, the EF-358 coalesce turns it into an **empty collection**, no exception anywhere (CITED — this is exactly how slice 8's renamed-alias bug presented: *"0 elements, silently, under explicit `DriverLinq`"*) | **SILENT** |

**So: a read-side mismatch must be treated as SILENT.** It is loud only for the narrowest and least
interesting leaf type. **VERIFIED — MEASURED(T0) §5.1, twice, by mutation:**

- **Mutation 1 (deliberate).** Force the bare alias to `_v`, leave the array shaper site behind ⇒ **five rows of
  silently empty collections** in the default mode (§4.3 quotes the output). The `nullable value type` row of the
  table above is the mechanism.
- **Mutation 2 (accidental, and therefore the more convincing).** The design's **own** tier-1 alias, unmutated,
  on the late-fallback route ⇒ nullable string `null`, nullable int `null`, owned array `[]` (§4.2's box). Nobody
  had to break anything to produce this; the design as written produced it.

That is the whole justification for §3's single-carrier design rather than "two edits made carefully" — but it
is *not* on its own sufficient, which is what §6.2 now says.

### 6.2 There is NO fail-loud invariant. The original design's was REFUTED and is removed.

**The original design specified an assertion at site A** — quoted here because the correction only makes sense
against it:

> ```csharp
> // MongoQueryExpression.ApplyProjection, before the loop
> if (Select.IsBareProjection
>     && !(Select.Projection.Count >= 1
>          && Select.TryGetProjectionAlias(null, out var bare)
>          && Select.Projection.Any(p => p.Alias == bare)))
> {
>     throw new InvalidOperationException("… the emit side and the shaper side have diverged …");
> }
> ```
>
> — offered as the slice's safety story, and as *"mutation-verified in Task 1"*.

**REFUTED, and it is not a near miss. MEASURED(T0) §3.2/§5.1: the check never fired once across twelve runs —
six spec-suite runs and six functional-probe runs — including every single run that produced silent wrong
data.** It is *structurally incapable* of firing, because it compares the override string against
`Select.Projection`'s own aliases: **two facts the same code block writes, in the same block.** As specified it
is a tautology with a throw attached, and its "mutation-verified in Task 1" step would have passed **vacuously**
— hand-building a disagreeing `MongoSelectDefinition` proves only that `Any` works.

The divergence it was meant to catch is between that alias and (a) what the **driver** emits on a late fallback,
or (b) what the shaper is handed after a **`Route` flip**. Site A can see neither.

**Decision: remove it. Do not ship a decorative invariant.** A relocated version would have to compare the
shaper's expectation against *what the executed pipeline actually emits*, on every route the shaper can be handed
— which in practice means the pipeline is not available at the only place the check could live, so the honest fix
is to remove the ambiguity in the routes instead. That is §4.6.

**Where the protection actually is, in full, so nothing rests on a check that does not work:**

| Hazard | What closes it | Evidence |
|---|---|---|
| Bare array aliased away from its element name | `IsNativeArrayProjectionLeaf`'s alias conjunct + tier 1 choosing that alias (§4.3) | MEASURED(T0) §5.1 mutation 1 — five silently empty collections when bypassed |
| Late native-factory failure hands the shaper a `_v`-keyed `$project` | **Task 1b's tier-conditional strip** (§4.6) | MEASURED(T0) §6.1/§6.2 — variant A silent, variant D clean |
| `Route` flips to `GroupBy` after emit committed (`Distinct`) | **the §7.3 narrowing** | MEASURED(T0) §5.3 — 4 cases hard-fail without it |
| Projected bare set-op operand changes `$$ROOT` | **the §7.2 narrowing** | MEASURED(T0) §5.3 — 12 MQL diffs + 2 throws→answers without it |
| Anything else | the §9.3 functional net, **including its mandatory parameterized-`Where` legs** | §9.3 |

**Consequence for Task 1:** it loses one deliverable (the invariant) and one mutation step. It keeps the carrier
and the read-side alias derivations, and its inertness is already VERIFIED (§2.3).

---

## 7. Blast radius — what else opening this `default:` arm opens

`TryPopulateNativeProjection` is the **one shared entry point for every `Select.Projection` population site**
(CITED, `Query/AGENTS.md`, EF-347 note): a plain terminal `Select`, a trailing projection after a set op
(slice C2), and a projected set-op **operand** (slice C1, gated only on `IsPlainProjectedSelect`'s
`Route == Projection && Projection.Count > 0`). Opening the bare arm therefore opens all three. Each is
addressed below; the two narrowings are what keep 3a a boundary slice rather than a boundary-plus-composition
slice.

### 7.1 Trailing bare projection after a set op — **admit; VERIFIED safe**

`Union(A,B).Select(i => i.Value)` is currently deferred (CITED, AGENTS.md: *"a **bare-scalar** trailing
projection (`Select(i => i.Value)` — SP3 never pushes a bare scalar, set-ops or not)"*). Under 3a it becomes
native. **VERIFIED SAFE — MEASURED(T0) §3.4, and the mechanism is confirmed, not just the outcome.** All four
operators, all three modes, on a fixture where **two distinct entities share the same `Title`** (so whole-entity
dedup and projected-value dedup give *different* answers):

| query | `Native` | `DriverLinq` | `NativeOnly` |
|---|---|---|---|
| `Where(≤3).Union(Where(≥3)).Select(b => b.Title)` | `p2,p2,q0,r_missing,s_null` | identical | identical |
| `…Concat(…).Select(b => b.Title)` | `p2,p2,q0,r_missing,r_missing,s_null` | identical | identical |
| `…Intersect(…).Select(b => b.Title)` | `r_missing` | *(no oracle — throws)* | `r_missing` |
| `…Except(…).Select(b => b.Title)` | `p2,q0` | *(no oracle — throws)* | `p2,q0` |
| `Union(all, all).Select(b => b.Title)` | `p2,p2,q0,r_missing,s_null` | identical | identical |

All five answers are correct against the seed, and **the duplicated `p2` directly confirms the inferred
mechanism**: slice C2's dedup runs over **whole entities before** the trailing `$project`, so a trailing
projection cannot change set semantics — exactly why C2 is recorded as sound while C1 is not.
`Intersect`/`Except` have no driver-LINQ oracle (pre-existing) and their native answers are the arithmetically
correct ones. **Admit. No narrowing needed.** The 2 spec wins here are `Union_Select` and `Concat_with_pruning`.
Task 2 still pins it functionally (§9.3 test 8) so a future regression is loud.

### 7.2 Bare set-op **operand** — **deliberate narrowing, declined in 3a**

`Select(a => a.Name).Union(...)` is currently deferred for the *same* reason (`Projection.Count == 0`), and
unlike §7.1 it is **not** obviously safe: C1 dedups over the **projected** document, so a bare projected
operand changes what `$$ROOT` is. `Intersect`/`Except` have **no driver-LINQ oracle at all** (CITED), so a
wrong admission there produces the only answer available in any mode. Slice 8 hit precisely this and declined
via a `!HasArrayProjectionLeaf` conjunct on `IsPlainProjectedSelect`, having MEASURED
`Union 1 → 2 rows, Intersect 1 → 0, Except 0 → 1` (CITED).

**Follow the precedent exactly:** add `&& !Select.IsBareProjection` to `IsPlainProjectedSelect`. Disposition
after the narrowing is the pre-3a one — `Union`/`Concat` fall back gracefully, `Intersect`/`Except` hard-fail
in every mode. Pinned by a tripwire test in each of the four kinds, so a future widening flips a deliberate
test rather than passing unnoticed. Bare projected operands belong to **3d**.

**This narrowing is LOAD-BEARING, not a scope preference — MEASURED(T0) §5.3.** Dropping it (together with
§7.3): default `Native` goes **85 → 109** failures, **+24** Passed→Failed, and `NativeOnly` gets **net worse**.
Attributable to this narrowing specifically: **12 cases become MQL diffs** (`Union_non_entity`,
`Concat_non_entity`, `Select_Union`, `Union_over_OrderBy_Take1/2`, `Union_over_OrderBy_without_Skip_Take1/2`,
`Union_over_column_column`) **and `Intersect_non_entity` / `Except_non_entity` flip to
`Assert.ThrowsAny() Failure: No exception was thrown`** — i.e. they now *return an answer* where the spec asserts
a throw, on the two operators with **no** driver-LINQ oracle, and the spec does not check the answer. That is the
exact slice-8 hazard (`Union 1→2 / Intersect 1→0 / Except 0→1`) arriving by this door.

**Functional probes with the narrowing ON confirm the pre-3a disposition is preserved (MEASURED(T0) §5.3):**
`Union`/`Concat` bare operands return correct values under `Native`/`DriverLinq` and throw
`NativeTranslationNotSupportedException` under `NativeOnly`; `Intersect`/`Except` bare operands hard-fail in every
mode. That is what §9.3 test 15 must assert.

### 7.3 Bare `Distinct` — **deliberate narrowing, declined in 3a**

`Select(o => o.Country).Distinct()` is currently deferred because `TryBindDistinctFromProjection`'s
`Projection.Count == 0` guard rejects it (CITED). Under 3a `Projection.Count == 1`, so it would newly bind a
degenerate `$group`. `TryBindDistinctFromProjection` already carries four provenance-shaped declines of exactly
this kind (`Grouping != null`, `UnwindSource != null`, `OperandsProjected`, `HasDefaultKeySerialization`). Add a
fifth: decline when `select.IsBareProjection`. Bare `Distinct` composition is 3d's work.

**The original design said of admitting it: *"It might well work — the flatten preserves aliases."* MEASURED
FALSE (T0 §5.3), and this is the same defect §3.3 instance 1 describes.** Dropping this narrowing hard-fails
**4 cases** — `Distinct_Scalar` and `OrderBy_Distinct`, `async: False/True` each — with
`ArgumentException: Expression of type 'QueryingEnumerable`2[BsonDocument,BsonDocument]' cannot be used …`, in
`Native` **and** `NativeOnly`, **from a base state of passing under `Native`**. Mechanism:
`TryBindDistinctFromProjection` clears `Projection`, installs a `Grouping`, `Route` flips to `GroupBy`, and site
A's own `Route == Projection` conjunct reverts the alias to `null` *after* the emit side committed. So this is
not a scope choice deferred for tidiness — **it is one of the two things standing between this slice and a
measured regression**, and §9.3 test 16 is its tripwire.

### 7.4 **Tier 2 changes the shaper in EVERY mode — MEASURED, no longer a prediction**

`MongoSelectDefinition.Route` is computed at **translation** time and is **mode-independent**. Two shaper-side
arms are gated on `Route == NativeRoute.Projection` and therefore change behaviour under `DriverLinq` too the
moment the emit gate opens for a bare body (READ, `MongoProjectionBindingExpressionVisitor.cs:423-429` and
`:181-188`):

- **the count arm.** Today a bare `Select(b => b.Posts.Count)` is rebuilt by the `Queryable` switch's EF-357
  arm into a client-side `Enumerable.Count` over a `CollectionShaperExpression`, `CanPushDown` is false, the
  mixed path strips the `Select`, the pipeline is `aggregate([])` and the count is folded in process — which
  is why it is currently correct for **every** array state including missing/null (CITED, AGENTS.md and
  status §4). Under 3a it is registered as a projection member instead, in every mode. Under `Native` it goes
  native and is correct (the `$size` is `$ifNull`-wrapped). Under **`DriverLinq`** it very likely becomes a
  push-down and inherits the **already-documented** wrapped-count divergence: the driver renders a **bare**
  server-side `$size`, which `MongoCommandException`s on a missing or explicitly-null array where the current
  client-side fold answers `0` (CITED, `Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_aborts_on_a_missing_array`).
- **the array arm** (`TryBindNativeArrayProjection`) — but that one is *safe* by §4.2/§4.3, because tier 1's
  alias is the element name and the mixed path reads it correctly off a whole document.

**This section was INFERRED and is now VERIFIED — MEASURED(T0) §3.3, and it is exactly right, including the
"very likely".** Base MQL for a bare `.Count`, in both `Native` and `DriverLinq`: `aggregate([])` — the
client-side fold, confirmed. Head MQL: `Native` = `{$project: {_v: {$size: {$ifNull: ["$Posts", []]}}, _id: 0}}`;
`DriverLinq` = `{$project: {_v: {$size: "$Posts"}, _id: 0}}` — **no `$ifNull`, hence the abort.** Full
disposition, ragged fixture (populated / empty / **missing** / explicit **BSON null** / populated):

| shape | mode | base | head (variant D) |
|---|---|---|---|
| `Select(b => b.Posts.Count)` | `Native` | `2,0,0,0,1` | `2,0,0,0,1` |
| | `DriverLinq` | `2,0,0,0,1` | **`MongoCommandException`** |
| | `NativeOnly` | throws (decline) | `2,0,0,0,1` |
| `Select(b => b.Posts)` | `Native` | `[h1\|h2];[];[];[];[h9]` | same |
| | `DriverLinq` | same | same |
| | `NativeOnly` | throws (decline) | same |

**The array arm's safety claim is VERIFIED too:** the bare owned array under `DriverLinq` emits `aggregate([])`
(the mixed client-side shaper over whole documents) and returns correct values for all four array states —
precisely because tier 1's alias is the element name (§4.2/§4.3 VERIFIED for that route).

It is not a break per the rubric (`MongoQueryMode` does not exist at any release tag, so `DriverLinq` is
unobservable to an upgrading consumer — CITED VectorSearch design §10), and the resulting behaviour matches the
wrapped spelling, i.e. it makes two spellings of the same query agree rather than disagree. But it is a real
change to the escape hatch's behaviour for a shape that works today, and **the owner should see it before Task 3
lands**. **If it is unacceptable, drop tier 2 from 3a** and take variant C — **74 instead of 80**, not "78
instead of 100" (§4.4, §4.6).

### 7.5 Not affected — checked and stated

- **`SelectMany` trailing projections** bind through `NativeSelectManyBinder.TryBindTransparentIdentifierProjection`,
  a different binder that this arm does not touch (READ).
- **`GroupBy(key).Select(aggregate)`** reaches the grouped branch of `TranslateSelect` via
  `GroupByShaperExpression` and never `TryPopulateNativeProjection` (READ).
- **Composed slot operators after a bare projection** (`Select(o => o.Name).Skip(1).Take(2)`) record into
  `PipelineOps` and the lowerer appends `Projection` **last**, so paging runs before the `$project` — the same
  ordering wrapped projections already have, and 1:1 with respect to rows (READ, `MongoSelectLowerer`).
  **VERIFIED — MEASURED(T0) §5.4:** `Where`/`OrderBy`/`Skip`/`Take` then a bare `Select` goes native and is
  correct.
- **NOT unaffected, and the original design had this in the wrong slice: a CARDINALITY operator after a bare
  projection.** `Select(b => b.Title).Count()` / `.First()` **succeeds natively and correctly** — MEASURED(T0)
  §5.4. §10 item 3 listed `Select(...).Count()` among the 104 cases *declined* by §7.2/§7.3 and deferred to 3d;
  that is wrong for the cardinality half. It is an **incidental widening that arrives with 3a**, it is not
  narrowed by either §7.2 or §7.3 (neither guard is on the cardinality path), and it therefore needs a test of
  its own — §9.3 test 17. `Select(...).Union(...)` and `Select(...).Distinct()` remain 3d's, per §7.2/§7.3.
- **The one-pass streaming materializer** is not involved: the `Route == Projection` branch calls
  `CompileShapedQuery(..., allowStreaming: false)` (CITED step-3 spike Q4c, re-READ at `:294-301`). 3a widens the set
  of native-but-non-streaming queries, which is a small allocation cost against nothing and a note for SP7
  Phase 2, not a blocker.

---

## 8. Task breakdown — one subagent per task

> Every task: both `MONGODB_URI` and `ATLAS_URI` **unset** (TestContainers boots `mongodb/mongodb-atlas-local`
> so the Atlas-gated VectorSearch cases run for real); `dotnet test` **redirected to a file**, never piped
> through `tail`/`head`; its **own uniquely-named scratchpad subdirectory**; no other agent's scratchpad or
> worktree touched. Every task ends with the full solution green on **EF8, EF9 and EF10**
> (`dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` etc.), and
> `git diff <base> HEAD -- src/ | grep -c '^[+-].*#if'` ⇒ **0**, plus a direct grep of every **new** file
> (the tracked-file diff misses those).
>
> **Baseline at `7af6e0da`/`69a65d31`, VERIFIED by Task 0 and compared as a failing-test-name SET on both axes
> thereafter** (MEASURED(T0) §1): EF10 spec, default `Native` **4593 passed / 0 failed / 17 skipped**;
> `MONGODB_EF_NATIVE_ONLY=1` **2352 passed / 2241 failed / 17 skipped**. Failing names are unique (2241 distinct
> names for 2241 failures), so name-set comparison is well-defined.
>
> **When bucketing failures by message, classify `Assert.Throws`/`Assert.ThrowsAny` FIRST** — they quote the
> inner exception. The step-3 spike MEASURED that the naive order over-counts the projection bucket by **149**
> (1030 vs 881).
>
> **And bucket by MESSAGE TRANSITION, not just by pass/fail — the whole win is invisible otherwise.**
> MEASURED(T0) §2.1: because `AssertMql` runs under `NativeOnly` too, a case that stops declining fails at its
> committed MQL assertion instead. A name-set diff alone reports **2** wins where there are **80**. Every
> comparison below must classify `NativeTranslationNotSupportedException → MQL string diff` as a **win**.

**Ordering: Task 1 → Task 1b → Task 2 → Task 3 → Task 4 → Task 5.**

**Ordering rationale — CORRECTED.** Task 1 lands the *read* side while it is inert (**VERIFIED**, §2.3); Task 1b
lands the fallback strip, also inert (nothing sets `IsBareProjection` until Task 2); Task 2 opens the emit gate.
**No commit ever has the emit gate open with the read side unchanged, and no commit ever has the emit gate open
without the strip.** The original design justified that by §3.3's "the measured-worse state is unrepresentable"
— **REFUTED** (§3.3). The correct justification is weaker but sufficient: it holds **because the narrowings and
the strip are in place before the gate opens**, in that order, by construction of the task sequence. It is a
property of the ordering, not of the mechanism.

### Task 0 — DONE. Report: `docs/superpowers/specs/2026-08-06-step3a-task0-spike-findings.md`

Measured the complete change in throwaway worktrees against a base at `69a65d31`, both axes, by failing-test-name
set with message-transition bucketing. **Outcome: five of this design's claims refuted, and a shippable shape
found (variant D) that the design did not contain.** All five corrections are folded into the sections above; the
report is authoritative on every number. Its own **known limitations**, each of which becomes a later task's job:

- **EF8 and EF9 were not run.** Every task below still ends with the full solution green ×3.
- **The functional and unit suites were not run in full** — only the probe filter. **UNVERIFIED: variant D's
  effect on `NativeArrayProjectionTests`, `NativeOwnedCollectionCountTests` and `NativeSetOpsTests`.** Assigned
  to Tasks 2 and 3, which must run those three classes explicitly and not just the new file.
- The bare-entity positive control was inconclusive from Task 0's own ragged fixture (its §5.4).
- The tier-2 `_v` collision guard was never exercised (no fixture stored a property at `_v`). Assigned to Task 3.
- Variant D's conditional was `alias != "_v"`, a spike expedient. **The implementation must carry the tier as
  data on the override** (§3.1, §4.6).

### Task 1 — the alias carrier and the read side, inert

`MongoSelectDefinition`'s override table (with the **sentinel key** and the **tier**, §3.1) + writer +
`TryGetProjectionAlias` + `IsBareProjection` + `BareProjectionTier`; `ApplyProjection`'s alias derivation
(site A); `TryBindNativeArrayProjection`'s alias derivation (site C). **No invariant check — §6.2.** Nothing
writes the table, so behaviour is byte-identical.

*Verify:* unit tests that hand-build a `MongoSelectDefinition`/`MongoQueryExpression` — (a) with an empty table,
`ApplyProjection` produces a null alias exactly as before; (b) with an override, the `ProjectionExpression`
carries it; (c) with `Route != Projection`, the override is ignored; (d) the array site reads the override;
(e) the tier round-trips through `BareProjectionTier`. Full solution green ×3. **EF10 spec suite byte-identical
on both axes, by name set** — this is the one acceptance criterion in the slice that is *exactly* "zero diffs",
and it is VERIFIED achievable (MEASURED(T0) §1, the inertness control).

**Mutation:** the original design's required mutation is **deleted** along with the invariant it tested — it
would have passed vacuously (§6.2). Replacement, and it is a real one: **set an override, then flip `Route` away
from `Projection`** ⇒ test (c) must go red if the `Route` conjunct is removed.

### Task 1b — the late-fallback strip (NEW; inert until Task 2)

**This task did not exist in the original design, and Task 2 ships silent wrong data without it.**

One conditional in `MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery`'s `Route == Projection`
branch: when `TryBuildNativeFactory` returns `null`, call the existing `StripPushedDownSelect` **iff**
`Select.BareProjectionTier == ProjectionAliasTier.DocumentPath`. §4.6. Read the tier off the override; do **not**
sniff the alias string.

**Inert on its own** — nothing sets `IsBareProjection` until Task 2 — so it lands in the same "no behaviour
change" phase as Task 1.

*Verify:* EF10 spec suite byte-identical on both axes, by name set (it must be, being unreachable). Unit test
that the condition reads the tier and not the alias string. Full solution green ×3. **The behavioural proof of
this task lives in Task 2's parameterized-`Where` legs** (§9.3) — Task 1b cannot be proven functionally in
isolation, and the task report must say so rather than claim coverage it does not have.

### Task 2 — open the emit gate for tier 1

`TryPopulateNativeProjection`'s `default:` arm; the three-step alias derivation of §4.3; admit
`MongoFieldExpression` (non-dotted) and `MongoElementRefExpression` (non-dotted) only — **return `false` for
every other leaf kind**, including the computed ones. Register the override with tier `DocumentPath`. The two
narrowings (§7.2 `IsPlainProjectedSelect`, §7.3 `TryBindDistinctFromProjection`). First slice of
`NativeBareProjectionTests` (§9 tests 1–8, 13–17, **each with its parameterized-`Where` leg**).

*Verify:* EF10 spec, `MONGODB_EF_NATIVE_ONLY=1`, **by failing-test-name set with message-transition bucketing**
versus a base worktree — expect **74** Failed→Passed (72 "stopped declining" + the strip's 2) and **nothing** to
move the other way.

**Acceptance criterion on the default-`Native` axis — REPLACED. The original one is unmeetable.** It said
*"still 4593/0/17 with **zero** `AssertMql` baseline diffs (any diff is a finding, not a re-baseline, until
explained)"*. **MEASURED(T0) §5.4: tier 1 rewrites 78 `AssertMql` baselines across 13 spec files.** The criterion
is now:

> **The `AssertMql` diffs are exactly the `_v` → alias rename, and they are ENUMERATED in the task report.**
> Uniformly `{$project: {_v: "$X", _id: 0}}` → `{$project: {X: "$X", _id: 0}}`, plus `$sort`-ordering diffs on a
> handful. **Any diff that is not of that form is a finding, and stops the task.** Re-baseline with
> `EF_TEST_REWRITE_BASELINES=1`, then the axis must be back to **4593/0/17** except for the **one** known
> residual, `NorthwindCompiledQueryMongoTest.Multiple_queries` (§4.6) — which must be **explained**, not merely
> re-baselined.
>
> **The 13 files are NOT named by the Task-0 report** — it counts them but does not enumerate them. Task 2
> enumerates them in its own report. UNVERIFIED until then; do not guess the list.

Full solution green ×3, **and the three classes Task 0 did not run: `NativeArrayProjectionTests`,
`NativeOwnedCollectionCountTests`, `NativeSetOpsTests`.**

**Mutations, all required:**
(a) revert site A's alias derivation while leaving the binder arm ⇒ the functional tests must fail — **but note
honestly that this failure is LOUD only for a non-nullable leaf** (MEASURED(T0) §5.1), so the test that catches it
must be a non-nullable one or must assert values, not absence-of-throw;
(b) change tier 1's alias from the element name to a literal `"_x"` ⇒ the bare-array `DriverLinq` test must go red.
**This is *the* mutation, and it is MEASURED to work** — MEASURED(T0) §5.1 mutation 1 produced five silently empty
collections;
(c) delete the §7.2 narrowing ⇒ the four set-op operand tripwires go red — MEASURED(T0) §5.3 confirms 12 spec
cases plus two throws→answers move, so this mutation is known live;
(d) **NEW: remove the Task-1b strip** ⇒ every parameterized-`Where` leg of tests 1/3/4 goes red (nullable scalar
`null`, owned array `[]`). Without this mutation Task 1b is untested;
(e) **NEW: delete the §7.3 `Distinct` narrowing** ⇒ test 16 goes red, and the EF10 spec `Distinct_Scalar` /
`OrderBy_Distinct` cases go red on **both** axes (MEASURED(T0) §5.3, 4 cases).

### Task 3 — tier 2 (computed bare leaves)

Widen the bare arm's node-kind gate to `MongoSizeExpression`, `MongoFilteredSizeExpression` and the arithmetic
`MongoBinaryExpression`, aliased `_v`, registered with tier `Synthetic` (so Task 1b's strip does **not** fire for
them — §4.6), plus the `_v` collision guard. `NativeBareProjectionTests` tests 9–12, **each with its
parameterized-`Where` leg, whose expectation differs from tier 1's: a tier-2 leaf must come back CORRECT on the
late fallback *without* the strip** (§9.3). **A separate commit from Task 2** because its fallback disposition is
different (§4.4) and because it changes the `DriverLinq` shaper (§7.4) — keeping the two separable is what lets
tier 2 be dropped for variant C without unpicking tier 1.

*Verify:* `NativeOnly` name-set delta = **exactly the 6 named cases** of §4.4 (4 × `Projecting_count_of_navigation
_which_is_generic_collection`/`…_generic_list`, 2 × `Explicit_cast_in_arithmetic_operation_is_preserved`) — not
"family B", which MEASURED(T0) §2.3 shows does not convert. Default `Native` unchanged. **An explicit `DriverLinq`
leg for every tier-2 test**, over a seed with a present, an empty, a **missing** and an **explicitly-null** array
— and that leg must **assert the `MongoCommandException`** for a bare `.Count` on the missing/null rows (§7.4,
MEASURED), not paper over it. Record it in the as-built note and in `docs/native-query-status-EF-322.md` §4. Full
solution green ×3, plus the three classes named in Task 2.

**Mutations:** (a) widen the node-kind gate to plain `TryTranslateValue` success ⇒ a bare `Select(o => 0)` test
must go red (CITED: a falsy constant aborts the aggregate); (b) remove the `_v` collision guard ⇒ the collision
test goes red — **and this requires BUILDING the fixture that stores a property at element `_v`, which Task 0
never had** (§4.4 mitigation 2 is defence-in-depth, UNVERIFIED as reachable until this test exists); (c) make
Task 1b's strip unconditional ⇒ the tier-2 parameterized-`Where` leg goes red with `Document element '_v' is
missing but required` (MEASURED(T0) §6.1 variant C).

### Task 4 — EF-362 readiness, timeboxed

Not "ship EF-362". Establish, by execution, whether the dotted widening is the one-conjunct change §4.5
predicts: drop tier 1's non-dotted requirement behind a local patch, add the `ElementPath`-aware serialization
info to `BsonBinding`, and measure `Select(b => new { b.Title, b.Home.Notes })` and
`Select(b => b.Home.City)` in all three modes. **Either** it works, in which case ship it as EF-362 with its
own tests and update the ticket; **or** it does not, in which case revert and record precisely what blocks it
on EF-362. Do not leave a half-widened gate in the tree.

*Verify:* whichever way it goes, the tripwire tests from Task 2 that pin the dotted **decline** must be
correct for the shipped state, and the EF-362 ticket must end this task with a measured answer rather than a
plan.

### Task 5 — sweeps, documentation, break check

The remaining functional tests; the `Query/AGENTS.md` as-built note; corrections to
`docs/native-query-status-EF-322.md` §4 (three entries move: bare-scalar owned count, bare array projection, the
`VectorSearch` residual count 16 → 12), §5 items 2–4, and §9.1 row 1; the release-tag break check.

**The as-built note must carry all of these, and the first six are things Task 0 found that the original design
either got wrong or did not know:**

1. **The alias/document-path coupling on the late-fallback route** — §4.2's corrected third row (the fallback
   keeps the pushed-down `$project`, keyed `_v`) and the tier-conditional strip that answers it. This is the one
   a future editor is most likely to undo.
2. **The FOURTH alias site**, `MongoMixedProjectionBindingRemovingExpressionVisitor:91` — with the "inherits site
   A, no edit needed, here is why" sentence, plus the two READ facts of §2.2 that matter to anyone widening it.
3. **Both narrowings are load-bearing, with their measured costs** (§7.2: +12 MQL diffs and 2 throws→answers;
   §7.3: 4 cases hard-failing from a passing base). Not scope preferences.
4. **`NorthwindCompiledQueryMongoTest.Multiple_queries`** — the one residual, an exception-*message* change on an
   unsupported cross-DbSet compiled query (§4.6).
5. **The incidental widening of bare-`Select`-then-`Count()`/`First()`** (§7.5) — corrected out of 3d.
6. **Tier 2's `DriverLinq` cost** (§7.4/§4.4) — the ragged-array abort, in `Query/AGENTS.md` **and**
   status §4.
7. §4.3's honest statement that the array alias conjunct is **vacuous** for a bare body, and that the
   `DeclaringEntityType == rootEntityType` sibling is what does the work.
8. §6.1's loud-vs-silent table, **and** §6.2's plain statement that there is no fail-loud invariant and where the
   protection is instead. Do not let a future reader look for the invariant this design once specified.
9. The **78 re-baselines across 13 files** as an intended, rubric-permitted change — enumerated by Task 2.

*Verify:* full EF10 spec suite on both axes versus a base worktree, by name set; `gh release list --limit 100
--json tagName` to re-derive `v10.0.2` / `v9.1.2` / `v8.4.2` (a clone's local tags go stale — `git fetch
--tags` first), then `git show <tag>:<path>` for every touched file that exists at the tag. Zero `#if`
delta under `src/`, new files checked directly. Full solution green ×3.

---

## 9. Test plan

### 9.1 How every "goes native" claim is proven

**`MONGODB_EF_NATIVE_ONLY=1` (or `UseQueryMode(MongoQueryMode.NativeOnly)`) succeeding. Nothing else.** MQL
shape cannot prove it — the native and driver-LINQ `$project` for a pushed-down projection are structurally
identical, and for a bare scalar the driver's own push-down emits a `$project` too. Where an MQL assertion
appears in this file it is for the *emitted alias* (a `$project` key, which is the thing §6 is about) and its
comment must say in as many words that it is **not** a routing proof — the precedent set by reference
`Include` after EF-370 and by `NativeVectorSearchTests.Vector_search_emits_the_stage_first`.

### 9.2 Unit tests

`tests/.../UnitTests/Query/NativeTranslation/`:

- `MongoSelectDefinitionProjectionAliasTests` — the table's write-once/read-many contract; the **sentinel key**
  (including that `TryGetProjectionAlias(null, …)` maps onto it); `IsBareProjection`; `BareProjectionTier`
  round-trip; `Route`-gating.
- `MongoQueryExpressionApplyProjectionTests` — the site-A cases of Task 1. **No invariant test — §6.2.**
- `NativeProjectionBinderBareBodyTests` — admit/decline per §5's table, asserting the **derived alias** and its
  **tier**, not just the boolean; the tier boundary; the `_v` collision guard.
- `MongoShapedQueryCompilingExpressionVisitorFallbackStripTests` (Task 1b) — that the strip decision reads
  `BareProjectionTier` and **not** the alias string.

### 9.3 `tests/.../FunctionalTests/Query/NativeBareProjectionTests.cs` (new)

The bare array and bare `.Count` wins are invisible to Northwind (CITED, status §5), so this file *is* the
measurement for them. Shape follows the sibling `Native*` files: `[XUnitCollection("QueryTests")]`,
`TemporaryDatabaseFixture`, `[Theory]` over `MongoQueryMode`.

> ### THE MOST IMPORTANT CORRECTION IN THIS REVISION
>
> **Every functional test in the original §9.3 used a CONSTANT-ONLY `Where`. That is precisely the case where the
> native factory succeeds and the late fallback NEVER HAPPENS.** MEASURED(T0) §2.4 and §8: as written, *the
> slice's own tests would all have been green while the shipped code silently returned nulls*. The design's
> control row proves it — `Where` with a **constant** prefix returns correct values under variant A, the same
> query with a **captured** prefix returns `<null>`.
>
> **Therefore, NON-OPTIONALLY: every bare leaf kind gets a parameterized-`Where` leg.** The cheapest measured
> trigger is a **captured local in a `string.StartsWith`** — the native renderer declines it (`"Only constant
> regex terms are natively representable"`), `TryBuildNativeFactory` returns `null`, and the query takes the late
> fallback under the **default `Native`** mode. No other shape in this test plan reaches that route.
>
> **The two tiers need DIFFERENT expectations on that leg, and this is not symmetric:**
>
> | Leaf tier | Parameterized-`Where` leg asserts | Why |
> |---|---|---|
> | **Tier 1** (scalar, PK, primitive collection, owned collection) | correct values under `Native` — reached via the **stripped** fallback over whole documents | Task 1b strips, so the tier-1 alias == the document path and the read hits (§4.6) |
> | **Tier 2** (`.Count`, filtered count, arithmetic) | correct values under `Native` — reached via the **un-stripped** driver push-down | Task 1b must **not** strip; the driver's own alias *is* `_v` (§4.4, §4.6 variant C) |
>
> A test that asserts only "does not throw" is worthless here: the tier-1 nullable cases return `null`, and the
> array case returns `[]`, with **no exception at all**. **Assert values.** And include at least one
> **non-nullable** leaf and one **nullable** leaf per tier-1 test, because the non-nullable one is the only leaf
> kind that fails loudly (§6.1) and a suite of only non-nullable leaves would have caught the bug by luck.

**Seed rule, load-bearing (unchanged, and Task 0 used exactly this):** every array-valued fixture carries the
four array states — populated, empty, **field missing**, **field explicitly BSON null** — because that is the axis
on which the count and array paths have historically diverged (EF-357/EF-358 and the wrapped-count `DriverLinq`
abort). **Added by this revision:** the seed also needs a **nullable string**, a **nullable int**, and (for test 8)
**two distinct entities sharing one `Title`** so whole-entity dedup and projected-value dedup give different
answers (that is what made §7.1's verification non-vacuous — MEASURED(T0) §3.4).

| # | Test | Pins | Parameterized-`Where` leg | Tier |
|---|---|---|---|---|
| 1 | `Bare_scalar_projection_goes_native` | ordered values; `NativeOnly` succeeds | **REQUIRED** — non-nullable **and** nullable string **and** nullable int leaves, values asserted | 1 |
| 2 | `Bare_primary_key_projection_goes_native` | the `_id` alias case — values, and the `$project` body contains `_id` and **no** `_id: 0` (VERIFIED, T0 §5.4) | **REQUIRED** | 1 |
| 3 | `Bare_primitive_collection_projection_goes_native` | element-by-element values across all four array states | **REQUIRED** — assert elements, never `!= null` | 1 |
| 4 | `Bare_owned_collection_projection_goes_native` | nested element values (never `!= null`), across all four array states | **REQUIRED** — this is the leg that returned `[];[]` under variant A | 1 |
| 5 | `Bare_owned_collection_projection_matches_driver_linq` | **the §6.1 silent case.** Same query under explicit `DriverLinq` returns the identical nested values — the test Task 2 mutation (b) must break | n/a (`DriverLinq` never takes the late fallback) | 1 |
| 6 | `Bare_owned_collection_projection_emits_the_element_name_alias` | MQL: the `$project` key is `Posts`, and `_id` is emitted alongside. **Captioned as not a routing proof** | n/a | 1 |
| 7 | `Bare_projection_composed_with_filter_sort_and_paging_goes_native` | ordered values after `Where`/`OrderBy`/`Skip`/`Take` (VERIFIED, T0 §5.4) | **REQUIRED** — the `Where` here becomes the parameterized one | 1 |
| 8 | `Bare_projection_after_a_set_operation_goes_native` | §7.1, all four set operators, **all three modes**; the duplicate-`Title` row asserted explicitly, since it is what proves whole-entity dedup precedes the trailing `$project` (VERIFIED, T0 §3.4) | not required (no oracle for `Intersect`/`Except`; §7.1 is verified) | 1 |
| 9 | `Bare_owned_collection_count_projection_goes_native` | counts across all four array states; all three modes asserted separately — **and the `DriverLinq` leg must assert `MongoCommandException` on the missing/null rows** (§7.4, MEASURED) | **REQUIRED**, tier-2 expectation | 2 |
| 10 | `Bare_filtered_count_projection_goes_native` | same, with a predicate | **REQUIRED**, tier-2 expectation | 2 |
| 11 | `Bare_arithmetic_projection_goes_native` | computed values; integer-`Divide` still declines (Guard A) | **REQUIRED**, tier-2 expectation | 2 |
| 12 | `Bare_constant_projection_still_declines` | `Select(o => 0)` and `Select(o => 5)`: correct values under `Native`, `NativeTranslationNotSupportedException` under `NativeOnly`. The `0` row is the one that would **abort the aggregate** if the gate widened (VERIFIED declining, T0 §5.4) | n/a (never admitted) | 2 |
| 13 | `Bare_owned_hop_scalar_projection_declines` | `Select(b => b.Home.City)` — correct values under `Native`, throws under `NativeOnly`. **EF-362 tripwire** (VERIFIED, T0 §5.4) | n/a | 1 |
| 14 | `Bare_entity_projection_is_unchanged_and_still_native` | `Select(x => x)` and `Where(...).Select(x => x)` succeed under `NativeOnly` — the positive control that 3a did not disturb what already worked. **Use its OWN clean flat fixture**: Task 0's ragged fixture hit an unrelated shadow-FK seeding gap (identically at base and head) and could not settle this (T0 §5.4) | n/a | — |
| 15 | `Bare_projected_set_operation_operand_still_declines` | §7.2, one case per set-op kind, asserting the **pre-3a** disposition, now MEASURED rather than assumed: `Union`/`Concat` correct under `Native`/`DriverLinq` and `NativeTranslationNotSupportedException` under `NativeOnly`; `Intersect`/`Except` hard-fail in **every** mode (T0 §5.3) | n/a | — |
| 16 | `Bare_projection_then_Distinct_still_declines` | §7.3 — `Select(b => b.Title).Distinct()` correct under `Native`/`DriverLinq`, declines under `NativeOnly` (T0 §5.3). **This is a correctness tripwire, not a scope one**: without the narrowing, 4 spec cases hard-fail from a passing base | n/a | — |
| **17** | **`Bare_projection_then_cardinality_operator_goes_native`** | **NEW.** `Select(b => b.Title).Count()` and `.First()` succeed natively with correct values (VERIFIED, T0 §5.4) — the incidental widening §7.5 corrects out of 3d | **REQUIRED** | 1 |

Plus the four `VectorSearch_with_projection` cases, which are spec cases and need no new functional test —
they are the family-A proof in the spec name-set diff. **VERIFIED by direct measurement (T0 §2.3):** the
VectorSearch `NativeOnly` residual moves **20 → 16**, and the remaining 16 are exactly 4
`VectorSearch_with_complex_pre_filter` (EF-382) + 12 entity-leaf (3b). Task 2 re-confirms.

### 9.4 Mutation discipline

Every guard test above must be shown to discriminate **by mutation**, and the mutations are listed per task in
§8. A test that stays green when the code it guards is broken is worthless, and this branch has already had
that exact finding twice (`Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate` and the slice-8
alias conjunct both went unprotected until someone ran the mutation) — **and now a third time, in this very
slice: the original §6.2 invariant would have passed its own mutation vacuously while catching nothing across
twelve runs (§6.2).**

**The three mutations that matter most, revised:** Task 2 (b) — alias → a literal — is *the* mutation, and it is
MEASURED to work (five silently empty collections). Task 2 (d) — remove the Task-1b strip — is the only thing
that tests Task 1b at all. Task 2 (e) — delete the `Distinct` narrowing — is the only thing that tests §7.3, and
it is known live (4 spec cases). Task 2 (a) is kept, but **note honestly that it is loud only for a non-nullable
leaf**, so it must be asserted on values or on a non-nullable leaf.

---

## 10. Expected win, and what remains

**MEASURED, not predicted — the family table below was the original design's basis and it does NOT convert at
the predicted rate.** The authoritative figures are MEASURED(T0) §2.3, by failing-test-name set on both axes with
message-transition bucketing:

| Variant | "stopped declining" | outright Failed→Passed | total win | default-`Native` residual |
|---|---:|---:|---:|---:|
| Tier 1 only, no strip | 72 | 0 | **72** | — |
| Tier 1 + tier 2, no strip (**the original design**) | 76 | 2 | **78** | **7**, and silent wrong data |
| Tier 1 only **+ the §4.6 strip** (variant C) | — | — | **74** | **1** |
| **Tier 1 + tier 2 + the §4.6 strip (variant D — SHIPPED)** | — | — | **80** | **1** |

**Win by test method (MEASURED(T0) §2.3, the 80):** `Include_reference_when_projection` 8,
`Include_collection_when_projection` 8, `VectorSearch_with_projection` 4, `Projecting_count_of_navigation_*` 4,
and 2 each of `Where_projection`, `Where_primitive`, `Union_Select`, `Take_subquery_projection`,
`Take_simple_projection`, `Select_scalar_primitive_after_take`, `Select_scalar_primitive`, `Select_scalar`,
`Select_project_filter`, `Select_project_filter2`, `Select_into`, `Select_Order`, `Select_OrderDescending`,
`Queryable_simple_anonymous_projection_subquery`, `Projection_when_null_value`, `OrderBy_scalar_primitive`,
`OrderBy_multiple`, `OrderBy_ThenBy`, `OrderBy_ThenBy_same_column_different_direction`, `OrderBy_Select`,
`OrderBy_OrderBy_same_column_different_direction`, `OrderByDescending`, `OrderByDescending_ThenBy`,
`OrderByDescending_ThenByDescending`, `Explicit_cast_in_arithmetic_operation_is_preserved`,
`Concat_with_pruning`, `Anonymous_projection_AsNoTracking_Selector`, plus 1 each of `Tag_on_scalar_query` and
`Multiple_entities_can_revert`.

**The family table, kept for provenance and annotated with what it actually delivered** (CITED from the step-3
spike's Q2.2, `(method × async)` spec cases under `MONGODB_EF_NATIVE_ONLY=1`, not distinct queries):

| Family | Sole-cause cases | 3a task | Predicted | **MEASURED** |
|---|---:|---|---|---|
| **A** — bare body, already a resolvable field | 78 | Task 2 (tier 1) | Failed → Passed | **74** — REFUTED as 78; and 76 of the 78 "stopped declining" rather than passing |
| **B** — bare body, already a translatable value | 22 | Task 3 (tier 2), partially — a bare *constant* or captured parameter stays declined by design | up to +22 | **+6** — REFUTED. Most family-B bodies are constants, captured parameters, or leaf kinds `TryTranslateLeaf` still declines |
| **A+B inside the 158** — boundary **plus** a composition relaxation | 104 | **not 3a** | unchanged; 3d | unchanged — **except the cardinality half, which arrives with 3a** (§7.5) |
| **C / F** — needs a per-feature translator | 190 | **not 3a** | unchanged; 3c | unchanged |
| **D** — entity leaf in a projection | 58 | **not 3a** | unchanged; 3b | unchanged |
| **Z / the 363** — declined somewhere else first | 363 | **not projection work at all** | unchanged | unchanged |

Plus, invisible to the spec suite and provable only by §9.3: bare primitive-collection projections, bare owned
entity-collection projections, and bare owned/reference `.Count` projections.

Plus **4 of the 16 `VectorSearch` residuals** (`VectorSearch_with_projection`, `.Select(e => e.Author)` — a
family-A bare field). **VERIFIED — MEASURED(T0) §2.3**: the residual moves 20 → 16 and the remaining 16 split
exactly 4 (`complex_pre_filter`, EF-382) + 12 (entity-leaf, **3b's**). The score-leaf work the VectorSearch slice
already shipped is not wasted on those 12, merely insufficient.

**What is still failing after 3a, and why** — the honest list:

1. **The entity leaf beside a computed leaf** (`new { Book = e, Score = … }`) — a different mechanism (the
   mixed shaper), where EF-356 lives. 3b, and **the owner has ruled 3b *fixes* EF-356** (§0.1 ruling 2).
2. **The computed long tail** — `?:`/`??` (32), date parts (~16), `Add`/`$concat` (20), `Convert`/casts (16),
   `EF.Property` (10), array literals (10), subquery materialization (~18). 3c. Note ~44 of the "computed"
   tail is really a navigation hop in a projection, i.e. joins work wearing a projection label (CITED).
3. **Composition after a bare projection** — `Select(...).Union(...)` and `Select(...).Distinct()`, declined by
   §7.2/§7.3, now *reachable* because 3a exists. 3d. **CORRECTED: `Select(...).Count()` is NOT on this list.**
   The original item 3 included it; MEASURED(T0) §5.4 shows a cardinality operator after a bare projection
   **succeeds natively and correctly** with 3a alone — neither narrowing is on that path. §7.5, §9.3 test 17.
4. **EF-362** — the owned-hop leaf, dotted alias. §4.5; Task 4 gives it a measured answer either way. **The
   decline is VERIFIED live** (T0 §5.4), so Task 4 has a real tripwire to flip.
5. **`NorthwindCompiledQueryMongoTest.Multiple_queries`** — the one default-`Native` residual variant D leaves
   behind: an exception-*message* change on an unsupported cross-DbSet compiled query. Not contract per the
   rubric; a spec-override edit. §4.6. **UNVERIFIED why the message changes** — Task 2.
6. **The 363** — GroupBy breadth (130), predicate/sort breadth (109), the scalar-aggregate binder (56),
   `Distinct` (20), the post-terminal guards (34), the operator catch-all (4). These should be
   **re-attributed** in `docs/native-query-status-EF-322.md` §9.1 rather than left under a label that says
   "projection"; Task 5 does that.
7. **4 genuine client-method cases** that can never go native — the owner has ruled these into the §9.3
   re-baseline bucket, not the coverage gap (§0.1, step-3 spike ruling 4).

---

## 11. Breaking changes

**Expected: none, and no `BREAKING-CHANGES.md` entry — but verify per member against the release TAGS in
Task 5, not from here, and not against `upstream/main`.** Baselines to re-derive with
`gh release list --limit 100 --json tagName`: `v10.0.2` / `v9.1.2` / `v8.4.2`.

The reasoning:

- **CITED** (VectorSearch design §10, `git ls-tree`-verified there): `Infrastructure/MongoQueryMode.cs` exists
  at **none** of the three tags. So on a released package every query in this document runs on driver-LINQ,
  and every mode-dependent statement here — including §7.4's `DriverLinq` change — is **vacuous** at the
  published baseline.
- Every `src/` file 3a touches is `internal` (`MongoSelectDefinition`, `MongoQueryExpression`,
  `NativeProjectionBinder`, the two projection-binding visitors, `MongoShapedQueryCompilingExpressionVisitor`,
  the QMTEV). Per the rubric, `internal` is never public surface regardless of `InternalsVisibleTo`.
- The rubric explicitly carves out **which internal execution path a supported LINQ query takes** and **the
  exact emitted MQL**. 3a is a fallback → native routing flip with **unchanged results** for every shape it
  admits, plus a changed `$project` for shapes that previously emitted none. **The 78 re-baselined `AssertMql`
  strings across 13 spec files fall squarely inside that carve-out** (§0, MEASURED(T0) §5.4) — they are not a
  break, and Task 2 does not need a `BREAKING-CHANGES.md` entry for them.
- No `Mongo:`-prefixed annotation key changes; no stored document-shape change; no
  `IMongoClientWrapper`/`IMongoDatabaseCreator`/`IMongoTransactionManager` change.
- **The one thing to look at twice, now MEASURED rather than predicted** (§7.4/§4.4): under explicit
  `DriverLinq`, a bare `Select(b => b.Posts.Count)` over a **missing** or explicitly-**null** stored array goes
  from `0` to `MongoCommandException`. **Not a break** — `MongoQueryMode` exists at none of the three tags, so
  `DriverLinq` is unobservable to an upgrading consumer, and the rubric's exception-type carve-out does not even
  need to be invoked. But it is a behaviour change to a documented escape hatch, it is the **only** measured
  behaviour change in the slice, and it belongs in the as-built note and in status §4 whether or not it belongs
  in `BREAKING-CHANGES.md`. **The owner should be sighted on it before Task 3 lands** (§7.4).
- **Nothing else in the slice changes an observable value.** MEASURED(T0) §6.2: variant D produces 0
  `NativeOnly` Passed→Failed, 1 default-`Native` residual (an exception *message* on an unsupported shape), and
  **0 `#if` lines added or removed under `src/`**.

---

## 12. Provenance, and every claim's verdict

### 12.1 The original design's own reproduction — unchanged, and it did no execution

```bash
# The design pass, in the main tree at 7af6e0da — read-only.
git log --oneline -3            # 7af6e0da / f4d50b5a / 81b82ed7
git status --short              # clean, before and after
git worktree list               # only the three pre-existing .claude/worktrees/agent-* (not touched)
# then: Read / grep over src/MongoDB.EntityFrameworkCore/Query/**, Storage/BsonBinding.cs,
#       docs/superpowers/specs/2026-08-06-step3-projection-spike-findings.md,
#       docs/superpowers/specs/2026-08-06-vectorsearch-slice-design.md,
#       docs/native-query-status-EF-322.md §4/§5/§9,
#       src/MongoDB.EntityFrameworkCore/Query/AGENTS.md (in full)
```

**No build, no test, no worktree, no `src/` edit, no commit.** That is why five claims needed refuting: the
design was **deliberately** unmeasured, and Task 0 existed to measure it. The revision pass added no execution
either — it edits this document only. **Every measured claim above is MEASURED(T0)**, i.e. it comes from
`docs/superpowers/specs/2026-08-06-step3a-task0-spike-findings.md`, whose own §9 records the commands, the
worktrees (created and removed), and its trap compliance.

### 12.2 The five refuted claims, in one place

| # | The original claim | Verdict | Where corrected |
|---|---|---|---|
| 1 | "**78** from tier 1, up to **100** with tier 2 … Failed → Passed" | **REFUTED** three ways — the numbers are **74 / +6 / 80**, tier 2 is +6 not +22, and 76 of 78 become MQL diffs rather than passes (nothing is green without re-baselining) | §0, §4.4, §10 |
| 2 | "The measured-worse state … is **unrepresentable**" | **REFUTED** — `Distinct_Scalar`/`OrderBy_Distinct` reproduce it (4 cases green at base); site A's own `Route == Projection` conjunct manufactures the divergence | §3.3 |
| 3 | The §6.2 fail-loud invariant | **REFUTED** — never fired across twelve runs, including every silent-wrong-data run; it compares two facts the same block writes. **Removed**, not relocated | §6.2 |
| 4 | "There are **THREE** alias derivation sites" | **REFUTED** — four. `MongoMixedProjectionBindingRemovingExpressionVisitor:91` is an independent fourth derivation; it needs no edit, and now the inventory says why | header box, §2.2 |
| 5 | §3.1's carrier code | **REFUTED** — `Dictionary<string?,…>` throws on a null key; a sentinel is required. Fixed, and the tier is now carried as data too | §3.1 |

**And one thing the design did not have at all:** the late-fallback strip (§4.6, Task 1b). Its absence is why
claim 1's variant would have shipped **silent wrong data** under the default mode.

### 12.3 Everything still UNVERIFIED, and who settles it

*No unassigned UNVERIFIED claims remain. Task 0's own limitations are folded in here.*

| UNVERIFIED | Settled by |
|---|---|
| Which **13 spec files** carry the 78 `AssertMql` re-baselines — Task 0 counted them but did not enumerate them | **Task 2**, in its report |
| Why `NorthwindCompiledQueryMongoTest.Multiple_queries`' exception message changes (the one variant-D residual) | **Task 2** |
| Variant D's effect on `NativeArrayProjectionTests`, `NativeOwnedCollectionCountTests`, `NativeSetOpsTests` — Task 0 ran only its probe filter | **Tasks 2 and 3**, which must run those three classes explicitly |
| That the tier-2 `_v` **collision guard** is reachable at all — no fixture in Task 0 stored a property at element `_v` | **Task 3**, which must build that fixture |
| The bare-entity positive control — Task 0's fixture hit an unrelated shadow-FK seeding gap (identically at base and head) | **Task 2**, §9.3 test 14, on its **own clean flat fixture** |
| EF8 / EF9 behaviour — Task 0 ran EF10 only | **every task's** "full solution green ×3" |
| `Select(b => b.Home.Notes)` (owned-hop array) and `Select(o => o.Customer.City)` declining — INFERRED, not probed | **Task 2**, §9.3 (test 13 covers the scalar hop; the array hop is EF-362's, Task 4) |
| That MongoDB renders `$project: {"Home.Notes": "$Home.Notes"}` as nested output | **Task 4** |
| That the `ElementPath`-aware read is the only missing piece for EF-362 | **Task 4** |
