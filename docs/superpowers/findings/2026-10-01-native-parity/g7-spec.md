# g7-spec: native design for SpecificationTests rows (read-only research, nothing built or run)

Decompiled upstream bases with ~/.dotnet/tools/ilspycmd (it IS available) into plan/g7-spec/*.cs
(NorthwindFunctions/Misc/Where/DbFunctions/GroupBy bases + AdHocJson base, EF 9.0.19). All query shapes below are
read from those, not guessed. Paths are under src/MongoDB.EntityFrameworkCore/Query unless stated.
Line numbers are for the tree at EF-322c c7854e7f.

## 0. Summary table (decline site -> fix size)
| Rows | EF | Decline site | Fix |
|---|---|---|---|
| Int/DateTime/TimeSpan_Compare_to_simple_zero (TimeSpan one is a copy-paste of DateTime upstream) | 8,9 | NativeTranslation/MongoExpressionTranslator.StringCompare.cs:61-70 `IsStringCompareCall` only matches string receivers/`string.Compare` | S: generalise to int/long/short/byte/double/decimal/DateTime `CompareTo`/`T.Compare`, relational fold only |
| Static_equals_nullable_datetime_compared_to_non_nullable | 8,9 | MongoExpressionTranslator.cs:807-816 static `object.Equals(a,b)` arm: `leftArg.Type != rightArg.Type` (DateTime? vs DateTime) -> `AreMismatchedExactEqualityTypes` false -> returns null | XS |
| String_{StartsWith,EndsWith,Contains}_with_StringComparison_unsupported | 9 | MongoExpressionTranslator.MethodCalls.cs:331-385 `TryMatchRegexMethod`: `comparison is not (Ordinal or OrdinalIgnoreCase)` -> false | XS but OWNER RULING (see 2.4) |
| String_Compare_nested, String_Compare_to_nested | 8,9 | sub-queries 2 and 5 of 6: `x.CustomerID.ToUpper()` as a `$cmp` operand: TranslateOperand has no ToUpper arm (by design, ASCII-only) | RULING: keep declined, split the override (2.5) |
| Where_date_add_year_constant_component | 8,9 | MongoExpressionTranslator.cs:221-228 `TryTranslateDateTimeMember`: receiver of `.Year` is a `AddYears(-1)` call, not a field or date-member chain -> `else return false` | XS |
| Where_datetime_now/utcnow/today, Where_datetimeoffset_utcnow | 8,9 | row-independent predicate holding `DateTime.Now`/`UtcNow`/`Today`/`DateTimeOffset.UtcNow` MemberExpression: TranslateNode default arm, TranslateValue (cs:1990) only knows Constant / query parameter | M (per-execution runtime parameter, section 1) |
| F2 `DateTime.UtcNow/Now/Today(.AddX)` baked at compile time | all | cs:357 + `TryEvaluateClosedSubtree` cs:385-415 (SpecifyKind at cs:405-407) | same M change |
| Random_next_is_not_funcletized_1..6, Where_guid_newguid, Random_return_less_than_1/greater_than_0 | 8,9 | no refusal natively: translator returns null -> NativeOnly throws NativeTranslationNotSupportedException; default mode relied on bridge VisitMethodCall (Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs:778) | S: central scan of CapturedExpression in VisitShapedQuery (section 3) |
| Select_expression_datetime_add_ticks | 8,9,10 | MongoExpressionTranslator.cs:349 `DateAddUnitsByMethodName` has no AddTicks; read-side gate `IsDateAddMethod` cs:467 | S |
| Select_expression_other_to_string | 8,9,10 | `DateTime.ToString()` leaf: emit side NativeProjectionBinder.TryTranslateLeaf has no arm, only `IsDateTimeOffsetToString` is peeled on the read side | M, OWNER RULING (4) |
| Where_bitwise_binary_and/or/xor/not | and/or/not 8,9; xor 9 | cs:1842 `MapArithmeticOperator` has no And/Or/ExclusiveOr; no unary BitNot | M (section 5) |
| GroupBy_aggregate_using_grouping_key_Pushdown | 8,9,10 | NativeGroupByBinder.cs:812 `translator.TryTranslateValue(selector.Body, ...)` where body is `g.Key` | S (section 6) |
| AdHocJson x4 | 9 | see section 7; only Project_top_level_entity_with_null_value_required_scalars worked on main, the other three assert a THROW | M-L / or cheap pin |

## 1. F2 + the clock rows: ONE design = per-execution runtime parameter

### Root cause
* `TryTranslateDateAdd` (MongoExpressionTranslator.cs:356-358): `TranslateOperand(receiver)` returns null for `DateTime.UtcNow`,
  so `TryEvaluateClosedSubtree(receiver)` compiles and invokes it AT TRANSLATION TIME. `NonDeterministicCalls` only
  excludes method calls (Random/NewGuid); the MemberExpressions `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`
  are not excluded, so the value is baked into the cached template -> stale on every later execution of the cached plan.
* cs:405-407 `SpecifyKind(Now, Utc)` relabels wall clock as UTC: off by the machine UTC offset. (Intended for literal
  `new DateTime(1900,1,1)` to dodge host-tz skew; wrong for Local `Now`/`Today`.)
* Bare `DateTime.Now/UtcNow` in a comparison or as a whole closed predicate is not baked, it just declines (TranslateValue
  cs:1990 knows Constant/query-param only) -> fallback; so NativeOnly fails (the 4 Where rows) and Native falls back.
* What the driver did on main: its partial evaluator folds the clock per EXECUTION on the client and serialises it with
  the DateTime serializer: `DateTime.Now` (Kind Local) is converted Local->UTC (instant), `UtcNow` as is, `Today` = local
  midnight as an instant. Therefore: instant comparison, client clock. That is the parity target.

### Decision
Client clock evaluated per execution, NOT `$$NOW`:
* `$$NOW` is aggregation-only (forces `$expr`, loses index use of `{Created:{$lt:date}}`), is server clock (differs from the
  driver/main), cannot express `Now`(Local)/`Today`/`DateTimeOffset.*`, and cannot fold a closed boolean like
  `DateTime.Now.Date == DateTime.Today`.
* The pipeline already substitutes placeholders per Build (MongoPipelineFactory.Build 541-583; template built once). So
  add a placeholder whose VALUE is computed by a compiled delegate each Build.

Rule: a maximal closed subtree (no lambda parameter, no extension node other than EF query parameters) that CONTAINS a
clock member and has type bool/bool?/DateTime/DateTime? becomes a runtime-evaluated `MongoParameterExpression`. The
whole subtree is evaluated in .NET per execution (so `DateTimeOffset.UtcNow != myDto`, `DateTime.Now.Date == DateTime.Today`
are exact C# semantics; DateTimeOffset-typed results are NOT admitted: BsonValue.Create cannot serialise one, they only
occur inside a closed bool).

### Code
1. NEW Query/RuntimeClock.cs
```csharp
internal static class RuntimeClock
{
    public static bool IsClockMember(MemberExpression m)
        => m is { Expression: null, Member: PropertyInfo { Name: "Now" or "UtcNow" or "Today" } p }
           && (p.DeclaringType == typeof(DateTime) && p.Name != "Today" || p.DeclaringType == typeof(DateTime)
               || p.DeclaringType == typeof(DateTimeOffset) && p.Name != "Today");

    public static bool ContainsClock(Expression e) { var f = new Finder(); f.Visit(e); return f.Found; }

    private sealed class Finder : ExpressionVisitor
    {
        public bool Found;
        protected override Expression VisitMember(MemberExpression node)
        { if (IsClockMember(node)) { Found = true; return node; } return base.VisitMember(node); }
    }
}
```
(Write the predicate plainly: DateTime.{Now,UtcNow,Today}, DateTimeOffset.{Now,UtcNow}.)

2. MongoParameterExpression (Expressions/MongoParameterExpression.cs): add optional ctor arg and property
`Func<IReadOnlyDictionary<string, object?>, object?>? RuntimeEvaluator`. Reusing this node avoids a new MongoExpression
subtype (no MongoExpressionNodeCoverageTests / negator / dialect-classifier churn: it is already handled as a
parameter everywhere, including the bare-bool-parameter predicate root `{ $expr: <placeholder> }`).

3. MongoExpressionTranslator.cs
```csharp
private static int _runtimeParameterSeed;

// Returns a runtime parameter for a closed, clock-containing subtree; null otherwise.
internal static MongoParameterExpression? TryCreateRuntimeClockParameter(Expression node, IProperty? forSerialization)
{
    var type = node.Type;
    if (type != typeof(bool) && type != typeof(bool?) && type != typeof(DateTime) && type != typeof(DateTime?)) return null;
    if (!RuntimeClock.ContainsClock(node) || !IsClosedOverQueryParametersOnly(node)) return null;

    var values = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "values");
    var body = new QueryParameterLookupRewriter(values).Visit(node);
    var evaluator = Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, object?>>(
        Expression.Convert(body, typeof(object)), values).Compile();
    var name = "__mongoef_runtime_" + Interlocked.Increment(ref _runtimeParameterSeed);
    return new MongoParameterExpression(name, forSerialization, valueType: type, runtimeEvaluator: evaluator);
}
```
`QueryParameterLookupRewriter`: for every node where `NativeQueryParameter.TryGetQueryParameterName(node, out n)`
replace with `Expression.Convert(Expression.Property(values, "Item", Expression.Constant(n)), node.Type)`.
`IsClosedOverQueryParametersOnly`: like `ParameterOrExtensionNodeFinder` (cs:~436-460) but a node accepted by
`TryGetQueryParameterName` is allowed (EF8/EF9 ParameterExpression with QueryParameterPrefix, EF10 QueryParameterExpression).
Hooks (each is the same call):
* TranslateNode (cs:680) FIRST statement: `if (TryCreateRuntimeClockParameter(node, null) is { } rt) return rt;` (bool root only fires for bool type).
* TranslateValue (cs:1990) before `return null`: `if (TryCreateRuntimeClockParameter(node, forSerialization) is { } rt) return rt;`
  (passes the comparison property: `o.Created < DateTime.UtcNow.AddDays(-1)` stays query dialect `{Created:{$lt:<placeholder>}}`,
  serialised through the property's serializer, honouring HasDateTimeKind).
* `TryTranslateDateAdd` receiver: needs nothing extra, `TranslateOperand(receiver)` bottoms out in TranslateValue(node, null).
* `TryEvaluateClosedSubtree` (cs:385): add `|| RuntimeClock.ContainsClock(node)` to the decline condition (belt and braces: a clock can never be baked again).
  Leave the SpecifyKind relabel for literals only.

4. Expressions rendering: MongoValueRenderer.RenderValue (the single parameter-render site, MongoValueRenderer.cs:48-62)
```csharp
if (parameter.RuntimeEvaluator is { } evaluator)
{
    placeholders.RegisterRuntimeEvaluator(parameter.Name, evaluator);   // idempotent set
    /* then fall through to the normal CreatePlaceholder(...) with the property serializer or null */
}
```
PlaceholderTable: `Dictionary<string, Func<...>> RuntimeEvaluators` + `RegisterRuntimeEvaluator`.

5. MongoPipelineFactory.Build (cs 541 and 564): first line of each overload
```csharp
parameterValues = RuntimeParameterValues.Wrap(parameterValues, _placeholders.RuntimeEvaluators);  // returns input when none
```
`RuntimeParameterValues : IReadOnlyDictionary<string, object?>`: ctor(inner, evaluators); `TryGetValue(name)` evaluates the
delegate against `inner` (NOT itself) once and caches in the instance; everything else delegates; Keys/Count/enumerator = inner + evaluators.
SerializeParameter (cs:677) is unchanged; it already does `Coerce`+serialize; with serializer null it is `BsonValue.Create(DateTime)`
which converts Local->UTC like the driver (and keeps UtcNow as is).
One Build = one clock reading per distinct closed subtree (two `DateTime.UtcNow` texts in one query get two readings, microseconds apart; accepted).

### Correctness for Local Now vs UTC-stored data
* `o.Created <= DateTime.Now` compares INSTANTS (Local Now -> UTC ms) with the stored UTC instants. Same as the driver on main.
  In-memory LINQ would compare raw ticks; that only agrees when the machine tz is UTC. Record as accepted/parity, not a bug.
* Local-kind property (`HasDateTimeKind(Local)`): property serializer converts the Local value the same way. Filters are unaffected by kind (NativeDateTimeKindReadBack doc).
* Calendar AddXxx over a Local-kind PROPERTY still declines (EF-459); a closed clock receiver is not a property, so `DateTime.Now.AddDays(1)` is fine: it is evaluated entirely in .NET (whole subtree), no UTC-vs-local calendar issue at all.
* Today = Now.Date (Local midnight) -> instant of local midnight. Parity with driver.

### Expected MQL
* `Where(o => o.Created < DateTime.UtcNow.AddDays(-1))`: `Rows.{ "$match" : { "Created" : { "$lt" : { "$date" : "<per execution>" } } } }`
* `Where(c => DateTime.Now != myDatetime)`: `Customers.{ "$match" : { "$expr" : true } }` (driver dropped the stage: baselines `Customers.` / `Employees.`
  in NorthwindWhereQueryMongoTest.cs:2296-2334 must change for default AND NativeOnly mode since default is now native).
* `Where(o => o.Created <= DateTime.UtcNow.AddMinutes(o.Foo * 0))`: `$expr {$lte:[ "$Created", {$dateAdd:{startDate:<placeholder>, unit:"minute", amount:{$multiply:["$Foo",0]}}} ]}`

### Failing tests first
Spec: NorthwindWhereQueryMongoTest Where_datetime_now, Where_datetime_utcnow, Where_datetime_today, Where_datetimeoffset_utcnow
(EF8/9; update baselines to `$expr:true`, remove nothing else, no IsNativeOnly branch exists in these four).
NEW FunctionalTests, add to tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NonDeterministicFunctionTests.cs (same Row / CreateContext):
```csharp
// F2: the receiver must be evaluated per execution. One cached plan, two executions, clock moves between them.
[Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
public async Task UtcNow_add_receiver_is_evaluated_per_execution_not_baked_into_the_cached_plan(MongoQueryMode mode)
{
    var collection = database.CreateCollection<Row>($"un_stale_{mode}");
    collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = DateTime.UtcNow.AddMilliseconds(600) });
    using var db = SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
    {
        b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
    });

    int Count() => db.Entities.Where(o => o.Created <= DateTime.UtcNow.AddMilliseconds(0)).Count();

    Assert.Equal(0, Count());          // row is 600 ms in the future
    await Task.Delay(1200);
    Assert.Equal(1, Count());          // stale plan (baked first reading) keeps answering 0
}

[Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
public void Local_Now_add_receiver_compares_instants_in_any_time_zone(MongoQueryMode mode)
{
    // past row and future row relative to the real instant; correct answer is tz independent: only the past row.
    // The old SpecifyKind(Now, Utc) bug answers wrongly whenever |UTC offset| > 30 min (include both for +2h, none for -2h).
    var collection = database.CreateCollection<Row>($"now_local_{mode}");
    var now = DateTime.UtcNow;
    collection.InsertMany([
        new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = now.AddMinutes(-30) },
        new Row { Id = ObjectId.GenerateNewId(), Foo = 2, Created = now.AddMinutes(+30) }]);
    using var db = /* same context factory, mode */;

    Assert.Equal([1], db.Entities.Where(o => o.Created <= DateTime.Now.AddMinutes(0)).Select(o => o.Foo).ToList());
    Assert.Equal([1], db.Entities.Where(o => o.Created <= DateTime.Now.AddMinutes(o.Foo * 0)).Select(o => o.Foo).ToList());   // row-dependent amount, closed receiver
    Assert.Equal([1, 2], db.Entities.Where(o => o.Created > DateTime.Today.AddYears(-1)).OrderBy(o => o.Foo).Select(o => o.Foo).ToList());
}
// NOTE: tz-independent staleness test above is the real discriminator on a UTC CI box. For the tz test run the suite
// once with TZ=Pacific/Auckland and once TZ=America/Los_Angeles (dotnet reads TZ at process start, not settable in-test).

