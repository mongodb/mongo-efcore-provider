# g1-tph: native TPH type tests / derived members / derived Include target

Read-only research (no build/test run). All line numbers are on EF-322c @ c7854e7f. Claims marked [UNVERIFIED] were
read from code but not executed.

## 1. Decline sites

| # | File:line | Condition | Blocks |
|---|-----------|-----------|--------|
| D1 | src/.../Query/NativeTranslation/MongoExpressionTranslator.TypeIs.cs:44-45 (`TryTranslateTypeIs`) | `_entityType.BaseType is not null \|\| _entityType.GetDirectlyDerivedTypes().Any()` -> `return false`. `_entityType` is always the ROOT collection type (`mongoQ.CollectionExpression.EntityType`, NativeSlotPopulator.cs:48), so every `x is T` over a TPH hierarchy declines; the Where is `MarkNotNativelyRepresentable()` (NativeSlotPopulator Where arm, final `else`), gate throws "Query is not natively representable" (MongoShapedQueryCompilingExpressionVisitor.cs ~784). | Can_configure_type_discriminator_element_name (`First(e => e is Customer)`), Can_configure_type_discriminator_with_int (`First(e => e is KeyedCustomer)`), Returns_correct_values_when_property_name_shared_between_entities (`is Supplier \|\| is Contact`), Returns_correct_values_when_navigation_shared_between_entities (`is Order \|\| is OrderWithProducts`), Returns_correct_entity_where_is_type_query (`First(is Supplier)`, `Where(is Order)`), Returns_correct_entities_with_mixed_query (`OfType<BaseEntity>().Where(is Customer \|\| GetType()==typeof(Order))`; `OfType<BaseEntity>` is identity at TranslateOfType:1932) |
| D2 | .../MongoExpressionTranslator.EntityType.cs:66-67 (`TryTranslateGetTypeComparison`) | same hierarchy check -> `return false` | Returns_correct_entity_where_GetType_query (3 mappings), Returns_correct_entities_with_mixed_query (2nd disjunct) |
| D3 | .../MongoExpressionTranslator.Members.cs:127-130 (`TryResolveMember`): `var scopeType = isOuter ? _outerEntityType! : _entityType; scopeType.FindProperty(memberName)` | After `OfType<Customer>()` the lambda parameter is `Customer` but the translator stays bound to the root `BaseEntity`; a member declared only on the derived type (`Name` on Customer) -> `FindProperty` null -> `return false` -> Where/Single declines. (Base-declared members like `Status` resolve fine.) The NativeOfTypeTests comment already calls this "a known gap". | OfType_does_not_break_entity_serializer_association (2nd query: `Where(Status==Active).OfType<Customer>().Single(c => c.Name == "Customer 1")`; EF normalizes `Single(pred)` to `Where(pred).Single()`, lambda param type Customer). 1st query in that test (`OfType<Customer>().Where(e => e.Status == ...)`) only reads a base member and is expected to already be native. |
| D4 | .../MongoSelectLowerer.cs:459-505 (`AppendLookupStages`), driven by LookupExpression.cs:78-89 | `LookupExpression` ctor, for a nav whose target is a non-root TPH type, stages a discriminator `$match` into `PipelineStages` and sets `PipelineKind = FallbackOnly`. `AppendLookupStages` admits collection lookups only if `IsNativeCollectionLookup` (requires `!HasPipeline`) or Kind in {None, NestedInclude, FilteredInclude}. FallbackOnly matches none -> falls to the final `else` and throws "Native pipeline does not support lookup for navigation 'PriorityOrders' ...". | CrossCollectionRelationshipTests.Include_tph_derived_target_collection_nav_excludes_sibling_type_rows |
| D5 | src/.../Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:2056 `TranslateCast` -> `ReshapeShaperExpression(source, castType)` | `Planets.Cast<SuperPlanet>()` (SuperPlanet is not a mapped entity): shaper is replaced with a scalar-style binding of an entity query; gate declines (NativeOnly: NativeTranslationNotSupportedException; Native: falls back to driver, which throws ExpressionNotSupportedException ".As"). | UnsupportedQueriesTests.Cast_to_child_not_supported_in_driver -- NOT a gap to close (see Task 6). |

Important findings:
* The OfType path (TranslateOfType + `TryBuildDiscriminatorPredicate`, QMTEV:1920-1996) is already native, so the
  predicate builder exists; `is`/`GetType()` simply never reuse it.
* Nested ThenInclude to a derived target ALREADY works natively with the discriminator `$match` inside the
  sub-pipeline (ExtractNestedIncludePipeline keeps parent kind NestedInclude; BuildLookupDocument keeps nested
  PipelineStages). That is why Include_then_include_tph_derived_target_excludes_sibling_type_rows is not in the gap
  list. So `ToLookupStageDocument()`'s let+pipeline form with a leading discriminator stage is proven renderable; only
  the top-level gate (D4) is missing.
