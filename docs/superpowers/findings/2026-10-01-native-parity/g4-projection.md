# g4-projection: ProjectionTests + MqlMethodTests native gaps, plus F3 / F6 / F10

Static analysis only (no build/run). Line numbers are from EF-322c at HEAD; "excerpt" lines are approximate, function names are authoritative.
Every decline below was traced by reading the code, NOT observed at runtime. Each task's first TDD step must confirm the predicted decline site.

## 0. Findings in one screen

| Test(s) | Root cause | Decline site |
|---|---|---|
| to_anonymous_via_mql_field, mixed_ef_property_and_mql_field, entity_and_mql_field, nested_anonymous_with_mql_field | `Mql.Field(p,"name",ser)` leaf is never resolved to a field. | NativeProjectionBinder.TryTranslateLeafCore (~1022): plain-field arm only matches MemberExpression / EF.Property. Falls to the end `result = null!; return false` (~1239). Wrapped arm then `return false` (~173) => Route=Fallback => gate throw MongoShapedQueryCompilingExpressionVisitor.cs:~321. |
| calculated_from_ef_property_and_mql_field | Mql.Field as ternary test. | MongoExpressionTranslator.TranslateOperand ConditionalExpression arm -> TryTranslate(test) -> TranslateNode default arm -> TryResolveMember (Members.cs:44) false -> null. |
| Sum_with_mql_field_and_field | Mql.Field operand of the Sum selector. | NativeCardinalityBinder.cs:~318 `translator.TryTranslateValue(selector.Body)` -> TranslateOperand -> TryResolveMember false. |
| deeply_nested_anonymous, nested_anonymous_with_calculated_fields, nested_entity, nested_entity_and_scalar, complex_combination_nested | Nested `new{...}` leaf is only admitted by TryGetDocumentConstructionLeaf (NativeProjectionBinder ~960) when EVERY member is a plain top-level default-serialized field (IsPlainTopLevelFieldRead). A computed member, a whole-entity member, or a deeper nest declines the whole leaf. | TryGetDocumentConstructionLeaf returns false -> TryTranslateLeafCore end (~1239). |
| to_tuple | `Tuple.Create(a,b,c)` is a static MethodCallExpression, not NewExpression. TryGetProjectionMembers has no arm; the positional-ctor arm needs `NewExpression{Members:null}`. | Falls to the default (bare body) arm of TryPopulateNativeProjection: TryBindAsBareProjection -> TryTranslateLeaf false; IsRowIndependentLeaf false; IsClientOnlyWholeEntityExpression false => `return false`. |
| owned_type | `new{ p.name, Car = p.parkingCar }`: member name `Car` != owned element name `parkingCar`. | TryTranslateLeafCore owned-nav arm (~1086-1095) requires `alias == ownedNavElementName`; alias = member name via DeriveWrappedLeafAlias. Pinned as an intentional decline by NativeOwnedReferenceWholeEntityTests.Renamed_owned_reference_entity_leaf_falls_back_but_still_reads_correct_values (must be flipped). |
| with_subquery_count | `p.mainAtmosphere.Count()` is Enumerable.Count over a PRIMITIVE collection (string[]). TranslateOperand's count arm (MongoExpressionTranslator.cs:~1799) needs TryResolveOwnedCollectionPath (embedded collection nav only). | TryTranslateLeafCore end (~1239). |
| MqlMethodTests x4 | Mql.Exists / IsMissing / IsNullOrMissing have no native arm at all. | TranslateNode default arm (MongoExpressionTranslator.cs:~1184-1200) returns null -> MarkNotNativelyRepresentable -> gate throw at MongoShapedQueryCompilingExpressionVisitor.cs:~784 ("not natively representable"). |

