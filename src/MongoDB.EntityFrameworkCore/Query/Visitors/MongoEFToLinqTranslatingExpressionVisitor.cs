/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor : System.Linq.Expressions.ExpressionVisitor
{
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Mql.Field) && m.GetParameters().Length == 3);

    // DateTimeOffset members whose translation is rewritten below to work around the driver not
    // implementing IBsonDocumentSerializer on DateTimeOffsetSerializer (CSHARP-5296 / EF-218).
    private static readonly HashSet<string> DateTimeOffsetComponentMembers =
    [
        nameof(DateTimeOffset.DateTime),
        nameof(DateTimeOffset.LocalDateTime),
        nameof(DateTimeOffset.UtcDateTime),
        nameof(DateTimeOffset.Date),
        nameof(DateTimeOffset.TimeOfDay),
        nameof(DateTimeOffset.Year),
        nameof(DateTimeOffset.Month),
        nameof(DateTimeOffset.Day),
        nameof(DateTimeOffset.Hour),
        nameof(DateTimeOffset.Minute),
        nameof(DateTimeOffset.Second),
        nameof(DateTimeOffset.Millisecond),
        nameof(DateTimeOffset.DayOfWeek),
        nameof(DateTimeOffset.DayOfYear)
    ];

    private static readonly MethodInfo DateTimeAddMinutesMethodInfo =
        typeof(DateTime).GetMethod(nameof(DateTime.AddMinutes), [typeof(double)])!;

    private readonly QueryContext _queryContext;
    private readonly Expression _source;
    private readonly BsonSerializerFactory _bsonSerializerFactory;
    private readonly IReadOnlyList<LookupExpression> _pendingLookups;
    private readonly Dictionary<IEntityType, Expression> _innerSources;
    private EntityQueryRootExpression? _foundEntityQueryRootExpression;

    // When forceUnwind lookups stand in for a Join chain that StripJoinForLookup did not actually remove,
    // the Join survives in the translated tree and is rendered natively by the driver; emitting the
    // forceUnwind $lookup/$unwind stages on top is then both redundant and (for scalar terminals) invalid.
    // Set false in that case so AppendLookupStages skips them. Defaults true (lookups are appended).
    private bool _appendForceUnwindLookups = true;

    // Lookups this EXECUTION must emit somewhere BELOW the tail of the pipeline, because
    // StripJoinForLookup reattached user-composed operators above them that read their output fields. Held
    // here, not written back onto the shared (compile-time) LookupExpression objects. See IsInjectedEarly.
    private readonly HashSet<LookupExpression> _injectedEarlyLookups = [];

    // Where each of those groups is emitted, keyed by REFERENCE on the node it is emitted immediately
    // ABOVE. Each entry is one-shot: Visit removes it before recursing, so it cannot re-enter itself.
    //
    // Usually there is a single entry, keyed on the join chain's base source - the expression the innermost
    // join was applied to, i.e. everything the user composed BELOW the joins. When an operator is
    // INTERLEAVED BETWEEN two joins there is one entry per reattachment boundary instead, so a Skip/Take
    // written between two joins is emitted between their two $lookup stages rather than above both of them.
    private readonly Dictionary<Expression, List<LookupExpression>> _injectAboveNodeLookups =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Verifies that every node recorded in <see cref="_injectAboveNodeLookups"/> was actually reached
    /// during the translation walk.
    /// </summary>
    /// <remarks>
    /// If one was not, the lookups recorded against it were never emitted — and because
    /// <c>IsInjectedEarly</c> also excludes them from <c>AppendLookupStages</c>, the join's
    /// <c>$lookup</c>/<c>$unwind</c> pair would vanish from the pipeline entirely and the query would
    /// silently return unjoined rows. That should be unreachable: the nodes are captured from the tree this
    /// same visitor is about to walk. This is cheap insurance that the failure is loud if it ever is
    /// reachable, since the symptom would otherwise be wrong data rather than an error.
    /// </remarks>
    private void AssertAllEarlyLookupInjectionsFired()
    {
        if (_injectAboveNodeLookups.Count > 0)
        {
            throw new InvalidOperationException(
                "A join node recorded for early $lookup injection was never reached while "
                + "translating the query. This is an internal error in the MongoDB EF Core provider; "
                + "please report it with the query that produced it.");
        }
    }

    internal MongoEFToLinqTranslatingExpressionVisitor(
        QueryContext queryContext,
        Expression source,
        BsonSerializerFactory bsonSerializerFactory,
        IReadOnlyList<LookupExpression>? pendingLookups = null,
        Dictionary<IEntityType, Expression>? innerSources = null)
    {
        _queryContext = queryContext;
        _source = source;
        _bsonSerializerFactory = bsonSerializerFactory;
        _pendingLookups = pendingLookups ?? Array.Empty<LookupExpression>();
        _innerSources = innerSources ?? new Dictionary<IEntityType, Expression>();
    }

    public Dictionary<string, object> AdditionalState { get; } = new();

    /// <summary>
    /// Translate a projected query (anonymous types with entity members).
    /// Strips joins for lookup-based queries and appends any pending $lookup stages.
    /// </summary>
    public Expression TranslateProjected(Expression? efQueryExpression)
    {
        GuardAgainstMultiBranchNavigationCount(efQueryExpression);

        if (efQueryExpression == null)
        {
            return AppendLookupStages(_source);
        }

        // For explicit Join queries with pending lookups, strip the join and use $lookup instead.
        // Otherwise rewrite any Include-generated LeftJoin into Queryable.Join + LeftJoinResult so the
        // driver's pipeline translator (which has no LeftJoin translator) accepts it.
        // NOTE: unlike Translate below, this method does NOT call GuardAgainstUnstrippableForceUnwindJoin
        // when the strip fails. That guard covers the WHOLE-ENTITY path (Translate, below); the
        // MIXED-projection path reached from here is knowingly ungated, not immune by construction. A
        // pure/PLAIN projected query's member accesses are translated by the driver's own LINQ v3 provider
        // directly against whatever document shape the actually-executed pipeline produces, so it genuinely
        // has no pre-built shape commitment to mismatch — but a MIXED projection (one containing an entity
        // reference LINQ v3 cannot translate) reached through TranslateProjected also reads
        // MongoQueryExpression.UsesDriverJoinFields, and that shaper IS pre-built the same way the
        // whole-entity one is. Guarding this call site the same way Translate is guarded would turn a
        // correct, pre-existing fallback (a nested-aggregate GroupBy shape that ReattachComposedOperator
        // cannot rebuild, relying on the driver rendering the surviving joins natively despite a pending
        // ForceUnwind lookup) into a spurious InvalidOperationException. So a mixed-projection shape
        // sharing this exact vulnerability, should one ever surface, is not yet covered by any guard here —
        // a known gap, not a verified-safe one.
        Expression expressionToTranslate;
        if (_pendingLookups.Count > 0)
        {
            var stripped = StripJoinForLookup(efQueryExpression);
            GuardAgainstUnstrippableMultiJoin(stripped, efQueryExpression, isEntityShaped: false);
            expressionToTranslate = stripped ?? efQueryExpression;
            // forceUnwind lookups stand in for an explicit Join chain that StripJoinForLookup removed.
            // When the strip did not fire (e.g. the join is buried under OrderBy/terminal operators the
            // stripper doesn't recurse through), the Join survives in the translated tree and the driver
            // renders it natively - appending the forceUnwind $lookup/$unwind stages on top would both be
            // redundant and, for a scalar-cardinality terminal (Any/All/Count), try to wrap the scalar
            // result in AppendStage. Skip them in that case.
            _appendForceUnwindLookups = stripped != null;
        }
        else
        {
            expressionToTranslate = RewriteLeftJoins(efQueryExpression, convertExplicitJoins: false);
        }

        var query = Visit(expressionToTranslate)!;
        AssertAllEarlyLookupInjectionsFired();
        return AppendLookupStages(query);
    }

    /// <param name="efQueryExpression">The captured EF method chain to rewrite as driver-LINQ.</param>
    /// <param name="resultCardinality">The query's result cardinality.</param>
    /// <param name="guardUnstrippableForceUnwindJoin">
    /// Whether a failed <see cref="StripJoinForLookup"/> with a <c>ForceUnwind</c> lookup pending should fail
    /// translation (see <see cref="GuardAgainstUnstrippableForceUnwindJoin"/>). <see langword="true"/> for the
    /// READ path, whose whole-entity shaper is pre-built assuming the flat <c>_lookup_&lt;Nav&gt;</c> shape and
    /// so genuinely can mismatch. <see langword="false"/> for the BULK path
    /// (<c>MongoShapedQueryCompilingExpressionVisitor.BuildIdDocumentQuery</c>, which reuses this same
    /// translate call to fetch raw <c>BsonDocument</c>s for <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>): there
    /// is no shaper on that path, so the guard's premise doesn't hold and it would be a pure false-positive
    /// throw surface there.
    /// </param>
    public MethodCallExpression Translate(
        Expression? efQueryExpression,
        ResultCardinality resultCardinality,
        bool guardUnstrippableForceUnwindJoin = true)
    {
        GuardAgainstMultiBranchNavigationCount(efQueryExpression);

        if (efQueryExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            var source = AppendLookupStages(_source);
            return ApplyAsSerializer(source, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        // For explicit Join queries with pending lookups (forceUnwind), strip the join
        // and let AppendLookupStages handle it. For Include LeftJoins, strip the outer Select
        // and let the driver handle the LeftJoin natively.
        var expressionToTranslate = efQueryExpression;
        if (_pendingLookups.Any(l => l.ForceUnwind))
        {
            var stripped = StripJoinForLookup(efQueryExpression);
            if (guardUnstrippableForceUnwindJoin)
            {
                GuardAgainstUnstrippableForceUnwindJoin(stripped, efQueryExpression);
            }
            expressionToTranslate = stripped ?? efQueryExpression;
            // See TranslateProjected: only emit the forceUnwind lookups when they actually replaced a
            // stripped Join chain. If the strip did not fire the Join survives and the driver renders it.
            _appendForceUnwindLookups = stripped != null;
        }
        else if (_innerSources.Count > 0)
        {
            expressionToTranslate = StripOuterSelectForJoin(efQueryExpression) ?? efQueryExpression;
        }

        var query = (MethodCallExpression)Visit(expressionToTranslate)!;
        AssertAllEarlyLookupInjectionsFired();

        if (resultCardinality == ResultCardinality.Enumerable)
        {
            var withLookups = AppendLookupStages(query);
            return ApplyAsSerializer(withLookups, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        var withLookupsSingle = AppendLookupStages(query.Arguments[0]);
        var documentQueryableSource = ApplyAsSerializer(withLookupsSingle, BsonDocumentSerializer.Instance, typeof(BsonDocument));

        return Expression.Call(
            null,
            query.Method.GetGenericMethodDefinition().MakeGenericMethod(typeof(BsonDocument)),
            documentQueryableSource);
    }

    private static MethodCallExpression ApplyAsSerializer(
        Expression query,
        IBsonSerializer resultSerializer,
        Type resultType)
    {
        var asMethodInfo = AsMethodInfo.MakeGenericMethod(query.Type.GenericTypeArguments[0], resultType);
        var serializerExpression = Expression.Constant(resultSerializer, resultSerializer.GetType());

        return Expression.Call(
            null,
            asMethodInfo,
            query,
            serializerExpression
        );
    }

    private static bool IsAsQueryableMethod(MethodInfo method)
        => method.Name == "AsQueryable" && method.DeclaringType == typeof(Queryable);

    public override Expression? Visit(Expression? expression)
    {
        // The join-replacing $lookup/$unwind stages go here: directly above the node StripJoinForLookup
        // recorded them against. That is the base source the innermost join was applied to, so anything the
        // user composed BELOW the joins (notably Skip/Take/Distinct, whose result depends on how many rows
        // reach them) still runs first, while the operators StripJoinForLookup reattached ABOVE the joins
        // see the flattened lookup fields. When an operator is INTERLEAVED BETWEEN two joins there is one
        // recorded node per reattachment boundary, so each join's lookup lands on the correct side of the
        // interleaved operator.
        if (expression != null && _injectAboveNodeLookups.Remove(expression, out var lookupsHere))
        {
            // The entry was removed above, so the recursive Visit cannot re-enter here (one-shot).
            var translatedNode = Visit(expression)!;
            return EmitLookupStages(translatedNode, lookupsHere);
        }

        switch (expression)
        {
            // Replace materialization collection expression with the actual nav property in order for Mql.Exists etc. to work.
            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                var subQuery = Visit(materializeCollectionNavigationExpression.Subquery);
                if (subQuery is MethodCallExpression mce && IsAsQueryableMethod(mce.Method))
                {
                    return Visit(mce.Arguments[0]);
                }

                return subQuery;

#if EF8 || EF9
            // Replace the QueryContext parameter values with constant values for this execution.
            case ParameterExpression parameterExpression:
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    if (_queryContext.ParameterValues.TryGetValue(parameterExpression.Name, out var value))
                    {
                        return Expression.Constant(value).ConvertIfRequired(expression.Type);
                    }
                }

                break;
#else
            case QueryParameterExpression queryParameterExpression:
                return Expression.Constant(_queryContext.Parameters[queryParameterExpression.Name]).ConvertIfRequired(expression.Type);
#endif

            // Wrap OfType<T> with As(serializer) to re-attach the custom serializer in LINQ3
            case MethodCallExpression
                {
                    Method.Name: nameof(Queryable.OfType), Method.IsGenericMethod: true, Arguments.Count: 1
                } ofTypeCall
                when ofTypeCall.Method.DeclaringType == typeof(Queryable):
                var resultType = ofTypeCall.Method.GetGenericArguments()[0];
                var resultEntityType = _queryContext.Context.Model.FindEntityType(resultType)
                                       ?? throw new NotSupportedException($"OfType type '{resultType.ShortDisplayName()
                                       }' does not map to an entity type.");
                var resultSerializer = _bsonSerializerFactory.GetEntitySerializer(resultEntityType);
                var translatedOfTypeCall = Expression.Call(null, ofTypeCall.Method, Visit(ofTypeCall.Arguments[0])!);
                return ApplyAsSerializer(translatedOfTypeCall, resultSerializer, resultType);

            // Rewrite instance entity.Equals(other) to key comparisons (e.g. composite key entities)
            case MethodCallExpression { Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1 } instanceEqualsCall:
                var instanceRewrite = TryRewriteEntityEquality(
                    instanceEqualsCall.Object!,
                    instanceEqualsCall.Arguments[0],
                    ExpressionType.Equal);
                if (instanceRewrite != null)
                    return instanceRewrite;

                // Plain C# returns false here (e.g. ((int?)1).Equals((ulong)2)); left untouched, the driver's
                // Equals translator instead tries to serialize the RHS with the LHS's serializer and throws
                // (EF-221). Fold to the correct constant instead.
                if (IsAlwaysFalseAcrossTypeMismatch(instanceEqualsCall.Object!, instanceEqualsCall.Arguments[0]))
                    return Expression.Constant(false);

                break;

            // Replace object.Equals(Property(p, "propName"), ConstantExpression) elements generated by EF's Find.
            case MethodCallExpression { Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2 } methodCallExpression:
                // Check for entity equality before visiting (composite keys use Equals instead of ==)
                var entityRewrite = TryRewriteEntityEquality(
                    methodCallExpression.Arguments[0].RemoveObjectConvert(),
                    methodCallExpression.Arguments[1].RemoveObjectConvert(),
                    ExpressionType.Equal);
                if (entityRewrite != null)
                    return entityRewrite;

                var left = Visit(methodCallExpression.Arguments[0].RemoveObjectConvert())!;
                var right = Visit(methodCallExpression.Arguments[1].RemoveObjectConvert())!;
                var method = methodCallExpression.Method;

                if (left.Type == right.Type)
                {
                    return Expression.Equal(left.RemoveObjectConvert(), right.RemoveObjectConvert());
                }

                // Same mismatched-type fold as the instance-call case above (EF-221): object.Equals(a, b)
                // with genuinely incompatible simple types (e.g. int vs ulong) is always false in plain C#.
                if (AreMismatchedExactEqualityTypes(
                        Nullable.GetUnderlyingType(left.Type) ?? left.Type,
                        Nullable.GetUnderlyingType(right.Type) ?? right.Type))
                    return Expression.Constant(false);

                var parameters = method.GetParameters();
                left = left.ConvertIfRequired(parameters[0].ParameterType);
                right = right.ConvertIfRequired(parameters[1].ParameterType);
                return Expression.Call(null, method, left, right);

            // Replace EF-generated Property(p, "propName") with Property(p.propName) or Mql.Field(p, "propName", serializer)
            case MethodCallExpression methodCallExpression
                when methodCallExpression.Method.IsEFPropertyMethod()
                     && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression:
                var source = Visit(methodCallExpression.Arguments[0])
                             ?? throw new InvalidOperationException("Unsupported source to EF.Property expression.");

                var propertyName = propertyNameExpression.GetConstantValue<string>();
                var entityType = _queryContext.Context.Model.FindEntityType(source.Type);
                if (entityType != null)
                {
                    // Try an EF property
                    var efProperty = entityType.FindProperty(propertyName);
                    if (efProperty != null)
                    {
                        var doc = source;

                        // Composite keys need to go via the _id document
                        var isCompositeKeyAccess = efProperty.IsPrimaryKey() && entityType.FindPrimaryKey()?.Properties.Count > 1;
                        if (isCompositeKeyAccess)
                        {
                            var mqlFieldDoc = MqlFieldMethodInfo.MakeGenericMethod(source.Type, typeof(BsonValue));
                            doc = Expression.Call(null, mqlFieldDoc, source, Expression.Constant("_id"),
                                Expression.Constant(BsonValueSerializer.Instance));
                        }

                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(doc.Type, efProperty.ClrType);
                        var serializer = BsonSerializerFactory.CreateTypeSerializer(efProperty);
                        var callExpression = Expression.Call(null, mqlField, doc,
                            Expression.Constant(efProperty.GetElementName()),
                            Expression.Constant(serializer));
                        return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                    }

                    // Try an EF navigation if no property
                    var efNavigation = entityType.FindNavigation(propertyName);
                    if (efNavigation != null)
                    {
                        var elementName = efNavigation.TargetEntityType.GetContainingElementName();
                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, efNavigation.ClrType);
                        var serializer = _bsonSerializerFactory.GetNavigationSerializer(efNavigation);
                        var callExpression = Expression.Call(null, mqlField, source,
                            Expression.Constant(elementName),
                            Expression.Constant(serializer));
                        return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                    }
                }

                // Try CLR property
                // This should not really be required but is kept here for backwards compatibility with any edge cases.
                var clrProperty = source.Type.GetProperties().FirstOrDefault(p => p.Name == propertyName);
                if (clrProperty != null)
                {
                    var propertyExpression = Expression.Property(source, clrProperty);
                    return propertyExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                }

                var defaultSerializer = BsonSerializer.LookupSerializer(methodCallExpression.Type);
                if (defaultSerializer != null)
                {
                    var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, methodCallExpression.Type);
                    var callExpression = Expression.Call(null, mqlField, source,
                        propertyNameExpression,
                        Expression.Constant(defaultSerializer));
                    return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                }

                return VisitMethodCall(methodCallExpression);

            // Replace plain entity-property/navigation member access in a JOIN result selector (e.g. a
            // user's verbatim `(c, o) => new { c.ContactName, o.OrderID }`) with Mql.Field reads that honour
            // the entity's BSON element names. Single-collection queries don't need this - the root source's
            // .As(EntitySerializer) already resolves member access correctly - but in a join the driver
            // synthesizes a result-type serializer for the _outer/_inner shape that does NOT carry EF's
            // entity serializers, so `o.OrderID` would resolve to a literal "OrderID" field instead of the
            // PK's "_id". Gated on _innerSources so it only affects join queries.
            case MemberExpression memberExpression
                when _innerSources.Count > 0
                     && memberExpression.Expression != null
                     && _queryContext.Context.Model.FindEntityType(memberExpression.Expression.Type) is { } memberEntityType
                     && (memberEntityType.FindProperty(memberExpression.Member.Name) != null
                         || memberEntityType.FindNavigation(memberExpression.Member.Name) != null):
                {
                    var memberSource = Visit(memberExpression.Expression)
                                       ?? throw new InvalidOperationException("Unsupported source to member access expression.");

                    var memberProperty = memberEntityType.FindProperty(memberExpression.Member.Name);
                    if (memberProperty != null)
                    {
                        var doc = memberSource;

                        // Composite keys need to go via the _id document
                        var isCompositeKeyAccess = memberProperty.IsPrimaryKey()
                                                   && memberEntityType.FindPrimaryKey()?.Properties.Count > 1;
                        if (isCompositeKeyAccess)
                        {
                            var mqlFieldDoc = MqlFieldMethodInfo.MakeGenericMethod(memberSource.Type, typeof(BsonValue));
                            doc = Expression.Call(null, mqlFieldDoc, memberSource, Expression.Constant("_id"),
                                Expression.Constant(BsonValueSerializer.Instance));
                        }

                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(doc.Type, memberProperty.ClrType);
                        var serializer = BsonSerializerFactory.CreateTypeSerializer(memberProperty);
                        var callExpression = Expression.Call(null, mqlField, doc,
                            Expression.Constant(memberProperty.GetElementName()),
                            Expression.Constant(serializer));
                        return callExpression.ConvertIfRequired(memberExpression.Type);
                    }

                    var memberNavigation = memberEntityType.FindNavigation(memberExpression.Member.Name)!;
                    var navElementName = memberNavigation.TargetEntityType.GetContainingElementName();
                    var navMqlField = MqlFieldMethodInfo.MakeGenericMethod(memberSource.Type, memberNavigation.ClrType);
                    var navSerializer = _bsonSerializerFactory.GetNavigationSerializer(memberNavigation);
                    var navCall = Expression.Call(null, navMqlField, memberSource,
                        Expression.Constant(navElementName),
                        Expression.Constant(navSerializer));
                    return navCall.ConvertIfRequired(memberExpression.Type);
                }

            // Rewrite DateTimeOffset.DateTime/.Year/.Date/etc member access into a UTC-field +
            // Offset-field reconstruction that the driver's own DateTime member translator can
            // then handle natively. Works around CSHARP-5296: the driver's DateTimeOffsetSerializer
            // does not implement IBsonDocumentSerializer, so member access directly off a
            // DateTimeOffset-typed expression fails in the driver's LINQ translator. See EF-218.
            case MemberExpression { Expression: { } dateTimeOffsetSource } dateTimeOffsetMember
                when dateTimeOffsetSource.Type == typeof(DateTimeOffset)
                     && DateTimeOffsetComponentMembers.Contains(dateTimeOffsetMember.Member.Name):

                if (TryResolveDateTimeOffsetElementAccess(dateTimeOffsetSource, out var dtoDocSource, out var dtoElementName))
                {
                    var dtoDocMethod = MqlFieldMethodInfo.MakeGenericMethod(dtoDocSource.Type, typeof(BsonValue));
                    var dtoDoc = Expression.Call(null, dtoDocMethod, dtoDocSource,
                        Expression.Constant(dtoElementName), Expression.Constant(BsonValueSerializer.Instance));

                    var utcFieldMethod = MqlFieldMethodInfo.MakeGenericMethod(typeof(BsonValue), typeof(DateTime));
                    var utcField = Expression.Call(null, utcFieldMethod, dtoDoc,
                        Expression.Constant("DateTime"), Expression.Constant(DateTimeSerializer.Instance));

                    if (dateTimeOffsetMember.Member.Name == nameof(DateTimeOffset.UtcDateTime))
                    {
                        return utcField;
                    }

                    var offsetFieldMethod = MqlFieldMethodInfo.MakeGenericMethod(typeof(BsonValue), typeof(int));
                    var offsetField = Expression.Call(null, offsetFieldMethod, dtoDoc,
                        Expression.Constant("Offset"), Expression.Constant(Int32Serializer.Instance));

                    var localDateTime = Expression.Call(utcField, DateTimeAddMinutesMethodInfo,
                        Expression.Convert(offsetField, typeof(double)));

                    // NOTE: .LocalDateTime is deliberately translated identically to .DateTime (both use the
                    // value's stored Offset) — true .NET semantics need the *executing machine's* time zone,
                    // which isn't available in server-side aggregation. Also: the "DateTime" sub-field is
                    // millisecond-truncated (sub-ms ticks lost vs. client-side eval via "Ticks"), and the
                    // reconstructed Kind is always Utc, not Unspecified/Local — don't call .ToLocalTime() on it.
                    return dateTimeOffsetMember.Member.Name is nameof(DateTimeOffset.DateTime) or nameof(DateTimeOffset.LocalDateTime)
                        ? localDateTime
                        : Expression.MakeMemberAccess(localDateTime, typeof(DateTime).GetProperty(dateTimeOffsetMember.Member.Name)!);
                }

                break;

            // Handle method call to VectorQuery
            case MethodCallExpression methodCallExpression
                when methodCallExpression.IsVectorSearch():
                return ProcessVectorSearch(methodCallExpression);

            case MethodCallExpression methodCallExpression:
                return VisitMethodCall(methodCallExpression);

            // Unwrap include expressions.
            case IncludeExpression includeExpression:
                return Visit(includeExpression.EntityExpression);

            // Replace the root with the MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression entityQueryRootExpression:
                if (_foundEntityQueryRootExpression == null)
                {
                    _foundEntityQueryRootExpression = entityQueryRootExpression;
                    return InjectAfterRootLookupStages(_source);
                }

                // Check inner sources first (handles self-joins where outer and inner are the same type)
                if (_innerSources.TryGetValue(entityQueryRootExpression.EntityType, out var innerSource))
                {
                    return innerSource;
                }

                if (_foundEntityQueryRootExpression.EntityType == entityQueryRootExpression.EntityType)
                {
                    return _source;
                }

                throw new InvalidOperationException($"Unsupported cross-DbSet query between '{_foundEntityQueryRootExpression.EntityType.Name}' " +
                                                    $"and '{entityQueryRootExpression.EntityType.Name}'. " +
                                                    "The MongoDB EF Core Provider does not support this cross-collection query. " +
                                                    "Consider using Join, Include, or restructuring your query.");
        }

        return base.Visit(expression);

        Expression ProcessVectorSearch(MethodCallExpression methodCallExpression)
        {
            var propertyExpression = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();
            var preFilterExpression = methodCallExpression.Arguments[2] is UnaryExpression
                ? methodCallExpression.Arguments[2].UnwrapLambdaFromQuote()
                : null;
            var queryVector = ParamValue<QueryVector>(3);
            var limit = ParamValue<int>(4);
            var options = ParamValue<VectorQueryOptions?>(5);

            var entityType = _queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType()!);

            // The guard/member/index resolution lives in VectorSearchStageBuilder.Resolve, reflection-free, so
            // its exceptions surface unwrapped exactly as they always have. See that type's remarks - the split
            // between Resolve and CreateStage is what keeps the observable exceptions identical across the
            // driver-LINQ bridge and the native path.
            var resolved = VectorSearchStageBuilder.Resolve(
                entityType, _source.Type, propertyExpression, options, _queryContext.QueryLogger);

            AdditionalState[MongoExecutableQuery.VectorQueryProperty] = resolved.Member;
            AdditionalState[MongoExecutableQuery.VectorQueryIndexName] = resolved.Options.IndexName!;

            object? filterDefinition = null;
            if (preFilterExpression != null)
            {
                filterDefinition = Activator.CreateInstance(
                    typeof(ExpressionFilterDefinition<>).MakeGenericType(entityType!.ClrType),
                    Visit(preFilterExpression));
            }

            var vectorSearchPipelineStage = VectorSearchStageBuilder.CreateStage(
                entityType!, propertyExpression, resolved, filterDefinition, queryVector!, limit);

            var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
                .MakeGenericMethod(entityType!.ClrType, entityType.ClrType);

            var serializerType = typeof(IBsonSerializer<>).MakeGenericType(entityType.ClrType);

            var vectorSource = Expression.Call(
                null,
                appendStageMethod,
                Visit(methodCallExpression.Arguments[0])!,
                Expression.Constant(vectorSearchPipelineStage),
                Expression.Constant(null, serializerType));

            return Expression.Call(
                null,
                appendStageMethod,
                vectorSource,
                Expression.New(
                    typeof(BsonDocumentPipelineStageDefinition<,>)
                        .MakeGenericType(entityType.ClrType, entityType.ClrType)
                        .GetConstructor([typeof(BsonDocument), serializerType])!,
                    Expression.Constant(AddScoreField),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

#if EF8 || EF9
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.ParameterValues[((ParameterExpression)methodCallExpression.Arguments[index]).Name!];
#else
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.Parameters[((QueryParameterExpression)methodCallExpression.Arguments[index]).Name];
#endif
        }
    }

    /// <summary>
    /// Resolves a DateTimeOffset-typed sub-expression down to the document and stored element name it
    /// reads from. Returns false for shapes it doesn't recognize, so the caller falls back to default
    /// translation (and the original driver error).
    /// </summary>
    private bool TryResolveDateTimeOffsetElementAccess(Expression expression, out Expression doc, out string elementName)
    {
        doc = null!;
        elementName = null!;

        // Unwrap Nullable<DateTimeOffset>.Value.
        if (expression is MemberExpression { Member.Name: "Value" } valueMember
            && Nullable.GetUnderlyingType(valueMember.Expression!.Type) == typeof(DateTimeOffset))
        {
            expression = valueMember.Expression!;
        }

        Expression source;
        string propertyName;
        switch (expression)
        {
            case MethodCallExpression methodCall
                when methodCall.Method.IsEFPropertyMethod()
                     && methodCall.Arguments[1] is ConstantExpression { Value: string name }:
                source = methodCall.Arguments[0];
                propertyName = name;
                break;

            case MemberExpression { Expression: { } memberSource } memberExpression:
                source = memberSource;
                propertyName = memberExpression.Member.Name;
                break;

            default:
                return false;
        }

        var entityType = _queryContext.Context.Model.FindEntityType(source.Type);
        var property = entityType?.FindProperty(propertyName);
        if (property == null)
        {
            return false;
        }

        if (property.FindTypeMapping() is { Converter: not null })
        {
            throw new NotSupportedException(
                $"Projecting a member of '{property.DeclaringType.DisplayName()}.{property.Name}' is not supported "
                + "because the property has a value converter configured. Member access on DateTimeOffset "
                + "(e.g. '.DateTime', '.Year') is only supported for the default document representation.");
        }

        if (property.GetBsonRepresentation() is { BsonType: not BsonType.Document } representation)
        {
            throw new NotSupportedException(
                $"Projecting a member of '{property.DeclaringType.DisplayName()}.{property.Name}' is not supported "
                + $"because the property uses a non-default BSON representation ('{representation.BsonType}'). "
                + "Member access on DateTimeOffset (e.g. '.DateTime', '.Year') is only supported for the default "
                + "document representation.");
        }

        doc = Visit(source)!;
        elementName = property.GetElementName();
        return true;
    }

    private static readonly MethodInfo EFPropertyMethodInfo =
        typeof(EF).GetMethod(nameof(EF.Property))!;

    private static readonly BsonDocument AddScoreField =
        new("$addFields", new BsonDocument { { "__score", new BsonDocument("$meta", "vectorSearchScore") } });

    // Types whose Equals(object) requires an exact runtime-type match, i.e. no cross-type equality.
    private static readonly HashSet<Type> ExactTypeEqualityTypes =
    [
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
        typeof(decimal), typeof(char), typeof(string), typeof(Guid), typeof(DateTime),
        typeof(DateTimeOffset), typeof(TimeSpan)
    ];

    /// <summary>
    /// True when <paramref name="receiver"/>.Equals(<paramref name="argument"/>) is guaranteed to return
    /// <see langword="false"/> at runtime because the two sides are known-different simple types with no
    /// cross-type equality (e.g. <c>((int?)1).Equals((ulong)2)</c>).
    /// </summary>
    private static bool IsAlwaysFalseAcrossTypeMismatch(Expression receiver, Expression argument)
    {
        var receiverType = Nullable.GetUnderlyingType(receiver.Type) ?? receiver.Type;
        var argumentType = argument.RemoveObjectConvert().Type;
        argumentType = Nullable.GetUnderlyingType(argumentType) ?? argumentType;

        return AreMismatchedExactEqualityTypes(receiverType, argumentType);
    }

    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> are different <see cref="ExactTypeEqualityTypes"/>
    /// members, so equality between them is always false. Scoped to that set rather than any mismatched
    /// types, since an arbitrary type's <c>Equals(object)</c> override could compare across types.
    /// </summary>
    private static bool AreMismatchedExactEqualityTypes(Type left, Type right)
        => left != right && ExactTypeEqualityTypes.Contains(left) && ExactTypeEqualityTypes.Contains(right);

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.Name == nameof(Enumerable.Contains))
        {
            var rewrite = VisitContainsMethod(node);
            if (rewrite != null)
                return rewrite;
        }

        // A projected/filtered cross-collection collection-navigation Count, lowered by EF Core to
        // Queryable.Count(Queryable.Where(DbSet<Target>(), fkPredicate)). Rewrite it to a client-side
        // Enumerable.Count over Mql.Field(outerDoc, "_lookup_<Nav>", navSerializer); the driver renders
        // this as a server-side { $size: "$_lookup_<Nav>" } reading the array materialized by the
        // InjectAfterRoot $lookup.
        if (TryRewriteCollectionNavigationCount(node, out var sizeRewrite))
        {
            return sizeRewrite;
        }

        // A bare/unfiltered embedded (owned) collection-navigation Count, e.g. `b.Posts.Count`, lowered by
        // EF Core to Queryable.Count(Queryable.AsQueryable(EF.Property(shaper, "Posts"))). See
        // TryRewriteEmbeddedCollectionNavigationCount for why this needs a null/missing-safe normalization
        // the driver's own rendering of a bare Count (a server-side $size) doesn't provide.
        if (TryRewriteEmbeddedCollectionNavigationCount(node, out var embeddedCountRewrite))
        {
            return embeddedCountRewrite;
        }

        var zeroTakeRewrite = TryRewriteZeroTake(node);
        if (zeroTakeRewrite != null)
        {
            return zeroTakeRewrite;
        }

        return base.VisitMethodCall(node);
    }

    /// <summary>
    /// Rewrites a bare embedded (owned) collection-navigation <c>Count</c>/<c>LongCount</c> (e.g.
    /// <c>b.Posts.Count</c>) into <c>Enumerable.Count</c> over a <c>??</c>-normalized array read — needed
    /// because the driver's bare-Count translation is a server-side <c>$size</c>, which throws on a
    /// missing or null array (unlike a predicated Count, translated as null-tolerant <c>$map</c>/<c>$sum</c>).
    /// </summary>
    private bool TryRewriteEmbeddedCollectionNavigationCount(MethodCallExpression node, out Expression result)
    {
        result = null!;

        if (node.Method.DeclaringType != typeof(Queryable)
            || node.Arguments.Count != 1
            || node.Method.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount)))
        {
            return false;
        }

        if (node.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.AsQueryable), DeclaringType: var asQueryableDeclaring }
            } asQueryableCall
            || asQueryableDeclaring != typeof(Queryable))
        {
            return false;
        }

        // Narrow to genuinely embedded (owned) collection navigations only: AsQueryable(...) also shows
        // up wrapping other enumerable sources (e.g. an IGrouping in a GroupBy/Union pipeline) that are
        // not a document field read at all, so rewriting unconditionally miscompiles those shapes.
        if (asQueryableCall.Arguments[0] is not MethodCallExpression efPropertyCall
            || !efPropertyCall.Method.IsEFPropertyMethod()
            || efPropertyCall.Arguments[1] is not ConstantExpression { Value: string propertyName }
            || _queryContext.Context.Model.FindEntityType(efPropertyCall.Arguments[0].Type) is not { } sourceEntityType
            || sourceEntityType.FindNavigation(propertyName) is not { } navigation
            || !navigation.IsEmbedded())
        {
            return false;
        }

        var fieldAccess = Visit(asQueryableCall.Arguments[0]);
        var elementType = fieldAccess?.Type.TryGetItemType();
        if (elementType == null)
        {
            return false;
        }

        var countMethod = (node.Method.Name == nameof(Queryable.LongCount)
                ? EnumerableLongCountMethod
                : EnumerableCountMethod)
            .MakeGenericMethod(elementType);

        var emptyCollection = Expression.Constant(Activator.CreateInstance(fieldAccess!.Type), fieldAccess.Type);
        var normalizedFieldAccess = Expression.Coalesce(fieldAccess, emptyCollection);

        result = Expression.Call(null, countMethod, normalizedFieldAccess);
        return true;
    }

    /// <summary>
    /// Rewrites <c>source.Take(0)</c> (constant or parameterized) into <c>source.Where(_ => false)</c>.
    /// The driver's <c>AstLimitStage</c> rejects a limit of 0 (EF-254) — a MongoDB <c>$limit</c> stage of 0
    /// is meaningless server-side, so the driver's guard is correct and shouldn't be relaxed. The provider
    /// already knows the concrete count by translation time (EF query parameters are resolved to constants
    /// upstream of this visitor), so it can short-circuit to the equivalent empty-result shape itself
    /// without ever emitting <c>$limit</c>.
    /// </summary>
    private Expression? TryRewriteZeroTake(MethodCallExpression node)
    {
        if (node.Method.Name != nameof(Queryable.Take)
            || node.Method.DeclaringType != typeof(Queryable)
            || node.Arguments.Count != 2
            || TryEvaluateToConstant(node.Arguments[1]) is not 0)
        {
            return null;
        }

        var elementType = node.Method.GetGenericArguments()[0];
        var parameter = Expression.Parameter(elementType, "_");
        var falsePredicate = Expression.Lambda(Expression.Constant(false), parameter);

        return Visit(Expression.Call(
            null,
            QueryableMethods.Where.MakeGenericMethod(elementType),
            node.Arguments[0],
            Expression.Quote(falsePredicate)));
    }

    /// <summary>
    /// Rewrites <c>Queryable.Count(Queryable.Where(DbSet&lt;Target&gt;(), fkPredicate))</c> (and the
    /// <c>LongCount</c> variant) — the lowered form of a projected/filtered collection-navigation count
    /// such as <c>c.Orders.Count</c> — into <c>Enumerable.Count(Mql.Field(outerDoc, "_lookup_&lt;Nav&gt;",
    /// navSerializer))</c>. The collection array is materialized by the matching <see
    /// cref="LookupExpression.InjectAfterRoot"/> $lookup; the driver renders the count as a server-side
    /// <c>{ $size: "$_lookup_&lt;Nav&gt;" }</c>.
    ///
    /// Guard: only fires when there is exactly one <see cref="LookupExpression.InjectAfterRoot"/> lookup
    /// pending. Multiple such lookups arise under a set operation (e.g. Union of two nav-count branches)
    /// where only one branch's root $lookup is injected; rewriting then would produce a runtime
    /// "$size must be an array" error, so we leave the subtree untranslated (translation failure) instead.
    /// </summary>
    private static readonly HashSet<string> SetOperationMethodNames =
        new(StringComparer.Ordinal) { "Union", "Concat", "Except", "Intersect" };

    /// <summary>
    /// Defence-in-depth only — called from <see cref="Translate"/> (the WHOLE-ENTITY shaper path) and only
    /// when its <c>guardUnstrippableForceUnwindJoin</c> argument is set (the bulk path clears it: there is
    /// no shaper on that path, so this guard's premise does not hold there). Never called from
    /// <see cref="TranslateProjected"/> (see the note at that call site for why the same guard there would
    /// turn a correct, pre-existing fallback into a regression).
    /// <para>
    /// When a <c>ForceUnwind</c> lookup is pending, the whole-entity shaper is built (at translation time,
    /// via <see cref="Expressions.MongoQueryExpression.UsesDriverJoinFields"/>) assuming the FLAT
    /// <c>_lookup_&lt;Nav&gt;</c> document shape, regardless of whether <see cref="StripJoinForLookup"/>
    /// later actually manages to strip the join out of this execution's captured chain. A failed strip is
    /// safe to fall through on only while the surviving join's composed operators still operate on the
    /// join's own <c>TransparentIdentifier</c> (the shape <see cref="TransparentIdentifierToLookupFieldRewriter"/>
    /// knows how to rewrite) — not when a composed operator's lambda is written directly against the
    /// already-flattened root type (guarded against separately in <see cref="ReattachComposedOperator"/>).
    /// </para>
    /// <para>
    /// This guard exists so that if a not-yet-found gap in <see cref="ReattachComposedOperator"/>/
    /// <see cref="TransparentIdentifierToLookupFieldRewriter"/> lets a strip fail while a <c>ForceUnwind</c>
    /// lookup is pending for a WHOLE-ENTITY query, the shaper mismatch fails cleanly (an
    /// <see cref="InvalidOperationException"/>, in every <see cref="Infrastructure.MongoQueryMode"/>) rather
    /// than reaching the shaper and throwing an unrelated-looking BSON-materialization exception.
    /// </para>
    /// </summary>
    private void GuardAgainstUnstrippableForceUnwindJoin(Expression? stripped, Expression efQueryExpression)
    {
        if (stripped == null && _pendingLookups.Any(l => l.ForceUnwind))
        {
            throw new InvalidOperationException(
                Microsoft.EntityFrameworkCore.Diagnostics.CoreStrings.TranslationFailed(efQueryExpression.Print()));
        }
    }

    /// <summary>
    /// A projected collection-navigation count is materialized by a single <c>$lookup</c> injected right
    /// after the root source. Under a set operation (e.g. <c>Union</c>) where more than one branch reads
    /// the looked-up collection, the non-leading branch becomes a <c>$unionWith</c> sub-pipeline that does
    /// not see that root-level <c>$lookup</c>, so its server-side <c>{ $size: "$_lookup_&lt;Nav&gt;" }</c>
    /// would fail at runtime with "argument to $size must be an array". Detect that shape and fail
    /// translation cleanly (an <see cref="InvalidOperationException"/>) instead of emitting a pipeline that
    /// crashes on the server.
    /// </summary>
    private void GuardAgainstMultiBranchNavigationCount(Expression? efQueryExpression)
    {
        if (efQueryExpression == null || !_pendingLookups.Any(l => l.InjectAfterRoot))
        {
            return;
        }

        var finder = new MultiBranchNavigationCountFinder();
        finder.Visit(efQueryExpression);
        if (finder.HasMultiBranchNavigationCount)
        {
            // Fail as a standard EF Core translation failure (the message AssertTranslationFailed expects)
            // rather than letting a broken pipeline reach the server.
            throw new InvalidOperationException(
                Microsoft.EntityFrameworkCore.Diagnostics.CoreStrings.TranslationFailed(efQueryExpression.Print()));
        }
    }

    /// <summary>
    /// When <see cref="StripJoinForLookup"/> declines a shape (returns <see langword="null"/>), the Join
    /// chain survives in the translated tree and the driver renders it natively as nested
    /// <c>_outer</c>/<c>_inner</c> documents - see the call sites' comments.
    /// <para>
    /// An ENTITY-shaped result (<paramref name="isEntityShaped"/>, i.e. reached via <see cref="Translate"/>
    /// rather than <see cref="TranslateProjected"/>) materializes via an alias keyed off
    /// <see cref="Expressions.MongoQueryExpression.UsesDriverJoinFields"/> — a flat <c>_lookup_&lt;Alias&gt;</c>
    /// field when 2+ forced-unwind lookups are registered, or the driver-native <c>_inner</c> field for a
    /// single reference join. The native fallback here produces neither: it's the UNMODIFIED nested
    /// <c>_outer</c>/<c>_inner</c> shape, valid for only ONE level of driver-native nesting. With 2+
    /// registered lookups declined, that mismatch is unconditionally wrong regardless of join kind - reject
    /// it always, not only when a join happens to be left-outer.
    /// </para>
    /// <para>
    /// A scalar/anonymous projection (<c>!isEntityShaped</c>) never reads through that alias — its leaf
    /// fields are baked in as literal structural paths (e.g. <c>Outer.Outer._id</c>) that match whatever
    /// shape the driver's native rendering actually produces, so chaining native rendering two levels deep
    /// is fine there as long as every join involved is a plain inner <c>Join</c> (see
    /// <c>NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties</c>).
    /// It still breaks when one of the un-reattached joins is left-outer (<c>LeftJoin</c>/<c>GroupJoin</c> +
    /// <c>DefaultIfEmpty</c>) - the second level's null-preserving <c>$unwind</c> re-nests under another
    /// <c>_outer</c> that those baked-in paths never expected, materializing null entities instead of
    /// failing loudly (see EF-X024).
    /// </para>
    /// </summary>
    private void GuardAgainstUnstrippableMultiJoin(Expression? stripped, Expression fullQuery, bool isEntityShaped)
    {
        var forceUnwindLookups = _pendingLookups.Where(l => l.ForceUnwind).ToList();
        if (stripped == null
            && forceUnwindLookups.Count > 1
            && (isEntityShaped || forceUnwindLookups.Any(l => l.PreserveNullAndEmptyArrays)))
        {
            throw new InvalidOperationException(
                Microsoft.EntityFrameworkCore.Diagnostics.CoreStrings.TranslationFailed(fullQuery.Print()));
        }
    }

    /// <summary>
    /// Detects a set operation (Union/Concat/Except/Intersect) at least two of whose operands contain a
    /// projected collection-navigation count subtree (<c>Count(Where(EntityQueryRootExpression, fk))</c>).
    /// </summary>
    private sealed class MultiBranchNavigationCountFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public bool HasMultiBranchNavigationCount { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (SetOperationMethodNames.Contains(node.Method.Name)
                && (node.Method.DeclaringType == typeof(Queryable)
                    || node.Method.DeclaringType == typeof(Enumerable)))
            {
                var branchesWithCount = node.Arguments.Count(ContainsNavigationCount);
                if (branchesWithCount >= 2)
                {
                    HasMultiBranchNavigationCount = true;
                }
            }

            return base.VisitMethodCall(node);
        }

        private static bool ContainsNavigationCount(Expression expression)
        {
            var finder = new NavigationCountFinder();
            finder.Visit(expression);
            return finder.Found;
        }

        private sealed class NavigationCountFinder : System.Linq.Expressions.ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.DeclaringType == typeof(Queryable)
                    && node.Arguments.Count == 1
                    && node.Method.Name is nameof(Queryable.Count) or nameof(Queryable.LongCount)
                    && node.Arguments[0] is MethodCallExpression
                    {
                        Method: { Name: nameof(Queryable.Where), DeclaringType: var d },
                        Arguments: [EntityQueryRootExpression, _]
                    }
                    && d == typeof(Queryable))
                {
                    Found = true;
                }

                return base.VisitMethodCall(node);
            }
        }
    }

    private bool TryRewriteCollectionNavigationCount(MethodCallExpression node, out Expression result)
    {
        result = null!;

        if (node.Method.DeclaringType != typeof(Queryable)
            || node.Arguments.Count != 1
            || node.Method.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount)))
        {
            return false;
        }

        if (node.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        // Resolve the InjectAfterRoot $lookup registered by the projection binder for this navigation
        // (matched by target entity type). The multi-branch set-operation case is rejected earlier by
        // GuardAgainstMultiBranchNavigationCount, so here we only need the matching lookup to exist.
        var targetEntityType = rootExpression.EntityType;
        var lookup = _pendingLookups.FirstOrDefault(
            l => l.InjectAfterRoot && l.TargetEntityType == targetEntityType);
        if (lookup == null)
        {
            return false;
        }

        // InjectAfterRoot is only ever set for navigation-derived lookups (collection-count
        // projections), so a matched lookup here always carries a real navigation.
        var navigation = lookup.Navigation!;

        // Extract the outer document reference from the FK predicate: the parameter that is NOT the inner
        // Where lambda's parameter (e.g. the `c` in `o0 => c.CustomerID == o0.CustomerID`).
        var predicate = predicateArg.UnwrapLambdaFromQuote();
        var innerParam = predicate.Parameters[0];
        var outerReference = FindOuterReference(predicate.Body, innerParam);
        if (outerReference == null)
        {
            return false;
        }

        var visitedOuter = Visit(outerReference)!;

        // Mql.Field<TOuter, TNav>(outerDoc, "_lookup_<Nav>", navSerializer) reads the looked-up array.
        var navClrType = navigation.ClrType;
        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(visitedOuter.Type, navClrType);
        var navSerializer = _bsonSerializerFactory.GetNavigationSerializer(navigation);
        var fieldAccess = Expression.Call(null, mqlField, visitedOuter,
            Expression.Constant(lookup.As),
            Expression.Constant(navSerializer));

        var elementType = navClrType.TryGetItemType() ?? navigation.TargetEntityType.ClrType;
        var countMethod = (node.Method.Name == nameof(Queryable.LongCount)
                ? EnumerableLongCountMethod
                : EnumerableCountMethod)
            .MakeGenericMethod(elementType);

        result = Expression.Call(null, countMethod, fieldAccess);
        return true;
    }

    private static readonly MethodInfo EnumerableCountMethod =
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1);

    private static readonly MethodInfo EnumerableLongCountMethod =
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Enumerable.LongCount) && m.GetParameters().Length == 1);

    /// <summary>
    /// Find the outer-entity reference in an FK-equality predicate body — the sub-expression rooted at a
    /// parameter other than the inner Where lambda parameter (e.g. <c>c</c> in
    /// <c>o => c.CustomerID == o.CustomerID</c>). Returns the outer parameter or member access whose
    /// ultimate parameter is the outer one.
    /// </summary>
    private static Expression? FindOuterReference(Expression body, ParameterExpression innerParam)
    {
        var finder = new OuterReferenceFinder(innerParam);
        finder.Visit(body);
        return finder.Found;
    }

    private sealed class OuterReferenceFinder(ParameterExpression innerParam)
        : System.Linq.Expressions.ExpressionVisitor
    {
        public ParameterExpression? Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (Found == null && node != innerParam)
            {
                Found = node;
            }

            return node;
        }
    }

    private Expression? VisitContainsMethod(MethodCallExpression node)
    {
        Expression collectionExpr;
        Expression itemExpr;

        if (node is { Object: not null, Arguments.Count: 1 })
        {
            // Instance: list.Contains(item)
            collectionExpr = node.Object;
            itemExpr = node.Arguments[0];
        }
        else if (node.Object == null && node.Arguments.Count == 2
                 && (node.Method.DeclaringType == typeof(Enumerable)
                     || node.Method.DeclaringType == typeof(Queryable)))
        {
            // Static: Enumerable/Queryable.Contains(source, item)
            collectionExpr = node.Arguments[0];
            itemExpr = node.Arguments[1];
        }
        else
        {
            return null;
        }

        var rewrite = TryRewriteEntityContains(collectionExpr, itemExpr)
                      ?? TryRewriteScalarProjectedContains(collectionExpr, itemExpr);
        return rewrite != null ? Visit(rewrite)! : null;
    }

    /// <summary>
    /// Rewrites <c>source.Select(selector).Contains(item)</c> (scalar, not entity, projection) into
    /// <c>source.Any(x => selector(x) == item)</c>. The two are equivalent regardless of what precedes the
    /// <c>Select</c>, and routing through <c>Any</c> avoids handing the driver's LINQ provider a
    /// <c>Contains</c>-of-a-projection shape it mistranslates (CSHARP-5678 / EF-229).
    /// </summary>
    private Expression? TryRewriteScalarProjectedContains(Expression collection, Expression item)
    {
        if (collection is not MethodCallExpression { Method.Name: nameof(Queryable.Select) } selectCall
            || selectCall.Arguments.Count != 2)
            return null;

        var selectMethodDefinition = selectCall.Method.GetGenericMethodDefinition();
        var isQueryable = selectMethodDefinition == QueryableMethods.Select;
        if (!isQueryable && selectMethodDefinition != EnumerableMethods.Select)
            return null;

        var selector = selectCall.Arguments[1].UnwrapLambdaFromQuote();
        if (selector.Parameters.Count != 1)
            return null;

        var elementType = selector.Parameters[0].Type;
        var predicate = Expression.Lambda(
            Expression.Equal(selector.Body, item, liftToNull: false, method: null),
            selector.Parameters[0]);

        var anySource = isQueryable ? typeof(Queryable) : typeof(Enumerable);
        var anyMethod = anySource.GetMethods()
            .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(elementType);

        return Expression.Call(null, anyMethod, selectCall.Arguments[0],
            isQueryable ? Expression.Quote(predicate) : predicate);
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var rewrite = TryRewriteEntityEquality(node.Left, node.Right, node.NodeType);
            if (rewrite != null)
                return rewrite;
        }

        return base.VisitBinary(node);
    }

    /// <summary>
    /// Find an entity type in the model, preferring owned types (which require searching all entity types).
    /// </summary>
    private IEntityType? FindEntityType(Type clrType)
        => _queryContext.Context.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType == clrType && e.IsOwned())
           ?? _queryContext.Context.Model.FindEntityType(clrType);

    /// <summary>
    /// Get the properties to use for entity comparison: primary key properties for non-owned entities,
    /// all CLR-mapped properties for owned entities.
    /// Returns null if no usable properties are found.
    /// </summary>
    private static IReadOnlyList<IProperty>? GetComparisonProperties(IEntityType entityType)
    {
        if (entityType.IsOwned())
        {
            var properties = entityType.GetProperties()
                .Where(p => p.PropertyInfo != null || p.FieldInfo != null) // exclude shadow properties — they have no CLR member to read a constant value from
                .ToList();
            return properties.Count > 0 ? properties : null;
        }

        return entityType.FindPrimaryKey()?.Properties;
    }

    /// <summary>
    /// Build a balanced equality expression comparing a query-side expression to a constant entity value
    /// across all comparison properties. For owned entities uses direct member access, for non-owned uses EF.Property.
    /// </summary>
    private static Expression CreateEntityEqualityExpression(
        Expression querySide, object constantEntity, IReadOnlyList<IProperty> properties,
        bool isOwned, ExpressionType nodeType = ExpressionType.Equal)
    {
        var comparisons = properties.Select(p =>
        {
            Expression queryAccess;
            Expression constantExpr;

            if (isOwned)
            {
                queryAccess = p.PropertyInfo != null
                    ? Expression.Property(querySide, p.PropertyInfo)
                    : Expression.Field(querySide, p.FieldInfo!);
                // Shadow properties are excluded by GetComparisonProperties so PropertyInfo/FieldInfo is always non-null here
                var value = p.PropertyInfo?.GetValue(constantEntity) ?? p.FieldInfo?.GetValue(constantEntity);
                constantExpr = Expression.Constant(value, p.ClrType);
            }
            else
            {
                queryAccess = CreateEfPropertyExpression(querySide, p);
                constantExpr = ExtractKeyValue(constantEntity, p);
            }

            return nodeType == ExpressionType.Equal
                ? Expression.Equal(queryAccess, constantExpr)
                : Expression.NotEqual(queryAccess, constantExpr);
        }).ToList();

        return CombineBalanced(comparisons, nodeType == ExpressionType.Equal
            ? Expression.AndAlso
            : Expression.OrElse);
    }

    /// <summary>
    /// Combine a list of expressions into a balanced binary tree using the given combiner.
    /// </summary>
    private static Expression CombineBalanced(IReadOnlyList<Expression> expressions,
        Func<Expression, Expression, BinaryExpression> combiner)
    {
        return expressions.Count switch
        {
            1 => expressions[0],
            2 => combiner(expressions[0], expressions[1]),
            _ => combiner(
                CombineBalanced(expressions.Take(expressions.Count / 2).ToList(), combiner),
                CombineBalanced(expressions.Skip(expressions.Count / 2).ToList(), combiner))
        };
    }

    private Expression? TryRewriteEntityEquality(Expression left, Expression right, ExpressionType nodeType)
    {
        var (entitySide, constantValue) = ResolveEntityComparison(left, right);
        if (entitySide == null || constantValue == null)
            return null;

        var entityType = FindEntityType(entitySide.RemoveConvert().Type);
        if (entityType == null)
            return null;

        var properties = GetComparisonProperties(entityType);
        if (properties == null)
            return null;

        return Visit(CreateEntityEqualityExpression(entitySide, constantValue, properties, entityType.IsOwned(), nodeType))!;
    }

    private Expression? TryRewriteEntityContains(Expression collection, Expression item)
    {
        var elementType = collection.Type.TryGetItemType();
        if (elementType == null)
            return null;

        var entityType = FindEntityType(elementType);
        if (entityType == null)
            return null;

        var properties = GetComparisonProperties(entityType);
        if (properties == null)
            return null;

        // Item is constant
        var constantValue = TryEvaluateToConstant(item);
        if (constantValue != null)
        {
            var parameter = Expression.Parameter(elementType, "__x");
            var predicate = CreateEntityEqualityExpression(parameter, constantValue, properties, entityType.IsOwned());
            var lambda = Expression.Lambda(predicate, parameter);

            var isQueryable = collection.Type.IsGenericType
                              && collection.Type.GetGenericTypeDefinition() == typeof(IQueryable<>);
            var anySource = isQueryable ? typeof(Queryable) : typeof(Enumerable);
            var anyMethod = anySource.GetMethods()
                .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                .MakeGenericMethod(elementType);

            return Expression.Call(null, anyMethod, collection,
                isQueryable ? Expression.Quote(lambda) : lambda);
        }

        // Collection is constant
        var constantCollection = TryEvaluateToConstant(collection);
        if (constantCollection is System.Collections.IEnumerable enumerable)
        {
            var allEntities = enumerable.Cast<object?>().ToList();
            if (allEntities.Count == 0)
                return Expression.Constant(false);

            var parts = new List<Expression>();

            if (allEntities.Any(e => e == null))
                parts.Add(Expression.Equal(item, Expression.Constant(null, item.Type)));

            foreach (var entity in allEntities.Where(e => e != null))
                parts.Add(CreateEntityEqualityExpression(item, entity!, properties, entityType.IsOwned()));

            return CombineBalanced(parts, Expression.OrElse);
        }

        return null;
    }

    private (Expression? entitySide, object? constantValue) ResolveEntityComparison(Expression left, Expression right)
    {
        var result = TryResolveEntityComparison(left, right);
        return result.entitySide != null
            ? result :
            TryResolveEntityComparison(right, left); // Try swapped
    }

    private (Expression? entitySide, object? constantValue) TryResolveEntityComparison(
        Expression candidateEntity, Expression candidateConstant)
    {
        var unwrappedEntity = candidateEntity.RemoveConvert();
        var entityType = FindEntityType(unwrappedEntity.Type);
        if (entityType == null)
            return (null, null);

        // If neither side evaluates to a constant (e.g. entity == entity), we return null and let EF Core handle it
        var constantValue = TryEvaluateToConstant(candidateConstant);
        if (constantValue == null)
            return (null, null);

        return (candidateEntity, constantValue);
    }

    private object? TryEvaluateToConstant(Expression expression)
    {
        expression = expression.RemoveConvert();

        if (expression is ConstantExpression constant)
            return constant.Value;

        if (expression is MemberExpression { Expression: ConstantExpression closureConstant } member)
        {
            return member.Member switch
            {
                FieldInfo field => field.GetValue(closureConstant.Value),
                PropertyInfo prop => prop.GetValue(closureConstant.Value),
                _ => null
            };
        }

#if EF8 || EF9
        if (expression is ParameterExpression param
            && param.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true
            && _queryContext.ParameterValues.TryGetValue(param.Name, out var value))
            return value;
#else
        if (expression is QueryParameterExpression queryParam)
            return _queryContext.Parameters[queryParam.Name];
#endif

        // Fallback: try to compile and evaluate the expression (handles MemberInitExpression, nested closures, etc.)
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(expression, typeof(object)));
            return lambda.Compile()();
        }
        catch
        {
            return null;
        }
    }

    private static MethodCallExpression CreateEfPropertyExpression(Expression source, IProperty property)
    {
        var method = EFPropertyMethodInfo.MakeGenericMethod(property.ClrType);
        return Expression.Call(null, method, source, Expression.Constant(property.Name));
    }

    private static ConstantExpression ExtractKeyValue(object entity, IProperty property)
    {
        if (property.PropertyInfo == null && property.FieldInfo == null)
            throw new NotSupportedException($"Entity comparison on shadow key property '{property.Name}' is not supported.");

        var value = property.PropertyInfo?.GetValue(entity) ?? property.FieldInfo?.GetValue(entity);
        return Expression.Constant(value, property.ClrType);
    }

    private static readonly MethodInfo AsMethodInfo = typeof(MongoQueryable)
        .GetMethods()
        .First(mi => mi is { Name: nameof(MongoQueryable.As), IsPublic: true, IsStatic: true } && mi.GetParameters().Length == 2);
}
