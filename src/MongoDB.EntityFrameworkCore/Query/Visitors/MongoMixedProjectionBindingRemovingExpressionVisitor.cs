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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
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

    public MongoMixedProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        QueryTrackingBehavior trackingBehavior)
        : base(rootEntityType, queryExpression, docParameter, trackingBehavior)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
        _docParameter = docParameter;
    }

    /// <inheritdoc />
    protected override bool ReadsUnprojectedDocuments => true;

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

                // A CONSTRUCTED sub-entity leaf (EF-447, `new { Book = new Book { Id = e.Id, ... } }`), mixed
                // alongside the (also-native) Score leaf. MongoProjectionBindingExpressionVisitor registers the
                // whole New/MemberInit node as a single projection-mapping leaf (see its own matching Visit()
                // case), keyed to the SAME MongoDocumentConstructionExpression NativeProjectionBinder built at
                // emit time. Rebuild the CLR object here by reading each member off the WHOLE document at its
                // own NATURAL root-relative path — this visitor only ever sees whole, un-projected documents
                // (the pushed-down Select is always stripped), which never carry this leaf's native $project
                // alias at all. BuildDocumentConstructionExpression (base class) does the shared reconstruction;
                // ReadDocumentConstructionMember (overridden just below) supplies the per-member READ.
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
    /// Resolve an INDEX-bound scalar projection leaf against the WHOLE document by the leaf's own root-relative
    /// document path, instead of by its projection alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This visitor only ever runs over whole, un-projected documents (the pushed-down <c>Select</c> is always
    /// stripped before it is used — see <c>MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery</c> and
    /// its late-fallback arm). The <see cref="ProjectionBindingExpression"/> handling above covers
    /// <c>ProjectionMember</c>-bound leaves, which resolve through EF's own projection mapping and therefore
    /// already read from the right sub-document. INDEX-bound leaves — the shape a natively-bound genuine
    /// <c>Join</c> produces (EF-444), where <c>BindResultMember</c>/<c>BindSelectManyMember</c> register each
    /// member positionally — fall through to the base visitor, which reads them by their projection ALIAS. That
    /// is correct against the native <c>$project</c> this provider emits (where the alias IS the field name) and
    /// WRONG against a whole document whenever the alias differs from the leaf's own path:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>new { o, r.Total }</c> — alias <c>Total</c>, path <c>_lookup_Orders.Total</c>: MEASURED as
    /// <c>Document element 'Total' is missing but required</c> under an explicit
    /// <c>MongoQueryMode.DriverLinq</c>;
    /// </description></item>
    /// <item><description>
    /// <c>new { N = o.Name, r }</c> — alias <c>N</c>, path <c>Name</c>: MEASURED as a SILENT <see langword="null"/>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Reading by path fixes both without narrowing what goes native. The path is root-relative by construction
    /// (<c>MongoFieldExpression.ElementName</c> is what the emit side puts after the <c>$</c> in the
    /// <c>$project</c> value), so against an un-projected document it is exactly the right read — the same
    /// equivalence <c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c> already
    /// relies on for a document-path alias. Only the LAST segment is read through the leaf's own
    /// <c>IProperty</c> (its serializer, nullability and value converter); the segments before it are plain
    /// sub-document reads, and a missing one yields <see langword="null"/> rather than throwing, which is what
    /// keeps an unmatched left-outer row reading as a null scalar instead of an exception.
    /// </para>
    /// <para>
    /// Deliberately a no-op when the alias already equals the path (the overwhelmingly common case — the two
    /// reads are then literally the same), and when the driver's own <c>_outer</c>/<c>_inner</c> join shape is in
    /// play (<c>UsesDriverJoinFields</c>), whose document is not the one these paths are relative to.
    /// </para>
    /// </remarks>
    private bool TryBindNativeFieldLeafAsDocumentPath(
        ProjectionBindingExpression projectionBindingExpression, out Expression result)
    {
        result = null!;

        // GATED ON THE JOIN, not merely on the alias-vs-path disagreement, so the method's blast radius matches
        // its justification exactly. Index-bound leaves are produced at three sites — BindResultMember (a join,
        // this method's whole reason to exist), BindSelectManyMember and BindGroupMember — and neither of the
        // latter two currently produces a leaf whose alias differs from its element name, so today the
        // disagreement test alone would be a REACHABILITY ACCIDENT rather than a guard. For GroupBy in
        // particular a future collision here would be strictly worse than the status quo: an unreachable-alias
        // read fails LOUDLY today, whereas a path read would silently return whatever that raw document field
        // happens to hold. Free to close, so close it.
        if (_queryExpression.Select.JoinScope is null || _queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var projection = GetProjection(projectionBindingExpression);
        if (projection.Alias is not { } alias)
        {
            return false;
        }

        // MongoOuterFieldExpression is a sibling of MongoFieldExpression (a join scope's OUTER-side scalar
        // leaf resolves as one via NativeJoinScopeTranslator's shared operand path — see
        // MongoExpressionTranslator.TranslateOperand's own remarks), and it is exactly as root-relative/
        // document-path-readable as MongoFieldExpression, so it must be admitted here too — matching
        // NativeJoinScopeProjectionBinder's own "every sibling leaf must be whole-document-readable" gate,
        // which likewise treats the two as equivalent.
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

        // The property/binding-type invariant, carried over VERBATIM from the sibling read this method mirrors
        // (MongoProjectionBindingRemovingExpressionVisitor's alias-addressed ProjectionBindingExpression arm).
        // Nullability may differ in EITHER direction and both are legitimate — a nullable binding over a
        // non-nullable property (widening), or a non-nullable binding over a NULLABLE property, which is what a
        // `Nullable<T>.Value` leaf produces once MongoExpressionTranslator.TryResolveMember peels the `.Value`
        // (EF-402) and stages the nullable property itself. Unwrap both sides before comparing so neither
        // direction trips, but keep the assert: without it a future change that stages a leaf whose property
        // genuinely disagrees with the binding would mis-deserialise through the wrong serializer silently.
        if (fieldInfo.Property.ClrType != projectionBindingExpression.Type
            && fieldInfo.Property.ClrType.UnwrapNullableType() != projectionBindingExpression.Type.UnwrapNullableType())
        {
            throw new InvalidOperationException(
                $"Aliased projection type '{projectionBindingExpression.Type}' does not match source property " +
                $"'{fieldInfo.Property.Name}' of type '{fieldInfo.Property.ClrType}'; the property's serializer " +
                "may produce values that cannot be cast to the binding's outer type.");
        }

        // No BsonArray/BsonDocument arms, unlike the sibling CreateGetValueExpression(…, IProperty, …) this
        // otherwise mirrors: those exist for a leaf bound to a raw BSON type, and a bare field leaf
        // (MongoFieldExpression/MongoOuterFieldExpression) is always backed by a scalar IProperty (an
        // array/owned leaf is a different MongoExpression kind and is filtered out by the switch above). The
        // two paths differ deliberately; if a raw-BSON field leaf ever becomes possible, add the arms rather
        // than assuming this read covers it.
        var valueExpression = BsonBinding.CreateGetPropertyValueAtPath(
            _docParameter, fieldInfo.ElementName.Split('.'), fieldInfo.Property, projectionBindingExpression.Type);

        // MANDATORY, not defensive, and the exact mirror of what the native leg does after the same read:
        // CreateGetPropertyValueAtPath's generic argument is `property.IsNullable ? mappedType.MakeNullable()
        // : mappedType`, so for a `.Value`-peeled leaf (`x.o.Rank.Value` over an `int? Rank`) the read comes
        // back typed `int?` while the binding expects `int`. Handing that back unconverted is a hard
        // shaper-compile type mismatch, not a wrong value. Pinned by NativeJoinTests
        // .Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly's `.Value` case.
        result = valueExpression.Type == projectionBindingExpression.Type
            ? valueExpression
            : Expression.Convert(valueExpression, projectionBindingExpression.Type);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ignores <paramref name="alias"/> entirely and reads <paramref name="field"/> at its own NATURAL
    /// root-relative document location instead — the mirror of <c>TryResolveFieldAccess</c>'s ordinary
    /// scalar-leaf read, redirected through the driver's own "_outer" sub-document when
    /// <see cref="MongoQueryExpression.UsesDriverJoinFields"/>, exactly like every other read in this visitor.
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

        return CreateGetValueExpression(docExpr, field.Property, memberType);
    }

    /// <summary>
    /// Binds a scalar property access on a singleton (reference) navigation in a mixed projection
    /// (e.g. <c>select new { A = o.Customer, B = o.Customer.City }</c>, or <c>EF.Property&lt;T&gt;(o.Customer,
    /// "City")</c> for a shadow property, which has no CLR member and so can only be read this way). The mapped
    /// expression is either a <see cref="MemberExpression"/> (EF Core's <c>PropertyExpression</c>) or an
    /// <c>EF.Property</c> <see cref="MethodCallExpression"/>, whose source is the navigation target's
    /// <see cref="StructuralTypeShaperExpression"/>. Because the accessed property belongs to the navigation
    /// target rather than the query root, it is read from the joined sub-document: the driver's native LeftJoin
    /// places the lone joined reference under <c>"_inner"</c>. Returns <see langword="false"/> for anything that
    /// is not such a navigation property access so the caller can fall back to its other resolution paths.
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

        // Only handle a property access on a JOINED navigation target. A property access on the root entity's
        // own shaper (e.g. select new { o, o.CustomerID }) is a root-level property and is handled by the
        // existing TryResolveFieldAccess path, which reads it from "_outer". Reading it from "_inner" here
        // would return the wrong (joined) document's value.
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
