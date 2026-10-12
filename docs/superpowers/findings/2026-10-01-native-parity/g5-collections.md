# g5-collections: design (read-only research; nothing built or run)

Paths below are relative to /Users/arthur.vickers/code/provider3/src/MongoDB.EntityFrameworkCore/Query/ unless absolute.
NT = NativeTranslation/. MET = NT/MongoExpressionTranslator.cs (partials alongside). Line numbers are at HEAD c7854e7f.

NOT VERIFIED BY EXECUTION: shapes EF hands the translator (ArrayLength unary vs Count() call vs `.Count` member; get_Item
call shape). Task 0 of each section is a 10-minute "dump the tree" spike (log `predicate.Body.ToString()` in
NativeSlotPopulator Where arm) before coding. All arms below accept every plausible spelling so this is a confirmation.

## 1. Decline sites (all share the same terminal path)

Common tail: translator returns null -> NT/NativeSlotPopulator.cs:290 `mongoQ.Select.MarkNotNativelyRepresentable()` (Where arm
191-290) -> Route=Fallback -> Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:784 throws
`NativeTranslationNotSupportedException` under NativeOnly. (First() with predicate goes NT/NativeCardinalityBinder.cs:341/358 into
the same translator, so same cause.)

| Test(s) | Exact query | Decline site (translator) | Condition |
|---|---|---|---|
| WhereDictionaryTests Where_{Dictionary,IDictionary,ReadOnlyDictionary}_contains_key (3) | `e.Dictionary.ContainsKey("key1")` | MET:1183-1197 `default:` arm of TranslateNode | MethodCall "ContainsKey" matches no arm; `TryResolveMember(node)` false -> `return null` (1197) |
| ..._key_equals_value (3), _key_equals_null, _key_not_equals_null, _value_in_range (2: Dictionary, IDictionary) | `e.Dictionary["k"] == v` / `!= null` / `>= 72 && < 101` | MET TranslateComparisonCore: 1333/1381 `TryResolveMember` false for a `get_Item` call (no member chain), falls to `TranslateOperand(left)` -> bottom `TranslateValue(node,null)` (1909) null -> `if (leftOperand is null) return null` (1431-1433) | indexer call on a dictionary property has no IProperty/path; there is NO dictionary handling anywhere in Query/ (grep ContainsKey/get_Item/IDictionary: zero hits) |
| UpdateEntityTests.Update_dictionary_property_with_struct_value | `First(e => e.dictionary["Total"].Equals(expectedTotal1))` and `First(e => e.dictionary["Total"] == expectedTotal2)` (record struct; captured -> EF *parameters*) | same as above (Equals(T) arm MET:761-766 -> TranslateComparisonCore; `==` -> op_Equality BinaryExpression -> TranslateComparison) | needs a PARAMETER serialized by the dictionary's value serializer, not an IProperty serializer |
| WhereTests.Where_string_array_any, Where_string_list_any | `p.mainAtmosphere.Any()` | MET:1074-1075 quantifier arm | `TryResolveOwnedCollectionPath` requires the final hop be an embedded collection NAVIGATION; a primitive-collection PROPERTY (string[]/List<string>) fails -> `return null // not an owned-collection source` (Members.cs:544-546 explicitly says "a primitive collection property ... false") |
| Where_string_array_count / _list_count / _array_length | `.Count()==2`, `.Count==2`, `.Length==3` | MET TranslateOperand arm 1799-1801: `TryMatchCountExpression` matches Count()/`.Count` but `TryResolveOwnedCollectionPath` false; `.Length` (ExpressionType.ArrayLength unary, or member Length on array) is not matched by `TryMatchCountExpression` (MethodCalls.cs:120-158) at all | same root cause |
| CompositeKeyQueryTests.Should_match_entity_array_contains | `expected.Contains(e)` (Entity[] captured -> parameter; HasKey(Key1,Key2)) | NT/MET.EntityEquality.cs:232 `primaryKey.Properties.Count != 1` -> `return false // composite key: no multi-field $in`; then the general Contains arm (MET:916) cannot resolve item (SelfParam is not a member) and tuple/computed-needle arms don't match an entity -> null | single-key `$in` only |

Findings (not test-blocked, same files):
- F7 sites: NT/MongoPipelineFactory.cs:765-768 (scalar tail of SerializeParameter) and the `isArray` loop 750-757; constants:
  NT/MongoValueRenderer.cs:62-68 `ToBsonValue` (`SerializeThroughWriter(info.Serializer, null)`). Int32Serializer/GuidSerializer/
  EnumSerializer<T> etc. hard-cast the boxed value -> NullReferenceException (not in the catch list).
- F9: NT/MongoValueRenderer.cs:45 `BsonValue.Create(constant.Value)` for a tuple element made by TryDecomposeTupleOperand
  (MET.TupleEquality.cs:~97 `new MongoConstantExpression(tupleValue[i], forSerialization: null)` / TryTranslateTupleArguments).
- F5: MET:998 (`receiverProperty.ClrType != typeof(string)`) and :1039 (termProperty). Same ClrType-only gate at
  MET.CaseMapping.cs:47, MET.Like.cs:68, MET.Regex.cs:88,106,128, MET.StringEquals.cs:64.

## 2. Design

### 2.1 Primitive collection Any()/Count()/Count/Length  (string[] / List<string> and any scalar-element array property)

Root cause is purely "resolver only knows owned navigations". Everything downstream already exists (MongoSizeExpression,
TryRenderSizeComparison array-index `$exists` form, negator inversion). So: add a primitive-collection resolver and call it
from the two arms.

NT/MET.Members.cs (new member):
```csharp
/// Resolves a bare primitive-collection PROPERTY (string[], List<int>, ...) stored as a BSON array of scalars to its
/// document path. Declines converted/non-array storage: a whole-collection converter has no array to $size/index.
private bool TryResolvePrimitiveCollectionPath(
    Expression source, [NotNullWhen(true)] out string? arrayPath, out bool isOuter)
{
    arrayPath = null;
    if (!TryResolveMember(Unwrap(source), out var property, out var path, out isOuter))
        return false;

    var elementType = GetEnumerableElementType(property.ClrType);
    if (property.ClrType == typeof(string) || elementType is null
        // Scalar elements only: "path.<n>" on an array of arrays/documents is ambiguous with a field named "<n>".
        || (elementType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(elementType)))
        return false;

    if (!NativeGroupByBinder.HasDefaultKeySerialization(property) || property.GetBinaryVectorDataType() != null)
        return false;

    var serializer = BsonSerializerFactory.GetPropertySerializationInfo(property).Serializer;
    if (serializer is not IBsonArraySerializer || serializer is IBsonDictionarySerializer)
        return false;

    arrayPath = path;
    return true;
}
```
(`GetEnumerableElementType` is MethodCalls.cs:826, already used by the Contains arm which is the precedent; `using MongoDB.Bson.Serialization;`
`using MongoDB.EntityFrameworkCore.Serializers;` as TranslateArrayContainsItem does.)