* Latent bugs seen on the way (pre-existing, also relevant to main):
  - `TryBuildDiscriminatorPredicate` (QMTEV:1982) and `LookupExpression` ctor (LookupExpression.cs:82-84) include
    `GetDiscriminatorValue()` of ABSTRACT types, which is null -> `$in:[null,...]` also matches documents with NO
    discriminator element. (`OfType<AbstractMid>()`.)
  - `LookupExpression` ctor uses `BsonValue.Create(d.GetDiscriminatorValue())` -> ignores value converter /
    BsonRepresentation on the discriminator (OfType's predicate and `MongoEFDiscriminator.GetDiscriminatorsForTypeAndSubTypes`
    do honor them, EF-349). A converted discriminator on a derived Include target would match nothing (wrong rows,
    silent). Fix by reusing `new MongoEFDiscriminator(targetEntityType).GetDiscriminatorsForTypeAndSubTypes(clrType)`
    (internal, same assembly) in the ctor.
* Pre-existing bug "Join to derived TPH DbSet returns sibling rows": `CreateShapedQueryExpression` (QMTEV:1614-1623)
  builds `new MongoQueryExpression(entityType)` with NO discriminator narrowing for a derived-type root, in every mode.
  Probe (probes-main/branch): `Owners.Join(db.Dogs, ...)` => `["o1-rex","o1-tom"]` (tom is a Cat) on main driver, branch
  DriverLinq, Native and NativeOnly; `Join(db.Animals.OfType<Dog>())` throws on all (driver `.OfType().As()`
  not supported; NativeOnly "Query projects a non-entity result"). Plain `db.Dogs.ToList()` very likely has the same hole
  [UNVERIFIED -- add a probe first, Task 5a].

## 2. Design summary

One shared predicate builder + three small translator changes + one lowerer admission + a test-assertion change.

TDD order: Task 1 (shared builder, abstract/converter correctness) -> 2 (`is`/`GetType`) -> 3 (derived members) -> 4
(Include) -> 5 (derived DbSet root / join; investigative) -> 6 (Cast test assertion) -> 7 (docs).

### Task 1 -- shared discriminator predicate builder (refactor + abstract fix)

Failing tests (existing, must stay green): NativeOfTypeTests.* (all). New parity test:

```csharp
// tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeTphTypeTestTests.cs  (new file, see Task 2 for full)
[Fact]
public void OfType_abstract_intermediate_does_not_match_documents_without_discriminator()
{
    // Party is abstract (null discriminator value). $in:[null, ...] would also match a doc with no _t element.
    var collection = database.CreateCollection<Base>();
    using (var db = Make(collection, MongoQueryMode.Native, ShadowMapping))
    {
        db.Add(new Customer { Label = "c" }); db.Add(new Supplier { Label = "s" }); db.Add(new Order { Label = "o" });
        db.SaveChanges();
    }
    // Raw doc with no discriminator element at all.
    database.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
        .InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "nodisc" } });

    var labels = NativeModeAssert.NativeAndParity(mode =>
    {
        using var db = Make(collection, mode, ShadowMapping);
        return db.Entities.OfType<Party>().OrderBy(e => e.Label).Select(e => e.Label).ToList();
    });
    Assert.Equal(new[] { "c", "s" }, labels);
}
```
(If the driver returns the no-disc row this is a driver-wrong case: change to native-only assertion and record for main.)

New file `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoDiscriminatorPredicate.cs`:

```csharp
internal static class MongoDiscriminatorPredicate
{
    /// <summary>
    /// Discriminator test for <paramref name="target"/> (and, if <paramref name="includeDerived"/>, its subtree).
    /// Abstract types have no discriminator value and are skipped. Returns false for a non-TPH type. No concrete
    /// type => constant false (or true if negated).
    /// </summary>
    internal static bool TryBuild(IEntityType target, bool includeDerived, bool negated, out MongoExpression predicate)
    {
        predicate = null!;
        var discriminatorProperty = target.FindDiscriminatorProperty();
        if (discriminatorProperty is null) return false;

        var types = includeDerived ? target.GetDerivedTypes().Prepend(target) : [target];
        var values = types.Where(t => !t.ClrType.IsAbstract)
            .Select(t => t.GetDiscriminatorValue()).Where(v => v is not null).Distinct().ToArray();
        if (values.Length == 0)
        {
            predicate = new MongoConstantExpression(negated, forSerialization: null);
            return true;
        }

        var field = new MongoFieldExpression(discriminatorProperty, discriminatorProperty.GetElementName());
        predicate = values.Length == 1
            ? new MongoBinaryExpression(negated ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal, field,
                new MongoConstantExpression(values[0], forSerialization: discriminatorProperty))
            : new MongoInExpression(field,
                new MongoConstantExpression(values!, forSerialization: discriminatorProperty), negated);
        return true;
    }
}
```
Edit `QMTEV.TranslateOfType` (:1946) to call `MongoDiscriminatorPredicate.TryBuild(resultEntityType, includeDerived: true, negated: false, out var predicate)`;
delete `TryBuildDiscriminatorPredicate` (:1966-1996). OfType<AbstractLeafWithNoConcrete> now yields a constant-false conjunct
(check the lowerer handles a bare bool-constant conjunct; the TypeIs fold already produces one, so it should).
Risk: `object[]` typed values array vs the existing `string[]`/`int[]` shape -- the renderers enumerate
`IEnumerable` and re-wrap each item with `ForSerialization`, so object[] is fine (`RenderInValues`, both dialects).

