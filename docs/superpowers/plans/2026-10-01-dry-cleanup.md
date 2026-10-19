# DRY cleanup of EF-322c — implementation plan

Branch `EF-322c` (commit directly on it; no worktrees). Source of the findings: a 7-reviewer DRY review of
`git diff dec7e26f..HEAD -- src` done 2026-10-01. Line numbers below were taken at commit c7854e7f and may
drift as earlier tasks land — always locate code by the named symbol, not the line number.

No spec exists; the binding requirement from the owner is: **make the code more DRY, but don't merge code if
the result becomes considerably harder to understand and reason about (e.g. heavy parameterization:
behaviour-switching bool/flag params, lambdas threaded through to vary several steps, do-everything helpers).**

## Global Constraints

- **Pure refactor: no behaviour change** unless an item explicitly says otherwise. If, on reading the code, an
  item turns out to change behaviour (different guards, different null handling, different decline/throw
  paths) or would need a mode flag to preserve behaviour, SKIP it and record why in the report. Skipping a
  doubtful item is always acceptable; a silent behaviour change is not.
- Each item is a candidate, not a mandate: re-verify the duplication exists as described before changing it.
- Preserve file BOMs. Match surrounding naming, comment density and idiom. Keep existing explanatory comments
  (move them to the shared helper when the helper absorbs the logic they explain).