MET.cs quantifier arm (1074-1075) becomes:
```csharp
if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType, out var sourceIsOuter))
{
    // bare Any() over a primitive collection property: same "count >= 1" node as the owned form.
    if (elementLambda is null
        && TryResolvePrimitiveCollectionPath(quantifierSource, out var primitiveAnyPath, out var primitiveAnyOuter)
        && !primitiveAnyOuter)
        return new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression(primitiveAnyPath, typeof(int), nullSafe: true),
            new MongoConstantExpression(1, forSerialization: null));
    return null;   // Any(pred)/All(pred) over primitive collections stay declined (see follow-ups)
}
```
MET.cs TranslateOperand, insert BEFORE the existing owned count arm (line ~1799):
```csharp
if (TryMatchCountExpression(node, out var primitiveCountSource, out var primitiveCountPredicate)
    && primitiveCountPredicate is null
    && TryResolvePrimitiveCollectionPath(primitiveCountSource, out var primitiveCountPath, out var primitiveCountOuter)
    && !primitiveCountOuter)
    return new MongoSizeExpression(primitiveCountPath, node.Type, nullSafe: true);
```
MET.MethodCalls.cs `TryMatchCountExpression` (switch at ~134): add two cases before `default`:
```csharp
// array.Length: the expression-tree form of an array's length is the ArrayLength node ...
case UnaryExpression { NodeType: ExpressionType.ArrayLength, Operand: { } arrayOperand }:
    source = arrayOperand; return true;
// ... and hand-built/EF-rewritten trees may carry the member form. Arrays only: string.Length must not match.
case MemberExpression { Member: PropertyInfo { Name: nameof(Array.Length) }, Expression: { Type.IsArray: true } arrayReceiver }:
    source = arrayReceiver; return true;
```
Name-collision safety is preserved because every consumer of TryMatchCountExpression is gated by a path resolver
(NativeReferenceCollectionCountPredicateBinder.cs:51/58 calls it too: it requires a reference-collection nav, so a `.Length`
match there resolves to nothing and falls through; add a test that `c.Orders.Count > 2` is unchanged).

Expected MQL (existing renderer, no renderer change):
- `Any()`            -> `{ "mainAtmosphere.0": { $exists: true } }`
- `Count()==2`/`Length==3` -> `{ "mainAtmosphere.1": {$exists:true}, "mainAtmosphere.2": {$exists:false} }` (n-1 exists, n not)
- `Count()==0` -> `{ "mainAtmosphere.0": {$exists:false} }` (true for missing/null/empty, matching LINQ)
- parameterized threshold / `$expr` fallback -> `{$expr:{$eq:[{$size:{$ifNull:["$mainAtmosphere",[]]}}, <ph>]}}`.
`!Any()` flows through the Not arm (MET:837 `Left: MongoSizeExpression` -> TryNegate inversion).

Risks: (a) `path.<n>` exists-form on an array-of-arrays (excluded by the scalar-element gate); (b) value-converted collection
(excluded: `HasDefaultKeySerialization`); (c) EF may normalize `.Count`/`.Length` for primitive collections into
`Queryable.Count(AsQueryable(EF.Property<..>))` - handled by existing UnwrapAsQueryable + TryResolveMember's EF.Property arm;
(d) EF10 may rewrite primitive-collection `Any()` into something else (Task 0 spike).

### 2.2 Dictionaries (always BSON document representation)

Storage facts: MongoTypeMappingSource.CreateDictionaryTypeMapping admits string keys only; BsonSerializerFactory.GetDictionarySerializer
(Serializers/BsonSerializerFactory.cs:414-451) always builds `DictionaryInterfaceImplementerSerializer`/`ReadOnly...` with default
(Document) representation; `[BsonDictionaryOptions]` is rejected by BsonDictionaryOptionsAttributeConvention. So `d["k"]` is the
field path `<dictPath>.k`; there is no array-of-documents layout to support. The serializer still gets checked
(`IBsonDictionarySerializer { DictionaryRepresentation: Document }`) so a converter/future option declines instead of mis-rendering.

Following the AGENTS.md rule (node needing different handling at 3+ sites = sealed sibling, never a flag on MongoFieldExpression),
two new sealed nodes. MongoFieldExpression cannot carry it: it requires an IProperty whose serializer is the DICTIONARY's.

New file Expressions/MongoDictionaryEntryExpression.cs:
```csharp
internal sealed class MongoDictionaryEntryExpression : MongoExpression
{
    public MongoDictionaryEntryExpression(IProperty dictionaryProperty, string path, Type valueType)
    { DictionaryProperty = dictionaryProperty; Path = path; Type = valueType; }
    public IProperty DictionaryProperty { get; }
    /// "<dictionary document path>.<key>", e.g. "Dictionary.key1".
    public string Path { get; }
    public override Type Type { get; }       // the CLR value type TValue
}
```
New file Expressions/MongoDictionaryContainsKeyExpression.cs: `(string Path, bool Negated)`, Type bool.