[Theory, InlineData(MongoQueryMode.NativeOnly)]
public void Closed_clock_predicate_is_native_and_refreshes(MongoQueryMode mode) { /* Where(c => DateTime.UtcNow != fixed) returns all; Where(c => DateTime.UtcNow < new DateTime(2000,1,1)) returns none; second execution of each re-evaluates */ }
```
Also: UtcNow_in_predicate_still_works / Now_in_predicate_still_works: add `InlineData(MongoQueryMode.NativeOnly)`.

### Risks
* `MongoParameterExpression.Type` is `object` when no property: MongoDateAddExpression.Type = startDate.Type = object; nothing keys off it today (check `ReadsNullAsDefault`/`IsNonNullableValueType` consumers; add a Type override using ValueType when ForSerialization is null).
* Anything that enumerates `parameterValues` expecting only EF names (logging, streaming path): the wrapper enumerates inner+computed, verify QueryingEnumerable/MongoExecutableQuery do not.
* Compiled queries: evaluator closes over nothing; query-parameter names are per query so stable.
* DateTimeOffset-typed closed subtree not admitted: `o.Dto < DateTimeOffset.UtcNow` stays declined (same as before).

## 2. Functions rows (NorthwindFunctionsQueryMongoTest; class does not exist in the EF10 build)

### 2.1 Int/DateTime/TimeSpan_Compare_to_simple_zero
Queries (decompiled): `c.OrderID.CompareTo(orderId) == 0`, `0 != ...`, `> 0`, `0 >= ...`, `0 < ...`, `<= 0`; DateTime ones use
`c.OrderDate!.Value.CompareTo(myDatetime)` or `DateTime.Compare(c.OrderDate!.Value, myDatetime)`. Upstream TimeSpan test is a copy of the DateTime one.
Decline: `TryTranslateStringCompare` (MongoExpressionTranslator.StringCompare.cs:21) -> `IsStringCompareCall` (61-74) accepts only string.
Design: generalise the recogniser, keep string logic, restrict the non-string case to the relational fold (no `$cmp`):
```csharp
private static bool IsCompareCall(Expression node, out Expression a, out Expression b, out bool isString)
{
    a = b = null!; isString = false;
    switch (Unwrap(node))
    {
        case MethodCallExpression { Method.Name: "CompareTo", Object: { Type: var t } obj, Arguments: [var arg] }
            when IsOrderedPrimitive(t) && arg.Type == t:                    // int,long,short,byte,sbyte,double,float,decimal,DateTime,string
            (a, b) = (obj, arg); isString = t == typeof(string); return true;
        case MethodCallExpression { Method.Name: "Compare", Object: null, Arguments: [var x, var y] } call
            when IsOrderedPrimitive(call.Method.DeclaringType!) && x.Type == call.Method.DeclaringType:
            (a, b) = (x, y); isString = x.Type == typeof(string); return true;
        default: return false;
    }
}
```
In `TryTranslateStringCompare`: when `!isString` -> skip the `IsFoldSafe` requirement (string-only: nullability+constant), call `TranslateComparisonCore(a, b, relOp)`
for `k` in {0, -1, 1} mappings (existing table), and `return false` if `relational is null` (never emit `$cmp` for non-strings).
Expected MQL = existing baselines (`{OrderID/_id:10250}`, `$ne`, `$gt`, `$lte`, `$gt`, `$lte`; dates `{"$date":"1998-05-04T00:00:00Z"}`).
Do not admit uint/ulong (BSON order differs), enum, Guid (binary order), TimeSpan.
Remove: nothing (these overrides have no IsNativeOnly branch). Risk: `o.OrderDate.Value.CompareTo(x)` on a NULL date throws in LINQ, matches nothing here (query dialect); same as the `.Value` comparison the translator already does.
Tests: the three rows; add FunctionalTests NativeAndParity for `long`, `double`, `decimal`, nullable `.Value`, and `Compare(a, b) == 2` folds.

### 2.2 Static_equals_nullable_datetime_compared_to_non_nullable
`where object.Equals(o.OrderDate, arg)`, `arg` DateTime param, OrderDate DateTime?. Site: MongoExpressionTranslator.cs:803-818:
`leftArg.Type (DateTime?) != rightArg.Type (DateTime)` -> `AreMismatchedExactEqualityTypes(DateTime, DateTime)` is false (same underlying) -> `: null` (decline).
Fix: strip nullability before the type check:
```csharp
var leftUnder = Nullable.GetUnderlyingType(leftArg.Type) ?? leftArg.Type;
var rightUnder = Nullable.GetUnderlyingType(rightArg.Type) ?? rightArg.Type;
if (leftUnder != rightUnder) return AreMismatchedExactEqualityTypes(leftUnder, rightUnder) ? new MongoConstantExpression(false, null) : null;
return TranslateComparisonCore(leftArg, rightArg, ExpressionType.Equal);
```
(`object.Equals(null-able, value)` is exactly lifted `==`: null never equals a non-null; both-null equals null == null.)
Expected MQL (existing baseline): `{ "$match" : { "OrderDate" : { "$date" : "1996-07-04T00:00:00Z" } } }`. Mirror in the bridge is already there (EFToLinq cs:339-360 handles `left.Type == right.Type` only; DriverLinq passes via `Equals` call). Add a null-vs-null / int?-vs-int test.

### 2.3 (nothing)

### 2.4 String_{StartsWith,EndsWith,Contains}_with_StringComparison_unsupported (EF9)
Queries (override): `StartsWith("Qu", CurrentCulture)`, `StartsWith("m", CurrentCultureIgnoreCase)` must work; `InvariantCulture`/`InvariantCultureIgnoreCase` must fail.
Decline: MongoExpressionTranslator.MethodCalls.cs:366-370 `if (comparison is not (Ordinal or OrdinalIgnoreCase)) return false;` (doc says "culture-sensitive comparisons ... deliberately not replicated").
The driver on main maps CurrentCulture* to ordinal / ordinal-ignore-case (baseline options "s" / "is") and rejects Invariant*.
OWNER RULING needed: this widens an accepted divergence (culture-aware semantics vs byte regex; e.g. tr-TR, ignorable chars). To meet the brief (worked on main) implement the driver mapping:
```csharp
caseInsensitive = comparison switch {
    StringComparison.Ordinal or StringComparison.CurrentCulture => false,
    StringComparison.OrdinalIgnoreCase or StringComparison.CurrentCultureIgnoreCase => true,
    _ => throw/return false };
