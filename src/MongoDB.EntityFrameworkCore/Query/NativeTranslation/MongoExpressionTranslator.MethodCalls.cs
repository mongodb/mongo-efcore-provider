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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;           // QueryableMethods
using MongoDB.EntityFrameworkCore.Extensions;        // GetDocumentPath(), IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;       // BsonSerializerFactory
using IBsonArraySerializer = MongoDB.Bson.Serialization.IBsonArraySerializer;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — method-call recognizers (quantifiers, element counts, <c>Contains</c>,
/// string methods) and the scope guard that keeps a correlated element predicate out of the element-scoped translator.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    internal enum MongoQuantifierKind
    {
        Any,
        All
    }

    // EF produces the Queryable spelling; the Enumerable spelling is accepted so hand-built trees translate the same.
    private static bool TryMatchQuantifierMethod(
        MethodCallExpression call,
        out MongoQuantifierKind kind,
        [NotNullWhen(true)] out Expression? source,
        out LambdaExpression? elementLambda)
    {
        kind = MongoQuantifierKind.Any;
        source = null;
        elementLambda = null;

        if (call.Method.Name == nameof(Enumerable.Any))
            kind = MongoQuantifierKind.Any;
        else if (call.Method.Name == nameof(Enumerable.All))
            kind = MongoQuantifierKind.All;
        else
            return false;

        var declaringType = call.Method.DeclaringType;
        if (declaringType != typeof(Enumerable) && declaringType != typeof(Queryable))
            return false;

        switch (call.Arguments.Count)
        {
            case 1:
                if (kind is not MongoQuantifierKind.Any)
                    return false;
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            case 2:
            {
                // The Queryable spelling quotes its lambda; the Enumerable spelling does not.
                var argument = call.Arguments[1];
                if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                    argument = quote.Operand;

                if (argument is not LambdaExpression { Parameters.Count: 1 } lambda)
                    return false;

                source = UnwrapAsQueryable(call.Arguments[0]);
                elementLambda = lambda;
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryMatchElementAggregateMethod(
        MethodCallExpression call,
        [NotNullWhen(true)] out string? mqlOperator,
        [NotNullWhen(true)] out Expression? source,
        [NotNullWhen(true)] out LambdaExpression? selector)
    {
        source = null;
        selector = null;
        mqlOperator = call.Method.Name switch
        {
            nameof(Enumerable.Sum) => "$sum",
            nameof(Enumerable.Min) => "$min",
            nameof(Enumerable.Max) => "$max",
            nameof(Enumerable.Average) => "$avg",
            _ => null
        };

        if (mqlOperator is null
            || (call.Method.DeclaringType != typeof(Enumerable) && call.Method.DeclaringType != typeof(Queryable))
            || call.Arguments.Count != 2)
        {
            return false;
        }

        var argument = call.Arguments[1];
        if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            argument = quote.Operand;

        if (argument is not LambdaExpression { Parameters.Count: 1 } lambda)
            return false;

        source = UnwrapAsQueryable(call.Arguments[0]);
        selector = lambda;
        return true;
    }

    /// <summary>
    /// <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c> of one numeric element property over a referenced collection, as the
    /// array operator over the looked-up array path (<c>{ $sum: "$_lookup_Orders.Total" }</c>). An empty array gives
    /// <c>0</c> for <c>$sum</c> and <c>null</c> for the others (LINQ's answer for a nullable result; a non-nullable
    /// result reads that <c>null</c> strictly and throws, as LINQ does).
    /// </summary>
    /// <remarks>
    /// Only the referenced-collection resolver feeds this; owned collections stay declined. The selector must be one
    /// numeric property stored as itself, so the server's reduction is the CLR one (<see cref="StoredOrdering"/>).
    /// </remarks>
    private MongoExpression? TranslateReferencedArrayAggregate(
        MethodCallExpression call, string mqlOperator, Expression source, LambdaExpression selector)
    {
        if (ReferencedCollectionResolver?.Invoke(Unwrap(source)) is not { } referenced)
            return null;

        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
            body = conversion.Operand;

        if (!body.TryGetMemberOrEFProperty(out var receiver, out var name)
            || !ReferenceEquals(receiver.RemoveConvert(), selector.Parameters[0])
            || referenced.ElementType.FindProperty(name) is not { } property
            || property.IsShadowProperty())
        {
            return TranslateReferencedComputedAggregate(call, mqlOperator, referenced, selector);
        }

        var underlying = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (underlying != typeof(int) && underlying != typeof(long) && underlying != typeof(double)
            && underlying != typeof(decimal) && underlying != typeof(float))
        {
            return null;
        }

        var preserved = mqlOperator is "$sum" or "$avg"
            ? StoredOrdering.PreservesClrValueForNumericReduction(property)
            : StoredOrdering.PreservesClrOrderingForAggregateAndSort(property);
        if (!preserved || (property.GetValueConverter() is not null && !StoredSerialization.HasDefaultKeySerialization(property)))
            return null;

        return new MongoArrayReduceExpression(
            mqlOperator, $"{referenced.ArrayPath}.{LookupExpression.GetFieldPath(property)}", call.Type);
    }

    /// <summary>
    /// <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c> of ARITHMETIC over the element (<c>c.Orders.Sum(o =&gt; o.Price * o.Qty)</c>), as the
    /// reduction of a per-element <c>$map</c> over the looked-up array (<see cref="MongoMappedArrayReduceExpression"/>).
    /// </summary>
    /// <remarks>
    /// Only numeric arithmetic over the element's own default-stored numeric properties, constants and query parameters (and numeric casts
    /// of them): anything else (a string <c>Length</c>, a conditional, a date part, a member of another entity) declines, because the
    /// native read of a null or missing operand differs from LINQ's and these are exactly the cases where it would be read as 0. Division
    /// follows the result type (<c>MapArithmeticOperator</c>): integer division truncates per element, as C# does.
    /// </remarks>
    private MongoExpression? TranslateReferencedComputedAggregate(
        MethodCallExpression call, string mqlOperator, ReferencedCollectionSource referenced, LambdaExpression selector)
    {
        var underlying = Nullable.GetUnderlyingType(call.Type) ?? call.Type;
        if (underlying != typeof(int) && underlying != typeof(long) && underlying != typeof(double)
            && underlying != typeof(decimal) && underlying != typeof(float))
        {
            return null;
        }

        var elementTranslator = new MongoExpressionTranslator(referenced.ElementType, selector.Parameters[0]);
        if (!elementTranslator.TryTranslateValue(selector.Body, out var element)
            || element is not (MongoBinaryExpression { IsArithmetic: true } or MongoConvertExpression or MongoUnaryExpression)
            || !IsNumericArithmeticOverElement(element, mqlOperator is "$sum" or "$avg"))
        {
            return null;
        }

        return new MongoMappedArrayReduceExpression(mqlOperator, referenced.ArrayPath, element, call.Type);
    }

    private static bool IsNumericArithmeticOverElement(MongoExpression expression, bool numericReduction)
        => expression switch
        {
            MongoFieldExpression field
                => !field.Property.IsShadowProperty()
                   && IsNumericClrType(field.Property.ClrType)
                   && StoredSerialization.HasDefaultKeySerialization(field.Property)
                   && (numericReduction
                       ? StoredOrdering.PreservesClrValueForNumericReduction(field.Property)
                       : StoredOrdering.PreservesClrOrderingForAggregateAndSort(field.Property)),
            // A parameter carries no static type here (no property supplies its serialization context), but it is an operand of numeric
            // arithmetic, which the translator builds only from numeric operands.
            MongoParameterExpression => true,
            MongoConstantExpression => IsNumericClrType(expression.Type),
            MongoBinaryExpression { IsArithmetic: true } binary
                => IsNumericArithmeticOverElement(binary.Left, numericReduction) && IsNumericArithmeticOverElement(binary.Right, numericReduction),
            MongoConvertExpression convert
                => IsNumericClrType(convert.Type) && IsNumericArithmeticOverElement(convert.Operand, numericReduction),
            _ => false
        };

    private static bool IsNumericClrType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double)
               || underlying == typeof(decimal) || underlying == typeof(float);
    }

    /// <summary>
    /// Matches an element count over a collection (<c>.Count</c>, <c>Count()</c>/<c>LongCount()</c>, with or without a
    /// predicate), yielding the source and the predicate lambda (<see langword="null"/> if none).
    /// </summary>
    /// <remarks>
    /// Must stay pure: <see cref="TranslateComparison"/> can call it twice per query. Name-based matching is safe
    /// against a mapped scalar named <c>Count</c> because every match is gated on
    /// <see cref="TryResolveOwnedCollectionPath"/>.
    /// </remarks>
    internal static bool TryMatchCountExpression(
        Expression node,
        [NotNullWhen(true)] out Expression? source,
        out LambdaExpression? predicate)
    {
        source = null;
        predicate = null;

        if (node.Type != typeof(int) && node.Type != typeof(long))
            return false;

        switch (node)
        {
            // b.Posts.Count: hand-built trees only; EF normalizes it into the call form.
            case MemberExpression { Member: PropertyInfo { Name: nameof(List<int>.Count) }, Expression: { } receiver }:
                source = UnwrapAsQueryable(receiver);
                return true;

            case UnaryExpression { NodeType: ExpressionType.ArrayLength, Operand: { } arrayOperand }:
                source = arrayOperand;
                return true;

            // Member form of an array's length. Arrays only: string.Length must not match.
            case MemberExpression { Member: PropertyInfo { Name: nameof(Array.Length) }, Expression: { Type.IsArray: true } arrayReceiver }:
                source = arrayReceiver;
                return true;

            case MethodCallExpression { Arguments.Count: 1 } call
                when call.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                     && (call.Method.DeclaringType == typeof(Enumerable)
                         || call.Method.DeclaringType == typeof(Queryable)):
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            // Compared as generic method definitions (a constructed instantiation is never reference-equal to the open one).
            case MethodCallExpression { Arguments.Count: 2 } call when IsCanonicalCountWithPredicate(call.Method):
                source = UnwrapAsQueryable(call.Arguments[0]);
                predicate = call.Arguments[1].UnwrapLambdaFromQuote();
                return true;

            default:
                return false;
        }
    }

    // Both declaring types are admitted; UnwrapLambdaFromQuote handles the quoted and unquoted lambdas.
    internal static bool IsCanonicalCountWithPredicate(MethodInfo method)
    {
        if (!method.IsGenericMethod)
            return false;

        var definition = method.GetGenericMethodDefinition();
        return definition == QueryableMethods.CountWithPredicate
            || definition == QueryableMethods.LongCountWithPredicate
            || definition == EnumerableMethods.CountWithPredicate
            || definition == EnumerableMethods.LongCountWithPredicate;
    }

    /// <summary>
    /// Builds the translator for an owned-collection element predicate: single-scope when uncorrelated, two-scope
    /// (<paramref name="isCorrelated"/>) when its sole free parameter is this translator's own <see cref="SelfParam"/>
    /// or, on a two-scope translator, its outer parameter. Any other correlation declines.
    /// </summary>
    /// <remarks>
    /// The identity guard is load-bearing: single-scope <see cref="TryResolveMember"/> resolves by name, so an enclosing
    /// member sharing a name with an element member would silently retarget to the element.
    /// </remarks>
    private bool TryCreateElementPredicateTranslator(
        LambdaExpression predicate, IEntityType elementType,
        [NotNullWhen(true)] out MongoExpressionTranslator? translator, out bool isCorrelated)
    {
        translator = null;
        isCorrelated = ReferencesEnclosingScope(predicate.Body, predicate.Parameters[0], out var freeParam);
        if (!isCorrelated)
        {
            translator = new MongoExpressionTranslator(elementType);
            return true;
        }

        // EF-446: on a two-scope translator the root is the established outer parameter (MongoOuterFieldExpression
        // resolves at document root at any depth); a correlation to an intermediate scope still declines.
        var rootParam = SelfParam ?? _outerParam;
        var rootEntityType = SelfParam is not null ? _entityType : _outerEntityType;
        if (rootParam is null || rootEntityType is null || freeParam is null || !ReferenceEquals(freeParam, rootParam))
            return false;

        // innerPrefix null, not "": the renderer prepends the element scope's variable itself.
        translator = new MongoExpressionTranslator(
            elementType, outerParam: rootParam, outerEntityType: rootEntityType, innerPrefix: null);
        return true;
    }

    /// <summary>
    /// True when <paramref name="body"/> references a free parameter other than <paramref name="elementParameter"/>
    /// (is correlated with an enclosing scope); <paramref name="found"/> is the sole free parameter, or
    /// <see langword="null"/> if none or more than one.
    /// </summary>
    /// <remarks>
    /// Scope is decided by identity, not name: an outer-rooted <c>o.Name</c> would otherwise silently bind to the
    /// element's <c>Name</c> and return wrong rows (cf. <c>NativeSelectManyBinder.ReferencesParameter</c>). Parameters
    /// bound by nested lambdas, and EF query parameters (<see cref="ParameterExpression"/>s on EF8/EF9, see
    /// <see cref="NativeQueryParameter.TryGetQueryParameterName"/>), are not free.
    /// </remarks>
    private static bool ReferencesEnclosingScope(
        Expression body, ParameterExpression elementParameter, out ParameterExpression? found)
    {
        var visitor = new FreeParameterVisitor(elementParameter);
        visitor.Visit(body);
        found = visitor.FoundParameter;
        return visitor.FoundFreeParameter;
    }

    private sealed class FreeParameterVisitor(ParameterExpression elementParameter) : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _bound = [elementParameter];

        // By reference identity, so one parameter referenced twice isn't counted as two.
        private readonly List<ParameterExpression> _distinctFree = [];

        public bool FoundFreeParameter { get; private set; }

        // Null for two or more: a multi-parameter body must not pass a caller's ReferenceEquals check, or the second
        // parameter's members would resolve by name against the element scope (wrong rows).
        public ParameterExpression? FoundParameter => _distinctFree.Count == 1 ? _distinctFree[0] : null;

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            var added = 0;
            foreach (var parameter in node.Parameters)
            {
                if (!ContainsByIdentity(parameter))
                {
                    _bound.Add(parameter);
                    added++;
                }
            }

            var result = base.VisitLambda(node);

            // Pop only what this lambda pushed, so an outer binding of the same instance survives.
            _bound.RemoveRange(_bound.Count - added, added);
            return result;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!ContainsByIdentity(node) && !NativeQueryParameter.TryGetQueryParameterName(node, out _))
            {
                FoundFreeParameter = true;
                if (!ContainsByIdentity(node, _distinctFree))
                    _distinctFree.Add(node);
            }

            return node;
        }

        private bool ContainsByIdentity(ParameterExpression parameter) => ContainsByIdentity(parameter, _bound);

        private static bool ContainsByIdentity(ParameterExpression parameter, List<ParameterExpression> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(candidate, parameter))
                    return true;
            }

            return false;
        }
    }

    private static Expression UnwrapAsQueryable(Expression source)
    {
        if (source is MethodCallExpression { Arguments: [var inner] } call
            && call.Method.Name == nameof(Queryable.AsQueryable)
            && call.Method.DeclaringType == typeof(Queryable))
        {
            return inner;
        }

        return source;
    }

    internal static bool TryMatchContainsMethod(
        MethodCallExpression call,
        [NotNullWhen(true)] out Expression? collection,
        [NotNullWhen(true)] out Expression? item)
    {
        collection = null;
        item = null;

        if (call.Method.Name != nameof(Enumerable.Contains))
            return false;

        // Two-argument form only; the IEqualityComparer overload has no query-dialect equivalent.
        if (call.Method.IsStatic && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            item = call.Arguments[1];
            return true;
        }

        // ISet<T> needs no entry: its Contains is inherited from ICollection<T>.
        if (!call.Method.IsStatic && call.Object is not null && call.Arguments.Count == 1)
        {
            var declaringType = call.Method.DeclaringType;
            if (declaringType is { IsGenericType: true })
            {
                var def = declaringType.GetGenericTypeDefinition();
                // Deliberately a whitelist: "any IEnumerable<T> with a Contains(T)" would capture string.Contains(char).
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>) || def == typeof(ICollection<>)
                    || def == typeof(IReadOnlySet<>) || def == typeof(IImmutableSet<>)
                    // EF8 hands these over un-normalized (EF9+ rewrites them to Enumerable.Contains).
                    || def == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>) || def == typeof(ImmutableHashSet<>))
                {
                    collection = call.Object;
                    item = call.Arguments[0];
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Recognizes <c>string.StartsWith/EndsWith/Contains(string|char)</c> and the <see cref="StringComparison"/> overload
    /// for Ordinal[IgnoreCase] and CurrentCulture[IgnoreCase] (matched ordinally, as driver-LINQ does: accepted
    /// divergence). Invariant comparisons are left unmatched.
    /// </summary>
    private static bool TryMatchRegexMethod(
        MethodCallExpression call,
        out MongoRegexKind kind,
        [NotNullWhen(true)] out Expression? receiver,
        [NotNullWhen(true)] out Expression? term,
        out bool caseInsensitive)
    {
        kind = default;
        receiver = null;
        term = null;
        caseInsensitive = false;

        if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
            return false;

        switch (call.Method.Name)
        {
            case nameof(string.StartsWith):
                kind = MongoRegexKind.StartsWith;
                break;
            case nameof(string.EndsWith):
                kind = MongoRegexKind.EndsWith;
                break;
            case nameof(string.Contains):
                kind = MongoRegexKind.Contains;
                break;
            default:
                return false;
        }

        switch (call.Arguments.Count)
        {
            case 1 when call.Arguments[0].Type == typeof(string) || call.Arguments[0].Type == typeof(char):
                break;

            case 2 when call.Arguments[0].Type == typeof(string)
                        && call.Arguments[1] is ConstantExpression { Value: StringComparison comparison }:
                // D-CULTURE: CurrentCulture[IgnoreCase] is wrong for culture-special data on both paths.
                if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase
                    or StringComparison.CurrentCulture or StringComparison.CurrentCultureIgnoreCase))
                    return false;

                caseInsensitive = comparison is StringComparison.OrdinalIgnoreCase or StringComparison.CurrentCultureIgnoreCase;
                break;

            default:
                return false;
        }

        receiver = call.Object;
        term = call.Arguments[0];
        return true;
    }

    // BSON has no char type and the default char serialization is Int32, so string operators need the string form.
    private static MongoExpression? TranslateCharAsString(Expression node)
    {
        if (node is ConstantExpression { Value: char ch })
            return new MongoConstantExpression(ch.ToString(), forSerialization: null);

        if (node.Type == typeof(char) && NativeQueryParameter.TryGetQueryParameterName(node, out var name))
            return new MongoParameterExpression(name, forSerialization: null);

        return null;
    }

    // IndexOf(string|char[, int]) -> $indexOfCP (code points); StringComparison and count overloads fall through.
    private static bool TryMatchIndexOfMethod(
        Expression node,
        [NotNullWhen(true)] out Expression? receiver,
        [NotNullWhen(true)] out Expression? term,
        out Expression? start)
    {
        receiver = null;
        term = null;
        start = null;

        if (node is not MethodCallExpression call || call.Method.IsStatic || call.Object is null
            || call.Object.Type != typeof(string) || call.Method.Name != nameof(string.IndexOf))
            return false;

        if (call.Arguments.Count is < 1 or > 2
            || (call.Arguments[0].Type != typeof(string) && call.Arguments[0].Type != typeof(char))
            || (call.Arguments.Count == 2 && call.Arguments[1].Type != typeof(int)))
            return false;

        receiver = call.Object;
        term = call.Arguments[0];
        start = call.Arguments.Count == 2 ? call.Arguments[1] : null;
        return true;
    }

    private static bool TryMatchSubstring(
        Expression node, [NotNullWhen(true)] out Expression? receiver, [NotNullWhen(true)] out Expression? start,
        out Expression? length)
    {
        receiver = start = length = null;
        if (node is not MethodCallExpression { Method.Name: nameof(string.Substring), Object: { } obj } call
            || obj.Type != typeof(string) || call.Arguments.Count is < 1 or > 2)
            return false;

        receiver = obj;
        start = call.Arguments[0];
        length = call.Arguments.Count == 2 ? call.Arguments[1] : null;
        return true;
    }

    // The three-argument StringComparison/bool overloads fall through.
    private static bool TryMatchReplace(
        Expression node, [NotNullWhen(true)] out Expression? receiver, [NotNullWhen(true)] out Expression? find,
        [NotNullWhen(true)] out Expression? replacement)
    {
        receiver = find = replacement = null;
        if (node is not MethodCallExpression { Method.Name: nameof(string.Replace), Object: { } obj } call
            || obj.Type != typeof(string) || call.Arguments.Count != 2
            || call.Arguments[0].Type != call.Arguments[1].Type
            || (call.Arguments[0].Type != typeof(string) && call.Arguments[0].Type != typeof(char)))
            return false;

        receiver = obj;
        find = call.Arguments[0];
        replacement = call.Arguments[1];
        return true;
    }

    // $strLenCP counts Unicode code points, .NET counts UTF-16 units, so a surrogate pair is one shorter than in C#.
    // Matches driver-LINQ and the other CP operators; pinned by NativeStringLengthTests.
    private static bool TryMatchStringLength(Expression node, [NotNullWhen(true)] out Expression? receiver)
    {
        receiver = null;

        if (node is not MemberExpression { Expression: { } inner } member
            || inner.Type != typeof(string) || member.Member.Name != nameof(string.Length))
            return false;

        receiver = inner;
        return true;
    }

    // string.FirstOrDefault()/LastOrDefault() bind to the static Enumerable.*<char>(string) overloads with no cast
    // wrapper. Predicated overloads and non-char sources fall through.
    private static bool TryMatchStringFirstOrLastMethod(
        MethodCallExpression call,
        out MongoStringFirstOrLastKind kind,
        [NotNullWhen(true)] out Expression? receiver)
    {
        kind = default;
        receiver = null;

        if (!call.Method.IsStatic || call.Method.DeclaringType != typeof(Enumerable) || call.Arguments.Count != 1)
            return false;

        if (call.Method.Name == nameof(Enumerable.FirstOrDefault))
            kind = MongoStringFirstOrLastKind.First;
        else if (call.Method.Name == nameof(Enumerable.LastOrDefault))
            kind = MongoStringFirstOrLastKind.Last;
        else
            return false;

        if (!call.Method.IsGenericMethod || call.Method.GetGenericArguments() is not [{ } elementType]
            || elementType != typeof(char) || call.Arguments[0].Type != typeof(string))
        {
            return false;
        }

        receiver = call.Arguments[0];
        return true;
    }

    /// <summary>
    /// <c>string.Join(separator, new[] { a, b, c })</c> expanded at translate time into a <see cref="MongoConcatExpression"/>
    /// with the separator interleaved (there is no native join operator).
    /// </summary>
    /// <remarks>
    /// <c>$concat</c> nulls the whole result on any null operand, whereas .NET treats null as empty, so each element is
    /// coalesced against <c>""</c>. Only a <see cref="NewArrayExpression"/> of strings is admitted (runtime collections
    /// have no translate-time arity; non-string elements would need an untested <c>$toString</c>).
    /// </remarks>
    private bool TryTranslateStringJoin(Expression node, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is not MethodCallExpression call
            || !call.Method.IsStatic
            || call.Method.DeclaringType != typeof(string)
            || call.Method.Name != nameof(string.Join)
            || call.Arguments.Count != 2)
        {
            return false;
        }

        if (!TryTranslateValue(call.Arguments[0], out var separator))
            return false;

        // Coalesce the separator too: a null one would null the whole $concat.
        separator = new MongoCoalesceExpression(separator, new MongoConstantExpression(string.Empty, forSerialization: null));

        var elementsArg = call.Arguments[1];
        if (elementsArg is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            elementsArg = convert.Operand;

        if (elementsArg is not NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray
            || newArray.Type.GetElementType() != typeof(string))
        {
            return false;
        }

        if (newArray.Expressions.Count == 0)
        {
            result = new MongoConstantExpression(string.Empty, forSerialization: null);
            return true;
        }

        var operands = new List<MongoExpression>();
        for (var i = 0; i < newArray.Expressions.Count; i++)
        {
            if (i > 0)
                operands.Add(separator);

            if (!TryTranslateValue(newArray.Expressions[i], out var element))
                return false;

            operands.Add(new MongoCoalesceExpression(element, new MongoConstantExpression(string.Empty, forSerialization: null)));
        }

        result = new MongoConcatExpression(operands);
        return true;
    }

    // Framework collection types have a parameterless constructor known to produce an empty collection; a user type's may not.
    private static bool IsFrameworkCollectionType(Type type)
        => type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true
           && type.Assembly.GetName().Name is { } assemblyName
           && (assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName == "mscorlib");

    /// <summary>
    /// Translates the collection side of a <c>Contains</c> call into a constant or query-parameter node, using
    /// <paramref name="property"/> as the element serialization context. <see langword="null"/> for any other shape or an
    /// element-type mismatch.
    /// </summary>
    private static MongoExpression? TranslateInValues(Expression collectionExpr, IProperty property)
    {
        var unwrapped = Unwrap(collectionExpr);

        var elementType = unwrapped.Type.TryGetEnumerableElementType();
        if (elementType is null)
            return null;

        var propertyType = property.ClrType.UnwrapNullableType();
        var underlyingElementType = elementType.UnwrapNullableType();

        // An EF-boxed `object[]`/`List<object>` has element type `object`; each element is coerced through the
        // property's serializer at render/build time, where a wrong-typed element still fails.
        if (underlyingElementType != propertyType && underlyingElementType != typeof(object))
            return null;

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, property);

        // `new List<string>()` inline (Contains_with_local_collection_empty_inline): EF leaves it as a bare
        // NewExpression. Only framework collections are treated as empty: a user collection's parameterless
        // constructor may pre-populate it, which would silently drop its elements from the match.
        if (unwrapped is NewExpression { Arguments.Count: 0 } emptyCollection
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(emptyCollection.Type)
            && IsFrameworkCollectionType(emptyCollection.Type))
        {
            return new MongoConstantExpression(Array.CreateInstance(elementType, 0), property);
        }

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, property);

        // EF8 passes an inline array literal as a NewArrayExpression (EF9+ pre-folds it to a constant).
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            if (TryBuildConstantArray(newArray, elementType, out var values))
                return new MongoConstantExpression(values, property);

            // `new[] { prm1, prm2 }`: EF hoists each captured local as its own query parameter.
            var elements = new MongoExpression[newArray.Expressions.Count];
            for (var i = 0; i < newArray.Expressions.Count; i++)
            {
                var elementExpr = Unwrap(newArray.Expressions[i]);
                if (elementExpr is ConstantExpression elementConstant)
                {
                    elements[i] = new MongoConstantExpression(elementConstant.Value, property);
                }
                else if (NativeQueryParameter.TryGetQueryParameterName(elementExpr, out var elementParameterName))
                {
                    elements[i] = new MongoParameterExpression(elementParameterName, property);
                }
                else
                {
                    return null;
                }
            }

            return new MongoValueListExpression(elements);
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="TranslateInValues"/>, but for a computed item (no backing <see cref="IProperty"/>), so values are
    /// serialized raw (<c>BsonValue.Create</c>).
    /// </summary>
    private static MongoExpression? TranslateInValuesRaw(Expression collectionExpr, Type elementClrType)
    {
        var unwrapped = Unwrap(collectionExpr);

        var elementType = unwrapped.Type.TryGetEnumerableElementType();
        if (elementType != elementClrType)
            return null;

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, forSerialization: null);

        // EF8's inline-array-literal shape (see TranslateInValues).
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            return TryBuildConstantArray(newArray, elementClrType, out var values)
                ? new MongoConstantExpression(values, forSerialization: null)
                : null;
        }

        // RenderInValues' parameter arm falls back to an element serializer keyed on elementClrType.
        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization: null, rawElementType: elementClrType);

        return null;
    }

    private static bool TryBuildConstantArray(
        NewArrayExpression newArray, Type elementType, [NotNullWhen(true)] out Array? values)
    {
        values = Array.CreateInstance(elementType, newArray.Expressions.Count);
        for (var i = 0; i < newArray.Expressions.Count; i++)
        {
            if (Unwrap(newArray.Expressions[i]) is not ConstantExpression elementConstant)
            {
                values = null;
                return false;
            }

            values.SetValue(elementConstant.Value, i);
        }

        return true;
    }

    /// <summary>
    /// Translates the item of <c>arrayField.Contains(constant)</c>, serialized through the array's element serializer
    /// (<see cref="IBsonArraySerializer.TryGetItemSerializationInfo"/>) so element-level representation is honored;
    /// <paramref name="arrayProperty"/> itself as context would coerce the scalar to the whole collection type.
    /// <see langword="null"/> when that can't be done correctly (e.g. a whole-collection converter's serializer).
    /// </summary>
    private static MongoConstantExpression? TranslateArrayContainsItem(Expression itemExpr, IProperty arrayProperty)
    {
        if (itemExpr is not ConstantExpression constant)
            return null;

        var elementType = arrayProperty.ClrType.TryGetEnumerableElementType();
        if (elementType is null)
            return null;

        var underlyingElementType = elementType.UnwrapNullableType();
        var underlyingItemType = itemExpr.Type.UnwrapNullableType();
        if (underlyingItemType != underlyingElementType)
            return null;

        // { field: null } also matches a missing element, which this arm doesn't reproduce; decline.
        if (constant.Value is null)
            return null;

        var arrayInfo = BsonSerializerFactory.GetPropertySerializationInfo(arrayProperty);
        if (arrayInfo.Serializer is not IBsonArraySerializer arraySerializer
            || !arraySerializer.TryGetItemSerializationInfo(out var itemInfo))
        {
            return null;
        }

        try
        {
            var coerced = BsonValueSerializer.Coerce(elementType, constant.Value);
            var rendered = BsonValueSerializer.SerializeNullAware(itemInfo.Serializer, coerced);
            return new MongoConstantExpression(rendered, forSerialization: null);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    // Integral receivers only: $toString formats floating-point, decimal, bool and date differently from .NET.
    private static bool TryMatchIntegralToString(Expression node, [NotNullWhen(true)] out Expression? receiver)
    {
        receiver = null;
        if (node is not MethodCallExpression { Method.Name: nameof(ToString), Object: { } obj, Arguments.Count: 0 })
            return false;

        // Unsigned types are admitted: their default serializers store them exactly (overflow is rejected at
        // SaveChanges, not wrapped), so $toString never sees a wrapped negative (NativeComputedReceiverRegexTests).
        // A non-default serialization declines at the call site (AllFieldsDefaultSerialized).
        // IsIntegerType unwraps Nullable<T>, so a nullable receiver is declined explicitly.
        var type = obj.Type;
        if (type.IsNullableValueType() || !IsIntegerType(type))
            return false;

        receiver = obj;
        return true;
    }

    // string.Concat(string, string[, string[, string]]) has the semantics of `+` (TranslateConcatOperand); object/array/span overloads decline.
    private MongoExpression? TryTranslateStringConcatMethod(Expression node, bool allowNumericWidening)
    {
        if (node is not MethodCallExpression { Method.IsStatic: true } call
            || call.Method.DeclaringType != typeof(string)
            || call.Method.Name != nameof(string.Concat)
            || call.Arguments.Count is < 2 or > 4
            || call.Method.GetParameters().Any(p => p.ParameterType != typeof(string)))
            return null;

        return TranslateConcatParts(call.Arguments, allowNumericWidening);
    }
}