- `src/` is nullable-enabled; annotate new members (`[NotNullWhen(true)]` on Try-pattern outs).
- Version-conditional code (`#if EF8 || EF9`, `#if !EF8`) must keep compiling in all three configs.
- New helpers are `private`/`internal` — never `public` (no public API change).
- Don't touch tests except to add a test an item explicitly asks for.
- **Verification per task:**
  1. `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"`, `"Debug EF9"`, `"Debug EF10"` — all must build
     with no new warnings.
  2. Full EF10 test run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --logger "trx"
     --results-directory <your unique scratch dir>`, with `MONGODB_URI`/`ATLAS_URI` unset (Docker
     testcontainers). Compare failing tests against the baseline failure list the controller gives you; any
     NEW failure must be fixed or the offending item reverted.
  3. Use a unique scratch directory for all logs/trx (the controller supplies it) — never write into another
     agent's directory, never delete other directories.
- One commit per task on `EF-322c`, message prefixed `EF-322: ` (e.g. `EF-322: DRY — reuse shared comparison
  and parameter helpers`). Don't push. Don't commit `docs/superpowers/plans/*` or `.superpowers/`.

### Task 1: Reuse existing / sibling helpers instead of private copies (cross-cutting)

Mechanical: delete a copy and call one shared helper.

A. **Comparison operator map + mirror.** `NativeGroupByBinder.MapComparisonOperator` (~1703, throws) and
`FlipComparison` (~1716) duplicate `MongoExpressionTranslator.MapComparisonOperator` (~1571, returns
`MongoBinaryOperator?`) and `Mirror` (~2113). `NativeReferenceCollectionCountPredicateBinder.MapOperator`
(~107) is a third map. Make the translator's two `internal static`; delete the copies; throwing callers use
`MapComparisonOperator(t) ?? throw ...` (keep the existing exception type/message) or `is { } op` where the
caller already pattern-matches the six comparison NodeTypes (NativeGroupByBinder ~669, ~1079, ~1601;
NativeReferenceCollectionCountPredicateBinder ~39). Also expose `MongoExpressionTranslator.IsComparison`
(~2123) if that's the cleaner replacement for the restated NodeType pattern.

B. **`ReferencesParameter` copies.** `NativeGroupByBinder` private `ParameterReferenceFinder` +
`ReferencesParameter` (~1131-1157) and `NativeJoinScopeTranslator.ReferencesParameterOutsideHopChain` /
`ParameterPresenceVisitor` (~185-202) — replace with `ExpressionExtensionMethods.ReferencesParameter`
(`ExpressionExtensionMethods.cs` ~291), matching by reference identity. Check the JoinScope one really is plain
"references p" (the name says "outside hop chain" — if it excludes hop-chain accesses, it is NOT a duplicate:
skip it). Optionally add the early-exit (stop visiting once found) to the shared visitor.

C. **Include fixup call built 3×.** `MongoStreamingEntityMaterializerRewriter.SpliceReferenceInclude` (~647-689)
and `SpliceCollectionInclude` (~697-740), and `MongoProjectionBindingRemovingExpressionVisitor.AddInclude`
(~1246-1281), each build `GenerateFixup` + `Expression.IfThen(IsAssignableFrom(...), Call(IncludeReference|
IncludeCollection<TIncluding,TIncluded>(entry, instance, concreteType, related, Constant(nav), Constant(inverse),
Constant(fixup), Constant(setLoaded))))`. Add one `MongoIncludeFixups.CreateIncludeCall(INavigation navigation,
Expression entityEntry, Expression instance, Expression concreteEntityType, Expression relatedEntity,
bool setLoaded)` (name may be adjusted) choosing Reference vs Collection from `navigation.IsCollection` (as
`AddInclude` already does). Collapse the two Splice methods into one `SpliceInclude`. Make the three
`Include*MethodInfo` private if nothing else uses them. Keep the `#pragma warning disable EF1001` scoping.

D. **Comparison negation 3×.** `MongoExpressionNegator` `comparison` arm (~149-177) and `comparison3` arm
(~208-234) have identical switch bodies; `NativeGroupByBinder.TryNegateGroupComparison` (~1729-1745) is the
same rule ($eq↔$ne invert; < <= > >= wrap in `MongoUnaryOperator.Not`; else decline). Add `internal static bool
MongoExpressionNegator.TryNegateComparison(MongoBinaryExpression comparison, [NotNullWhen(true)] out
MongoExpression? negated)`; the two case labels keep their own guards and comments; the GroupBy one becomes a
call. Verify the three bodies really are identical first.

E. **Member/EF.Property matching hand-rolled.** Use `ExpressionExtensionMethods.TryGetMemberOrEFProperty`
(~261) in: `NativeProjectionBinder.TryGetOwnedReferenceNavigationLeaf` switch (~884-900),
`NativeProjectionBinder.IsNavigationOnParameter` (~1785) and `IsDirectMemberAccessOn` (~2539),
`MongoQueryableMethodTranslatingExpressionVisitor.PeelEmbeddedSegments` (~3026-3034) and
`GetKeySelectorTargetObject` (~3005-3011). Caveat: `IsNavigationOnParameter` and `GetKeySelectorTargetObject`
currently accept a non-constant EF.Property name; if the helper requires a constant, check whether a non-constant
can reach these sites — if it plausibly can, skip that site.

F. **TransparentIdentifier test by type name.** `Parameters[0].Type.Name.StartsWith("TransparentIdentifier")`
at QMTEV (~1142, ~1257, ~1366) and `NativeProjectionBinder` (~1354) → use `IsTransparentIdentifierType()`
(ExpressionExtensionMethods ~333). `{Member.Name: "Outer" or "Inner"} && member.Expression == param` at QMTEV
~1158, ~1180, ~1266 → `member.IsTransparentIdentifierOuterOrInnerAccess() && member.Expression == param`
(~391). Confirm the helpers' semantics are equal-or-tighter in a way that can't reject a real EF transparent
identifier.

G. **Arithmetic-leaf shape restated on emit and read sides.** `Add|Subtract|Multiply|Divide|Modulo` node-type
lists at `NativeProjectionBinder` ~795-798 and ~1172-1173, `NativeSelectManyBinder.IsArithmeticComputedLeaf`
(~532-537), and `MongoProjectionBindingExpressionVisitor` ~234-235, ~1695-1702 (there is already
`IsArithmeticNodeType` at ~687 in that file). Create one `internal static bool IsArithmeticLeafShape(...)` next
to `NativeProjectionBinder.IsNumericComputedLeafShape` and call it from all sites. Only merge sites whose
operator set is identical.

H. **Enumerable element type 2×.** `MongoExpressionTranslator.MethodCalls.GetEnumerableElementType` (~826-841)
and `NativeProjectionBinder.TryGetEnumerableElementType` (~1763-1779) → one `TryGetEnumerableElementType(this
Type, out Type)` (or similar) in `src/MongoDB.EntityFrameworkCore/TypeExtensionMethods.cs`. Preserve the array
shortcut where it affects results.

I. **Stale MethodInfo caches.** `MongoEFToLinqTranslatingExpressionVisitor` `EnumerableCountMethod` /
`EnumerableLongCountMethod` (~1122-1128) → `EnumerableMethods.CountWithoutPredicate` /
`LongCountWithoutPredicate`; `LocalCollectionFilterFoldingVisitor` hand-reflected `Where`/`ToArray` (~40-44) →
`EnumerableMethods.Where`/`ToArray`. Check which `EnumerableMethods` (EF Core's or the provider's
`src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs`) is in scope and has the member.

J. **Inline helpers.** Replace inline `type != x.Type ? Expression.Convert(x, type) : x`-style code with the
existing `ExpressionExtensionMethods.ConvertIfRequired` (~96) — in `MongoProjectionBindingRemovingExpressionVisitor`
(~280, ~701, ~740, ~883, ~943), `MongoMixedProjectionBindingRemovingExpressionVisitor` (~210, ~336, ~462, ~660,
~705, ~738), `MongoStreamingEntityMaterializerRewriter` (~1006, ~1075, ~1081). Only where semantics match exactly
(check what ConvertIfRequired compares). Replace `Nullable.GetUnderlyingType(t) ?? t` with `t.UnwrapNullableType()`
(TypeExtensionMethods ~123) in `MongoExpressionTranslator.cs` and its `Members.cs`/`MethodCalls.cs` partials.

K. **Dead code.** `MongoProjectionBindingRemovingExpressionVisitor.CollectionAccessorAddMethodInfo` (~1312) is
unreferenced — delete (confirm with grep).

### Task 2: Native rendering and pipeline building

Files: `Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`, `MongoQueryLanguageRenderer.cs`,
`MongoPipelineFactory.cs`, `MongoSelectLowerer.cs`, `MongoValueRenderer.cs`, `Query/Expressions/MongoBinaryExpression.cs`.

1. **`$literal` wrap 5×.** "Render, then wrap in `$literal` if node is `MongoConstantExpression` or
`MongoParameterExpression`": `MongoAggregationExpressionRenderer.RenderBranch` (~298-304), and
`MongoPipelineFactory` `RenderProject` (~312-319), `RenderAddFields` (~341-345), `RenderKeyedGroup` accumulator
operand (~391-395), `RenderKeyPart` (~418-424; is RenderBranch with elementVariable null). Make `RenderBranch`
internal (rename to something like `RenderLiteralSafe` only if the name is clearer), delete `RenderKeyPart`,
call it at all sites.
2. **`RenderInValues` in both renderers.** `MongoQueryLanguageRenderer` (~213-242) and
`MongoAggregationExpressionRenderer` (~766-807), plus the shape checks `MongoQueryLanguageRenderer` In arm of
`IsQueryDialectRenderable` (~435-438) and `MongoAggregationExpressionRenderer.CanRenderInValues` (~270-272).
Move one `RenderInValues(MongoExpression, PlaceholderTable)` + one `IsRenderableInValues(MongoExpression)` into
`MongoValueRenderer`. Before merging, verify the aggregation version's parameter arm is a strict superset that
behaves identically for every input the query-dialect version accepts; if not, skip.
3. **`RenderProject`/`RenderAddFields` loop.** After (1), share `private static BsonDocument
RenderFields(IEnumerable<MongoProjection>, PlaceholderTable)`; `RenderProject` keeps its `_id` handling.
4. **Lowerer stage sequences.** (a) `MongoSelectLowerer` set-op operand pre-combine stages (~106-112 and
~333-337: Grouping→MongoGroupStage; MongoProjectStage(Projection); PostGroupOps) → `AppendProjectedOperandStages`.
(b) lookup + post-join ops (~67/71/75 and ~88/93/94: AppendLookupStages, PostJoinOps, PostLookupPagingOps) →
`AppendLookupAndPostJoinStages`. Keep call-site comments.
5. **Boolean operator set 4×.** Add `internal bool IsComparison` / `IsArithmetic` to `MongoBinaryExpression`
(comparison = the six comparisons; boolean = comparisons + AndAlso/OrElse; check exactly which set each site
uses) and use them at `MongoBinaryExpression.Type` (~43-46) and `MongoAggregationExpressionRenderer` ~876-878,
~979-982, ~1041-1044. Name the properties for exactly the set they hold.
6. **Small helpers.** `NotIf(bool negated, BsonValue test)` for `negated ? {$not:[x]} : x` in the aggregation
renderer (~646, 679, 702, 753, 763); `IfNull(BsonValue value, BsonValue replacement)` for `{$ifNull:[x,
fallback]}` (aggregation renderer ~12 sites, MongoPipelineFactory ~413); `ThrowIfUnsafeTruthinessRoot(MongoExpression,
string positionDescription)` for the 3 checks (~735-744, ~823-828, ~1171-1181) — only if the message stays
equally specific; `NestedElementVariable(string?)` for `elementVariable is null ? "e" : elementVariable + "e"`
(~581, ~602). Repeated literal stage BSON in MongoPipelineFactory (`{$group:{_id:"$$ROOT"}}` ~192,479,502,509;
`{$replaceRoot:{newRoot:"$_id"}}` ~480,531; always-false `{_id:{$type:-1}}` ~600 and MongoQueryLanguageRenderer
~68) → small static helpers. `SerializeParameter` coerce-then-serialize (~703, 757, 766) → local function (leave
MongoValueRenderer ~78 alone: it coerces to property.ClrType).

### Task 3: GroupBy binder

Files: `Query/NativeTranslation/NativeGroupByBinder.cs`, `NativeCardinalityBinder.cs`, `NativeSlotPopulator.cs`,
`PlaceholderTable.cs`, `Query/Expressions/MongoSelectDefinition.cs`.

0. **Possible bug first (TDD).** `TryBindGroupSideOperand` (~1659) sets `keySerializationProperty = null` for
any composite key even for an `_id.<Sub>` path, while `ResolveKeyMemberSerializationProperty` (~706) returns the
matched part's property. Write a FunctionalTest (or spec-style test in the existing native GroupBy test class —
find where GroupBy HAVING tests live) for a composite-key GroupBy whose HAVING (`.Where(g => g.Key.SubGuid ==
someGuid)`) or `g.Any(...)`-style group-side comparison compares a Guid (or other value-converted) sub-key
against a captured value, running under the default (native) query mode, asserting correct rows. If it fails
(wrong rows or throw), fix by making `TryBindGroupSideOperand` call `ResolveKeyMemberSerializationProperty`. If it
passes, still replace the inline restatement with the call only if behaviour is provably identical for all
inputs; otherwise leave and report. Also run the same test under `UseQueryMode(MongoQueryMode.DriverLinq)` for
parity if that's how sibling tests are written.
1. **Group member dispatch 2×.** Outer member loop in `TryBindGroupProjection` (~293-327) and in
`TryBindNestedGroupProjectionConstruction` (~556-589): key path → ReadKeyMember; TryBindAccumulator ||
TryBindPushAccumulator; nested construction; TryTranslateGroupProjectionExpression + IsMisreadWholeValueLeaf.
Extract `TryBindGroupMemberValue(...)` with an `out bool isKeyRead` output (outputs are fine; no behaviour
flags). Outer loop keeps its case-mapping peel and bare-body bookkeeping.
2. **Scope record.** `(ParameterExpression groupingParameter, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool
isComposite, MongoGroupElementTranslator translator)` threaded through ~13 helpers → `private readonly record
struct GroupBindingScope(...)`, built once per entry point; pending-orderings loop uses `scope with { Grouping =
... }`. Do this only if it clearly reads better at the call sites; it's the largest mechanical change in the task.
3. **Accumulator classification.** Between `TryBindAccumulator` and `TryBindFilteredAccumulator`: selector op
mapping (~794-802 / ~1388-1393), Count/LongCount-without-predicate test (~771-774 / ~1362-1366), `$sum:{$cond:
[pred,1,0]}` (~787-789 / ~1378-1381), "filter may empty a non-nullable $min/$max/$avg" (~1277 / ~1420). Small
named helpers.
4. **State predicates.** `IsDistinct && !IsGroupBy && Grouping != null` (NativeCardinalityBinder ~46, ~237;
NativeSlotPopulator ~130, ~189, ~534; MongoSelectDefinition ~167) and `IsGroupBy && !IsDistinct && Grouping !=
null` (NativeCardinalityBinder ~247, ~257; MongoSelectDefinition ~168, ~750) → properties on
`MongoSelectDefinition` (e.g. `IsProjectedDistinctOutput`, `IsKeyedGroupOutput`). Op allow-list
`Count/LongCount/Any/All || (Sum/Min/Max/Average && selector != null)` (NativeCardinalityBinder ~239-242,
~249-252) → `IsPostGroupAggregateShape(op, selector)`.
5. **Cardinality construction 3×.** `BuildEmptyBehavior` + presence + `ForAggregate` (NativeCardinalityBinder
~364-387, NativeGroupByBinder ~1536-1546, ~1927-1930) → `internal static MongoCardinality
NativeCardinalityBinder.CreateAggregateCardinality(MongoAggregateOperator op, MongoExpression? operand, Type
resultType)`, deriving presence from op. Only if the three really agree.
6. **Smaller.** `NativeSlotPopulator` AND-flattener 2× (~399-410, ~460-471) → reuse
`NativeSelectManyBinder.FlattenAndAlso` (~395) moved to a shared internal location; `PlaceholderTable` six
`Create*Placeholder` methods share index/add/sentinel block and the tuple type is spelled twice → `private
BsonValue Add(...)` + positional record struct (keep the MongoPipelineFactory deconstruction ~681 working);
Skip/Take paging marking (NativeSlotPopulator ~313-316, ~321-324) → into `PopulatePagingSlot`; `isComposite`
computed identically (~240, ~1475, ~1572) → private `IsCompositeKey(keyParts)` (do NOT use
`MongoGrouping.IsCompositeKey` — differs on zero parts); `TryResolveDistinctOrderingKey` field-or-alias read 2×
(~1862, ~1877) → local function.
7. **Constant-or-parameter → MongoExpression 4×** (`NativeSlotPopulator.TranslateCountExpression` ~792-801,
`NativeCardinalityBinder.TryTranslateContainsItem` ~182-194, `NativeGroupByBinder.TryTranslateComparisonConstant`
~1680-1701, `MongoExpressionTranslator.TranslateValue` ~1990-2002): only consolidate the copies that are
behaviourally identical; do not route sites through TranslateValue if that adds valueType/array-element support
they lack.

### Task 4: Native projection / join / SelectMany binders

Files: `Query/NativeTranslation/NativeProjectionBinder.cs`, `NativeCorrelationMatcher.cs`,
`NativeSelectManyBinder.cs`, `NativeJoinScopeProjectionBinder.cs`, `NativeJoinScopeTranslator.cs`,
`MongoTransparentScopeResolver.cs`.

1. **FK-correlated `Where(EntityQueryRoot, pred)` 5×.** `NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation`
(~160-178); `NativeProjectionBinder` list leaf (~2183-2207) and reducer leaf (~2416-2433); `NativeSelectManyBinder`
(~181-191, ~266-276). Add `TryMatchRootWhere(Expression, out EntityQueryRootExpression root, out LambdaExpression
predicate)` and build the correlated matcher on it; ProjectionBinder sites call the matcher. Collision staging
(`NativeCorrelationMatcher` ~204-222 vs `NativeProjectionBinder` ~2209-2228) and the staged-or-pending-lookup-
with-alias probe (~208, ~2219, ~2481) → `TryStageNativeCollectionLookup(...)` in NativeCorrelationMatcher.
2. **Per-leaf staging bookkeeping 4×** in NativeProjectionBinder (~176-188, ~246-256, ~285-292, ~416-424, plus
parallel-list appends ~166, ~462): one local `TryStageLeaf(alias, leaf, value, throwsOnNull, isArrayLeaf =
false, isOwnedNavEntityLeaf = false)` keeping `projections`/`leafIsArray`/`leafIsOwnedNavEntity` index-aligned.
The two optional bools are data, not behaviour switches — acceptable; if it ends up needing more, stop.
3. **Plain-field test.** `(x is MemberExpression || EF.Property call) && translator.TryTranslateField(x, out f)`
at ~700, ~1024, ~1044, ~1067 → `TryTranslatePlainField(translator, expr, out field)`; merge the plain-field and
string-sequence arms (~1024-1058) by peeling the string-sequence call first, if that keeps the dotted/non-default
decline identical.
4. **SelectMany owned unwind.** `TryBindOwnedInnerFilter` (~734-771) re-implements `TryTranslateReferenceFilterLayer`
(~413-432) → call it (rename to `TryTranslateScopedFilterLayer` if apt). Owned-nav resolution + filter + unwind in
`TryBind` (~62-72, ~104-109) and `TryBindBareNavUnwind` (~128-145) → `TryResolveOwnedUnwind(...)`.
5. **JoinScopeProjectionBinder.** Eligibility guard 3× (~76-82, ~383-390, ~411-418) → `TryGetEligibleScope(...)`;
add-with-dedup 5× (~150-158, ~217-223, ~237-242, ~250-255, ~292-297) → local `TryStage(alias, value,
throwsOnNull = false)`.
6. **Smaller.** `hopNames: ["Outer", "Inner"]` at all ~8 call sites → one static
`MongoTransparentScopeResolver.TransparentIdentifierHops` (or drop the parameter if every caller passes the same
value); NativeProjectionBinder identical Include-peel loops (~1314-1319, ~1348-1352) → `PeelIncludes` (or an
`UnwrapIncludes()` extension shared with Task 6 item 6 — create it in `ExpressionExtensionMethods` here);
`NativeSelectManyBinder.UnwrapAsQueryable` (~773) vs `MongoExpressionTranslator.MethodCalls` ~261 — share only
if the extra DeclaringType check can't reject something that reaches the SelectMany site;
`NativeJoinScopeTranslator.InnerAccessDetector.VisitMember` (~453) restates `IsBareInnerAccess` (~298) → call it;
`valueMode ? TryTranslateValue : TryTranslate` 3× (~74, ~125, ~398) → tiny local helper.

### Task 5: MongoExpressionTranslator

Files: `Query/NativeTranslation/MongoExpressionTranslator*.cs`.

1. **Inner-scope string field resolver ~7×.** `TryResolveMember(x, out p, out path, out isOuter) && !isOuter &&
p.ClrType == typeof(string)` then `new MongoFieldExpression(p, path)` at Regex.cs ~86, ~104, ~126; Like.cs ~66;
StringEquals.cs ~57; CaseMapping.cs ~46; MongoExpressionTranslator.cs ~1036; non-string variant at ~149
(`TryTranslateField`) and ~293 (TimeOfDay). Add `private bool TryResolveInnerField(Expression, [NotNullWhen(true)]
out MongoFieldExpression? field)` in Members.cs. Do NOT use at Contains-item arm (~918) or regex receiver (~989).
StringEquals: today an outer left side declines; check the helper doesn't change which branch runs — if it does,
skip that site.
2. **Math operand loop.** `Math.cs` angle (~84-95), binary (~113-122), unary (~124-132) paths and
`TryTranslateRoundOrLog` (~135-157, whose `allowNumericWidening` param is always true) → one
`TryBuildMathExpression(MethodCallExpression call, MongoMathFunction function, out MongoExpression? result)`.
3. **Owned reference path.** `Members.TryResolveOwnedReferenceNavigationPath` (~493-506) re-implements
`TryWalkEmbeddedReferenceHops` (~385-399); sole caller ~455-469 only reads the target type → call the walker.
4. **Composite `_id.` path rule 3×** (Members.cs ~135-137, ~430; EntityEquality.cs `GetKeyFieldPath` ~149) → use
`GetKeyFieldPath` everywhere (rename only if the new name is clearly more accurate; it's internal and used by
`NativeJoinScopeTranslator`).
5. **String concat 2×.** `TranslateStringConcat` (~1916-1930) and `MethodCalls.TryTranslateStringConcatMethod`
(~804-824) → `TranslateConcatParts(IEnumerable<Expression> parts, bool allowNumericWidening)`.
6. **Constant array literal 2×** in `TranslateInValues` (~638-654) and `TranslateInValuesRaw` (~700-712) →
`TryBuildConstantArray(NewArrayExpression, Type elementType)`.
7. **Element-predicate translator setup 2×** (~1094-1101 quantifier, ~1811-1825 filtered count) →
`CreateElementPredicateTranslator(LambdaExpression, IEntityType, out bool isCorrelated)` — only if it reads
clearly; skip otherwise.
8. **Type tests.** `TryMatchIntegralToString` 8-type list (~791) → in terms of `IsIntegerType`; `IsNumericType`
(~1977) → `IsIntegerType(type) || float/double/decimal` — only if sets are identical. "No hierarchy" check
(`BaseType is not null || GetDirectlyDerivedTypes().Any()`) at EntityType.cs ~70, TypeIs.cs ~45,
StreamingEligibility.cs ~40 → small internal extension.

### Task 6: Driver-LINQ bridge and projection binding

Files: `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor*.cs`, `MongoProjectionBindingExpressionVisitor*.cs`,
`LocalCollectionFilterFoldingVisitor.cs`, `Query/Expressions/LookupExpression.cs`,
`Query/Expressions/MongoSelectDefinition.cs`, `Query/Expressions/MongoQueryExpression.Lookup.cs`.

1. **AppendStage construction.** `.LeftJoin.cs` `AppendRawStage` (~407-425) is the general helper;
`AppendBsonStage` (~1153-1167) and three inline copies in `EmitLookupStages` (~1430-1473) and the vector-search
score stage in `MongoEFToLinqTranslatingExpressionVisitor.cs` (~584-606, second call only) → call `AppendRawStage`.
2. **Bind-whole-leaf 3 lines ~12×** in MongoProjectionBindingExpressionVisitor (~224, 237, 250, 260, 304, 315,
326, 381, 392, 405, 946; .Lookup.cs ~265) → `private ProjectionBindingExpression BindWholeLeaf(Expression leaf)`.
3. **Five identical First/FirstOrDefault/Single/SingleOrDefault/Any arms** (~1113-1166) → one arm with a static
`Dictionary<MethodInfo, MethodInfo>` Queryable→Enumerable map. Keep Count arms (~1091-1105) separate.
4. **"Tree contains X" visitors.** `QueryParameterDetector` (~1745), `ShaperReferenceDetector` (~1780),
`.LeftJoin.cs IncludeExpressionFinder` (~1070), `NavigationCountFinder` (~1034) → shared `internal static class
ExpressionSearch { static bool Contains(Expression, Func<Expression,bool>) }` in `Query/` (a single predicate
argument is fine). Leave `.StoredOrdering.cs ParameterFinder` (~778) alone (doesn't descend into extensions).
Verify each finder's VisitExtension behaviour matches the shared one; skip any that differ.
5. **StoredOrdering partial.** 5a constructed-member resolution (`.StoredOrdering.cs` ~611-632 `WalkMemberOfValue`,
~706-719 `ResolveValues`; smaller in `.GroupingKey.cs KeySubstitutor` ~344) → `TryResolveConstructedMember(Expression
definition, string name)`; 5b parameter→sources→FindElementExpressions (~266-279, ~676-693) →
`FindParameterElements`; 5c GroupBy result-selector probe (~291, ~922) → `FindGroupByResultSelector`.
6. **Small.** `x.LocalField.StartsWith(y.As + ".", Ordinal)` (.LeftJoin.cs ~898, 900, 928, 1125;
MongoQueryExpression.Lookup.cs ~61) → `LookupExpression.ReadsOutputOf(...)`; `TryGetProjectionAlias(name, out
var a) ? a : name` (MongoProjectionBindingExpressionVisitor ~455, ~1586; QMTEV ~855; MongoSelectDefinition ~401,
~445) → `MongoSelectDefinition.ResolveProjectionAlias`; `Mql.Field` call construction
(MongoEFToLinqTranslatingExpressionVisitor ~382-502, ~1106) → `MqlField(...)`; nullable
`StructuralTypeShaperExpression(T, Convert(Convert(p, object), ValueBuffer), nullable: true)` (~10 sites) →
`CreateNullableEntityShaper`; Local-kind DateTime triggers (main ~700, ~785; .GroupingKey.cs ~195, ~205) →
`IsServerDatePart`/`IsCalendarAdd`; Count rewrite guard + method choice (~848/883 vs ~1062/1113);
`IsQueryableOrEnumerable(MethodInfo)` for ~9 declaring-type checks; arithmetic list in
MongoProjectionBindingExpressionVisitor (if Task 1 G didn't cover it); `while (x is IncludeExpression i) x =
i.EntityExpression;` loops (QMTEV ~1175, ShapedQueryCompiling ~417, MongoQueryExpression.cs ~172,
MongoProjectionBindingExpressionVisitor.Lookup.cs ~294) → `UnwrapIncludes()` extension (reuse if Task 4 created
it).
7. **Transparent scope depth (riskier — do last; skip if any doubt).** `.LeftJoin.cs` lookup-field rewriter
(~1287-1299, `TryGetDepth`/`Descend`/`NestingDepth` ~1337-1368) re-implements
`MongoTransparentScopeResolver.TryResolveScopeDepth` (~39-73). Replace only if the interleaved and
self-referencing join tests (find them: grep FunctionalTests/SpecificationTests for interleaved/self-ref join
tests under DriverLinq) pass and you can show the depth mapping is identical.

### Task 7: Queryable translator, shaped compiler, materializers

Files: `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`,
`MongoShapedQueryCompilingExpressionVisitor.cs`, `MongoMixedProjectionBindingRemovingExpressionVisitor.cs`,
`MongoProjectionBindingRemovingExpressionVisitor.cs`, `Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs`,
`NativeDateTimeKindReadBack.cs`, `Storage/BsonBinding.cs`, `Query/Expressions/LookupExpression.cs`,
`src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs`.

1. **Join-scope gate core.** `IsSingleEligibleNativeJoinScope` (~937-962) and `TryGetGroupByJoinScope`
(~1083-1092) both check JoinScope non-null, Levels.Count == Joins.Count, !HasUnsupportedOperator,
!HasTerminalOperator, UnwindSource == null, every Join.Lookup non-null → `HasCompleteJoinScope(MongoQueryExpression,
[NotNullWhen(true)] out MongoJoinScope? scope)`; each gate keeps its extra checks.
2. **Single-level join confirmation.** `AddLookup(join.Lookup)` + `MarkReferenceIncludeConfirmed()` +
`MarkJoinLookupConfirmed()` at the `TranslateSelect` arms gated on `Levels.Count: 1` (~482-498, ~645-650) →
`NativeJoinScopeProjectionBinder.ConfirmEntireChain` (~520). Do NOT change the arm at ~453-465 (no single-level
gate — switching it is a behaviour change; out of scope).
3. **Raw-key lookup.** Hop-anchor resolution (~2581-2594, ~2791-2803) → `TryResolveKeyHopAnchor(...)`;
`new LookupExpression(... forceUnwind: true) { PreserveNullAndEmptyArrays = IsLeftOuter }` + through-alias prefix
(~2915-2927, ~2947-2956) → `BuildRawKeyJoinLookup(innerEntityType, pairs, throughJoin, joinInfo)`.
4. **Alias binding.** Document-construction carve-out (~785-789, ~3343-3348) → `TryBindDocumentConstruction`;
rename `BindSelectManyMember` (~3270) to `BindProjectionByAlias` since it's generic; 4 copies of `return
source.UpdateShaperExpression(BindSelectManyMember(mongoQ, SyntheticBareProjectionAlias, selector.Body))`
(~549, 559, 587, 606) → local function.
5. **Set ops.** `AppendSetOperation(new MongoSetOperation(...))` + `IsSetOp = true` 3× (~3412, ~3433, ~3486) →
`AppendSetOperationLink(...)`; `IsPlainDistinctSelect` (~3614) / `IsPlainGroupBySelect` (~3631) shared 8
conjuncts → one shared helper for the common conjuncts only, if it needs no behaviour flag (a data parameter like
`allowPreCombineLookups` is acceptable only if it's a single obvious difference).
6. **LookupExpression pipeline form 2×** (`ToLookupStageDocument` pipeline branch ~347-362,
`CompositeKeyLookupStageDocument` ~380-405) → `PipelineLookupStage(BsonValue let, BsonValue matchExpr)`.
7. **EnumerableMethods boilerplate.** `types => [typeof(IEnumerable<>).MakeGenericType(types[0])]` ~10× in the
new block → local `GetSourceOnly(string name)`.
8. **Shaped compiler.** `(bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(rootEntityType,
mongoQ, bsonDoc, behavior)` 5× (~192, 207, 237, 250, 314) → small factory; inline `TryBuildAggregateFactory`
(~793) which only reorders args of `TryBuildPipeline`.
9. **Mixed binding-removing visitor.** `_outer` redirect 5× (~218, 351, 613, 652, 749) → `ResolveSourceDocument(Expression?
documentExpression)`; scalar property read in `ClientPropertyReadRewriter.TryRead` (~599-622) and
`ResolveArithmeticOperand` (~741-757) → `TryReadScalarProperty(Expression node, Type type, out Expression read)`
(leave `TryBindStringSequenceLeaf` alone); aliased-type-mismatch check (base ~261-268, mixed ~319-326) →
`protected static void ThrowIfAliasedTypeMismatch(IProperty, Type)` in the base visitor.
10. **NativeDateTimeKindReadBack.** `select.PriorGrouping != null ? Level.GroupInput : Level.Document` 4× →
`GroupInputLevel(select)`; level→(grouping, input level) mapping in `CompositeGroupingAt` (~293) and
`TryResolveArrayReduce` (~404) → `GroupingAt(select, level)`; `Find(...) != null || (IsDateTimeTyped(x) &&
ReferencesKindSensitiveDate(...))` 2× → `CarriesKindSensitiveDate(...)`.
11. **BsonBinding.** Kind-aware factory (~251-257) delegate to the serializer overload (~264-267), same for the
AtPath pair (~286-292) if clean; "null for required non-nullable property" throw 3× (`GetPropertyValue` ~383,
`GetPropertyValueOrPlaceholder` ~431, `GetPropertyValueAtElement` ~454) → `ThrowIfNullForRequired<T>(...)`, and
share the message with the streaming rewriter (~524, ~1028) if they are meant to be identical.
12. **Streaming rewriter Expression builders.** Null-guarded sub-document descent 2× (~411-422, ~436-447) →
`BuildNullGuardedDescent(EntityPlan)`; `GetCurrentBsonType == Null` test 4× → `IsCurrentNull()`; element-name
dispatch `IfThenElse(StringEquals(_name, name), body, chain)` 4× → `Dispatch(...)`; `InvalidOperationExceptionCtor`
(~908) and `RequiredPropertyNullExceptionCtor` (~1041) are the same ctor → keep one.
