# g2 — value converters / BsonRepresentation / read-back (research, static read, 2026-10-01)

Paths under `src/MongoDB.EntityFrameworkCore/Query/` unless stated. Every decline site found by code reading;
items marked VERIFY need a probe before coding.

## Decline sites

### A. Aggregate operand gate (5 tests)
`ProjectionTests.Sum_with_value_converter`, `Min_…`, `Max_…`, `Average_…`, `Sum_with_value_converter_via_ef_property`
(`PlanetWithLongOrder.orderFromSun` long with `HasConversion<int>()` = `CastingConverter<long,int>`, IntegralNarrowing).
- Decline: `NativeTranslation/NativeCardinalityBinder.cs:318` → `translator.TryTranslateValue(selector.Body)` false
  because `MongoExpressionTranslator.cs:173` rejects `!AllFieldsDefaultSerialized` (`NativeGroupByBinder.HasDefaultKeySerialization`,
  `NativeGroupByBinder.cs:194`). Selector-less variant: same gate at `NativeCardinalityBinder.cs:300-306`.
- Driver admits via `StoredOrdering.PreservesClrOrderingForAggregateAndSort` (allows IntegralNarrowing).
- Existing pins: `RepresentedPropertyPushdownTests.cs:~739-742` (`Big_Sum`, `Big_Max`, `Big_Average` decline).

### B. Comparison constants (1 test + hazard)
`ProjectionTests.Count_with_value_converter_in_predicate_is_refused` (main asserted `Count(p => p.orderFromSun > 4L) == 4`).
- Decline: `MongoExpressionTranslator.cs:1338/:1384` `ComparisonPreservesClrOrdering` → `StoredOrdering.PreservesClrOrdering`
  (`StoredOrdering.cs:66-70`) excludes IntegralNarrowing.
- What the refusal guards: the constant is serialized through `ValueConverterSerializer<long,int>` (unchecked
  `CastingConverter`), so `Big > 3_000_000_000L` wraps. Int32 vs Int64 order numerically server-side, so order is NOT the hazard.
- Latent (VERIFY, possibly a main bug): `Big == 3_000_000_000L` also wraps through the converter → matches stored -1294967296.

### C. Nested construction members and casts over non-default fields (4 tests)
- `Select_projection_nested_with_value_converter(_ef_property)`: `Sub = new { p.orderFromSun }`. Decline
  `NativeProjectionBinder.IsPlainTopLevelFieldRead` (`:704`) requires `HasDefaultKeySerialization`; called from
  `TryGetDocumentConstructionLeaf` (`:928-954`). Read side is already property-aware
  (`MongoProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember` ~:1008 →
  `BsonBinding.CreateGetPropertyValueAtPath(..., field.Property, ...)`; mixed reader `:347` too). Gate over-conservative.
- `Select_projection_alias_with_bson_representation_widening_cast` (`(long)p.orderFromSun`) and `_nullable_lift`
  (`(int?)p.orderFromSun`), int with `HasBsonRepresentation(String)`: decline `NativeProjectionBinder.cs:~1160-1190`
  (`TryTranslateValue` → `AllFieldsDefaultSerialized`). Read side raw-alias-reads any Convert leaf (`:~219-224`).

### D. Dotted owned leaf with BsonRepresentation (1 test)
`OwnedEntity_projection_alias_with_bson_representation_uses_owned_property_serializer` (`Alias = e.location.longitude`).
Decline `NativeProjectionBinder.cs:1031` (`!HasDefaultKeySerialization && ElementName.Contains('.')`; twin `:1050`).
Comment "resolver is single-hop" is stale: `TryResolveFieldAccessSource` (`:1083-1175`) recurses owned navs.

### E. Owned-collection element Select leaf (4 tests; not converter-related)
`OwnedEntity_collection_projection_alias_with_bson_representation…`, `SharedClrTypeProjectionTests.*` (3):
`new { e.secondary, Lngs = e.secondary.Select(l => new { Alias = l.lng }).ToList() }`. `TryTranslateLeafCore`
(`NativeProjectionBinder.cs:~1103-1190`) has no arm for a mapped Select over an owned collection. Shared-CLR-type trap:
`TryResolveFieldAccessSource` has a loose `FirstOrDefault(e => e.ClrType == parameterExpression.Type)` (~:1128) — resolve
the element entity type from the navigation, never by CLR type.

