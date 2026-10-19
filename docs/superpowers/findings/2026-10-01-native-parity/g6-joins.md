# g6-joins: native support for the joins bucket + F1  (read-only research; nothing built/run)

CONFIDENCE KEY: [H] traced end-to-end in code, [M] strong inference from code + test + EF source (efcore checkout at
~/code/efcore), [L] hypothesis - needs the Task-0 probe before coding.
EF source facts used: Skip/Take do NOT apply the pending selector (NavigationExpandingExpressionVisitor.ProcessSkipTake,
NavigationExpandingExpressionVisitor.cs:1059); Distinct DOES (ProcessDistinct :1045 -> emits Select(ti=>ti.Outer) first);
EF emits Queryable.Join only when `!entityReference.IsOptional && !derivedTypeConversion && onDependent &&
foreignKey.IsEffectivelyRequired()` (NavigationExpandingExpressionVisitor.ExpressionVisitors.cs:477), else LeftJoin.

## 0. Task 0 (prerequisite, 30 min): decline-reason instrumentation
`MongoSelectDefinition.MarkNotNativelyRepresentable()` (MongoSelectDefinition.cs:1144) records no reason. Add (temporary, env-gated,
not committed) `[CallerMemberName]/Environment.StackTrace` capture into a `DeclineTrace` string and print it in
`ThrowIfNativeOnlyForbidsFallback` (MongoShapedQueryCompilingExpressionVisitor.cs:932). Then run the [M]/[L] rows below once to
confirm the site before coding. Every site marked [H] does not need it.

## 1. Decline sites (file:line, condition, tests blocked)