New file NT/MongoExpressionTranslator.Dictionary.cs (partial):
```csharp
private static readonly Type[] StringKeyedDictionaryInterfaces = [typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>)];

private static bool TryGetStringKeyedValueType(Type type, [NotNullWhen(true)] out Type? valueType)
{
    valueType = null;
    foreach (var i in type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces())
        if (i.IsGenericType && StringKeyedDictionaryInterfaces.Contains(i.GetGenericTypeDefinition())
            && i.GetGenericArguments() is [var key, var value] && key == typeof(string))
        { valueType = value; return true; }
    return false;
}

// A dotted path can't address "", a key containing '.', or one starting with '$'; (also NUL).
private static bool IsAddressableDictionaryKey(string key)
    => key.Length > 0 && key[0] != '$' && !key.Contains('.') && !key.Contains('\0');

private bool TryResolveDictionaryProperty(Expression receiver, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? path)
{
    if (!TryResolveMember(Unwrap(receiver), out property, out path, out var isOuter) || isOuter
        || !TryGetStringKeyedValueType(property.ClrType, out _)
        || !NativeGroupByBinder.HasDefaultKeySerialization(property))
        return false;
    return BsonSerializerFactory.GetPropertySerializationInfo(property).Serializer
        is IBsonDictionarySerializer { DictionaryRepresentation: DictionaryRepresentation.Document };
}

/// `d["key"]` / `EF.Property`-rooted receivers; only a constant key (a parameterized key would need a runtime path segment).
private bool TryResolveDictionaryEntry(Expression node, [NotNullWhen(true)] out MongoDictionaryEntryExpression? entry)
{
    entry = null;
    if (node is not MethodCallExpression { Method.Name: "get_Item", Object: { } receiver, Arguments: [var keyArg] }
        || !TryGetStringKeyedValueType(receiver.Type, out var valueType)
        || Unwrap(keyArg) is not ConstantExpression { Value: string key } || !IsAddressableDictionaryKey(key)
        || !TryResolveDictionaryProperty(receiver, out var property, out var path))
        return false;
    entry = new MongoDictionaryEntryExpression(property, path + "." + key, valueType);
    return true;
}

private bool TryTranslateDictionaryContainsKey(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    if (call is not { Method.Name: "ContainsKey", Object: { } receiver, Arguments: [var keyArg] }
        || !TryGetStringKeyedValueType(receiver.Type, out _)
        || Unwrap(keyArg) is not ConstantExpression { Value: string key } || !IsAddressableDictionaryKey(key)
        || !TryResolveDictionaryProperty(receiver, out _, out var path))
        return false;
    result = new MongoDictionaryContainsKeyExpression(path + "." + key, negated: false);
    return true;
}

// The value side, serialized by the DICTIONARY's own value serializer (what SaveChanges wrote), not by an IProperty.
private static MongoExpression? TranslateDictionaryValue(Expression valueNode, MongoDictionaryEntryExpression entry)
{
    var valueSerializer = ((IBsonDictionarySerializer)BsonSerializerFactory
        .GetPropertySerializationInfo(entry.DictionaryProperty).Serializer).ValueSerializer;
    switch (valueNode)
    {
        case ConstantExpression { Value: null }:
            return new MongoConstantExpression(BsonNull.Value, forSerialization: null);
        case ConstantExpression constant:
            try
            {   // pre-rendered, like TranslateArrayContainsItem; BsonValue.Create(BsonValue) is the identity.
                var coerced = BsonValueSerializer.Coerce(entry.Type, constant.Value);
                return new MongoConstantExpression(BsonValueSerializer.SerializeThroughWriter(valueSerializer, coerced), forSerialization: null);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or InvalidOperationException)
            { return null; }
        case var p when NativeQueryParameter.TryGetQueryParameterName(p, out var name):
            return new MongoParameterExpression(name, forSerialization: null, valueType: p.Type, explicitSerializer: valueSerializer);
        default:
            return null;
    }
}

// Equality works for any value type (struct/sub-document included). Relational operators run on the stored form, so only
// types whose stored order equals CLR order and whose serializer is the plain one.
private static bool IsOrderPreservingEntryType(Type t)
    => (Nullable.GetUnderlyingType(t) ?? t) is var u
       && (u == typeof(int) || u == typeof(long) || u == typeof(short) || u == typeof(byte) || u == typeof(double) || u == typeof(string));

/// Returns true when it OWNS the comparison (an unresolvable value then declines the whole comparison; don't fall
/// through to the general path, which would mis-handle a dictionary get_Item).
private bool TryTranslateDictionaryEntryComparison(
    Expression left, Expression right, ExpressionType nodeType, out MongoBinaryExpression? result)
{
    result = null;
    var l = Unwrap(left); var r = Unwrap(right);
    bool mirrored; Expression entrySide, valueSide; MongoDictionaryEntryExpression? entry;
    if (TryResolveDictionaryEntry(l, out entry)) { entrySide = left; valueSide = r; mirrored = false; }
    else if (TryResolveDictionaryEntry(r, out entry)) { entrySide = right; valueSide = l; mirrored = true; }
    else return false;

    // A non-identity cast on the entry side ((long)d["k"] > 5L) is not absorbed here: decline.
    if (HasNonIdentityConvert(entrySide, entry.Type)) return true;
    if (!IsSimpleValue(valueSide)) return true;
    var op = MapComparisonOperator(mirrored ? Mirror(nodeType) : nodeType);
    if (op is null || (IsRelationalComparison(nodeType) && !IsOrderPreservingEntryType(entry.Type))) return true;

    var value = TranslateDictionaryValue(valueSide, entry);
    if (value is not null) result = new MongoBinaryExpression(op.Value, entry, value);
    return true;
}
```
(`HasNonIdentityConvert`: peel Convert layers; every layer must be object-boxing or nullable<->underlying of entry.Type; reuse the
body of `UnwrapOrderPreserving` with "widening" disallowed. Small private static.)

Hook sites:
1. MET TranslateComparisonCore, immediately after the entity-null arm (before the "Query-native shape" comment, line ~1327):
```csharp
if (TryTranslateDictionaryEntryComparison(left, right, nodeType, out var dictionaryComparison))
    return dictionaryComparison;
```
2. MET TranslateNode, before the `default:` arm (any position among MethodCall arms; put right after the `callEq` arm at :730):
```csharp
case MethodCallExpression { Method.Name: "ContainsKey" } containsKeyCall
    when TryTranslateDictionaryContainsKey(containsKeyCall, out var containsKey):
    return containsKey;
```
3. NT/MongoQueryLanguageRenderer.cs:
   - `IsQueryNativeComparison` (102): `(b.Left is MongoFieldExpression or MongoDictionaryEntryExpression) && b.Right is MongoConstantExpression or MongoParameterExpression`
   - `RenderComparison` (~122): `var elementName = binary.Left switch { MongoFieldExpression f => f.ElementName, MongoDictionaryEntryExpression d => d.Path, _ => throw ... };`
   - RenderNode: `MongoDictionaryContainsKeyExpression k => new BsonDocument(k.Path, new BsonDocument("$exists", !k.Negated)),`
   - IsQueryDialectRenderable: `MongoDictionaryContainsKeyExpression => true,` (comparison case is covered by IsQueryNativeComparison)
4. NT/MongoExpressionNegator.TryFlipNegatedFlag: `MongoDictionaryContainsKeyExpression e => new MongoDictionaryContainsKeyExpression(e.Path, !e.Negated),` ($exists true/false partition everything).
5. NT/MongoFieldPrefixRewriter.Rewrite: `MongoDictionaryEntryExpression d => new MongoDictionaryEntryExpression(d.DictionaryProperty, prefix + "." + d.Path, d.Type)`, `MongoDictionaryContainsKeyExpression k => new(prefix + "." + k.Path, k.Negated)`.
6. NT/MongoAggregationExpressionRenderer: NO arm (falls to the throw -> TryBuildPipeline fallback; CanRender default false). A dictionary comparison
   inside an `$expr`-only context therefore falls back to driver-LINQ in Native mode, loudly declines in NativeOnly. Justified: a bare `$lt` over a
   possibly-missing key answers true in $expr (null orders low) - AGENTS.md "aggregation dialect orders null/missing below every value".