### F. Small causes
- `ValueConverterTests.Double_/Guid_can_deserialize_and_query_from_string_default`: `First(e => e.amount.ToString() == amount)`
  on a `HasConversion<string>()` property. `TryMatchIntegralToString` (`MongoExpressionTranslator.MethodCalls.cs:780-798`)
  admits integrals only; `AllFieldsDefaultSerialized` (`MongoExpressionTranslator.cs:1766`) rejects the converted field.
- `FindTests.Find_equivalent_in_LINQ_v3_works_when_constant_nullable`: `Equals(p._id, key)` with `ObjectId? key`; static
  `object.Equals` arm `MongoExpressionTranslator.cs:~802-818` returns null for a nullability-only type mismatch.
- `DateTimeOffsetMemberProjectionTests.Select_DateTimeOffset_remaining_components`: only `e.DateTimeOffset.TimeOfDay`
  declines; `TryTranslateTimeOfDayLeaf` (~:285-306) requires `receiver.Type == typeof(DateTime)`.
- `DateTimeOffsetMemberProjectionTests.*_with_string_representation_throws` / `*_with_value_converter_throws`: NOT gaps —
  driver threw `NotSupportedException` on main; fix the assertions (mode-aware), not the provider.

## Task designs

### T1. Min/Max read-back through the property serializer; checked Sum (F4, F8, bucket A)
Failing: the 5 bucket-A tests under NativeOnly. New `tests/.../Query/NativeAggregateReadBackTests.cs`:
```csharp
public enum Level { Low = 1, Mid = 2, High = 3 }
public class AggDoc { public ObjectId Id { get; set; } public Level Level { get; set; } public TimeSpan Span { get; set; }
    public DateOnly Day { get; set; } public char Letter { get; set; } public int Count { get; set; } }
[Theory]
[InlineData("Max_enum")] [InlineData("Min_enum")] [InlineData("Max_DateOnly")] [InlineData("Min_DateOnly")]
[InlineData("Max_char")] [InlineData("Min_char")]
public void MinMax_reads_back_through_property_serializer(string shape)
{
    var native = Run(MongoQueryMode.NativeOnly, shape);   // today: InvalidCastException at MongoShapedQueryCompilingExpressionVisitor.cs:928
    var driver = Run(MongoQueryMode.DriverLinq, shape);
    Assert.Equal(driver, native);
}
[Theory]
[InlineData(MongoQueryMode.Native)] [InlineData(MongoQueryMode.NativeOnly)] [InlineData(MongoQueryMode.DriverLinq)]
public void Sum_int_overflow_throws_OverflowException(MongoQueryMode mode)
{
    // seed Count = int.MaxValue, int.MaxValue, 1
    Assert.Throws<OverflowException>(() => Create(mode).Entities.Sum(e => e.Count));   // today native wraps (F4)
}
```
(TimeSpan Min/Max: see decision on lexicographic default storage.)

