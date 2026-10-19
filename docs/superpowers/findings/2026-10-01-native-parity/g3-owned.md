# g3-owned: OwnedEntityTests (non bson_representation) + ThenIncludeThroughOwnedNavigationTests

(Research only; nothing was built or run. Static trace. Every claim below tagged [verified-by-reading] or [needs-probe].)

## 0. Summary table

| # | Tests (all fail NativeOnly with "not natively representable" unless noted) | Root cause | Decline site | Fix size |
|---|---|---|---|---|
| A | OwnedEntity_collection_can_be_tested_for_null / _not_null, OwnedEntity_collection_field_can_be_tested_for_null / _not_null | `ownedCollection == null` / `!= null` has no native arm | MongoExpressionTranslator.cs:1287 TranslateComparisonCore: `TryResolveEntityTypedOperand` (Members.cs:445) rejects collection navs (Members.cs:496 `hop.IsCollection`); falls to :1431 `TranslateOperand(left)` -> null. Surfaces as NativeSlotPopulator.cs:290 `MarkNotNativelyRepresentable()` | small |
| B | nested_one_level_first_matching_location, nested_one_level_where_no_matching_location | owned-reference == / != a captured owned instance: no native arm | TryTranslateEntityEquality (EntityEquality.cs:90 `leftIsSelf == rightIsSelf` -> false; only root PK equality exists), then TranslateComparisonCore :1431 null | medium |
| C | nested_one_level_collection_match / _not_match (`locations.Contains(location)`), _any_match / _any_not_match (`Any(l => l == location)`) | owned-collection Contains(entity) has no arm; element-scope entity equality has no arm (element translator has `SelfParam == null`, and element PK is shadow -> EntityEquality.cs:100 declines anyway) | TranslateNode Contains arms (cs:893-964) all need a scalar item; Any arm (cs:1121-1123) `elementTranslator.TryTranslate` -> null | medium (shares code with B) |
| D | OwnedEntity_dotted_scalar_leaf_projects_alongside_array_leaf, OwnedEntity_two_level_dotted_scalar_leaf_projects_alongside_array_leaf | DELIBERATE decline: sibling sweep NativeProjectionBinder.cs:472-480 + `IsWholeDocumentReadableLeaf` (:1808) rejects any dotted scalar leaf next to an array leaf (late-fallback-over-whole-document invariant). Pinned as a decline by Ef362OwnedHopArrayProjectionTests.Owned_hop_SCALAR_leaf_alongside_the_array_leaf_declines... (line 189) | NativeProjectionBinder.cs:477 `return false` | medium (emit+read) |
| E | OwnedEntity_collection_leaf_projection_with_nested_collection_element | DELIBERATE decline: `IsNativeArrayProjectionLeaf` NativeProjectionBinder.cs:1486 `!TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded)`. Pinned by NativeArrayProjectionTests.Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows (:360, asserts NativeOnly throws). The shaper side (MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection :1576) builds a bare element shaper with no nested IncludeExpression | NativeProjectionBinder.cs:1486 + VisitExtension TryBindNativeArrayProjection | larger; needs spike |
| F | ThenInclude_reaching_through_an_owned_navigation_after_a_prior_join..., ThenInclude_through_one_of_two_sibling_owned_navigations... (EF10 only) | DELIBERATE decline of `Include(Buyer).ThenInclude(Address).ThenInclude(Region)` chain: TryWalkIncludeChain QMTEV.cs:1402-1409 `if (thenNav.IsEmbedded()) { if (HasNonEmbeddedThenInclude(thenInclude)) return false; ...}`. Comment at :1559 says the shape is "already rejected" | QMTEV.cs:1406 | medium |

Suggested order: A (small, high confidence) -> B+C (shared helper) -> D -> F -> E (spike first).

## 1. Null vs missing vs empty semantics (answers the owner's question)

- Stored states of an owned collection: missing / explicit BSON null / `[]` / populated. EF writes a CLR-null owned collection as *no element* (see comment in OwnedEntityTests.cs:1425).
- Driver-LINQ bridge on main: `x.Children == null` -> `{Children: null}` (matches missing AND explicit null, NOT `[]`); `!= null` -> `{Children: {$ne: null}}` (matches `[]` and populated). [verified-by-test-comment OwnedEntityTests.cs:1425; the bridge does not rewrite collection==null]
- Branch materialization normalizes missing/null -> `[]` (MongoProjectionBindingRemovingExpressionVisitor.cs:~362 `Coalesce(bsonArrayExpression, new BsonArray())`). This does NOT conflict with the filter: the filter is evaluated server-side on the STORED document, materialization happens after. The test comment explicitly pins "`== null` still matches the row with no stored field but materializes as `[]`".
- Consequence for tests: an in-memory LINQ oracle over *materialized* entities would say `Children == null` is false for every row and is WRONG as an oracle. Oracles must be written over stored state (hand-computed expected id sets), as below.
- Native rendering must therefore be exactly "null-or-missing" for `== null`, complement for `!= null`. Do not add an `[]` arm.