```
Invariant* stay declined (NativeOnly: NativeTranslationNotSupportedException, accepted by the override's `AssertTranslationFailed` = MongoSpecTestHelpers.AssertNativeTranslationFailedAsync; Native: driver ExpressionNotSupportedException, also accepted).
Expected MQL: as the override baselines (`"pattern":"^Qu","options":"s"`, `"^m","is"`; Invariant lines `Customers.` since failing). Note `Where` shapes elsewhere (EF-243 Ordinal) unaffected.
Record: culture-sensitive StartsWith is evaluated ordinally on main AND on native after this change (wrong for culture-special data).

### 2.5 String_Compare_nested / String_Compare_to_nested (EF8/EF9)
6 sub-queries each (decompiled). Only #2 and #5 fail natively: `0 != string.Compare(c.CustomerID, c.CustomerID.ToUpper())`, `1 == string.Compare(c.CustomerID, c.CustomerID.ToUpper())` (CompareTo twin).
The others (`"M" + c.CustomerID`, `"ALFKI".Replace("ALF", c.CustomerID)`) go through the existing `$cmp` path (MongoStringCompareExpression, `MongoReplaceExpression`, `MongoConcatExpression`).
Decline: TranslateOperand has no `ToUpper` arm on purpose (Query/AGENTS.md: `$toUpper`/`$toLower` never emitted natively, ASCII-only).
RULING needed, recommended: keep declined (correct for ASCII data only on main; wrong for non-ASCII on main = record as wrong-on-main for main fix). Then:
* Replace the base call in both overrides by copied sub-queries (like the existing "Copied from base" DateTime override): run #1,#3,#4,#6 with `AssertQuery` + a NativeOnly `AssertNativeTranslationFailed` for #2/#5; keep full `base.*` + MQL when `!IsNativeOnly`.
* If the owner prefers parity: allow `$toUpper` ONLY as a `MongoStringCompareExpression` operand whose other operand is the same field; documented ASCII-only. Not recommended (violates the invariant; no non-ASCII oracle).
Expected MQL for the passing sub-queries: the existing baseline entries 1,3,4,6.

### 2.6 Where_guid_newguid (EF8/9) and the Random/NewGuid family: section 3.

## 3. Random / Guid.NewGuid: native owns the refusal
Facts: upstream `Random_next_is_not_funcletized_1..6` are `Where(o => o.OrderID > Random.Shared.Next(...))` / `new Random(15).Next(...)`; `Where_guid_newguid`
is `Where(c => Guid.NewGuid() != default(Guid))`; `Random_return_*` is `Count(o => EF.Functions.Random() < 1.0)`. All expect
`InvalidOperationException` (exact type, xUnit `Assert.Throws`), except DbFunctions rows which currently expect `TargetInvocationException`
(the driver's reflection invoke of `EF.Functions.Random()`).
On main these baked ONE value (silent wrong) or (for DbFunctions) threw from the driver; the only requirement is that native refuses itself.
Design (B, one central scan; chosen over throwing from the translator because TranslateNode/TranslateOperand run speculatively and under several binders):
1. NonDeterministicCalls.cs: add
```csharp
// in IsNonDeterministic
if (declaring == typeof(DbFunctionsExtensions) && method.IsStatic && method.Name == nameof(DbFunctionsExtensions.Random)) return true;