Code:
1. `Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:923-929`:
```csharp
private static object ConvertNumericNarrowing(object mapped, Type targetType) => mapped switch
{
    long l when targetType == typeof(int) => checked((int)l),
    double d when targetType == typeof(int) => checked((int)d),
    double d when targetType == typeof(long) => checked((long)d),
    _ => Convert.ChangeType(mapped, targetType)
};
```
2. Min/Max: generalize `NativeDateTimeKindReadBack.FindForAggregateOperand` (`:277-279`) to
`NativeAggregateReadBack.FindOperandProperty(MongoSelectDefinition select, MongoExpression operand)` (bare
`MongoFieldExpression` operand, or the bare-source projection of a selector-less `Select(...).Max()`), then
```csharp
var readProperty = cardinality is { Aggregate: MongoAggregateOperator.Min or MongoAggregateOperator.Max, Selector: { } operand }
    ? NativeAggregateReadBack.FindOperandProperty(mongoQueryExpression.Select, operand) : null;
var scalarSerializer = readProperty is null ? null : BsonSerializerFactory.CreateTypeSerializer(readProperty);
```
pass it in place of `kindAwareScalarSerializer`; in `DeserializeKindAwareScalar<TResult>`:
```csharp
var v = new BsonSerializationInfo(ScalarField, serializer, serializer.ValueType).DeserializeValue(bsonValue);
return v is TResult t ? t : (TResult)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult));
```
3. Gate for converted numeric operands (EF-337 aggregate half) in `MongoExpressionTranslator`:
```csharp
public bool TryTranslateStoredAggregateField(Expression body, MongoAggregateOperator op, [NotNullWhen(true)] out MongoFieldExpression? field)
{
    field = null;
    var operand = UnwrapOrderPreserving(body);
    if (!TryResolveMember(operand, out var property, out var path, out var isOuter) || isOuter) return false;
    if (!StoredOrdering.PreservesClrOrderingForAggregateAndSort(property)) return false;
    if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Average && !IsNumericType(property.ClrType)) return false;
    field = new MongoFieldExpression(property, path);
    return true;
}
```
Call after `TryTranslateValue` fails at `NativeCardinalityBinder.cs:318` and `:300-306`. Average over Decimal128-represented
int keeps the pinned TruncationException (use generic read for Sum/Average). Expected MQL: `$group {_id:null, v:{$sum:"$orderFromSun"}}`.
Pins flip: `RepresentedPropertyPushdownTests` `Big_*`, `IsPreExistingLoudFailure` clause (:316-324), `Max_string_as_ObjectId` (:449).

### T2. Cast leaf over BsonRepresentation field + nested construction members (bucket C)
1. `NativeProjectionBinder.cs:704` `IsPlainTopLevelFieldRead`: drop `&& HasDefaultKeySerialization(field.Property)` (keep no-dot).
   VERIFY second caller `TryCollectClientConditionalBranches` (~:620) and mixed fallback reader (:347).
2. Shared predicate (emit and read sides both call it):
```csharp
internal static bool TryGetClientRelabelCastOverStoredField(MongoExpressionTranslator translator, Expression leaf, out MongoFieldExpression field)
{
    field = null!;
    if (leaf is not UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u) return false;
    var from = u.Operand.Type.UnwrapNullableType(); var to = u.Type.UnwrapNullableType();
    if (!(from == to || MongoExpressionTranslator.IsWideningNumericConvert(from, to))) return false;
    if (!(u.Operand is MemberExpression || u.Operand is MethodCallExpression c && c.Method.IsEFPropertyMethod())) return false;
    if (!translator.TryTranslateField(u.Operand, out var f) || f.ElementName.Contains('.')) return false;
    if (f.Property.GetValueConverter() != null || f.Property.GetTypeMapping().Converter != null) return false;
    if (NativeGroupByBinder.HasDefaultKeySerialization(f.Property)) return false;
    field = f; return true;
}
```
   Insert before the `TryTranslateValue` arm (~:1160). Read side (`MongoProjectionBindingRemovingExpressionVisitor.cs:~219-224`):
```csharp
var read = BsonBinding.CreateGetValueExpression(DocParameter, projection.Alias, prop, prop.ClrType);
return Expression.Convert(read, projectionBindingExpression.Type);
```
Expected MQL `{ $project: { Position: "$orderFromSun", _id: 0 } }`. Negative: `(int)p.LongStoredAsString` still declines.

### T3. Dotted owned leaf with non-default property (bucket D)
Remove dotted+non-default declines at `NativeProjectionBinder.cs:1031`/`:1050` for wrapped leaves; bare/late-fallback strip
path still declines dotted via `TryDeriveDocumentPathAlias` / `IsWholeDocumentReadableLeaf` (:1790-1796). VERIFY late fallback.
Expected `{ $project: { Alias: "$Location.Longitude" } }`.