## 2. Task A: owned collection `== null` / `!= null`

### Code
File: src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs, extend `TryResolveEntityTypedOperand` (:445) by one arm that accepts an owned *collection* nav chain, reusing the owned-reference result shape (ElementRef, nullSafe):

```csharp
// after the TryResolveOwnedReferenceNavigationPath arm, before `elementRef = null; return false;`
if (TryResolveOwnedCollectionPath(UnwrapAsQueryable(node), out var collectionPath, out var collectionElementType, out var collectionIsOuter))
{
    if (collectionIsOuter) { elementRef = null; return false; }   // same rule as the reference arm
    // nullSafe: a missing array must compare equal to null; $expr $eq treats missing != null otherwise.
    elementRef = new MongoElementRefExpression(collectionPath, node.Type, nullSafe: true);
    return true;
}
```
`TryResolveOwnedCollectionPath` (Members.cs:524) already requires the final hop to be an embedded COLLECTION, intermediate hops embedded single references, and `_innerPrefix == null`. TranslateComparisonCore :1296-1309 then produces `MongoBinaryExpression(Equal/NotEqual, ElementRef(nullSafe), Constant(null))` with no change. Renderer: MongoAggregationExpressionRenderer.cs:61 renders `{$ifNull: ["$Children", null]}` for NullSafe ElementRef.

[needs-probe] Tree shape. After nav-expansion the operand may be `EF.Property<List<B>>(e,"children")`, `e.children`, or wrapped. `TryBeginOwnedHopWalk` uses `TryGetMemberOrEFProperty`, which covers the first two. If it is wrapped in `MaterializeCollectionNavigationExpression` (check by printing `predicate.Body.Print()` in NativeSlotPopulator.cs:191 under a debugger), add one peel in the new arm (`node is MaterializeCollectionNavigationExpression m ? m.Subquery : node`). The bridge handles whatever shape arrives (tests pass in DriverLinq), so the shape is decidable by one debug print.

### Expected MQL
- `FirstOrDefault(e => e.children == null)`: `{ $match: { $expr: { $eq: [ { $ifNull: ["$children", null] }, null ] } } }, { $limit: 1 }`
- `e.children != null && e.children.Count > 0`: `$match: { $and: [ { $expr: { $ne: [ {$ifNull:["$children",null]}, null ] } }, <existing Count>0 array-index form {"children.0": {$exists: true}}-style> ] }`
- Equivalent to `{children: null}` for matching purposes; $expr is not index-usable (acceptable for embedded arrays; optional follow-up: a MongoFieldExpression-like query-dialect node for ElementRef null checks of non-root paths, which would also allow it inside $elemMatch; owned-reference null checks have the same limit today).
- Inside `$elemMatch` ($expr banned): `IsQueryDialectRenderable` already rejects an ElementRef compare, so `Any(c => c.Tags == null)` over a nested owned collection declines cleanly (keep; correct).

### Negation
`!(e.children == null)` goes through the existing negator ($eq<->$ne inversion is exact for null checks). Verify with a test (below).

### Tests
Failing now (NativeOnly): the four OwnedEntity_collection_(field_)can_be_tested_for_(not_)null tests (they run under default mode, so to prove native, add NativeOnly twins, see below; or flip the whole class via a `[Theory]` over MongoQueryMode).
NEW parity test (functional, new file tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionNullCheckTests.cs):

