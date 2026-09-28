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
/// <see cref="MongoExpressionTranslator"/> — method-call recognizers. Matches the LINQ method shapes the
/// translator understands (quantifiers, element counts, <c>Contains</c>, the string regex family) and the
/// scope guard that keeps a correlated element predicate out of the element-scoped translator.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>Which quantifier <see cref="TryMatchQuantifierMethod"/> matched.</summary>
    internal enum MongoQuantifierKind
    {
        /// <summary><c>Any</c> — at least one element satisfies the predicate (or, bare, the array is non-empty).</summary>
        Any,

        /// <summary><c>All</c> — every element satisfies the predicate. Has no parameterless form.</summary>
        All
    }

    /// <summary>
    /// Matches <c>source.Any()</c>, <c>source.Any(pred)</c> or <c>source.All(pred)</c>, returning the source with its
    /// <c>AsQueryable()</c> wrapper stripped and, for the predicate form, the unquoted element lambda.
    /// </summary>
    /// <remarks>
    /// EF produces the <see cref="Queryable"/> spelling (<c>Queryable.Any(AsQueryable(EF.Property(b, "Posts")),
    /// Quote(p =&gt; ...))</c>); the <see cref="Enumerable"/> spelling is also accepted so hand-built trees
    /// translate the same.
    /// </remarks>
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
                // Bare Any(): array is non-empty. All has no parameterless overload, so reject anything else.
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

    /// <summary>
    /// Matches an element count over a collection (<c>.Count</c> property, <c>Count()</c>/<c>LongCount()</c>, or
    /// <c>Count(pred)</c>/<c>LongCount(pred)</c>), yielding the source with any <c>AsQueryable()</c> stripped and
    /// the predicate lambda (<see langword="null"/> if none).
    /// </summary>
    /// <remarks>
    /// EF normalizes <c>.Count</c> into the call form; the property arm exists for hand-built trees. Must stay
    /// pure: <see cref="TranslateComparison"/> can call it twice per query. Name-based matching is safe against a
    /// mapped scalar named <c>Count</c> because every match is gated on <see cref="TryResolveOwnedCollectionPath"/>.
    /// </remarks>
    internal static bool TryMatchCountExpression(
        Expression node,
        [NotNullWhen(true)] out Expression? source,
        out LambdaExpression? predicate)
    {
        source = null;
        predicate = null;

        // Only int/long-valued nodes can be a count; this cheaply excludes unrelated members named "Count".
        if (node.Type != typeof(int) && node.Type != typeof(long))
            return false;

        switch (node)
        {
            // b.Posts.Count — only from hand-built trees; EF normalizes this into the call form.
            case MemberExpression { Member: PropertyInfo { Name: nameof(List<int>.Count) }, Expression: { } receiver }:
                source = UnwrapAsQueryable(receiver);
                return true;

            // Parameterless Count(source)/LongCount(source): the shape every real EF query arrives in.
            case MethodCallExpression { Arguments.Count: 1 } call
                when call.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                     && (call.Method.DeclaringType == typeof(Enumerable)
                         || call.Method.DeclaringType == typeof(Queryable)):
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            // Predicated overloads, compared as generic method definitions (a constructed instantiation is never
            // reference-equal to the open definition).
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
    /// True when <paramref name="body"/> (an element-predicate lambda body) references a free parameter other
    /// than <paramref name="elementParameter"/>, i.e. is correlated with an enclosing scope. Also reports the
    /// sole free parameter via <paramref name="found"/> (<see langword="null"/> if none or more than one), so
    /// <c>Count(pred)</c>/<c>Any</c>/<c>All</c> can build a two-scope translator only when that parameter is
    /// exactly <see cref="MongoExpressionTranslator.SelfParam"/>.
    /// </summary>
    /// <remarks>
    /// Scope is decided by identity, not name: single-scope <see cref="TryResolveMember"/> resolves members by
    /// name, so an outer-rooted <c>o.Name</c> would silently bind to the element's <c>Name</c> and return wrong
    /// rows (cf. <c>NativeSelectManyBinder.ReferencesParameter</c>). Parameters bound by nested lambdas, and EF
    /// query parameters (themselves <see cref="ParameterExpression"/>s on EF8/EF9, see
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

    /// <summary>
    /// Finds parameters free in the visited expression: not the element parameter, not bound by a nested lambda,
    /// and not an EF query parameter. See <see cref="ReferencesEnclosingScope"/>.
    /// </summary>
    private sealed class FreeParameterVisitor(ParameterExpression elementParameter) : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _bound = [elementParameter];

        // Tracked by reference identity so one parameter referenced twice isn't counted as two.
        private readonly List<ParameterExpression> _distinctFree = [];

        public bool FoundFreeParameter { get; private set; }

        /// <summary>
        /// The sole free parameter, or <see langword="null"/> if there are none or two or more distinct ones. A
        /// multi-parameter body must not pass a caller's <c>ReferenceEquals(found, expectedScope)</c> check, or the
        /// second parameter's members would be resolved by name against the element scope (wrong rows).
        /// </summary>
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

            // Pop only what this lambda actually pushed, so an outer binding of the same instance survives.
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

    // EF wraps a quantifier's source in one Queryable.AsQueryable(); strip it to expose the member/EF.Property chain.
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

    /// <summary>
    /// Recognizes <c>Enumerable.Contains(source, item)</c> or an instance <c>Contains(item)</c> on an
    /// <c>ICollection&lt;T&gt;</c> implementation or a set interface that declares its own <c>Contains</c>
    /// (<c>IReadOnlySet&lt;T&gt;</c>, <c>IImmutableSet&lt;T&gt;</c>), matched by method shape rather than name alone.
    /// </summary>
    internal static bool TryMatchContainsMethod(
        MethodCallExpression call,
        [NotNullWhen(true)] out Expression? collection,
        [NotNullWhen(true)] out Expression? item)
    {
        collection = null;
        item = null;

        if (call.Method.Name != nameof(Enumerable.Contains))
            return false;

        // Static Enumerable.Contains<TSource>(IEnumerable<TSource>, TSource) — the two-argument form only;
        // the three-argument IEqualityComparer<TSource> overload has no query-dialect equivalent.
        if (call.Method.IsStatic && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            item = call.Arguments[1];
            return true;
        }

        // Instance ICollection<T>.Contains(item) (List<T>, HashSet<T>, IList<T>, ...), plus the set interfaces that
        // declare their own Contains. ISet<T> needs no entry: its Contains is inherited from ICollection<T>.
        if (!call.Method.IsStatic && call.Object is not null && call.Arguments.Count == 1)
        {
            var declaringType = call.Method.DeclaringType;
            if (declaringType is { IsGenericType: true })
            {
                var def = declaringType.GetGenericTypeDefinition();
                // An explicit whitelist, deliberately: generalizing to "any IEnumerable<T> with a Contains(T)" would also
                // capture string.Contains(char).
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>) || def == typeof(ICollection<>)
                    || def == typeof(IReadOnlySet<>) || def == typeof(IImmutableSet<>)
                    // EF8 hands these concrete-type instance calls over un-normalized (EF9+ rewrites them to
                    // Enumerable.Contains before the provider sees them).
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
    /// Recognizes <c>string.StartsWith/EndsWith/Contains(string)</c>, plus the <see cref="StringComparison"/>
    /// overload for <see cref="StringComparison.Ordinal"/>/<see cref="StringComparison.OrdinalIgnoreCase"/> only —
    /// the members MongoDB regex can reproduce exactly. Culture-sensitive comparisons are left unmatched (the
    /// driver-LINQ path silently treats them as ordinal; not replicated in a new native path).
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
            case 1 when call.Arguments[0].Type == typeof(string):
                break;

            case 2 when call.Arguments[0].Type == typeof(string)
                        && call.Arguments[1] is ConstantExpression { Value: StringComparison comparison }:
                if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
                    return false;

                caseInsensitive = comparison == StringComparison.OrdinalIgnoreCase;
                break;

            default:
                return false;
        }

        receiver = call.Object;
        term = call.Arguments[0];
        return true;
    }

    /// <summary>
    /// Recognizes only the single-argument <c>string.IndexOf(string)</c> (<c>$indexOfCP</c>), the overload the
    /// driver-LINQ path translates, so native and fallback behavior match. Other overloads fall through.
    /// </summary>
    private static bool TryMatchIndexOfMethod(
        Expression node, [NotNullWhen(true)] out Expression? receiver, [NotNullWhen(true)] out Expression? term)
    {
        receiver = null;
        term = null;

        if (node is not MethodCallExpression call || call.Method.IsStatic || call.Object is null
            || call.Object.Type != typeof(string) || call.Method.Name != nameof(string.IndexOf))
            return false;

        if (call.Arguments.Count != 1 || call.Arguments[0].Type != typeof(string))
            return false;

        receiver = call.Object;
        term = call.Arguments[0];
        return true;
    }

    /// <summary>
    /// Recognizes <c>string.Length</c> (<c>x.Name.Length</c>) — the only member the aggregation dialect has a
    /// dedicated operator for (<c>$strLenCP</c>). A receiver that isn't <see cref="string"/> is left unmatched.
    /// </summary>
    private static bool TryMatchStringLength(Expression node, [NotNullWhen(true)] out Expression? receiver)
    {
        receiver = null;

        if (node is not MemberExpression { Expression: { } inner } member
            || inner.Type != typeof(string) || member.Member.Name != nameof(string.Length))
            return false;

        receiver = inner;
        return true;
    }

    /// <summary>
    /// Recognizes <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c>, which bind to the static
    /// <c>Enumerable.FirstOrDefault&lt;char&gt;(string)</c>/<c>LastOrDefault&lt;char&gt;(string)</c> overloads with no
    /// cast wrapper. Predicated overloads and non-<see langword="char"/> sources fall through to driver-LINQ.
    /// </summary>
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
    /// Recognizes <c>string.Join(separator, new[] { a, b, c })</c> over a fixed-length array literal of strings and
    /// expands it at translate time into a <see cref="MongoConcatExpression"/> (<c>$concat</c>) with the separator
    /// interleaved; there is no native join operator.
    /// </summary>
    /// <remarks>
    /// <c>$concat</c> nulls the whole result on any null operand, whereas .NET treats a null element as empty, so
    /// each element is wrapped in <see cref="MongoCoalesceExpression"/> against <c>""</c>. Only a
    /// <see cref="NewArrayExpression"/> argument is admitted (runtime collections have no translate-time arity),
    /// and only string elements (non-string <c>T</c> would need an untested <c>$toString</c>).
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

        // Coalesce the separator too: a null separator would otherwise null the whole $concat, whereas .NET
        // treats it as empty. Reused at every interleaved position.
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

    /// <summary>
    /// Whether <paramref name="type"/> is one of the framework's own collection types (<c>List&lt;T&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, <c>ImmutableList&lt;T&gt;</c>, …) — whose parameterless constructor is known to produce an
    /// empty collection — rather than a user-defined one.
    /// </summary>
    private static bool IsFrameworkCollectionType(Type type)
        => type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true
           && type.Assembly.GetName().Name is { } assemblyName
           && (assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName == "mscorlib");

    /// <summary>
    /// Translates the collection side of a <c>Contains</c> call into a <see cref="MongoConstantExpression"/>
    /// (a captured/inline collection) or <see cref="MongoParameterExpression"/> (a query-parameter
    /// collection), using <paramref name="property"/> as the element serialization context. Returns
    /// <see langword="null"/> for any other shape, or when the collection's element type does not match
    /// the property's CLR type.
    /// </summary>
    private static MongoExpression? TranslateInValues(Expression collectionExpr, IProperty property)
    {
        var unwrapped = Unwrap(collectionExpr);

        var elementType = GetEnumerableElementType(unwrapped.Type);
        if (elementType is null)
            return null;

        var propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var underlyingElementType = Nullable.GetUnderlyingType(elementType) ?? elementType;

        // An EF-boxed `object[]`/`List<object>` has element type `object`, not the property's CLR type. No static
        // check needed: each element is coerced through the property's serializer at render/build time
        // (RenderInValues / MongoPipelineFactory.SerializeParameter), where a wrong-typed element still fails.
        if (underlyingElementType != propertyType && underlyingElementType != typeof(object))
            return null; // collection element type mismatches the property — not supported

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, property);

        // `new List<string>()` inline (EF's Contains_with_local_collection_empty_inline): EF's parameter extraction
        // leaves an argument-less collection construction as a bare NewExpression rather than folding it to a
        // constant. For a BCL collection type that is always empty, so it is an empty $in/$nin haystack. Restricted to
        // the framework's own System.Collections* types: a user collection's parameterless constructor may
        // pre-populate it, and treating that as empty would silently drop its elements from the match.
        if (unwrapped is NewExpression { Arguments.Count: 0 } emptyCollection
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(emptyCollection.Type)
            && IsFrameworkCollectionType(emptyCollection.Type))
        {
            return new MongoConstantExpression(Array.CreateInstance(elementType, 0), property);
        }

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, property);

        // EF8 passes an inline array literal as a NewArrayExpression (EF9+ pre-folds it to a constant). Handle the
        // all-constant case here; anything else falls through to the per-element loop.
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            var values = Array.CreateInstance(elementType, newArray.Expressions.Count);
            var allConstant = true;
            for (var i = 0; i < newArray.Expressions.Count; i++)
            {
                if (Unwrap(newArray.Expressions[i]) is not ConstantExpression elementConstant)
                {
                    allConstant = false;
                    break;
                }

                values.SetValue(elementConstant.Value, i);
            }

            if (allConstant)
                return new MongoConstantExpression(values, property);

            // `new[] { prm1, prm2 }` with separately captured locals: EF hoists each element as its own query
            // parameter. Build a MongoValueListExpression of per-element nodes; decline on any computed element.
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
                    return null; // unsupported element shape
                }
            }

            return new MongoValueListExpression(elements);
        }

        return null; // any other node shape (method call, sub-expression, etc.) is not supported
    }

    /// <summary>
    /// Like <see cref="TranslateInValues"/>, but for a <c>Contains</c> whose item is computed (no backing
    /// <see cref="IProperty"/>), so values are serialized raw (<c>BsonValue.Create</c>). A query-parameter
    /// collection declines here because <see cref="PlaceholderTable.CreateArrayPlaceholder"/> needs a real element
    /// serializer.
    /// </summary>
    private static MongoExpression? TranslateInValuesRaw(Expression collectionExpr, Type elementClrType)
    {
        var unwrapped = Unwrap(collectionExpr);

        var elementType = GetEnumerableElementType(unwrapped.Type);
        if (elementType != elementClrType)
            return null;

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, forSerialization: null);

        // EF8's inline-array-literal shape (see TranslateInValues).
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            var values = Array.CreateInstance(elementType, newArray.Expressions.Count);
            for (var i = 0; i < newArray.Expressions.Count; i++)
            {
                if (Unwrap(newArray.Expressions[i]) is not ConstantExpression elementConstant)
                    return null; // non-constant element — not supported

                values.SetValue(elementConstant.Value, i);
            }

            return new MongoConstantExpression(values, forSerialization: null);
        }

        // A captured local arrives as an EF query parameter. ForSerialization stays null; RenderInValues'
        // parameter arm falls back to an element serializer keyed on elementClrType.
        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization: null, rawElementType: elementClrType);

        return null; // any other shape is not supported for a computed needle
    }

    /// <summary>
    /// Translates the item of <c>arrayField.Contains(constant)</c> to a <see cref="MongoConstantExpression"/>
    /// serialized through the array's element serializer. Returns <see langword="null"/> if that can't be done
    /// correctly.
    /// </summary>
    /// <remarks>
    /// Using <paramref name="arrayProperty"/> as the serialization context would coerce the scalar to the whole
    /// collection type. Instead the element serializer comes from
    /// <see cref="IBsonArraySerializer.TryGetItemSerializationInfo"/> on the property's serializer, so element-level
    /// representation is honored. A whole-collection value converter's serializer isn't an
    /// <see cref="IBsonArraySerializer"/>, so this declines rather than mis-serializing. The value is rendered
    /// eagerly because translate and render happen in the same compile-time phase for native queries.
    /// </remarks>
    private static MongoConstantExpression? TranslateArrayContainsItem(Expression itemExpr, IProperty arrayProperty)
    {
        // Only a constant item is supported (a parameterized item falls through and declines).
        if (itemExpr is not ConstantExpression constant)
            return null;

        var elementType = GetEnumerableElementType(arrayProperty.ClrType);
        if (elementType is null)
            return null;

        // Defensive: the compiler already guarantees this for EF trees; guards hand-built ones.
        var underlyingElementType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        var underlyingItemType = Nullable.GetUnderlyingType(itemExpr.Type) ?? itemExpr.Type;
        if (underlyingItemType != underlyingElementType)
            return null;

        // { field: null } also matches a missing element, which this arm doesn't reproduce; decline.
        if (constant.Value is null)
            return null;

        var arrayInfo = BsonSerializerFactory.GetPropertySerializationInfo(arrayProperty);
        if (arrayInfo.Serializer is not IBsonArraySerializer arraySerializer
            || !arraySerializer.TryGetItemSerializationInfo(out var itemInfo))
        {
            return null; // e.g. a whole-collection value converter — no per-element serializer to resolve.
        }

        try
        {
            var coerced = BsonValueSerializer.Coerce(elementType, constant.Value);
            var rendered = BsonValueSerializer.SerializeThroughWriter(itemInfo.Serializer, coerced);
            return new MongoConstantExpression(rendered, forSerialization: null);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }
}
