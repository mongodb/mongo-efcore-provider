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
using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Extends <see cref="MongoProjectionBindingRemovingExpressionVisitor"/> to handle mixed projections
/// (containing both entity references and scalar properties). In this path, the LINQ V3 query
/// returns full BsonDocuments (Select is stripped), and scalars are read from the document that
/// owns the mapped property using the property's actual serialization info.
/// </summary>
internal sealed class MongoMixedProjectionBindingRemovingExpressionVisitor
    : MongoProjectionBindingRemovingExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly IEntityType _rootEntityType;
    private readonly ParameterExpression _docParameter;

    private readonly bool _ownerKeyMayBeAbsent;

    /// <param name="rootEntityType">The root entity type of the query.</param>
    /// <param name="queryExpression">The query being shaped.</param>
    /// <param name="docParameter">The shaper's <see cref="BsonDocument"/> parameter.</param>
    /// <param name="trackingBehavior">The query's tracking behavior.</param>
    /// <param name="pushedDownSelectRetained">
    /// <see langword="true"/> when the projecting <c>Select</c> could not be stripped because an operator composes
    /// over its result (<c>Distinct</c>, <c>Union</c>, <c>Concat</c>, ...). The driver then returns projected
    /// documents keyed by member name, which carry an owned-reference leaf's sub-document but not its owner's key.
    /// </param>
    public MongoMixedProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        QueryTrackingBehavior trackingBehavior,
        bool pushedDownSelectRetained = false)
        : base(rootEntityType, queryExpression, docParameter, trackingBehavior)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
        _docParameter = docParameter;

        // The owned key only feeds identity: under NoTracking it's unobservable (TrackAll already rejects an owned
        // entity projected without its owner), but identity resolution would merge every row onto the placeholder.
        _ownerKeyMayBeAbsent = pushedDownSelectRetained && trackingBehavior == QueryTrackingBehavior.NoTracking;
    }

    /// <inheritdoc />
    protected override bool ReadsUnprojectedDocuments => true;

    /// <inheritdoc />
    protected override bool OwnerKeyMayBeAbsent => _ownerKeyMayBeAbsent;

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is ProjectionBindingExpression projectionBindingExpression)
        {
            if (projectionBindingExpression.ProjectionMember != null)
            {
                var mappedExpression = _queryExpression.GetMappedProjection(
                    projectionBindingExpression.ProjectionMember);

                // Resolve the source expression: after ApplyProjection it's wrapped as Constant(index),
                // so we unwrap to get the actual stored expression; otherwise use it directly.
                Expression? sourceExpression;
                string? alias;
                if (mappedExpression is ConstantExpression { Value: int })
                {
                    var projection = GetProjection(projectionBindingExpression);
                    alias = projection.Alias;
                    sourceExpression = projection.Expression;
                    if (alias is null)
                    {
                        // A null alias usually means the binding is the whole document/entity
                        // (e.g. select new { o }) — hand back the BsonDocument for the entity shaper.
                        // But a scalar root-property binding (e.g. select p.name.ToArray()) also has no
                        // alias; resolve it to a field read instead of returning the whole document.
                        if (TryBindArithmeticLeaf(sourceExpression, projectionBindingExpression.Type, out var rootArithmeticRead))
                        {
                            return rootArithmeticRead;
                        }

                        // The bare-body spelling of the string-to-char-sequence leaf (`select p.Name.AsEnumerable()`);
                        // TryResolveFieldAccess can't resolve an Enumerable.* call and would return the whole document.
                        if (TryBindStringSequenceLeaf(sourceExpression, projectionBindingExpression.Type,
                                out var rootStringSequenceRead))
                        {
                            return rootStringSequenceRead;
                        }

                        var rootField = TryResolveFieldAccess(sourceExpression);
                        if (rootField.Property != null)
                            return CreateGetValueExpression(
                                rootField.DocumentExpression ?? _docParameter, rootField.Property,
                                projectionBindingExpression.Type);
                        if (rootField.FieldName != null)
                            return BsonBinding.CreateGetElementValue(
                                rootField.DocumentExpression ?? _docParameter, rootField.FieldName,
                                projectionBindingExpression.Type);
                        return _docParameter;
                    }
                }
                else
                {
                    alias = projectionBindingExpression.ProjectionMember.Last?.Name;
                    sourceExpression = mappedExpression;
                }

                // A scalar member access on a singleton (reference) navigation, e.g. select o.Customer.City.
                // The source expression is a MemberExpression whose source is the navigation's
                // StructuralTypeShaperExpression. The property belongs to the navigation target entity, not the
                // query root, so it must be read from the joined sub-document (the driver's native LeftJoin
                // places the lone joined reference under "_inner") rather than the root document.
                if (TryBindNavigationMemberAccess(sourceExpression, projectionBindingExpression.Type, out var navMemberRead))
                {
                    return navMemberRead;
                }

                // A constructed sub-entity leaf (`new { Book = new Book { Id = e.Id, ... } }`), registered as a single
                // leaf keyed to NativeProjectionBinder's MongoDocumentConstructionExpression. The document here is
                // whole (no $project alias), so each member is read at its natural root-relative path via
                // ReadDocumentConstructionMember.
                if (sourceExpression is MongoDocumentConstructionExpression documentConstruction)
                {
                    return BuildDocumentConstructionExpression(documentConstruction, alias!);
                }

                // A computed-arithmetic leaf (e.g. select new { c, Total = c.Age * c.Score }) mixed alongside
                // a whole entity reference. MongoProjectionBindingExpressionVisitor registers the raw binary
                // expression as a single projection-mapping leaf (see its arithmetic BinaryExpression case);
                // evaluate it here by resolving each operand against the materialized document and rebuilding
                // the arithmetic client-side, since the driver-LINQ Select was stripped in this mixed path.
                if (TryBindArithmeticLeaf(sourceExpression, projectionBindingExpression.Type, out var arithmeticRead))
                {
                    return arithmeticRead;
                }

                // A string-to-char-sequence leaf (`select new { P = b.City.AsEnumerable(), b.Posts }`) mixed with an
                // owned reference. The Select was stripped, so re-apply the operator client-side over a whole-document
                // read; the alias read below would throw "Document element 'P' is missing but required".
                if (TryBindStringSequenceLeaf(sourceExpression, projectionBindingExpression.Type,
                        out var stringSequenceRead))
                {
                    return stringSequenceRead;
                }

                var fieldAccess = TryResolveFieldAccess(sourceExpression);
                if (fieldAccess.Property != null)
                {
                    if (fieldAccess.DocumentExpression is ParameterExpression parameterExpression
                        && fieldAccess.MemberInfo != null
                        && fieldAccess.MemberInfo.DeclaringType?.IsAssignableFrom(parameterExpression.Type) == true)
                    {
                        var memberAccess = Expression.MakeMemberAccess(parameterExpression, fieldAccess.MemberInfo);
                        return memberAccess.Type == projectionBindingExpression.Type
                            ? memberAccess
                            : Expression.Convert(memberAccess, projectionBindingExpression.Type);
                    }

                    // When using the driver's native Join, scalar properties read from the root entity
                    // live in the "_outer" sub-document, not at the document root. The resolver returns
                    // the root doc parameter for such accesses; redirect it to "_outer" here.
                    var docExpr = fieldAccess.DocumentExpression ?? _docParameter;
                    if (_queryExpression.UsesDriverJoinFields
                        && ReferenceEquals(docExpr, _docParameter))
                    {
                        docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
                    }

                    return CreateGetValueExpression(
                        docExpr,
                        fieldAccess.Property,
                        projectionBindingExpression.Type);
                }

                if (fieldAccess.FieldName != null)
                {
                    return BsonBinding.CreateGetElementValue(
                        fieldAccess.DocumentExpression ?? _docParameter,
                        fieldAccess.FieldName,
                        projectionBindingExpression.Type);
                }

                return CreateGetValueExpression(
                    _docParameter,
                    alias,
                    !projectionBindingExpression.Type.IsNullableType(),
                    projectionBindingExpression.Type);
            }

            if (TryBindNativeFieldLeafAsDocumentPath(projectionBindingExpression, out var pathRead))
            {
                return pathRead;
            }

            return base.VisitExtension(extensionExpression);
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Resolves an index-bound scalar projection leaf against the whole document by its root-relative path rather
    /// than by its projection alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Index-bound leaves (from a natively-bound <c>Join</c>'s <c>BindResultMember</c>) otherwise fall through to the
    /// base visitor's alias read, which is right for the native <c>$project</c> but wrong against a whole document
    /// when alias and path differ: <c>new { o, r.Total }</c> throws "Document element 'Total' is missing", and
    /// <c>new { N = o.Name, r }</c> silently reads <see langword="null"/>.
    /// </para>
    /// <para>
    /// The path is root-relative by construction (<c>MongoFieldExpression.ElementName</c>). Only the last segment
    /// is read through the leaf's <c>IProperty</c>; a missing intermediate yields <see langword="null"/>, so an
    /// unmatched left-outer row reads as a null scalar. No-op when alias equals path or when
    /// <c>UsesDriverJoinFields</c> (a different document shape).
    /// </para>
    /// </remarks>
    private bool TryBindNativeFieldLeafAsDocumentPath(
        ProjectionBindingExpression projectionBindingExpression, out Expression result)
    {
        result = null!;

        // Gated on the join, not just alias/path disagreement: BindSelectManyMember and BindGroupMember also produce
        // index-bound leaves, and for them a path read would silently return a raw field where the alias read
        // currently fails loudly.
        if (_queryExpression.Select.JoinScope is null || _queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var projection = GetProjection(projectionBindingExpression);
        if (projection.Alias is not { } alias)
        {
            return false;
        }

        // MongoOuterFieldExpression (a join scope's outer-side scalar) is as root-relative as MongoFieldExpression,
        // matching NativeJoinScopeProjectionBinder's whole-document-readable gate.
        (IProperty Property, string ElementName)? field = null;
        foreach (var staged in _queryExpression.Select.Projection)
        {
            if (staged.Alias == alias)
            {
                field = staged.Expression switch
                {
                    MongoFieldExpression f => (f.Property, f.ElementName),
                    MongoOuterFieldExpression o => (o.Property, o.ElementName),
                    _ => null
                };
                break;
            }
        }

        if (field is not { } fieldInfo || fieldInfo.ElementName == alias)
        {
            return false;
        }

        // Same property/binding-type invariant as the base visitor's alias read. Nullability may differ either way
        // (e.g. a `Nullable<T>.Value` leaf stages the nullable property), so compare unwrapped types; a genuine
        // mismatch would silently deserialize through the wrong serializer.
        if (fieldInfo.Property.ClrType != projectionBindingExpression.Type
            && fieldInfo.Property.ClrType.UnwrapNullableType() != projectionBindingExpression.Type.UnwrapNullableType())
        {
            throw new InvalidOperationException(
                $"Aliased projection type '{projectionBindingExpression.Type}' does not match source property " +
                $"'{fieldInfo.Property.Name}' of type '{fieldInfo.Property.ClrType}'; the property's serializer " +
                "may produce values that cannot be cast to the binding's outer type.");
        }

        // No BsonArray/BsonDocument arms, unlike CreateGetValueExpression: a bare field leaf is always a scalar
        // IProperty. Add them if a raw-BSON field leaf ever becomes possible.
        var valueExpression = BsonBinding.CreateGetPropertyValueAtPath(
            _docParameter, fieldInfo.ElementName.Split('.'), fieldInfo.Property, projectionBindingExpression.Type);

        // Mandatory: for a `.Value`-peeled leaf (`x.o.Rank.Value` over `int? Rank`) the read is typed `int?` but the
        // binding expects `int`, a shaper-compile type mismatch. Pinned by
        // NativeJoinTests.Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly.
        result = valueExpression.Type == projectionBindingExpression.Type
            ? valueExpression
            : Expression.Convert(valueExpression, projectionBindingExpression.Type);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ignores <paramref name="alias"/> and reads <paramref name="field"/> at its natural root-relative location,
    /// redirected through "_outer" when <see cref="MongoQueryExpression.UsesDriverJoinFields"/>.
    /// </remarks>
    protected override Expression ReadDocumentConstructionMember(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
        Type memberType)
    {
        var docExpr = (Expression)_docParameter;
        if (_queryExpression.UsesDriverJoinFields)
        {
            docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
        }

        // A join-scope member's ElementName is a dotted path relative to docExpr (e.g. "_lookup_Customer.CustomerID"),
        // so walk it with the multi-segment helper, which treats an absent intermediate (unmatched left-outer row)
        // as null. Dispatching on the dot is safe only because NativeProjectionBinder.TryGetDocumentConstructionLeaf
        // declines dotted root-relative members; if that's relaxed, this must carry provenance instead.
        if (field.ElementName.Contains('.'))
        {
            return BsonBinding.CreateGetPropertyValueAtPath(docExpr, field.ElementName.Split('.'), field.Property, memberType);
        }

        return CreateGetValueExpression(docExpr, field.Property, memberType);
    }

    /// <summary>
    /// Binds a scalar property access on a reference navigation in a mixed projection
    /// (<c>select new { A = o.Customer, B = o.Customer.City }</c>, or <c>EF.Property</c> for a shadow property),
    /// reading it from the driver LeftJoin's <c>"_inner"</c> sub-document. Returns <see langword="false"/> for
    /// anything else so the caller can try its other resolution paths.
    /// </summary>
    private bool TryBindNavigationMemberAccess(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        StructuralTypeShaperExpression shaper;
        IProperty? property;
        switch (mappedExpression)
        {
            case MemberExpression { Expression: StructuralTypeShaperExpression memberShaper } memberExpression:
                shaper = memberShaper;
                property = shaper.StructuralType is IEntityType memberEntityType
                    ? memberEntityType.FindProperty(memberExpression.Member)
                    : null;
                break;

            case MethodCallExpression methodCallExpression
                when methodCallExpression.Method.IsEFPropertyMethod()
                     && methodCallExpression.Arguments[0] is StructuralTypeShaperExpression efPropertyShaper
                     && methodCallExpression.Arguments[1] is ConstantExpression { Value: string propertyName }:
                shaper = efPropertyShaper;
                property = shaper.StructuralType is IEntityType efPropertyEntityType
                    ? efPropertyEntityType.FindProperty(propertyName)
                    : null;
                break;

            default:
                return false;
        }

        // Only a joined navigation target. A root-entity property (select new { o, o.CustomerID }) is read from
        // "_outer" by TryResolveFieldAccess; reading it from "_inner" would return the joined document's value.
        if (property == null || shaper.StructuralType == _rootEntityType)
        {
            return false;
        }

        // Only the driver-native single-reference join shape (joined document under "_inner") is supported
        // here; other shapes fall through to the existing resolution paths / translation failure.
        if (!_queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var innerDoc = CreateGetValueExpression(_docParameter, "_inner", false, typeof(BsonDocument));
        result = CreateGetValueExpression(innerDoc, property, resultType);
        return true;
    }

    /// <summary>
    /// Binds a string-to-char-sequence leaf (<c>select new { P = b.City.AsEnumerable(), b.Posts }</c>, and
    /// <c>.ToList()</c>/<c>.ToArray()</c>) against the whole document, reading the source property at its natural
    /// location instead of a <c>$project</c> alias (cf. <see cref="MongoProjectionBindingRemovingExpressionVisitor"/>).
    /// </summary>
    /// <remarks>
    /// Reads through the source <see cref="IProperty"/> so converters and <c>BsonRepresentation</c> apply, then
    /// rebuilds the original call around that read. Returns <see langword="false"/> if not such a leaf or the
    /// argument doesn't resolve to a property.
    /// </remarks>
    private bool TryBindStringSequenceLeaf(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        if (mappedExpression is not MethodCallExpression call
            || !NativeProjectionBinder.IsStringSequenceMaterializationCall(call))
        {
            return false;
        }

        var fieldAccess = TryResolveFieldAccess(call.Arguments[0]);
        if (fieldAccess.Property is not { } property)
        {
            return false;
        }

        // The emit side only admits string-typed sources, so a non-string property means the two sides disagree.
        Debug.Assert(property.ClrType == typeof(string),
            $"String-sequence projection leaf resolved to non-string property '{property.Name}' of type '{property.ClrType}'.");

        // The driver's native Join places root scalars under "_outer", as elsewhere in this visitor.
        var docExpr = fieldAccess.DocumentExpression ?? _docParameter;
        if (_queryExpression.UsesDriverJoinFields
            && ReferenceEquals(docExpr, _docParameter))
        {
            docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
        }

        result = Expression.Call(call.Method, CreateGetValueExpression(docExpr, property, typeof(string)));
        if (result.Type != resultType)
        {
            result = Expression.Convert(result, resultType);
        }

        return true;
    }

    /// <summary>
    /// Binds a computed-arithmetic projection leaf (e.g. <c>select new { c, Total = c.Age * c.Score }</c>).
    /// <see cref="MongoProjectionBindingExpressionVisitor"/> registers such a leaf as the raw
    /// <see cref="BinaryExpression"/> (not decomposed into independent operand bindings — see its arithmetic
    /// case), because the mapped projection is stored once per <c>ProjectionMember</c> and decomposing would
    /// have both operands clobber that single slot. In this mixed path the driver-LINQ Select was stripped
    /// (full <see cref="BsonDocument"/>s come back), so the arithmetic must be evaluated client-side: each
    /// operand is resolved to a document read (recursing for nested arithmetic) and the same operator is
    /// rebuilt over the resolved reads. Returns <see langword="false"/> for anything that is not such an
    /// arithmetic leaf so the caller can fall back to its other resolution paths.
    /// </summary>
    private bool TryBindArithmeticLeaf(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        if (mappedExpression is not BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo } binaryExpression)
        {
            return false;
        }

        var left = ResolveArithmeticOperand(binaryExpression.Left);
        var right = ResolveArithmeticOperand(binaryExpression.Right);

        result = Expression.MakeBinary(
            binaryExpression.NodeType, left, right, binaryExpression.IsLiftedToNull, binaryExpression.Method);
        if (result.Type != resultType)
        {
            result = Expression.Convert(result, resultType);
        }

        return true;
    }

    /// <summary>
    /// Resolves one operand of a computed-arithmetic projection leaf (see <see cref="TryBindArithmeticLeaf"/>)
    /// to an expression that reads its value from the materialized document. Constants pass through unchanged;
    /// nested arithmetic recurses; a scalar property access (member or <c>EF.Property</c>, on the root entity
    /// or a joined navigation target) is resolved the same way a standalone scalar leaf would be.
    /// </summary>
    private Expression ResolveArithmeticOperand(Expression operand)
    {
        var unwrapped = operand.RemoveConvert();

        if (unwrapped is ConstantExpression)
        {
            return operand;
        }

        if (unwrapped is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo } nestedBinary)
        {
            Expression nestedResult = Expression.MakeBinary(
                nestedBinary.NodeType,
                ResolveArithmeticOperand(nestedBinary.Left),
                ResolveArithmeticOperand(nestedBinary.Right),
                nestedBinary.IsLiftedToNull,
                nestedBinary.Method);

            return nestedResult.Type == operand.Type ? nestedResult : Expression.Convert(nestedResult, operand.Type);
        }

        if (TryBindNavigationMemberAccess(unwrapped, operand.Type, out var navRead))
        {
            return navRead;
        }

        var fieldAccess = TryResolveFieldAccess(unwrapped);
        if (fieldAccess.Property != null)
        {
            var docExpr = fieldAccess.DocumentExpression ?? _docParameter;
            if (_queryExpression.UsesDriverJoinFields
                && ReferenceEquals(docExpr, _docParameter))
            {
                docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
            }

            return CreateGetValueExpression(docExpr, fieldAccess.Property, operand.Type);
        }

        if (fieldAccess.FieldName != null)
        {
            return BsonBinding.CreateGetElementValue(
                fieldAccess.DocumentExpression ?? _docParameter, fieldAccess.FieldName, operand.Type);
        }

        throw new InvalidOperationException(CoreStrings.TranslationFailed(operand.Print()));
    }
}