public static void ThrowIfAnyNonDeterministic(Expression? tree)
{
    if (tree is null) return;
    var finder = new Finder(); finder.Visit(tree);
    if (finder.First is { } call) ThrowIfNonDeterministic(call);
}
private sealed class Finder : ExpressionVisitor {
    public MethodCallExpression? First;
    protected override Expression VisitMethodCall(MethodCallExpression n) { if (First is null && IsNonDeterministic(n)) First = n; return First is null ? base.VisitMethodCall(n) : n; }
    protected override Expression VisitExtension(Expression n) => n;   // never reduce EF extension nodes
}
```
2. Visitors/MongoShapedQueryCompilingExpressionVisitor.cs VisitShapedQuery (line ~151) FIRST statement after the `mongoQueryExpression` cast,
   all modes (DriverLinq's bridge would throw the same later):
   `NonDeterministicCalls.ThrowIfAnyNonDeterministic(mongoQueryExpression.CapturedExpression);`
   (CapturedExpression is the full chain incl. terminal and every lambda: set at the tail of QMTEV.VisitMethodCall cs:316; `ContainsVectorSearch()` already scans it.)
   EF10: Guid.NewGuid()/Random are parameterised by the funcletizer there and never appear as a method call, so no behaviour change (the EF10 override already passes).
3. Test changes: Misc overrides Random_next_is_not_funcletized_1..6 (NorthwindMiscellaneousQueryMongoTest.cs:4643-4733): DELETE the `IsNativeOnly` blocks, keep
   `Assert.ThrowsAsync<InvalidOperationException>`. Where_guid_newguid already expects InvalidOperationException for EF8/9, no change.
   NorthwindDbFunctionsQueryMongoTest.cs:119-143: replace `TargetInvocationException`+message with
   `await Assert.ThrowsAsync<InvalidOperationException>(...)` and `Assert.Contains("Random", ex.Message)`; keep `AssertMql("Orders.")`. (Exception type for an unsupported feature is not contract, AGENTS.md.)
   FunctionalTests NonDeterministicFunctionTests: add `InlineData(MongoQueryMode.NativeOnly)` to Random_in_predicate_throws..., Random_compared_to_column..., Random_in_ordering..., Guid_NewGuid_in_predicate_throws..., Random_in_projection...; they fail first (NativeTranslationNotSupportedException is not an exact InvalidOperationException); add `EF_Functions_Random_in_predicate_throws` for the three modes.
   Guid_NewGuid_in_closed_date_add_receiver_throws NativeOnly branch (expects NativeTranslationNotSupportedException) must change to InvalidOperationException: the central scan now fires first.
Risks: scan is before the gate, so it also refuses Random in a client-evaluated tail the translator would have left to the shaper (e.g. `Select(o => Random.Shared.Next())`); today that already throws via the bridge in Native, and the test `Random_in_projection_throws_rather_than_repeating_one_value` pins it. `Random_evaluated_client_side_before_the_query_works` (value hoisted before the query) is untouched.

## 4. Select_expression_datetime_add_ticks and Select_expression_other_to_string
### 4.1 AddTicks
Query: `Where(o => o.OrderDate != null).Select(o => new Order { OrderDate = o.OrderDate.Value.AddTicks(10000L) })` (10000 ticks = 1 ms). Driver baseline: `$dateAdd { unit:"millisecond", amount: 1.0 }`.
Design: one shared recogniser so emit and read side agree (the invariant in AGENTS.md):
```csharp
// MongoExpressionTranslator.cs; replaces the DateAddUnitsByMethodName lookup in TryTranslateDateAdd (cs:349) and IsDateAddMethod (cs:467)
private static bool TryGetDateAddShape(MethodCallExpression call, out MongoDateAddUnit unit, out Expression amount, out MongoExpression? constantAmount)
{
    ... existing map for AddYears..AddMilliseconds (constantAmount = null) ...
    if (call.Method.Name == nameof(DateTime.AddTicks) && Unwrap(call.Arguments[0]) is ConstantExpression { Value: long ticks }
        && ticks % TimeSpan.TicksPerMillisecond == 0)
    { unit = Millisecond; constantAmount = new MongoConstantExpression((double)(ticks / TimeSpan.TicksPerMillisecond), null); return true; }
    return false;
}
```
`TryTranslateDateAdd` uses `constantAmount ?? TranslateOperand(call.Arguments[0], true)`; `IsDateAddMethod` => `TryGetDateAddShape(...)` (so MongoProjectionBindingExpressionVisitor.cs:325 follows automatically).
Precision caveat (documented in the helper): BSON dates are millisecond-precise and `$dateAdd` rejects a fractional amount, so sub-ms constants (`AddTicks(5)`) and parameter/field ticks DECLINE (driver-LINQ would emit a fractional amount: server error / truncation). Amount rendered as double 1.0 to match the baseline.
Expected MQL: `Orders.{ "$match" : { "OrderDate" : { "$ne" : null } } }, { "$project" : { "OrderDate" : { "$dateAdd" : { "startDate" : "$OrderDate", "unit" : "millisecond", "amount" : 1.0 } }, "_id" : 0 } }` (identical to the existing else-branch baseline).
Remove: the `if (MongoSpecTestHelpers.IsNativeOnly)` branch in Select_expression_datetime_add_ticks (NorthwindMiscellaneousQueryMongoTest.cs:4482-4487), keep the else body.
Tests: spec row (EF8/9/10); FunctionalTests NativeAndParity: `AddTicks(20000L)`, `AddTicks(5)` DeclinesCleanly, `AddTicks(param)` DeclinesCleanly, Where-filter `o.When.AddTicks(10000) > x`.

### 4.2 other_to_string  (OWNER RULING)
Query: `Where(o => o.OrderDate != null).Select(o => new Order { ShipName = o.OrderDate.Value.ToString() })`. Upstream asserter ignores ShipName (elementSorter on ShipName only), which is why the ISO `$toString` of the driver "passes".
Main result: ISO-8601 strings from the driver's `$toString` instead of .NET culture format = WRONG on main (record for main). The handoff doc (docs/superpowers/plans/2026-09-30-native-misc-low-hanging-HANDOFF.md:77,335) already flags this as needing a ruling.
Recommended design (correct, not driver parity): evaluate client-side like DateTimeOffset.ToString (EF-217) and the case-mapping peel:
* Emit: `NativeProjectionBinder.PeelCaseMapping` (cs:766) generalised: also peel `Method.Name == "ToString"`, `Object.Type.UnwrapNullableType()` DateTime or DateTimeOffset, 0 or 1 constant-string argument, stage only the receiver `o.OrderDate.Value` (mark `hasCaseMappingLeaf` => HasClientCaseMappingProjectionLeaf, which already makes later value-reading operators decline).
* Read: MongoProjectionBindingExpressionVisitor.cs:340 arm `IsDateTimeOffsetToString` extended with a DateTime sibling `IsDateTimeToString`, guarded by `_queryExpression.Select.Route == NativeRoute.Projection` (so the fallback/driver pushdown still sees the original call and the unchanged `$toString`).
* Keep ProjectionAnalyzer.HasCaseMappingProjectedValue unchanged for DateTime (fallback path unchanged).
Expected MQL: `Orders.{ "$match" : { "OrderDate" : { "$ne" : null } } }, { "$project" : { "OrderDate" : "$OrderDate", "_id" : 0 } }` (receiver only; override baseline for default mode changes; the current override has no AssertMql).
If the owner instead wants driver parity: render `$dateToString` with the driver's default format in a new MongoConvertExpression target; NOT recommended (ISO != .NET).
Tests: spec row (all EF); FunctionalTests: `Select(e => e.When.ToString())` equals client evaluation for Utc, Local-kind, nullable `.Value`, `ToString("o")`, NativeOnly + DriverLinq-wrong-recorded.

## 5. Where_bitwise_binary_and/or/xor/not (EF8/9; xor EF9)
Queries: `(o.OrderID & 0x2808) == 10248`, `(o.OrderID | 0x2808) == 10248`, `(o.OrderID ^ 1) == 10249`, `~o.OrderID == negatedId` (param).
Decline: cs:1842 only maps via `MapArithmeticOperator` (Add..Modulo); And/Or are mapped only for bool (cs:668-677); `ExclusiveOr`, integer `Not` have no node.
Design:
* Expressions/MongoExpression.cs: `enum MongoBinaryOperator` add `BitAnd, BitOr, BitXor`; `enum MongoUnaryOperator` add `BitNot`.
* MongoExpressionTranslator: new `MapBitwiseOperator(BinaryExpression)` (do NOT touch `MapArithmeticOperator`: NativeSelectManyBinder.cs:593 shares it):
```csharp
internal static MongoBinaryOperator? MapBitwiseOperator(BinaryExpression n)
    => IsBitwiseInteger(n.Type) ? n.NodeType switch { And => BitAnd, Or => BitOr, ExclusiveOr => BitXor, _ => null } : null;
