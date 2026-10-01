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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;
using MongoDB.EntityFrameworkCore.Serializers;
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

    /// <summary>
    /// True when an owned entity's owner key may legitimately be absent from the shaped document, so a missing key
    /// reads as a non-null placeholder instead of throwing. See
    /// <see cref="MongoMixedProjectionBindingRemovingExpressionVisitor"/>'s retained-Select case.
    /// </summary>
    protected virtual bool OwnerKeyMayBeAbsent => false;

    /// <summary>Whether <paramref name="alias"/> is a native whole-root-entity ("$$ROOT") leaf.</summary>
    private bool IsWholeRootEntityAlias(string? alias)
        => alias != null
           && _queryExpression.Select.Projection.Any(
               p => p.Alias == alias
                    && p.Expression is MongoElementRefExpression
                    {
                        Path: MongoElementRefExpression.WholeRootDocumentPath
                    });

    /// <summary>
    /// Whether <paramref name="alias"/> names a reference-collection-nav <c>First</c> leaf that must throw
    /// <see cref="InvalidOperationException"/> when the navigation matched no row. Looked up by alias against
    /// <see cref="MongoQueryExpression.CorrelatedReducerLeaves"/>, keyed on
    /// <see cref="MongoCorrelatedReducerLeaf.ThrowOnEmpty"/>, so no other leaf kind is affected.
    /// </summary>
    private bool TryGetThrowOnEmptyCorrelatedReducerLeaf(string? alias, out MongoCorrelatedReducerLeaf? leaf)
    {
        leaf = null;
        if (alias is null)
        {
            return false;
        }

        foreach (var candidate in _queryExpression.CorrelatedReducerLeaves)
        {
            if (candidate.Alias == alias && candidate.ThrowOnEmpty)
            {
                leaf = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="alias"/> names a reference-collection-nav <c>FirstOrDefault</c> leaf whose reduced
    /// member is a non-nullable value type. With no matching row the alias is missing, and
    /// <see cref="BsonBinding.GetElementValue{T}"/> throws for a non-nullable <c>T</c> instead of yielding
    /// <c>default(T)</c> (see <c>NativeCorrelatedReducerProjectionTests</c>). Nullable members already read as
    /// null. Mutually exclusive with <see cref="TryGetThrowOnEmptyCorrelatedReducerLeaf"/>.
    /// </summary>
    private bool IsDefaultOnEmptyCorrelatedReducerLeaf(string? alias, Type leafType)
    {
        if (alias is null || !leafType.IsValueType || Nullable.GetUnderlyingType(leafType) is not null)
        {
            return false;
        }

        foreach (var candidate in _queryExpression.CorrelatedReducerLeaves)
        {
            if (candidate.Alias == alias && !candidate.ThrowOnEmpty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="document"/> lacks <paramref name="elementName"/> or it is BSON null. A left-outer
    /// <c>$unwind</c> over an unmatched <c>$lookup</c> leaves the lookup field null, and a dotted read through it
    /// is missing in <c>$project</c>; either means "no related row".
    /// </summary>
    internal static bool IsElementAbsentOrNull(BsonDocument document, string elementName)
        => !document.TryGetValue(elementName, out var value) || value.IsBsonNull;

    private static readonly MethodInfo IsElementAbsentOrNullMethodInfo =
        typeof(MongoProjectionBindingRemovingExpressionVisitor)
            .GetMethod(nameof(IsElementAbsentOrNull), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConstructorInfo SequenceContainsNoElementsConstructorInfo =
        typeof(InvalidOperationException).GetConstructor([typeof(string)])!;

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            // The alias holds the whole computed value (native $project, or a driver-LINQ push-down of the same
            // Select), so read it once instead of re-applying the call over it. The mixed visitor overrides this
            // to evaluate ClientExpression over whole documents.
            case NativeComputedLeafExpression computedLeaf:
                {
                    var computedProjection = GetProjection(computedLeaf.Binding);
                    return TryCreateThrowOnNullAliasRead(computedProjection.Alias!, computedLeaf.Type, out var throwingComputedRead)
                        ? throwingComputedRead
                        : BsonBinding.CreateGetElementValue(DocParameter, computedProjection.Alias!, computedLeaf.Type);
                }

            case ProjectionBindingExpression projectionBindingExpression:
                {
                    var projection = GetProjection(projectionBindingExpression);

                    // Alias is null when the projection's expression has no natural name —
                    // the BsonDoc itself is the value.
                    if (projection.Alias is null)
                    {
                        return DocParameter;
                    }

                    // A constructed sub-entity leaf (`new { Book = new Book { Id = e.Id } }`): rebuild the object
                    // from members nested under this leaf's $project alias ("Book.Id"). The mixed visitor overrides
                    // ReadDocumentConstructionMember to read natural paths off whole documents instead.
                    if (projection.Expression is MongoDocumentConstructionExpression construction)
                    {
                        return BuildDocumentConstructionExpression(construction, projection.Alias);
                    }

                    // A null-propagated scalar behind a non-nullable type (`x.S.Length`, `(long)x.S.Length`,
                    // `x.S.Length + 5`, possibly over an unmatched left-outer side): read as T? and throw EF's
                    // "Nullable object must have a value." on null. Precedes every other arm, since a Convert or
                    // arithmetic leaf would otherwise read the null as 0.
                    if (TryCreateThrowOnNullAliasRead(projection.Alias, projectionBindingExpression.Type, out var throwingRead))
                    {
                        return throwingRead;
                    }

                    // FirstOrDefault over a non-nullable value-type member: with no related row the alias is missing
                    // and a raw read throws, so emit default(T) explicitly for absent/null.
                    //
                    // Must precede the numeric-cast branch: the nullable-widened shape
                    // (`Convert(nav.Select(m => Convert(m.Rank, int?)).FirstOrDefault(), int)`) is registered as a
                    // Convert node, which that branch would otherwise claim. Keyed by alias, so no other leaf matches.
                    if (IsDefaultOnEmptyCorrelatedReducerLeaf(projection.Alias, projectionBindingExpression.Type))
                    {
                        return Expression.Condition(
                            Expression.Call(
                                IsElementAbsentOrNullMethodInfo, DocParameter, Expression.Constant(projection.Alias)),
                            Expression.Default(projectionBindingExpression.Type),
                            CreateAliasRead(projection.Alias, projectionBindingExpression.Type));
                    }

                    // A native numeric-cast leaf (`new { X = (int)x.D }`) holds the converted value under its alias.
                    // TryResolveFieldAccess would strip the Convert and read via the pre-cast property's serializer, so
                    // use the raw alias read like other computed leaves. Safe because the emit side
                    // (AllFieldsDefaultSerialized) never admits a cast over a converted/non-default-representation
                    // field. A nullable cast over a Local-kind DateTime still reads with that kind (CreateAliasRead).
                    if (projection.Expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked })
                    {
                        return CreateAliasRead(projection.Alias, projectionBindingExpression.Type);
                    }

                    // Native string-to-char-sequence leaf: only the raw string was pushed down (BSON has no
                    // char-sequence form) and the whole AsEnumerable/ToList/ToArray call was registered. Read the
                    // string through the property-aware path (so converters apply) and re-apply the call to it.
                    // The mixed shaper's counterpart is TryBindStringSequenceLeaf.
                    if (projection.Expression is MethodCallExpression stringSequenceCall
                        && NativeProjectionBinder.IsStringSequenceMaterializationCall(stringSequenceCall)
                        && TryResolveFieldAccess(stringSequenceCall.Arguments[0]).Property is { } stringSequenceProperty)
                    {
                        // The emit side admits this leaf only over a string-typed source, so anything else means
                        // the resolver and emit side disagree about the member.
                        Debug.Assert(stringSequenceProperty.ClrType == typeof(string),
                            $"String-sequence projection leaf resolved to non-string property "
                            + $"'{stringSequenceProperty.Name}' of type '{stringSequenceProperty.ClrType}'.");

                        var rawStringRead = BsonBinding.CreateGetValueExpression(
                            DocParameter, projection.Alias, stringSequenceProperty, typeof(string));
                        return Expression.Call(stringSequenceCall.Method, rawStringRead);
                    }

                    // Resolve the source IProperty so its serializer/nullability apply, not whatever EF property
                    // shares the alias name on the root entity. TryResolveFieldAccess unwraps Convert nodes, but a
                    // Convert-introducing alias (`new { X = (long)p.intProp }`) goes through the LINQ V3 push-down
                    // (ProjectionAnalyzer.CanPushDown) or is intercepted above, so the binding type equals the
                    // property type here; the assert below pins that so a violation fails loudly instead of
                    // mis-deserialising via the wrong serializer.
                    //
                    // Nullability may differ in either direction (nullable binding over a non-nullable property, or
                    // the Nullable<T>.Value peel: `x.Converted.Value` as `int` over `int?`); unwrap both sides.
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
                        return valueExpression.ConvertIfRequired(projectionBindingExpression.Type);
                    }

                    // Reference-collection-nav First().Member: must throw on an empty source like Enumerable.First().
                    // Only the alias survives $project, and it is missing/null exactly when no related row matched.
                    // A matched row with a null member can't be confused with that because
                    // NativeProjectionBinder.TryGetCorrelatedReducerLeaf declines First() over nullable members.
                    if (TryGetThrowOnEmptyCorrelatedReducerLeaf(projection.Alias, out _))
                    {
                        return Expression.Condition(
                            Expression.Call(
                                IsElementAbsentOrNullMethodInfo, DocParameter, Expression.Constant(projection.Alias)),
                            Expression.Throw(
                                Expression.New(
                                    SequenceContainsNoElementsConstructorInfo,
                                    Expression.Constant("Sequence contains no elements")),
                                projectionBindingExpression.Type),
                            CreateAliasRead(projection.Alias, projectionBindingExpression.Type));
                    }

                    // Non-property expressions (arithmetic, constants, Mql.Field) and key-property bindings carry
                    // the value under the projection alias as the BSON element name (e.g. `{ OrderID: "$_id" }`),
                    // so read it raw by alias. A GroupBy key, $min/$max output or join-scope leaf over a Local-kind
                    // DateTime reads with that kind (CreateAliasRead).
                    return CreateAliasRead(projection.Alias, projectionBindingExpression.Type);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    IArrayProjectionExpression arrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            // ObjectArrayProjectionExpression and ArrayAliasProjectionExpression are both
                            // admissible; anything else is a translation failure, not a cast crash.
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
                        // Nested collection inside a parent collection item (ThenInclude on collection-then-collection):
                        // read the BsonArray from the parent document by its document-path field name.
                        //
                        // An alias-addressed array (null ArrayFieldName) can't reach here: alias-addressed arrays are
                        // only top-level native projection leaves. The `?? throw` makes a violation loud instead of a
                        // bare KeyNotFoundException from the _projectionBindings lookup.
                        var arrayFieldName = arrayProjection.ArrayFieldName
                                             ?? throw new InvalidOperationException(
                                                 CoreStrings.TranslationFailed(extensionExpression.Print()));
                        var parentAccess = arrayProjection.AccessExpression;
                        var parentDoc = _projectionBindings[parentAccess];
                        bsonArrayExpression = BsonBinding.CreateGetBsonArray(parentDoc, arrayFieldName);
                        arrayName = arrayFieldName;
                    }

                    // Normalize a missing or BSON-null stored array to an empty BsonArray, so the collection is empty
                    // rather than null (EF Core's contract; otherwise it depends on the POCO's initializer).
                    //
                    // Must stay here at the point of use: VisitBinary hard-casts assignment RHS to UnaryExpression
                    // (see the contract note there), so a Coalesce at the assignment would throw in every mode.
                    // TypeAs is null for absent and non-array elements alike.
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

                    // Contract with BsonDocumentInjectingExpressionVisitor: the RHS is always an Expression.TypeAs.
                    // Changing that node shape requires widening this cast in the same change, or every entity and
                    // collection shaper throws InvalidCastException.
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

                        // For an alias-addressed array fieldName was already set from projection.Alias above. No
                        // current IArrayProjectionExpression reaches the throw; it guards a future node kind or route.
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
                                // Cross-collection Include result: under "_inner" in driver-native LeftJoin mode, or
                                // its own "_lookup_<Navigation>" field in flat $lookup + $unwind mode. Derived from
                                // the projection node (navigation + UsesDriverJoinFields), never mutable global state.
                                // Nested under a collection Include, the parent RootReference is bound to the
                                // per-element document, so read the joined field from that, not the query root.
                                innerAccessExpression = GetCrossCollectionRootDocument(crossCollectionAccess);
                                fieldName = GetCrossCollectionFieldName(crossCollectionAccess);
                                // fieldRequired = false also makes a native LeftJoin's unmatched Inner row read as a
                                // null reference navigation: the joined field is absent for such a row, and
                                // requiring it would throw.
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
                                // On the fallback route documents are whole and un-projected, so "$$ROOT" resolves
                                // to the document itself rather than a non-existent field.
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

    // A non-null stand-in for an absent owner key: default for a non-nullable value type, "" for string. Other key
    // types have no safe stand-in, so the read stays required.
    private static bool TryGetOwnerKeyPlaceholder(Type keyType, out object placeholder)
    {
        if (keyType == typeof(string))
        {
            placeholder = string.Empty;
            return true;
        }

        if (keyType.IsValueType && Nullable.GetUnderlyingType(keyType) == null)
        {
            placeholder = Activator.CreateInstance(keyType)!;
            return true;
        }

        placeholder = null!;
        return false;
    }

    protected Expression CreateGetValueExpression(
        Expression docExpression,
        IProperty property,
        Type type)
    {
        if (property.IsOwnedTypeKey())
        {
            var entityType = (IReadOnlyEntityType)property.DeclaringType;

            // Re-rooted whole-element owned SelectMany ($replaceRoot; see MongoShapedQueryCompilingExpressionVisitor):
            // the owner key and ordinal were merged in under sentinel fields so the owned key materializes non-null.
            // entityType == _rootEntityType only in this case, since the query root is always a document root.
            if (entityType == _rootEntityType)
            {
                // The sentinels are nested under the ShadowField wrapper, so this is a two-segment path read;
                // CreateGetElementValue would treat "__mongoef_shadow.__ownerKey" as one literal key.
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
                    return _ordinalMappings[docExpression].ConvertIfRequired(type);
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
                        // The principal is the owner's own stored key (not another owned key further up the chain,
                        // which recurses and applies this check at the root). Only a shadow owned key: a CLR-mapped
                        // one would surface the placeholder.
                        if (OwnerKeyMayBeAbsent
                            && property.IsShadowProperty()
                            && !principalProperty.IsOwnedTypeKey()
                            && TryGetOwnerKeyPlaceholder(principalProperty.ClrType, out var placeholder))
                        {
                            var ownerDocument = CreateGetValueExpression(
                                ownerBsonDocExpression, (string?)null, false, typeof(BsonDocument));
                            Expression keyRead = BsonBinding.CreateGetPropertyValueOrPlaceholder(
                                ownerDocument, principalProperty, principalProperty.ClrType, placeholder);
                            return keyRead.ConvertIfRequired(type);
                        }

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
        // Redirect to a bound element document only when the joined reference is nested under a collection Include
        // (parent RootReference is for an entity other than the query root, and its element document is bound).
        // Top-level Includes and driver-native LeftJoin chains read from the absolute root.
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
    protected ProjectionExpression GetProjection(ProjectionBindingExpression projectionBindingExpression)
        => _queryExpression.Projection[GetProjectionIndex(projectionBindingExpression)];

    /// <summary>
    /// Rebuilds the CLR object a <see cref="MongoDocumentConstructionExpression"/> leaf represents (a
    /// <see cref="MemberInitExpression"/> or a <see cref="NewExpression"/> with named members), reading each
    /// member via <see cref="ReadDocumentConstructionMember"/>.
    /// </summary>
    protected Expression BuildDocumentConstructionExpression(MongoDocumentConstructionExpression construction, string alias)
        => BuildDocumentConstructionExpression(construction, [alias]);

    /// <summary>
    /// Path-aware overload for a construction nested inside another. <paramref name="path"/> runs from the
    /// document root through this construction's own alias; nested constructions render as real sub-documents,
    /// so reads must walk every level rather than a two-segment <c>[alias, memberName]</c> path.
    /// </summary>
    private Expression BuildDocumentConstructionExpression(MongoDocumentConstructionExpression construction, string[] path)
    {
        switch (construction.OriginalExpression)
        {
            case MemberInitExpression memberInit:
                {
                    var bindings = new MemberBinding[construction.Members.Count];
                    for (var i = 0; i < construction.Members.Count; i++)
                    {
                        var (memberName, value) = construction.Members[i];
                        var member = memberInit.Bindings.First(b => b.Member.Name == memberName).Member;
                        bindings[i] = Expression.Bind(
                            member, ReadDocumentConstructionMemberTyped(construction, path, memberName, value, member));
                    }

                    return Expression.MemberInit(memberInit.NewExpression, bindings);
                }

            case NewExpression { Members: not null } newExpression:
                {
                    var arguments = new Expression[construction.Members.Count];
                    for (var i = 0; i < construction.Members.Count; i++)
                    {
                        var (memberName, value) = construction.Members[i];
                        arguments[i] = ReadDocumentConstructionMemberTyped(
                            construction, path, memberName, value, newExpression.Members[i]);
                    }

                    return Expression.New(newExpression.Constructor!, arguments, newExpression.Members);
                }

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(construction.OriginalExpression.Print()));
        }
    }

    private Expression ReadDocumentConstructionMemberTyped(
        MongoDocumentConstructionExpression construction, string[] path, string memberName, MongoExpression value,
        MemberInfo member)
    {
        var memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo fieldInfo => fieldInfo.FieldType,
            _ => value is MongoFieldExpression fieldForType ? fieldForType.Property.ClrType : value.Type
        };

        // A plain field uses its own IProperty/serializer; it only occurs at depth 1, so `path[0]` is the alias.
        // A nested construction recurses with this member's name appended.
        //
        // Anything else (accumulator output, g.Key reference, constant) reads at the value's own translated type,
        // then converts to the declared member type. Reading directly at a declared `object` type (e.g.
        // Odata_groupby_empty_key's `Value`) would yield a raw Decimal128 instead of a decimal.
        Expression read = value switch
        {
            MongoFieldExpression field => ReadDocumentConstructionMember(construction, path[0], memberName, field, memberType),
            MongoDocumentConstructionExpression nested => BuildDocumentConstructionExpression(nested, [..path, memberName]),
            _ => ReadDocumentConstructionMemberGeneric(construction, path, memberName, value.Type)
        };

        return read.ConvertIfRequired(memberType);
    }

    /// <summary>
    /// Reads a member with no single backing <see cref="IProperty"/> (accumulator output, g.Key reference, computed
    /// or constant value) by element path. <paramref name="readType"/> is the value's own translated type, not the
    /// possibly-widened declared type.
    /// </summary>
    /// <remarks>
    /// A member holding a Local-kind DateTime property's unchanged value (a <c>g.Key</c> or <c>$max</c> read) reads with
    /// that kind; see <see cref="NativeDateTimeKindReadBack"/>.
    /// </remarks>
    protected virtual Expression ReadDocumentConstructionMemberGeneric(
        MongoDocumentConstructionExpression construction, string[] path, string memberName, Type readType)
        => BsonBinding.CreateGetElementValueAtPath(DocParameter, [..path, memberName], readType,
            FindDateTimeKindSource(construction, memberName, readType));

    // The Local-kind property a construction member's value passes through unchanged, if any.
    private IReadOnlyProperty? FindDateTimeKindSource(
        MongoDocumentConstructionExpression construction, string memberName, Type readType)
    {
        if (readType.UnwrapNullableType() != typeof(DateTime))
            return null;

        foreach (var (name, value) in construction.Members)
        {
            if (name == memberName)
                return NativeDateTimeKindReadBack.FindForProjectionValue(_queryExpression.Select, value);
        }

        return null;
    }

    /// <summary>
    /// When the native emit side flagged the output under <paramref name="alias"/>
    /// <see cref="MongoProjection.ThrowsOnNull"/> (<see cref="MongoSelectDefinition.FindThrowOnNullProjection"/>), reads
    /// it as a nullable and takes <see cref="Nullable{T}.Value"/>, so a null (or missing) value throws EF's
    /// "Nullable object must have a value." instead of reading as <c>default(T)</c>.
    /// </summary>
    /// <remarks>
    /// The value type is <paramref name="type"/> when that is a non-nullable value type (a widened
    /// <c>(long)x.S.Length</c> reads the server's int as long); otherwise (a boxed <see cref="object"/> read) it is the
    /// staged expression's own type, and the value is converted back to <paramref name="type"/>.
    /// </remarks>
    private bool TryCreateThrowOnNullAliasRead(string alias, Type type, [NotNullWhen(true)] out Expression? read)
    {
        // A nullable read of the alias wants the null itself (never produced for a flagged leaf today: the flag is set
        // only for a non-nullable read, and a Distinct or set op keeps the leaf's type).
        if (Nullable.GetUnderlyingType(type) is not null
            || _queryExpression.Select.FindThrowOnNullProjection(alias) is not { } flagged)
        {
            read = null;
            return false;
        }

        var valueType = type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? type
            : flagged.Expression.Type.UnwrapNullableType();
        Expression value = Expression.Property(
            CreateAliasRead(alias, valueType.MakeNullable()), valueType.MakeNullable().GetProperty(nameof(Nullable<int>.Value))!);
        read = value.ConvertIfRequired(type);
        return true;
    }

    /// <summary>
    /// Reads a projection output by alias through the generic serializer for <paramref name="type"/>. When the alias
    /// holds the unchanged value of a Local-kind DateTime property (a GroupBy key, a <c>$min</c>/<c>$max</c>, a cast or
    /// join-scope leaf), the value reads back with that kind, as the property's own serializer would; see
    /// <see cref="NativeDateTimeKindReadBack"/>.
    /// </summary>
    /// <remarks>
    /// A native <c>DateTime.TimeOfDay</c> leaf (<see cref="MongoSelectDefinition.IsTimeOfDayProjection"/>) holds
    /// milliseconds since midnight, read through <see cref="BsonSerializerFactory.TimeOfDayMillisecondsSerializer"/>.
    /// </remarks>
    private Expression CreateAliasRead(string alias, Type type)
        => type.UnwrapNullableType() == typeof(TimeSpan) && _queryExpression.Select.IsTimeOfDayProjection(alias)
            ? BsonBinding.CreateGetElementValue(DocParameter, alias, type, BsonSerializerFactory.CreateTimeOfDaySerializer(type))
            : type.UnwrapNullableType() == typeof(float)
                ? BsonBinding.CreateGetElementValue(DocParameter, alias, type, BsonBinding.CreateNarrowingFloatSerializer(type))
                : BsonBinding.CreateGetElementValue(
                    DocParameter,
                    alias,
                    type,
                    type.UnwrapNullableType() == typeof(DateTime)
                        ? NativeDateTimeKindReadBack.FindForProjectionAlias(_queryExpression.Select, alias)
                        : null);

    /// <summary>
    /// Reads one member of a <see cref="MongoDocumentConstructionExpression"/> leaf. The native implementation
    /// reads <c>"{alias}.{memberName}"</c>, where the native <c>$project</c> put it;
    /// <see cref="MongoMixedProjectionBindingRemovingExpressionVisitor"/> overrides it to read
    /// <paramref name="field"/>'s natural path off whole, un-projected documents.
    /// </summary>
    protected virtual Expression ReadDocumentConstructionMember(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
        Type memberType)
        => BsonBinding.CreateGetPropertyValueAtPath(DocParameter, [alias, memberName], field.Property, memberType);

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
                // Cross-collection Include results are at the document root, under "_inner" (lone driver-native
                // reference) or "_lookup_<Navigation>" (flat mode), derived from the projection node.
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

        // Peel Nullable<T>.Value (`x.Score.Value`): the receiver names the stored field. Mirrors
        // MongoExpressionTranslator.TryResolveMember so both sides resolve the same IProperty; otherwise FindProperty
        // misses and the raw alias read drops the value converter.
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

        // For `new { o.Prop }`, EF stores MemberExpr(STS_root, "Prop") in the projection mapping. Recognise the root
        // entity's shaper here to resolve the property and use its own element name instead of the projected alias.
        if (expression is StructuralTypeShaperExpression { StructuralType: IEntityType shaperEntityType })
        {
            if (shaperEntityType == _rootEntityType)
            {
                return (shaperEntityType, DocParameter);
            }

            // A non-root entity shaper in a join query is the join's inner side (e.g. `o.Prop` or
            // `EF.Property(o, "Prop")` in a mixed projection). Read from the joined sub-document, not the query root,
            // which has no such top-level field and would materialise null (EF-352). Which sub-document depends on
            // the join shape:
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

        // Owned navigations aren't joins: a dotted hop like `b.Home.City` is a plain MemberExpression or EF.Property
        // call, not an ObjectAccessExpression. Recurse so multi-level hops don't fall back to a leaf lookup on the
        // wrong document.
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

#pragma warning disable EF1001 // Internal EF Core API usage.
        Expression entityEntryVariable = _trackQueryResults
            ? shaperBlock.Variables.Single(v => v.Type == typeof(InternalEntityEntry))
            : Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        var concreteEntityTypeVariable = shaperBlock.Variables.Single(v => v.Type == typeof(IEntityType));
        var navigationExpression = Visit(includeExpression.NavigationExpression);

        shaperExpressions.Add(
            MongoIncludeFixups.CreateIncludeCall(
                navigation,
                entityEntryVariable,
                instanceVariable,
                concreteEntityTypeVariable,
                navigationExpression,
#pragma warning disable EF1001 // Internal EF Core API usage.
                includeExpression.SetLoaded));
#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    private static readonly MethodInfo PopulateCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(PopulateCollection))!;

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

    private int GetProjectionIndex(ProjectionBindingExpression projectionBindingExpression)
        => projectionBindingExpression.ProjectionMember != null
            ? _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember).GetConstantValue<int>()
            : projectionBindingExpression.Index
              ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");
}