```csharp
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionNullCheckTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Parent { public string Id { get; set; } = ""; public List<Child> Children { get; set; } = null!; }
    public class Child { public string Name { get; set; } = ""; }
    // owned collection: [Owned] by model, with explicit key to keep the element shaper simple
    private static readonly Action<ModelBuilder> Model = mb => mb.Entity<Parent>().OwnsMany(p => p.Children);

    // Stored states: missing, explicit null, empty, one, two.
    private IMongoCollection<Parent> Seed(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        raw.InsertMany([
            new BsonDocument { { "_id", "missing" } },
            new BsonDocument { { "_id", "null" }, { "Children", BsonNull.Value } },
            new BsonDocument { { "_id", "empty" }, { "Children", new BsonArray() } },
            new BsonDocument { { "_id", "one" }, { "Children", new BsonArray { new BsonDocument("Name", "a") } } },
            new BsonDocument { { "_id", "two" }, { "Children", new BsonArray { new BsonDocument("Name", "a"), new BsonDocument("Name", "b") } } }]);
        return database.MongoDatabase.GetCollection<Parent>(raw.CollectionNamespace.CollectionName);
    }

    public static TheoryData<string, string[]> Cases() => new()
    {   // oracle = hand-computed over STORED state, never over materialized entities (missing/null materialize as [])
        { "eq-null",            ["missing", "null"] },
        { "null-eq",            ["missing", "null"] },
        { "ne-null",            ["empty", "one", "two"] },
        { "not-eq-null",        ["empty", "one", "two"] },
        { "ne-null-and-count",  ["one", "two"] },
        { "eq-null-or-empty",   ["empty", "missing", "null"] },
    };

    private static IQueryable<Parent> Apply(IQueryable<Parent> q, string shape) => shape switch
    {
        "eq-null" => q.Where(p => p.Children == null),
        "null-eq" => q.Where(p => null == p.Children),
        "ne-null" => q.Where(p => p.Children != null),
        "not-eq-null" => q.Where(p => !(p.Children == null)),
        "ne-null-and-count" => q.Where(p => p.Children != null && p.Children.Count > 0),
        "eq-null-or-empty" => q.Where(p => p.Children == null || p.Children.Count == 0),
        _ => throw new ArgumentOutOfRangeException(shape)
    };

    [Theory, MemberData(nameof(Cases))]
    public void Owned_collection_null_checks_match_stored_state_in_every_mode(string shape, string[] expected)
    {
        var collection = Seed(nameof(Owned_collection_null_checks_match_stored_state_in_every_mode) + shape);
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode, Model);          // copy CreateContext from NativeOwnedCollectionPredicateTests
            var ids = Apply(db.Entities.AsNoTracking(), shape).Select(p => p.Id).OrderBy(i => i).ToList();   // id-only: avoids materialization
            Assert.Equal(expected, ids);
        }
    }

    [Fact]   // materialization of a matched null/missing row stays []
    public void Null_check_match_materializes_empty_collection()
    {   /* NativeOnly Where(p => p.Children == null).AsNoTracking().ToList(): both rows have Children.Count == 0 and non-null */ }

    [Fact]   // decline stays: ElementRef compare cannot render inside $elemMatch
    public void Owned_collection_null_check_inside_elemMatch_declines_cleanly()
    {   /* class with Parent.Children[].Tags owned collection; Where(p => p.Children.Any(c => c.Tags == null)) NativeOnly -> NativeModeAssert.DeclinesCleanly; Native/DriverLinq return stored-state answer */ }
}
```
Use `Select(p => p.Id)` to keep the test on the filter, not the shaper.

### Risks
- Silent-wrong risk is low: rendering is the same `$ifNull` the owned-reference arm already uses.
- `HasValue`-style mutation test (prove it discriminates): mutate nullSafe to false; "missing" row must then fail eq-null.
- AField (field-mapped) variant: `TryGetMemberOrEFProperty` returns the member name; nav is found by `FindNavigation(name)` so field-backed navs work [needs-probe on a field member expression: `Member.Name` is the field name = nav name here].

## 3. Task B+C: owned entity equality vs a captured/constant owned value

### What main does (the oracle) [verified-by-reading]
MongoEFToLinqTranslatingExpressionVisitor.cs:1226-1233 + 1329-1399: for an owned entity type, `a == value` is rewritten to an AND of per-property equalities over ALL CLR-mapped, non-shadow properties of the owned type (`GetComparisonProperties` :1262: PropertyInfo/FieldInfo != null; NOT navigations); `!=` is the OR of `!=`. `coll.Contains(value)` -> `coll.Any(__x => per-property AND)` (:1346). Null comparand value -> no rewrite (plain null compare). So parity = per-property equality; NOT whole-subdocument equality (field order/extra fields irrelevant). Known loose edge on main (record it, do not fix on branch): nested owned navigations of the compared type are ignored (LocationWithCity.city is not compared).

### Design
New method in MongoExpressionTranslator.EntityEquality.cs (shares `TryExtractEntityMemberValue`, which already turns a constant/MemberInit/New/EF query parameter into `MongoConstantExpression`/`MongoParameterExpression(extractFromEntityValue: true)`):