7. MongoParameterExpression: add ctor arg `IBsonSerializer? explicitSerializer = null` + property `ExplicitSerializer`; `Type` => `ForSerialization?.ClrType ?? ValueType ?? typeof(object)`.
   NT/MongoValueRenderer.RenderValue parameter branch, first lines:
   `if (parameter.ExplicitSerializer is { } explicitSerializer) return placeholders.CreatePlaceholder(parameter.Name, explicitSerializer);`
8. tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNodeCoverageTests.cs: add the two samples + matrix rows
   (this test fails until every dispatcher is visited - that is its purpose); MongoFieldPrefixRewriterTests/MongoQueryLanguageRendererTests rows.

Expected MQL:
- ContainsKey("key1")            -> `{ "Dictionary.key1": { $exists: true } }`; `!ContainsKey` -> `{ $exists: false }`
- `["key2"] == "value2"`         -> `{ "Dictionary.key2": "value2" }`
- `["key2"] == null`             -> `{ "Dictionary.key2": null }` (matches null AND missing key = driver parity); `!= null` -> `{ "Dictionary.key1": { $ne: null } }`
- `["key1"] >= 72 && ["key2"] < 101` -> `{ "Dictionary.key1": { $gte: 72 }, "Dictionary.key2": { $lt: 101 } }`
- struct: `{ "dictionary.Total": { Currency: "USD", Amount: <as the class map writes it> } }` (param -> placeholder via the value serializer; exact sub-document equality,
  field order = class-map order, identical to what SaveChanges wrote and to the driver).
Parity note: a missing key is "null" here and throws KeyNotFoundException in C#; the driver does the same as this (documented difference, already main behaviour).

Risks: (1) STEP 0 VERIFY `DictionaryInterfaceImplementerSerializer<,,>` AND `ReadOnlyDictionaryInterfaceImplementerSerializer<,,>` both are
`IBsonDictionarySerializer` with `DictionaryRepresentation.Document` (unit assertion over the three property types); if ReadOnly is not, the 3
ReadOnly tests need a type-based fallback (`DictionaryRepresentation` is not readable) - gate on the concrete serializer type instead.
(2) `get_Item` EF shape: EF may wrap the dictionary property in `Convert` to the interface (Unwrap handles) or hand `Property<T>(e,"Dictionary")` (TryResolveMember handles).
(3) decimal/DateTime value types in the equality arm go through the dictionary value serializer (same as write) so they are exact; relational gate keeps them out.
(4) Key containing '.', '$', empty, or a parameterized key declines (-> driver in Native, throws in NativeOnly): same as before this work.
(5) `Dictionary<string,bool>` bare `Where(e => e.Dict["flag"])` and `Select`/OrderBy over an entry stay declined (follow-ups).

### 2.3 Composite-key entity-list Contains  (`expected.Contains(e)`, HasKey(Key1,Key2))