### Task 2 -- `x is T` and `x.GetType() ==/!= typeof(T)` over a TPH hierarchy (D1, D2)

Failing tests (existing): all DiscriminatorTests rows in the TSV except OfType_does_not_break_...; verbatim names:
Can_configure_type_discriminator_element_name, Can_configure_type_discriminator_with_int,
Returns_correct_values_when_property_name_shared_between_entities(x3), Returns_correct_values_when_navigation_shared_between_entities(x3),
Returns_correct_entity_where_is_type_query(x2), Returns_correct_entity_where_GetType_query(x3),
Returns_correct_entities_with_mixed_query(x3).

New parity tests (new file tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeTphTypeTestTests.cs, modelled on
NativeOfTypeTests; `XUnitCollection("QueryTests")`):

```csharp
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeTphTypeTestTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Base <- Party(abstract) <- Customer <- SubCustomer ; Party <- Supplier ; Base <- Order <- OrderWithLines
    public class Base { public ObjectId Id { get; set; } public string Label { get; set; } = ""; public string? Kind { get; set; } }
    public abstract class Party : Base { }
    public class Customer : Party { public string CustomerName { get; set; } = ""; }
    public class SubCustomer : Customer { public int Code { get; set; } }
    public class Supplier : Party { public string CustomerName { get; set; } = ""; /* same CLR name, sibling */ }
    public class Order : Base { }
    public class OrderWithLines : Order { }
    public class Unrelated { public ObjectId Id { get; set; } }

    public enum Mode { RealString, Shadow, Int }

    static void Real(ModelBuilder mb) => mb.Entity<Base>().HasDiscriminator(e => e.Kind)
        .HasValue<Base>("base").HasValue<Customer>("cust").HasValue<SubCustomer>("sub").HasValue<Supplier>("sup")
        .HasValue<Order>("ord").HasValue<OrderWithLines>("ordx");     // Party deliberately has no value (abstract)
    static void Shadow(ModelBuilder mb) { mb.Entity<Base>().HasDiscriminator(); mb.Entity<Customer>(); mb.Entity<SubCustomer>();
        mb.Entity<Supplier>(); mb.Entity<Order>(); mb.Entity<OrderWithLines>();
        mb.Entity<Supplier>().Property(s => s.CustomerName).HasElementName("sname"); }   // sibling differs in element name
    static Action<ModelBuilder> Map(Mode m) => m switch { Mode.RealString => Real, _ => Shadow };

    static SingleEntityDbContext<Base> Make(IMongoCollection<Base> c, MongoQueryMode mode, Action<ModelBuilder> map)
        => SingleEntityDbContext.Create(c, map,
            optionsBuilderAction: b => new MongoDbContextOptionsBuilder(b).UseQueryMode(mode));

    public static IEnumerable<object[]> Shapes()
    {
        yield return Shape("is Customer",               q => q.Where(e => e is Customer));
        yield return Shape("is abstract Party",         q => q.Where(e => e is Party));
        yield return Shape("is leaf SubCustomer",       q => q.Where(e => e is SubCustomer));
        yield return Shape("is Base (always true)",     q => q.Where(e => e is Base));
        yield return Shape("not is Customer",           q => q.Where(e => !(e is Customer)));
        yield return Shape("is || is",                  q => q.Where(e => e is Supplier || e is Order));
        yield return Shape("is && base member",         q => q.Where(e => e is Customer && e.Label != "b"));
        yield return Shape("GetType ==",                q => q.Where(e => e.GetType() == typeof(Customer)));
        yield return Shape("GetType !=",                q => q.Where(e => e.GetType() != typeof(Customer)));
        yield return Shape("reversed GetType",          q => q.Where(e => typeof(Order) == e.GetType()));
        yield return Shape("OfType + derived member",   q => q.OfType<Customer>().Where(c => c.CustomerName == "x"));
        yield return Shape("OfType + is sub",           q => q.OfType<Customer>().Where(c => c is SubCustomer));
        yield return Shape("OfType sibling same name",  q => q.OfType<Supplier>().Where(s => s.CustomerName == "x"));
    }
    static object[] Shape(string n, Func<IQueryable<Base>, IQueryable<Base>> f) => [n, f];

    [Theory, MemberData(nameof(Shapes))]
    public void Type_tests_go_native_and_match_driver(string name, Func<IQueryable<Base>, IQueryable<Base>> shape)
    {
        foreach (var mode in Enum.GetValues<Mode>())            // RealString, Shadow, Int (Int mapping: see Task 2b)
        {
            var collection = database.CreateCollection<Base>(name, values: [mode]);
            Seed(collection, Map(mode));
            var ids = NativeModeAssert.NativeAndParity(m =>
            {
                using var db = Make(collection, m, Map(mode));
                return shape(db.Entities.AsNoTracking()).OrderBy(e => e.Label).Select(e => e.Label).ToList();
            });
            Assert.NotNull(ids);
        }
    }
    // + Seed(): one of every concrete type, two Customers with CustomerName "x"/"y", a Supplier with CustomerName "x",
    // a SubCustomer with CustomerName "x", and (raw BsonDocument insert) one doc with NO discriminator element.
```
(Use the Seed + `Raw doc` additions to also assert the `!is` shape treats missing discriminator like the driver.)