```csharp
/// Element parameter of an owned-collection element lambda (Any/Contains element scope). Distinct from SelfParam on purpose:
/// SelfParam enables root-entity arms ($$ROOT, PK equality) that are wrong inside $elemMatch.
internal ParameterExpression? ElementParam { get; set; }

private bool TryTranslateOwnedEntityEquality(Expression leftSide, Expression rightSide, bool isNotEqual,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    if (_innerPrefix is not null || _outerParam is not null || ProjectedAliasScope is not null) return false;

    var left = Unwrap(leftSide); var right = Unwrap(rightSide);
    // which side is the owned operand?
    if (!TryResolveOwnedOperand(left, out var leftType, out var leftPrefix))
    { (left, right) = (right, left); if (!TryResolveOwnedOperand(left, out leftType, out leftPrefix)) return false; }
    else if (TryResolveOwnedOperand(right, out _, out _)) return false;          // owned == owned: decline

    var other = right;
    if (other is ConstantExpression { Value: null } || other.Type != leftType!.ClrType) return false;   // null handled by TranslateComparisonCore
    if (leftType.GetDerivedTypes().Any()) return false;                                                 // TPH owned: decline

    var props = leftType.GetProperties().Where(p => p.PropertyInfo != null || p.FieldInfo != null).ToList(); // mirror bridge
    if (props.Count == 0) return false;
    // A runtime-null parameter compares each property to null: only equals "location == null" when some compared
    // property can never be null/missing on a present sub-document. Otherwise decline (constants/inline new are exempt).
    if (NativeQueryParameter.TryGetQueryParameterName(other, out _) && props.All(p => p.IsNullable)) return false;

    result = CombineKeyComparisons(props, p =>
    {
        if (!TryExtractEntityMemberValue(other, p, out var value)) return null;
        var path = leftPrefix.Length == 0 ? GetKeyFieldPath(p) : leftPrefix + "." + GetKeyFieldPath(p);
        return new MongoBinaryExpression(isNotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
            new MongoFieldExpression(p, path), value);
    }, combineWithAnd: !isNotEqual);
    return result is not null;

    bool TryResolveOwnedOperand(Expression e, out IEntityType? type, out string prefix)
    {
        type = null; prefix = "";
        if (ElementParam is not null && ReferenceEquals(e, ElementParam) && _entityType.IsOwned()) { type = _entityType; return true; }
        if (TryResolveOwnedReferenceNavigationPath(e, out var p, out var nav, out var isOuter) && !isOuter && nav.TargetEntityType.IsOwned())
        { type = nav.TargetEntityType; prefix = p; return true; }
        return false;
    }
}
```
Dispatch: add in TranslateNode (cs:708 area) BEFORE `TryTranslateEntityEquality`:
```csharp
case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } ownedEq
    when TryTranslateOwnedEntityEquality(ownedEq.Left, ownedEq.Right, ownedEq.NodeType == ExpressionType.NotEqual, out var ownedEquality):
    return ownedEquality;
```
`TryResolveOwnedReferenceNavigationPath` is element-scope-correct (scope-relative via TryBeginOwnedHopWalk), so the same method serves root `p.location == x` (path `location.latitude`) and element-scope `l.Geo == x` (path `Geo.Lat`).

Element scope: in the quantifier arm (cs:1121) set `elementTranslator.ElementParam = elementLambda.Parameters[0];` (also on `correlatedElementTranslator` at :1099 is NOT needed; it is two-scope and `_outerParam is not null` makes the method decline).

Owned-collection `Contains` arm, new, in TranslateNode before cs:893 (the shape arrives as `Enumerable.Contains`, `Queryable.Contains(AsQueryable(nav), item)` or instance `List.Contains`; `TryMatchContainsMethod` (MethodCalls.cs:278) accepts only Enumerable/instance, so add the Queryable overload in a local matcher):
```csharp
case MethodCallExpression c when TryTranslateOwnedCollectionContains(c, out var ownedContains): return ownedContains;
...
private bool TryTranslateOwnedCollectionContains(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    Expression? source, item;
    if (call.Method.Name != nameof(Enumerable.Contains)) return false;
    if (call.Method.IsStatic && call.Method.DeclaringType is var d && (d == typeof(Enumerable) || d == typeof(Queryable)) && call.Arguments.Count == 2)
    { source = call.Arguments[0]; item = call.Arguments[1]; }
    else if (!TryMatchContainsMethod(call, out source, out item)) return false;

    if (!TryResolveOwnedCollectionPath(Unwrap(UnwrapAsQueryable(source)), out var arrayPath, out var elementType, out var isOuter) || isOuter)
        return false;
    var param = Expression.Parameter(elementType.ClrType, "__e");
    var elementTranslator = new MongoExpressionTranslator(elementType) { ElementParam = param };
    if (!elementTranslator.TryTranslateOwnedEntityEquality(param, item, isNotEqual: false, out var pred)) return false;
    if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred)) return false;
    result = new MongoElemMatchExpression(arrayPath, pred, negated: false);
    return true;
}
```
`!locations.Contains(x)` / `!Any(l => l == x)` ride the existing Not arm (`TryFlipNegatedFlag(MongoElemMatchExpression)`).

Method-source whitelist check: Queryable/Enumerable only; no new MethodInfo constants needed (match by DeclaringType + Name, as TryMatchContainsMethod does).

### Expected MQL
- `p.location == location`: `{$match: {$and: [{"location.latitude": {$numberDecimal..}}, {"location.longitude": ...}]}}` (the two comparisons are query-dialect; captured value is a placeholder substituted per execution via CreateEntityMemberPlaceholder).
- `!=`: `{$or: [{"location.latitude": {$ne: ..}}, {"location.longitude": {$ne: ..}}]}`
- `locations.Contains(location)`: `{locations: {$elemMatch: {latitude: .., longitude: ..}}}`; `!Contains`: `{locations: {$not: {$elemMatch: ...}}}`.
Property serializers: MongoParameterExpression -> `BsonSerializerFactory.GetPropertySerializationInfo` (decimal -> Decimal128 etc.), same machinery as root entity equality (EntityEquality.cs).