### T4. ToString() over a built-in to-string-converted property (bucket F, 7 rows)
In `TranslateComparisonCore` before the member arms (~:1330):
```csharp
if (nodeType is ExpressionType.Equal or ExpressionType.NotEqual
    && TryResolveToStringConvertedMember(leftUnwrapped, out var tsProp, out var tsPath)
    && rightUnwrapped.Type == typeof(string) && IsSimpleValue(rightUnwrapped))
{
    var tsOp = MapComparisonOperator(nodeType)!.Value;
    var tsValue = TranslateValue(rightUnwrapped, forSerialization: null);
    return tsValue is null ? null : new MongoBinaryExpression(tsOp, new MongoFieldExpression(tsProp, tsPath), tsValue);
}
// + mirrored (value == x.P.ToString()) arm
```
`TryResolveToStringConvertedMember`: zero-arg `ToString()` whose receiver resolves to a property whose converter is exactly
`GuidToStringConverter`, `NumberToStringConverter<T>` or `EnumToStringConverter<T>`, provider type string, no BsonRepresentation,
non-nullable model. Expected `{ amount: "1.1234" }`. Culture caveat for double/float (decision).

### T5. Static Equals with nullability-only mismatch (FindTests)
`MongoExpressionTranslator.cs:~805-818`:
```csharp
if (leftArg.Type != rightArg.Type)
{
    var leftBase = Nullable.GetUnderlyingType(leftArg.Type) ?? leftArg.Type;
    var rightBase = Nullable.GetUnderlyingType(rightArg.Type) ?? rightArg.Type;
    if (leftBase == rightBase)
        return TranslateComparisonCore(leftArg, rightArg, ExpressionType.Equal);
    return ExpressionExtensionMethods.AreMismatchedExactEqualityTypes(leftBase, rightBase)
        ? new MongoConstantExpression(false, forSerialization: null) : null;
}
```
Depends on F7 (null parameter vs non-nullable property) being fixed first.

### T6. DateTimeOffset.TimeOfDay projection leaf
`TryTranslateTimeOfDayLeaf`: accept a DateTimeOffset receiver →
`new MongoDatePartExpression(new MongoDateTimeOffsetLocalExpression(field), MongoDatePart.TimeOfDay)`; default-serialized
field only; read via `TimeOfDayMillisecondsSerializer`. Millisecond precision (same as DateTime TimeOfDay leaf).

### T7. Narrowing integral converters in native comparisons (bucket B) — needs owner ruling
```csharp
// StoredOrdering.cs
internal static bool PreservesClrOrderingForNativeComparison(IProperty p)
    => PreservesClrOrdering(p) || ClassifyNumericConversion(p) == NumericConversion.IntegralNarrowing;
internal static bool ConstantsSerializeByClrType(IProperty p)
    => ClassifyNumericConversion(p) is NumericConversion.IntegralNarrowing;
```
`MongoExpressionTranslator.cs:1538` uses the native predicate; `ConstantSerializationContext` (~:1550-1561) returns null when
`ConstantsSerializeByClrType` so constants serialize by CLR type (Int64). Shared `PreservesClrOrdering` untouched (bridge keeps
refusing). Expected `{ orderFromSun: { $gt: NumberLong(4) } }`. VERIFY `SerializeParameter` with null `forSerialization`.
Hazard test: stored int -1294967296 must NOT match `Big == 3_000_000_000L` (run on main too → main bug if it matches).

### T8. Owned-collection element Select leaf (bucket E)
Recommended option B: stage the whole owned array leaf (existing `TryTranslateOwnedCollectionArray` under the navigation
path), shaper re-applies `.Select(l => ...).ToList()` over materialized owned entities (like case-mapping client leaves);
same-path array-leaf dedupe at `NativeProjectionBinder.cs:~150` (`seenAliases.Add`). New `HasClient…` flag so later
value-reading operators decline. Option A (`$map` + per-element reader using owned property serializers from
`navigation.TargetEntityType`) if B is insufficient.

### T9. Test-only
`DateTimeOffsetMemberProjectionTests.*_throws` become mode-aware refusal assertions; refresh stale EF-337 comments in
`ProjectionTests.cs` (the `OrderBy(p => p.name)` tests).

## EF-337 summary
| Storage | Hazard | Faithful native rendering? |
|---|---|---|
| long as int (CastingConverter narrowing) | constant wraps through converter | YES (T7) |
| int as String | lexicographic order, `$sum` ignores strings | not recommended; driver WRONG on main |
| opaque custom converter | order unprovable | NO |
| decimal as int | truncation | NO |
| Int64/Double/Decimal128 representations | exact | already allowed |