// IsBitwiseInteger: int/long (+nullable). short/byte promote to int through Convert; uint/ulong excluded (signed $bit* results).
```
  In `TranslateOperand` add, next to the arithmetic arm (cs:1842): `if (node is BinaryExpression bit && MapBitwiseOperator(bit) is {} bop) { left/right = TranslateOperand(...); return new MongoBinaryExpression(bop, left, right); }`
  and `if (node is UnaryExpression { NodeType: ExpressionType.Not } bn && IsBitwiseInteger(bn.Type)) return new MongoUnaryExpression(MongoUnaryOperator.BitNot, TranslateOperand(bn.Operand))`
  placed BEFORE the generic `Not` hand-off at cs:1879 (which assumes a bool predicate), and before the TranslateNode `Not` arm (cs:821) via an `IsBitwiseInteger` guard so integer `~` is not treated as a boolean Not.
* MongoAggregationExpressionRenderer: add the three operators to `IsRenderableOperator` (cs:276-289) and the `op` switch in RenderBinary (cs:~865: `$bitAnd`,`$bitOr`,`$bitXor`); RenderUnary (cs:812) emit `{ $bitNot: <operand> }` for BitNot; CanRender (cs:210) arm for `MongoUnaryExpression { Operator: BitNot }` => `CanRender(operand)`.
  MongoQueryLanguageRenderer.RenderUnary (cs:152) already throws for non-Not, and IsQueryDialectRenderable must say false for BitNot/BitXor etc. so the whole comparison renders `$expr` (verify the classifier default; NodeCoverage test must stay green).
* Decline/propagation sites that enumerate arithmetic operators (NativeProjectionBinder.cs:1925, MongoGroupElementTranslator.cs:137 `PropagatesMissingAsNull`): do NOT add the new operators (they fail closed: Select leaf / GroupBy accumulators of bitwise decline) unless tests prove them. `$bitAnd` propagates null, so adding to PropagatesMissingAsNull is safe later.
Expected MQL:
 and: `{ "$match" : { "$expr" : { "$eq" : [{ "$bitAnd" : ["$_id", 10248] }, 10248] } } }` (driver emits `{_id:{$bitsAllSet:10248}}`; optional query-dialect special case for `(field & C) == C`, C >= 0 constant, would restore the driver MQL and avoid the 6.3 requirement for that one test, which is NOT gated by `TestServer.SupportsBitwiseOperators`);
 or: `{ "$eq":[{ "$bitOr":["$_id",10248] },10248] }`; xor: `$bitXor` ... `10249`; not: `{ "$eq":[{ "$bitNot":"$_id" }, <param> ] }` (baseline had the literal -10249, param here? EF funcletizes `negatedId` to a parameter -> placeholder; keep the existing baseline comparison tolerant).
Override changes: Where_bitwise_binary_and (cs:2483) must gain the `if (!TestServer.SupportsBitwiseOperators) return;` guard the other three have (unless the `$bitsAllSet` special case is implemented) and its baseline becomes the `$expr/$bitAnd` form.
Risks: server >= 6.3 required for `$bitAnd/$bitOr/$bitXor/$bitNot` (same as driver); `Select(o => o.OrderID & 3)` becomes natively admitted through TranslateOperand: add parity tests (NativeAndParity vs DriverLinq) for Select and Where over bitwise, plus nullable `int?` (`$bitAnd` null-propagates, same as lifted C#), negative constants, long/int mix. Add MongoExpressionNodeCoverageTests rows for the new operators.

## 6. GroupBy_aggregate_using_grouping_key_Pushdown (all EF versions)
Query: `GroupBy(o => o.CustomerID).Where(g => g.Count() > 10).Select(g => new { g.Key, Max = g.Max(e => g.Key) }).OrderBy(t => t.Key).Take(20).Skip(4)`.
Decline: NativeGroupByBinder.cs:812 (`TryBindAccumulator`, after the `$max` method detection) `translator.TryTranslateValue(selector.Body, out var operand)` with `selector.Body == g.Key` -> false (the root translator cannot resolve the grouping parameter) -> `TryBindAccumulator` false -> projection declines -> gate "Query projects a non-entity result".
The infrastructure for the exact same resolution already exists: `TryResolveKeyReferenceAsRawExpression` (cs:996-1045) resolves `g.Key` / `g.Key.Sub` to the key part's per-document expression, used by accumulator CONDITIONS. Accumulators over the key were never wired (EF-457 in the override comment).
Fix (cs:810-815):
```csharp
MongoExpression? operand;
var selector = call.Arguments[1].UnwrapLambdaFromQuote();
if (selector is null) return false;
if (TryResolveKeyReferenceAsRawExpression(selector.Body, selector.Body, groupingParameter, keyParts, isComposite, out var keyOperand))
{
    if (translator.HasLeftOuterJoinLevel) return false;      // same guard as the condition path (cs:1087)
    operand = keyOperand;                                    // raw per-document key expression: $max over a constant-within-group value is the key
}
else if (!translator.TryTranslateValue(selector.Body, out operand)) return false;
```
(`otherOperand` = selector.Body makes a zero-part key decline, `IsSafeZeroPartKeyComparison` only admits literal null; composite whole key declines: right.)
`ReducesPossiblyUnmatchedJoinSideToDefault(op, ..., selector.Body, operand, translator)` still runs after; with a `g`-parameter body `MayReadAnUnmatchedJoinSide` returns false (documented safe) and `ReadsNullAsDefault` runs on the real operand.
Do not wrap in `NullSafeKeyRead`: `$max` over missing == null == the key's null; bare field matches the baseline.
Same for `g.Sum/Min/Average(e => g.Key)` (all four operators share the call site) and the Queryable/Enumerable forms. The other three TryTranslateValue sites (cs:890 `$push`, 1331, 1414 element-selected/filtered accumulators) get the same helper if their tests demand it; out of scope here.
Expected MQL (driver baseline, names may differ: native uses its own accumulator aliases, so the baseline for NativeOnly may need regenerating, the override's `__agg0/__agg1` come from the driver):
`Orders.{ "$group" : { "_id" : "$CustomerID", ..."$sum":1 ...,"$max":"$CustomerID" } }, { "$match" : ... $gt [.., 10] }, { "$project" : { "Key":"$_id","Max":..., "_id":0 } }, { "$sort" : { "Key" : 1 } }, { "$limit" : 20 }, { "$skip" : 4 }`.
Open risk (unverified): Take(20).Skip(4) after the GroupBy projection and the sort-by-Key post-group ordering are separate native features; if the row still declines after the accumulator fix, the next site is the post-group `OrderBy(t.Key)`/paging gate (HasTerminalOperator) in QMTEV.
Remove: the whole `if (MongoSpecTestHelpers.IsNativeOnly)` branch in NorthwindGroupByQueryMongoTest.cs:1251-1257 and (likely) regenerate the default baseline.
Tests: spec row (EF8/9/10); FunctionalTests NativeAndParity: `g.Max(e => g.Key)`, `g.Min(e => g.Key)`, `g.Sum(e => g.Key)` over an int key, composite key `g.Max(e => g.Key.A)`, computed key, nullable key, left-join grouped key (declines), zero-part key (declines).

## 7. AdHocJsonQueryMongoTest (EF9 only) four rows
Decompiled queries (Context21006; owned types by convention, no ToJson in the Mongo model):
* Project_top_level_json_entity_with_missing_scalars: `Where(x => x.Id < 4).AsNoTracking().Select(x => new { x.Id, x.OptionalReference, x.RequiredReference, x.Collection })`; override expects InvalidOperationException containing "Document element is missing for required" (driver THREW on main).
* Project_top_level_entity_with_null_value_required_scalars: `Where(x => x.Id == 4).AsNoTracking().Select(x => new { x.Id, x.RequiredReference })`; SUCCEEDS on main, `RequiredReference.Number == 0.0`. The only row that "worked" on main.
* Project_missing_required_navigation: `Where(x => x.Id == 5).AsNoTracking().Select(x => x.RequiredReference.NestedRequiredReference)` (BARE, two owned hops); override expects InvalidOperationException "Field 'RequiredReference' required but not present" (threw on main).
* Project_null_required_navigation: `Where(x => x.Id == 6).AsNoTracking().Select(x => x.RequiredReference)` (BARE, one hop); expects InvalidOperationException "Field 'NestedRequiredReference' required but not present" (threw on main).
Base-test intent is EF semantics (null/default instead of throwing); the Mongo overrides pin the provider throw (EF-164, EF-X005).

Decline sites:
* Bare rows (3, 4): NativeProjectionBinder.cs:1085-1099 (TryTranslateLeaf): owned-reference leaf admitted only `allowWholeRootEntityLeaf` (wrapped bodies) AND `alias == element name`; the bare `default:` arm (cs:~330) passes `BareLeafProvisionalAlias = " bare"` and `allowWholeRootEntityLeafForThis:false` -> never matches (comment at 1086-1087 "a bare `b => b.Address` declines"). Two-hop `x.A.B` additionally fails `TryGetOwnedReferenceNavigationLeaf` (cs:879) `IsSelectorParameter(receiver, outerParameter)`.
* Wrapped rows (1, 2): `new { x.Id, x.RequiredReference }` IS an admitted shape in general (FunctionalTests NativeOwnedReferenceWholeEntityTests.Owned_reference_entity_leaf_beside_field_leaf_goes_native...), so the decline is specific to this model and I could NOT pin it statically. Candidates in order: (a) JsonEntity is a shared-type owned type used by three navigations with its OWN nested owned references/collections (NestedOptionalReference, NestedRequiredReference, NestedCollection): the read side (MongoProjectionBindingExpressionVisitor owned-nav-entity leaf / auto-Include of nested owned navs) or the lowerer declines (MarkNotNativelyRepresentable); (b) the `Collection` array leaf of row 1 (List<JsonEntity> whose elements own nested collections) via IsNativeArrayProjectionLeaf; (c) `GetContainingElementName()` / alias check at cs:1090-1092 for the shared type. TASK 0 (do first): run the two wrapped rows under NativeOnly with a break on every `MarkNotNativelyRepresentable()` / `return false` of `TryPopulateNativeProjection`, record the first site, then fix that site; do not guess.

Design for the bare rows (rows 3, 4), recommended: do NOT add an alias-addressed $project; reuse the proven whole-document mixed reader natively:
* QMTEV.TranslateSelect (Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs, in the `else if (!IsTransparentIdentifierSelector...)` chain ~cs:595): new arm BEFORE `TryPopulateNativeProjection`: selector body is a chain of embedded REFERENCE navigations from the selector parameter (`x.N` or `x.N.M`, each `INavigation` `IsEmbedded() && !IsCollection`, final type == target CLR type), `Projection.Count == 0`, no terminal operator -> `Select.MarkOwnedNavigationEntityResult()` (new flag; leaves Route == WholeEntity so the pipeline is the plain filtered document fetch = baseline `Entities.{ "$match" : { "_id" : 5 } }`). Any later value-reading operator must `MarkNotNativelyRepresentable` (reuse the `HasClientWrappedWholeEntityShaper` post-select gates at QMTEV.cs:3513/3552 and NativeCardinalityBinder.cs:166 by also testing the new flag).
* Shaped compiler VisitProjectedQuery (Visitors/MongoShapedQueryCompilingExpressionVisitor.cs, before line 321 `ThrowIfNativeOnlyForbidsFallback`):
```csharp
if (queryMode != MongoQueryMode.DriverLinq
    && mongoQueryExpression.Select.Route == NativeRoute.WholeEntity
    && mongoQueryExpression.Select.HasOwnedNavigationEntityResult)
{
    return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
        (bsonDoc, behavior) => new MongoMixedProjectionBindingRemovingExpressionVisitor(
            rootEntityType, mongoQueryExpression, bsonDoc, behavior, pushedDownSelectRetained: false),
        allowStreaming: false);
}
```
  This is exactly what the late fallback executes today (strip the Select, mixed reader over whole docs), so the exception text is unchanged: "Field 'RequiredReference' required but not present" / "Field 'NestedRequiredReference' required but not present" come from the shared entity materializer (the same one already pinned by Project_root_entity_with_missing_required_navigation which passes natively).
Rows 1, 2 after Task 0: expected exceptions/values come from the same materializer ("Document element is missing for required ...": the comment at NativeProjectionBinder.cs:~490 already names this exact message for owner-key-less $project). Expected MQL for the override baselines: rows 3,4 `Entities.{ "$match" : { "_id" : 5 } }` / `6` (unchanged); rows 1,2: native emits `$project { Id:"$_id", OptionalReference.., RequiredReference.., Collection.., _id }` so the override baselines (`Entities.{ "$match" : { "_id" : { "$lt" : 4 } } }` only) must be regenerated if they go through the alias-addressed DOM path rather than the mixed reader.
Cheap alternative (use if the owner treats the three THROW rows as out of scope, per the brief "driver threw on main"): in the three throwing overrides add
```csharp
if (MongoSpecTestHelpers.IsNativeOnly) { await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(() => base.X(async)); AssertMql(); return; }
```
(accepts NativeTranslationNotSupportedException or InvalidOperationException) and only implement Task 0 for the one row that worked on main (Project_top_level_entity_with_null_value_required_scalars).
Tests (FunctionalTests, explicit modes, NativeModeAssert): `Select(x => x.Owned)` Native vs DriverLinq equal incl. missing/null required cases (existing Required_owned_reference_missing_throws_matching_driver_linq is the model), two-hop `Select(x => x.A.B)`, bare owned leaf then `Where`/`Distinct`/`Union` (must decline cleanly), optional ref absent -> null.

## 8. IsNativeOnly branches to delete when the above lands (NorthwindMiscellaneousQueryMongoTest.cs / others)
* Random_next_is_not_funcletized_1..6 (cs:4645-4651, 4660-4666, 4675-4681, 4690-4696, 4705-4711, 4720-): all six `if (MongoSpecTestHelpers.IsNativeOnly) {...}` blocks.
* Select_expression_datetime_add_ticks (cs:4482-4487).
* NorthwindGroupByQueryMongoTest.GroupBy_aggregate_using_grouping_key_Pushdown (cs:1251-1257).
* Not branches but baselines to change: Where_datetime_now/utcnow/datetimeoffset_utcnow/today (`Customers.`/`Employees.` -> `$match $expr true`), Where_bitwise_binary_* (and: gate + `$expr` baseline), Select_expression_other_to_string (add AssertMql), NorthwindDbFunctionsQueryMongoTest Random_return_* (exception type), AdHocJson rows per section 7.

## 9. Wrong-on-main to record (fix on main or accept)
* Select_expression_other_to_string: `DateTime.ToString()` pushed down as `$toString` = ISO, not .NET format; test asserter hides it.
* `StartsWith/EndsWith/Contains(x, CurrentCulture[IgnoreCase])` evaluated ordinally (culture-special data wrong); stays wrong natively after 2.4.
* `string.Compare(x, x.ToUpper())`: `$toUpper` ASCII-only on main (wrong for non-ASCII); native keeps declining.
* Random/Guid.NewGuid in Where: main baked one value (silent wrong) before EF-255.
* `DateTime.Now` comparisons are instant comparisons (client clock) on main; in-memory tick comparison differs when tz != UTC (accepted parity).
* AddTicks with sub-millisecond amount: driver emits fractional `$dateAdd` amount (server error/truncation).

## 10. Escalations (do not auto-approve)
* 2.4 culture comparisons ruling; 2.5 ToUpper-in-Compare ruling; 4.2 DateTime.ToString client-eval vs ISO parity ruling.
* Emitted MQL changes for Where_datetime_*, and any query using `DateTime.Now/UtcNow/Today(.AddX)` (placeholder instead of baked date).
* Exception type change TargetInvocationException -> InvalidOperationException for EF.Functions.Random (unsupported feature, not contract).
* Gate/visitor-pipeline touching: VisitShapedQuery scan (section 3) and VisitProjectedQuery new branch (section 7); both change orderings in the gate.
