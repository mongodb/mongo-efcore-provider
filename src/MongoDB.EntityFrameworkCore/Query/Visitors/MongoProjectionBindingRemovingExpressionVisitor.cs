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
    /// True when an owned entity's owner key may legitimately be absent from the shaped document, so a missing key reads
    /// as a non-null placeholder instead of throwing (see the mixed visitor's retained-Select case).
    /// </summary>
    protected virtual bool OwnerKeyMayBeAbsent => false;

    /// <summary>
    /// The projected document key an owned-reference leaf is read from when the driver returns projected documents (a retained
    /// pushed-down <c>Select</c>): the member name, which differs from the navigation's own element name once the leaf is renamed
    /// (<c>new { R = x.O }</c>). <see langword="null"/> keeps the navigation's element name.
    /// </summary>
    protected virtual string? ResolveProjectedOwnedReferenceAlias(INavigation navigation) => null;

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
    /// <see cref="InvalidOperationException"/> when the navigation matched no row (keyed on
    /// <see cref="MongoCorrelatedReducerLeaf.ThrowOnEmpty"/>, so no other leaf kind is affected).
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
    /// Whether <paramref name="alias"/> names a reference-collection-nav <c>FirstOrDefault</c> leaf whose reduced member is
    /// a non-nullable value type. With no matching row the alias is missing and
    /// <see cref="BsonBinding.GetElementValue{T}"/> throws for a non-nullable <c>T</c> instead of yielding
    /// <c>default(T)</c> (see <c>NativeCorrelatedReducerProjectionTests</c>). Mutually exclusive with
    /// <see cref="TryGetThrowOnEmptyCorrelatedReducerLeaf"/>.
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
    /// True when <paramref name="document"/> lacks <paramref name="elementName"/> or it is BSON null: a left-outer
    /// <c>$unwind</c> over an unmatched <c>$lookup</c> leaves null, and a dotted read through it is missing in <c>$project</c>.
    /// </summary>
    internal static bool IsElementAbsentOrNull(BsonDocument document, string elementName)
        => !document.TryGetValue(elementName, out var value) || value.IsBsonNull;

    private static readonly MethodInfo IsElementAbsentOrNullMethodInfo =
        typeof(MongoProjectionBindingRemovingExpressionVisitor)
            .GetMethod(nameof(IsElementAbsentOrNull), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Whether <paramref name="elementName"/> is absent from <paramref name="document"/> (an explicit BSON null is present).
    /// </summary>
    internal static bool IsElementAbsent(BsonDocument document, string elementName)
        => !document.Contains(elementName);

    private static readonly MethodInfo IsElementAbsentMethodInfo =
        typeof(MongoProjectionBindingRemovingExpressionVisitor)
            .GetMethod(nameof(IsElementAbsent), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConstructorInfo SequenceContainsNoElementsConstructorInfo =
        typeof(InvalidOperationException).GetConstructor([typeof(string)])!;

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            // The alias holds the whole computed value (native $project, or a driver-LINQ push-down), so read it once
            // instead of re-applying the call. The mixed visitor overrides this to evaluate ClientExpression.
            case NativeComputedLeafExpression computedLeaf:
                {
                    var computedProjection = GetProjection(computedLeaf.Binding);
                    return TryCreateThrowOnNullAliasRead(computedProjection.Alias!, computedLeaf.Type, out var throwingComputedRead)
                        ? throwingComputedRead
                        : TryCreateThrowOnMalformedNullAliasRead(computedProjection.Alias!, computedLeaf.Type, out var strictComputedRead)
                            ? strictComputedRead
                            : BsonBinding.CreateGetElementValue(DocParameter, computedProjection.Alias!, computedLeaf.Type);
                }

            case ProjectionBindingExpression projectionBindingExpression:
                {
                    var projection = GetProjection(projectionBindingExpression);

                    // Alias is null when the BsonDoc itself is the value.
                    if (projection.Alias is null)
                    {
                        return DocParameter;
                    }

                    // A constructed sub-entity leaf: rebuild the object from members nested under this leaf's alias
                    // ("Book.Id"). The mixed visitor overrides ReadDocumentConstructionMember to read natural paths.
                    if (projection.Expression is MongoDocumentConstructionExpression construction)
                    {
                        return BuildDocumentConstructionExpression(construction, projection.Alias);
                    }

                    // A null-propagated scalar behind a non-nullable type (`x.S.Length + 5`, possibly over an unmatched
                    // left-outer side): read as T? and throw EF's "Nullable object must have a value." on null. Precedes
                    // every other arm, since a Convert or arithmetic leaf would otherwise read the null as 0.
                    if (TryCreateThrowOnNullAliasRead(projection.Alias, projectionBindingExpression.Type, out var throwingRead))
                    {
                        return throwingRead;
                    }

                    // A computed leaf over a non-nullable property (`x.Rank + 1`) is null only when a malformed document
                    // omits the field; read strictly so it throws as driver-LINQ did, not 0.
                    if (TryCreateThrowOnMalformedNullAliasRead(projection.Alias, projectionBindingExpression.Type, out var strictRead))
                    {
                        return strictRead;
                    }

                    // FirstOrDefault over a non-nullable value-type member: with no related row the alias is missing and a raw
                    // read throws, so emit default(T) for absent/null. Must precede the numeric-cast branch, which would claim
                    // the nullable-widened shape (registered as a Convert node). Keyed by alias.
                    if (IsDefaultOnEmptyCorrelatedReducerLeaf(projection.Alias, projectionBindingExpression.Type))
                    {
                        return Expression.Condition(
                            Expression.Call(
                                IsElementAbsentOrNullMethodInfo, DocParameter, Expression.Constant(projection.Alias)),
                            Expression.Default(projectionBindingExpression.Type),
                            CreateAliasRead(projection.Alias, projectionBindingExpression.Type));
                    }

                    // A native numeric-cast leaf holds the converted value under its alias; TryResolveFieldAccess would strip
                    // the Convert and read via the pre-cast property's serializer, so read the raw alias. Safe because the
                    // emit side (AllFieldsDefaultSerialized) never admits a cast over a converted/non-default-representation
                    // field. A nullable cast over a Local-kind DateTime still reads with that kind (CreateAliasRead).
                    if (projection.Expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } castLeaf)
                    {
                        // A relabel cast over a non-default-stored field projected the bare stored field
                        // (NativeProjectionBinder.IsClientRelabelCast): read it through the property's serializer, then cast.
                        if (TryResolveFieldAccess(castLeaf.Operand).Property is { } relabelProperty
                            && NativeProjectionBinder.IsClientRelabelCast(castLeaf, relabelProperty))
                        {
                            var storedRead = BsonBinding.CreateGetValueExpression(
                                DocParameter, projection.Alias, relabelProperty, relabelProperty.ClrType);
                            return Expression.Convert(storedRead, projectionBindingExpression.Type);
                        }

                        return TryCreateRequiredScalarCastRead(castLeaf, projection.Alias, projectionBindingExpression.Type, out var castRead)
                            ? castRead
                            : CreateAliasRead(projection.Alias, projectionBindingExpression.Type);
                    }

                    // Native string-to-char-sequence leaf: only the raw string was pushed down (BSON has no char-sequence
                    // form). Read it property-aware (converters apply) and re-apply the call. Mixed counterpart:
                    // TryBindStringSequenceLeaf.
                    if (projection.Expression is MethodCallExpression stringSequenceCall
                        && NativeProjectionBinder.IsStringSequenceMaterializationCall(stringSequenceCall)
                        && TryResolveFieldAccess(stringSequenceCall.Arguments[0]).Property is { } stringSequenceProperty)
                    {
                        // The emit side admits this leaf only over a string-typed source: otherwise resolver and emit side disagree.
                        Debug.Assert(stringSequenceProperty.ClrType == typeof(string),
                            $"String-sequence projection leaf resolved to non-string property "
                            + $"'{stringSequenceProperty.Name}' of type '{stringSequenceProperty.ClrType}'.");

                        var rawStringRead = BsonBinding.CreateGetValueExpression(
                            DocParameter, projection.Alias, stringSequenceProperty, typeof(string));
                        return Expression.Call(stringSequenceCall.Method, rawStringRead);
                    }

                    // Resolve the source IProperty so its serializer/nullability apply, not whatever EF property shares the
                    // alias name on the root entity. A Convert-introducing alias (`(long)p.intProp`) goes through the LINQ V3
                    // push-down or is intercepted above, so the binding type equals the property type; the assert below pins
                    // that. Nullability may differ in either direction (nullable binding over a non-nullable property, or the
                    // Nullable<T>.Value peel); unwrap both sides.
                    var fieldAccess = TryResolveFieldAccess(projection.Expression);
                    if (fieldAccess.Property != null)
                    {
                        ThrowIfAliasedTypeMismatch(fieldAccess.Property, projectionBindingExpression.Type);

                        // Projection route: a bare stored scalar whose element is missing reads default(T), as the driver's
                        // $project push-down did (D-F10). So does a projected Distinct's flattened key over a default-serialized
                        // property with no missing marker (a bool: its composite `_id` omits a MISSING sub-key). A marked key,
                        // incl. a value-converted one, is read by TryCreateThrowOnMalformedNullAliasRead above
                        // (ClassifyMalformedFieldRead). Whole-entity routes stay strict.
                        // A dotted alias is a document path (a dotted scalar beside an array leaf takes its path as alias):
                        // walk it segment by segment through the property's serializer, over whole documents and nested
                        // $project output alike.
                        if (projection.Alias.Contains('.'))
                        {
                            return BsonBinding.CreateGetPropertyValueAtPath(
                                    DocParameter, projection.Alias.Split('.'), fieldAccess.Property,
                                    projectionBindingExpression.Type)
                                .ConvertIfRequired(projectionBindingExpression.Type);
                        }

                        var valueExpression = (_queryExpression.Select.Route == NativeRoute.Projection
                                               || _queryExpression.Select is { Route: NativeRoute.GroupBy, IsDistinct: true, IsGroupBy: false }
                                               && StoredSerialization.HasDefaultKeySerialization(fieldAccess.Property))
                            && !ReadsUnprojectedDocuments
                            && fieldAccess.Property.ClrType.UnwrapNullableType() != typeof(BsonDocument)
                            && fieldAccess.Property.ClrType.UnwrapNullableType() != typeof(BsonArray)
                            ? BsonBinding.CreateGetScalarProjectionValueExpression(
                                DocParameter, projection.Alias, fieldAccess.Property, projectionBindingExpression.Type)
                            : BsonBinding.CreateGetValueExpression(
                                DocParameter,
                                projection.Alias,
                                fieldAccess.Property,
                                projectionBindingExpression.Type);

                        // The read's type is the property's CLR type (widened to nullable if nullable) while the binding type may
                        // be the non-nullable form: convert to keep the shaper tree well-typed.
                        return valueExpression.ConvertIfRequired(projectionBindingExpression.Type);
                    }

                    // Reference-collection-nav First().Member must throw on an empty source like Enumerable.First(). Only the
                    // alias survives $project, missing/null exactly when no row matched (TryGetCorrelatedReducerLeaf declines
                    // First() over nullable members, so a matched null member can't be confused with that).
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

                    // A join-scope bare scalar leaf (`o.Customer!.Rank`) reads like a root bare leaf (D-F10): missing -> default.
                    if (TryCreateJoinScopeBareScalarRead(projection.Alias, projectionBindingExpression.Type, out var joinScopeRead))
                    {
                        return joinScopeRead;
                    }

                    // Non-property expressions (arithmetic, constants, Mql.Field) and key-property bindings carry the value
                    // under the alias as the BSON element name: read raw by alias. A GroupBy key, $min/$max output or
                    // join-scope leaf over a Local-kind DateTime reads with that kind (CreateAliasRead).
                    return CreateAliasRead(projection.Alias, projectionBindingExpression.Type);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    IArrayProjectionExpression arrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            // Both array projection kinds are admissible; anything else is a translation failure, not a cast crash.
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
                        // Nested collection inside a parent collection item (ThenInclude on collection-then-collection): read
                        // the BsonArray from the parent document by its path. An alias-addressed array (null ArrayFieldName)
                        // is only a top-level native projection leaf and can't reach here; `?? throw` makes a violation loud
                        // instead of a KeyNotFoundException from _projectionBindings.
                        var arrayFieldName = arrayProjection.ArrayFieldName
                                             ?? throw new InvalidOperationException(
                                                 CoreStrings.TranslationFailed(extensionExpression.Print()));
                        var parentAccess = arrayProjection.AccessExpression;
                        var parentDoc = _projectionBindings[parentAccess];
                        bsonArrayExpression = BsonBinding.CreateGetBsonArray(parentDoc, arrayFieldName);
                        arrayName = arrayFieldName;
                    }

                    // Normalize a missing or BSON-null stored array to an empty BsonArray (EF Core's contract; otherwise it
                    // depends on the POCO's initializer). Must stay here: VisitBinary hard-casts assignment RHS to
                    // UnaryExpression, so a Coalesce at the assignment would throw in every mode. TypeAs is null for absent
                    // and non-array elements alike.
                    bsonArrayExpression = Expression.Coalesce(bsonArrayExpression, Expression.New(typeof(BsonArray)));

                    var jObjectParameter = Expression.Parameter(typeof(BsonDocument), arrayName + "Object");
                    var ordinalParameter = Expression.Parameter(typeof(int), arrayName + "Ordinal");

                    var accessExpression = arrayProjection.InnerProjection.ParentAccessExpression;
                    _projectionBindings[accessExpression] = jObjectParameter;
                    // An array that materializes into no navigation (a list of another collection's entities) has no owner: its
                    // elements are root documents of their own collection, never owned entities reading the owner's key.
                    if (arrayProjection.OwnerEntityType is { } ownerEntityType)
                    {
                        _ownerMappings[accessExpression] = (ownerEntityType, arrayProjection.AccessExpression);
                        _ownerMappings[jObjectParameter] = (ownerEntityType, arrayProjection.AccessExpression);
                    }

                    _ordinalMappings[accessExpression] = Expression.Add(ordinalParameter, Expression.Constant(1, typeof(int)));
                    var innerShaper = (BlockExpression)Visit(collectionShaperExpression.InnerShaper);

                    innerShaper = AddIncludes(innerShaper);

                    var entities = Expression.Call(
                        EnumerableMethods.SelectWithOrdinal.MakeGenericMethod(typeof(BsonDocument), innerShaper.Type),
                        Expression.Call(
                            EnumerableMethods.Cast.MakeGenericMethod(typeof(BsonDocument)),
                            bsonArrayExpression),
                        Expression.Lambda(innerShaper, jObjectParameter, ordinalParameter));

                    // No navigation: a plain list of the materialized entities, assigned by the projection, not fixed up into a principal.
                    if (collectionShaperExpression.Navigation is not { } navigation)
                    {
                        return Expression.Call(
                            EnumerableMethods.ToList.MakeGenericMethod(collectionShaperExpression.ElementType),
                            entities);
                    }

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

                    // A collection item shaper's block may end with the entity variable instead of a ConditionalExpression.
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
                        // No conditional: the entity is always present; append includes directly.
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

                        // An alias-addressed array already has fieldName from projection.Alias. No current array kind reaches the
                        // throw; it guards a future node kind or route.
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

                        if (accessExpression is ObjectAccessExpression { Navigation: { } projectedNavigation }
                            && ResolveProjectedOwnedReferenceAlias(projectedNavigation) is { } projectedAlias)
                        {
                            fieldName = projectedAlias;
                        }

                        switch (accessExpression)
                        {
                            case ObjectAccessExpression crossCollectionAccess
                                when IsCrossCollectionAccess(crossCollectionAccess):
                                // Cross-collection Include result: under "_inner" in driver-native LeftJoin mode, or its own
                                // "_lookup_<Navigation>" field in flat $lookup + $unwind mode (derived from the projection node,
                                // never mutable global state). Under a collection Include the parent RootReference is bound to the
                                // per-element document: read the joined field from that, not the query root.
                                innerAccessExpression = GetCrossCollectionRootDocument(crossCollectionAccess);
                                fieldName = GetCrossCollectionFieldName(crossCollectionAccess);
                                // fieldRequired = false also makes a native LeftJoin's unmatched Inner row read as a null
                                // reference navigation (the joined field is absent for it).
                                fieldRequired = false;
                                break;
                            // Embedded sub-document access: navigation-less ObjectAccessExpressions exist only for
                            // cross-collection explicit joins, handled above.
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
                                // On the fallback route documents are whole and un-projected, so "$$ROOT" is the document itself.
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

            // Re-rooted whole-element owned SelectMany ($replaceRoot; see MongoShapedQueryCompilingExpressionVisitor): the
            // owner key and ordinal were merged in under sentinel fields so the owned key materializes non-null.
            // entityType == _rootEntityType only in this case (the query root is always a document root).
            if (entityType == _rootEntityType)
            {
                // Nested under the ShadowField wrapper: a two-segment path (CreateGetElementValue would treat it as one key).
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
                        // The principal is the owner's own stored key (another owned key up the chain recurses and applies
                        // this at the root). Only a shadow owned key: a CLR-mapped one would surface the placeholder.
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
        // Redirect only when nested under a collection Include (parent RootReference is for a non-root entity whose element
        // document is bound); top-level Includes and driver-native LeftJoin chains read from the absolute root.
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
    /// Rebuilds the CLR object a <see cref="MongoDocumentConstructionExpression"/> leaf represents, reading each member via
    /// <see cref="ReadDocumentConstructionMember"/>.
    /// </summary>
    protected Expression BuildDocumentConstructionExpression(MongoDocumentConstructionExpression construction, string alias)
        => BuildDocumentConstructionExpression(construction, [alias]);

    /// <summary>
    /// Path-aware overload for a nested construction. <paramref name="path"/> runs from the document root through this
    /// construction's alias; nested constructions render as real sub-documents, so reads walk every level.
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

        // A plain field uses its own IProperty/serializer (only at depth 1, so `path[0]` is the alias); a nested construction
        // recurses. Anything else (accumulator output, g.Key reference, constant) reads at the value's own translated type,
        // then converts: reading at a declared `object` type (Odata_groupby_empty_key's `Value`) would yield a raw
        // Decimal128 instead of a decimal.
        Expression read = value switch
        {
            MongoFieldExpression field => ReadDocumentConstructionMember(construction, path[0], memberName, field, memberType),
            MongoDocumentConstructionExpression nested => BuildDocumentConstructionExpression(nested, [..path, memberName]),
            _ => ReadDocumentConstructionMemberGeneric(construction, path, memberName, value.Type)
        };

        return read.ConvertIfRequired(memberType);
    }

    /// <summary>
    /// Reads a member with no single backing <see cref="IProperty"/> (accumulator output, g.Key reference, computed or
    /// constant value) by element path, at <paramref name="readType"/> (the value's own translated type, not the possibly
    /// widened declared type). A Local-kind DateTime property's unchanged value reads with that kind
    /// (<see cref="NativeDateTimeKindReadBack"/>).
    /// </summary>
    protected virtual Expression ReadDocumentConstructionMemberGeneric(
        MongoDocumentConstructionExpression construction, string[] path, string memberName, Type readType)
        => BsonBinding.CreateGetElementValueAtPath(DocParameter, [..path, memberName], readType,
            FindDateTimeKindSource(construction, memberName, readType));

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
    /// When the emit side flagged the output under <paramref name="alias"/> <see cref="MongoProjection.ThrowsOnNull"/>,
    /// reads it as a nullable and takes <see cref="Nullable{T}.Value"/>, so null (or missing) throws EF's "Nullable object
    /// must have a value." instead of reading as <c>default(T)</c>. The value type is <paramref name="type"/> when that is
    /// a non-nullable value type (a widened <c>(long)x.S.Length</c> reads the server's int as long); otherwise (a boxed
    /// <see cref="object"/> read) the staged expression's own type, converted back.
    /// </summary>
    private bool TryCreateThrowOnNullAliasRead(string alias, Type type, [NotNullWhen(true)] out Expression? read)
    {
        // A nullable read wants the null itself (the flag is set only for a non-nullable read, and a Distinct or set op
        // keeps the leaf's type).
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
    /// On the native Projection route, when this select's own output under <paramref name="alias"/> classifies
    /// <see cref="NonNullableValueRead.ThrowOnMalformedNull"/> (a computation over a non-nullable property's field,
    /// <c>x.Rank + 1</c>), reads it as a nullable and takes <see cref="Nullable{T}.Value"/>, so the null the server answers
    /// when a document omits the field throws EF's "Nullable object must have a value." (driver-LINQ threw
    /// <see cref="FormatException"/>) instead of reading <c>default(T)</c>. For
    /// <see cref="NonNullableValueRead.DefaultOnMalformedMissing"/> (the leaf may select the omitted field itself:
    /// <c>x.Rank > 1 ? -1 : x.Rank</c>) a MISSING output reads <c>default(T)</c>, as driver-LINQ did, and a null still throws.
    /// </summary>
    /// <remarks>
    /// Read side only: the emit side treats both as <see cref="NonNullableValueRead.Plain"/>, so no later operator declines.
    /// A projected Distinct's flattened key (GroupBy route) reads through <see cref="MongoProjection.ThrowsOnMalformedNull"/>/
    /// <see cref="MongoProjection.DefaultsOnMalformedMissing"/>, set by NativeGroupByBinder from the upstream leaf's
    /// classification. Whole-document reads (<see cref="ReadsUnprojectedDocuments"/>) are unaffected.
    /// </remarks>
    private bool TryCreateThrowOnMalformedNullAliasRead(string alias, Type type, [NotNullWhen(true)] out Expression? read)
    {
        read = null;
        var route = _queryExpression.Select.Route;
        if (route is not (NativeRoute.Projection or NativeRoute.GroupBy)
            || ReadsUnprojectedDocuments
            || !type.IsValueType
            || Nullable.GetUnderlyingType(type) is not null)
        {
            return false;
        }

        foreach (var projection in _queryExpression.Select.Projection)
        {
            if (projection.Alias != alias)
            {
                continue;
            }

            // A projected Distinct's flattened key carries its upstream leaf's classification; otherwise only this select's
            // own computed leaf on the Projection route qualifies.
            var malformedRead = projection.ThrowsOnMalformedNull
                ? NonNullableValueRead.ThrowOnMalformedNull
                : projection.DefaultsOnMalformedMissing
                    ? MongoAggregationExpressionRenderer.ReclassifyMalformedReadAs(
                        NonNullableValueRead.DefaultOnMalformedMissing, projection.Expression.Type, type)
                    : route == NativeRoute.Projection
                        ? MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(type, projection.Expression)
                        : NonNullableValueRead.Plain;
            if (malformedRead is not (NonNullableValueRead.ThrowOnMalformedNull or NonNullableValueRead.DefaultOnMalformedMissing))
            {
                return false;
            }

            // A marked Distinct key over a value-converted field is read through its property so the converter applies:
            // MISSING reads default, an explicit null throws, as main's converter did.
            if (malformedRead == NonNullableValueRead.DefaultOnMalformedMissing
                && projection.DefaultsOnMalformedMissing
                && _queryExpression.Select.Grouping?.Key.FirstOrDefault(k => k.Name == alias) is { MarksMissing: true } keyPart
                && MongoAggregationExpressionRenderer.ReadsConvertedDistinctKeyThroughProperty(type, keyPart.FieldRef))
            {
                read = BsonBinding.CreateGetScalarProjectionValueExpression(
                    DocParameter, alias, ((MongoFieldExpression)keyPart.FieldRef).Property, type);
                return true;
            }

            read = malformedRead == NonNullableValueRead.DefaultOnMalformedMissing
                ? CreateDefaultOnMissingAliasRead(alias, type)
                : CreateStrictAliasRead(alias, type);
            return true;
        }

        return false;
    }

    /// <summary>
    /// On the native Projection route, reads a non-nullable cast of a stored scalar the emit side staged as the bare field
    /// (<c>(int)x.Rank</c>, <c>(long)x.Rank</c>) the way driver-LINQ's rendering of that cast answers: an identity cast (also
    /// over a nullable source), which the driver drops, reads a missing element as <c>default(T)</c> and throws on an
    /// explicit null; a cast the driver renders as <c>$toX</c> (<see cref="MongoConvertExpression.ToOperatorFor"/>) answers
    /// null for a missing or null field, so it reads strictly and throws for both rather than reading null as <c>0</c>.
    /// </summary>
    private bool TryCreateRequiredScalarCastRead(UnaryExpression castLeaf, string alias, Type type, [NotNullWhen(true)] out Expression? read)
    {
        read = null;
        if (_queryExpression.Select.Route != NativeRoute.Projection
            || ReadsUnprojectedDocuments
            || !type.IsValueType
            || Nullable.GetUnderlyingType(type) is not null
            || TryResolveFieldAccess(castLeaf).Property is not { } property
            || !property.ClrType.UnwrapNullableType().IsValueType
            || !_queryExpression.Select.Projection.Any(p => p.Alias == alias && p.Expression is MongoFieldExpression))
        {
            return false;
        }

        if (castLeaf.Operand.Type.UnwrapNullableType() == type)
        {
            read = property.IsNullable
                ? CreateDefaultOnMissingAliasRead(alias, type)
                : BsonBinding.CreateGetScalarProjectionValueExpression(DocParameter, alias, property, type);
            return true;
        }

        // An enum's cast to its underlying type, staged bare (IsEnumUnderlyingRelabel): read the stored integer; missing ->
        // default, null -> throws, as the driver (which drops the cast) did.
        if (MongoExpressionTranslator.IsEnumUnderlyingRelabel(castLeaf.Operand.Type, type))
        {
            read = CreateDefaultOnMissingAliasRead(alias, type);
            return true;
        }

        // Any other enum cast (to a wider integral type) keeps the plain alias read.
        if (MongoConvertExpression.ToOperatorFor(type) is null
            || castLeaf.Operand.Type.UnwrapNullableType().IsEnum)
        {
            return false;
        }

        read = CreateStrictAliasRead(alias, type);
        return true;
    }

    /// <summary>
    /// On the native Projection route over a join scope, reads a bare stored scalar leaf of a non-nullable property
    /// (<c>o.Customer!.Rank</c>; <see cref="MongoAggregationExpressionRenderer.ReadsJoinScopeBareScalarThroughProperty"/>)
    /// through that property like a root bare leaf: MISSING (an unmatched optional reference after <c>$lookup</c>) reads
    /// <c>default(T)</c> as driver-LINQ's push-down did, an explicit null throws (a <c>bool</c> reads false). The generic
    /// alias read would throw for MISSING and read an explicit null as <c>0</c>.
    /// </summary>
    private bool TryCreateJoinScopeBareScalarRead(string alias, Type type, [NotNullWhen(true)] out Expression? read)
    {
        read = null;
        if (_queryExpression.Select is not { Route: NativeRoute.Projection, JoinScope: not null } select
            || ReadsUnprojectedDocuments)
        {
            return false;
        }

        foreach (var projection in select.Projection)
        {
            if (projection.Alias != alias)
            {
                continue;
            }

            if (projection.ThrowsOnNull
                || !MongoAggregationExpressionRenderer.ReadsJoinScopeBareScalarThroughProperty(type, projection.Expression))
            {
                return false;
            }

            read = BsonBinding.CreateGetScalarProjectionValueExpression(
                DocParameter, alias, ((MongoFieldExpression)projection.Expression).Property, type);
            return true;
        }

        return false;
    }

    // `type` read as T? and unwrapped: a missing or null alias throws EF's "Nullable object must have a value.". A bool reads
    // both as false instead (driver-LINQ's BooleanSerializer reads null as false; DriverReadsNullAsDefault).
    private Expression CreateStrictAliasRead(string alias, Type type)
        => MongoAggregationExpressionRenderer.DriverReadsNullAsDefault(type)
            ? Expression.Condition(
                Expression.Call(IsElementAbsentOrNullMethodInfo, DocParameter, Expression.Constant(alias)),
                Expression.Default(type),
                CreateAliasRead(alias, type))
            : Expression.Property(
                CreateAliasRead(alias, type.MakeNullable()), type.MakeNullable().GetProperty(nameof(Nullable<int>.Value))!);

    // A missing alias reads default(T) (driver-LINQ's deserializer reading an omitted member); a null one throws.
    private Expression CreateDefaultOnMissingAliasRead(string alias, Type type)
        => Expression.Condition(
            Expression.Call(IsElementAbsentMethodInfo, DocParameter, Expression.Constant(alias)),
            Expression.Default(type),
            CreateStrictAliasRead(alias, type));

    /// <summary>
    /// Reads a projection output by alias through the generic serializer for <paramref name="type"/>. An alias holding the
    /// unchanged value of a Local-kind DateTime property (GroupBy key, <c>$min</c>/<c>$max</c>, cast or join-scope leaf)
    /// reads back with that kind (<see cref="NativeDateTimeKindReadBack"/>); a native <c>DateTime.TimeOfDay</c> leaf holds
    /// milliseconds, read through <see cref="BsonSerializerFactory.TimeOfDayMillisecondsSerializer"/>.
    /// </summary>
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
    /// Reads one member of a <see cref="MongoDocumentConstructionExpression"/> leaf: natively <c>"{alias}.{memberName}"</c>,
    /// where <c>$project</c> put it; the mixed visitor overrides it to read <paramref name="field"/>'s natural path off whole
    /// documents.
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
                // Driver-native LeftJoin Includes keep the root entity under "_outer".
                RootReferenceExpression when _queryExpression.UsesDriverJoinFields
                    => CreateGetValueExpression(DocParameter, "_outer", required, typeof(BsonDocument)),
                RootReferenceExpression => CreateGetValueExpression(DocParameter, null, required, typeof(BsonDocument)),
                // Cross-collection Include results sit at the document root, under "_inner" or "_lookup_<Navigation>".
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

    /// <summary>
    /// Throws when an alias read of <paramref name="property"/> bound as <paramref name="bindingType"/> differs beyond
    /// nullability (either direction): the property's serializer would produce values that can't be cast to the binding.
    /// </summary>
    protected static void ThrowIfAliasedTypeMismatch(IProperty property, Type bindingType)
    {
        if (property.ClrType != bindingType
            && property.ClrType.UnwrapNullableType() != bindingType.UnwrapNullableType())
        {
            throw new InvalidOperationException(
                $"Aliased projection type '{bindingType}' does not match source property " +
                $"'{property.Name}' of type '{property.ClrType}'; the property's serializer " +
                "may produce values that cannot be cast to the binding's outer type.");
        }
    }

    protected ResolvedFieldAccess TryResolveFieldAccess(Expression? expression)
    {
        if (expression == null) return default;

        expression = expression.RemoveConvert();

        // Peel Nullable<T>.Value (`x.Score.Value`): the receiver names the stored field. Mirrors
        // MongoExpressionTranslator.TryResolveMember; otherwise FindProperty misses and the raw alias read drops the converter.
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

        // `new { o.Prop }` stores MemberExpr(STS_root, "Prop"): resolve the property via the root shaper and use its own
        // element name, not the projected alias.
        if (expression is StructuralTypeShaperExpression { StructuralType: IEntityType shaperEntityType })
        {
            if (shaperEntityType == _rootEntityType)
            {
                return (shaperEntityType, DocParameter);
            }

            // A non-root entity shaper in a join query is the join's inner side: read from the joined sub-document, not the
            // query root, which has no such field and would materialise null (EF-352). Driver-native join: the lone
            // reference under "_inner"; flat $lookup+$unwind: each join in its own "_lookup_<Navigation>".
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

        // Owned navigations aren't joins: a dotted hop like `b.Home.City` is a plain MemberExpression or EF.Property call,
        // not an ObjectAccessExpression. Recurse so multi-level hops don't fall back to a leaf lookup on the wrong document.
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

        // No primary key to resolve the $lookup join, and keyless entities are never tracked.
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
    {
        if (projectionBindingExpression.ProjectionMember == null)
        {
            return projectionBindingExpression.Index
                   ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");
        }

        var mapped = _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember);
        return mapped is ConstantExpression { Value: int index }
            ? index
            : throw new InvalidOperationException(
                $"Internal error - the projection mapping for '{projectionBindingExpression.ProjectionMember}' was not flattened to a "
                + $"projection index (found '{mapped.GetType().Name}' of type '{mapped.Type.ShortDisplayName()}'). The query shape is not "
                + "supported by the provider; if it contains a client-evaluated method before SelectMany or Include, "
                + "rewrite the query to evaluate on the client explicitly (for example with AsEnumerable()).");
    }
}