### Where a decline must stay
- owned == owned (both sides member chains), outer-scope (two-scope) operands, TPH-owned types, all-nullable props with a *parameter* comparand (runtime null would turn into per-property null checks that can match a present-but-empty subdocument; bridge compares `loc == null` instead), MemberInit missing a binding (default value unknowable; TryExtractEntityMemberValue returns false), constant-collection `list.Contains(p.Home)`.

### Tests
Failing now: nested_one_level_first_matching_location, nested_one_level_where_no_matching_location, nested_one_level_collection_match, _not_match, _any_match, _any_not_match (run them NativeOnly via a pinned twin or an env flip).
NEW parity (file NativeOwnedEntityEqualityTests.cs):
```csharp
public class Addr { public string City { get; set; } = ""; public int Zip { get; set; } }          // Zip non-nullable => parameter comparand admitted
public class P { public string Id { get; set; } = ""; public Addr? Home { get; set; } public List<Addr> Visits { get; set; } = null!; }
// Model: mb.Entity<P>().OwnsOne(p => p.Home); mb.Entity<P>().OwnsMany(p => p.Visits);
// Seed raw BsonDocuments (ids): "homeX" Home {City:"X",Zip:1}; "homeY" Home {City:"Y",Zip:1}; "homeMissing" (no Home);
//   "homeNull" Home:null; "visitsX" Visits:[{X,1},{Y,2}]; "visitsEmpty" Visits:[]; "visitsMissing"; "visitsNull";
//   "extra" Home {City:"X",Zip:1,Extra:"e"} (extra element: per-property equality still matches; whole-doc equality would not -> discriminates the design)
//   "reordered" Home {Zip:1, City:"X"} (field order: discriminates too)
Addr x = new() { City = "X", Zip = 1 };
Assert ids for: p.Home == x  -> [homeX, extra, reordered]
                p.Home != x  -> [homeY, homeMissing, homeNull]          // parity with bridge: absent Home satisfies OR of $ne
                p.Visits.Contains(x) -> [visitsX]; !p.Visits.Contains(x) -> everything else incl. empty/missing/null
                p.Visits.Any(v => v == x) same as Contains
                p.Home == new Addr { City = "X", Zip = 1 }   (inline construct)
                Addr? none = null;  p.Home == none           -> must equal DriverLinq result  (runtime-null parameter)
                p.Home == x && p.Id != "extra"
Run each in NativeOnly, DriverLinq, Native; assert NativeOnly == DriverLinq == hand-expected (hand-expected printed above; recompute from the bridge rule if DriverLinq disagrees and record it as wrong-on-main).
```
Also a unit test in tests/...UnitTests/Query/NativeTranslation for the translator: `Owned_entity_equality_translates_to_per_property_AND` using the existing translator test harness (see MongoExpressionTranslator tests there).

## 4. Task D: dotted scalar leaf next to an array leaf

### Current behaviour [verified-by-reading]
`new { e.Home.City, e.Home.Notes }`: array leaf alias is the dotted document path `Home.Notes` (DeriveWrappedLeafAlias :1547); scalar leaf `Home.City` has `MongoFieldExpression.ElementName == "Home.City"` and alias `City`. Because the shaper is built before native-vs-driver is decided and a late fallback hands it whole documents, every sibling of an array leaf must be readable from a whole document: sweep at NativeProjectionBinder.cs:472-480 rejects dotted/renamed/computed siblings via `IsWholeDocumentReadableLeaf` (:1808). A late-fallback strip (MongoShapedQueryCompilingExpressionVisitor.cs:586, `ShouldStripBareProjectionOnFallback`) requires alias == document path.

