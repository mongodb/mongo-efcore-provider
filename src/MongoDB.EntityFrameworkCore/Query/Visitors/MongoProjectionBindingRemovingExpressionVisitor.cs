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
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class MongoProjectionBindingRemovingExpressionVisitor : ExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly IEntityType _rootEntityType;
    private readonly ParameterExpression DocParameter;
    private readonly bool _trackQueryResults;
    private readonly Dictionary<ParameterExpression, Expression> _materializationContextBindings = new();
    private readonly Dictionary<Expression, ParameterExpression> _projectionBindings = new();
    private readonly Dictionary<Expression, (IEntityType EntityType, Expression BsonDocExpression)> _ownerMappings = new();
    private readonly Dictionary<Expression, Expression> _ordinalMappings = new();
    private List<IncludeExpression> _pendingIncludes = [];

    /// <summary>
    /// Create a <see cref="MongoProjectionBindingRemovingExpressionVisitor"/>.
    /// </summary>
    /// <param name="rootEntityType">The <see cref="IEntityType"/> this projection relates to.</param>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> this visitor should use.</param>
    /// <param name="docParameter">The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.</param>
    /// <param name="trackingBehavior">The <see cref="QueryTrackingBehavior"/> for this query.</param>
    public MongoProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        QueryTrackingBehavior trackingBehavior)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
        DocParameter = docParameter;
        _trackQueryResults = trackingBehavior == QueryTrackingBehavior.TrackAll;
    }

    /// <summary>True for the fallback shaper, which sees whole un-projected documents.</summary>
    protected virtual bool ReadsUnprojectedDocuments => false;

    /// <summary>Whether <paramref name="alias"/> is a native whole-root-entity ("$$ROOT") leaf.</summary>
    private bool IsWholeRootEntityAlias(string? alias)
        => alias != null
           && _queryExpression.Select.Projection.Any(
               p => p.Alias == alias
                    && p.Expression is MongoElementRefExpression
                    {
                        Path: MongoElementRefExpression.WholeRootDocumentPath
                    });

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case ProjectionBindingExpression projectionBindingExpression:
                {
                    var projection = GetProjection(projectionBindingExpression);

                    // Alias is null when the projection's expression has no natural name —
                    // the BsonDoc itself is the value.
                    if (projection.Alias is null)
                    {
                        return DocParameter;
                    }

                    // A native numeric-cast projection leaf
                    // (`new { X = (int)x.D }`) is registered on the WRITE side as the whole
                    // UnaryExpression{Convert} node (see MongoProjectionBindingExpressionVisitor.Visit), and the
                    // $project alias holds the CONVERTED value ($toInt/$toLong/$toDouble/$toDecimal), never the
                    // underlying member's own raw stored representation. The comment immediately below this
                    // block — "aliased projections that introduce a Convert... go through the LINQ V3 push-down
                    // path and never reach this visitor" — predates this feature and is no longer true for a
                    // NATIVELY-representable cast leaf specifically: TryResolveFieldAccess unconditionally calls
                    // RemoveConvert(), which would strip this Convert, resolve the PRE-CAST member's own
                    // property, and either mis-deserialise the converted value through that property's own
                    // (pre-cast) serializer or trip the type-mismatch guard below and crash at shaper-compile
                    // time in EVERY query mode (not merely under NativeOnly) — turning a legitimate, correct
                    // native shape into a hard regression. Route it through the raw alias read instead, exactly
                    // like an arithmetic/count computed leaf (the branch below this one, reached when
                    // fieldAccess.Property is null).
                    //
                    // This branch is reachable ONLY for a leaf the EMIT side already admitted, and the emit
                    // side (NativeProjectionBinder.TryTranslateLeaf) can never admit one backed by a
                    // value-converted or non-default-BsonRepresentation field — TryTranslateValue's Guard B
                    // (AllFieldsDefaultSerialized) recurses through the Convert into the field and declines it
                    // at TRANSLATE time, so a converted property's cast never reaches this raw read with the
                    // WRONG (converted) value in hand. See that gate's own comment for the full dependency;
                    // it is recorded here too because this branch's own correctness rests on it.
                    if (projection.Expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked })
                    {
                        return BsonBinding.CreateGetElementValue(DocParameter, projection.Alias, projectionBindingExpression.Type);
                    }

                    // Resolve the source IProperty so we apply its serializer / nullability —
                    // not whatever EF property happens to share the alias name on the root entity.
                    // TryResolveFieldAccess unwraps Convert nodes, so in principle the binding's
                    // outer type could differ from the property's CLR type. In practice it never
                    // does at this call site: aliased projections that introduce a Convert (e.g.
                    // `new { X = (long)p.intProp }`) go through the LINQ V3 push-down path and
                    // never reach this visitor — see ProjectionAnalyzer.CanPushDown. (That claim now
                    // holds only for a NON-natively-representable Convert leaf; the native cast case
                    // is intercepted above, before this comment's invariant is ever consulted.) The
                    // assert below pins the invariant so a future change that violates it fails loudly
                    // rather than mis-deserialising via the wrong property's serializer.
                    //
                    // Nullability can differ in EITHER direction, and both are legitimate: the binding type can
                    // be the nullable form of a non-nullable property (widening), or — since TryResolveFieldAccess
                    // now peels a `Nullable<T>.Value` leaf (EF-402) — the property can be the NULLABLE form of a
                    // non-nullable binding type (`x.Converted.Value` binds as `int` against a `Converted` property
                    // typed `int?`). Unwrap both sides before comparing so either direction is accepted.
                    var fieldAccess = TryResolveFieldAccess(projection.Expression);
                    if (fieldAccess.Property != null)
                    {
                        if (fieldAccess.Property.ClrType != projectionBindingExpression.Type
                            && fieldAccess.Property.ClrType.UnwrapNullableType() != projectionBindingExpression.Type.UnwrapNullableType())
                        {
                            throw new InvalidOperationException(
                                $"Aliased projection type '{projectionBindingExpression.Type}' does not match source property " +
                                $"'{fieldAccess.Property.Name}' of type '{fieldAccess.Property.ClrType}'; the property's serializer " +
                                "may produce values that cannot be cast to the binding's outer type.");
                        }

                        var valueExpression = BsonBinding.CreateGetValueExpression(
                            DocParameter,
                            projection.Alias,
                            fieldAccess.Property,
                            projectionBindingExpression.Type);

                        // The read expression's type is the property's CLR type widened to nullable when
                        // the property is nullable; the assert above permits the binding type to be the
                        // non-nullable form, so convert to the exact binding type to keep the shaper's
                        // expression tree well-typed.
                        return valueExpression.Type == projectionBindingExpression.Type
                            ? valueExpression
                            : Expression.Convert(valueExpression, projectionBindingExpression.Type);
                    }

                    // For non-property expressions (arithmetic, constants, Mql.Field) — and for
                    // key-property bindings — the push-down result document carries the value under
                    // the projection alias as its BSON element name (e.g. `{ OrderID: "$_id" }`), so
                    // read it raw by that alias. projection.Expression is non-nullable (see
                    // ProjectionExpression), so there is no null-source path to handle here.
                    return BsonBinding.CreateGetElementValue(
                        DocParameter,
                        projection.Alias,
                        projectionBindingExpression.Type);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    IArrayProjectionExpression arrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            // Both array-projection node kinds are admissible: a navigation-driven
                            // ObjectArrayProjectionExpression (a projected reference collection) and an
                            // ArrayAliasProjectionExpression (a native $project alias). Anything that is not
                            // an array projection is a translation failure, not an unchecked-cast crash.
                            if (projection.Expression is not IArrayProjectionExpression fromProjection)
                            {
                                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                            }

                            arrayProjection = fromProjection;
                            break;
                        case IArrayProjectionExpression inlineArrayProjection:
                            arrayProjection = inlineArrayProjection;
                            break;
                        default:
                            throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                    }

                    Expression bsonArrayExpression;
                    string arrayName;
                    if (_projectionBindings.TryGetValue((Expression)arrayProjection, out var bsonArrayVar))
                    {
                        bsonArrayExpression = bsonArrayVar;
                        arrayName = bsonArrayVar.Name!;
                    }
                    else
                    {
                        // Nested collection inside a parent collection item (ThenInclude on
                        // collection-then-collection). Read the BsonArray from the parent document by its
                        // DOCUMENT-PATH field name.
                        //
                        // An ALIAS-addressed array (ArrayAliasProjectionExpression) has no document-path field
                        // name at all — its ArrayFieldName is always null — so it must never reach here. It
                        // cannot: this branch is reached only when VisitBinary did NOT register a bsonArrayN
                        // variable for the node, i.e. when BsonDocumentInjectingExpressionVisitor never emitted
                        // an assignment for this collection shaper, which happens only for a shaper NESTED
                        // inside another collection shaper's InnerShaper (that visitor's
                        // CollectionShaperExpression case returns without visiting children). An alias-addressed
                        // array is created only for a TOP-LEVEL leaf of a terminal native projection, never
                        // nested. The `?? throw` makes a future violation LOUD rather than a silent null field
                        // name, which would read the wrong array with no exception at all.
                        //
                        // It resolves BEFORE the _projectionBindings lookup below, deliberately: on the native
                        // projection route the root RootReferenceExpression is NOT registered there, so an
                        // alias-addressed array reaching this branch would otherwise die in that indexer with a
                        // bare KeyNotFoundException — losing this diagnostic entirely.
                        var arrayFieldName = arrayProjection.ArrayFieldName
                                             ?? throw new InvalidOperationException(
                                                 CoreStrings.TranslationFailed(extensionExpression.Print()));
                        var parentAccess = arrayProjection.AccessExpression;
                        var parentDoc = _projectionBindings[parentAccess];
                        bsonArrayExpression = BsonBinding.CreateGetBsonArray(parentDoc, arrayFieldName);
                        arrayName = arrayFieldName;
                    }

                    // Normalize a MISSING or explicitly-BSON-null stored array to an EMPTY BsonArray, so the
                    // shaper below enumerates nothing and PopulateCollection returns an EMPTY collection rather than
                    // null. Empty-not-null is EF Core's contract for a collection navigation, and without this the
                    // result depends on the POCO's field initializer (IncludeCollection skips GetOrCreate when the
                    // related-entity sequence is null). The empty collection is built by PopulateCollection through the
                    // navigation's OWN IClrCollectionAccessor, so a non-List navigation (HashSet<T>, a custom
                    // collection) gets the right CLR type for free.
                    //
                    // The coalesce MUST stay here, at the point of use, and NOT be folded into either assignment site:
                    // VisitBinary below hard-casts a BsonDocument/BsonArray assignment's right-hand side to
                    // UnaryExpression (the Expression.TypeAs that BsonDocumentInjectingExpressionVisitor emits), so a
                    // Coalesce there throws InvalidCastException for every shaper in every query mode.
                    //
                    // Note TypeAs yields null both for an ABSENT element and for a present-but-not-an-array element, so
                    // this treats the two alike; both produced null before, so that is not a regression.
                    bsonArrayExpression = Expression.Coalesce(bsonArrayExpression, Expression.New(typeof(BsonArray)));

                    var jObjectParameter = Expression.Parameter(typeof(BsonDocument), arrayName + "Object");
                    var ordinalParameter = Expression.Parameter(typeof(int), arrayName + "Ordinal");

                    var accessExpression = arrayProjection.InnerProjection.ParentAccessExpression;
                    _projectionBindings[accessExpression] = jObjectParameter;
                    _ownerMappings[accessExpression] = (arrayProjection.Navigation.DeclaringEntityType, arrayProjection.AccessExpression);
                    _ownerMappings[jObjectParameter] = (arrayProjection.Navigation.DeclaringEntityType, arrayProjection.AccessExpression);
                    _ordinalMappings[accessExpression] = Expression.Add(ordinalParameter, Expression.Constant(1, typeof(int)));
                    var innerShaper = (BlockExpression)Visit(collectionShaperExpression.InnerShaper);

                    innerShaper = AddIncludes(innerShaper);

                    var entities = Expression.Call(
                        EnumerableMethods.SelectWithOrdinal.MakeGenericMethod(typeof(BsonDocument), innerShaper.Type),
                        Expression.Call(
                            EnumerableMethods.Cast.MakeGenericMethod(typeof(BsonDocument)),
                            bsonArrayExpression),
                        Expression.Lambda(innerShaper, jObjectParameter, ordinalParameter));

                    var navigation = collectionShaperExpression.Navigation!;
                    return Expression.Call(
                        PopulateCollectionMethodInfo.MakeGenericMethod(navigation.TargetEntityType.ClrType, navigation.ClrType),
                        Expression.Constant(navigation.GetCollectionAccessor()),
                        Expression.Constant(navigation.DeclaringEntityType.DisplayName()),
                        Expression.Constant(navigation.Name),
                        entities);
                }

            case IncludeExpression includeExpression:
                {
                    if (includeExpression.Navigation is not INavigation navigation)
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{includeExpression.Navigation}' is not supported.");
                    }

                    var isFirstInclude = _pendingIncludes.Count == 0;
                    _pendingIncludes.Add(includeExpression);

                    var bsonDocBlock = (Visit(includeExpression.EntityExpression) as BlockExpression)!;

                    if (!isFirstInclude)
                    {
                        return bsonDocBlock;
                    }

                    // For collection item shapers (inside CollectionShaperExpression), the block
                    // may not have a ConditionalExpression — it just ends with the entity variable.
                    if (bsonDocBlock.Expressions[^1] is ConditionalExpression bsonDocCondition)
                    {
                        var shaperBlock = (BlockExpression)bsonDocCondition.IfFalse;
                        shaperBlock = AddIncludes(shaperBlock);

                        List<Expression> jObjectExpressions = [..bsonDocBlock.Expressions];
                        jObjectExpressions.RemoveAt(jObjectExpressions.Count - 1);

                        jObjectExpressions.Add(
                            bsonDocCondition.Update(bsonDocCondition.Test, bsonDocCondition.IfTrue, shaperBlock));

                        return bsonDocBlock.Update(bsonDocBlock.Variables, jObjectExpressions);
                    }
                    else
                    {
                        // No conditional — entity is always present (e.g., collection item shaper).
                        // Append includes directly to the block.
                        var shaperBlock = bsonDocBlock;
                        shaperBlock = AddIncludes(shaperBlock);
                        return shaperBlock;
                    }
                }
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Visits a <see cref="BinaryExpression"/> replacing empty ProjectionBindingExpressions
    /// while passing through visitation of all others.
    /// </summary>
    /// <param name="binaryExpression">The <see cref="BinaryExpression"/> to visit.</param>
    /// <returns>A <see cref="BinaryExpression"/> with any necessary adjustments.</returns>
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        if (binaryExpression.NodeType == ExpressionType.Assign)
        {
            if (binaryExpression.Left is ParameterExpression parameterExpression)
            {
                if (parameterExpression.Type == typeof(BsonDocument) || parameterExpression.Type == typeof(BsonArray))
                {
                    string? fieldName = null;
                    var fieldRequired = true;

                    // CONTRACT with BsonDocumentInjectingExpressionVisitor: the right-hand side of a
                    // BsonDocument/BsonArray variable assignment is always an Expression.TypeAs — a
                    // UnaryExpression — so this cast is safe. If that visitor ever needs a different node
                    // shape there (a Coalesce, a New), this cast must be widened in the SAME change, or every
                    // entity and collection shaper throws InvalidCastException in every query mode — which is
                    // why the missing/null-array normalization above lives at its point of use instead.
                    var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
                    if (projectionExpression is ProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        projectionExpression = projection.Expression;
                        fieldName = projection.Alias;
                    }
                    else if (projectionExpression is UnaryExpression convertExpression &&
                             convertExpression.NodeType == ExpressionType.Convert)
                    {
                        projectionExpression = ((UnaryExpression)convertExpression.Operand).Operand;
                    }

                    Expression innerAccessExpression;
                    if (projectionExpression is IArrayProjectionExpression arrayProjectionExpression)
                    {
                        innerAccessExpression = arrayProjectionExpression.AccessExpression;
                        _projectionBindings[projectionExpression] = parameterExpression;

                        // ArrayFieldName is null for an alias-addressed array, in which case fieldName was
                        // already set from projection.Alias above (the ProjectionBindingExpression branch) —
                        // that IS the alias, and it is the same name the emit side derived from the same
                        // ProjectionMember.
                        //
                        // NEITHER implementation of IArrayProjectionExpression can currently reach this throw,
                        // and that is worth stating so nobody reads it as a live failure mode:
                        // ObjectArrayProjectionExpression's constructor already `?? throw`s when Name would be
                        // null, so its ArrayFieldName is non-null by construction; and ArrayAliasProjectionExpression
                        // (whose ArrayFieldName is ALWAYS null) is only ever reached through the
                        // ProjectionBindingExpression branch above, which has already set fieldName from
                        // projection.Alias. The guard is kept anyway because it is free and fails LOUD: a future
                        // node kind, or a future route to this one that skips the alias resolution, would
                        // otherwise silently read the wrong array rather than say so.
                        fieldName ??= arrayProjectionExpression.ArrayFieldName
                                      ?? throw new InvalidOperationException(
                                          CoreStrings.TranslationFailed(binaryExpression.Print()));
                    }
                    else
                    {
                        var entityProjectionExpression = (EntityProjectionExpression)projectionExpression;
                        var accessExpression = entityProjectionExpression.ParentAccessExpression;
                        _projectionBindings[accessExpression] = parameterExpression;
                        fieldName ??= entityProjectionExpression.Name;

                        switch (accessExpression)
                        {
                            case ObjectAccessExpression crossCollectionAccess
                                when IsCrossCollectionAccess(crossCollectionAccess):
                                // Cross-collection Include result. In driver-native LeftJoin mode the single
                                // joined reference is at root level under "_inner"; in flat $lookup + $unwind
                                // mode each join is at root level under its own "_lookup_<Navigation>" field.
                                // The field name is derived from the projection node itself (its navigation +
                                // the computed UsesDriverJoinFields flag), never from mutable global state.
                                // For a reference nested under a collection Include, the parent RootReference
                                // is bound to the per-collection-element document; read the joined field from
                                // that element rather than the absolute query root.
                                innerAccessExpression = GetCrossCollectionRootDocument(crossCollectionAccess);
                                fieldName = GetCrossCollectionFieldName(crossCollectionAccess);
                                // fieldRequired = false is also what makes a native LeftJoin's unmatched Inner
                                // row (a dangling-FK dependent row with no matching principal) read as a plain
                                // null reference navigation, with no exception, for a whole-entity Inner leaf in
                                // a join projection (EF-444 Task 3). MongoSelectLowerer already emits
                                // preserveNullAndEmptyArrays: true for a left-outer REFERENCE navigation (as
                                // opposed to a left-outer COLLECTION navigation, which is a hard-coded false and
                                // handled by a separate, still-declining conjunct elsewhere), so the joined field
                                // is simply absent from the document for an unmatched row; requiring it here
                                // would turn that absence into a spurious exception instead of EF Core's own
                                // null-reference-navigation convention. No new code was needed to get this right
                                // — see NativeJoinTests.LeftJoin_unmatched_inner_row_reads_as_null_reference_navigation.
                                fieldRequired = false;
                                break;
                            // Embedded sub-document access: the navigation is always present here because
                            // navigation-less ObjectAccessExpressions are only created for cross-collection
                            // explicit joins, which are handled by the cross-collection case above.
                            case ObjectAccessExpression { Navigation: { } embeddedNavigation } innerObjectAccessExpression:
                                innerAccessExpression = innerObjectAccessExpression.AccessExpression;
                                _ownerMappings[accessExpression] =
                                    (embeddedNavigation.DeclaringEntityType, innerAccessExpression);
                                fieldRequired = innerObjectAccessExpression.Required;
                                break;
                            case RootReferenceExpression when _queryExpression.UsesDriverJoinFields:
                                // For driver-native LeftJoin Includes, the root entity is under "_outer".
                                innerAccessExpression = DocParameter;
                                fieldName = "_outer";
                                break;
                            case RootReferenceExpression:
                                innerAccessExpression = DocParameter;
                                // On the FALLBACK route the shaper is handed WHOLE, un-projected documents, so a
                                // whole-root-entity leaf's "$$ROOT" alias names no element and must resolve to the
                                // document itself rather than a non-existent "c" field.
                                if (ReadsUnprojectedDocuments && IsWholeRootEntityAlias(fieldName))
                                {
                                    fieldName = null;
                                }

                                if (_ownerMappings.TryGetValue(accessExpression, out var ownerInfo))
                                {
                                    _ownerMappings[parameterExpression] = (ownerInfo.EntityType, ownerInfo.BsonDocExpression);
                                }

                                if (_ordinalMappings.TryGetValue(accessExpression, out var ordinalExpression))
                                {
                                    _ordinalMappings[parameterExpression] = ordinalExpression;
                                }

                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Unknown access expression type {accessExpression.Type.ShortDisplayName()}.");
                        }
                    }

                    var valueExpression =
                        CreateGetValueExpression(innerAccessExpression, fieldName, fieldRequired, parameterExpression.Type);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, valueExpression);
                }

                if (parameterExpression.Type == typeof(MaterializationContext))
                {
                    var newExpression = (NewExpression)binaryExpression.Right;

                    EntityProjectionExpression entityProjectionExpression;
                    if (newExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        entityProjectionExpression = (EntityProjectionExpression)projection.Expression;
                    }
                    else
                    {
                        var projection = ((UnaryExpression)((UnaryExpression)newExpression.Arguments[0]).Operand).Operand;
                        entityProjectionExpression = (EntityProjectionExpression)projection;
                    }

                    _materializationContextBindings[parameterExpression] = entityProjectionExpression.ParentAccessExpression;

                    var updatedExpression = Expression.New(
                        newExpression.Constructor!,
                        Expression.Constant(ValueBuffer.Empty),
                        newExpression.Arguments[1]);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
                }
            }

            if (binaryExpression.Left is MemberExpression { Member: FieldInfo { IsInitOnly: true } } memberExpression)
            {
                return memberExpression.Assign(Visit(binaryExpression.Right));
            }
        }

        return base.VisitBinary(binaryExpression);
    }

    /// <summary>
    /// Visits a <see cref="MethodCallExpression"/> replacing calls to <see cref="ValueBuffer"/>
    /// with replacement alternatives from <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="Expression"/> to replace the original method call with.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;

        if (genericMethod == ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
            Expression innerExpression;
            if (methodCallExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
            {
                var projection = GetProjection(projectionBindingExpression);
                innerExpression =
                    CreateGetValueExpression(DocParameter, projection.Alias, projection.Required, typeof(BsonDocument));
            }
            else
            {
                innerExpression =
                    _materializationContextBindings[
                        (ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object!];
            }

            return CreateGetValueExpression(innerExpression, property, methodCallExpression.Type);
        }

        if (method.DeclaringType == typeof(Enumerable)
            && method.Name == nameof(Enumerable.Select)
            && genericMethod == EnumerableMethods.Select)
        {
            var lambda = (LambdaExpression)methodCallExpression.Arguments[1];
            if (lambda.Body is IncludeExpression includeExpression)
            {
                if (includeExpression.Navigation is not INavigation navigation)
                {
                    throw new InvalidOperationException(
                        $"Including navigation '{includeExpression.Navigation}' is not supported.");
                }

                _pendingIncludes.Add(includeExpression);

                Visit(includeExpression.EntityExpression);

                // Includes on collections are processed when visiting CollectionShaperExpression
                return Visit(methodCallExpression.Arguments[0]);
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    protected Expression CreateGetValueExpression(
        Expression docExpression,
        IProperty property,
        Type type)
    {
        if (property.IsOwnedTypeKey())
        {
            var entityType = (IReadOnlyEntityType)property.DeclaringType;

            // Bare-owned whole-element SelectMany: the shaper is re-rooted at THIS owned
            // element (via $replaceRoot — see MongoShapedQueryCompilingExpressionVisitor's WholeElement branch),
            // which merged the owner key + array ordinal into the re-rooted document under sentinel field
            // names, specifically so the owned key materializes NON-NULL (EF Core's own no-tracking null-key
            // guard rejects a partially-null owned key). Read those sentinel fields directly
            // instead of falling through to the ordinary owner/ordinal-mapping lookup below, which has no entry
            // for a re-rooted document (the query root — CollectionExpression.EntityType — is always a
            // document-root type, so entityType == _rootEntityType is true here ONLY in this re-rooted
            // whole-element case, making it a safe discriminator).
            if (entityType == _rootEntityType)
            {
                // Both sentinels live nested one level UNDER the single reserved ShadowField wrapper the
                // $mergeObjects adds (EF-428), so this is a two-segment path read, not a top-level element
                // read. CreateGetElementValueAtPath walks the segments; CreateGetElementValue would look
                // "__mongoef_shadow.__ownerKey" up as one literal document key and find nothing.
                string[] sentinelPath =
                [
                    MongoReplaceRootStage.ShadowField,
                    property.IsOwnedTypeOrdinalKey()
                        ? MongoReplaceRootStage.OrdinalField
                        : MongoReplaceRootStage.OwnerKeyField
                ];
                // includeArrayIndex writes the ordinal as a BSON int64; read the owner key at its own CLR type.
                var readClrType = property.IsOwnedTypeOrdinalKey() ? typeof(long) : property.ClrType;
                Expression read = BsonBinding.CreateGetElementValueAtPath(DocParameter, sentinelPath, readClrType);
                if (readClrType != property.ClrType)
                {
                    read = Expression.Convert(read, property.ClrType);
                }

                return Expression.Convert(read, type);
            }

            if (!entityType.IsDocumentRoot())
            {
                var ownership = entityType.FindOwnership();
                if (ownership?.IsUnique == false && property.IsOwnedTypeOrdinalKey())
                {
                    var readExpression = _ordinalMappings[docExpression];
                    if (readExpression.Type != type)
                    {
                        readExpression = Expression.Convert(readExpression, type);
                    }

                    return readExpression;
                }

                var principalProperty = property.FindFirstPrincipal();
                if (principalProperty != null)
                {
                    Expression? ownerBsonDocExpression = null;
                    if (_ownerMappings.TryGetValue(docExpression, out var ownerInfo))
                    {
                        ownerBsonDocExpression = ownerInfo.BsonDocExpression;
                    }
                    else if (docExpression is RootReferenceExpression rootReferenceExpression)
                    {
                        ownerBsonDocExpression = rootReferenceExpression;
                    }
                    else if (docExpression is ObjectAccessExpression objectAccessExpression)
                    {
                        ownerBsonDocExpression = objectAccessExpression.AccessExpression;
                    }

                    if (ownerBsonDocExpression != null)
                    {
                        return CreateGetValueExpression(ownerBsonDocExpression, principalProperty, type);
                    }
                }
            }

            return Expression.Default(type);
        }

        return Expression.Convert(
            CreateGetValueExpression(docExpression, property.Name, !type.IsNullableType(), type, property.DeclaringType,
                property.GetTypeMapping()),
            type);
    }

    /// <summary>
    /// Whether an <see cref="ObjectAccessExpression"/> refers to a cross-collection Include result
    /// (a separate document joined in via the driver's native LeftJoin or via $lookup + $unwind),
    /// as opposed to an embedded sub-document accessed in place. Cross-collection results live at the
    /// root of the joined BsonDocument; embedded navigations are nested under their containing element.
    /// </summary>
    private static bool IsCrossCollectionAccess(ObjectAccessExpression accessExpression)
        => accessExpression.AccessExpression is RootReferenceExpression
           && (accessExpression.Navigation is null || !accessExpression.Navigation.IsEmbedded());

    /// <summary>
    /// Determine the root-level BsonDocument field a cross-collection Include result is read from.
    /// The decision comes from the projection node itself plus the query's computed
    /// <see cref="MongoQueryExpression.UsesDriverJoinFields"/> flag — the single source of truth —
    /// not from any mutable per-translation state:
    /// <list type="bullet">
    /// <item>driver-native LeftJoin mode: the lone joined reference is under "_inner";</item>
    /// <item>flat $lookup + $unwind mode: each join is under its own "_lookup_&lt;Navigation&gt;" alias,
    /// which is exactly the alias baked into the access expression's <see cref="ObjectAccessExpression.Name"/>.</item>
    /// </list>
    /// </summary>
    private string GetCrossCollectionFieldName(ObjectAccessExpression accessExpression)
        => _queryExpression.UsesDriverJoinFields ? "_inner" : accessExpression.Name;

    /// <summary>
    /// Determine the <see cref="BsonDocument"/> a cross-collection Include result is read from. For a
    /// top-level cross-collection Include this is the query root document (<see cref="DocParameter"/>).
    /// For a cross-collection reference nested under a collection Include (e.g.
    /// <c>Product.OrderDetails.ThenInclude(od =&gt; od.Order)</c>), the parent <see cref="RootReferenceExpression"/>
    /// is bound to the per-element document of the enclosing collection; the joined field
    /// (<c>_lookup_&lt;RefNav&gt;</c>) lives on that element, so read from the bound element document instead.
    /// </summary>
    private Expression GetCrossCollectionRootDocument(ObjectAccessExpression accessExpression)
    {
        // Only redirect to a bound element document when the joined reference is genuinely nested under a
        // collection Include — i.e. its parent RootReference is for an entity OTHER than the query root and
        // that element document is currently bound. Top-level cross-collection Includes (parent == query
        // root) and driver-native LeftJoin reference chains continue to read from the absolute root.
        if (accessExpression.AccessExpression is RootReferenceExpression { EntityType: var parentType }
            && parentType != _queryExpression.CollectionExpression.EntityType
            && _projectionBindings.TryGetValue(accessExpression.AccessExpression, out var elementDocument))
        {
            return elementDocument;
        }

        return DocParameter;
    }

    /// <summary>
    /// Obtain the registered <see cref="ProjectionExpression"/> for a given <see cref="ProjectionBindingExpression"/>.
    /// </summary>
    /// <param name="projectionBindingExpression">The <see cref="ProjectionBindingExpression"/> to look-up.</param>
    /// <returns>The registered <see cref="ProjectionExpression"/> this <paramref name="projectionBindingExpression"/> relates to.</returns>
    protected ProjectionExpression GetProjection(ProjectionBindingExpression projectionBindingExpression)
        => _queryExpression.Projection[GetProjectionIndex(projectionBindingExpression)];

    /// <summary>
    /// Create a new compilable <see cref="Expression"/> the shaper can use to obtain the value from the <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="docExpression">The <see cref="Expression"/> used to access the <see cref="BsonDocument"/>.</param>
    /// <param name="propertyName">The name of the property.</param>
    /// <param name="required"><see langword="true"/> if the field is required, <see langword="false"/> if it is optional.</param>
    /// <param name="type">The <see cref="Type"/> of the value as it is within the document.</param>
    /// <param name="declaredType">The optional <see cref="ITypeBase"/> this element comes from.</param>
    /// <param name="typeMapping">Any associated <see cref="CoreTypeMapping"/> to be used in mapping the value.</param>
    /// <returns>A compilable <see cref="Expression"/> to obtain the desired value as the correct type.</returns>
    protected Expression CreateGetValueExpression(
        Expression docExpression,
        string? propertyName,
        bool required,
        Type type,
        ITypeBase? declaredType = null,
        CoreTypeMapping? typeMapping = null)
    {
        var entityType = declaredType ?? docExpression switch
        {
            RootReferenceExpression rootReferenceExpression => rootReferenceExpression.EntityType,
            ObjectAccessExpression docAccessExpression => docAccessExpression.Navigation?.TargetEntityType ?? docAccessExpression.EntityType,
            _ => _rootEntityType
        };

        var innerExpression = docExpression;
        if (_projectionBindings.TryGetValue(docExpression, out var innerVariable))
        {
            innerExpression = innerVariable;
        }
        else
        {
            innerExpression = docExpression switch
            {
                // For driver-native LeftJoin Includes, the root entity is under "_outer" in the BsonDocument.
                RootReferenceExpression when _queryExpression.UsesDriverJoinFields
                    => CreateGetValueExpression(DocParameter, "_outer", required, typeof(BsonDocument)),
                RootReferenceExpression => CreateGetValueExpression(DocParameter, null, required, typeof(BsonDocument)),
                // Cross-collection Include results are at the root of the BsonDocument. The field is
                // derived from the projection node (navigation + computed UsesDriverJoinFields flag):
                // "_inner" for the lone driver-native reference, "_lookup_<Navigation>" in flat mode.
                ObjectAccessExpression crossCollectionAccess when IsCrossCollectionAccess(crossCollectionAccess)
                    => CreateGetValueExpression(
                        GetCrossCollectionRootDocument(crossCollectionAccess),
                        GetCrossCollectionFieldName(crossCollectionAccess), false, typeof(BsonDocument)),
                ObjectAccessExpression docAccessExpression => CreateGetValueExpression(docAccessExpression.AccessExpression,
                    docAccessExpression.Name, required, typeof(BsonDocument)),
                _ => innerExpression
            };
        }

        var elementType = typeMapping?.ClrType ?? type;
        return BsonBinding.CreateGetValueExpression(innerExpression, propertyName, required, elementType, entityType!);
    }

    protected ResolvedFieldAccess TryResolveFieldAccess(Expression? expression)
    {
        if (expression == null) return default;

        expression = expression.RemoveConvert();

        // Nullable<T>.Value: `x.Score.Value` is a MemberExpression whose OWN receiver (`x.Score`) is the member
        // access that actually names the stored field — `.Value` only changes the CLR type, never the element
        // read. Peeling it here, before Member/Expression are split apart below, mirrors
        // MongoExpressionTranslator.TryResolveMember's emit-side peel so both sides resolve to the same
        // IProperty/serializer for this leaf (EF-402). Without this peel, `memberExpression.Member` below is the
        // "Value" PropertyInfo itself — never a real mapped property on any entity — so FindProperty always
        // misses and the caller falls through to a raw alias read that discards the property's value converter.
        if (expression is MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } valueReceiver }
            && Nullable.GetUnderlyingType(valueReceiver.Type) is not null)
        {
            expression = valueReceiver.RemoveConvert();
        }

        if (expression is MemberExpression memberExpression)
        {
            var source = TryResolveFieldAccessSource(memberExpression.Expression);
            var property = source.EntityType?.FindProperty(memberExpression.Member);
            return property != null
                ? new ResolvedFieldAccess(property, source.DocumentExpression, null, memberExpression.Member)
                : new ResolvedFieldAccess(null, source.DocumentExpression, memberExpression.Member.Name, memberExpression.Member);
        }

        if (expression is MethodCallExpression methodCall)
        {
            if (methodCall.Method.IsEFPropertyMethod()
                && methodCall.Arguments[1] is ConstantExpression { Value: string propertyName })
            {
                var source = TryResolveFieldAccessSource(methodCall.Arguments[0]);
                var property = source.EntityType?.FindProperty(propertyName);
                return property != null
                    ? new ResolvedFieldAccess(property, source.DocumentExpression, null, null)
                    : new ResolvedFieldAccess(null, source.DocumentExpression, propertyName, null);
            }

            if (methodCall.Method.IsGenericMethod
                && methodCall.Method.GetGenericMethodDefinition() == MqlFieldMethodInfo
                && methodCall.Arguments[1] is ConstantExpression { Value: string fieldName })
            {
                var source = TryResolveFieldAccessSource(methodCall.Arguments[0]);
                return new ResolvedFieldAccess(null, source.DocumentExpression, fieldName, null);
            }
        }

        return new ResolvedFieldAccess(null, null, null, null);
    }

    private (IEntityType? EntityType, Expression? DocumentExpression) TryResolveFieldAccessSource(Expression? expression)
    {
        expression = expression.RemoveConvert();

        if (expression is EntityTypedExpression entityTypedExpression)
        {
            return (entityTypedExpression.EntityType, entityTypedExpression);
        }

        if (expression is RootReferenceExpression rootReferenceExpression)
        {
            return (rootReferenceExpression.EntityType, rootReferenceExpression);
        }

        if (expression is ObjectAccessExpression objectAccessExpression)
        {
            return (objectAccessExpression.Navigation?.TargetEntityType ?? objectAccessExpression.EntityType!, objectAccessExpression);
        }

        // When a projection lambda does `new { o.Prop }`, EF stores MemberExpr(STS_root, "Prop")
        // in the projection mapping. Recognise the root entity's shaper here so we can resolve
        // the property and use its own BSON element name instead of the projected alias.
        if (expression is StructuralTypeShaperExpression { StructuralType: IEntityType shaperEntityType })
        {
            if (shaperEntityType == _rootEntityType)
            {
                return (shaperEntityType, DocParameter);
            }

            // A non-root entity shaper in a join query is the inner side of the join — e.g. reading a
            // scalar or shadow property off the joined entity via `o.Prop` or `EF.Property(o, "Prop")`
            // in a mixed projection. Resolve the read against the joined sub-document rather than the
            // query root (which has no such top-level field and would otherwise materialise the value
            // as null — EF-352). Which sub-document depends on the emitted join shape:
            //   * driver-native join   -> the lone joined reference is nested under "_inner";
            //   * flat $lookup+$unwind -> each join lands in its own root-level "_lookup_<Navigation>".
            if (_queryExpression.IsJoinQuery)
            {
                if (_queryExpression.UsesDriverJoinFields)
                {
                    var innerDocument = CreateGetValueExpression(DocParameter, "_inner", false, typeof(BsonDocument));
                    return (shaperEntityType, innerDocument);
                }

                var joinField = _queryExpression.GetPendingLookups()
                    .FirstOrDefault(l => l.TargetEntityType == shaperEntityType)?.As;
                if (joinField != null)
                {
                    var lookupDocument = CreateGetValueExpression(DocParameter, joinField, false, typeof(BsonDocument));
                    return (shaperEntityType, lookupDocument);
                }
            }
        }

        if (expression is ParameterExpression parameterExpression)
        {
            var entityType = _rootEntityType.Model.GetEntityTypes()
                .FirstOrDefault(e => e.ClrType == parameterExpression.Type);
            if (entityType != null)
            {
                return (entityType, parameterExpression);
            }
        }

        if (expression != null && _ownerMappings.TryGetValue(expression, out var ownerInfo))
        {
            return (ownerInfo.EntityType, ownerInfo.BsonDocExpression);
        }

        // Owned navigations aren't joins, so a dotted hop like `b.Home.City` has no ObjectAccessExpression
        // wrapping "Home" — just a plain MemberExpression or an EF.Property call. Recurse to resolve it,
        // so multi-level hops (`b.Home.Inner.City`) don't fall back to a leaf lookup on the wrong document.
        if (expression is MemberExpression navMemberExpression)
        {
            var navSource = TryResolveFieldAccessSource(navMemberExpression.Expression);
            var embeddedNavDocument = TryResolveEmbeddedNavigationDocument(navSource, navSource.EntityType?.FindNavigation(navMemberExpression.Member));
            if (embeddedNavDocument != null)
            {
                return embeddedNavDocument.Value;
            }
        }

        if (expression is MethodCallExpression navPropertyCall
            && navPropertyCall.Method.IsEFPropertyMethod()
            && navPropertyCall.Arguments[1] is ConstantExpression { Value: string navPropertyName })
        {
            var navSource = TryResolveFieldAccessSource(navPropertyCall.Arguments[0]);
            var embeddedNavDocument = TryResolveEmbeddedNavigationDocument(navSource, navSource.EntityType?.FindNavigation(navPropertyName));
            if (embeddedNavDocument != null)
            {
                return embeddedNavDocument.Value;
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Builds the nested-document read for an embedded (owned) *reference* navigation hop, or
    /// <see langword="null"/> to let callers fall through to other resolution paths. Collection
    /// navigations are excluded: their container is a BSON array, not a <see cref="BsonDocument"/>.
    /// </summary>
    private (IEntityType EntityType, Expression DocumentExpression)? TryResolveEmbeddedNavigationDocument(
        (IEntityType? EntityType, Expression? DocumentExpression) navSource, INavigation? navigation)
    {
        if (navigation == null || navigation.IsCollection || !navigation.IsEmbedded() || navSource.DocumentExpression == null)
        {
            return null;
        }

        var elementName = navigation.TargetEntityType.GetContainingElementName() ?? navigation.Name;
        var navDocumentExpression =
            CreateGetValueExpression(navSource.DocumentExpression, elementName, false, typeof(BsonDocument));
        return (navigation.TargetEntityType, navDocumentExpression);
    }

    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Mql.Field) && m.GetParameters().Length == 3);

    protected readonly record struct ResolvedFieldAccess(
        IProperty? Property,
        Expression? DocumentExpression,
        string? FieldName,
        MemberInfo? MemberInfo);

    private string? FindProjectionAlias(Expression expression)
        => _queryExpression.Projection.FirstOrDefault(p => p.Expression != null && p.Expression.Equals(expression))?.Alias;

    private BlockExpression AddIncludes(BlockExpression shaperBlock)
    {
        if (_pendingIncludes.Count == 0)
        {
            return shaperBlock;
        }

        List<Expression> shaperExpressions = [..shaperBlock.Expressions];
        var instanceVariable = shaperExpressions[^1];
        shaperExpressions.RemoveAt(shaperExpressions.Count - 1);

        var includesToProcess = _pendingIncludes;
        _pendingIncludes = [];

        foreach (var include in includesToProcess)
        {
            AddInclude(shaperExpressions, include, shaperBlock, instanceVariable);
        }

        shaperExpressions.Add(instanceVariable);
        shaperBlock = shaperBlock.Update(shaperBlock.Variables, shaperExpressions);
        return shaperBlock;
    }

    private void AddInclude(
        List<Expression> shaperExpressions,
        IncludeExpression includeExpression,
        BlockExpression shaperBlock,
        Expression instanceVariable)
    {
        var navigation = (INavigation)includeExpression.Navigation;

        // Include on a keyless entity is not supported: there is no primary key to resolve the
        // $lookup join, and keyless entities are never tracked (no InternalEntityEntry is emitted).
        if (navigation.DeclaringEntityType.FindPrimaryKey() == null)
        {
            throw new InvalidOperationException(CoreStrings.TranslationFailed(includeExpression.Print()));
        }

        var includeMethod = navigation.IsCollection ? IncludeCollectionMethodInfo : IncludeReferenceMethodInfo;
        var includingClrType = navigation.DeclaringEntityType.ClrType;
        var relatedEntityClrType = navigation.TargetEntityType.ClrType;
#pragma warning disable EF1001 // Internal EF Core API usage.
        Expression entityEntryVariable = _trackQueryResults
            ? shaperBlock.Variables.Single(v => v.Type == typeof(InternalEntityEntry))
            : Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        var concreteEntityTypeVariable = shaperBlock.Variables.Single(v => v.Type == typeof(IEntityType));
        var inverseNavigation = navigation.Inverse;
        var fixup = GenerateFixup(
            includingClrType, relatedEntityClrType, navigation, inverseNavigation!);

        var navigationExpression = Visit(includeExpression.NavigationExpression);

        shaperExpressions.Add(
            Expression.IfThen(
                Expression.Call(
                    Expression.Constant(navigation.DeclaringEntityType, typeof(IReadOnlyEntityType)),
                    IsAssignableFromMethodInfo,
                    Expression.Convert(concreteEntityTypeVariable, typeof(IReadOnlyEntityType))),
                Expression.Call(
                    includeMethod.MakeGenericMethod(includingClrType, relatedEntityClrType),
                    entityEntryVariable,
                    instanceVariable,
                    concreteEntityTypeVariable,
                    navigationExpression,
                    Expression.Constant(navigation),
                    Expression.Constant(inverseNavigation, typeof(INavigation)),
                    Expression.Constant(fixup),
#pragma warning disable EF1001 // Internal EF Core API usage.
                    Expression.Constant(includeExpression.SetLoaded))));
#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    private static readonly MethodInfo IncludeReferenceMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeReference))!;

    private static void IncludeReference<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object entity,
        IEntityType entityType,
        TIncludedEntity relatedEntity,
        INavigation navigation,
        INavigation inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool __)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);
            if (relatedEntity != null)
            {
                fixup(includingEntity, relatedEntity);
                if (inverseNavigation != null
                    && !inverseNavigation.IsCollection)
                {
                    inverseNavigation.SetIsLoadedWhenNoTracking(relatedEntity);
                }
            }
        }
        // For non-null relatedEntity StateManager will set the flag
        else if (relatedEntity == null)
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeCollection))!;

    private static void IncludeCollection<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry? entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object? entity,
        IEntityType entityType,
        IEnumerable<TIncludedEntity>? relatedEntities,
        INavigation navigation,
        INavigation inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool setLoaded)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);

            if (relatedEntities != null)
            {
                foreach (var relatedEntity in relatedEntities)
                {
                    fixup(includingEntity, relatedEntity);
                    inverseNavigation?.SetIsLoadedWhenNoTracking(relatedEntity!);
                }
            }
        }
        else
        {
            if (setLoaded)
            {
#pragma warning disable EF1001 // Internal EF Core API usage.
                entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
            }

            if (relatedEntities != null)
            {
                using var enumerator = relatedEntities.GetEnumerator();
                while (enumerator.MoveNext())
                {
                }
            }
        }

        // Ensure empty collections still initialize a new CLR object for them
        if (relatedEntities != null && !navigation.IsShadowProperty())
        {
            navigation.GetCollectionAccessor()!.GetOrCreate(entity, forMaterialization: true);
        }
    }

    private static Delegate GenerateFixup(
        Type entityType,
        Type relatedEntityType,
        INavigation navigation,
        INavigation inverseNavigation)
    {
        var entityParameter = Expression.Parameter(entityType);
        var relatedEntityParameter = Expression.Parameter(relatedEntityType);
        List<Expression> expressions =
        [
            navigation.IsCollection
                ? AddToCollectionNavigation(entityParameter, relatedEntityParameter, navigation)
                : AssignReferenceNavigation(entityParameter, relatedEntityParameter, navigation)
        ];

        if (inverseNavigation != null)
        {
            expressions.Add(
                inverseNavigation.IsCollection
                    ? AddToCollectionNavigation(relatedEntityParameter, entityParameter, inverseNavigation)
                    : AssignReferenceNavigation(relatedEntityParameter, entityParameter, inverseNavigation));
        }

        return Expression.Lambda(Expression.Block(typeof(void), expressions), entityParameter, relatedEntityParameter)
            .Compile();
    }

    private static Expression AssignReferenceNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => entity.MakeMemberAccess(navigation.GetMemberInfo(true, true)).Assign(relatedEntity);

    private static Expression AddToCollectionNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => Expression.Call(
            Expression.Constant(navigation.GetCollectionAccessor()),
            CollectionAccessorAddMethodInfo,
            entity,
            relatedEntity,
            Expression.Constant(true));

    private static readonly MethodInfo PopulateCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(PopulateCollection))!;

    private static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(nameof(IReadOnlyEntityType.IsAssignableFrom), [
            typeof(IReadOnlyEntityType)
        ])!;

    private static TCollection PopulateCollection<TEntity, TCollection>(
        IClrCollectionAccessor accessor,
        string declaringEntityTypeDisplayName,
        string navigationName,
        IEnumerable<TEntity> entities)
    {
        var created = accessor.Create();
        if (created is not ICollection<TEntity> collection)
        {
            throw new InvalidOperationException(
                $"The collection navigation '{declaringEntityTypeDisplayName}.{navigationName}' is typed as "
                + $"'{created.GetType().ShortDisplayName()}', which does not implement "
                + $"'{typeof(ICollection<TEntity>).ShortDisplayName()}'. Collection navigations must be backed "
                + "by a type that implements ICollection<T> so the provider can populate it during materialization.");
        }

        foreach (var entity in entities)
        {
            collection.Add(entity);
        }

        return (TCollection)collection;
    }

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

    private int GetProjectionIndex(ProjectionBindingExpression projectionBindingExpression)
        => projectionBindingExpression.ProjectionMember != null
            ? _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember).GetConstantValue<int>()
            : projectionBindingExpression.Index
              ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");
}