Rendering decision: `{ $or: [ { "_id.Key1": "two", "_id.Key2": 2 }, ... ] }` - one conjunction of dotted key paths per element. This is
order-agnostic and index-usable (an `_id: {$in:[docs]}` form would be an ORDER-SENSITIVE exact sub-document match and silently differ from
driver-LINQ's per-field OR chain on data written with a different key order). Parameter length is unknown at compile time, so the clause
array is built per execution by a placeholder (the template is compiled once; AGENTS.md pipeline invariant).

Empty array: `$or:[]`/`$nor:[]` is a server error, so the builder emits one always-false clause `{ "_id.Key1": { $in: [] } }`
(`$or` -> matches nothing; `$nor` -> matches everything, i.e. `!Contains` over an empty list). Null elements are skipped (can never match).

New file Expressions/MongoCompositeKeyInExpression.cs:
```csharp
internal sealed class MongoCompositeKeyInExpression(IReadOnlyList<IProperty> keyProperties, MongoExpression values, bool negated)
    : MongoExpression
{
    public IReadOnlyList<IProperty> KeyProperties { get; } = keyProperties;
    /// MongoConstantExpression { Value: IEnumerable of entity instances } or MongoParameterExpression (the entity list).
    public MongoExpression Values { get; } = values;
    public bool Negated { get; } = negated;
    public override Type Type => typeof(bool);
}
```
Shared builder (new static, NT/CompositeKeyClauses.cs) used by compile-time render AND MongoPipelineFactory so constant/parameter agree:
```csharp
internal static class CompositeKeyClauses
{
    internal static BsonArray Build(IEnumerable entities, IReadOnlyList<IProperty> keys)
    {
        var clauses = new BsonArray();
        foreach (var entity in entities)
        {
            if (entity is null) continue;
            var clause = new BsonDocument();
            foreach (var key in keys)
            {
                var serializer = BsonSerializerFactory.GetPropertySerializationInfo(key).Serializer;
                var coerced = BsonValueSerializer.Coerce(serializer.ValueType, key.GetGetter().GetClrValue(entity));
                clause.Add(MongoExpressionTranslator.GetKeyFieldPath(key), BsonValueSerializer.SerializeNullAware(serializer, coerced));
            }
            clauses.Add(clause);
        }
        if (clauses.Count == 0)
            clauses.Add(new BsonDocument(MongoExpressionTranslator.GetKeyFieldPath(keys[0]), new BsonDocument("$in", new BsonArray())));
        return clauses;
    }
}
```
(GetKeyFieldPath is `internal static` at EntityEquality.cs:~139.)

PlaceholderTable: add `CreateCompositeKeyArrayPlaceholder(string name, IReadOnlyList<IProperty> keys)`; extend the entry tuple with an 8th element
`IReadOnlyList<IProperty>? CompositeKeyProperties` (single deconstruction site: MongoPipelineFactory.cs:681; Entries is only read at NativeProjectionBinder.cs:2498 `.Count`
and MongoValueRendererTests.cs:47). MongoPipelineFactory.SerializeParameter, first branch after the missing-key check:
`if (compositeKeys is not null) return CompositeKeyClauses.Build((IEnumerable)(rawValue ?? Array.Empty<object>()), compositeKeys);`

MongoQueryLanguageRenderer: RenderNode arm + IsQueryDialectRenderable (`Values is MongoConstantExpression or MongoParameterExpression`):
```csharp
MongoCompositeKeyInExpression ck => RenderCompositeKeyIn(ck, placeholders),
private BsonDocument RenderCompositeKeyIn(MongoCompositeKeyInExpression ck, PlaceholderTable placeholders)
    => new(ck.Negated ? "$nor" : "$or", ck.Values switch
    {
        MongoConstantExpression { Value: System.Collections.IEnumerable items } => CompositeKeyClauses.Build(items, ck.KeyProperties),
        MongoParameterExpression p => placeholders.CreateCompositeKeyArrayPlaceholder(p.Name, ck.KeyProperties),
        _ => throw new NativeTranslationNotSupportedException("Unsupported composite-key $in values node.")
    });
```
Negator.TryFlipNegatedFlag: `MongoCompositeKeyInExpression e => new(e.KeyProperties, e.Values, !e.Negated)`. FieldPrefixRewriter: no arm (default throws -> decline in prefixed scopes; coverage-matrix row says so).
Aggregation renderer: no arm (declines).

Translator, EntityEquality.cs:232 replace the single-key guard:
```csharp
var primaryKey = _entityType.FindPrimaryKey();
if (primaryKey is null) return false;
if (primaryKey.Properties.Any(p => p.IsShadowProperty())) return false;

if (primaryKey.Properties.Count > 1)
{
    var entityValues = TranslateEntityListValues(collection);
    if (entityValues is null) return false;
    result = new MongoCompositeKeyInExpression(primaryKey.Properties, entityValues, negated: false);
    return true;
}
var keyProperty = primaryKey.Properties[0]; // existing single-key path unchanged
```
```csharp
internal static MongoExpression? TranslateEntityListValues(Expression collectionExpr)
{
    var unwrapped = Unwrap(collectionExpr);
    if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } c) return new MongoConstantExpression(c.Value, forSerialization: null);
    return NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var name)
        ? new MongoParameterExpression(name, forSerialization: null) : null;
}
```
`!expected.Contains(e)` takes the generic Not arm (MET:821-828) -> TryFlipNegatedFlag.

Expected MQL for the test: `{ $or: [ { "_id.Key1": "two", "_id.Key2": 2 }, { "_id.Key1": "two", "_id.Key2": 3 } ] }`.

Risks: tuple-shape growth in PlaceholderTable (mechanical); `IsSelfParamTheEntity` guard (root entity only, already in TryTranslateEntityListContains); composite key whose entity has a value-converted key component (serializer from factory handles); a TPH base type with composite key (IsSelfParamTheEntity's subtype note applies - same as single-key path).

### 2.4 F7 - null parameter / null list element vs non-nullable property (parity with driver)

Driver on main: `Rank == nullParam` -> `{Rank:null}` (matches missing -> [c]); `!=` -> `{Rank:{$ne:null}}` ([a,b]); `Level == nullLvl` -> [];
`ids=[1,null].Contains(Rank)` -> `{Rank:{$in:[1,null]}}` ([a,c]). Native should emit exactly these (parity, not "correct" C# [] / [a,b,c]; with
well-formed data - Rank present everywhere - both agree).

One shared helper, NT/BsonValueSerializer.cs:
```csharp
/// SerializeThroughWriter, but a null for a serializer of a NON-nullable value type (Int32Serializer, GuidSerializer,
/// EnumSerializer<T>, DateTimeSerializer, BooleanSerializer ...) is BSON null instead of a NullReferenceException from the
/// serializer's unbox. Reference/nullable serializers keep writing null themselves (unchanged behaviour).
public static BsonValue SerializeNullAware(IBsonSerializer serializer, object? value)
    => value is null && serializer.ValueType is { IsValueType: true } vt && Nullable.GetUnderlyingType(vt) is null
        ? BsonNull.Value
        : SerializeThroughWriter(serializer, value);
```
Call sites (3 + 1): MongoValueRenderer.ToBsonValue (:67), MongoPipelineFactory.SerializeParameter isArray loop (:752-756), scalar tail (:766-768), entity-array loop
(:709-710, keys never null but harmless). Constant list `new int?[]{1,null}` renders via RenderInValues -> RenderValue(new MongoConstantExpression(null, property)) -> ToBsonValue -> fixed.
No translator change. No emitted MQL changes for anything that previously worked (it threw).

Aggregation-dialect caveat (document, don't fix): `$eq:[ "$Rank", <null param> ]` does not equate missing with null (AGENTS.md: top-level $eq/$ne null is `$ifNull`-wrapped only for a compile-time null). The member-vs-parameter shape is query dialect (`{Rank: null}`) so F7's cases are unaffected; a computed operand compared with a null parameter stays an existing, separate question.

### 2.5 F9 - Guid (and other BsonValue.Create-unmappable) tuple element

Cause: tuple elements are translated property-less, so a constant/parameter beside a field is `BsonValue.Create`d. Fix = pair each constant/parameter element
with the field in the same tuple position on the other side and give it that property as serialization context (exactly what the driver does through its
member serializer; the field read in $expr is the stored form, so the constant must be the stored form too).

MET.TupleEquality.cs, in TryTranslateTupleEquality after the decomposition and before building `leftTuple/rightTuple`:
```csharp
leftElements = BindTupleValueSerialization(leftElements, rightElements);
rightElements = BindTupleValueSerialization(rightElements, leftElements);   // uses the ORIGINAL left via the captured array: call order matters; clone first
```
(write as: `var (l, r) = (BindTupleValueSerialization(leftElements, rightElements), BindTupleValueSerialization(rightElements, leftElements)); leftElements = l; rightElements = r;`)
```csharp
private static MongoExpression[] BindTupleValueSerialization(MongoExpression[] values, MongoExpression[] counterparts)
{
    var bound = (MongoExpression[])values.Clone();
    for (var i = 0; i < bound.Length; i++)
    {
        if (counterparts[i] is not MongoFieldExpression { Property: var property }
            || !NativeGroupByBinder.HasDefaultKeySerialization(property))
            continue;

        bound[i] = values[i] switch
        {
            MongoConstantExpression { ForSerialization: null, Value: { } v } when !IsBsonValueCreateSafe(v.GetType())
                => new MongoConstantExpression(v, property),
            MongoParameterExpression { ForSerialization: null, RawElementType: null, ExtractFromEntityValue: false } prm
                when prm.ValueType is { } t && !IsBsonValueCreateSafe(t)
                => new MongoParameterExpression(prm.Name, property, arrayElementIndex: prm.ArrayElementIndex, valueType: prm.ValueType),
            _ => values[i]
        };
    }
    return bound;
}

// What BsonValue.Create maps without a serializer; everything else needs the field's serializer (Guid, enums, DateTimeOffset, TimeSpan, char, DateOnly ...).
private static bool IsBsonValueCreateSafe(Type t)
{
    t = Nullable.GetUnderlyingType(t) ?? t;
    return t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(double)
        || t == typeof(DateTime) || t == typeof(ObjectId) || t == typeof(Decimal128);
}
```
Restricting the rebind to unmappable types leaves every currently-working tuple's MQL byte-identical (no EF cache / stored-query change).
TryDecomposeTupleOperand default branch (whole-tuple parameter): pass `valueType: operand.Type.GetGenericArguments()[i]` into each element MongoParameterExpression so `ValueType` is known.

Safety net (covers computed counterparts with no field to pair, and ANY future raw constant): NT/MongoValueRenderer.RenderValue
```csharp
case MongoConstantExpression constant:
    if (constant.ForSerialization is not null) return ToBsonValue(constant.ForSerialization, constant.Value);
    try { return BsonValue.Create(constant.Value); }
    catch (ArgumentException)   // "cannot be mapped to a BsonValue" (Guid, enum, DateTimeOffset...)
    { throw new NativeTranslationNotSupportedException($"Native predicate translation cannot serialize a '{constant.Value?.GetType().Name}' constant without a property serializer."); }
```
-> TryBuildPipeline turns it into a driver-LINQ fallback in Native mode (MongoShapedQueryCompilingExpressionVisitor.cs:805), a clean decline in NativeOnly.
Likewise for a ForSerialization-null parameter whose `ValueType` is not BsonValue.Create-safe: throw the same exception at render time (compile time), because the
runtime `BsonValue.Create(rawValue)` in MongoPipelineFactory.cs:760-761 cannot fall back.

Expected MQL: `{ $expr: { $eq: [ ["$Gid","$N"], [ BinData(4,...), 1 ] ] } }` (Gid constant produced by the Guid property's serializer = same representation as stored).

### 2.6 F5 - string StartsWith/EndsWith/Contains on [BsonRepresentation(ObjectId)] string

Decision: DECLINE (do not translate). Rationale: a regex runs on the stored value; with an ObjectId representation the stored value is not a string, so a
regex arm can only be wrong (native: silently `[]`) and the translatable alternative (`$toString` receiver via the computed-receiver arm: `$regexMatch` over
`{$toString:"$_id"}`, the 24-hex string C# sees) is a NEW capability that also changes what main did (server error). Declining makes Native mode fall back to the
driver, which raises the same server error as main (loud, parity); NativeOnly throws NativeTranslationNotSupportedException. Not a regression of a working case (main never worked).
Offer `$toString` translation as an optional follow-up (it would make the case work, strictly better than main).

Shared predicate, NT/MET.Regex.cs (or Members.cs), used at all 8 ClrType==string sites listed in section 1:
```csharp
/// A regex runs on the STORED value: only a string stored as a BSON string (no representation, or explicit String) can be searched.
/// A value converter is deliberately NOT gated here: converted-but-string-stored properties already behave identically on both paths
/// (pre-existing main behaviour, e.g. probe "Docs.Where(Code.StartsWith)" => [] in all modes) and gating would turn coincidentally-correct
/// Contains results into NativeOnly failures.
internal static bool IsRegexSearchableString(IProperty property)
    => property.ClrType == typeof(string)
       && property.GetBsonRepresentation() is null or { BsonType: BsonType.String };
```
Edits: MET:998 `if (!IsRegexSearchableString(receiverProperty)) return null;`, MET:1039 `|| !IsRegexSearchableString(termProperty)`, CaseMapping:47, Like:68, Regex:88/106/128, StringEquals:64.
Note: `Id`/`Contains(string)` on key with ObjectId representation also reaches the `MongoRegexExpression` via `TryTranslateStringEqualsWithComparison`/Like - covered by the same predicate.
Escalate-to-user note: a query that returned `[]` natively now throws (NativeOnly) / server-errors (Native, = main). That is the intended fix for silent-wrong, but it IS "a translation that previously 'worked' now fails" in the escalation list's literal sense.

## 3. TDD task designs (in order; each = failing test -> minimal code -> green on EF8/9/10)

All new native tests: new file tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCollectionGapTests.cs (+ unit tests under UnitTests/Query/NativeTranslation/).
Existing 17 gap tests are the failing tests for T1-T4 (they need a way to run under NativeOnly: either the harness flag, or pin a copy - see "Pinning" note).

Shared helper (put in the new test file):
```csharp
private void AssertNativeParity<T>(IMongoCollection<T> collection, Func<IQueryable<T>, List<string>> query,
    Action<ModelBuilder>? model = null, string[]? expected = null) where T : class
{
    List<string> Run(MongoQueryMode mode)
    {
        using var db = SingleEntityDbContext.Create(collection, model,
            b => new MongoDbContextOptionsBuilder(b).UseQueryMode(mode));
        return query(db.Entities.AsNoTracking());
    }
    var native = Run(MongoQueryMode.NativeOnly);       // throws NativeTranslationNotSupportedException if it declines
    Assert.Equal(Run(MongoQueryMode.DriverLinq), native);
    if (expected is not null) Assert.Equal(expected, native);
}
private static IMongoCollection<BsonDocument> Raw<T>(IMongoCollection<T> c)
    => c.Database.GetCollection<BsonDocument>(c.CollectionNamespace.CollectionName);
```

### T1 Primitive collections (blocks 5 WhereTests)
Failing: WhereTests.Where_string_array_any/_count/_length, Where_string_list_any/_count (NativeOnly).
New (data with empty, missing, null, 1, 3 elements - the exists-form must agree with LINQ on all):
```csharp
public class TagRow { public ObjectId Id { get; set; } public string Name { get; set; } = ""; public string[] Tags { get; set; } = []; public List<int> Nums { get; set; } = []; }

private IMongoCollection<TagRow> SeedTags()
{
    var c = database.CreateCollection<TagRow>();
    Raw(c).InsertMany(
    [
        new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "empty" }, { "Tags", new BsonArray() }, { "Nums", new BsonArray() } },
        new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "missing" } },
        new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "null" }, { "Tags", BsonNull.Value }, { "Nums", BsonNull.Value } },
        new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "one" }, { "Tags", new BsonArray { "a" } }, { "Nums", new BsonArray { 1 } } },
        new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "three" }, { "Tags", new BsonArray { "a", "b", "c" } }, { "Nums", new BsonArray { 1, 2, 3 } } },
    ]);
    return c;
}
```
(NB: reading "missing"/"null" docs into a non-nullable `string[]` property: use `Select(x => x.Name)` projection only, so no materialization of Tags; if the
driver path throws on those rows materializing, project names only - it does.)
Facts (each `AssertNativeParity(SeedTags(), q => q.Where(<pred>).OrderBy(x=>x.Name).Select(x=>x.Name).ToList(), expected)`):
- `x.Tags.Any()` -> ["one","three"]; `!x.Tags.Any()` -> ["empty","missing","null"]; `x.Nums.Any()`
- `x.Tags.Count() == 3` -> ["three"]; `x.Tags.Length == 0` -> ["empty","missing","null"]; `x.Tags.Length > 0`; `x.Tags.Length != 1`; `x.Nums.Count == 1`; `x.Nums.Count > 1`
- parameterized threshold: `var n = 3; x.Tags.Length == n` (renders `$expr $size $ifNull`)
- combined `x.Tags.Any() && x.Tags.Contains("a")`
- negative control (stays unchanged): `x.Name.Length == 3` (string.Length must not match the new ArrayLength/Length arm)
- Declines cleanly: a value-converted collection (`HasConversion` to a delimited string) -> `NativeModeAssert.DeclinesCleanly`.
Unit (UnitTests/Query/NativeTranslation, translator-level, mirror existing `NativeCardinality*`/translator tests): `Any()` / `.Count()` / `.Length` over a `List<string>`/`string[]`
property produce `MongoBinaryExpression{Left: MongoSizeExpression{FieldName:"Tags", NullSafe:true}}`; over an `OwnsMany` navigation unchanged.

### T2 Dictionaries (blocks 11 WhereDictionaryTests + the Update test)
Failing: the 11 listed WhereDictionaryTests, UpdateEntityTests.Update_dictionary_property_with_struct_value (NativeOnly).
Step 0 unit assertion (guards design risk 1):
```csharp
[Theory] [InlineData(typeof(Dictionary<string,int>))] [InlineData(typeof(IDictionary<string,int>))] [InlineData(typeof(IReadOnlyDictionary<string,int>))] [InlineData(typeof(ReadOnlyDictionary<string,int>))]
public void Dictionary_property_serializer_is_a_document_dictionary_serializer(Type t) { /* build model with a property of t, GetPropertySerializationInfo(prop).Serializer is IBsonDictionarySerializer { DictionaryRepresentation: DictionaryRepresentation.Document } */ }
```
NEW tests (functional, `AssertNativeParity`, entity `{ ObjectId _id; Dictionary<string,string> D; Dictionary<string,int> I; Dictionary<string,MonetaryAmount> M }`):
- `!x.D.ContainsKey("a")` (exists:false); `x.D.ContainsKey("a") && x.D["a"] != null`; `x.D.ContainsKey("a") || x.I["n"] > 5`
- parameter value: `var v = "value2"; x.D["key2"] == v`; `string? nv = null; x.D["key2"] == nv`; `int lim = 72; x.I["key1"] >= lim`; mirrored `72 <= x.I["key1"]`; `x.I["k"].Equals(5)`
- negation of relational: `!(x.I["key1"] > 50)` (renders `$not`; missing key row included - driver parity)
- missing key rows: `x.I["nokey"] == 0`, `x.I["nokey"] > 0` must equal driver
- struct param (the Update test shape) both `==` and `.Equals`; and `x.M["Total"] != param`
- declines cleanly: key with dot `x.D["a.b"] == "x"`, empty key, parameterized key `x.D[keyVar] == "x"`, relational over decimal/DateTime value dictionary, `x.D["k"] == x.Other` (field-to-field) -> DeclinesCleanly (NativeOnly throws NativeTranslationNotSupportedException, Native == DriverLinq)
- order check: assert MQL via the logging spy used in NativeEntityEqualityTests (`CreateContextWithLogging`) that the match stage equals the expected documents above (MQL shape cannot prove native; this pins the shape only).
Unit: renderer tests in MongoQueryLanguageRendererTests for the two nodes (incl. negated flip, `$not` of a relational dictionary comparison), node-coverage matrix rows.

### T3 Composite-key entity list Contains (blocks CompositeKeyQueryTests.Should_match_entity_array_contains)
NEW (extend CompositeKeyQueryTests entity; run `AssertNativeParity` over a SingleEntityDbContext with `HasKey(Key1,Key2)`, seed (one,1),(two,2),(two,3)):
- param array `var wanted = new[]{ e1, e3 }; Contains(e)` -> 2; same with `List<Entity>`; inline `new[]{ one }` (constant on EF9+, NewArrayInit on EF8)
- empty list -> 0 rows; `!wanted.Contains(e)` with empty -> all rows; `!Contains` non-empty
- list containing null element(s); list with duplicates
- `wanted.Contains(e) && e.Key2 > 1` (CombineAnd with `$or`)
- composite key with Guid + enum components (serializer coverage)
- single-key `Contains` MQL unchanged (regression pin).
Unit: `CompositeKeyClauses.Build` for [], [null], [a,b]; and placeholder round trip via MongoPipelineFactory.Build with two executions of different lengths from one compiled template (the whole reason the placeholder exists).

### T4 F7 (default-mode loud failure; fix first - smallest)
```csharp
public class NullParamTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Level { Low = 1, High = 10 }
    public class Row
    {
        public ObjectId Id { get; set; } public string Title { get; set; } = "";
        public int Rank { get; set; } public Level Level { get; set; } public Guid Gid { get; set; }
        public DateTime When { get; set; } public bool Flag { get; set; } public long Big { get; set; }
    }
    private IMongoCollection<Row> Seed()   // c is ill-formed on purpose (Rank/Gid/When/Flag missing): same rows as the review probe
    {
        var c = database.CreateCollection<Row>();
        var g = Guid.NewGuid(); var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Raw(c).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "a" }, { "Rank", 1 }, { "Level", 2 }, { "Gid", new BsonBinaryData(g, GuidRepresentation.Standard) }, { "When", when }, { "Flag", true }, { "Big", 1L } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "b" }, { "Rank", 2 }, { "Level", 10 }, { "Gid", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) }, { "When", when.AddDays(1) }, { "Flag", false }, { "Big", 2L } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "c" }, { "Level", 1 }, { "Big", 3L } },
        ]);
        return c;
    }
    private List<string> Titles(IQueryable<Row> q) => q.OrderBy(x => x.Title).Select(x => x.Title).ToList();

    [Fact] public void Rank_equals_null_int_parameter()      { int? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Rank == p)), expected: ["c"]); }
    [Fact] public void Rank_not_equals_null_int_parameter()  { int? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Rank != p)), expected: ["a", "b"]); }
    [Fact] public void Level_equals_null_enum_parameter()    { Level? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Level == p)), expected: []); }
    [Fact] public void Guid_equals_null_guid_parameter()     { Guid? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Gid == p))); }
    [Fact] public void Guid_not_equals_null_guid_parameter() { Guid? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Gid != p))); }
    [Fact] public void DateTime_equals_null_parameter()      { DateTime? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.When == p))); }
    [Fact] public void Bool_equals_null_parameter()          { bool? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Flag == p))); }
    [Fact] public void Long_equals_null_parameter()          { long? p = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Big == p)), expected: []); }
    [Fact] public void Non_null_value_still_matches()        { int? p = 2; AssertNativeParity(Seed(), q => Titles(q.Where(x => x.Rank == p)), expected: ["b"]); }
    [Fact] public void Null_parameter_in_disjunction_with_range()  // the shape that already works; regression pin
    { int? min = null; AssertNativeParity(Seed(), q => Titles(q.Where(x => min == null || x.Rank >= min)), expected: ["a", "b", "c"]); }
    // Contains with a null element
    [Fact] public void Int_list_with_null_parameter()   { var ids = new List<int?> { 1, null }; AssertNativeParity(Seed(), q => Titles(q.Where(x => ids.Contains(x.Rank))), expected: ["a", "c"]); }
    [Fact] public void Not_int_list_with_null_parameter() { var ids = new List<int?> { 1, null }; AssertNativeParity(Seed(), q => Titles(q.Where(x => !ids.Contains(x.Rank))), expected: ["b"]); }
    [Fact] public void Int_array_with_null_constant()   => AssertNativeParity(Seed(), q => Titles(q.Where(x => new int?[] { 1, null }.Contains(x.Rank))), expected: ["a", "c"]);
    [Fact] public void Guid_list_with_null_parameter()  { var ids = new List<Guid?> { Guid.Empty, null }; AssertNativeParity(Seed(), q => Titles(q.Where(x => ids.Contains(x.Gid)))); }
    [Fact] public void Enum_list_with_null_parameter()  { var ls = new List<Level?> { Level.Low, null }; AssertNativeParity(Seed(), q => Titles(q.Where(x => ls.Contains(x.Level)))); }
    [Fact] public void DateTime_list_with_null_constant() => AssertNativeParity(Seed(), q => Titles(q.Where(x => new DateTime?[] { null }.Contains(x.When))));
}
```
Also run each under `MongoQueryMode.Native` (default) asserting no throw - F7 was loud in default mode. Unit: MongoValueRendererTests - `RenderValue(new MongoConstantExpression(null, int property))` => BsonNull; placeholder round-trip `Build` with null for an int property => BsonNull.
Expected MQL: `{Rank: null}`, `{Rank: {$ne: null}}`, `{Rank: {$in: [1, null]}}`, `{Level: null}`.
(The first three "expected" values are driver-main parity on the ill-formed row c, per the probe; with c well-formed they are [] / [a,b,c]. Assert parity primarily.)

### T5 F9
```csharp
public class Doc { public ObjectId Id { get; set; } public string Code { get; set; } = ""; public Guid Gid { get; set; } public int N { get; set; } public Level Lvl { get; set; } public DateTime When { get; set; } public DateTimeOffset At { get; set; } }
[Fact] public void Tuple_with_guid_equals_constant_tuple()  { var g = Guid.NewGuid(); /* seed */ AssertNativeParity(seed, q => q.Where(d => new Tuple<Guid,int>(d.Gid, d.N) == new Tuple<Guid,int>(g, 1)).Select(d => d.Code).ToList(), expected: ["abc"]); }
[Fact] public void Tuple_with_guid_not_equals()
[Fact] public void ValueTuple_with_guid_equals()          // (d.Gid, d.N) == (g, 1)  (ValueTuple, funcletized shape)
[Fact] public void Tuple_with_enum_element()              // new Tuple<Level,int>(d.Lvl, d.N) == Tuple.Create(Level.High, 1)
[Fact] public void Tuple_with_datetime_element()          // DateTime is BsonValue.Create-safe: MQL must be byte-identical to today (pin via MQL spy)
[Fact] public void Tuple_with_datetimeoffset_element()    // unmappable: either native (via property serializer) or DeclinesCleanly - assert parity either way
[Fact] public void Tuple_guid_vs_computed_counterpart_declines_cleanly()   // new Tuple<Guid,int>(g, d.N + 0) == ... -> NativeTranslationNotSupportedException under NativeOnly, Native == DriverLinq
[Fact] public void Tuple_guid_parameter_whole_tuple()     // compiled-query / parameter tuple: Func-captured tuple variable
```
Unit: `MongoValueRenderer.RenderValue(new MongoConstantExpression(Guid.NewGuid(), null), ...)` throws NativeTranslationNotSupportedException (not ArgumentException).
Expected MQL: `{ $expr: { $eq: [ ["$Gid","$N"], [ BinData(4, ...), 1 ] ] } }`.

### T6 F5
```csharp
public class Doc5 { [BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ""; [BsonRepresentation(BsonType.String)] public string Tag { get; set; } = ""; public string Plain { get; set; } = ""; }
[Fact] public void StartsWith_on_objectid_represented_string_declines_and_matches_driver()
{
    var prefix = oid.ToString()[..10];
    Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly, q => q.Where(d => d.Id.StartsWith(prefix))...));
    var driver = Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.DriverLinq, ...));
    var native = Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.Native, ...));
    Assert.Equal(driver.GetType(), native.GetType());          // both MongoCommandException ($indexOfCP ... found: objectId) - never a silent []
}
// same for EndsWith, Contains, StartsWith(constant), field-to-field, `EF.Functions.Like(d.Id, ...)`, string.Equals(d.Id, x, OrdinalIgnoreCase), Regex.IsMatch(d.Id, ..), ToLower() == (case-mapping arm)
[Fact] public void StartsWith_on_string_represented_as_String_stays_native()  // [BsonRepresentation(BsonType.String)] Tag, and Plain: NativeAndParity
```
Unit: `IsRegexSearchableString` truth table (no rep, String, ObjectId, Int32/non-string clr).

### Order and sizing
T4 (S, 1 helper + 4 call sites) -> T5 (S-M) -> T6 (S) -> T1 (M; 3 edits + 1 resolver) -> T3 (M-L; new node, placeholder tuple growth, builder) -> T2 (L; 2 new nodes, parameter ExplicitSerializer, 6 dispatcher sites + coverage matrix).
Each new MongoExpression type forces MongoExpressionNodeCoverageTests matrix updates (by design).

## 4. Pinning / harness note
The 17 existing tests pass in default mode only because the driver fallback runs; they turn green under NativeOnly only after T1-T3. To keep them honest add
the NativeOnly twin (T1/T2/T3 `AssertNativeParity` facts) rather than editing the originals; originals keep exercising whatever mode the harness sets.

## 5. Escalation flags (per query-reviewer rules)
- F5: silent-`[]` becomes a loud failure (NativeOnly translation failure; Native = driver server error as on main). Owner sign-off recommended.
- New nodes / placeholder tuple change touch the render pipeline shape (not a visitor-pipeline refactor); MQL for previously-working queries is unchanged by every item above (T5 rebinding is limited to types that threw).
- No public API, no new IQueryable extension methods, no annotation writes, no `#if` needed (IBsonDictionarySerializer/IBsonArraySerializer are driver APIs common to all three configs; verify EF8 build compiles `GetBinaryVectorDataType` usage - it is already used in BsonSerializerFactory under all configs).
- Wrong-on-main to record for main: `Rank == nullParam` returns docs with Rank MISSING (c) and `!=` drops them (non-nullable property cannot be null; C# answer [] / all); `Code.StartsWith` on a value-converted string matches nothing; dictionary `d["k"]` on a missing key is treated as null (C# throws KeyNotFound).