Read side is already generic for most of this: MongoProjectionBindingExpressionVisitor.IsScalarMethodPropertyAccess registers Mql.Field whole; the remover reads it raw by alias (remover "Non-property expressions (arithmetic, constants, Mql.Field)" arm). Nested NewExpression is walked by VisitNew with EnterProjectionMember, and ApplyProjection (MongoQueryExpression.cs:116) keys the alias by `projectionMember.Last.Name`. So nested constructions need only EMIT-side flattening with unique last-names.

## 1. TDD tasks (order matters: T1 unblocks 6 tests)

Harness for every task: add `MongoQueryMode? mode` to GuidesDbContext.Create (tests/.../Entities/Guides/GuidesDbContext.cs) -> `new MongoDbContextOptionsBuilder(b).UseQueryMode(mode)`, and add a `NativeProjectionParityTests` class (ReadOnlySampleGuidesFixture) that runs each existing ProjectionTests query under NativeOnly and compares to DriverLinq via `NativeModeAssert.NativeAndParity`. Red first, then implement. Full EF8/EF9/EF10 suite + MONGODB_EF_NATIVE_ONLY=1 spec run before claiming done (dispatch/allow-list change, per memory).

### T1 Mql.Field resolves to a field (6 tests)
Files: MongoExpressionTranslator.Members.cs (TryResolveMember), NativeProjectionBinder.cs (TryTranslateLeafCore plain-field arm; MqlFieldMethodInfo already at ~1441).

In TryResolveMember's `switch (node)` add an arm BEFORE `default`, and a separate resolution because Mql.Field names the ELEMENT, not the CLR property:

```csharp
// Mql.Field(p, "name", serializer): receiver may be wrapped in nav-expansion auto-Include layers (entity with owned navs
// => Mql.Field(IncludeExpression(p, ...))). Peeling is safe: an include changes what is materialized, not which doc is read.
case MethodCallExpression { Method.IsGenericMethod: true } mql
    when mql.Method.GetGenericMethodDefinition() == MqlFieldMethodInfo
         && mql.Arguments is [var mqlReceiver, ConstantExpression { Value: string mqlName }, _]:
{
    var peeled = mqlReceiver.RemoveConvert();
    while (peeled is IncludeExpression include) peeled = include.EntityExpression.RemoveConvert();
    if (peeled is not ParameterExpression mqlParam) return false;
    isOuter = _outerParam is not null && ReferenceEquals(mqlParam, _outerParam);
    var scope = isOuter ? _outerEntityType! : _entityType;
    // top level only, by stored element name, never composite-key components
    var byElement = scope.GetProperties()
        .Where(p => !IsCompositeKeyComponent(p) && p.GetElementName() == mqlName).ToList();
    if (byElement.Count != 1) return false;
    var prop = byElement[0];
    // TField must match; serializer arg is ignored exactly as TryRecognizeVectorScoreLeaf does (a query-parameterized
    // static singleton can't be inspected at compile time). Non-default mappings decline so the ignored serializer cannot matter.
    if ((Nullable.GetUnderlyingType(mql.Type) ?? mql.Type) != (Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType)
        || !NativeGroupByBinder.HasDefaultKeySerialization(prop)) return false;
    property = prop; fieldPath = prop.GetElementName(); return true;
}
```
(Move MqlFieldMethodInfo to a shared internal static, e.g. on MongoExpressionTranslator, and have NativeProjectionBinder reference it; don't add a third reflection copy.)
Careful: `_innerPrefix`/DistinctAliasScope cases: reuse the same guards the fast path has after the switch (DistinctAliasScope returns false for Mql: add `if (DistinctAliasScope is not null && !isOuter) return false;`).

TryTranslateLeafCore (~1022): widen the guard
`(leafExpression is MemberExpression || (leafExpression is MethodCallExpression c && (c.Method.IsEFPropertyMethod() || IsMqlFieldCall(c)))) && translator.TryTranslateField(...)`.
Leave IsPlainTopLevelFieldRead/TryGetDocumentConstructionLeaf alone (Mql members in a nested construction go through T2's flatten).
Expected MQL: `$project: { Name: "$name", Order: "$orderFromSun" }` (same as EF.Property leaves). Ternary leaf: `Label: {$cond:{if:"$hasRings", then:{$literal:"Ringed"}, else:{$literal:"Plain"}}}`. Sum: `$group {_id:null, v:{$sum:{$add:["$orderFromSun","$orderFromSun"]}}}`.
Tests (existing names): Select_projection_to_anonymous_via_mql_field, _mixed_ef_property_and_mql_field, _calculated_from_ef_property_and_mql_field, _entity_and_mql_field, Sum_with_mql_field_and_field. New unit tests in MongoExpressionTranslatorTests: Mql.Field with unmapped name declines; with BsonRepresentation/converter property declines; TField mismatch declines; outer-param Mql.Field resolves isOuter.
Risks: serializer arg ignored (documented divergence only when a user passes a representation-differing serializer for a default-mapped property). Shared TryResolveMember means Where/OrderBy/GroupBy now accept Mql.Field too (strictly more native, same semantics).

### T2 Nested constructions: flatten with unique last-name aliases (5 tests)
File: NativeProjectionBinder.TryPopulateNativeProjection wrapped arm (`case NewExpression or MemberInitExpression when TryGetProjectionMembers(...)`, excerpt ~104).
Keep TryGetDocumentConstructionLeaf for all-plain nests (sub-document, unchanged). Only when a nested construction value is REJECTED by it, replace the member by its members, recursively (depth cap 8):

```csharp
private static IReadOnlyList<(string MemberName, Expression Value)> FlattenNestedConstructions(
    MongoExpressionTranslator translator, IReadOnlyList<(string MemberName, Expression Value)> members, int depth = 0)
{
    var flat = new List<(string, Expression)>(members.Count);
    foreach (var (name, value) in members)
    {
        if (depth < 8
            && value.TryGetProjectionMembers(out var nested)              // anonymous New with Members, or parameterless MemberInit
            && !TryGetDocumentConstructionLeaf(translator, value, out _)) // all-plain nests stay sub-documents
        {
            flat.AddRange(FlattenNestedConstructions(translator, nested, depth + 1));
            continue;
        }
        flat.Add((name, value));
    }
    return flat;
}
```
and iterate `FlattenNestedConstructions(translator, wrappedMembers)` instead of `wrappedMembers`. The existing `seenAliases.Add(alias)` (case-insensitive) already declines duplicate last-names across levels, which is exactly the collision the read side would otherwise rename (`DoubleOrder0`). No read-side change: VisitNew already enters the member path and ApplyProjection takes `Last.Name`.
All 5 tests have unique last-names (checked: complex_combination: Name,Order,Planet1,Planet2,DoubleOrder,RingScore,OrderSquared,OrderPlusTen,SubName,SubPlanet,SubCalc,SubRingScore). Whole-entity nested leaf `SubPlanet = p` takes the existing `allowWholeRootEntityLeaf` `$$ROOT` arm; two `$$ROOT` leaves dedupe onto one EntityProjectionExpression alias (as Planet1/Planet2 already do for flat bodies).
Expected MQL deeply_nested: `{name:"$name", orderFromSun:"$orderFromSun", hasRings:"$hasRings", DoubleOrder:{$multiply:["$orderFromSun",2]}}`; nested_entity: `{Planet:"$$ROOT", SubPlanet:"$$ROOT"}`.
Late-fallback (Native, TryBuildNativeFactory decline) reads the same flat members through the mixed reader exactly as a flat computed leaf does today (no worse than flat).
Risks: alias collision declines are intentional (fallback). Do NOT flatten MemberInit with ctor args (TryGetProjectionMembers returns false -> declines). Add unit test: `new{ A = x.A, Sub = new{ A = x.B + 1 } }` declines (duplicate alias) and DriverLinq/Native agree.

### T3 Tuple.Create (1 test)
Normalize BEFORE both binder and shaper see the body, but only when native admits it. In MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect, branch `else if (!IsTransparentIdentifierSelector(selector) && ...)` (excerpt ~line 740 region):

```csharp
var nativeSelector = NativeProjectionBinder.NormalizeTupleFactory(selector);   // Tuple.Create<..>(a,b,..) -> new Tuple<..>(a,b,..) for <=7 args
if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, nativeSelector))
    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
else { selector = nativeSelector; /* then existing HasPositionalCtorProjectionShaper arm builds from selector.Body */ }
```
NormalizeTupleFactory: `MethodCallExpression{Method:{IsStatic:true, Name:"Create", DeclaringType: var t}} when t==typeof(Tuple)` -> `Expression.New(call.Type.GetConstructors().Single(), call.Arguments)`; recursion only on top-level body (nested Tuple.Create stays -> declines).
Do not widen TryGetProjectionMembers (AGENTS: allowPositionalConstructorArguments is shared with GroupBy/SelectMany). Result: existing positional arm + IsScalarPositionalConstruction + BuildPositionalCtorProjectionShaper. MQL `{_ctorArg0:"$name", _ctorArg1:"$orderFromSun", _ctorArg2:"$hasRings"}`.
Risk: the late-fallback mixed reader sees `new Tuple` instead of the call: VisitNew then binds args per Item1.. member via TryMatchConstructorArgumentMembers when possible (precedent: KeyValuePair tests). Verify with explicit DriverLinq + Native legs.

### T4 Owned reference nav with renamed member (1 test)
NativeProjectionBinder.DeriveWrappedLeafAlias: when `TryGetOwnedReferenceNavigationLeaf(...)` matches, return `nav.TargetEntityType.GetContainingElementName()` regardless of member name (arrays keep their stricter rule). Then the existing arm (alias == element name) admits; `alias != memberName` registers `namedAliasOverrides (Car -> parkingCar, DocumentPath tier)`, which ApplyProjection and the fallback strip already honor. Needs mongoQ + outerParameter (already parameters of DeriveWrappedLeafAlias).
Flip the pin NativeOwnedReferenceWholeEntityTests.Renamed_owned_reference_entity_leaf_falls_back_but_still_reads_correct_values: NativeOnly must now succeed, and Native + explicit DriverLinq must still return correct rows (this is the proof the pin's old reason, "driver bridge renders under the same name", no longer bites: the override maps member->document path). Collision `new{ Address = b.Title, Addr = b.Address }` -> seenAliases decline.
MQL: `{name:1? "$name", parkingCar:"$parkingCar", _id:retained}` (hasOwnedNavEntityLeaf keeps owner _id).
Risk: medium; this reverses a deliberate decline. Gate: run NativeOwnedReferenceWholeEntityTests + Ef362/NativeArray suites in all three modes.

### T5 Primitive-collection Count projection leaf (1 test)
Leaf-only (not TranslateOperand, so predicates keep their current behavior), mirroring TryTranslateTimeOfDayLeaf. Add to MongoExpressionTranslator:

```csharp
public bool TryTranslatePrimitiveCollectionCountLeaf(Expression leaf, [NotNullWhen(true)] out MongoSizeExpression? result)
{
    result = null;
    if (!TryMatchCountExpression(leaf, out var source, out var predicate) || predicate is not null) return false;
    if (!TryResolveMember(Unwrap(source), out var property, out var path, out var isOuter) || isOuter
        || !property.IsPrimitiveCollection || !NativeGroupByBinder.HasDefaultKeySerialization(property)) return false;
    result = new MongoSizeExpression(path, leaf.Type, nullSafe: true);   // AGENTS: $size on missing/null errors; $ifNull mandatory
    return true;
}
```
Call it in TryTranslateLeafCore right before the arithmetic arm (~1169). Admission list at ~1201 already contains MongoSizeExpression. Bare `Select(b => b.Tags.Count)` stays declined (IsFallbackSafeBareSizeLeaf -> TryMatchRewritableBareCountBody false), so NativeComputedBareProjectionTests.Primitive_collection_bare_count_is_NOT_admitted... stays valid.
MQL `AtmosphereCount: {$size:{$ifNull:["$mainAtmosphere",[]]}}`. Note: nullSafe returns 0 where main errored on a ragged array (driver threw => out of scope; document). Alternative nullSafe:false preserves the abort; owner call, both pass the target test.

### T6 Mql.Exists / IsMissing / IsNullOrMissing (4 tests) - new MongoExpression node
New `MongoFieldPresenceExpression(string path, MongoFieldPresence kind, bool pathMayBeArray)`; kinds Exists, Missing, NullOrMissing, NotNullOrMissing (Negate(): Exists<->Missing, NullOrMissing<->NotNullOrMissing). Sealed sibling type per AGENTS (3+ dispatch sites).
Sites (all must agree; MongoExpressionNodeCoverageTests enumerates the subtype and needs a sample plus 8 table rows: Agg.CanRender true, Agg.Render rendered, AllFieldsDefaultSerialized true (+converted true), Negator.TryNegate true, PrefixRewriter.Rewrite rendered, QL.IsQueryDialectRenderable true (false when null-kind && pathMayBeArray), QL.Render rendered):
1. MongoExpressionTranslator.TranslateNode: new arm before `default`:
```csharp
case MethodCallExpression { Method: { IsStatic: true, DeclaringType: var d, Name: var n } } mqlCall
    when d == typeof(Mql) && n is nameof(Mql.Exists) or nameof(Mql.IsMissing) or nameof(Mql.IsNullOrMissing)
         && mqlCall.Arguments is [var target]:
{
    if (!TryResolvePresenceTarget(target, out var path, out var mayBeArray)) return null;
    var kind = n switch { nameof(Mql.Exists) => Exists, nameof(Mql.IsMissing) => Missing, _ => NullOrMissing };
    return new MongoFieldPresenceExpression(path, kind, mayBeArray);
}
```
TryResolvePresenceTarget: strip Convert and IncludeExpression layers; if MaterializeCollectionNavigationExpression -> `.Subquery`, then UnwrapAsQueryable (the EFToLinq visitor does the same at MongoEFToLinqTranslatingExpressionVisitor.cs:~274, which is why this works on the driver path); then try in order: TryResolveMember (scalar / primitive collection / EF.Property; mayBeArray = property enumerable non-string), TryResolveOwnedReferenceNavigationPath (mayBeArray false), TryResolveOwnedCollectionPath (mayBeArray true). Require !isOuter. A converted property is fine (presence is storage-level).
2. MongoQueryLanguageRenderer.RenderNode: Exists `{path:{$exists:true}}`, Missing `{path:{$exists:false}}`, NullOrMissing `{path:null}`, NotNullOrMissing `{path:{$ne:null}}`. IsQueryDialectRenderable: true, except null-kinds with pathMayBeArray (`{f:null}` also matches arrays CONTAINING null) -> false, so they render via $expr at top-level $match and callers inside $elemMatch decline.
3. MongoAggregationExpressionRenderer.Render/CanRender: Exists `{$ne:[{$type:"$path"},"missing"]}`, Missing `{$eq:[{$type:"$path"},"missing"]}`, NullOrMissing `{$in:[{$type:"$path"},["null","missing"]]}`, NotNullOrMissing `{$not:[{$in:[...]}]}` (use FieldRef(path, elementVariable) so $filter/$map scopes work).
4. MongoExpressionNegator.TryNegateCore: flip kind (exact complements). Not in TryFlipNegatedFlag (no Negated flag).
5. MongoFieldPrefixRewriter: `new(prefix + "." + p.Path, p.Kind, p.PathMayBeArray)`.
Expected MQL: `Where(p => Mql.Exists(p.AnOptionalString))` => `{$match:{AnOptionalString:{$exists:true}}}`; IsNullOrMissing(AnOptionalArray) => `{$match:{$expr:{$in:[{$type:"$AnOptionalArray"},["null","missing"]]}}}`.
Unit tests: translator arm per kind x {scalar, EF.Property, owned ref, owned collection (MaterializeCollectionNavigation spelling), primitive array}; negation `!Mql.Exists(..)` => Missing; unknown arg (computed) declines; element-prefixed rewrite.
Risk: MaterializeCollectionNavigationExpression / IncludeExpression spelling of the argument is inferred, not observed - first red run will show; handle in TryResolvePresenceTarget only.

### T7 F3 - non-nullable computed projection over a null operand reads 0
Root cause: MongoAggregationExpressionRenderer.WalkNullBehindNonNullableType (~1031) ends `_ => default` for MongoFieldExpression, so `x.Score!.Value + 1`, `*2`, `/2`, `Math.Abs`, `cond ? Score!.Value : 0` are classified Plain; alias read `BsonBinding.ReadElementValue<T>` -> TryReadElementValue returns true/default for BsonNull (BsonBinding.cs ~525) => 0.
Fix (reuses the existing ThrowsOnNull machinery end to end: read as T? + .Value => EF's "Nullable object must have a value."; downstream Distinct/set-op/group already decline on a flagged alias; absorbing operators decline):
a) WalkNullBehindNonNullableType: add arm gated by the existing projection-leaf-only flag `dateParts` (true only from ClassifyNonNullableValueRead; MayBeNullBehindNonNullableType passes false so group keys / accumulators / Sum are unchanged, same precedent as EF-461):
```csharp
MongoFieldExpression or MongoOuterFieldExpression when dateParts
    => new NullBehindNonNullable(MayBeNullUnlessProven(node, nonNull), false),
```
(MayBeNull(field) == property CLR type nullable; `nonNull` already carries `x.Score != null ? ... : ...` proofs via IsSameStoredValue; Coalesce is `_ => default` so `(x.Score ?? 0) + 1` stays Plain.)
b) ClassifyNonNullableValueRead: first line after the value-type check: `if (node is MongoFieldExpression or MongoOuterFieldExpression) return NonNullableValueRead.Plain;` - a bare property leaf is read property-aware (already throws) and must not flag downstream operators.
c) Belt-and-braces for malformed data (Rank missing, non-nullable property): do NOT flag non-nullable fields (would cascade-decline every `Select(x=>x.A+x.B).Distinct()/Sum()`). Optional, measure first: in MongoProjectionBindingRemovingExpressionVisitor.VisitExtension ProjectionBindingExpression arm, where it falls to the final `CreateAliasRead(projection.Alias, type)` for a computed (Binary/Conditional/MethodCall math) leaf of non-nullable value type on Route==Projection, read `CreateAliasRead(alias, type.MakeNullable())` and take `.Value`. Closes `Rank + 1` with Rank missing without any downstream flag. Needs a full-suite run (EF-461-style groups unaffected; Average over empty reads would newly throw InvalidOperationException which is also EF's behavior).
New tests (FunctionalTests/Query/NativeNullableMemberTests.cs, fixture SeedRagged: r1 Score 10 Rank 1, r2 Score null Rank 3, r3 Score missing Rank 4, r4 Score 3 Rank 2):
```csharp
[Theory]
[InlineData("add")] [InlineData("mul")] [InlineData("div")] [InlineData("abs")] [InlineData("cond")]
public void Computed_projection_over_a_null_nullable_operand_throws_instead_of_reading_zero(string shape)
{
    var collection = SeedRagged(nameof(Computed_projection_over_a_null_nullable_operand_throws_instead_of_reading_zero) + shape);
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(collection, mode);
        var q = db.Entities.AsNoTracking().OrderBy(x => x.Title);
        var ex = Assert.Throws<InvalidOperationException>(() => (shape switch
        {
            "add"  => q.Select(x => new { V = x.Score!.Value + 1 }).ToList().Select(a => a.V).ToList(),
            "mul"  => q.Select(x => (int)x.Score! * 2).ToList(),
            "div"  => q.Select(x => x.Score!.Value / 2).ToList(),
            "abs"  => q.Select(x => Math.Abs(x.Score!.Value)).ToList(),
            _      => q.Select(x => x.Rank > 1 ? x.Score!.Value : 0).ToList(),
        }));
        Assert.Contains("Nullable object must have a value", ex.Message);
    }
}

[Fact]
public void Computed_projection_over_nullable_operand_is_unchanged_when_the_operand_is_proven_or_coalesced()
{
    var collection = SeedRagged(nameof(Computed_projection_over_nullable_operand_is_unchanged_when_the_operand_is_proven_or_coalesced));
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
    var q = db.Entities.AsNoTracking().OrderBy(x => x.Title);
    Assert.Equal([11, 1, 1, 4], q.Select(x => (x.Score ?? 0) + 1).ToList());
    Assert.Equal([11, 0, 0, 4], q.Select(x => x.Score != null ? x.Score.Value + 1 : 0).ToList());
}

[Fact]
public void Distinct_over_a_flagged_computed_leaf_declines_cleanly()
    => NativeModeAssert.DeclinesCleanly(m => { using var db = CreateContext(SeedRagged("distinct"), m); /* well-formed rows only */
        return db.Entities.AsNoTracking().Where(x => x.Rank <= 2).Select(x => x.Score!.Value + 1).Distinct().OrderBy(v => v).ToList(); });
```
Verify the third by mutation (drop the Field arm; it must fail), per branch-review-coverage lesson. Rank-missing case (optional step c) test: seed {Title:"c"} without Rank, `Select(x => new { V = x.Rank + 1 })` must throw InvalidOperationException under Native+NativeOnly (driver threw FormatException).

### T8 F6 - Math.Sign / Max / Min over a nullable operand in a predicate matches null rows
Root cause: RenderMath Sign is a `$switch` (null -> default 0? no: `$gt:[null,0]` false, `$lt:[null,0]` true => -1) and Max/Min `$max/$min` skip null, so the relational null guard (`$gt:[lowerSide,null]` when MayBeNull) never sees a null; driver-LINQ throws "Expression not supported", so main had no working behavior.
Fix (smallest, fail-early, no MQL change for non-nullable operands): MongoExpressionTranslator.Math.cs TryTranslateMath, after operands are translated:
```csharp
// Sign's $switch orders null below 0 and $max/$min skip null: over a possibly-null operand they answer a non-null
// value where C# throws and where the relational null guard cannot see the null. Same set as MongoGroupElementTranslator.IsNullPropagatingMathFunction's complement.
if (!MongoGroupElementTranslator.IsNullPropagatingMathFunction(function)
    && operands.Any(MongoAggregationExpressionRenderer.MayBeNullOperand))
    return false;
```
with `internal static bool MayBeNullOperand(MongoExpression o) => o is MongoParameterExpression { ValueType: { } t } ? IsNullableClrType(t) : MayBeNull(o);` (parameter-type aware like DateAddAmountMayBeNull, so `Math.Max(x.A, capturedInt)` and `Math.Max(x.A ?? 0, 1)` stay native; a nullable field `.Value` operand declines).
Tests (UnitTests MongoExpressionTranslatorMathTests + Functional): `Math.Sign(d.D!.Value) < 0` and `Math.Max(d.D!.Value, 1.0) > 0` over rows {D=null, D=-1, D=5}: NativeOnly throws NativeTranslationNotSupportedException; Native result equals DriverLinq (driver throws ExpressionNotSupportedException => assert both throw); `Math.Max(d.D ?? 0, 1.0) > 2` and `Math.Sign(d.Rank)` (non-nullable) stay native and match in-memory. Existing Max/Min walker `MayAbsorb` stays (still needed for Length/IndexOf operands).
Alternative (more coverage, emits new MQL => owner escalation): render Sign/Max/Min null-propagating (`$cond:[{$eq:[{$ifNull:[arg,null]},null]},null,<op>]` only when operand MayBeNull) and add them to IsNullPropagatingMathFunction. Not recommended now.

### T9 F10 - bare `Select(x => x.Rank)` with Rank missing: native throws, main returned 0
Facts: main whole-entity materialization THROWS for a missing required element (main:Storage/BsonBinding.cs:229/257 "Document element ... is missing for required non-nullable property"); main's bare scalar projection went through the driver `$project {_v}` whose deserializer fills default(T) for a MISSING element but throws FormatException for explicit BSON null (probe "(int)Score! * 2 (bare)"). Branch throws via BsonBinding.GetPropertyValueAtElement (BsonBinding.cs ~470).
Recommendation: DECISION NEEDED FROM OWNER (escalation: "previously worked now fails"). My recommendation is to restore main's lenient behavior for MISSING only, because AGENTS says native-by-default is not a break because "results are unchanged", and schema-evolved collections (field added later) make `Select(x => new { x.NewField })` a very common working query; EF's model-strictness argument is already carried by whole-entity reads (which stay strict, on both main and branch).
Rule to implement = "mirror the driver": stored property missing -> default(T) (non-nullable value type only); explicit BSON null -> keep throwing; computed null -> throw (T7).
Code (projection plain-leaf read only; entity/owned materialization untouched): add to BsonBinding
```csharp
internal static T? GetScalarProjectionValueAtElement<T>(BsonDocument document, string elementName, IReadOnlyProperty property)
{
    // as GetPropertyValueAtElement, but a missing element of a non-nullable property reads default(T), as the driver's push-down did
    var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
    var projected = new BsonSerializationInfo(elementName, info.Serializer, info.NominalType);
    if (TryReadElementValue(document, projected, out T? value))
    { if (value == null && !property.IsNullable) throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name}'."); return value; }
    return default;   // missing
}
```
and call it from MongoProjectionBindingRemovingExpressionVisitor's plain-property arm (`fieldAccess.Property != null` branch, ~254) when `_queryExpression.Select.Route == NativeRoute.Projection && !ReadsUnprojectedDocuments`. If the owner prefers strictness, do nothing and record main's leniency as a main inconsistency.
Tests (NativeNullableMemberTests, seed doc {Title:"c"} with Rank missing): `Select(x => x.Rank)` and `Select(x => new { x.Rank })` under Native/NativeOnly/DriverLinq all return [.., 0]; explicit null Score bare read still throws; whole-entity `ToList()` of the same doc still throws InvalidOperationException. Probe anonymous `new { x.Rank }` on main first (probes only covered the bare spelling).

## 2. Escalate to user
- F10 behavior decision (T9). Regression if left strict.
- T4 reverses a pinned deliberate decline (renamed owned nav) and flips one test.
- T5/T6 emit new MQL for previously-fallback shapes (not a regression; owner-visible).
- T7 changes some native-passing projections from "reads 0" to "throws" (matches EF/driver; may flip spec baselines expecting 0): run MONGODB_EF_NATIVE_ONLY spec suites on EF8/9/10.
- T8 any native Math.Max/Min/Sign over a nullable operand now declines.

## 3. Risk checklist
- EF8/9/10: no new `#if`; IncludeExpression/MaterializeCollectionNavigationExpression exist in all three; QueryParameter shapes are not touched.
- Layering: translator reads metadata only (GetElementName, IsPrimitiveCollection); no serializer instantiation in Query.
- Run every new native join/projection shape under explicit DriverLinq too (AGENTS).
- Prove each new guard discriminates by mutation (delete the guard, test must fail).