| # | Site | Condition | Tests blocked | Conf |
|---|------|-----------|---------------|------|
| D1 | MongoQueryableMethodTranslatingExpressionVisitor.cs:1480-1482 `TryConfirmReferenceIncludeChain` | `Joins.Count != chain.Count` -> `MarkNotNativelyRepresentable` (line 417). EF-synthesized *filter* join (Where over `l.Order.X`) + Include join = 2 joins, 1 Include level. Take/Skip/Distinct sit between the two joins (in PostJoinOps), so nothing else is wrong. | CrossCollectionRelationshipTests.{Take,Skip,Distinct}_between_two_joins_*, Take_between_two_joins_to_same_target_entity_type_* | H |
| D2 | same file :566-568 (comment :566 "Restricted to Levels.Count == 1") bare scalar `Select(ti => ti.Outer.Outer.X)` arm | chain scope (Levels.Count >= 2) never reaches the bare-value arm; falls to catch-all :610-630 (`TryPopulateNativeProjection` can't resolve TI hops) -> `MarkNotNative`. Gate message "projects a non-entity result". | CrossCollectionIncludeTests.Chained_self_referencing_navigation_filter_resolves_reference_not_inverse_collection (3 self-joins, Where null-check on level 3 is already native via TryTranslateChainedScopeConjunction), Ef369MultiJoinComposedTests.ThenInclude_with_composed_projection_Select (EF10 only; the sibling `..._composed_Count` already passes natively via NativeCardinalityBinder) | H |
| D3 | MongoTransparentScopeResolver.cs:80-110 `ScopeRerootingVisitor.VisitMember` | only rewrites `scope.Member`; a BARE scope access used as `EF.Property(ti.Outer.Inner,"OrderDate")` receiver is never rewritten (depth-1 `ScopeSplittingVisitor` does rewrite bare `x.Inner`, which is why the 1-join sibling test is green). `TryRerootToSingleScope` then sees `ti` surviving (NativeJoinScopeTranslator.cs:173-177) -> chain leaf declines (NativeJoinScopeProjectionBinder.cs:266) -> `TryBindProjection` false -> catch-all :627. | ShadowPropertyFlatJoinProjectionTests.Shadow_property_via_EF_Property_in_flat_multi_join_projection_materializes_value | H |
| D4 | NativeJoinScopeProjectionBinder.cs:181-225 nested-wrapped-leaf arm | `select new { o1, o1.o2, Shadow=EF.Property(o1.o2,..) }` where `o1 = new { o2 }` (inner side was `select new { o2 }`; that Select stays a *pending* selector in nav-expansion so the inner is a bare Orders scan - join itself is eligible). Leaf `o1` = `new { o2 = ti.Inner }`: arm requires every nested member to translate to a `MongoFieldExpression` (:201-207) but `ti.Inner` is a whole entity -> `declined` -> projection declines. | ShadowPropertyJoinProjectionTests.Shadow_property_via_EF_Property_in_join_projection_materializes_value (the "direct" sibling is green) | M |
| D5 | NativeJoinScopeProjectionBinder.cs:125-143 (+ read side QMTEV.BindResultMember :3304-3314) include-unwrap loop | EF auto-includes OWNED navigations around a projected entity leaf. `Planet` has `OwnsOne(parkingCar)`: leaf `p` arrives `Include(Include(ti.Outer, parkingCar), parkingCars)`. The loop treats every non-collection include as a *reference Include* (`TryResolveReferenceIncludeLevel` requires `!IsEmbedded()` :360-361) -> `includesResolvableInLeaf=false` -> leaf `p` unresolved -> decline. (Owned collection `parkingCars` is skipped by the `IsCollection:true` test, which is why only the owned REFERENCE trips it.) | UnsupportedQueriesTests.Join_can_be_translated (Planet x Moon, navigation-less join, composite-PK key `_id.planetId` - join itself is eligible) | M (probe: print IncludeExpression in selector) |
| D6 | MongoShapedQueryCompilingExpressionVisitor.cs:651-655 streaming gate + :678-710 | `Clients.Select(p => p.Company)` is a bare INNER entity leaf (arm :481-499, `HasBareJoinInnerEntityLeaf`). Gate uses `rootEntityType = CollectionExpression.EntityType` (Client) for `StreamingEligibility.IsEligible` and `new MongoStreamingEntityMaterializerRewriter(rootEntityType)`, but the shaper materializes Company -> rewriter throws `NativeTranslationNotSupportedException` "value read for property '_id' with no streaming local" (MongoStreamingEntityMaterializerRewriter.cs:1090). `catch ... when (mode != NativeOnly)` (:705) swallows it and falls to DOM in Native, so only NativeOnly sees it. | UnsupportedQueriesTests.Select_can_select_foreign_navigation | H |
| D7 | NativeSlotPopulator.cs / TranslateJoinCore :2259-2262 + :2294-2298 | inner = `Orders.Where(o=>o.OrderID<10500).Include(o=>o.Customer)`: `IsBareCollectionScan` false (PipelineOps + inner Joins) -> `MarkSawNonBareJoinInner`, `IsNativelyEligible=false`, no JoinScope -> `new {c,o}` projection has no confirming arm -> Fallback. | NorthwindMiscellaneousQueryMongoTest.Perform_identity_resolution_reuses_same_instances_across_joins (+ see 4.7: fallback appears to DROP the inner Where) | H (site) / see 4.7 |
| D8 | NativeProjectionBinder.cs:2096-2135 `TryTranslateProjectedCollectionCount` | only a bare `Count()` leaf is admitted; EF8's `c.Orders.Count > 0` leaf is a BinaryExpression over it; `translator.TryTranslateValue` doesn't know reference-collection Count (NativeReferenceCollectionCountPredicateBinder only runs from Where). | NorthwindNavigationsQueryMongoTest.Collection_select_nav_prop_predicate (EF8 only; EF9/10 are `AssertTranslationFailed` on main, EF-216) | M |
| D9 | MongoShapedQueryCompilingExpressionVisitor.cs:222-227 / :321 | GroupBy without supported aggregate: NativeOnly throws NativeTranslationNotSupportedException; default throws InvalidOperationException from EF/driver (main: EF "could not be translated"). See 4.8. | UnsupportedQueriesTests.GroupBy_cannot_be_translated, GroupBy_with_element_selector_cannot_be_translated (and Cast_to_child_not_supported_in_driver, same class) | H |
| F1 | MongoQueryableMethodTranslatingExpressionVisitor.cs:1527 | `PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired` | silent wrong rows, all modes | H |

NOT a decline site for these tests (verified): `IsSingleEligibleNativeJoinScope` :986-1037 / `HasPagingRecordedBetweenJoins` :1028 - not reached,
because the Include-chain arm :411 returns before it. TranslateJoinCore :2287 does NOT fire: the second join is a LeftJoin over an
optional (ObjectId?) FK -> `IsRowCountPreserving`.

## 2. F1 (principal-side reference Include) - root cause and fix [H]

Cause: single-join Include path registers a NEW lookup whose inner/left-outer-ness is guessed from `ForeignKey.IsRequired`
(:1521-1529). EF's rule is `onDependent && IsEffectivelyRequired && !entityReference.IsOptional && !derivedTypeConversion`
(see header). Principal-side reference nav (`Customer.Address`, FK on `Address`) is ALWAYS LeftJoin in EF, yet IsRequired=true here ->
inner `$unwind` -> principals without a dependent vanish. Same wrong guess for derived-type conversion / optional entity reference
(not reachable with one join, but the guess is wrong in principle). Joins.Count>1 chains already use JoinInfo lookups (IsLeftOuter from
the actual operator) so they were right. Select(c.Address.City) and Where(c.Address==null) paths are NOT affected (probe confirms
branch == main for both): they use `JoinInfo.Lookup` built with `isLeftOuter` (:2420), and the null-check arm requires
`Joins[0] is { IsLeftOuter: true }` (NativeSlotPopulator.cs:240).

Fix (use the recorded join, the "Joins[i].IsLeftOuter alternative", mapped by navigation not alias):
```csharp
// TryConfirmReferenceIncludeChain, replace the else-branch at :1518-1538
else
{
    // The join recorded the operator EF actually produced (LeftJoin for a principal-side reference, Join only for a required
    // dependent-side one). Never infer it from ForeignKey.IsRequired.
    var join = mongoQueryExpression.Joins.FirstOrDefault(j => j.Navigation == navigation && j.Lookup is { ForceUnwind: true });
    if (join is null) return false;
    if (join.Lookup!.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal)) return false; // keep defensive check
    newLookups.Add(join.Lookup);          // PreserveNullAndEmptyArrays == join.IsLeftOuter, As == join.Alias
}
```
Alias is identical to the old `new LookupExpression(navigation)` (`_lookup_<Nav>`, UniquifyLookupAlias only renames on collisions),
so MQL for existing optional/required dependent-side Includes is unchanged; ONLY principal-side (and EF8/9 shim cases where
IsLeftOuter differs) change. No MQL regression for supported shapes -> no escalation for dependent-side; the principal-side change is
a bug fix (rows reappear) - flag to owner as "behaviour change, wrong->right" per the escalation list.

Tests (NEW) tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/PrincipalSideReferenceIncludeTests.cs:
```csharp
[XUnitCollection("QueryTests")]
public class PrincipalSideReferenceIncludeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public static TheoryData<MongoQueryMode> Modes => new() { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly };

    [Theory, MemberData(nameof(Modes))]
    public void Include_principal_side_reference_with_required_dependent_fk_keeps_principals_without_a_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);           // Customers 1 "with"(Address Paris), 2 "without"; Address.CustomerId is int (required FK)
        var result = db.Customers.Include(c => c.Address).OrderBy(c => c.Id).ToList()
            .Select(c => $"{c.Name}:{c.Address?.City ?? "<none>"}").ToList();
        Assert.Equal(["with:Paris", "without:<none>"], result);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Include_principal_side_reference_NoTracking(MongoQueryMode mode) { /* same with AsNoTracking() */ }

    [Theory, MemberData(nameof(Modes))]   // controls that must not change
    public void Include_dependent_side_required_reference_still_drops_dangling_dependents(MongoQueryMode mode)
    {   // Address.Include(a => a.Customer) with a dangling CustomerId=99 row: EF emits Join (inner) -> row absent; matches relational.
        using var db = Setup(mode, addDangling: true);
        Assert.Equal(["Paris"], db.Addresses.Include(a => a.Customer).OrderBy(a => a.Id).ToList().Select(a => a.City).ToList());
    }

    [Theory, MemberData(nameof(Modes))]
    public void Include_dependent_side_optional_reference_keeps_rows(MongoQueryMode mode) { /* Parcel.CustomerId int? -> LeftJoin, null nav kept */ }

    [Theory, MemberData(nameof(Modes))]
    public void Select_and_Where_over_principal_side_reference_keep_principals_without_a_dependent(MongoQueryMode mode)
    {   // regression pins for the two paths the review asked to check (already correct):
        Assert.Equal(["with:Paris", "without:<null>"], db.Customers.OrderBy(c => c.Id).Select(c => new { c.Name, City = c.Address!.City }).ToList()
            .Select(a => $"{a.Name}:{a.City ?? "<null>"}"));
        Assert.Equal(["without"], db.Customers.Where(c => c.Address == null).Select(c => c.Name).ToList());
        Assert.Equal(["with"],    db.Customers.Where(c => c.Address != null).Select(c => c.Name).ToList());
    }
    // Model: Customer{int Id; string Name; Address? Address}  Address{int Id; int CustomerId; string City; Customer? Customer}
    //   b.HasOne(c => c.Address).WithOne(a => a.Customer).HasForeignKey<Address>(a => a.CustomerId);
    // Context options: .UseMongoDB(client, db, o => o.UseQueryMode(mode)) + IgnoreCacheKeyFactory (copy Ef373InterleavedPagingTests.RootDbContext).
}
```
Expected MQL after fix (Native): `Customers: [{$sort:{_id:1}}, {$lookup:{from:Addresses, localField:"_id", foreignField:"CustomerId", as:"_lookup_Address"}}, {$unwind:{path:"$_lookup_Address", preserveNullAndEmptyArrays:true}}]`
(today `preserveNullAndEmptyArrays:false`). Mutation check: revert the one line and the first test must fail in all three modes.

## 3. Designs for the blocked tests

### 3.1 D1: relax Include-chain count (Take/Skip/Distinct between two joins)  - effort S, risk M
Why this is sound: lowerer layout is `PipelineOps -> ALL lookups -> PostJoinOps -> PostLookupPagingOps` (MongoSelectLowerer.cs:58-75).
In these queries the Where reached join-1's Inner so `JoinInnerAccessConfirmed` flipped ActiveOps to PostJoinOps: the paging/Distinct was
recorded INTO PostJoinOps after the Where (MongoSelectDefinition.cs:165-170). Join-2 (the Include) is a left-outer reference =>
row-preserving, and TranslateJoinCore :2287 already declines when a non-commuting PostJoinOp meets a non-row-preserving later join. So
`[match(inner), limit]` after both lookups == page-then-join-2. Only the count check blocks it.

Code (TryConfirmReferenceIncludeChain, replace :1480-1485):
```csharp
var select = mongoQueryExpression.Select;
if (select.HasTerminalOperator || select.SawNonBareJoinInner) return false;
var extraJoins = mongoQueryExpression.Joins.Count - chain.Count;
if (extraJoins < 0) return false;
if (extraJoins > 0 && !TryAdmitFilterJoins(mongoQueryExpression, chain, extraJoins)) return false;
```
and map each chain level to its OWN join by the NavigationExpression hop chain (robust to two joins on one navigation / alias uniquify),
as `NativeJoinScopeProjectionBinder.TryResolveReferenceIncludeLevel` does (:360-371): `TryResolveScopeDepth(include.NavigationExpression, selectorParam, ["Outer","Inner"], Joins.Count, out k)`; require `Joins[k-1].Navigation == navigation`; use `Joins[k-1].Lookup`
(this also subsumes F1's fix). TryConfirm needs the selector parameter passed in (callers at :413, :423 have `selector`).
```csharp
private static bool TryAdmitFilterJoins(MongoQueryExpression q, List<IncludeExpression> chain, int extra)
{
    var s = q.Select;
    // (a) already confirmed by an earlier arm (Distinct case: Select(ti=>ti.Outer) confirmed join 1 via the bare-leaf arm :481), or
    // (b) not confirmed, but a native PostJoinOps predicate reads its Inner (the Where that created it) - nothing else could have.
    var alreadyConfirmed = s.ConfirmedReferenceIncludeCount;          // NEW internal getter on _confirmedReferenceIncludes
    var unconfirmedOk = alreadyConfirmed == 0 && s.JoinInnerAccessConfirmed;
    if (alreadyConfirmed != extra && !unconfirmedOk) return false;
    // every join NOT backing an Include level must be a resolved, flattened, non-collection reference join
    foreach (var j in q.Joins.Where(j => !chain.Any(c => c.Navigation == j.Navigation)))   // by position when navs repeat
        if (j.Lookup is not { ForceUnwind: true } || j.Navigation is not { IsCollection: false } || !j.IsNativelyEligible) return false;
    // paging recorded after join 1 but still in PipelineOps would lower BEFORE both $lookups (HasPagingRecordedBetweenJoins gap)
    if (s.HasPaging && s.HasPagingRecordedAfterAJoin && !q.AreAllJoinsRowCountPreserving()) return false;
    return true;
}
```
After the per-level `AddLookup`/`MarkReferenceIncludeConfirmed` loop add, for case (b): `for (i<extra) s.MarkReferenceIncludeConfirmed(); ` (candidates = Joins.Count, so confirmations must equal Joins.Count or
`HasUnconfirmedCandidateJoin` (MongoSelectDefinition.cs:1184) keeps Route=Fallback). Lookups for all joins are already registered by
RebindInnerShaperToOuterQuery (:2963-2982, Joins.Count>1), so no extra AddLookup.

Expected MQL (Take_between; lookup order may be [Product, Order] because :2970 adds the current join first):
```
Lines: [{$sort:{name:1}},
        {$lookup:{from:Products,localField:"prod_id",foreignField:"_id",as:"_lookup_Product"}},{$unwind:{path:"$_lookup_Product",preserveNullAndEmptyArrays:true}},
        {$lookup:{from:Orders,localField:"ord_id",foreignField:"_id",as:"_lookup_Order"}},{$unwind:{path:"$_lookup_Order",preserveNullAndEmptyArrays:true}},
        {$match:{"_lookup_Order.name":{$ne:"O2"}}},{$limit:3}]
```
Skip: `{$skip:1}` instead of limit. Distinct: `...,{$match},{$unset:"_lookup_Order"},{$group:{_id:"$$ROOT"}},{$replaceRoot:{newRoot:"$_id"}}`
(TryBindWholeEntityDistinct :2028-2037 ran BEFORE join 2 existed, so its ExcludeField only names `_lookup_Order`; the dedup key then also
contains the 1:1 `_lookup_Product` document - functionally dependent on the Line, so dedup result is identical; the doc keeps
`_lookup_Product` for the Include read). Same-type test: aliases `_lookup_PrimaryOrder`/`_lookup_SecondaryOrder` (distinct navigations).

Tests: the 4 existing methods under `[Theory]` x {Native, DriverLinq, NativeOnly} (they currently pin only default mode) + NEW guards:
```csharp
// must STILL decline (Include join is an inner Join because FK required -> not row preserving; TranslateJoinCore :2287 marks non-native)
[Fact] void Take_between_a_filter_join_and_a_REQUIRED_include_join_declines_cleanly_and_pages_correctly()
    => NativeModeAssert.DeclinesCleanly(m => RunRequiredProductShape(m));          // NativeOnly throws; Native == DriverLinq == oracle
// ordering oracle over ragged data: lines with missing Order, missing Product, dangling Product id, 5 rows, Skip(1).Take(2)
[Theory] void Skip_Take_between_joins_matches_in_memory_oracle(MongoQueryMode mode)
// mutation: delete the (s.HasPaging && HasPagingRecordedAfterAJoin) conjunct with a user-join-then-Include shape (Join(..).Take(1).Include) - must fail
```
Risks: (1) a user `Join` whose Inner is NOT read, followed by an Include, is now admitted when `JoinInnerAccessConfirmed`; condition (b) is deliberately
tied to the Where having read an Inner. (2) shaper compile with an unused TI1.Inner binding (same situation as the existing
Where+Select(ti.Outer) native path). (3) EF8/9 shim: `IsLeftOuter` comes from IsEf8Ef9LeftJoinShim; tests fail on all 3 versions today, run all 3.
(4) AGENTS.md "Post-terminal gating" - Distinct is a terminal-ish op; we only admit a whole-entity PostJoinOps Distinct (not `IsDistinct` grouping). Update the
TryConfirm doc comment ("declines a user Join plus a downstream Include") accordingly.

Optional Phase 2 (NOT needed for these 4, would unpin Ef373InterleavedPagingTests.*_declines_under_NativeOnly): per-op join boundary.
Record `JoinBoundary = Joins.Count` on every MongoSelectOp at record time (MongoSelectDefinition.AppendSkip/AppendLimit/AddPredicateConjunct) and have
`MongoSelectLowerer.Lower` emit lookups for join i immediately before the first op whose boundary > i (lookup list ordered by Joins index, not
GetPendingLookups' dependency sort; transitive localField dependency is already index-monotone). That removes HasPagingRecordedBetweenJoins,
DeferPipelineOpsPastConfirmedJoin and HasNonCommutingPostJoinOp. Medium-large; MQL changes for all join+paging shapes -> escalate.

### 3.2 D2: bare scalar leaf over a chained join scope  - effort S, risk M
Change arm at QMTEV :567-590: drop `JoinScope is { Levels.Count: 1 }` and translate by scope count:
```csharp
else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out _)
         && mongoQueryExpression.Select.JoinScope is { } bareScope
         && mongoQueryExpression.Select.Projection.Count == 0
         && selector.Body is not ConditionalExpression
         && !selector.Body.TryGetProjectionMembers(out _)
         && TryTranslateBareScopeValue(bareScope, selector, out var bareValueLeaf)   // below
         && !NativeProjectionBinder.IsMisreadWholeValueLeaf(bareValueLeaf!)
         && ClassifyNonNullableValueRead(...) is var r && r != NonNullableValueRead.Decline) { ...unchanged... ConfirmEntireChain }

static bool TryTranslateBareScopeValue(MongoJoinScope s, LambdaExpression sel, out MongoExpression? leaf)
    => s.Levels.Count == 1
        ? NativeJoinScopeTranslator.TryTranslateValue(s, sel.Parameters[0], sel.Body, out leaf)
        : NativeJoinScopeTranslator.TryTranslateSingleScope(s, sel.Parameters[0], sel.Body, valueMode: true, out leaf);   // by hop chain, never CLR type
```
This is exactly what the wrapped-projection arm already does for chains (NativeJoinScopeProjectionBinder.cs:259-270), so semantics are shared. `OrderBy(n => n)` after
the Select is already native: nav-expansion folds it before the Select (key becomes `ti.Outer.Outer.Outer.EmployeeName`) and the chained arm
NativeSlotPopulator.cs:594-598 records it into PostJoinOps because JoinInnerAccessConfirmed flipped.
Expected MQL (self-ref test): `Staff: [{$lookup mgr_id->_id as _lookup_Manager},{$unwind preserve:true},{$lookup localField:"_lookup_Manager.mgr_id" as _lookup_Manager1}...,
{$unwind..},{$lookup ... _lookup_Manager2},{$unwind..}, {$match:{<MongoLookupNullCheck _lookup_Manager2 == null>}}, {$sort:{emp_name:1}}, {$project:{_v:"$emp_name",_id:0}}]`
Ef369: `Orders: [{$lookup Customers}, {$unwind true},{$lookup Regions localField "_lookup_Customer.region_id"},{$unwind true},{$match:{"_lookup_Customer_Region.name":"West"}},{$project:{_v:"$desc"}}]`.
Risk: a bare leaf resolving to an INNER level (idx>=1) of non-nullable CLR type over an unmatched left-outer level reads default (F3-like). The shared
`ClassifyNonNullableValueRead` + `MongoProjection.ThrowsOnNull` machinery already covers Length/IndexOf only; add tests: chain `Select(ti.Inner.IntProp)` with an unmatched
level (expect decline or throw-on-null, never 0). Also `OrderBy` key reading idx>=1 stays declined (:594 root-only) - correct.
Tests: the 2 existing methods x 3 modes; NEW `Chained_bare_scalar_projection_over_ragged_chain_matches_in_memory_oracle` (rows with 0..3 managers; Select(s => s.Manager.Manager.Name), Select(s => s.Manager.Manager.Rank /*int*/)).

### 3.3 D3: EF.Property over a bare scope in a chained join  - effort S, risk L
Add to `MongoTransparentScopeResolver.ScopeRerootingVisitor`:
```csharp
protected override Expression VisitMethodCall(MethodCallExpression node)
{
    // EF.Property(<bare scope access>, "Shadow"): the receiver is a scope, not a member of one, so VisitMember never sees it.
    if (node.Method.IsEFPropertyMethod()
        && node.Arguments is [var receiver, ConstantExpression { Value: string } name]
        && receiver.RemoveConvert() is MemberExpression bare
        && TryResolveScopeDepth(bare, rootParam, hopNames, sourceCount, out var scope)
        && node.Method.DeclaringType == typeof(EF) )
    {
        if (ResolvedScope is { } prior && prior != scope) CrossScope = true;
        ResolvedScope = scope;
        return node.Update(null, [Expression.Convert(scopeParams[scope], typeof(object)), name]);
    }
    return base.VisitMethodCall(node);
}
```
(`MongoExpressionTranslator.TryResolveMember` :87-95 already accepts `EF.Property(param, "name")` and resolves via `scopeType.FindProperty`, shadow included, and prefixes the level's InnerPrefix.)
Expected MQL: `Customers: [{$match:{_id:"ALFKI"}}, {$lookup Orders localField:_id foreignField:cust_id as:_lookup_Orders},{$unwind preserve:false},{$lookup Items localField:"_lookup_Orders._id" foreignField:"ord_id" as:"_lookup_Items"},{$unwind false},{$project:{o:"$_lookup_Orders", i:"$_lookup_Items", Shadow:"$_lookup_Orders.OrderDate"}}]` (alias names per TryStageInnerLevel).
Tests: existing method x 3 modes; NEW unit test in tests/...UnitTests (ScopeRerootingVisitor on `EF.Property<DateTime?>(ti.Outer.Inner,"X")` => scope 1, no residual root param) and a mutation check (remove override => D3 test fails NativeOnly). Also add EF.Property on the root scope `EF.Property<string>(ti.Outer.Outer,"S")` and a CrossScope pair (`EF.Property(ti.Outer.Inner,"A") == EF.Property(ti.Inner,"B")` must decline).
Risk: the depth-1 path already works; chain read side for a shadow field (`MongoFieldExpression` with prefixed path) - confirm with the Task-0 probe that nothing else declines for this test (I could not find another site).

### 3.4 D4: nested construction around a whole-entity Inner leaf  - effort M, risk M
Binder (NativeJoinScopeProjectionBinder.TryBindProjection, new arm BEFORE the nested-wrapped-leaf arm at :181): if `leafBody.TryGetProjectionMembers(out nested)` and EVERY nested value
resolves via `TryResolveScopeDepth` to a whole-entity INNER scope (idx>=1; no Include, root idx 0 declines to keep alias rules), stage each level with
`TryStageInnerLevel` (dedups with a sibling `o1.o2` leaf on the same InnerPrefix) and record the member as "entity-wrapper" (no projection of its own):
`staged` gets nothing for `o1`; remember `entityWrapperMembers.Add(alias)`; commit them with `mongoQ.Select.AddEntityWrapperMember(alias)` (new, write-once like AddClientConditionalMember).
Read side: `BindResultMember` (QMTEV :3285): when the FOLDED value is a `NewExpression/MemberInit` whose members are all rebindable entity shapers, return
`folded.RebuildProjectionMembers(members.Select(m => RebindEntityShaper(q, (StructuralTypeShaperExpression)m, alias: null)))` (alias null => dedup onto the existing level entry, :3369).
Expected MQL: `Customers: [{$lookup Orders as _lookup_Orders},{$unwind false},{$match:{"_lookup_Orders.cust_id":"ALFKI"}},{$project:{"_lookup_Orders":"$_lookup_Orders", Shadow:"$_lookup_Orders.OrderDate", o2:...}}]`
(one `_lookup_Orders` entry serves `o1.o2`, `o2` and the Shadow read).
Tests: existing method x 3 modes; NEW `Nested_anonymous_wrapper_around_joined_entity_yields_same_instance_for_o1_o2_and_o2` (tracking + NoTrackingWithIdentityResolution), and `Wrapper_with_scalar_and_entity_members_declines_cleanly` (mixed members stay declined).
Risk: identity - `o1.o2` and `o2` must be the SAME instance under tracking (both bind the same EntityProjection index; verify). Keep root (`new { c }`) declined. [M] because the decline site is inferred; do Task 0 first.

### 3.5 D5: embedded (owned) Include wrapper around a join-scope entity leaf  - effort S-M, risk M
Treat embedded includes like collection includes (their data lives in the staged whole document):
- NativeJoinScopeProjectionBinder.cs:130 `if (include.Navigation is not { IsCollection: true })` -> `if (include.Navigation is { IsCollection: false } n && !n.IsEmbedded())`.
- QMTEV.BindResultMember :3307: `includeToUnwrap.Navigation is not { IsCollection: true } && !IsRebindableEntityShaper(...)` -> also exempt `Navigation.IsEmbedded()`;
  re-wrap loop :3328 treat `IsCollection || IsEmbedded()` as "keep NavigationExpression as is", then `VisitIncludeExpression` registers/handles it as it does for owned collections.
Whole-root leaf is staged `$$ROOT` (:155-158) and the inner leaf `_lookup_<Nav>` whole doc, both contain the owned sub-documents, so DOM materialization reads them. Guard: streaming stays off (allowStreaming false on the Projection route).
Expected MQL: `planets: [{$lookup:{from:moons,localField:"_id",foreignField:"_id.planetId",as:"_lookup_Moon"}},{$unwind false},{$project:{p:"$$ROOT","_lookup_Moon":"$_lookup_Moon"}}]`.
Tests: UnsupportedQueriesTests.Join_can_be_translated x 3 modes (assert row VALUES: Planet.parkingCar populated, parkingCars count 2 for Tatooine+Endor: make the seed assert `Assert.Equal("YELLOW TAXI", r.p.parkingCar.reg)`); NEW join over an owner with owned ref+collection and a left-outer unmatched inner. [M]: confirm Include wrapper presence via Task 0 before coding.

### 3.6 D6: bare inner entity leaf must not stream-root at the collection root  - effort XS, risk L [H]
```csharp
// MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery :651
var streaming = allowStreaming
    && nativeFactory != null
    && !mongoQueryExpression.Select.HasBareJoinInnerEntityLeaf        // shaper root is the lookup target, not rootEntityType
    && ...existing...
```
(Cleaner alternative: root the rewriter at the lookup's TargetEntityType when `BareJoinEntityLeaf is (_, true)`; DOM is the lower-risk answer and what Native mode already silently does.) Also make the `catch` at :705 narrower for NativeOnly? No - keep, the fix removes the trigger.
Test: UnsupportedQueriesTests.Select_can_select_foreign_navigation x 3 modes with VALUE assertions (seed a Client with and without Company; today only `Assert.NotNull(result)`); NEW NativeOnly test `Select_inner_entity_leaf_when_root_and_target_share_key_name`. Expected MQL: `Clients: [{$lookup Companies localField:CompanyId foreignField:_id as:_lookup_Company},{$unwind preserve:true}]` then DOM shape reads `_lookup_Company`.
Check other Inner-leaf native tests still stream-free: expect no perf delta (they were already DOM when their root was ineligible).

### 3.7 D7: `Orders.Where(..).Include(o=>o.Customer)` as a Join inner (identity-resolution spec test)  - effort L, and WRONG ON MAIN [M]
Evidence: the baseline MQL (NorthwindMiscellaneousQueryMongoTest.cs:4383, identical on main :4297 for EF10) has NO `OrderID < 10500` `$match`/pipeline anywhere. The spec
test asserts only `Assert.Same` identity so a dropped inner filter is invisible. EF8/EF9 on main: `AssertTranslationFailed` (EF-X020), so only EF10 "worked" on main - and
returned unfiltered inner rows. Per the brief this is wrong-on-main: RECORD IT FOR MAIN (probe: seed Orders 10248..11077 for ALFKI, run the query on main, `Assert.DoesNotContain(result, r => r.o.OrderID >= 10500)`).
Recommendation: do NOT build native support to match main. Options: (a) classify non-bare inner (`SawNonBareJoinInner` with PipelineOps/Include) as FallbackWrongData -> HardDecline in Native/NativeOnly (ClassifyNativeDisposition :961) - turns today's silent wrong rows into a clear error => ESCALATE (changes default-mode behaviour); (b) real native: lookup with `pipeline:[{$match}]` for an inner whose ops are only MongoMatchOp + inner reference Include migrated as transitive joins (RebindInnerShaperToOuterQuery would have to accept IncludeExpression inner shapers, :2748-2752 returns early today). L effort; defer. Spec test: leave baseline, add `// Fails (wrong rows on main)` doc entry under NativeOnly via MongoSpecTestHelpers.IsNativeOnly branch.

### 3.8 D8: EF8 `Select(c => c.Orders.Count > 0)`  - effort S-M, risk M (EF8-only)
Add a pure helper next to `TryTranslateProjectedCollectionCount` (NativeProjectionBinder.cs:2096) for a comparison leaf: BinaryExpression{Equal..LessThanOrEqual} where one side `MongoExpressionTranslator.TryMatchCountExpression(...)` with null predicate; reuse `TryMatchReferenceCollectionCountNavigation` + `TryBuildReferenceCollectionCountLookup` (staged, no mutation) and `TryTranslateValue` for the other side; result `new MongoBinaryExpression(op, size, other)`. Wire into TryTranslateLeaf next to the existing Count arm. Stage `pendingBareCountStamps`/`pendingLookups` exactly like the bare Count leaf (commit block ~:508).
Expected MQL = the baseline already in NorthwindNavigationsQueryMongoTest.cs:319 (`{$lookup Orders...},{$project:{_v:{$gt:[{$size:"$_lookup_Orders"},0]},_id:0}}`).
Caveat: EF9/10 expectations are `AssertTranslationFailed` (main failed, EF-216); if the new arm also matches the EF9/10 tree the Native-default spec tests start succeeding and those `AssertTranslationFailed` expectations break - gate the arm or update the baseline per version; run all 3 versions. Probe first: it is [M] that EF8's tree is `Count(Where(Orders, corr)) > 0`.

## 4. Expected-refusal tests (UnsupportedQueriesTests GroupBy x2, Cast_to_child)
Main: `_db.Planets.GroupBy(p => p.hasRings).ToList()` threw InvalidOperationException (EF "...GroupBy(...) could not be translated", asserted with `.GroupBy(` / `p.hasRings` message checks that the branch test dropped). So on main these were NOT working queries - out of parity scope.
Branch default: native declines (route Fallback), driver/EF throws InvalidOperationException -> test passes. NativeOnly: the native layer already owns a clear refusal
(VisitProjectedQuery :222-227 -> NativeTranslationNotSupportedException "Query groups without a supported aggregate projection"), but exact-type `Assert.Throws<InvalidOperationException>` fails.
=> No product change. Test change: make the expectation mode-aware (functional tests do not honor MONGODB_EF_NATIVE_ONLY today; add `FunctionalTests/Utilities/TestMode.IsNativeOnly` reading the env var like SpecificationTests/MongoSpecTestHelpers.IsNativeOnly) and a helper
```csharp
internal static void ThrowsRefusal<TDefault>(Action act) where TDefault : Exception
{ if (TestMode.IsNativeOnly) Assert.Throws<NativeTranslationNotSupportedException>(act); else Assert.Throws<TDefault>(act); }
```
(Alternative: pin a second explicit test `GroupBy_without_aggregate_is_refused_under_NativeOnly` and leave the default-mode one.) Same for Cast_to_child (driver ExpressionNotSupportedException vs native refusal).

## 5. Suggested TDD order
1. F1 (1 line + tests) - independent, highest value (silent wrong rows). 2. D6 (1 line). 3. D3 (visitor override). 4. D2 (arm lift). 5. D1 (+ F1 mapping by hop). 6. D5. 7. D4. 8. D8 (EF8). 9. test-helper change for 4. 10. D7 decision with owner.
Full-suite verification (all 3 EF versions) after D1/D2/D5 - they touch Include-chain/projection admission (AGENTS.md: "gate must call the fix's predicate"; the D1 extra-join predicate must be the same one TranslateJoinCore :2287 relies on - share `AreAllJoinsRowCountPreserving`).

## 6. Escalations
- F1 fix: wrong->right row-set change in all modes (principal-side Include now returns principals without dependents) - tell owner (behaviour change; matches main).
- D7(a): turning a silently-wrong fallback into a throw (default mode) = "translation that previously 'worked' now fails".
- D1 Phase 2 (per-op join boundary) would change emitted MQL for join+paging shapes: ESCALATE before doing.
- Wrong-on-main to file on main: inner `Where` on a join inner dropped by the driver-LINQ bridge (suspected; verify), F1 is NOT wrong on main.