### Design (keep the invariant, widen it): give the dotted scalar the dotted path as its alias too
1. Emit (NativeProjectionBinder.TryPopulateNativeProjection, wrapped arm, line ~135-190): in the sweep at :472 (only when `hasArrayLeaf || hasOwnedNavEntityLeaf`), instead of `return false` for a leaf that is `MongoFieldExpression { ElementName: contains '.' }` whose current alias == its member name, rewrite: alias := `field.ElementName`; register `namedAliasOverrides.Add((memberName, field.ElementName))` (DocumentPath tier). Needs a parallel `memberNames` list next to `projections`/`leafIsArray` (the loop at :135 knows `memberName`). Reject (return false) when the new alias collides: equal to or a dotted prefix/extension of any other alias (`seenAliases.Any(a => a == n || a.StartsWith(n + ".") || n.StartsWith(a + "."))`), e.g. an owned-nav entity leaf `Home` next to `Home.City` (MongoDB "path collision").
2. Read (MongoProjectionBindingRemovingExpressionVisitor.cs ~line 253-268, the `fieldAccess.Property != null` arm): when `projection.Alias.Contains('.')` build the read with `BsonBinding.CreateGetPropertyValueAtPath(DocParameter, projection.Alias.Split('.'), fieldAccess.Property, projectionBindingExpression.Type)` (Storage/BsonBinding.cs:304; property-aware, absent intermediate -> default for nullable T, throws otherwise) instead of `CreateGetValueExpression` (literal element name).
3. `IsWholeDocumentReadableLeaf` stays; the rewrite runs before it. `ShouldStripBareProjectionOnFallback` is already true because an override is registered, so the fallback strips the Select and the same path read works on whole documents.
Alternative rejected: make the late fallback use MongoMixedProjectionBindingRemovingExpressionVisitor (existing for join-scope/doc-construction leaves). Rejected because the mixed route has never been reasoned with an array leaf (AGENTS.md invariant "array leaf + whole-document fallback"), and `clientConditional` leaves are already barred from array leaves for the same reason (NativeProjectionBinder.cs:436).

### Expected MQL
`$project: { "Home.City": "$Home.City", "Home.Notes": "$Home.Notes", "_id": "$_id" }` (dotted keys nest; no collision as neither is a prefix of the other). Two-level: `"Home.Inner.City"` and `"Home.Notes"`.

### Tests
Failing now: OwnedEntity_dotted_scalar_leaf_projects_alongside_array_leaf, OwnedEntity_two_level_dotted_scalar_leaf_projects_alongside_array_leaf. These use SaveChanges, then project; add NativeOnly twins.
Flip the pin in Ef362OwnedHopArrayProjectionTests.cs:189 (`Owned_hop_SCALAR_leaf_alongside_the_array_leaf_declines_and_the_fallback_returns_the_scalar_correctly`) from `Assert.Throws<NativeTranslationNotSupportedException>` to a NativeOnly success with the same `expected` array; rename. NEW (same file, uses its Seed with populated/empty/missing/null arrays):
```csharp
[Theory] [InlineData(false)] [InlineData(true)]   // shadowKey false/true
public void Owned_hop_scalar_leaf_beside_array_leaf_reads_every_array_state_in_every_mode(bool shadowKey)
{   foreach (var mode in AllModes) { ...Select(b => new { b.Home.City, b.Home.Notes }) ... expected {"NYC=[n1|n2]","LA=[]","SF=[]","DC=[]"} } }
[Fact] public void Dotted_scalar_beside_whole_owned_reference_leaf_declines_on_alias_collision()  // new { b.Home, b.Home.City }: DeclinesCleanly under NativeOnly; Native/DriverLinq correct
[Fact] public void Renamed_dotted_scalar_beside_array_leaf_reads_correctly()  // new { Town = b.Home.City, b.Home.Notes } : alias override memberName Town -> "Home.City"; all modes agree
[Fact] public void Late_fallback_reads_dotted_scalar_from_whole_documents()  // force late decline (e.g. add a client-only operator the lowerer declines, or run explicit DriverLinq) and assert same values: proves alias==path invariant
```
Plus a nullable-vs-required check: scalar `City` declared non-nullable with a row where `Home.City` missing -> same exception text in Native and DriverLinq ("missing for required non-nullable property") [parity of failure mode, assert both throw].
MQL assertion test: `Contains("\"Home.City\" : \"$Home.City\"")`.

