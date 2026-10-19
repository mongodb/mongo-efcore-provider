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

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed partial class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Dictionary<ProjectionMember, Expression> _projectionMapping = new();
    private readonly Dictionary<ProjectionMember, Type> _aliasedConstructionMembers = new();
    private readonly Stack<ProjectionMember> _projectionMembers = new();
    private readonly Dictionary<ParameterExpression, CollectionShaperExpression> _collectionShaperMapping = new();
    private readonly Stack<INavigation> _includedNavigations = new();

    private MongoQueryExpression _queryExpression;

    // The members a MemberInit's bindings assign, set only while its NewExpression is visited (see
    // TryGetConstructorArgumentMembers).
    private IReadOnlyCollection<string> _memberInitBindingNames;

    // The selector body passed to this Translate() call. The bare filtered-count rebuild arm uses it to tell a
    // bare-body Count(pred) from the same call nested in a wrapped projection that declined to Fallback.
    private Expression _translatedRootExpression;

    /// <summary>
    /// Perform translation of the <paramref name="expression" /> that belongs to the
    /// supplied <paramref name="queryExpression"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> the expression being translated belongs to.</param>
    /// <param name="expression">The <see cref="Expression"/> being translated.</param>
    /// <returns>The translated expression tree.</returns>
    public Expression Translate(
        MongoQueryExpression queryExpression,
        Expression expression)
    {
        _queryExpression = queryExpression;
        _projectionMembers.Push(new ProjectionMember());
        _translatedRootExpression = expression;
        _aliasedConstructionMembers.Clear();

        var result = Visit(expression);

        _queryExpression.ReplaceProjectionMapping(_projectionMapping);
        _projectionMapping.Clear();
        _queryExpression.ReplaceAliasedConstructionMembers(_aliasedConstructionMembers);
        _aliasedConstructionMembers.Clear();
        _queryExpression = null;
        _translatedRootExpression = null;

        _projectionMembers.Clear();

        return MatchTypes(result, expression.Type);
    }

    /// <summary>
    /// Visits an already re-bound <see cref="IncludeExpression"/> (from
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember</c>) without <see cref="Translate"/>'s
    /// <see cref="MongoQueryExpression.ReplaceProjectionMapping"/> side effect, so the caller's index-based projection
    /// mapping survives. Only the Include handling in <see cref="VisitExtension"/> (register the collection
    /// <c>$lookup</c>, rewrite via <c>RewriteCollectionIncludeForLookup</c>) is needed.
    /// <para>
    /// Still seeds <see cref="_projectionMembers"/>: a nested owned collection navigation (auto-included, no
    /// <c>.Include()</c> needed) reaches <see cref="TryBindNativeArrayProjection"/>, which assumes a non-empty stack
    /// ("Stack empty" otherwise). <see cref="_translatedRootExpression"/> stays null, which its readers treat as a
    /// safe decline. <see cref="_projectionMapping"/> is restored on every return path as a precaution.
    /// </para>
    /// </summary>
    internal Expression VisitIncludeExpression(MongoQueryExpression queryExpression, IncludeExpression includeExpression)
    {
        var previousQueryExpression = _queryExpression;
        _queryExpression = queryExpression;
        _projectionMembers.Push(new ProjectionMember());
        var projectionMappingKeysBefore = _projectionMapping.Keys.ToList();
        try
        {
            return Visit(includeExpression);
        }
        finally
        {
            foreach (var key in _projectionMapping.Keys.Except(projectionMappingKeysBefore).ToList())
            {
                _projectionMapping.Remove(key);
            }
            _projectionMembers.Pop();
            _queryExpression = previousQueryExpression;
        }
    }

    /// <inheritdoc />
    public override Expression Visit(Expression expression)
    {
        switch (expression)
        {
            case null:
                return null;

            // A constructed sub-entity leaf (`new { Book = new Book { Id = e.Id, ... } }`) already translated to a
            // MongoDocumentConstructionExpression. Register the whole node as one member: recursing would map
            // "Book.Id"/"Book.Title" to root-level reads, but natively they live under the "Book" alias.
            // TryGetNativeDocumentConstructionLeaf reuses the emit side's result rather than re-deriving it.
            case NewExpression or MemberInitExpression
                when TryGetNativeDocumentConstructionLeaf(expression, out var documentConstructionLeaf):
                {
                    var constructionMember = GetCurrentProjectionMember();
                    _projectionMapping[constructionMember] = documentConstructionLeaf;
                    return new ProjectionBindingExpression(_queryExpression, constructionMember, expression.Type);
                }

            case NewExpression:
            case MemberInitExpression:
            case StructuralTypeShaperExpression:
            case MaterializeCollectionNavigationExpression:
                return base.Visit(expression);

#if EF8 || EF9
            case ParameterExpression parameterExpression:
                if (_collectionShaperMapping.ContainsKey(parameterExpression))
                {
                    return parameterExpression;
                }
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    return Expression.Call(
                        GetParameterValueMethodInfo.MakeGenericMethod(parameterExpression.Type),
                        QueryCompilationContext.QueryContextParameter,
                        Expression.Constant(parameterExpression.Name));
                }

                throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
#else
            case QueryParameterExpression queryParameter:
                return Expression.Call(
                    GetParameterValueMethodInfo.MakeGenericMethod(queryParameter.Type),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(queryParameter.Name));

            case ParameterExpression parameterExpression:
                return _collectionShaperMapping.ContainsKey(parameterExpression)
                    ? parameterExpression
                    : throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
#endif

            case ConstantExpression:
                return expression;

            // Already bound by index to our query while still native: a native SelectMany's element shaper (built via
            // AddToProjection), reached again through the mandatory `.Select(ti => ti.Inner)` over EF's
            // TransparentIdentifier. Pass it through rather than re-binding (cf. the join-rebinding case in
            // VisitExtension). The Route == Projection guard is load-bearing: a Select after GroupBy/Distinct is also
            // bound by index, but its post-terminal guard already set Route = Fallback so it is reported unsupported
            // rather than reading a stale by-index projection (see
            // NativeGroupByTests.Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data).
            case ProjectionBindingExpression { Index: not null } projectionBindingExpression
                when projectionBindingExpression.QueryExpression == _queryExpression
                     && _queryExpression.Select.Route == NativeRoute.Projection:
                return projectionBindingExpression;

            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type);

            // Arithmetic leaf: register the whole binary node as one member. The default walk would map both operands
            // to the same member and silently produce (A*B)² instead of A*B.
            // Route == Projection confines this to projections the binder accepted in full. A mixed shape like
            // `new { c, Total = c.Age * c.Score }` stays Fallback and goes to the mixed shaper, which can't read a raw
            // BinaryExpression and would read a nonexistent field named after the alias.
            case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                    or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo } binaryExpression
                when _queryExpression.Select.Route == NativeRoute.Projection:
                var arithProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[arithProjectionMember] = binaryExpression;
                return new ProjectionBindingExpression(_queryExpression, arithProjectionMember, expression.Type);

            // Numeric-cast leaf: register the whole Convert as one member. The default walk keeps only the operand, and
            // the read side would decode the $toInt/$toLong/... result with the pre-cast property's serializer (see
            // MongoProjectionBindingRemovingExpressionVisitor's ProjectionBindingExpression case). Route == Projection
            // as for arithmetic. The `Operand is not StructuralTypeShaperExpression` exclusion keeps this disjoint from
            // the navigation Convert(shaper, T) shape VisitMember handles; a NewExpression/MemberInitExpression operand
            // is a reference upcast of a constructed DTO, whose members NativeProjectionBinder binds individually.
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                    Operand: not (StructuralTypeShaperExpression or NewExpression or MemberInitExpression) } castExpression
                when _queryExpression.Select.Route == NativeRoute.Projection:
                var castProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[castProjectionMember] = castExpression;
                return new ProjectionBindingExpression(_queryExpression, castProjectionMember, expression.Type);

            // String-to-char-sequence leaf (`e.City.AsEnumerable()`/ToList()/ToArray()): register the whole call as
            // one member, otherwise the wrapper is dropped and the shaper gets a string where IEnumerable<char>/
            // List<char>/char[] was expected. Route == Projection as for arithmetic.
            case MethodCallExpression stringSequenceCall
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && NativeProjectionBinder.IsStringSequenceMaterializationCall(stringSequenceCall):
                var stringSequenceMember = GetCurrentProjectionMember();
                _projectionMapping[stringSequenceMember] = stringSequenceCall;
                return new ProjectionBindingExpression(_queryExpression, stringSequenceMember, expression.Type);

            // Conditional leaf: register the whole ConditionalExpression as one member; the default walk would write
            // Test/IfTrue/IfFalse to the same member. Route == Projection as for arithmetic.
            case ConditionalExpression when _queryExpression.Select.Route == NativeRoute.Projection:
                var conditionalMember = GetCurrentProjectionMember();
                _projectionMapping[conditionalMember] = expression;
                return new ProjectionBindingExpression(_queryExpression, conditionalMember, expression.Type);

            // Reference-collection-nav First/FirstOrDefault leaf (`a.IdentificationMethods.FirstOrDefault().Method`),
            // staged as a MongoCorrelatedReducerLeaf at emit time. Nav-expansion puts the member access inside the
            // reducer, so the default walk would try to translate the whole chain (including a DbSet root) and throw.
            // Register it opaquely; the read side needs only the alias ("_lookup_<Nav>.<Member>").
            // TryGetCorrelatedReducerLeaf looks it up in CorrelatedReducerLeaves rather than re-deriving admissibility.
            case MethodCallExpression correlatedReducerCandidate
                when TryGetCorrelatedReducerLeaf(correlatedReducerCandidate, out _):
                var reducerMember = GetCurrentProjectionMember();
                _projectionMapping[reducerMember] = correlatedReducerCandidate;

                return new ProjectionBindingExpression(_queryExpression, reducerMember, expression.Type);

            // DateTime/DateTimeOffset .AddXxx(amount) leaf, already a MongoDateAddExpression. Register the whole call;
            // otherwise the start date and amount both write the same member and the alias ends up typed as the amount.
            // IsDateAddMethod is the emit side's own predicate. Route == Projection as for arithmetic.
            case MethodCallExpression dateAddCandidate
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && MongoExpressionTranslator.IsDateAddMethod(dateAddCandidate):
                var dateAddMember = GetCurrentProjectionMember();
                _projectionMapping[dateAddMember] = dateAddCandidate;
                return new ProjectionBindingExpression(_queryExpression, dateAddMember, expression.Type);

            // ToLower/ToUpper (and the Invariant forms) over any string receiver: $toLower/$toUpper are ASCII-only, so
            // the call always runs client-side over the bound receiver (NativeProjectionBinder stages only the
            // receiver), with null propagated. As a projected value it also stays off driver-LINQ push-down (see
            // ProjectionAnalyzer.HasCaseMappingProjectedValue).
            case MethodCallExpression caseMappingCall when NativeProjectionBinder.IsCaseMappingCall(caseMappingCall):
                return NullPropagatingStringCall(caseMappingCall, NullPropagatingReceiver(Visit(caseMappingCall.Object)!));

            // A computed string/math/predicate leaf (`x.S.Substring(1, 2)`, `x.S.Contains("a")`, `!x.S.Contains("a")`,
            // `x.S == null`) that the native $project computes whole. Bind it as usual (receiver to the alias, the
            // call re-applied client-side), so the mixed shaper and driver-LINQ push-down analysis keep working in
            // every query mode, and wrap it so the native alias reader can instead read the computed value once
            // (see NativeComputedLeafExpression). Matched structurally against the leaf the emit side staged for
            // this member, so only the exact subtree the server computed is wrapped.
            case MethodCallExpression or UnaryExpression { NodeType: ExpressionType.Not } or BinaryExpression
                when IsNativeComputedLeaf(expression):
                var computedMember = GetCurrentProjectionMember();
                Expression clientExpression;
                if (IsClientComputedLeaf(expression))
                {
                    // The mixed reader re-evaluates the leaf whole (see the client-computed case below): bound operand
                    // by operand, each operand would read as the last one bound, and the call re-applied over a bound
                    // Length would dereference a null string (`x.S.Length.ToString()` is null for a null S).
                    _projectionMapping[computedMember] = expression;
                    clientExpression = new ProjectionBindingExpression(_queryExpression, computedMember, expression.Type);
                }
                else
                {
                    clientExpression = base.Visit(expression);
                }

                return new NativeComputedLeafExpression(
                    clientExpression, new ProjectionBindingExpression(_queryExpression, computedMember, expression.Type));

            case MethodCallExpression methodCallExpression
                when IsScalarMethodPropertyAccess(methodCallExpression):
                var projMember = GetCurrentProjectionMember();
                _projectionMapping[projMember] = methodCallExpression;

                return new ProjectionBindingExpression(_queryExpression, projMember, expression.Type);

            // A computed-arithmetic leaf (`c.Age * c.Score`) mixed with a whole entity reference (which forces the mixed
            // shaper). Register the binary node whole: the default walk writes both operands to the same ProjectionMember
            // slot, so Age * Score would materialise as Score * Score. Scoped to simple scalar reads / nested arithmetic
            // (IsSimpleArithmeticLeaf); collection-navigation Sum()/Count() must still decompose so their own guards fire.
            case BinaryExpression binaryExpression
                when IsArithmeticNodeType(binaryExpression.NodeType) && IsSimpleArithmeticLeaf(binaryExpression):
                var arithmeticMember = GetCurrentProjectionMember();
                _projectionMapping[arithmeticMember] = binaryExpression;

                return new ProjectionBindingExpression(_queryExpression, arithmeticMember, expression.Type);

            // A computed scalar leaf outside a native projection (`S.IndexOf(T)`, `S + T.ToLower()`,
            // `string.Compare(S, T)`, `(int?)S.Trim().Length`), which only the mixed reader reads: over whole documents
            // it re-evaluates the leaf client-side (MongoMixedProjectionBindingRemovingExpressionVisitor
            // .TryBindClientComputedLeaf), so register it whole, as the arithmetic leaf. The default walk writes every
            // operand to this same member, and each would then read as the last one visited (`T.IndexOf(T)`).
            // Driver-LINQ push-down doesn't read the shaper, so it is unaffected.
            case BinaryExpression or ConditionalExpression or UnaryExpression or MethodCallExpression
                when _queryExpression.Select.Route != NativeRoute.Projection && IsClientComputedLeaf(expression):
                var clientLeafMember = GetCurrentProjectionMember();
                _projectionMapping[clientLeafMember] = expression;
                return new ProjectionBindingExpression(_queryExpression, clientLeafMember, expression.Type);

            default:
                return base.Visit(expression);
        }
    }

    /// <summary>
    /// True when <paramref name="expression"/> is the New/MemberInit that <c>NativeProjectionBinder</c> translated into
    /// a <see cref="MongoDocumentConstructionExpression"/> for the current projection member. Reads the emit side's
    /// result in <c>Select.Projection</c> so the bind side can't admit a shape the emit side declined.
    /// </summary>
    /// <remarks>
    /// Matches on alias + CLR type, not reference equality with
    /// <see cref="MongoDocumentConstructionExpression.OriginalExpression"/>: <see cref="ReplacingExpressionVisitor"/>
    /// has already rebuilt the tree. A projection member position names exactly one leaf.
    /// </remarks>
    private bool TryGetNativeDocumentConstructionLeaf(
        Expression expression, out MongoDocumentConstructionExpression construction)
    {
        // Shared with BindResultMember via MongoSelectDefinition so the two can't drift.
        var found = _queryExpression.Select.TryGetDocumentConstructionProjection(
            GetCurrentProjectionMember().Last?.Name, expression.Type, out var candidate);
        construction = candidate!;
        return found;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is the current member's reference-collection-nav
    /// <c>First</c>/<c>FirstOrDefault</c> leaf staged by <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c>,
    /// looked up by alias in <see cref="MongoQueryExpression.CorrelatedReducerLeaves"/> (same discipline as
    /// <see cref="TryGetNativeDocumentConstructionLeaf"/>). <paramref name="leaf"/> is currently unused by the caller.
    /// </summary>
    private bool TryGetCorrelatedReducerLeaf(Expression expression, out MongoCorrelatedReducerLeaf leaf)
    {
        leaf = null;

        if (_queryExpression.Select.Route != NativeRoute.Projection)
        {
            return false;
        }

        var memberName = GetCurrentProjectionMember().Last?.Name;
        if (memberName is null)
        {
            return false;
        }

        var alias = _queryExpression.Select.TryGetProjectionAlias(memberName, out var overriddenAlias)
            ? overriddenAlias
            : memberName;

        foreach (var candidate in _queryExpression.CorrelatedReducerLeaves)
        {
            if (candidate.Alias == alias)
            {
                leaf = candidate;
                return true;
            }
        }

        return false;
    }

    // `receiver == null ? null : receiver.M(args)`, evaluating the receiver once. The form IsClientCaseMapping
    // recognizes.
    internal static Expression NullPropagatingStringCall(MethodCallExpression call, Expression receiver)
    {
        var value = Expression.Variable(typeof(string), "value");
        var nullString = Expression.Constant(null, typeof(string));
        return Expression.Block(
            typeof(string),
            [value],
            Expression.Assign(value, receiver),
            Expression.Condition(
                Expression.Equal(value, nullString), nullString, Expression.Call(value, call.Method, call.Arguments)));
    }

    /// <summary>
    /// True for the client-side case-mapping form <see cref="Visit"/> produces for <c>x.S.ToLower()</c> et al.
    /// (read by <c>ProjectionAnalyzer.HasCaseMappingProjectedValue</c>).
    /// </summary>
    internal static bool IsClientCaseMapping(Expression expression)
        => expression is BlockExpression { Result: ConditionalExpression { IfFalse: MethodCallExpression call } }
           && NativeProjectionBinder.IsCaseMappingCall(call);

    // Null propagation down a client-side chain of string instance calls (`x.S.Trim()` under `.ToUpper()`), as over
    // the server-computed value. A native computed leaf keeps its raw read; only its client form (which the mixed
    // shaper evaluates over whole documents) is rewritten.
    private static Expression NullPropagatingReceiver(Expression receiver)
        => receiver switch
        {
            NativeComputedLeafExpression computedLeaf
                => new NativeComputedLeafExpression(
                    NullPropagatingReceiver(computedLeaf.ClientExpression), computedLeaf.Binding),
            MethodCallExpression { Object: { Type: var objectType } instance } call
                when objectType == typeof(string) && call.Method.DeclaringType == typeof(string)
                     && call.Type == typeof(string)
                => NullPropagatingStringCall(call, NullPropagatingReceiver(instance)),
            _ => receiver
        };

    private bool IsNativeComputedLeaf(Expression expression)
        => _queryExpression.Select.TryGetProjectionLeaf(GetCurrentProjectionMember().Last?.Name, out var projection)
           && projection.Value.Source is { } source
           && ExpressionEqualityComparer.Instance.Equals(source, expression)
           && projection.Value.Expression is MongoRegexExpression or MongoStringIndexOfExpression or MongoBinaryExpression
               or MongoUnaryExpression or MongoMathExpression or MongoTrimExpression or MongoSubstringExpression
               or MongoReplaceExpression or MongoStringFirstOrLastExpression
               // `x.S.Substring(0, 1) ?? "none"`: the server computes the whole $ifNull, so the client form must not
               // re-apply the left-hand call over the alias (it would answer "n" for a null S).
               or MongoCoalesceExpression
               // `x.S.Length.ToString()` ($toString, null for a null S): re-applying ToString over the alias would read
               // the null as a 0 length and answer "0".
               or MongoConvertExpression
               // A translate-time fold of a computed node (`x.S.ToLower() == "Seattle"` is false): the alias holds
               // the folded value, not the receiver the client form would read.
               or MongoConstantExpression;

    /// <summary>
    /// True for a computed scalar leaf that the mixed reader can re-evaluate client-side over document reads (see
    /// <c>MongoMixedProjectionBindingRemovingExpressionVisitor.TryBindClientComputedLeaf</c>, which calls this to
    /// recognize a leaf registered whole): an operator, conditional, conversion, string/<see cref="Math"/>/value-type
    /// method or member (`x.S.Length`) at the root, at least one property read, and nothing outside
    /// <see cref="ClientLeafChecker"/>'s set. A property read itself, or its <c>Nullable&lt;T&gt;.Value</c>, is not
    /// computed: the read side resolves those as field accesses.
    /// </summary>
    internal static bool IsClientComputedLeaf(Expression expression)
        => expression switch
           {
               BinaryExpression or ConditionalExpression or UnaryExpression => true,
               MethodCallExpression call => !IsPropertyRead(call),
               MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } receiver }
                   when Nullable.GetUnderlyingType(receiver.Type) is not null => false,
               MemberExpression member => !IsPropertyRead(member),
               _ => false
           }
           && ClientLeafChecker.Check(expression, out var hasRead)
           && hasRead;

    // A scalar property read off an entity shaper, directly or through owned reference navigations (`x.S`, `x.O.W`,
    // `EF.Property<string>(x, "S")`): the read side resolves it with TryResolveFieldAccess.
    private static bool IsPropertyRead(Expression expression)
        => expression switch
        {
            MemberExpression { Expression: { } source } member
                => ResolveReadSource(source) is { } entityType && entityType.FindProperty(member.Member) != null,
            MethodCallExpression call when call.TryGetEFPropertyArguments(out var source, out var name)
                => ResolveReadSource(source) is { } entityType && entityType.FindProperty(name) != null,
            _ => false
        };

    private static IEntityType ResolveReadSource(Expression source)
    {
        source = source.RemoveConvert();
        if (source is StructuralTypeShaperExpression { StructuralType: IEntityType entityType })
        {
            return entityType;
        }

        var (owner, navigationName) = source switch
        {
            MemberExpression { Expression: { } memberOwner } member => (memberOwner, member.Member.Name),
            MethodCallExpression call when call.TryGetEFPropertyArguments(out var callOwner, out var name) => (callOwner, name),
            _ => (null, null)
        };

        return owner != null
               && ResolveReadSource(owner)?.FindNavigation(navigationName) is { IsCollection: false } navigation
               && navigation.IsEmbedded()
            ? navigation.TargetEntityType
            : null;
    }

    /// <summary>
    /// The admitted-node set for a client-evaluated leaf: constants; property reads (<see cref="IsPropertyRead"/>);
    /// instance members of strings and value types; methods declared on <see cref="string"/>, <see cref="Math"/> or a
    /// value type; unary conversions/negation/not; binary operators (no coalesce conversion lambda); conditionals. Any
    /// other node (a parameter, lambda, subquery, construction, shaper used as a value, or other extension node) rejects
    /// the leaf, so entity/collection materialization and collection-navigation aggregates keep the normal walk.
    /// </summary>
    private sealed class ClientLeafChecker : ExpressionVisitor
    {
        private bool _hasRead;
        private bool _rejected;

        internal static bool Check(Expression expression, out bool hasRead)
        {
            var checker = new ClientLeafChecker();
            checker.Visit(expression);
            hasRead = checker._hasRead;
            return !checker._rejected;
        }

        public override Expression Visit(Expression node)
        {
            if (_rejected || node is null)
            {
                return node;
            }

            if (IsPropertyRead(node))
            {
                _hasRead = true;
                return node;
            }

            switch (node)
            {
                case ConstantExpression:
                case ConditionalExpression:
                case BinaryExpression { NodeType: not ExpressionType.ArrayIndex, Conversion: null }:
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Not
                    or ExpressionType.Negate or ExpressionType.NegateChecked or ExpressionType.UnaryPlus }:
                case MemberExpression { Expression: { } receiver }
                    when receiver.Type == typeof(string) || receiver.Type.IsValueType:
                case MethodCallExpression call
                    when !call.Method.IsGenericMethod
                         && (call.Method.DeclaringType == typeof(string)
                             || call.Method.DeclaringType == typeof(Math)
                             || call.Method.DeclaringType?.IsValueType == true):
                    return base.Visit(node);

                default:
                    _rejected = true;
                    return node;
            }
        }
    }

    private static bool IsArithmeticNodeType(ExpressionType nodeType)
        => nodeType is ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
            or ExpressionType.Divide or ExpressionType.Modulo;

    private static bool IsSimpleArithmeticLeaf(Expression expression)
    {
        expression = expression.RemoveConvert();

        return expression switch
        {
            ConstantExpression => true,
            MemberExpression => true,
            MethodCallExpression methodCallExpression when methodCallExpression.TryGetEFPropertyArguments(out _, out _) => true,
            BinaryExpression binaryExpression when IsArithmeticNodeType(binaryExpression.NodeType) =>
                IsSimpleArithmeticLeaf(binaryExpression.Left) && IsSimpleArithmeticLeaf(binaryExpression.Right),
            _ => false,
        };
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            // Whole-root-entity leaf in a native projection ($project emits {"c": "$$ROOT"}). Emit side
            // (NativeProjectionBinder.TryTranslateLeaf) matches on CLR type + parameter identity; this side on entity
            // type + `Index: null`. An index-bound root shaper only arises for a join's inner entity (including a
            // self-join) and goes to the StructuralTypeShaperExpression arm below. Any other mismatch fails loudly: the
            // shaper would look for elements $project never emitted and throw on the missing `_id`.
            case StructuralTypeShaperExpression nativeRootShaper
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && nativeRootShaper.StructuralType == _queryExpression.CollectionExpression.EntityType
                     && nativeRootShaper.ValueBufferExpression is ProjectionBindingExpression { Index: null } rootBinding:
                {
                    var entityProj = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                        rootBinding.ProjectionMember);
                    var member = GetCurrentProjectionMember();
                    _projectionMapping[member] = entityProj;
                    return nativeRootShaper.Update(
                        new ProjectionBindingExpression(_queryExpression, member, typeof(ValueBuffer)));
                }

            case StructuralTypeShaperExpression structuralTypeShaperExpression:
                {
                    var projectionBindingExpression =
                        (ProjectionBindingExpression)structuralTypeShaperExpression.ValueBufferExpression;

                    EntityProjectionExpression entityProjection;
                    if (projectionBindingExpression.Index is int existingIndex
                        && projectionBindingExpression.QueryExpression == _queryExpression)
                    {
                        // Already bound by index to our query expression (e.g., from join rebinding)
                        entityProjection = (EntityProjectionExpression)_queryExpression.Projection[existingIndex].Expression;
                    }
                    else
                    {
                        entityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                            projectionBindingExpression.ProjectionMember);
                    }

                    return structuralTypeShaperExpression.Update(
                        new ProjectionBindingExpression(
                            _queryExpression, _queryExpression.AddToProjection(entityProjection), typeof(ValueBuffer)));
                }

            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                if (TryBindNativeArrayProjection(materializeCollectionNavigationExpression, out var arrayShaper))
                {
                    return arrayShaper;
                }

                if (materializeCollectionNavigationExpression.Navigation is INavigation embeddableNavigation
                    && embeddableNavigation.IsEmbedded())
                {
                    var visited = base.Visit(materializeCollectionNavigationExpression.Subquery);

                    // If the element type has its own embedded navigation, the Select arm above rebuilds this
                    // as an IEnumerable<T>-typed Enumerable.Select rather than the navigation's declared List<T>,
                    // which fails Expression.New's member-type check. Convert is a no-op at runtime:
                    // MongoProjectionBindingRemovingExpressionVisitor discards this shape later for the
                    // correctly-typed CollectionShaperExpression.
                    return visited != null && visited.Type != materializeCollectionNavigationExpression.Type
                        ? Expression.Convert(visited, materializeCollectionNavigationExpression.Type)
                        : visited;
                }

                return base.VisitExtension(materializeCollectionNavigationExpression);

            case IncludeExpression includeExpression:
                {
                    if (includeExpression.Navigation is not INavigation includableNavigation)
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{
                                nameof(includeExpression.Navigation)
                            }' is not supported.");
                    }

                    if (!includableNavigation.IsEmbedded() && includableNavigation.IsCollection)
                    {
                        var lookup = new LookupExpression(includableNavigation);

                        // Captured before prefixing. ObjectArrayProjectionExpression's Name must stay plain: the
                        // prefixing below is $lookup bookkeeping only, and the read side scopes via
                        // ParentAccessExpression. Using the prefixed name double-scopes the path and reads the wrong
                        // field.
                        var plainLookupAlias = lookup.As;

                        // When a LeftJoin restructures the document (_outer/_inner) for a cross-collection reference declaring
                        // entity, the $lookup localField must be prefixed with the inner sub-document path.
                        if (_queryExpression.UsesDriverJoinFields)
                        {
                            var declaringType = includableNavigation.DeclaringEntityType;
                            var rootType = _queryExpression.CollectionExpression.EntityType;
                            if (declaringType == rootType || declaringType.IsOwned())
                            {
                                lookup.LocalField = $"_outer.{lookup.LocalField}";
                                lookup.As = $"_outer.{lookup.As}";
                            }
                            else
                            {
                                lookup.LocalField = $"_inner.{lookup.LocalField}";
                                lookup.As = $"_inner.{lookup.As}";
                            }
                        }
                        else
                        {
                            // Flat multi-lookup mode: chained cross-collection reference navigations
                            // (OrderDetail.Order.Customer.Orders) are root-level $lookup+$unwind stages aliased "_lookup_<Nav>", not the
                            // driver's _outer/_inner. A trailing collection Include declared on one of those intermediates matches
                            // against its sub-document, so localField needs the "_lookup_<Nav>." prefix and "as" nests under the same
                            // intermediate (the shaper reads it relative to ParentAccessExpression).
                            // A collection Include on the inner side of a Join/LeftJoin over a collection navigation
                            // (e.g. Owners.LeftJoin(Orders.Include(r => r.OrderLines), ...)) isn't found by the
                            // reference-only match below; emitted at root it would match the outer _id and every
                            // collection would come back empty. Resolve the intermediate from the Include's own binding
                            // and nest the $lookup inside the join's sub-pipeline, so the array is at
                            // "_lookup_<Join>._lookup_<Collection>" without creating a phantom "_lookup_<Join>"
                            // document for an unmatched left-outer row (which would materialize a key-less entity
                            // instead of null).
                            if (TryGetDeclaringJoinLookup(includeExpression) is
                                { Navigation.IsCollection: true, ForceUnwind: true } declaringJoinLookup)
                            {
                                ExtractNestedIncludePipeline(
                                    includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);

                                // The same Include can be visited once per projection member (`new { a = r, b = r }`);
                                // nest only once.
                                var nestedLookupDocument = BuildLookupDocument(lookup);
                                if (!declaringJoinLookup.PipelineStages.Contains(nestedLookupDocument))
                                {
                                    declaringJoinLookup.PipelineStages.Add(nestedLookupDocument);
                                }

                                // Write-once, as in ExtractNestedIncludePipeline.
                                if (declaringJoinLookup.PipelineKind == LookupPipelineKind.None)
                                {
                                    declaringJoinLookup.PipelineKind = LookupPipelineKind.NestedInclude;
                                }

                                return RewriteCollectionIncludeForLookup(includeExpression, includableNavigation, plainLookupAlias);
                            }

                            var declaringType = includableNavigation.DeclaringEntityType;
                            var intermediateMatches = _queryExpression.GetPendingLookups().Where(
                                l => l.IsReference
                                     && l.ForceUnwind
                                     && l.TargetEntityType == declaringType).ToList();

                            // The intermediate is matched by target entity type, not alias. Several reference lookups targeting one type
                            // (two navigations to it, or a self-referential chain) are ambiguous: guessing would prefix the $lookup with
                            // the wrong "_lookup_<Nav>." path and silently return wrong results, so fail translation instead.
                            if (intermediateMatches.Count > 1)
                            {
                                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                            }

                            var intermediateLookup = intermediateMatches.Count == 1 ? intermediateMatches[0] : null;
                            if (intermediateLookup != null)
                            {
                                lookup.LocalField = $"{intermediateLookup.As}.{lookup.LocalField}";
                                lookup.As = $"{intermediateLookup.As}.{lookup.As}";
                            }
                        }

                        // Extract filtered Include pipeline stages (OrderBy, Skip, Take)
                        // and nested ThenInclude $lookups from the NavigationExpression.
                        ExtractNestedIncludePipeline(includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);

                        // The Include's navigation can share an alias with a join's lookup (e.g. Owners.Include(o =>
                        // o.Orders).Join(db.Orders, o => o.Id, r => r.OwnerId, ...)). AddLookup keeps the first
                        // registration (the join's, which is $unwind-ed), but the Include needs the bare array, so use
                        // a distinct alias.
                        //
                        // Likewise for a collision with a bare Count/LongCount lookup
                        // (LookupExpression.IsBareCountSizeSource): sharing is fine when this Include is unpaged, but a
                        // paged Include (lookup.HasPipeline) would be merged into the Count's entry and corrupt its
                        // $size.
                        var existingIncompatibleLookup = _queryExpression.GetPendingLookups()
                            .FirstOrDefault(l => l.As == lookup.As
                                && (l.ShouldUnwind || (l.IsBareCountSizeSource && lookup.HasPipeline)));
                        var readAlias = plainLookupAlias;
                        if (existingIncompatibleLookup != null)
                        {
                            lookup.As = $"{lookup.As}_include";
                            lookup.RenamedToAvoidJoinCollision = true;
                            readAlias = $"{plainLookupAlias}_include";
                        }

                        _queryExpression.AddLookup(lookup);
                        return RewriteCollectionIncludeForLookup(includeExpression, includableNavigation, readAlias);
                    }

                    _includedNavigations.Push(includableNavigation);
                    var newIncludeExpression = base.VisitExtension(includeExpression);
                    _includedNavigations.Pop();
                    return newIncludeExpression;
                }
            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
        }
    }

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        // A projected cross-collection collection navigation (e.g. select new { ..., Orders = c.Orders.ToList() }).
        // EF Core lowers this to Enumerable.ToList(Queryable.Select(Queryable.Where(DbSet<Target>(), joinPred), selector)).
        // There is no enclosing IncludeExpression to set up the $lookup, so bind it here to a CollectionShaperExpression
        // that reads from a dedicated "_lookup_<Nav>" array, mirroring the cross-collection Include path.
        if (TryBindProjectedCollectionNavigation(methodCallExpression, out var boundCollection))
        {
            return boundCollection;
        }

        // A projected cross-collection collection-navigation Count (e.g. select new { ..., c.Orders.Count }).
        // EF Core lowers this to Queryable.Count(Queryable.Where(DbSet<Target>(), joinPred)) with no enclosing
        // IncludeExpression. Register a "_lookup_<Nav>" $lookup (injected right after the root source) and bind
        // the count as a scalar projection; the EF-to-driver translator rewrites the subtree into a server-side
        // { $size: "$_lookup_<Nav>" }.
        if (TryBindProjectedCollectionNavigationCount(methodCallExpression, out var boundCount))
        {
            return boundCount;
        }

        // Owned collection-navigation count leaf in a native projection (`N = b.Posts.Count` or `Count(pred)`):
        // register the whole call as one member. Otherwise the Queryable switch below rebuilds it as a client-side
        // Enumerable.Count over a `Posts` array the native $project has already dropped.
        //
        // Must run before the fall-through's Update(...) and the switch's Visit(Arguments[0]); it returns whenever
        // Route == Projection and IsCanonicalCount hold, so the switch's Count arm only sees non-native shapes. Kept
        // after TryBindProjectedCollectionNavigationCount as defense-in-depth (the reference-collection $lookup is
        // deduped by NativeProjectionBinder's pendingLookups and AddLookup anyway).
        //
        // Route == Projection as for arithmetic. Matching is by canonical MethodInfo definition (see Query/AGENTS.md):
        // this block returns unconditionally, so a false positive would hijack an unrelated member.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && IsCanonicalCount(methodCallExpression.Method))
        {
            var countProjectionMember = GetCurrentProjectionMember();
            _projectionMapping[countProjectionMember] = methodCallExpression;
            return new ProjectionBindingExpression(_queryExpression, countProjectionMember, methodCallExpression.Type);
        }

        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            var visitedSource = Visit(source);

            StructuralTypeShaperExpression shaperExpression;
            switch (visitedSource)
            {
                case StructuralTypeShaperExpression shaper:
                    shaperExpression = shaper;
                    break;

                case UnaryExpression unaryExpression:
                    shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
                    if (shaperExpression == null || unaryExpression.NodeType != ExpressionType.Convert)
                    {
                        return null;
                    }

                    break;

                case ParameterExpression parameterExpression:
                    if (!_collectionShaperMapping.TryGetValue(parameterExpression, out var collectionShaper))
                    {
                        return null;
                    }

                    shaperExpression = (StructuralTypeShaperExpression)collectionShaper.InnerShaper;
                    break;

                default:
                    return null;
            }

            EntityProjectionExpression innerEntityProjection;
            switch (shaperExpression.ValueBufferExpression)
            {
                // A whole-root-entity leaf is bound by ProjectionMember (Index == null), so resolve via the local
                // mapping; ReplaceProjectionMapping only copies it into the query expression at the end of Translate.
                case ProjectionBindingExpression { Index: null } memberBoundBinding:
                    innerEntityProjection = (EntityProjectionExpression)(
                        _projectionMapping.TryGetValue(memberBoundBinding.ProjectionMember, out var localEntityProj)
                            ? localEntityProj
                            : _queryExpression.GetMappedProjection(memberBoundBinding.ProjectionMember));
                    break;

                case ProjectionBindingExpression innerProjectionBindingExpression:
                    innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                        innerProjectionBindingExpression.Index.Value].Expression;
                    break;

                case UnaryExpression unaryExpression:
                    innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                    break;

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }

            Expression navigationProjection;
            var navigation = _includedNavigations.FirstOrDefault(n => n.Name == memberName);
            if (navigation == null)
            {
                navigationProjection = innerEntityProjection.BindMember(memberName, visitedSource.Type, out var propertyBase);
                if (propertyBase is not INavigation projectedNavigation
                    || (!projectedNavigation.IsEmbedded() && !_includedNavigations.Contains(projectedNavigation)))
                {
                    return null;
                }

                navigation = projectedNavigation;
            }
            else
            {
                navigationProjection = innerEntityProjection.BindNavigation(navigation);
            }

            switch (navigationProjection)
            {
                case EntityProjectionExpression entityProjection:
                    return new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                        nullable: true);

                case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                    {
                        var innerShaperExpression = new StructuralTypeShaperExpression(
                            navigation.TargetEntityType,
                            Expression.Convert(
                                Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                                typeof(ValueBuffer)),
                            nullable: true);

                        return new CollectionShaperExpression(
                            objectArrayProjectionExpression,
                            innerShaperExpression,
                            navigation,
                            innerShaperExpression.StructuralType.ClrType);
                    }

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }
        }

        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(Queryable))
        {
            var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
            var visitedSource = Visit(methodCallExpression.Arguments[0]);

            switch (method.Name)
            {
                case nameof(Queryable.AsQueryable)
                    when genericMethod == QueryableMethods.AsQueryable:
                    // Unwrap AsQueryable
                    return visitedSource;

                case nameof(Queryable.Select)
                    when genericMethod == QueryableMethods.Select:
                    if (visitedSource is not CollectionShaperExpression shaper)
                    {
                        return null;
                    }

                    var lambda = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();

                    _collectionShaperMapping.Add(lambda.Parameters.Single(), shaper);

                    lambda = Expression.Lambda(Visit(lambda.Body), lambda.Parameters);
                    return Expression.Call(
                        EnumerableMethods.Select.MakeGenericMethod(method.GetGenericArguments()),
                        shaper,
                        lambda);

                // Count/LongCount over a materialized List<T> CollectionShaperExpression: MatchTypes leaves it as-is for an
                // IQueryable<T> parameter, so the fall-through's Update(...) throws in every mode. Rebuild against the
                // Enumerable equivalent (bare First/Any/... below). The decline branch uses `break`, not `return null`, which
                // would fold to Expression.Default(int) and silently return 0; `break` re-Visits Arguments[0] (not
                // side-effect-free, see the structural guard at the end of this block), but no known shape reaches it.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithoutPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithoutPredicate:
                    if (visitedSource is not CollectionShaperExpression countShaper)
                    {
                        break;
                    }

                    return Expression.Call(
                        (method.Name == nameof(Queryable.Count)
                            ? EnumerableMethods.CountWithoutPredicate
                            : EnumerableMethods.LongCountWithoutPredicate)
                        .MakeGenericMethod(method.GetGenericArguments()),
                        countShaper);

                // The same rebuild for bare First/FirstOrDefault/Single/SingleOrDefault/Any (see the Count arm above
                // for `break` vs `return null`). Predicated overloads and Sum/Min/Max/Average are out of scope. Unlike
                // Count, EF doesn't fuse a trailing member access: `b.Posts.First().Heading` becomes `b.Posts.Select(o
                // => o.Heading).First()`, so the source is often the rebuilt Enumerable.Select rather than a
                // CollectionShaperExpression. TryRebuildAsEnumerableSource accepts both while still only firing on
                // sources that would otherwise crash.
                case nameof(Queryable.First)
                    when genericMethod == QueryableMethods.FirstWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var firstSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.FirstWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        firstSource);

                case nameof(Queryable.FirstOrDefault)
                    when genericMethod == QueryableMethods.FirstOrDefaultWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var firstOrDefaultSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.FirstOrDefaultWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        firstOrDefaultSource);

                case nameof(Queryable.Single)
                    when genericMethod == QueryableMethods.SingleWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var singleSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.SingleWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        singleSource);

                case nameof(Queryable.SingleOrDefault)
                    when genericMethod == QueryableMethods.SingleOrDefaultWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var singleOrDefaultSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.SingleOrDefaultWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        singleOrDefaultSource);

                case nameof(Queryable.Any)
                    when genericMethod == QueryableMethods.AnyWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var anySource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.AnyWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        anySource);

                // The same rebuild for predicated Count/LongCount (bare `Select(b => b.Posts.Count(p => ...))`, optionally behind
                // an arithmetic/cast spine). A native filtered count is claimed earlier, so only non-native shapes get here.
                // The predicate is unquoted (UnwrapLambdaFromQuote) and deliberately not re-Visited: it runs client-side over
                // materialized instances. Decline is `break`, not `return null` (see the Count arm above).
                //
                // Guards, all needed:
                // - ContainsQueryParameter: a captured local stays an unresolved EF query-parameter node (the lambda
                //   isn't re-Visited) and would throw "must be reducible node" at compile time instead of a clean
                //   decline.
                // - IsReachableThroughArithmeticSpine(_translatedRootExpression, ...): wrapped projections whose
                //   element predicate declined to Fallback for other reasons (non-renderable like StartsWith,
                //   primitive-element, the Where(pred).Count() shape; see NativeOwnedCollectionFilteredCountTests) also
                //   reach here with a genuine CollectionShaperExpression. Only reachability distinguishes the wrapped
                //   non-renderable case.
                // - ContainsShaperReference: a bare correlated predicate passes reachability but references the outer
                //   shaper, which reachability can't detect.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithPredicate:
                    if (visitedSource is not CollectionShaperExpression filteredCountShaper
                        || !IsReachableThroughArithmeticSpine(_translatedRootExpression, methodCallExpression))
                    {
                        break;
                    }

                    var filteredCountLambda = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();
                    if (ContainsQueryParameter(filteredCountLambda.Body) || ContainsShaperReference(filteredCountLambda.Body))
                    {
                        break;
                    }

                    return Expression.Call(
                        (method.Name == nameof(Queryable.Count)
                            ? EnumerableMethods.CountWithPredicate
                            : EnumerableMethods.LongCountWithPredicate)
                        .MakeGenericMethod(method.GetGenericArguments()),
                        filteredCountShaper,
                        filteredCountLambda);
            }

            // No case above claimed this Queryable operator. The generic fall-through would crash in every mode once the
            // source is a materialized collection: Update(...) needs an IQueryable<T> (a List<T> shaper or rebuilt
            // Enumerable.Select isn't one, e.g. `b.Posts.Take(2).Select(...)`), and re-Visiting Arguments[0] re-runs the
            // non-idempotent _collectionShaperMapping.Add. Decline before anything mutates with the usual
            // InvalidOperationException; `return null` would fold to Expression.Default (silent null/empty collection).
            // Keyed on assignability, not per operator, so Skip/ElementAt/Where/... are covered.
            if (visitedSource != null
                && !ReferenceEquals(visitedSource, methodCallExpression.Arguments[0])
                && !method.GetParameters()[0].ParameterType.IsAssignableFrom(visitedSource.Type))
            {
                throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }
        }

        var newObject = Visit(methodCallExpression.Object);
        var newArguments = new Expression[methodCallExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
        {
            var argument = methodCallExpression.Arguments[i];
            var newArgument = Visit(argument);
            newArguments[i] = MatchTypes(newArgument, argument.Type);
        }

        Expression updatedMethodCallExpression = methodCallExpression.Update(
            newObject != null ? MatchTypes(newObject, methodCallExpression.Object?.Type) : null,
            newArguments);

        if (newObject?.Type.IsNullableType() == true && !methodCallExpression.Object.Type.IsNullableType())
        {
            var nullableReturnType = methodCallExpression.Type.MakeNullable();
            if (!methodCallExpression.Type.IsNullableType())
            {
                updatedMethodCallExpression = Expression.Convert(updatedMethodCallExpression, nullableReturnType);
            }

            return Expression.Condition(
                Expression.Equal(newObject, Expression.Default(newObject.Type)),
                Expression.Constant(null, nullableReturnType),
                updatedMethodCallExpression);
        }

        return updatedMethodCallExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitNew(NewExpression newExpression)
    {
        var memberInitBindingNames = _memberInitBindingNames;
        _memberInitBindingNames = null;
        if (newExpression.Arguments.Count == 0) return newExpression;
        var argumentMembers = newExpression.Members ?? TryGetConstructorArgumentMembers(newExpression, memberInitBindingNames);
        var hasMembers = argumentMembers != null;

        // Without a member per argument, every argument registers under the enclosing member. Two arguments
        // registering different expressions there would read back as the last one (see AliasedConstructionMembers).
        // Recorded rather than thrown here: only the mixed reader reads these registrations, and a pushed-down
        // projection never does.
        var ambientMember = !hasMembers && newExpression.Arguments.Count > 1
                            && _queryExpression.Select.Route != NativeRoute.Projection
            ? GetCurrentProjectionMember()
            : null;
        Expression firstRegistration = null;

        var newArguments = new Expression[newExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
        {
            var argument = newExpression.Arguments[i];

            if (hasMembers)
            {
                EnterProjectionMember(argumentMembers[i]);
            }

            Expression registrationBefore = null;
            if (ambientMember != null)
            {
                _projectionMapping.TryGetValue(ambientMember, out registrationBefore);
            }

            var visitedArgument = Visit(argument);

            if (hasMembers)
            {
                ExitProjectionMember();
            }

            if (ambientMember != null
                && _projectionMapping.TryGetValue(ambientMember, out var registration)
                && !ReferenceEquals(registration, registrationBefore))
            {
                if (firstRegistration == null)
                {
                    firstRegistration = registration;
                }
                else if (!ExpressionEqualityComparer.Instance.Equals(firstRegistration, registration))
                {
                    _aliasedConstructionMembers[ambientMember] = newExpression.Type;
                }
            }

            if (visitedArgument == null)
            {
                return null!;
            }

            newArguments[i] = MatchTypes(visitedArgument, argument.Type);
        }

        return newExpression.Update(newArguments);
    }

    // A member-less construction (`new KeyValuePair<string, int>(x.S.ToUpper(), x.S.Length * 10 + x.T.Length)`) gives its
    // arguments no member of their own, so each would be registered under the enclosing member and read back as the
    // last one bound. Outside a native projection (only the mixed reader reads these bindings), when every argument is
    // a client-evaluable scalar (ClientLeafChecker: no entity, collection or parameter), bind each argument under the
    // property or field its constructor parameter initializes by name. Otherwise null: the walk is unchanged. Also null
    // when a wrapping MemberInit assigns one of those members (`new Dto(x.S) { Name = x.T }` over a `name` parameter):
    // the argument and the binding would share one member, and the argument would read the binding's value.
    private IReadOnlyList<MemberInfo> TryGetConstructorArgumentMembers(
        NewExpression newExpression, IReadOnlyCollection<string> memberInitBindingNames)
        => _queryExpression.Select.Route == NativeRoute.Projection
            ? null
            : TryMatchConstructorArgumentMembers(newExpression, memberInitBindingNames);

    /// <summary>
    /// The members <see cref="VisitNew"/> binds a member-less construction's arguments under outside a native
    /// projection (see <see cref="TryGetConstructorArgumentMembers"/>), or <see langword="null"/> when the
    /// arguments can't each be bound under a member of their own. <paramref name="newExpression"/> must already have
    /// the selector parameter replaced by the source shaper, as <see cref="VisitNew"/> sees it. Also asked by
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect</c>, which routes a positional-ctor
    /// projection holding a client case mapping to this binding when it applies.
    /// </summary>
    internal static IReadOnlyList<MemberInfo> TryMatchConstructorArgumentMembers(
        NewExpression newExpression, IReadOnlyCollection<string> memberInitBindingNames)
    {
        if (newExpression.Constructor is not { } constructor
            || newExpression.Arguments.Count < 2)
        {
            return null;
        }

        var candidates = newExpression.Type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is PropertyInfo or FieldInfo)
            .ToList();
        var parameters = constructor.GetParameters();
        var members = new MemberInfo[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!ClientLeafChecker.Check(newExpression.Arguments[i], out _))
            {
                return null;
            }

            var matches = candidates.Where(m => string.Equals(m.Name, parameters[i].Name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToList();
            if (matches is not [var member]
                || members.Contains(member)
                || memberInitBindingNames?.Contains(member.Name) == true)
            {
                return null;
            }

            members[i] = member;
        }

        return members;
    }

    protected override MemberAssignment VisitMemberAssignment(MemberAssignment memberAssignment)
    {
        EnterProjectionMember(memberAssignment.Member);
        var visitedExpression = Visit(memberAssignment.Expression);
        ExitProjectionMember();

        if (visitedExpression == null)
        {
            return null!;
        }

        return memberAssignment.Update(MatchTypes(visitedExpression, memberAssignment.Expression.Type));
    }

    /// <inheritdoc />
    protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
    {
        _memberInitBindingNames = memberInitExpression.Bindings.Select(b => b.Member.Name).ToHashSet();
        Expression newExpression;
        try
        {
            newExpression = Visit(memberInitExpression.NewExpression);
        }
        finally
        {
            _memberInitBindingNames = null;
        }

        if (newExpression == null)
        {
            return null!;
        }

        var newBindings = new MemberBinding[memberInitExpression.Bindings.Count];
        for (var i = 0; i < newBindings.Length; i++)
        {
            if (memberInitExpression.Bindings[i].BindingType != MemberBindingType.Assignment)
            {
                return null!;
            }

            newBindings[i] = VisitMemberBinding(memberInitExpression.Bindings[i]);

            if (newBindings[i] == null)
            {
                return null!;
            }
        }

        return memberInitExpression.Update((NewExpression)newExpression, newBindings);
    }

    protected override Expression VisitMember(MemberExpression memberExpression)
    {
        var innerExpression = Visit(memberExpression.Expression);

        StructuralTypeShaperExpression shaperExpression;
        switch (innerExpression)
        {
            case StructuralTypeShaperExpression shaper:
                shaperExpression = shaper;
                break;

            case UnaryExpression unaryExpression:
                shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
                if (shaperExpression == null
                    || unaryExpression.NodeType != ExpressionType.Convert)
                {
                    return NullSafeUpdate(innerExpression);
                }

                break;

            default:
                return NullSafeUpdate(innerExpression);
        }

        EntityProjectionExpression innerEntityProjection;
        switch (shaperExpression.ValueBufferExpression)
        {
            // Same unconditional .Index.Value deref as in VisitMethodCall, but projection leaves (including Index:
            // null whole-root leaves) never reach here: Visit's MemberExpression arm returns first, and the only
            // base.Visit callers carry non-member nodes. Recheck if that arm ever lets a member access fall through.
            case ProjectionBindingExpression innerProjectionBindingExpression:
                innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                    innerProjectionBindingExpression.Index.Value].Expression;
                break;

            case UnaryExpression unaryExpression:
                // Unwrap EntityProjectionExpression when the root entity is not projected
                innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                break;

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        var navigationProjection = innerEntityProjection.BindMember(
            memberExpression.Member, innerExpression.Type, out var propertyBase);

        if (propertyBase is not INavigation navigation || !navigation.IsEmbedded())
        {
            return NullSafeUpdate(innerExpression);
        }

        switch (navigationProjection)
        {
            case EntityProjectionExpression entityProjection:
                return new StructuralTypeShaperExpression(
                    navigation.TargetEntityType,
                    Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                    nullable: true);

            case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                {
                    var innerShaperExpression = new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(
                            Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                            typeof(ValueBuffer)),
                        nullable: true);

                    return new CollectionShaperExpression(
                        objectArrayProjectionExpression,
                        innerShaperExpression,
                        navigation,
                        innerShaperExpression.StructuralType.ClrType);
                }

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        Expression NullSafeUpdate(Expression expression)
        {
            Expression updatedMemberExpression = memberExpression.Update(
                expression != null ? MatchTypes(expression, memberExpression.Expression.Type) : expression);

            if (expression?.Type.IsNullableType() == true)
            {
                var nullableReturnType = memberExpression.Type.MakeNullable();
                if (!memberExpression.Type.IsNullableType())
                {
                    updatedMemberExpression = Expression.Convert(updatedMemberExpression, nullableReturnType);
                }

                updatedMemberExpression = Expression.Condition(
                    Expression.Equal(expression, Expression.Default(expression.Type)),
                    Expression.Constant(null, nullableReturnType),
                    updatedMemberExpression);
            }

            return updatedMemberExpression;
        }
    }


    /// <inheritdoc />
    protected override ElementInit VisitElementInit(ElementInit elementInit)
        => elementInit.Update(elementInit.Arguments.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
        => newArrayExpression.Update(newArrayExpression.Expressions.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <summary>
    /// Binds an owned entity-collection leaf (<c>Select(b =&gt; new { b.Title, b.Posts })</c>) on the native route,
    /// reading the array from the <c>$project</c> alias. Registers it as one member and returns a
    /// <see cref="CollectionShaperExpression"/> over an <see cref="ArrayAliasProjectionExpression"/>; returns
    /// <see langword="false"/> for any other shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alias isn't stored on the node: <c>MongoQueryExpression.ApplyProjection</c> derives it from this
    /// <see cref="ProjectionMember"/>, as <c>NativeProjectionBinder</c> does on the emit side, so both agree.
    /// </para>
    /// <para>
    /// Runs before descending (not in <see cref="VisitMember"/>): visiting the owner shaper calls
    /// <c>AddToProjection</c>, after which <c>ApplyProjection</c> returns early and sibling leaves fail in
    /// <c>GetProjectionIndex</c> at shaper-compile time. Same invariant as the count and arithmetic leaves.
    /// </para>
    /// <para>
    /// Route == Projection is load-bearing: a mixed/fallback shape fetches whole documents and must read the array
    /// at its document path via the <c>ObjectArrayProjectionExpression</c> arm.
    /// </para>
    /// <para>
    /// Admissibility lives in <see cref="NativeTranslation.NativeProjectionBinder.IsNativeArrayProjectionLeaf"/>,
    /// shared with the emit side; disagreement means silent wrong data, so change it only there.
    /// </para>
    /// <para>
    /// A fresh <see cref="RootReferenceExpression"/> is safe as a <c>_projectionBindings</c>/<c>_ownerMappings</c>
    /// key because <see cref="EntityTypedExpression"/> equality is by <see cref="IEntityType"/>.
    /// </para>
    /// </remarks>
    private bool TryBindNativeArrayProjection(
        MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression,
        out Expression arrayShaper)
    {
        arrayShaper = null;

        // Derived from the same ProjectionMember (and alias override) ApplyProjection uses. A bare selector body has
        // no last member, so without the override this is null and the alias-agreement check below fails.
        var arrayProjectionMember = GetCurrentProjectionMember();
        var arrayMemberName = arrayProjectionMember.Last?.Name;
        var arrayAlias = _queryExpression.Select.TryGetProjectionAlias(arrayMemberName, out var overriddenAlias)
            ? overriddenAlias
            : arrayMemberName;

        if (_queryExpression.Select.Route != NativeRoute.Projection
            || !NativeProjectionBinder.IsNativeArrayProjectionLeaf(
                materializeCollectionNavigationExpression.Navigation as INavigation,
                _queryExpression.CollectionExpression.EntityType,
                arrayAlias))
        {
            return false;
        }

        var navigation = (INavigation)materializeCollectionNavigationExpression.Navigation!;
        var aliasedArray = new ArrayAliasProjectionExpression(
            navigation,
            new RootReferenceExpression(navigation.DeclaringEntityType));

        _projectionMapping[arrayProjectionMember] = aliasedArray;

        var innerShaper = new StructuralTypeShaperExpression(
            navigation.TargetEntityType,
            Expression.Convert(
                Expression.Convert(aliasedArray.InnerProjection, typeof(object)),
                typeof(ValueBuffer)),
            nullable: true);

        arrayShaper = new CollectionShaperExpression(
            new ProjectionBindingExpression(_queryExpression, arrayProjectionMember, aliasedArray.Type),
            innerShaper,
            navigation,
            innerShaper.StructuralType.ClrType);

        return true;
    }

    private ProjectionMember GetCurrentProjectionMember()
        => _projectionMembers.Peek();

    private void EnterProjectionMember(MemberInfo memberInfo)
        => _projectionMembers.Push(_projectionMembers.Peek().Append(memberInfo));

    private void ExitProjectionMember()
        => _projectionMembers.Pop();

    /// <summary>
    /// Decides whether <paramref name="visitedSource"/> (the visited <c>Arguments[0]</c> of a bare
    /// <c>Queryable.First</c>/<c>FirstOrDefault</c>/<c>Single</c>/<c>SingleOrDefault</c>/<c>Any</c>) would crash the
    /// generic fall-through, and if so returns it as the source for the <see cref="Enumerable"/> equivalent.
    /// </summary>
    /// <remarks>
    /// Wider than the Count arm's <c>CollectionShaperExpression</c> check because EF doesn't fuse a trailing member
    /// access into these reducers, so the source is often the rebuilt <c>Enumerable.Select</c>.
    /// </remarks>
    /// <param name="method">The constructed generic <c>Queryable</c> method being visited.</param>
    /// <param name="visitedSource">The already-visited <c>Arguments[0]</c>.</param>
    /// <param name="originalSource">The unvisited <c>Arguments[0]</c>, to detect "nothing changed".</param>
    /// <param name="enumerableSource">On success, <paramref name="visitedSource"/>.</param>
    /// <returns>
    /// <see langword="false"/> (fall through unchanged) when the source is null, unchanged, still assignable to
    /// <c>IQueryable&lt;TSource&gt;</c>, or not assignable to <c>IEnumerable&lt;TSource&gt;</c> (rebuilding would
    /// only trade a clean decline for an <see cref="ArgumentException"/>).
    /// </returns>
    private static bool TryRebuildAsEnumerableSource(
        MethodInfo method, Expression visitedSource, Expression originalSource, out Expression enumerableSource)
    {
        enumerableSource = null;

        if (visitedSource is null || ReferenceEquals(visitedSource, originalSource))
        {
            return false;
        }

        if (method.GetParameters()[0].ParameterType.IsAssignableFrom(visitedSource.Type))
        {
            return false;
        }

        var elementType = method.GetGenericArguments()[0];
        if (!typeof(IEnumerable<>).MakeGenericType(elementType).IsAssignableFrom(visitedSource.Type))
        {
            return false;
        }

        enumerableSource = visitedSource;
        return true;
    }

    /// <summary>
    /// True when <paramref name="target"/> (a filtered <c>Count</c>/<c>LongCount</c>) is <paramref name="root"/>
    /// itself or reachable from it through only arithmetic and numeric <c>Convert</c> nodes, e.g.
    /// <c>Select(b => (b.Posts.Count(pred) + 1) * 2)</c>.
    /// </summary>
    /// <remarks>
    /// Any other node kind declines, since the rebuild can't thread a <see cref="CollectionShaperExpression"/>
    /// through it. A pure read-only walk over the unvisited tree (no <see cref="Visit"/> calls), so it adds no
    /// double-registration hazard.
    /// </remarks>
    private static bool IsReachableThroughArithmeticSpine(Expression root, MethodCallExpression target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root switch
        {
            BinaryExpression
            {
                NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
                    or ExpressionType.Divide or ExpressionType.Modulo
            } binary
                => IsReachableThroughArithmeticSpine(binary.Left, target) || IsReachableThroughArithmeticSpine(binary.Right, target),
            UnaryExpression { NodeType: ExpressionType.Convert } unary
                => IsReachableThroughArithmeticSpine(unary.Operand, target),
            _ => false
        };
    }

    /// <summary>
    /// Whether <paramref name="method"/> is one of the eight canonical <c>Count</c>/<c>LongCount</c> methods
    /// (Queryable and Enumerable, with and without predicate), by reference equality on the generic definition.
    /// </summary>
    /// <remarks>
    /// Reference equality because a false positive at the call site is consequential (see <c>VisitMethodCall</c>).
    /// Non-generic methods decline before <c>GetGenericMethodDefinition</c>, which would throw.
    /// </remarks>
    private static bool IsCanonicalCount(MethodInfo method)
    {
        if (!method.IsGenericMethod)
        {
            return false;
        }

        var definition = method.GetGenericMethodDefinition();

        return definition == QueryableMethods.CountWithoutPredicate
            || definition == QueryableMethods.LongCountWithoutPredicate
            || definition == QueryableMethods.CountWithPredicate
            || definition == QueryableMethods.LongCountWithPredicate
            || definition == EnumerableMethods.CountWithoutPredicate
            || definition == EnumerableMethods.LongCountWithoutPredicate
            || definition == EnumerableMethods.CountWithPredicate
            || definition == EnumerableMethods.LongCountWithPredicate;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> contains an EF query-parameter node (see
    /// <see cref="NativeQueryParameter.TryGetQueryParameterName"/>). A captured local in the bare filtered-count
    /// predicate would survive the non-re-Visited rebuild and throw "must be reducible node" at compile time, so
    /// it is declined first.
    /// </summary>
    private static bool ContainsQueryParameter(Expression expression)
    {
        var detector = new QueryParameterDetector();
        detector.Visit(expression);
        return detector.Found;
    }

    private sealed class QueryParameterDetector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression Visit(Expression node)
        {
            if (Found || node is null)
            {
                return node;
            }

            if (NativeQueryParameter.TryGetQueryParameterName(node, out _))
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    /// <summary>
    /// Whether <paramref name="expression"/> contains a shaper node (<see cref="StructuralTypeShaperExpression"/>,
    /// <see cref="ProjectionBindingExpression"/>, <see cref="EntityProjectionExpression"/>). A bare correlated
    /// predicate (<c>b.Posts.Count(p => p.Title == b.Title)</c>) passes the reachability check, but
    /// <c>ReplacingExpressionVisitor</c> has turned <c>b</c> into a shaper that would survive the non-re-Visited
    /// rebuild and fail with a confusing <c>KeyNotFoundException</c> instead of a clean decline.
    /// </summary>
    private static bool ContainsShaperReference(Expression expression)
    {
        var detector = new ShaperReferenceDetector();
        detector.Visit(expression);
        return detector.Found;
    }

    private sealed class ShaperReferenceDetector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression Visit(Expression node)
        {
            if (Found || node is null)
            {
                return node;
            }

            if (node is StructuralTypeShaperExpression or ProjectionBindingExpression or EntityProjectionExpression)
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    /// <summary>
    /// Checks whether a method call expression represents a scalar property access that should
    /// be stored in the projection mapping (like <see cref="MemberExpression"/>), rather than
    /// being fully visited. This covers <c>EF.Property</c> (for non-navigation properties) and
    /// <c>Mql.Field</c> calls.
    /// </summary>
    private static bool IsScalarMethodPropertyAccess(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            // The selector parameter is already a shaper, possibly Convert-wrapped when EF.Property's receiver is an
            // unconstrained generic type (e.g.
            // NorthwindMiscellaneousQueryMongoTest.Select_Property_when_shadow_unconstrained_generic_method); without
            // RemoveConvert() the call isn't registered as a leaf and silently returns null. No Route guard,
            // deliberately: the read side (TryResolveFieldAccess / TryResolveFieldAccessSource) also unwraps Convert on
            // every route, so gating here would make the two sides disagree.
            if (source.RemoveConvert() is StructuralTypeShaperExpression { StructuralType: IEntityType entityType })
            {
                var navigation = entityType.FindNavigation(memberName);
                // Embedded navigations should be handled by VisitMethodCall
                return navigation == null || !navigation.IsEmbedded();
            }

            return false;
        }

        // Mql.Field<TDoc, TField>() is always a scalar field extraction
        if (methodCallExpression.Method is { Name: "Field", DeclaringType.FullName: "MongoDB.Driver.Mql" })
        {
            return true;
        }

        return false;
    }

    private static Expression MatchTypes(
        Expression expression,
        Type targetType)
        => expression == null
            ? Expression.Default(targetType)
            : targetType != expression.Type && targetType.TryGetItemType() == null
                ? Expression.Convert(expression, targetType)
                : expression;

    private static readonly MethodInfo GetParameterValueMethodInfo
        = typeof(MongoProjectionBindingExpressionVisitor)
            .GetTypeInfo().GetDeclaredMethod(nameof(GetParameterValue));

#if EF8 || EF9
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.ParameterValues[parameterName];
#else
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.Parameters[parameterName];
#endif
}
