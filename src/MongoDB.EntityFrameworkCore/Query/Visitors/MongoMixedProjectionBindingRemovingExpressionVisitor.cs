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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
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
    private readonly bool _pushedDownSelectRetained;

    /// <param name="rootEntityType">The root entity type being materialized.</param>
    /// <param name="queryExpression">The query being materialized.</param>
    /// <param name="docParameter">The parameter holding the current document.</param>
    /// <param name="trackingBehavior">The query tracking behavior.</param>
    /// <param name="pushedDownSelectRetained">
    /// <see langword="true"/> when the projecting <c>Select</c> could not be stripped because an operator composes over its
    /// result (<c>Distinct</c>, <c>Union</c>, ...). The driver then returns projected documents keyed by member name, which
    /// carry an owned-reference leaf's sub-document but not its owner's key.
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
        _pushedDownSelectRetained = pushedDownSelectRetained;
    }

    /// <inheritdoc />
    protected override bool ReadsUnprojectedDocuments => true;

    /// <inheritdoc />
    protected override bool OwnerKeyMayBeAbsent => _ownerKeyMayBeAbsent;

    /// <inheritdoc />
    protected override string? ResolveProjectedOwnedReferenceAlias(INavigation navigation)
    {
        if (!_pushedDownSelectRetained || _queryExpression.Select.JoinScope is not null || _queryExpression.UsesDriverJoinFields
            || _queryExpression.CapturedExpression is not { } captured
            || !_rootEntityType.IsAssignableFrom(navigation.DeclaringEntityType))
        {
            return null;
        }

        var finder = new RetainedSelectMemberFinder(navigation.Name);
        finder.Visit(captured);
        return finder.Ambiguous ? null : finder.Alias;
    }

    // Whether the retained Select assigns `member` a case-mapping call (`V = x.S.ToUpper()`).
    private sealed class RetainedSelectCaseMappingFinder(string member) : System.Linq.Expressions.ExpressionVisitor
    {
        private bool _found;

        // Whether any Select in the chain assigns a member named `member`: the key the driver writes it under. A Select that was
        // stripped (the outer rename) leaves the documents keyed by the inner Select's names.
        public static bool AssignsMember(Expression captured, string member)
        {
            var finder = new RetainedSelectCaseMappingFinder(member) { _collectAny = true };
            finder.Visit(captured);
            return finder._found;
        }

        private bool _collectAny;

        public static bool Maps(Expression captured, string member)
        {
            var finder = new RetainedSelectCaseMappingFinder(member);
            finder.Visit(captured);
            return finder._found;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(Queryable.Select) && node.Arguments.Count == 2
                && node.Arguments[1].UnwrapLambdaFromQuote() is { Parameters.Count: 1 } selector)
            {
                switch (selector.Body)
                {
                    case NewExpression { Members: { } members } newExpression:
                        for (var i = 0; i < members.Count; i++)
                        {
                            Check(members[i].Name, newExpression.Arguments[i]);
                        }

                        break;

                    case MemberInitExpression memberInit:
                        foreach (var binding in memberInit.Bindings.OfType<MemberAssignment>())
                        {
                            Check(binding.Member.Name, binding.Expression);
                        }

                        break;
                }
            }

            return base.VisitMethodCall(node);
        }

        private void Check(string name, Expression value)
        {
            while (value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            {
                value = convert.Operand;
            }

            if (name == member && (_collectAny || (value is MethodCallExpression call && NativeCaseMapping.IsCaseMappingCall(call))))
            {
                _found = true;
            }
        }
    }

    // The member the retained `Select(x => new { R = x.O, ... })` assigns the navigation `O` to: the key the driver's $project writes it under.
    private sealed class RetainedSelectMemberFinder(string navigationName) : System.Linq.Expressions.ExpressionVisitor
    {
        public string? Alias { get; private set; }

        public bool Ambiguous { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(Queryable.Select) && node.Arguments.Count == 2
                && node.Arguments[1].UnwrapLambdaFromQuote() is { Parameters.Count: 1 } selector)
            {
                switch (selector.Body)
                {
                    case NewExpression { Members: { } members } newExpression:
                        for (var i = 0; i < members.Count; i++)
                        {
                            Record(members[i].Name, newExpression.Arguments[i], selector.Parameters[0]);
                        }

                        break;

                    case MemberInitExpression memberInit:
                        foreach (var binding in memberInit.Bindings.OfType<MemberAssignment>())
                        {
                            Record(binding.Member.Name, binding.Expression, selector.Parameters[0]);
                        }

                        break;
                }
            }

            return base.VisitMethodCall(node);
        }

        private void Record(string member, Expression value, ParameterExpression parameter)
        {
            while (value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            {
                value = convert.Operand;
            }

            var matches = value switch
            {
                MemberExpression { Expression: var owner, Member.Name: var name } => owner == parameter && name == navigationName,
                MethodCallExpression { Method: var efProperty, Arguments: [{ } source, ConstantExpression { Value: string efName }, ..] }
                    => efProperty.IsEFPropertyMethod() && source == parameter && efName == navigationName,
                _ => false
            };

            if (matches)
            {
                // The same member in two Selects (a Union of identical projections) is one key; a different one is ambiguous.
                if (Alias is not null && Alias != member)
                {
                    Ambiguous = true;
                }

                Alias = member;
            }
        }
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        // Whole documents come back here (no $project), so the server-computed alias doesn't exist: evaluate the
        // client-side form over the document reads instead.
        if (extensionExpression is NativeComputedLeafExpression computedLeaf)
        {
            return Visit(computedLeaf.ClientExpression);
        }

        if (extensionExpression is ProjectionBindingExpression projectionBindingExpression)
        {
            if (projectionBindingExpression.ProjectionMember != null)
            {
                // A member-less construction whose arguments all registered under this one member would read each as the
                // last: decline loudly rather than return another argument's value.
                if (_queryExpression.AliasedConstructionMembers.TryGetValue(
                        projectionBindingExpression.ProjectionMember, out var aliasedConstructionType))
                {
                    throw new InvalidOperationException(
                        $"The projection constructs '{aliasedConstructionType.ShortDisplayName()}' from arguments that "
                        + "can't each be read from the document: its constructor's parameters don't each initialize a "
                        + "distinct property or field of the same name, or a member initializer also assigns one of them. "
                        + "Project into an anonymous type or a type with settable properties instead, or construct it "
                        + "client-side after 'AsEnumerable()'.");
                }

                var mappedExpression = _queryExpression.GetMappedProjection(
                    projectionBindingExpression.ProjectionMember);

                // After ApplyProjection the mapping is Constant(index): unwrap to the stored expression; otherwise use it directly.
                Expression? sourceExpression;
                string? alias;
                if (mappedExpression is ConstantExpression { Value: int })
                {
                    var projection = GetProjection(projectionBindingExpression);
                    alias = projection.Alias;
                    sourceExpression = projection.Expression;
                    if (alias is null)
                    {
                        // A null alias usually means the whole document/entity (`select new { o }`): hand back the
                        // BsonDocument for the entity shaper. A scalar root-property binding (`select p.name.ToArray()`) also
                        // has no alias: resolve it to a field read.
                        if (TryBindArithmeticLeaf(sourceExpression, projectionBindingExpression.Type, out var rootArithmeticRead))
                        {
                            return rootArithmeticRead;
                        }

                        // Bare-body string-to-char-sequence leaf; TryResolveFieldAccess can't resolve an Enumerable.* call and
                        // would return the whole document.
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

                // A scalar member on a singleton (reference) navigation (`select o.Customer.City`): the property belongs to
                // the navigation target, so read it from the joined sub-document (driver LeftJoin: "_inner").
                if (TryBindNavigationMemberAccess(sourceExpression, projectionBindingExpression.Type, out var navMemberRead))
                {
                    return navMemberRead;
                }

                // An unfiltered embedded-collection Count/LongCount (e.g. select new { b, N = b.Posts.Count })
                // mixed alongside a whole entity reference: the driver-LINQ Select was stripped, so count the
                // stored array client-side from the materialized document.
                if (TryBindEmbeddedCollectionCountLeaf(sourceExpression, projectionBindingExpression.Type, out var countRead))
                {
                    return countRead;
                }

                // A constructed sub-entity leaf (MongoDocumentConstructionExpression). The document is whole (no $project
                // alias), so each member is read at its natural root-relative path via ReadDocumentConstructionMember.
                if (sourceExpression is MongoDocumentConstructionExpression documentConstruction)
                {
                    return BuildDocumentConstructionExpression(documentConstruction, alias!);
                }

                // A computed scalar leaf (`p.Name.ToUpper()`, `p.Name.Trim().Length + 1`) registered whole by
                // MongoProjectionBindingExpressionVisitor (IsClientComputedLeaf). Re-evaluate it client-side over its own
                // property reads: a member path would read an element named after the member ("Length"), and operand by
                // operand every operand would read the same value. Before the arithmetic arm on purpose (it subsumes it).
                if (TryBindClientComputedLeaf(sourceExpression, projectionBindingExpression.Type, out var clientLeafRead))
                {
                    return clientLeafRead;
                }

                // A computed-arithmetic leaf beside a whole entity, registered as one raw binary leaf: resolve each operand
                // against the materialized document and rebuild the arithmetic client-side (the driver Select was stripped).
                if (TryBindArithmeticLeaf(sourceExpression, projectionBindingExpression.Type, out var arithmeticRead))
                {
                    return arithmeticRead;
                }

                // A string-to-char-sequence leaf mixed with an owned reference: the Select was stripped, so re-apply the
                // operator over a whole-document read; the alias read below would throw "Document element 'P' is missing".
                if (TryBindStringSequenceLeaf(sourceExpression, projectionBindingExpression.Type,
                        out var stringSequenceRead))
                {
                    return stringSequenceRead;
                }

                var fieldAccess = TryResolveFieldAccess(sourceExpression);

                // A case mapping the driver's retained Select evaluates server-side answers "" for a null source where C# (and EF's null
                // propagation) answers null; the projected document no longer holds the raw value to re-apply it to (EF-469). Fail loudly.
                if (_pushedDownSelectRetained
                    && alias is not null
                    && _queryExpression.Select.JoinScope is null
                    && !_queryExpression.UsesDriverJoinFields
                    && _queryExpression.CapturedExpression is { } retainedCaptured
                    && RetainedSelectCaseMappingFinder.Maps(retainedCaptured, alias))
                {
                    throw new InvalidOperationException(
                        $"The projected value '{alias}' applies ToLower/ToUpper to a string under a Select that an operator (Distinct, a set "
                        + "operation) composes over: the server answers an empty string for a null source where .NET answers null. Apply the case "
                        + "mapping after 'AsEnumerable()' instead.");
                }

                // Over a retained pushed-down Select the driver returns projected documents keyed by member name, so a plain root
                // property reads from its member's key through the property's own serializer (EF-469); its natural element name
                // (`S` for `V = x.S`) is absent. Only a bare field: a computed leaf declined above.
                if (_pushedDownSelectRetained
                    && alias is not null
                    && _queryExpression.Select.JoinScope is null
                    && !_queryExpression.UsesDriverJoinFields
                    && fieldAccess.Property != null
                    && _queryExpression.CapturedExpression is { } retainedMembers
                    && RetainedSelectCaseMappingFinder.AssignsMember(retainedMembers, alias)
                    && (fieldAccess.DocumentExpression is null || ReferenceEquals(fieldAccess.DocumentExpression, _docParameter)))
                {
                    return BsonBinding.CreateGetPropertyValueAtPath(
                            _docParameter, [alias], fieldAccess.Property, projectionBindingExpression.Type)
                        .ConvertIfRequired(projectionBindingExpression.Type);
                }

                if (fieldAccess.Property != null)
                {
                    if (fieldAccess.DocumentExpression is ParameterExpression parameterExpression
                        && fieldAccess.MemberInfo != null
                        && fieldAccess.MemberInfo.DeclaringType?.IsAssignableFrom(parameterExpression.Type) == true)
                    {
                        var memberAccess = Expression.MakeMemberAccess(parameterExpression, fieldAccess.MemberInfo);
                        return memberAccess.ConvertIfRequired(projectionBindingExpression.Type);
                    }

                    return CreateGetValueExpression(
                        ResolveSourceDocument(fieldAccess.DocumentExpression),
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
    /// Resolves an index-bound scalar projection leaf against the whole document by its root-relative path rather than its
    /// projection alias.
    /// </summary>
    /// <remarks>
    /// Index-bound leaves (from a native <c>Join</c>'s <c>BindResultMember</c>) otherwise fall to the base visitor's alias
    /// read, wrong against a whole document when alias and path differ: <c>new { o, r.Total }</c> throws "Document element
    /// 'Total' is missing", <c>new { N = o.Name, r }</c> silently reads <see langword="null"/>. Only the last path segment
    /// is read through the leaf's <c>IProperty</c>; a missing intermediate yields <see langword="null"/> (an unmatched
    /// left-outer row reads as a null scalar). No-op when alias equals path or with <c>UsesDriverJoinFields</c>.
    /// </remarks>
    private bool TryBindNativeFieldLeafAsDocumentPath(
        ProjectionBindingExpression projectionBindingExpression, out Expression result)
    {
        result = null!;

        // Gated on the join, not just alias/path disagreement: BindProjectionByAlias and BindGroupMember also produce
        // index-bound leaves, where a path read would silently return a raw field instead of failing loudly.
        if (_queryExpression.Select.JoinScope is null || _queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var projection = GetProjection(projectionBindingExpression);
        if (projection.Alias is not { } alias)
        {
            return false;
        }

        // MongoOuterFieldExpression (a join scope's outer-side scalar) is as root-relative as MongoFieldExpression.
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

        // Same invariant as the base alias read. Nullability may differ either way (a `Nullable<T>.Value` leaf stages the
        // nullable property), so compare unwrapped types; a genuine mismatch would deserialize through the wrong serializer.
        ThrowIfAliasedTypeMismatch(fieldInfo.Property, projectionBindingExpression.Type);

        // No BsonArray/BsonDocument arms (unlike CreateGetValueExpression): a bare field leaf is always a scalar IProperty.
        var valueExpression = BsonBinding.CreateGetPropertyValueAtPath(
            _docParameter, fieldInfo.ElementName.Split('.'), fieldInfo.Property, projectionBindingExpression.Type);

        // Mandatory: a `.Value`-peeled leaf (`x.o.Rank.Value` over `int? Rank`) reads `int?` but the binding expects `int`
        // (shaper-compile type mismatch). Pinned by NativeJoinTests.Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly.
        result = valueExpression.ConvertIfRequired(projectionBindingExpression.Type);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ignores <paramref name="alias"/> and reads <paramref name="field"/> at its natural root-relative location (through
    /// "_outer" when <see cref="MongoQueryExpression.UsesDriverJoinFields"/>).
    /// </remarks>
    protected override Expression ReadDocumentConstructionMember(
        MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
        Type memberType)
    {
        var docExpr = ResolveSourceDocument(null);

        // A join-scope member's ElementName is a dotted path relative to docExpr ("_lookup_Customer.CustomerID"): walk it with
        // the multi-segment helper (an absent intermediate, an unmatched left-outer row, reads null). Dispatching on the dot is
        // safe only because TryGetDocumentConstructionLeaf declines dotted root-relative members; if relaxed, carry provenance.
        if (field.ElementName.Contains('.'))
        {
            return BsonBinding.CreateGetPropertyValueAtPath(docExpr, field.ElementName.Split('.'), field.Property, memberType);
        }

        return CreateGetValueExpression(docExpr, field.Property, memberType);
    }

    /// <summary>
    /// Binds a scalar member access on a singleton (reference) navigation in a mixed projection
    /// (e.g. <c>select new { A = o.Customer, B = o.Customer.City }</c>). The mapped expression is a
    /// <see cref="MemberExpression"/> (EF Core's <c>PropertyExpression</c>) whose source is the navigation
    /// target's <see cref="StructuralTypeShaperExpression"/>. Because the accessed property belongs to the
    /// navigation target rather than the query root, it is read from the joined sub-document: the driver's
    /// native LeftJoin places the lone joined reference under <c>"_inner"</c>. Returns <see langword="false"/>
    /// for anything that is not such a navigation member access so the caller can fall back to its other
    /// resolution paths.
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

        // Only a joined navigation target: a root-entity property (`select new { o, o.CustomerID }`) is read from "_outer" by
        // TryResolveFieldAccess; "_inner" would return the joined document's value.
        if (property == null || shaper.StructuralType == _rootEntityType)
        {
            return false;
        }

        // Only the driver-native single-reference join shape (joined document under "_inner") is supported here.
        if (!_queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var innerDoc = CreateGetValueExpression(_docParameter, "_inner", false, typeof(BsonDocument));
        result = CreateGetValueExpression(innerDoc, property, resultType);
        return true;
    }

    /// <summary>
    /// Binds an unfiltered embedded (owned) collection <c>Count</c>/<c>LongCount</c> projection leaf (e.g.
    /// <c>select new { b, N = b.Posts.Count }</c>). <see cref="MongoProjectionBindingExpressionVisitor"/> maps
    /// such a leaf as <c>source == null ? default : Count(AsQueryable(EF.Property(shaper, nav)))</c>; here the
    /// stored array is read from the materialized document and its length taken, treating a missing or null
    /// array as empty. Returns <see langword="false"/> for any other shape (including a filtered count).
    /// </summary>
    private bool TryBindEmbeddedCollectionCountLeaf(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        if (mappedExpression is not ConditionalExpression
            {
                IfFalse: MethodCallExpression
                {
                    Method.Name: nameof(Queryable.Count) or nameof(Queryable.LongCount),
                    Arguments: [MethodCallExpression { Method.Name: nameof(Queryable.AsQueryable), Arguments: [var source] }]
                }
            }
            || source is not MethodCallExpression efPropertyCall
            || !efPropertyCall.Method.IsEFPropertyMethod()
            || efPropertyCall.Arguments[1] is not ConstantExpression { Value: string navigationName }
            || efPropertyCall.Arguments[0] is not StructuralTypeShaperExpression { StructuralType: IEntityType owner }
            || owner != _rootEntityType
            || owner.FindNavigation(navigationName) is not { } navigation
            || !navigation.IsEmbedded())
        {
            return false;
        }

        var array = BsonBinding.CreateGetBsonArray(_docParameter, navigation.TargetEntityType.GetContainingElementName()!);
        Expression count = Expression.Property(
            Expression.Coalesce(array, Expression.New(typeof(BsonArray))), nameof(BsonArray.Count));
        result = count.Type == resultType ? count : Expression.Convert(count, resultType);
        return true;
    }

    // Client-evaluated computed leaf. Every scalar property access becomes a document read and the rest (string/Math calls,
    // Length, arithmetic, comparisons, casts, conditionals, coalesces) runs client-side as written, so every operand reads
    // its own value. C# answers what EF answers except where EF propagates a null that C# dereferences: a string instance
    // call on a null receiver answers null where its result is nullable (NullPropagatingReceiverRewriter), and where the
    // value is read nullable (a nullable cast, or the leaf's own reference type) the cast answers null when a document
    // read it null-propagates from is null (NullPropagatingCastRewriter). A non-nullable value over a null
    // (`p.Name.Length`) throws, as EF does. Declines if any entity reference is left, and over a retained pushed-down
    // Select (Distinct, set op): the driver then returns projected documents lacking the properties the leaf reads.
    private bool TryBindClientComputedLeaf(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        // Stricter than TryBindArithmeticLeaf's guard (which admits a driver Join's whole { _outer, _inner } documents): any
        // retained Select declines here, leaving a Join's arithmetic leaf to TryBindArithmeticLeaf after this arm.
        if (_pushedDownSelectRetained
            || mappedExpression is null || !MongoProjectionBindingExpressionVisitor.IsClientComputedLeaf(mappedExpression))
        {
            return false;
        }

        var rewriter = new ClientPropertyReadRewriter(this);
        var rewritten = rewriter.Visit(mappedExpression);
        if (rewriter.Unresolved)
        {
            return false;
        }

        var body = rewritten.ConvertIfRequired(resultType);
        var propagator = new NullPropagatingCastRewriter(rewriter.Reads);
        result = propagator.Visit(body);
        if (!resultType.IsValueType)
        {
            // A reference-typed leaf (`p.Name.Length.ToString()`) has no cast to carry the null.
            result = propagator.Guard(body, result);
        }

        result = new NullPropagatingReceiverRewriter(rewriter.Reads).Visit(result)!;
        return true;
    }

    // `receiver == null ? null : receiver.M(args)` for every string instance call with a nullable result, evaluating the
    // receiver once. Document reads are left alone.
    private sealed class NullPropagatingReceiverRewriter(IReadOnlySet<Expression> reads) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
            => node is not null && reads.Contains(node) ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = base.VisitMethodCall(node);
            if (visited is not MethodCallExpression { Object: { } receiver } call
                || receiver.Type != typeof(string)
                || call.Method.DeclaringType != typeof(string)
                || (call.Type.IsValueType && !call.Type.IsNullableValueType()))
            {
                return visited;
            }

            var value = Expression.Variable(typeof(string), "receiver");
            return Expression.Block(
                call.Type,
                [value],
                Expression.Assign(value, receiver),
                Expression.Condition(
                    Expression.Equal(value, Expression.Constant(null, typeof(string))),
                    Expression.Constant(null, call.Type),
                    call.Update(value, call.Arguments)));
        }
    }

    // Makes each nullable cast `(int?)e` in a client-evaluated leaf answer null when EF's value of `e` is null, derivable
    // (and sound) only from document reads that force it: a read, and every null-propagating operator over one (Length, the
    // native string operators, arithmetic, numeric casts, ToString). Anything else (conditional, coalesce, concatenation,
    // comparison) contributes no read and is evaluated as written. Null-checking the reads rather than an operator's
    // receiver keeps the check itself from dereferencing a null (`(int?)p.Name.Trim().Length`).
    private sealed class NullPropagatingCastRewriter(IReadOnlySet<Expression> reads) : ExpressionVisitor
    {
        private static readonly HashSet<string> NullPropagatingStringMethods =
        [
            nameof(string.Substring), nameof(string.Trim), nameof(string.TrimStart), nameof(string.TrimEnd),
            nameof(string.ToUpper), nameof(string.ToLower), nameof(string.ToUpperInvariant),
            nameof(string.ToLowerInvariant), nameof(string.Replace), nameof(string.IndexOf)
        ];

        protected override Expression VisitUnary(UnaryExpression node)
        {
            var visited = base.VisitUnary(node);
            return node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                   && node.Type.IsNullableValueType()
                   && node.Operand.Type.IsValueType
                   && !node.Operand.Type.IsNullableValueType()
                ? Guard(node.Operand, visited)
                : visited;
        }

        // `original` is the pre-rewrite operand (its read nodes are shared with `rewritten`, which is nullable).
        internal Expression Guard(Expression original, Expression rewritten)
        {
            var anyNull = NullForcingReads(original).Distinct()
                .Select(read => (Expression)Expression.Equal(read, Expression.Constant(null, read.Type)))
                .Aggregate((Expression?)null, (acc, isNull) => acc is null ? isNull : Expression.OrElse(acc, isNull));
            return anyNull is null
                ? rewritten
                : Expression.Condition(anyNull, Expression.Constant(null, rewritten.Type), rewritten);
        }

        private IEnumerable<Expression> NullForcingReads(Expression node)
            => node switch
            {
                _ when reads.Contains(node) => node.Type.IsNullableType() ? [node] : [],
                MemberExpression { Expression: { } receiver } member when IsStringLength(member) => NullForcingReads(receiver),
                // A null argument EF propagates is one C# throws on; Replace's new value may be null in C# (it removes).
                MethodCallExpression { Object: { } receiver } call
                    when call.Method.DeclaringType == typeof(string) && NullPropagatingStringMethods.Contains(call.Method.Name)
                    => NullForcingReads(receiver).Concat(
                        (call.Method.Name == nameof(string.Replace) ? call.Arguments.Take(1) : call.Arguments)
                        .SelectMany(NullForcingReads)),
                MethodCallExpression { Object: { } receiver, Method.Name: nameof(ToString), Arguments.Count: 0 }
                    when receiver.Type.IsValueType
                    => NullForcingReads(receiver),
                UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
                    or ExpressionType.Negate or ExpressionType.NegateChecked or ExpressionType.UnaryPlus } unary
                    when unary.Operand.Type.IsValueType
                    => NullForcingReads(unary.Operand),
                BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.AddChecked or ExpressionType.Subtract
                    or ExpressionType.SubtractChecked or ExpressionType.Multiply or ExpressionType.MultiplyChecked
                    or ExpressionType.Divide or ExpressionType.Modulo } arithmetic
                    when arithmetic.Left.Type.IsValueType && arithmetic.Right.Type.IsValueType
                    => NullForcingReads(arithmetic.Left).Concat(NullForcingReads(arithmetic.Right)),
                _ => []
            };
    }

    private static bool IsStringLength(MemberExpression member)
        => member.Member.Name == nameof(string.Length) && member.Member.DeclaringType == typeof(string);

    // Replaces each scalar property access with its document read; anything else still referencing an entity marks the
    // leaf unresolved.
    private sealed class ClientPropertyReadRewriter(MongoMixedProjectionBindingRemovingExpressionVisitor owner) : ExpressionVisitor
    {
        internal bool Unresolved { get; private set; }

        internal HashSet<Expression> Reads { get; } = [];

        protected override Expression VisitMember(MemberExpression node)
        {
            if (IsStringLength(node) && node.Expression is { } receiver)
            {
                return node.Update(Visit(receiver));
            }

            return TryRead(node) ?? base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
            => node.TryGetEFPropertyArguments(out _, out _) ? TryRead(node) ?? MarkUnresolved(node) : base.VisitMethodCall(node);

        protected override Expression VisitParameter(ParameterExpression node) => MarkUnresolved(node);

        protected override Expression VisitExtension(Expression node) => MarkUnresolved(node);

        private Expression? TryRead(Expression node)
        {
            if (!owner.TryReadScalarProperty(node, node.Type, out var read))
            {
                return null;
            }

            Reads.Add(read);
            return read;
        }

        private Expression MarkUnresolved(Expression node)
        {
            Unresolved = true;
            return node;
        }
    }

    // The document a resolved field access reads from: its own, else the root. Under the driver's native Join, root-entity
    // scalars live in "_outer" but the resolver returns the root doc parameter for them, so redirect it.
    private Expression ResolveSourceDocument(Expression? documentExpression)
    {
        var docExpr = documentExpression ?? _docParameter;
        return _queryExpression.UsesDriverJoinFields && ReferenceEquals(docExpr, _docParameter)
            ? CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument))
            : docExpr;
    }

    // Reads a scalar property access (member or EF.Property, on the root entity or a joined navigation target) from the
    // materialized document.
    private bool TryReadScalarProperty(Expression node, Type type, out Expression read)
    {
        if (TryBindNavigationMemberAccess(node, type, out read))
        {
            return true;
        }

        var fieldAccess = TryResolveFieldAccess(node);
        if (fieldAccess.Property is not { } property)
        {
            read = null!;
            return false;
        }

        read = CreateGetValueExpression(ResolveSourceDocument(fieldAccess.DocumentExpression), property, type);
        return true;
    }

    // Binds a string-to-char-sequence leaf (`select new { P = b.City.AsEnumerable(), b.Posts }`, also ToList/ToArray) against
    // the whole document: reads through the source IProperty (converters/BsonRepresentation apply) and rebuilds the call.
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

        // The emit side only admits string-typed sources: a non-string property means the two sides disagree.
        Debug.Assert(property.ClrType == typeof(string),
            $"String-sequence projection leaf resolved to non-string property '{property.Name}' of type '{property.ClrType}'.");

        result = Expression.Call(
            call.Method, CreateGetValueExpression(ResolveSourceDocument(fieldAccess.DocumentExpression), property, typeof(string)));
        result = result.ConvertIfRequired(resultType);

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

        // Over a retained pushed-down Select the driver returns projected documents ({ R, V }) lacking the operands' source
        // elements: every operand would read as missing and the leaf answer a silently wrong value (EF-460). Decline so the
        // query fails loudly (the projected value can't be read by name: $concat answers null where C# treats null as empty).
        // The flag is also set for a driver Join, whose documents are whole ({ _outer, _inner }) and keep client re-evaluation.
        if (_pushedDownSelectRetained && _queryExpression.Select.JoinScope is null && !_queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        if (mappedExpression is not BinaryExpression binaryExpression
            || !NativeProjectionBinder.IsArithmeticLeafShape(binaryExpression))
        {
            return false;
        }

        var left = ResolveArithmeticOperand(binaryExpression.Left);
        var right = ResolveArithmeticOperand(binaryExpression.Right);

        result = Expression.MakeBinary(
            binaryExpression.NodeType, left, right, binaryExpression.IsLiftedToNull, binaryExpression.Method);
        result = result.ConvertIfRequired(resultType);

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

        if (unwrapped is BinaryExpression nestedBinary && NativeProjectionBinder.IsArithmeticLeafShape(nestedBinary))
        {
            Expression nestedResult = Expression.MakeBinary(
                nestedBinary.NodeType,
                ResolveArithmeticOperand(nestedBinary.Left),
                ResolveArithmeticOperand(nestedBinary.Right),
                nestedBinary.IsLiftedToNull,
                nestedBinary.Method);

            return nestedResult.ConvertIfRequired(operand.Type);
        }

        if (TryReadScalarProperty(unwrapped, operand.Type, out var propertyRead))
        {
            return propertyRead;
        }

        var fieldAccess = TryResolveFieldAccess(unwrapped);
        if (fieldAccess.FieldName != null)
        {
            return BsonBinding.CreateGetElementValue(
                fieldAccess.DocumentExpression ?? _docParameter, fieldAccess.FieldName, operand.Type);
        }

        throw new InvalidOperationException(CoreStrings.TranslationFailed(operand.Print()));
    }
}