### Risks
Silent wrong results if emit rewrite and read side disagree (the array leaf's own invariant); the paired test 'Late_fallback...' discriminates. Alias collision rule must also cover `_id` retention (existing).

## 5. Task E: array leaf whose element owns a nested collection/reference

### Current [verified-by-reading]
Gate NativeProjectionBinder.cs:1486 declines any element type with an eager-loaded navigation (the EF-360 comment at NativeArrayProjectionTests:353 says the *fallback* was fixed; the native shaper was not extended). Emit side would be fine (`$project: {Posts: "$Posts"}` carries nested arrays). Read side TryBindNativeArrayProjection (MongoProjectionBindingExpressionVisitor.cs:1606-1617) makes `innerShaper = StructuralTypeShaperExpression(element, ValueBuffer(aliasedArray.InnerProjection))` with no `IncludeExpression`, so nested `Comments` would never populate (silent empty). The removing visitor's CollectionShaper arm (MongoProjectionBindingRemovingExpressionVisitor.cs:~360-380) does `Visit(innerShaper)` then `AddIncludes(innerShaper)` driven by `_pendingIncludes`, i.e. nested collections are read from the element document by `arrayProjection.ArrayFieldName`/`AccessExpression` (the same machinery as the whole-entity doc-path route).

### Design (needs a one-hour spike before coding)
In TryBindNativeArrayProjection, after building `innerShaper`, wrap one `IncludeExpression` per eager-loaded embedded navigation `n` of the element type, recursively, mirroring MongoProjectionBindingExpressionVisitor.VisitMember's two arms (:1489-1509):
```csharp
Expression BuildElementShaper(EntityProjectionExpression elementProjection, IEntityType elementType)
{
    Expression shaper = new StructuralTypeShaperExpression(elementType,
        Expression.Convert(Expression.Convert(elementProjection, typeof(object)), typeof(ValueBuffer)), nullable: true);
    foreach (var n in elementType.GetNavigations().Where(n => n.IsEagerLoaded && n.IsEmbedded()))
    {
        var bound = elementProjection.BindNavigation(n);        // ObjectArrayProjectionExpression / EntityProjectionExpression with ParentAccessExpression
        Expression nested = bound switch
        {
            ObjectArrayProjectionExpression arr => new CollectionShaperExpression(arr,
                BuildElementShaper(arr.InnerProjection, n.TargetEntityType), n, n.TargetEntityType.ClrType),
            EntityProjectionExpression ep => BuildElementShaper(ep, n.TargetEntityType),
            _ => throw new InvalidOperationException()
        };
        shaper = new IncludeExpression(shaper, nested, n);
    }
    return shaper;
}
```
then relax NativeProjectionBinder.IsNativeArrayProjectionLeaf (:1486) ONLY for embedded eager navs (keep declining non-embedded navigations on the element: a cross-collection reference would need $lookup). Keep the comment-pinned invariant "single rule, emit and shaper share it".
Spike checklist: (1) print the Subquery that EF puts inside `MaterializeCollectionNavigationExpression` for `NestedBlog.Posts` to confirm the IncludeExpression shape the doc-path route consumes; (2) confirm `_ownerMappings`/`_ordinalMappings` (CollectionShaper arm lines ~383-386) chain correctly for shadow keys at two array depths (owner key + 2 ordinals); (3) confirm `AddIncludes` is invoked for the nested collection inside an alias-addressed outer array; (4) streaming: Projection route sets allowStreaming:false, fine.
If the spike finds the removing visitor cannot nest under an ArrayAlias root, the fallback design is to read the whole alias array through `MongoEntityMaterializerSerializer`/DOM for the element type (as Collection_of_collection_goes_native_via_dom does for whole entities) - larger.

### Tests
Failing now: OwnedEntity_collection_leaf_projection_with_nested_collection_element. Flip NativeArrayProjectionTests.Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows (:360) so NativeOnly returns rows (keep Native/DriverLinq checks). NEW ragged-data test (the whole-entity oracle exists: Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state :780; copy it for the NestedModel): seed raw docs with Posts[] where Comments is missing / null / [] / populated at each of the two levels, and assert `Select(b => new {b.Title, b.Posts})` NativeOnly == DriverLinq == `db.Entities.AsNoTracking().ToList()` projected client-side, including `Comments` element values. Also a nested owned *reference* inside the element (`Post.Geo`), and a lazy-inverse-owner element (existing test must stay green). Keep a DeclinesCleanly test for an element with a cross-collection reference navigation.

## 6. Task F: ThenInclude through an owned navigation (EF10 tests)

### Decline [verified-by-reading]
QMTEV.TryWalkIncludeChain (Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1356) sees `Include(Buyer).ThenInclude(Address /*embedded*/).ThenInclude(Region)`: at :1402 `thenNav.IsEmbedded()` and `HasNonEmbeddedThenInclude(thenInclude)` (:1563, recurses both axes) is true -> `return false` -> TryGetReferenceIncludeChain null -> the Select is marked not natively representable. The join stage already works: RebindInnerShaperToOuterQuery peels the owned segments (PeelEmbeddedSegments) and BuildNavigationJoinLookup (:2414) builds `localField: "_lookup_Buyer.Address.RegionId"` (EF-380), registered for Joins.Count > 1 (:2963-2981). So only the Include-chain recognizer rejects it, and the DriverLinq/Native-fallback path passes because the fallback reads the lookups the join stage registered.

### Design
Teach TryWalkIncludeChain to walk THROUGH single-reference embedded hops to a further REFERENCE include, tracking the embedded hop path; keep rejecting everything else.
- Replace the embedded branch (:1402-1412) with a recursive local walker over both IncludeExpression axes (EntityExpression siblings and NavigationExpression), because for sibling owned navs (ShippingAddress + BillingAddress, test 2) EF nests the sibling that holds `Region` on the EntityExpression axis of the outer include:
```csharp
// returns false => whole chain declines
bool WalkThroughEmbedded(IncludeExpression embedded, IEntityType previousTarget, JoinPathKey parent, List<string> embeddedNavs, ...)
{
    // embedded.Navigation must be an embedded SINGLE REFERENCE, else: if it (or anything under it) has a non-embedded include -> decline; else ok (plain owned auto-include, as today)
    // sibling embedded includes on embedded.EntityExpression: recurse the same way (they are owned auto-includes);
    // under embedded.NavigationExpression: if IncludeExpression with non-embedded REFERENCE nav declared on the owned type (nav.DeclaringEntityType == embedded.Navigation.TargetEntityType):
    //       referenceLevels.Add(inc); transitiveLevels.Add(inc); record (inc -> (parent include, string.Join(".", embeddedNavs element names)));
    //       continue the normal ThenInclude loop from inc (further references allowed; collection terminal as today)
    //    non-embedded COLLECTION nav behind an owned hop: decline (no single-document localField).
}
```
- Out: add `Dictionary<IncludeExpression,(IncludeExpression Parent,string EmbeddedPath)> ownedHops` to TryWalkIncludeChain/TryGetReferenceIncludeChain overloads (internal API used by 2 callers: TryGetReferenceIncludeChain, TryGetMixedReferenceAndCollectionIncludeChain).
- TryConfirmReferenceIncludeChain (:1472): for a level in `ownedHops`, require the pending lookup's `LocalField == $"{parentAlias}.{embeddedPath}.{LookupExpression.GetFieldPath(fkProperty)}"` where `parentAlias = pendingByAlias[GetLookupAlias(parentNav)].As`; mismatch -> `return false`. Existing `Joins.Count != chain.Count` stays as the guard that the join stage really produced a lookup per level (it also protects against a user Join plus an Include). Existing `transitiveLevels` exemption from the DeclaringEntityType == root check already covers owned-declared navs.
- Update the stale comment at :1559.

### Expected MQL (test asserts it, `LogTo`)
`$lookup {from: Buyers, localField: "BuyerId", foreignField: "_id", as: "_lookup_Buyer"}`, `$unwind` (preserveNullAndEmptyArrays for the optional FK), `$lookup {from: Regions, localField: "_lookup_Buyer.Address.RegionId", foreignField: "_id", as: "_lookup_Region"}`, `$unwind` preserve. Same pipeline the fallback already emits (the tests' `Assert.Contains(logs, ... localField ...)` therefore must hold unchanged).

### Where a decline must stay
`Include(b => b.Buyer).ThenInclude(b => b.Addresses /*owned COLLECTION*/).ThenInclude(a => a.Region)`: `$lookup` localField through an array would match any element's FK with no element-level correlation; keep DeclinesCleanly. Also owned-reference chain whose embedded segment names disagree with the registered lookup's LocalField (confirm-step check).

### Tests
Failing now: both ThenIncludeThroughOwnedNavigationTests (EF10 only; they pass on DriverLinq). Make the context ctors accept `MongoQueryMode? mode = null` (`new MongoDbContextOptionsBuilder(b).UseQueryMode(mode)`), add NativeOnly legs of both tests (keep the localField log assertion). NEW parity data (same Seed2 shape): buyer without ShippingAddress; address with `RegionId` missing; `RegionId` pointing at no region doc; order with no BuyerId; assert Native == DriverLinq == NativeOnly for `Order.Buyer.ShippingAddress.Region` null/non-null at each level (left-outer must preserve rows; the existing PreserveNullAndEmptyArrays logic is in the join stage). NEW DeclinesCleanly test for the owned-collection variant (needs a model with `Buyer.Addresses` owned collection with `Region` nav -- EF may refuse the model; if so skip and note).

### Risks
- EF8/EF9 shapes differ (LeftJoin shim); the walker change is shared code, tests are EF10-only: run EF8/EF9 full Include suites (ReferenceInclude/ThenInclude specs) to prove no regression (the memory note "allow-list/dispatch changes need FULL-SUITE review" applies).
- Escalation trigger (per query-reviewer rules): this changes which chains are native; no MQL change for previously-native queries.

## 7. Cross-cutting notes / things to record for main
- Wrong-or-loose on main (record, do not fix on branch): owned entity equality ignores nested owned navigations of the compared type (bridge GetComparisonProperties uses GetProperties only).
- No `$elemMatch`-dialect change: all new nodes reuse MongoElemMatchExpression/MongoBinaryExpression/MongoElementRefExpression, so MongoExpressionNodeCoverageTests needs no new subtype row.
- AGENTS.md updates when landing: (a) owned entity equality is per-property over CLR-mapped non-shadow properties, ElementParam vs SelfParam; (b) collection==null = null-or-missing on STORED doc, materializes as []; (c) dotted scalar sibling of an array leaf takes the dotted alias; (d) embedded-reference hop in TryWalkIncludeChain.
- EF-version #ifs: none required; Task F touches shared QMTEV code only; Task D/E visitors compile on all three (no new EF APIs: IncludeExpression ctor `(Expression, Expression, INavigationBase, bool setLoaded = false)` exists on EF8-10).