Native-only assertions (driver is WRONG here, EF-202: "driver-LINQ narrows on discriminator presence rather than the
comparison type"), so NOT parity tests -- assert absolute results under `MongoQueryMode.NativeOnly`:

```csharp
[Fact]
public void GetType_against_unmapped_or_abstract_type_matches_nothing_natively()
{
    // db.Entities.Where(e => e.GetType() == typeof(Unrelated)) -> empty; == typeof(Party) (abstract) -> empty;
    // != typeof(Unrelated) -> every row.
}
```
Record for main: driver returns every row for `GetType()==typeof(Unrelated)` (EF-202).

Code change in `.../NativeTranslation/MongoExpressionTranslator.TypeIs.cs`:

```csharp
private bool TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)
{
    result = null;
    if (SelfParam is null) return false;
    if (!IsSelfParamTheEntity(Unwrap(typeBinary.Expression))) return false;

    if (_entityType.BaseType is null && !_entityType.GetDirectlyDerivedTypes().Any())
    {
        result = new MongoConstantExpression(typeBinary.TypeOperand.IsAssignableFrom(_entityType.ClrType), forSerialization: null);
        return true;
    }
    return TryTranslateHierarchyTypeTest(typeBinary.TypeOperand, exact: false, negated: false, out result);
}

// Shared by `is` (exact:false) and GetType()==/!= (exact:true, negated = NotEqual).
private bool TryTranslateHierarchyTypeTest(Type operand, bool exact, bool negated, out MongoExpression? result)
{
    result = null;
    // One-scope, root-level only: a $filter/$map element scope or a Distinct output scope reads a different document
    // (prefixed path / flattened alias), and the field path below is the document-root discriminator.
    if (_outerParam is not null || _innerPrefix is not null || DistinctAliasScope is not null) return false;

    // `is`: every row is _entityType or a subtype, so a supertype operand is always true (covers object / unmapped CLR base).
    if (!exact && operand.IsAssignableFrom(_entityType.ClrType))
    {
        result = new MongoConstantExpression(true, forSerialization: null);   // (is is never negated here; `!is` is a Not node)
        return true;
    }

    var operandType = _entityType.Model.FindEntityType(operand);
    if (operandType is null || operandType.GetRootType() != _entityType.GetRootType())
    {
        // Rows are always some mapped member of THIS hierarchy, so `GetType() == typeof(NotInHierarchy)` is false.
        // For `is`, a non-entity operand (interface implemented by some subtype) cannot be decided -> decline.
        if (!exact) return false;
        result = new MongoConstantExpression(negated, forSerialization: null);
        return true;
    }

    return MongoDiscriminatorPredicate.TryBuild(operandType, includeDerived: !exact, negated, out result!);
}
```
`.../MongoExpressionTranslator.EntityType.cs` (D2): replace the hierarchy `return false` (lines 66-67) with
`return TryTranslateHierarchyTypeTest(comparisonType, exact: true, negated: !isEqual, out result);` (keep the
non-hierarchy constant fold below it). Update both file header remarks (they say TPH "decline").
`!(e is Customer)` needs no work: the Not arm flips `MongoInExpression.Negated`/`NotEqual` (exact complements; `$nin`/`$ne`
also match a missing discriminator, which C# `!(row is Customer)` also treats as true).

Expected MQL (RealProperty / shadow `_t`; values are serialized via the discriminator property so converters/BsonRepresentation hold):
* `Where(e => e is Customer)` -> `{ $match: { Kind: { $in: ["cust","sub"] } } }`
* `First(e => e is Supplier)` -> `[{ $match: { Kind: "sup" } }, { $limit: 1 }]` (shadow: `_t`)
* `Where(e => e is Order)` -> `{ $match: { _t: { $in: ["Order","OrderEx"] } } }`
* `GetType()==typeof(Order)` -> `{ $match: { _t: "Order" } }`; `!=` -> `{ _t: { $ne: "Order" } }`
* `is Supplier || is Contact` -> `{ $match: { $or: [ { _t: "Supplier" }, { _t: "Contact" } ] } }`
* element-name test -> `{ _entityType: { $in: ["Client","SubClient"] } }` (the `$limit:1` follows)
* int test -> `{ SubType: 1 }`
(Driver-LINQ emitted the same $in/$eq via the discriminator convention.)

Risks / watch-points:
1. `IsSelfParamTheEntity` only checks `ProjectedAliasScope`; I add `DistinctAliasScope is not null` -> decline for the new
   branch. A whole-entity `Distinct()` then `Where(is ...)`: confirm via test (`Distinct().Where(e => e is Customer)`;
   expect either parity or clean decline -- use `NativeModeAssert.DeclinesCleanly` if it declines).
2. Two-scope translators (`_outerParam`/`_innerPrefix`, correlated owned-collection elements): SelfParam is the outer
   param there; declined explicitly above. Keep the `TryTranslateTypeIs` call sites unchanged.
3. Boolean projection leaf `Select(e => e is Customer)`: the new `MongoInExpression` would render in the aggregation
   dialect (`$in:["$Kind",[...]]`). Add a Shape for it; missing discriminator -> `$in` false, matches C#. If the gate
   (`AllFieldsDefaultSerialized` / ClassifyNonNullableValueRead) rejects it, pin with DeclinesCleanly.
4. NegatE: `$nin`/`$ne` on a non-default-serialized (BsonRepresentation String) int discriminator -- covered by passing
   `forSerialization: discriminatorProperty`; add `Mode.Int` + converter variant (existing `HasConversion(v => v+100, v => v-100)`
   from Returns_correct_entity_with_OfType_query_when_discriminator_has_value_converter) as shapes.
5. GetType()==typeof(X) for X in hierarchy changes behavior vs driver only where the driver is wrong (EF-202); everything
   else identical. Escalate-to-user item: emitted MQL for a previously-fallback shape changes (not breaking per AGENTS.md
   "Is not a break").
6. Cardinality/aggregate callers (NativeCardinalityBinder.cs:277 `Count(e => e is Customer)`, `Any`, `All`) get the new
   support for free; add `Count`/`Any` shapes to the theory (separate Theory with `Func<IQueryable<Base>, object>`).

### Task 3 -- derived-only members after OfType<TDerived> (D3)

Failing test (existing): DiscriminatorTests.OfType_does_not_break_entity_serializer_association (x3).
New parity tests: the `OfType + derived member` and `OfType sibling same name` shapes from Task 2, plus
`OrderBy(c => c.CustomerName)` and `Single(c => c.CustomerName == "x")` shapes. Also a decline pin for what stays out of scope:

```csharp
[Fact]
public void Select_of_derived_member_after_OfType_matches_driver_or_declines_cleanly()
{
    // Only one of the two must hold; whichever it is, pin it (probe first -- Select binding reads properties outside TryResolveMember).
}
```

Change in `.../MongoExpressionTranslator.Members.cs` `TryResolveMember` (before `var scopeType = ...` line ~127):

```csharp
// OfType<TDerived>() narrows the lambda parameter to a derived entity type while this translator stays bound to the
// root collection type, so a member declared on TDerived is invisible to FindProperty. Resolve by the parameter's own
// type (by identity: only SelfParam, never by member name -- sibling subtypes can share a CLR name with different
// element names/properties). Sound because a derived parameter type only reaches here after OfType (which records the
// discriminator conjunct) -- Cast<TDerived> declines at the gate (Task 6).
var scopeType = isOuter ? _outerEntityType! : ResolveScopeEntityType(param);
...
private IEntityType ResolveScopeEntityType(ParameterExpression param)
    => _outerParam is null
       && ReferenceEquals(param, SelfParam)
       && param.Type != _entityType.ClrType
       && _entityType.GetDerivedTypes().FirstOrDefault(t => t.ClrType == param.Type) is { } derived
        ? derived
        : _entityType;
```
`FindProperty` on the derived IEntityType also returns inherited properties (same IProperty instance), so base members after
OfType are unchanged. Do NOT extend to `((Customer)e).Name` (Convert-to-derived member access): the rows are not narrowed
there, and a same-named element on a sibling would match natively (C# would throw / EF relational reads NULL) -- keep
declining (add a `DeclinesCleanly` test for `Where(e => ((Customer)e).CustomerName == "x")`).

Risks: other consumers resolve properties against the root type (NativeProjectionBinder.cs:64 passes the selector parameter
so Select leaves reach the same code; projection element reading / `AddTopLevelElementNames` use the root type). The new
theory shapes will show whether `OrderBy`/`Select` of a derived member are fully consistent; if a sub-path mis-resolves it
must decline (NativeOnly throw), never read the wrong element. Also check `EntityEquality` and `TryResolveOwnedFieldPath`
(owned navs declared on a derived type) still decline: they use `_entityType` and return null for derived-only navs.

### Task 4 -- top-level collection Include whose target is a derived TPH type (D4)

Failing test (existing): CrossCollectionRelationshipTests.Include_tph_derived_target_collection_nav_excludes_sibling_type_rows.
Keep `Include_then_include_tph_derived_target_excludes_sibling_type_rows` green.

Approach: stop treating the discriminator stage as "fallback only". Rename `LookupPipelineKind.FallbackOnly` ->
`DiscriminatorNarrowed` (LookupExpression.cs:27,38,88,212; the write-once guards in
MongoProjectionBindingExpressionVisitor.Lookup.cs:601,783; NativeProjectionBinder.cs:2212,2268,2435,2471 comments; keep
semantics "write-once: a later FilteredInclude/NestedInclude does not overwrite it") and admit it in the lowerer:

```csharp
// MongoSelectLowerer.AppendLookupStages (third disjunct, ~line 464-469)
|| (lookup.Navigation is { IsCollection: true } pipelinedNav
    && lookup.PipelineKind is LookupPipelineKind.NestedInclude or LookupPipelineKind.FilteredInclude
                           or LookupPipelineKind.DiscriminatorNarrowed
    && !lookup.ForceUnwind
    && lookup.As == LookupExpression.GetLookupAlias(pipelinedNav))
```
Leave the `RenamedToAvoidJoinCollision` disjunct (~line 463) excluding DiscriminatorNarrowed (its comment documents a leak;
that combination keeps declining) and leave reference-nav-to-derived-target declining (the `IsReference && !HasPipeline`
arm fails because of the staged `$match`; nothing changes for reference Includes). The correlated-Count/First recognizers
(`NativeProjectionBinder` 2210, 2435, 2469) keep declining via `IsNativeCollectionLookup` / the explicit TPH gate -- unchanged.

Rendering is already `ToLookupStageDocument()` via `MongoPipelineFactory.RenderLookup` (non-CorrelatedReducer path).
Expected MQL:

```
{ $lookup: { from: "<orders>", let: { localField: "$_id" },
    pipeline: [ { $match: { $expr: { $eq: ["$cust_id", "$$localField"] } } },
                { $match: { _t: { $in: ["Priority"] } } } ],
    as: "_lookup_PriorityOrders" } },
{ $limit: 1 }   // First()
```
Materialization: DOM collection materializer reads `_lookup_PriorityOrders` the same way as the driver fallback does (shared
shaper) [UNVERIFIED for polymorphic element types -- the existing test asserts `Assert.IsType<PriorityOrder>`].

Companion fix in the same task (silent wrong rows today in every mode, pre-existing): in the `LookupExpression` ctor replace
`BsonValue.Create(d.GetDiscriminatorValue())` with
`new BsonArray(new MongoEFDiscriminator(targetEntityType).GetDiscriminatorsForTypeAndSubTypes(targetEntityType.ClrType))`
(honors converter/BsonRepresentation; skip abstract nulls by filtering `t.ClrType.IsAbstract` in a local list first --
`GetDiscriminatorsForTypeAndSubTypes` does not filter).

New tests (in CrossCollectionRelationshipTests; add an optional `MongoQueryMode? mode = null` ctor parameter to
`CrossCollectionDbContextBase<TSelf>` and `DerivedCollectionNavDbContext` -> `.UseMongoDB(client, db, o => { if (mode is { } m) o.UseQueryMode(m); })`;
verify the `UseMongoDB(..., Action<MongoDbContextOptionsBuilder>)` overload name):

```csharp
[Fact]
public void Include_tph_derived_target_collection_nav_goes_native_and_matches_driver()
{
    var (ordersName, customersName) = SeedTphDerivedCollection();     // extract the seeding from the existing test
    List<string> Run(MongoQueryMode mode)
    {
        using var db = new DerivedCollectionNavDbContext(database, ordersName, customersName, mode);
        return db.Customers.Include(c => c.PriorityOrders).AsNoTracking().ToList()
            .SelectMany(c => c.PriorityOrders).Select(o => $"{o.OrderDescription}:{o.PriorityLevel}:{o.GetType().Name}").ToList();
    }
    var rows = NativeModeAssert.NativeAndParity(Run);
    Assert.Equal(new[] { "Rush:5:PriorityOrder" }, rows);
}

[Fact]
public void Include_tph_derived_target_with_filtered_include_goes_native_and_matches_driver()
{
    // Include(c => c.PriorityOrders.Where(o => o.PriorityLevel > 1).OrderBy(o => o.PriorityLevel).Take(1))
    // Seed 3 Priority + 1 Order row sharing the FK. Narrowing stage must run BEFORE the filter/skip/take stages.
}

[Fact]
public void Include_tph_derived_target_with_converted_discriminator_excludes_siblings()
{
    // HasDiscriminator<int>("_t").HasValue<TphOrder>(1).HasValue<PriorityOrder>(2) + HasConversion(v => v + 100, v => v - 100)
    // Before the ctor fix: $in:[2] never matches stored 102 -> empty include (silent wrong). After: one row.
}
```
Risks: (a) a DiscriminatorNarrowed + FilteredInclude combination becomes native -- comment at
MongoProjectionBindingExpressionVisitor.Lookup.cs:783 says it "isn't validated"; the stage list order is
[narrow, filter..., sort, skip, limit] since the ctor adds the narrowing first -- covered by the second test; if flaky, keep it
declining by adding `&& lookup.PipelineStages.Count == 1` for DiscriminatorNarrowed only. (b) `MongoQueryExpression.AddLookup`
(:165) merges only bare-then-pipelined; two Includes of the same derived nav still dedupe by `As` (unchanged). (c) The
let+pipeline `$expr $eq` form does not match null/missing FK the way localField/foreignField does -- identical to what the
driver bridge already emits, so no parity delta. (d) Public-surface: none; internal enum rename only. Emitted MQL for this
shape changes from driver-bridge to native (same stages) -- user-visible only as "which path", not contract.

### Task 5 -- derived DbSet root narrowing / Join to derived DbSet (pre-existing bug; main + branch)

5a. Probe first (cheap, no code): add to the probe file `Dogs.ToList()` and `Dogs.Select(d => d.Name)` under all modes.
    If `db.Dogs.ToList()` returns Cats, the hole is `CreateShapedQueryExpression` (QMTEV:1614).
5b. If confirmed, fix at the root in QMTEV `CreateShapedQueryExpression`:

```csharp
protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
{
    var queryExpression = new MongoQueryExpression(entityType);
    // A derived DbSet (db.Dogs) is the hierarchy's collection narrowed to the subtree.
    if (entityType.BaseType is not null)
    {
        if (MongoDiscriminatorPredicate.TryBuild(entityType, includeDerived: true, negated: false, out var narrow))
            queryExpression.Select.AddPredicateConjunct(narrow);
        else
            queryExpression.Select.MarkNotNativelyRepresentable();
    }
    ...
```
    Caveat: the conjunct is recorded on the source query, so as a JOIN INNER it makes the inner "non-bare"
    (`MarkSawNonBareJoinInner`, MongoSelectDefinition.cs:1187-1200) -> the native flat `$lookup` declines (clean in NativeOnly)
    and Native falls back to the driver bridge, which still returns sibling rows (driver has no way to express the narrowing;
    `OfType` inside a join inner throws `.OfType().As()`). Making the join itself correct natively = give the navigation-less
    join `LookupExpression` ctor (LookupExpression.cs:~95) the same discriminator `PipelineStages` + `DiscriminatorNarrowed`
    kind and let `AppendLookupStages` admit `lookup.IsReference && (kind == DiscriminatorNarrowed)` via
    `ToLookupStageDocument()` + `$unwind`; streaming gate (`IsStreamableReference`, requires `!HasPipeline`) then keeps it on DOM.
    That is new native-join surface: decide with owner (escalation: behavior change = results become CORRECT; main stays wrong).
    Record for main: "Join to derived TPH DbSet returns sibling rows" (fix there = add the narrowing in the EF->LINQ bridge or
    reject like OfType does).
    Parity test (only valid once 5b is done; until then pin the current wrong answer in a DOCUMENTED known-gap test is NOT
    allowed per spec-test rules -- use a NativeOnly decline pin `Assert.Throws<NativeTranslationNotSupportedException>` and a main ticket):

```csharp
[Fact]
public void Join_to_derived_dbset_excludes_sibling_rows()
{
    // Owners(1) ; Animals: Dog rex(OwnerId 1), Cat tom(OwnerId 1)
    // Owners.Join(db.Dogs, o => o.Id, d => d.OwnerId, (o, d) => o.Name + "-" + d.Name) must equal ["o1-rex"]
}
```

### Task 6 -- Cast_to_child_not_supported_in_driver (D5): the TEST must change, not the provider

`Planets.Cast<SuperPlanet>()` (SuperPlanet is not mapped) has no valid result: the driver throws on main
(`ExpressionNotSupportedException ... .As`), so per the brief it is out of scope for native parity. The assertion is
exception-type specific to the driver; under NativeOnly the native gate throws `NativeTranslationNotSupportedException`
first. Change the test to pin the mode it is about and add the native half:

```csharp
// tests/.../Query/UnsupportedQueryTests.cs  (add optional queryMode to GuidesDbContext.Create, Entities/Guides/GuidesDbContext.cs:31)
public static GuidesDbContext Create(IMongoDatabase database, Action<string>? logAction = null,
    ILoggerFactory? loggerFactory = null, bool sensitiveDataLogging = true, MongoQueryMode? queryMode = null) =>
    new(new DbContextOptionsBuilder<GuidesDbContext>()
        .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName,
            o => { if (queryMode is { } m) o.UseQueryMode(m); })
        ...

[Fact]
public void Cast_to_child_not_supported_in_driver()
{
    using var db = GuidesDbContext.Create(database.MongoDatabase, queryMode: MongoQueryMode.DriverLinq);
    var ex = Assert.Throws<ExpressionNotSupportedException>(() => db.Planets.Cast<SuperPlanet>().ToList());
    Assert.Contains(".As", ex.Message);
}

[Fact]
public void Cast_to_child_is_declined_cleanly_by_native()
{
    using var db = GuidesDbContext.Create(database.MongoDatabase, queryMode: MongoQueryMode.NativeOnly);
    Assert.Throws<NativeTranslationNotSupportedException>(() => db.Planets.Cast<SuperPlanet>().ToList());
}
```
Optionally also make `TranslateCast` call `MarkNotNativelyRepresentable()` explicitly for an entity-shaper source with a
different result type (clearer than relying on the "non-entity result" gate throw; keep `ReshapeShaperExpression` for
scalar sources). Verify the UseMongoDB `Action<MongoDbContextOptionsBuilder>` overload exists; else use
`.UseMongoDB(...)` then `new MongoDbContextOptionsBuilder(b).UseQueryMode(...)` as NativeOfTypeTests does.
The sibling probe-harness (all contexts forced to NativeOnly) will still show this test as "gap" unless it pins the mode
-- the pin is the sanctioned fix.

### Task 7 -- docs

* src/MongoDB.EntityFrameworkCore/Query/AGENTS.md: replace/extend "TPH `$lookup` to a derived type must narrow by
  discriminator" with: "`is T` / `GetType()==typeof(T)` / `OfType<T>` share `MongoDiscriminatorPredicate.TryBuild`
  (abstract types contribute no value; values go through the discriminator property's serializer); derived-only members
  resolve by the SelfParam's own type only (`ResolveScopeEntityType`); `LookupPipelineKind.DiscriminatorNarrowed` is native
  for collection Include only."
* Fix stale remarks in MongoExpressionTranslator.TypeIs.cs / EntityType.cs headers.

## 3. Summary of per-test fate

| Test | Fix |
|---|---|
| DiscriminatorTests.Can_configure_type_discriminator_element_name | Task 2 |
| ...with_int | Task 2 |
| ...OfType_does_not_break_entity_serializer_association (x3) | Task 3 (derived member) |
| ...Returns_correct_entities_with_mixed_query (x3) | Task 2 (is + GetType) |
| ...Returns_correct_entity_where_GetType_query (x3) | Task 2 |
| ...Returns_correct_entity_where_is_type_query (x2) | Task 2 |
| ...Returns_correct_values_when_navigation_shared_between_entities (x3) | Task 2 |
| ...Returns_correct_values_when_property_name_shared_between_entities (x3) | Task 2 |
| CrossCollectionRelationshipTests.Include_tph_derived_target_collection_nav_excludes_sibling_type_rows | Task 4 |
| UnsupportedQueriesTests.Cast_to_child_not_supported_in_driver | assertion changes (Task 6): pin DriverLinq; add NativeOnly decline pin |
| (pre-existing) Join to derived TPH DbSet sibling rows | Task 5; wrong on main -> file for main |

Escalate to user: Task 2/4 change emitted MQL path for previously-fallback shapes (non-breaking per rubric but "method's
emitted MQL changes"); Task 5's native-join extension is new join surface; Task 6 edits a test assertion (mode pin).
