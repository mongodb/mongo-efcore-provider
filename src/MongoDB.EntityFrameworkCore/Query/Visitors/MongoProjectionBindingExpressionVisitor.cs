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
    private readonly Dictionary<ProjectionMember, string> _explicitProjectionAliases = new();
    private readonly Stack<ProjectionMember> _projectionMembers = new();
    private readonly Dictionary<ParameterExpression, CollectionShaperExpression> _collectionShaperMapping = new();
    private readonly Stack<INavigation> _includedNavigations = new();

    private MongoQueryExpression _queryExpression;

    // Members a MemberInit's bindings assign; set only while its NewExpression is visited.
    private IReadOnlyCollection<string> _memberInitBindingNames;

    // Selector body of this Translate() call: lets the bare filtered-count arm tell a bare Count(pred) from one nested
    // in a wrapped projection that declined to Fallback.
    private Expression _translatedRootExpression;

    /// <summary>
    /// Perform translation of the <paramref name="expression" /> that belongs to the
    /// supplied <paramref name="queryExpression"/>.
    /// </summary>
    public Expression Translate(
        MongoQueryExpression queryExpression,
        Expression expression)
    {
        _queryExpression = queryExpression;
        _projectionMembers.Push(new ProjectionMember());
        _translatedRootExpression = expression;
        _aliasedConstructionMembers.Clear();
        _explicitProjectionAliases.Clear();

        var result = Visit(expression);

        _queryExpression.ReplaceProjectionMapping(_projectionMapping);
        _projectionMapping.Clear();
        _queryExpression.ReplaceAliasedConstructionMembers(_aliasedConstructionMembers);
        _aliasedConstructionMembers.Clear();
        _queryExpression.ReplaceExplicitProjectionAliases(_explicitProjectionAliases);
        _explicitProjectionAliases.Clear();
        _queryExpression = null;
        _translatedRootExpression = null;

        _projectionMembers.Clear();

        return MatchTypes(result, expression.Type);
    }

    /// <summary>
    /// Visits an already re-bound <see cref="IncludeExpression"/> (from <c>BindResultMember</c>) without
    /// <see cref="Translate"/>'s <see cref="MongoQueryExpression.ReplaceProjectionMapping"/> side effect, so the caller's
    /// index-based mapping survives. Only the Include handling in <see cref="VisitExtension"/> is needed.
    /// </summary>
    /// <remarks>
    /// Still seeds <see cref="_projectionMembers"/>: a nested auto-included owned collection reaches
    /// <see cref="TryBindNativeArrayProjection"/>, which assumes a non-empty stack. <see cref="_translatedRootExpression"/>
    /// stays null (readers treat that as a decline); <see cref="_projectionMapping"/> is restored on every return path.
    /// </remarks>
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

            // A row-independent Convert/member-of-constant leaf left on the shaper (HasClientEvaluatedProjectionLeaf,
            // IsRowIndependentLeaf). Nothing is projected for it, so the arms below would bind an alias $project never
            // emits: evaluate in place.
            case UnaryExpression or MemberExpression
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && _queryExpression.Select.HasClientEvaluatedProjectionLeaf
                     && NativeProjectionBinder.IsRowIndependentLeaf(expression, selectorParameter: null):
                return base.Visit(expression);

            // A constructed sub-entity leaf already translated to a MongoDocumentConstructionExpression: register the
            // whole node as one member (recursing would map "Book.Id" to root-level reads, but natively it is under the
            // "Book" alias). Reuses the emit side's result rather than re-deriving it.
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

            // Already bound by index while still native: a native SelectMany's element shaper reached again through
            // `.Select(ti => ti.Inner)` over EF's TransparentIdentifier. The Route == Projection guard is load-bearing: a
            // Select after GroupBy/Distinct is also bound by index but already set Route = Fallback, and must be reported
            // unsupported rather than read a stale projection
            // (NativeGroupByTests.Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data).
            case ProjectionBindingExpression { Index: not null } projectionBindingExpression
                when projectionBindingExpression.QueryExpression == _queryExpression
                     && _queryExpression.Select.Route == NativeRoute.Projection:
                return projectionBindingExpression;

            // A member read inside a client-only body over the whole entity (`new Wrapper(x) { Name = x.name }`): nothing
            // is projected, and a projection binding would read the ProjectionMember name (`Name`, not element name
            // `name`) off the raw document: null. Read off the materialized entity instead (VisitMember). VisitMember
            // null-guards as `T?`, so restore the member's own type for consumers that don't (a conditional test).
            case MemberExpression when _queryExpression.Select.Route == NativeRoute.WholeEntity
                                       && _queryExpression.Select.HasClientWrappedWholeEntityShaper:
                return MatchTypes(base.Visit(expression), expression.Type);

            // Same route, a combinator over such member reads (boxing Convert, `x.I + 1`, a ternary): walk it. The arms
            // below would register the whole node as a binding where nothing was projected: null.
            case UnaryExpression or BinaryExpression or ConditionalExpression
                when _queryExpression.Select.Route == NativeRoute.WholeEntity
                     && _queryExpression.Select.HasClientWrappedWholeEntityShaper:
                return base.Visit(expression);

            case MemberExpression memberExpression:
                return BindWholeLeaf(memberExpression);

            // Arithmetic leaf: register the whole node as one member (the default walk maps both operands to the same
            // member: (A*B)^2). Route == Projection confines this to projections the binder accepted in full; a mixed
            // shape stays Fallback and goes to the mixed shaper, which can't read a raw BinaryExpression.
            case BinaryExpression binaryExpression
                when NativeProjectionBinder.IsArithmeticLeafShape(binaryExpression)
                     && _queryExpression.Select.Route == NativeRoute.Projection:
                return BindWholeLeaf(binaryExpression);

            // Numeric-cast leaf: register the whole Convert (the default walk keeps only the operand, and the read side
            // would decode the $toInt/$toLong result with the pre-cast property's serializer). Route == Projection as for
            // arithmetic. The Operand exclusion keeps this disjoint from VisitMember's navigation Convert(shaper, T), and
            // a New/MemberInit operand is an upcast of a DTO whose members the binder binds individually.
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                    Operand: not (StructuralTypeShaperExpression or NewExpression or MemberInitExpression) } castExpression
                when _queryExpression.Select.Route == NativeRoute.Projection:
                return BindWholeLeaf(castExpression);

            // String-to-char-sequence leaf (`e.City.ToList()`): register the whole call, else the wrapper is dropped and
            // the shaper gets a string where IEnumerable<char>/List<char>/char[] was expected.
            case MethodCallExpression stringSequenceCall
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && NativeProjectionBinder.IsStringSequenceMaterializationCall(stringSequenceCall):
                return BindWholeLeaf(stringSequenceCall);

            // A ternary over client-only constructions whose test the binder staged as this member's bool leaf
            // (IsClientConditionalLeaf). Evaluate it here: the test binds as that leaf, each branch's row reads bind under
            // the member they were staged for, the rest is row-independent. Read whole, it would go through the
            // constructed type's class map (`Id` vs `_id`).
            case ConditionalExpression clientConditional
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && _queryExpression.Select.IsClientConditionalLeaf(
                         GetCurrentProjectionMember().Last?.Name, clientConditional.Test):
            {
                var boundTest = MatchTypes(Visit(clientConditional.Test), typeof(bool));
                var membersBefore = _projectionMapping.Keys.ToHashSet();
                var boundIfTrue = MatchTypes(Visit(clientConditional.IfTrue), clientConditional.Type);
                var boundIfFalse = MatchTypes(Visit(clientConditional.IfFalse), clientConditional.Type);

                // Take each branch read's alias from the staged read, not the member path; an unstaged registration
                // means the two sides disagree about the shape: fail loudly.
                foreach (var (branchMember, branchRead) in _projectionMapping)
                {
                    if (membersBefore.Contains(branchMember))
                    {
                        continue;
                    }

                    _explicitProjectionAliases[branchMember] =
                        _queryExpression.Select.TryGetClientConditionalReadAlias(branchRead, out var readAlias)
                            ? readAlias
                            : throw new InvalidOperationException(
                                $"Client-evaluated ternary branch read '{branchRead.Print()}' was not staged by the native projection binder.");
                }

                return Expression.Condition(boundTest, boundIfTrue, boundIfFalse, clientConditional.Type);
            }

            // Conditional leaf: register whole (the default walk writes Test/IfTrue/IfFalse to one member).
            case ConditionalExpression when _queryExpression.Select.Route == NativeRoute.Projection:
                return BindWholeLeaf(expression);

            // Reference-collection-nav First/FirstOrDefault leaf, staged as a MongoCorrelatedReducerLeaf at emit time.
            // Nav-expansion puts the member access inside the reducer, so the default walk would try to translate the
            // DbSet root and throw. Register opaquely; the read side needs only the alias ("_lookup_<Nav>.<Member>").
            case MethodCallExpression correlatedReducerCandidate
                when TryGetCorrelatedReducerLeaf(correlatedReducerCandidate, out _):
                return BindWholeLeaf(correlatedReducerCandidate);

            // .AddXxx(amount) leaf, already a MongoDateAddExpression: register whole, else start date and amount write
            // the same member and the alias is typed as the amount.
            case MethodCallExpression dateAddCandidate
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && MongoExpressionTranslator.IsDateAddMethod(dateAddCandidate):
                return BindWholeLeaf(dateAddCandidate);

            // ToLower/ToUpper over a string: $toLower/$toUpper are ASCII-only, so the call always runs client-side over
            // the bound receiver (the binder stages only the receiver), null-propagated; also kept off driver-LINQ
            // push-down (ProjectionAnalyzer.HasCaseMappingProjectedValue).
            case MethodCallExpression caseMappingCall when NativeProjectionBinder.IsCaseMappingCall(caseMappingCall):
                return NullPropagatingStringCall(caseMappingCall, NullPropagatingReceiver(Visit(caseMappingCall.Object)!));

            // DateTimeOffset.ToString(...): the driver would render the stored sub-document as BSON JSON (EF-217). Bind
            // the receiver and re-apply the call client-side.
            case MethodCallExpression dateTimeOffsetToString when IsDateTimeOffsetToString(dateTimeOffsetToString):
                return Expression.Call(
                    VisitThroughConversions(dateTimeOffsetToString.Object!), dateTimeOffsetToString.Method,
                    dateTimeOffsetToString.Arguments.Select(a => Visit(a)!));

            // DateTime.ToString(...) leaf: the binder staged only the receiver; re-apply the call client-side (D-TOSTRING).
            // Projection route only; the driver-LINQ fallback still sees the original call.
            case MethodCallExpression dateTimeToString
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && NativeProjectionBinder.IsDateTimeToString(dateTimeToString):
                return Expression.Call(
                    VisitThroughConversions(dateTimeToString.Object!), dateTimeToString.Method,
                    dateTimeToString.Arguments.Select(a => Visit(a)!));

            // A computed string/math/predicate leaf (`x.S.Substring(1, 2)`, `x.S == null`) that native $project computes
            // whole. Bind it as usual (receiver to the alias, call re-applied client-side) so the mixed shaper and
            // driver-LINQ push-down keep working, and wrap it so the native alias reader can read the computed value
            // instead (NativeComputedLeafExpression). Matched structurally against the leaf the emit side staged.
            //
            // Numeric Negate/widening-cast leaves (`-x.A`) join via the emit side's IsNumericComputedLeafShape: the
            // default walk would re-apply Negate over the already-negated server value (+x.A). With Route == Projection a
            // top-level widening Convert is claimed by the numeric-cast case above.
            case MethodCallExpression or UnaryExpression { NodeType: ExpressionType.Not } or BinaryExpression
                when IsNativeComputedLeaf(expression):
            case UnaryExpression
                when NativeProjectionBinder.IsNumericComputedLeafShape(expression) && IsNativeComputedLeaf(expression):
                var computedMember = GetCurrentProjectionMember();
                Expression clientExpression;
                if (IsClientComputedLeaf(expression))
                {
                    // The mixed reader re-evaluates the leaf whole: bound operand by operand each operand would read as
                    // the last one bound, and ToString over a bound Length would dereference a null string.
                    clientExpression = BindWholeLeaf(expression);
                }
                else
                {
                    clientExpression = base.Visit(expression);
                }

                return new NativeComputedLeafExpression(
                    clientExpression, new ProjectionBindingExpression(_queryExpression, computedMember, expression.Type));

            case MethodCallExpression methodCallExpression
                when IsScalarMethodPropertyAccess(methodCallExpression):
                return BindWholeLeaf(methodCallExpression);

            // Computed-arithmetic leaf mixed with a whole entity reference (forces the mixed shaper): register whole, else
            // Age * Score materialises as Score * Score. Limited to simple scalar reads/nested arithmetic
            // (IsSimpleArithmeticLeaf): collection-navigation Sum()/Count() must still decompose so their guards fire.
            case BinaryExpression binaryExpression
                when NativeProjectionBinder.IsArithmeticLeafShape(binaryExpression) && IsSimpleArithmeticLeaf(binaryExpression):
                return BindWholeLeaf(binaryExpression);

            // A computed scalar leaf outside a native projection (`S.IndexOf(T)`, `string.Compare(S, T)`), read only by the
            // mixed reader, which re-evaluates it client-side (TryBindClientComputedLeaf): register whole, else every
            // operand writes to this member and reads as the last one visited. Driver-LINQ push-down is unaffected.
            case BinaryExpression or ConditionalExpression or UnaryExpression or MethodCallExpression
                when _queryExpression.Select.Route != NativeRoute.Projection && IsClientComputedLeaf(expression):
                return BindWholeLeaf(expression);

            default:
                return base.Visit(expression);
        }
    }

    /// <summary>
    /// True when <paramref name="expression"/> is the New/MemberInit <c>NativeProjectionBinder</c> translated into a
    /// <see cref="MongoDocumentConstructionExpression"/> for the current projection member; reads the emit side's result
    /// so the bind side can't admit a shape the emit side declined.
    /// </summary>
    /// <remarks>
    /// Matches on alias + CLR type, not reference equality with <c>OriginalExpression</c>:
    /// <see cref="ReplacingExpressionVisitor"/> has already rebuilt the tree.
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
    /// True when <paramref name="expression"/> is the current member's reference-collection-nav <c>First</c>/
    /// <c>FirstOrDefault</c> leaf staged by the binder, looked up by alias in
    /// <see cref="MongoQueryExpression.CorrelatedReducerLeaves"/>. <paramref name="leaf"/> is unused by the caller.
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

        var alias = _queryExpression.Select.ResolveProjectionAlias(memberName);

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

    // `receiver == null ? null : receiver.M(args)` (receiver evaluated once): the form IsClientCaseMapping recognizes.
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
    /// </summary>
    internal static bool IsClientCaseMapping(Expression expression)
        => expression is BlockExpression { Result: ConditionalExpression { IfFalse: MethodCallExpression call } }
           && NativeProjectionBinder.IsCaseMappingCall(call);

    /// <summary>
    /// True for <c>ToString(...)</c> on a <see cref="DateTimeOffset"/> (or nullable), also through a boxing conversion;
    /// evaluated client-side (see <see cref="Visit"/>) and refused by the EF-to-driver bridge.
    /// </summary>
    internal static bool IsDateTimeOffsetToString(Expression expression)
        => expression is MethodCallExpression { Method.Name: nameof(ToString), Object: { } receiver }
           && IsDateTimeOffsetValue(receiver);

    /// <summary>
    /// True for a string concatenation (<c>+</c> or <c>string.Concat</c>, whose object overloads box each operand) with a
    /// <see cref="DateTimeOffset"/> operand. Native and driver both render it as <c>$toString</c> over the stored
    /// sub-document, yielding BSON JSON (EF-217): the native translator declines and the EF-to-driver bridge throws.
    /// </summary>
    internal static bool IsDateTimeOffsetConcatenation(Expression expression)
        => expression switch
        {
            BinaryExpression { NodeType: ExpressionType.Add } add when add.Type == typeof(string)
                => IsDateTimeOffsetValue(add.Left) || IsDateTimeOffsetValue(add.Right),
            MethodCallExpression { Method: { IsStatic: true, Name: nameof(string.Concat) } method } call
                when method.DeclaringType == typeof(string)
                => call.Arguments.Any(
                    a => IsDateTimeOffsetValue(a) || a is NewArrayExpression array && array.Expressions.Any(IsDateTimeOffsetValue)),
            _ => false
        };

    /// <summary>
    /// True when <paramref name="expression"/>, ignoring conversions (e.g. the boxing Convert of <c>string + object</c>),
    /// is a <see cref="DateTimeOffset"/> or nullable one.
    /// </summary>
    internal static bool IsDateTimeOffsetValue(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return (Nullable.GetUnderlyingType(expression.Type) ?? expression.Type) == typeof(DateTimeOffset);
    }

    // Binds the innermost operand of a conversion chain and re-applies the conversions, so a boxed DateTimeOffset
    // binds as a typed read rather than an object-typed leaf (which reads the stored sub-document as a BsonDocument).
    private Expression VisitThroughConversions(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? convert.Update(VisitThroughConversions(convert.Operand))
            : Visit(expression)!;

    // Null propagation down a client-side chain of string instance calls (`x.S.Trim()` under `.ToUpper()`). A native
    // computed leaf keeps its raw read; only its client form (evaluated by the mixed shaper) is rewritten.
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
    /// True for a computed scalar leaf the mixed reader can re-evaluate client-side over document reads
    /// (<c>TryBindClientComputedLeaf</c> calls this to recognize a leaf registered whole): an operator, conditional,
    /// conversion or string/<see cref="Math"/>/value-type method or member at the root, with at least one property read
    /// and nothing outside <see cref="ClientLeafChecker"/>'s set. A property read itself (or its
    /// <c>Nullable&lt;T&gt;.Value</c>) is not computed: the read side resolves those as field accesses.
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

    // A scalar property read off an entity shaper, directly or through owned reference navigations (`x.O.W`, `EF.Property`).
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
    /// The admitted-node set for a client-evaluated leaf: constants, property reads, instance members of strings/value
    /// types, methods of <see cref="string"/>/<see cref="Math"/>/value types, unary conversions/negation/not, binary
    /// operators (no coalesce conversion lambda) and conditionals. Any other node rejects the leaf, so entity/collection
    /// materialization and collection-navigation aggregates keep the normal walk.
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

    private static bool IsSimpleArithmeticLeaf(Expression expression)
    {
        expression = expression.RemoveConvert();

        return expression switch
        {
            ConstantExpression => true,
            MemberExpression => true,
            MethodCallExpression methodCallExpression when methodCallExpression.TryGetEFPropertyArguments(out _, out _) => true,
            BinaryExpression binaryExpression when NativeProjectionBinder.IsArithmeticLeafShape(binaryExpression) =>
                IsSimpleArithmeticLeaf(binaryExpression.Left) && IsSimpleArithmeticLeaf(binaryExpression.Right),
            _ => false,
        };
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            // Whole-root-entity leaf in a native projection ($project emits {"c": "$$ROOT"}). The emit side matches on CLR
            // type + parameter identity, this side on entity type + `Index: null`; an index-bound root shaper only arises
            // for a join's inner entity (incl. self-join) and takes the arm below. Any other mismatch fails loudly: the
            // shaper would look for elements $project never emitted.
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
                if (TryBindNativeArrayProjection(materializeCollectionNavigationExpression.Navigation as INavigation, out var arrayShaper))
                {
                    return arrayShaper;
                }

                if (materializeCollectionNavigationExpression.Navigation is INavigation embeddableNavigation
                    && embeddableNavigation.IsEmbedded())
                {
                    var visited = base.Visit(materializeCollectionNavigationExpression.Subquery);

                    // The Select arm above rebuilds an element type with its own embedded navigation as an
                    // IEnumerable<T>-typed Enumerable.Select, failing Expression.New's member-type check. Convert is a no-op
                    // at runtime: the removing visitor later discards this shape for the CollectionShaperExpression.
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

                        // Captured before prefixing: ObjectArrayProjectionExpression's Name must stay plain (the prefix is
                        // $lookup bookkeeping; the read side scopes via ParentAccessExpression, so a prefixed name
                        // double-scopes the path).
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
                            // Flat multi-lookup mode: chained reference navigations are root-level $lookup+$unwind stages
                            // aliased "_lookup_<Nav>", not _outer/_inner. A trailing collection Include declared on one of
                            // those intermediates needs the "_lookup_<Nav>." prefix on localField and "as".
                            // A collection Include on the inner side of a Join/LeftJoin over a collection navigation isn't
                            // found by the reference-only match below; emitted at root it would match the outer _id and every
                            // collection would come back empty. Nest the $lookup inside the join's sub-pipeline instead, which
                            // also avoids a phantom "_lookup_<Join>" document for an unmatched left-outer row (a key-less
                            // entity instead of null).
                            if (TryGetDeclaringJoinLookup(includeExpression) is
                                { Navigation.IsCollection: true, ForceUnwind: true } declaringJoinLookup)
                            {
                                ExtractNestedIncludePipeline(
                                    includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);

                                // The same Include can be visited once per projection member; nest only once.
                                var nestedLookupDocument = BuildLookupDocument(lookup);
                                if (!declaringJoinLookup.PipelineStages.Contains(nestedLookupDocument))
                                {
                                    declaringJoinLookup.PipelineStages.Add(nestedLookupDocument);
                                }

                                if (declaringJoinLookup.PipelineKind == LookupPipelineKind.None)
                                {
                                    declaringJoinLookup.PipelineKind = LookupPipelineKind.NestedInclude;
                                }

                                return RewriteCollectionIncludeForLookup(includeExpression, includableNavigation, plainLookupAlias);
                            }

                            var declaringType = includableNavigation.DeclaringEntityType;
                            var intermediateMatches = _queryExpression.GetPendingLookups().Where(
                                l => l.IsCollectionIncludeIntermediateFor(declaringType)).ToList();

                            // Matched by target entity type, not alias. Several reference lookups targeting one type are
                            // ambiguous: guessing would prefix the wrong "_lookup_<Nav>." path and return wrong results, so fail.
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

                        ExtractNestedIncludePipeline(includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);

                        // The Include's navigation can share an alias with a join's lookup (`Include(o => o.Orders).Join(
                        // db.Orders, ...)`). AddLookup keeps the first registration (the join's, $unwind-ed) but the Include
                        // needs the bare array: use a distinct alias. Likewise for a bare Count/LongCount lookup
                        // (IsBareCountSizeSource) when this Include is paged (HasPipeline): it would corrupt the Count's $size.
                        var existingIncompatibleLookup = _queryExpression.GetPendingLookups()
                            .FirstOrDefault(l => l.As == lookup.As
                                && (l.ShouldUnwind || (l.IsBareCountSizeSource && lookup.HasPipeline)));
                        var readAlias = plainLookupAlias;
                        if (existingIncompatibleLookup != null)
                        {
                            // Normally "_include", suffixed further if taken; a repeat of this renamed Include keeps sharing it.
                            var renamedAs = _queryExpression.GetUnusedLookupAlias(
                                $"{lookup.As}_include",
                                l => l.RenamedToAvoidJoinCollision && l.Navigation == lookup.Navigation);
                            var renameSuffix = renamedAs[lookup.As.Length..];
                            lookup.As = renamedAs;
                            lookup.RenamedToAvoidJoinCollision = true;
                            readAlias = plainLookupAlias + renameSuffix;
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
        // A projected element leaf over a cross-document collection (`c.Orders.OrderBy(..).ToList()`, `.FirstOrDefault()`), staged at
        // emit time as a keyed array lookup (NativeProjectionBinder.TryTranslateReferencedElementLeaf). Native route only: any
        // other route has no array to read and falls through to the translation failure below.
        if (TryBindReferencedElementLeaf(methodCallExpression, out var boundElements))
        {
            return boundElements;
        }

        // A projected cross-collection collection navigation (e.g. select new { ..., Orders = c.Orders.ToList() }).
        // EF Core lowers this to Enumerable.ToList(Queryable.Select(Queryable.Where(DbSet<Target>(), joinPred), selector)).
        // There is no enclosing IncludeExpression to set up the $lookup, so bind it here to a CollectionShaperExpression
        // that reads from a dedicated "_lookup_<Nav>" array, mirroring the cross-collection Include path.
        if (TryBindProjectedCollectionNavigation(methodCallExpression, out var boundCollection))
        {
            return boundCollection;
        }

        // An element Select over an owned collection staged as that collection's whole array (the emit side's
        // TryMatchOwnedArraySelectLeaf): bind the array as a collection shaper and re-apply the Select over the
        // materialized owned entities, so each member is read through its owned property's serializer.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && NativeProjectionBinder.TryMatchOwnedArraySelectLeaf(
                methodCallExpression, _queryExpression.CollectionExpression.EntityType,
                out var selectedNavigation, out _, out var elementSelector)
            && TryBindNativeArrayProjection(selectedNavigation, out var selectedArrayShaper))
        {
            var elementType = elementSelector.Parameters[0].Type;
            var resultType = elementSelector.ReturnType;
            var elements = Expression.Convert(selectedArrayShaper, typeof(IEnumerable<>).MakeGenericType(elementType));
            return Expression.Call(
                typeof(Enumerable).GetMethods()
                    .Single(m => m.Name == nameof(Enumerable.ToList)).MakeGenericMethod(resultType),
                Expression.Call(
                    typeof(Enumerable).GetMethods()
                        .Single(m => m.Name == nameof(Enumerable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(elementType, resultType),
                    elements,
                    elementSelector));
        }

        // A projected cross-collection collection-navigation Count (`c.Orders.Count`) is lowered by EF to
        // Count(Where(DbSet<Target>(), joinPred)) with no enclosing IncludeExpression. Register a "_lookup_<Nav>" $lookup
        // and bind the count as a scalar projection; the EF-to-driver translator rewrites it to { $size: "$_lookup_<Nav>" }.
        if (TryBindProjectedCollectionNavigationCount(methodCallExpression, out var boundCount))
        {
            return boundCount;
        }

        // Owned collection-navigation count leaf in a native projection (`b.Posts.Count`, `Count(pred)`): register the
        // whole call as one member, else the Queryable switch below rebuilds it as a client-side Enumerable.Count over a
        // `Posts` array the native $project dropped. Must run before the fall-through and the switch's
        // Visit(Arguments[0]); kept after TryBindProjectedCollectionNavigationCount as defense-in-depth. Matching is by
        // canonical MethodInfo (see Query/AGENTS.md): this block returns unconditionally, so a false positive would hijack
        // an unrelated member.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && IsCanonicalCount(methodCallExpression.Method))
        {
            return BindWholeLeaf(methodCallExpression);
        }

        // An aggregate or quantifier over a cross-collection collection navigation (`c.Orders.Any(...)`,
        // `.Sum(o => o.Total)`), which nav-expansion lowers over Where(DbSet<Target>(), joinPredicate). The native binder is
        // authoritative: it already matched the FK correlation and committed the `$lookup` (only then is
        // Route == Projection), so the read side only binds the call as one member. Declined shapes keep failing translation.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && IsReferencedCollectionAggregateLeaf(methodCallExpression))
        {
            return BindWholeLeaf(methodCallExpression);
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
                    return CreateNullableEntityShaper(navigation.TargetEntityType, entityProjection);

                case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                    {
                        var innerShaperExpression = CreateNullableEntityShaper(
                            navigation.TargetEntityType, objectArrayProjectionExpression.InnerProjection);

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
                // IQueryable<T> parameter, so the fall-through's Update(...) would throw in every mode. Rebuild against the
                // Enumerable equivalent. The decline branches use `break`, not `return null`, which would fold to
                // Expression.Default(int) and silently return 0; `break` re-Visits Arguments[0] (not side-effect-free, see
                // the structural guard below), but no known shape reaches it.
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

                // The same rebuild for bare First/FirstOrDefault/Single/SingleOrDefault/Any (`break` vs `return null` as
                // above); predicated overloads and Sum/Min/Max/Average are out of scope. EF doesn't fuse a trailing member
                // access (`b.Posts.First().Heading` is `b.Posts.Select(o => o.Heading).First()`), so the source may be the
                // rebuilt Enumerable.Select; TryRebuildAsEnumerableSource accepts both but only fires on sources that
                // would otherwise crash.
                case var _
                    when genericMethod != null
                         && BareElementOperatorEnumerableMethods.TryGetValue(genericMethod, out var enumerableMethod):
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var elementSource))
                    {
                        break;
                    }

                    return Expression.Call(enumerableMethod.MakeGenericMethod(method.GetGenericArguments()), elementSource);

                // The same rebuild for predicated Count/LongCount (optionally behind an arithmetic/cast spine). A native
                // filtered count is claimed earlier, so only non-native shapes get here. The predicate is deliberately not
                // re-Visited: it runs client-side over materialized instances. Guards, all needed:
                // - ContainsQueryParameter: a captured local stays an unresolved EF query-parameter node and would throw
                //   "must be reducible node" at compile time instead of a clean decline.
                // - IsReachableThroughArithmeticSpine: wrapped projections whose element predicate declined to Fallback
                //   for other reasons (see NativeOwnedCollectionFilteredCountTests) also reach here with a genuine
                //   CollectionShaperExpression; only reachability distinguishes them.
                // - ContainsShaperReference: a bare correlated predicate passes reachability but references the outer
                //   shaper.
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
            // source is a materialized collection (Update(...) needs an IQueryable<T>, and re-Visiting Arguments[0]
            // re-runs the non-idempotent _collectionShaperMapping.Add). Decline before anything mutates;
            // `return null` would fold to Expression.Default (silent null/empty collection). Keyed on assignability so
            // Skip/ElementAt/Where/... are covered.
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

        // Without a member per argument, every argument registers under the enclosing member, so two different
        // registrations there would read back as the last one (see AliasedConstructionMembers). Recorded rather than
        // thrown: only the mixed reader reads these, and a pushed-down projection never does.
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

    // A member-less construction (`new KeyValuePair<string, int>(x.S.ToUpper(), x.T.Length)`) would register every argument
    // under the enclosing member, each reading back as the last one bound. Outside a native projection (only the mixed
    // reader reads these), when every argument is client-evaluable (ClientLeafChecker), bind each under the member its
    // constructor parameter initializes by name; otherwise null. Also null when a wrapping MemberInit assigns one of
    // those members (`new Dto(x.S) { Name = x.T }`): argument and binding would share a member.
    private IReadOnlyList<MemberInfo> TryGetConstructorArgumentMembers(
        NewExpression newExpression, IReadOnlyCollection<string> memberInitBindingNames)
        => _queryExpression.Select.Route == NativeRoute.Projection
            ? null
            : TryMatchConstructorArgumentMembers(newExpression, memberInitBindingNames);

    /// <summary>
    /// The members <see cref="VisitNew"/> binds a member-less construction's arguments under outside a native
    /// projection, or <see langword="null"/> when they can't each be bound under a member of their own.
    /// <paramref name="newExpression"/> must already have the selector parameter replaced by the source shaper. Also asked
    /// by <c>TranslateSelect</c>, which routes a positional-ctor projection holding a client case mapping here.
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
            // Same unconditional .Index.Value deref as in VisitMethodCall. Member accesses reach here on one route only:
            // a client-only body over the whole entity (Route == WholeEntity, HasClientWrappedWholeEntityShaper), whose
            // root shaper VisitExtension already bound with an Index. Projection-route leaves (incl. Index: null
            // whole-root leaves) return in Visit's plain MemberExpression arm first. Recheck if another route lets a
            // member access fall through.
            case ProjectionBindingExpression innerProjectionBindingExpression:
                innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                    innerProjectionBindingExpression.Index.Value].Expression;
                break;

            case UnaryExpression unaryExpression:
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
                return CreateNullableEntityShaper(navigation.TargetEntityType, entityProjection);

            case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                {
                    var innerShaperExpression = CreateNullableEntityShaper(
                        navigation.TargetEntityType, objectArrayProjectionExpression.InnerProjection);

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
    /// The alias isn't stored on the node: <c>ApplyProjection</c> derives it from this <see cref="ProjectionMember"/>, as
    /// <c>NativeProjectionBinder</c> does on the emit side, so both agree.
    /// </para>
    /// <para>
    /// Runs before descending (not in <see cref="VisitMember"/>): visiting the owner shaper calls <c>AddToProjection</c>,
    /// after which <c>ApplyProjection</c> returns early and sibling leaves fail in <c>GetProjectionIndex</c>.
    /// </para>
    /// <para>
    /// Route == Projection is load-bearing: a mixed/fallback shape fetches whole documents and must read the array at
    /// its document path via the <c>ObjectArrayProjectionExpression</c> arm.
    /// </para>
    /// <para>
    /// Admissibility lives in <see cref="NativeTranslation.NativeProjectionBinder.IsNativeArrayProjectionLeaf"/>, shared
    /// with the emit side; disagreement means silent wrong data, so change it only there. A fresh
    /// <see cref="RootReferenceExpression"/> is a safe <c>_projectionBindings</c>/<c>_ownerMappings</c> key because
    /// <see cref="EntityTypedExpression"/> equality is by <see cref="IEntityType"/>.
    /// </para>
    /// </remarks>
    private bool TryBindNativeArrayProjection(
        INavigation candidateNavigation,
        out Expression arrayShaper)
    {
        arrayShaper = null;

        // Same ProjectionMember (and alias override) ApplyProjection uses; a bare selector body has no last member,
        // so without the override this is null and the alias-agreement check below fails.
        var arrayProjectionMember = GetCurrentProjectionMember();
        var arrayMemberName = arrayProjectionMember.Last?.Name;
        var arrayAlias = _queryExpression.Select.ResolveProjectionAlias(arrayMemberName);

        if (_queryExpression.Select.Route != NativeRoute.Projection
            || !NativeProjectionBinder.IsNativeArrayProjectionLeaf(
                candidateNavigation,
                _queryExpression.CollectionExpression.EntityType,
                arrayAlias))
        {
            return false;
        }

        var navigation = candidateNavigation;
        var aliasedArray = new ArrayAliasProjectionExpression(
            navigation,
            new RootReferenceExpression(navigation.DeclaringEntityType));

        _projectionMapping[arrayProjectionMember] = aliasedArray;

        var innerShaper = CreateElementShaperWithOwnedIncludes(navigation.TargetEntityType, aliasedArray.InnerProjection);

        arrayShaper = new CollectionShaperExpression(
            new ProjectionBindingExpression(_queryExpression, arrayProjectionMember, aliasedArray.Type),
            innerShaper,
            navigation,
            navigation.TargetEntityType.ClrType);

        return true;
    }

    /// <summary>
    /// Binds a projected element leaf over a cross-document collection (<see cref="ReferencedElementShape"/>) to a
    /// <see cref="CollectionShaperExpression"/> over the keyed array the emit side staged, read from the member's own
    /// <c>$project</c> alias; <c>FirstOrDefault</c> takes the first element of that (at most one element) list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only binds what the emit side accepted: the lookup is found by this member's alias in
    /// <see cref="LookupExpression.ElementLeafAliases"/> (written at the projection binder's commit point), never re-derived from the
    /// tree, so the two sides cannot disagree about admissibility. A leaf the emit side declined finds no lookup and falls through.
    /// </para>
    /// <para>
    /// The shaper has no navigation, so materializing it assigns a plain list and does not fix the elements up into a principal's
    /// own collection (which would leave it holding only the filtered subset). Like <see cref="TryBindNativeArrayProjection"/> it runs
    /// before descending, because visiting the outer shaper calls <c>AddToProjection</c>.
    /// </para>
    /// </remarks>
    private bool TryBindReferencedElementLeaf(MethodCallExpression methodCallExpression, out Expression result)
    {
        result = null!;

        if (_queryExpression.Select.Route != NativeRoute.Projection
            || !NativeReferencedElementMatcher.TryMatch(methodCallExpression, out var shape))
        {
            return false;
        }

        var arrayProjectionMember = GetCurrentProjectionMember();
        if (arrayProjectionMember.Last?.Name is not { } memberName)
            return false;

        var arrayAlias = _queryExpression.Select.ResolveProjectionAlias(memberName);
        var targetType = shape.Root.EntityType;
        var lookup = _queryExpression.GetPendingLookups().FirstOrDefault(
            l => l.IsCorrelatedArraySource
                 && l.TargetEntityType == targetType
                 && l.ElementLeafAliases.Contains(arrayAlias)
                 && (l.ElementLimit == 1) == shape.IsFirst);
        if (lookup is null)
            return false;

        var aliasedArray = new ArrayAliasProjectionExpression(targetType, new RootReferenceExpression(targetType));
        _projectionMapping[arrayProjectionMember] = aliasedArray;

        var innerShaper = CreateElementShaperWithOwnedIncludes(targetType, aliasedArray.InnerProjection);
        Expression collection = new CollectionShaperExpression(
            new ProjectionBindingExpression(_queryExpression, arrayProjectionMember, aliasedArray.Type),
            innerShaper,
            navigation: null,
            targetType.ClrType);

        if (shape.IsFirst)
        {
            result = Expression.Call(EnumerableMethods.FirstOrDefaultWithoutPredicate.MakeGenericMethod(targetType.ClrType), collection);
        }
        else if (shape.Selector is { } selector)
        {
            // The selector runs over the materialized entities, as for an element Select over an owned collection.
            result = Expression.Call(
                EnumerableMethods.ToList.MakeGenericMethod(selector.ReturnType),
                Expression.Call(
                    EnumerableMethods.Select.MakeGenericMethod(targetType.ClrType, selector.ReturnType),
                    collection,
                    selector));
        }
        else
        {
            result = collection;
        }

        return true;
    }

    /// <summary>
    /// The shaper for one element of an alias-addressed owned array: the element's own shaper, wrapped in an
    /// <see cref="IncludeExpression"/> per eager-loaded embedded navigation (recursively), as EF's nav-expansion builds
    /// it for a whole owned entity. Without them a nested owned collection would never be populated. The admission rule
    /// (<see cref="NativeProjectionBinder.IsNativeArrayProjectionLeaf"/>) guarantees every eager navigation is embedded.
    /// </summary>
    private static Expression CreateElementShaperWithOwnedIncludes(IEntityType elementType, EntityProjectionExpression elementProjection)
    {
        Expression shaper = CreateNullableEntityShaper(elementType, elementProjection);
        foreach (var nested in elementType.GetNavigations().Where(n => n.IsEagerLoaded && n.IsEmbedded()))
        {
            Expression nestedShaper = elementProjection.BindNavigation(nested) switch
            {
                ObjectArrayProjectionExpression array => new CollectionShaperExpression(
                    array,
                    CreateElementShaperWithOwnedIncludes(nested.TargetEntityType, array.InnerProjection),
                    nested,
                    nested.TargetEntityType.ClrType),
                EntityProjectionExpression reference => CreateElementShaperWithOwnedIncludes(nested.TargetEntityType, reference),
                _ => throw new InvalidOperationException($"Unable to bind owned navigation '{nested.Name}' of '{elementType.DisplayName()}'.")
            };

            shaper = new IncludeExpression(shaper, nestedShaper, nested);
        }

        return shaper;
    }

    /// <summary>
    /// A nullable shaper for <paramref name="entityType"/> reading its value buffer from <paramref name="projection"/>.
    /// </summary>
    private static StructuralTypeShaperExpression CreateNullableEntityShaper(IEntityType entityType, Expression projection)
        => new(
            entityType,
            Expression.Convert(Expression.Convert(projection, typeof(object)), typeof(ValueBuffer)),
            nullable: true);

    private ProjectionMember GetCurrentProjectionMember()
        => _projectionMembers.Peek();

    /// <summary>
    /// Registers <paramref name="leaf"/> whole as the current projection member's mapping and returns the binding
    /// that reads it, so the default walk doesn't write each operand to the same member.
    /// </summary>
    private ProjectionBindingExpression BindWholeLeaf(Expression leaf)
    {
        var projectionMember = GetCurrentProjectionMember();
        _projectionMapping[projectionMember] = leaf;
        return new ProjectionBindingExpression(_queryExpression, projectionMember, leaf.Type);
    }

    private void EnterProjectionMember(MemberInfo memberInfo)
        => _projectionMembers.Push(_projectionMembers.Peek().Append(memberInfo));

    private void ExitProjectionMember()
        => _projectionMembers.Pop();

    /// <summary>
    /// Decides whether <paramref name="visitedSource"/> (the visited <c>Arguments[0]</c> of a bare <c>Queryable.First</c>/
    /// <c>FirstOrDefault</c>/<c>Single</c>/<c>SingleOrDefault</c>/<c>Any</c>) would crash the generic fall-through, and if
    /// so returns it as the source for the <see cref="Enumerable"/> equivalent.
    /// </summary>
    /// <remarks>
    /// Wider than the Count arm's <c>CollectionShaperExpression</c> check because EF doesn't fuse a trailing member
    /// access into these reducers, so the source is often the rebuilt <c>Enumerable.Select</c>. Returns
    /// <see langword="false"/> when the source is null, unchanged, still assignable to <c>IQueryable&lt;TSource&gt;</c>,
    /// or not assignable to <c>IEnumerable&lt;TSource&gt;</c> (rebuilding would trade a clean decline for an
    /// <see cref="ArgumentException"/>).
    /// </remarks>
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
    /// True when <paramref name="target"/> (a filtered <c>Count</c>/<c>LongCount</c>) is <paramref name="root"/> itself or
    /// reachable from it through only arithmetic and numeric <c>Convert</c> nodes (the rebuild can't thread a
    /// <see cref="CollectionShaperExpression"/> through anything else). Read-only: no <see cref="Visit"/> calls, so no
    /// double-registration hazard.
    /// </summary>
    private static bool IsReachableThroughArithmeticSpine(Expression root, MethodCallExpression target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root switch
        {
            BinaryExpression binary when NativeProjectionBinder.IsArithmeticLeafShape(binary)
                => IsReachableThroughArithmeticSpine(binary.Left, target) || IsReachableThroughArithmeticSpine(binary.Right, target),
            UnaryExpression { NodeType: ExpressionType.Convert } unary
                => IsReachableThroughArithmeticSpine(unary.Operand, target),
            _ => false
        };
    }

    /// <summary>
    /// Whether <paramref name="method"/> is one of the eight canonical <c>Count</c>/<c>LongCount</c> methods, by
    /// reference equality on the generic definition (a false positive at the call site is consequential; see
    /// <c>VisitMethodCall</c>).
    /// </summary>
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
    /// Whether <paramref name="expression"/> contains an EF query-parameter node. A captured local in the bare
    /// filtered-count predicate would survive the non-re-Visited rebuild and throw "must be reducible node" at compile time.
    /// </summary>
    private static bool ContainsQueryParameter(Expression expression)
        => ExpressionSearch.Contains(expression, node => NativeQueryParameter.TryGetQueryParameterName(node, out _));

    /// <summary>
    /// Whether <paramref name="expression"/> contains a shaper node. A bare correlated predicate
    /// (<c>b.Posts.Count(p => p.Title == b.Title)</c>) passes the reachability check, but <c>b</c> has become a shaper
    /// that would survive the non-re-Visited rebuild and fail with a confusing <c>KeyNotFoundException</c>.
    /// </summary>
    private static bool ContainsShaperReference(Expression expression)
        => ExpressionSearch.Contains(
            expression, node => node is StructuralTypeShaperExpression or ProjectionBindingExpression or EntityProjectionExpression);

    /// <summary>
    /// Whether a method call is a scalar property access to store in the projection mapping like a
    /// <see cref="MemberExpression"/> (<c>EF.Property</c> on a non-navigation, or <c>Mql.Field</c>).
    /// </summary>
    private static bool IsScalarMethodPropertyAccess(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            // The selector parameter is already a shaper, possibly Convert-wrapped when EF.Property's receiver is an
            // unconstrained generic type (Select_Property_when_shadow_unconstrained_generic_method); without RemoveConvert()
            // the call isn't registered as a leaf and silently returns null. No Route guard, deliberately: the read side
            // (TryResolveFieldAccess) also unwraps Convert on every route, so gating here would make the sides disagree.
            if (source.RemoveConvert() is StructuralTypeShaperExpression { StructuralType: IEntityType entityType })
            {
                var navigation = entityType.FindNavigation(memberName);
                return navigation == null || !navigation.IsEmbedded();
            }

            return false;
        }

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

    /// <summary>
    /// Bare Queryable element operators the Queryable switch rebuilds against a materialized collection, mapped to their
    /// Enumerable equivalents.
    /// </summary>
    private static readonly Dictionary<MethodInfo, MethodInfo> BareElementOperatorEnumerableMethods = new()
    {
        [QueryableMethods.FirstWithoutPredicate] = EnumerableMethods.FirstWithoutPredicate,
        [QueryableMethods.FirstOrDefaultWithoutPredicate] = EnumerableMethods.FirstOrDefaultWithoutPredicate,
        [QueryableMethods.SingleWithoutPredicate] = EnumerableMethods.SingleWithoutPredicate,
        [QueryableMethods.SingleOrDefaultWithoutPredicate] = EnumerableMethods.SingleOrDefaultWithoutPredicate,
        [QueryableMethods.AnyWithoutPredicate] = EnumerableMethods.AnyWithoutPredicate,
    };

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
