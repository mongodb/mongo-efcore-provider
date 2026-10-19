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
    private enum MongoQuantifierKind
    {
        /// <summary><c>Any</c> — at least one element satisfies the predicate (or, bare, the array is non-empty).</summary>
        Any,

        /// <summary><c>All</c> — every element satisfies the predicate. Has no parameterless form.</summary>
        All
    }

    /// <summary>
    /// Matches a quantifier call — <c>source.Any()</c>, <c>source.Any(element =&gt; predicate)</c>, or
    /// <c>source.All(element =&gt; predicate)</c> — returning the quantifier's SOURCE with its
    /// <c>AsQueryable()</c> wrapper stripped and, for the predicate form, the unquoted element lambda.
    /// </summary>
    /// <remarks>
    /// EF hands the native translator the <see cref="Queryable"/> spelling, with the lambda
    /// <c>Quote</c>-wrapped and the source wrapped in exactly one <c>AsQueryable()</c> call:
    /// <c>Queryable.Any(Call(AsQueryable, [EF.Property(b, "Posts")]), Quote(p =&gt; ...))</c> — confirmed for
    /// every spelling, including the bare 1-argument form, a nested quantifier (whose own source has the
    /// identical shape, rooted on the element parameter), and a collection reached through owned references.
    /// The <see cref="Enumerable"/> spelling is accepted too, so a hand-built expression tree translates
    /// identically to an EF-produced one. <c>All</c> follows the identical shape but has no parameterless
    /// overload, so a 1-argument call can only ever be <c>Any</c>.
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
                // Bare Any() — the array-is-non-empty test. There is no parameterless All overload, so a
                // 1-argument call can only ever be Any; reject anything else rather than silently treating
                // it as a bare existential.
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
    /// Matches an element-count expression over a collection — the <c>Count</c> property on the collection
    /// itself, a PARAMETERLESS <c>Count()</c>/<c>LongCount()</c> call, or a PREDICATED
    /// <c>Count(predicate)</c>/<c>LongCount(predicate)</c> call — and yields the collection SOURCE with
    /// any <c>AsQueryable()</c> wrapper stripped, plus the predicate lambda (<see langword="null"/> for the
    /// predicate-less forms).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both no-predicate shapes are matched because EF's own preprocessing normalizes a <c>.Count</c>
    /// property access into the method-call form before the native translator ever sees it, so every real
    /// query arrives as <c>Queryable.Count(EF.Property(b, "Posts").AsQueryable())</c>. The
    /// <see cref="MemberExpression"/> arm is still required for a hand-built expression tree (e.g. a unit
    /// test calling <c>TryTranslate</c> directly), which carries a real <c>List&lt;T&gt;.Count</c> member
    /// access.
    /// </para>
    /// <para>
    /// The predicated overloads are matched by canonical <see cref="MethodInfo"/> via
    /// <see cref="IsCanonicalCountWithPredicate"/> rather than by name. <see cref="TranslateOperand"/>'s
    /// caller decides what a predicated match means (a <see cref="MongoFilteredSizeExpression"/> filtered by
    /// the element predicate) — this matcher only recognizes the shape and hands back the lambda unevaluated.
    /// </para>
    /// <para>
    /// This matcher must stay pure/idempotent: <see cref="TranslateComparison"/> can enter it twice per query.
    /// </para>
    /// <para>
    /// The name-based match (for the no-predicate arms) is safe even though an entity may legitimately have a
    /// mapped scalar property called <c>Count</c>: every match is gated on
    /// <see cref="TryResolveOwnedCollectionPath"/>, which requires the source chain to be rooted at the query
    /// parameter with its final hop an embedded collection navigation. A mapped scalar's receiver is an
    /// entity, never a collection, so it cannot resolve to an array path.
    /// </para>
    /// </remarks>
    private static bool TryMatchCountExpression(
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
            // b.Posts.Count — the ICollection<T>/List<T> Count property. Reached only by a HAND-BUILT tree
            // (see the remarks); EF itself normalizes this into the call form below.
            case MemberExpression { Member: PropertyInfo { Name: nameof(List<int>.Count) }, Expression: { } receiver }:
                source = UnwrapAsQueryable(receiver);
                return true;

            // Enumerable/Queryable.Count(source) / LongCount(source) — the parameterless overloads only. This is
            // the shape EVERY real EF query arrives in, for both the .Count property and the .Count() call.
            case MethodCallExpression { Arguments.Count: 1 } call
                when call.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                     && (call.Method.DeclaringType == typeof(Enumerable)
                         || call.Method.DeclaringType == typeof(Queryable)):
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            // The predicated overloads, matched by canonical MethodInfo rather than by name. Generic methods
            // are compared as definitions: an open definition and a constructed instantiation are never
            // reference-equal.
            case MethodCallExpression { Arguments.Count: 2 } call when IsCanonicalCountWithPredicate(call.Method):
                source = UnwrapAsQueryable(call.Arguments[0]);
                predicate = call.Arguments[1].UnwrapLambdaFromQuote();
                return true;

            default:
                return false;
        }
    }

    // The Queryable spelling quotes its lambda and the Enumerable spelling does not; UnwrapLambdaFromQuote above
    // handles both, so both declaring types are admitted here.
    private static bool IsCanonicalCountWithPredicate(MethodInfo method)
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
    /// True when <paramref name="body"/> — the body of an <c>Any</c> element-predicate lambda — references any
    /// <b>free</b> <see cref="ParameterExpression"/> other than <paramref name="elementParameter"/>, i.e. the
    /// predicate is CORRELATED with an enclosing scope (in practice the query parameter, as in
    /// <c>Where(o =&gt; o.Items.Any(i =&gt; o.Name == "x"))</c>). Such a predicate is DECLINED by the <c>Any</c> arm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hazard: identity, not name, decides scope.</b> The <c>Any</c> arm translates its element
    /// predicate with a single-scope <see cref="MongoExpressionTranslator"/> built on the element entity
    /// type, and single-scope <see cref="TryResolveMember"/> resolves a member access by name against that
    /// one scope with no parameter-identity check. A member rooted on the enclosing query parameter would
    /// therefore be silently resolved against the element type whenever both types declare a property of the
    /// same name (e.g. <c>Name</c>, <c>Id</c>), retargeting the condition and returning wrong rows instead of
    /// declining. This mirrors <c>NativeSelectManyBinder.ReferencesParameter</c>: scope must be decided by
    /// reference identity, never by member name.
    /// </para>
    /// <para>
    /// <b>Free, not merely present.</b> A <see cref="ParameterExpression"/> declared by a
    /// <see cref="LambdaExpression"/> inside the body is bound, not free, and must not trigger a decline — a
    /// nested quantifier (<c>Any(p =&gt; p.Comments.Any(c =&gt; c.Text == "t"))</c>) is supported. Parameters
    /// bound while descending are tracked and exempted; EF query parameters are exempt too, since on EF8/EF9
    /// a query parameter is itself a <see cref="ParameterExpression"/> (see
    /// <see cref="NativeQueryParameter.TryGetQueryParameterName"/>).
    /// </para>
    /// <para>
    /// A correlated element predicate is deferred, not impossible: <c>$elemMatch</c> can't reference the
    /// enclosing document at all, so supporting it would need a top-level <c>$expr</c> over
    /// <c>$filter</c>/<c>$anyElementTrue</c> instead of a two-scope translator. Declining keeps the shape on
    /// the driver-LINQ path, which translates it correctly today.
    /// </para>
    /// </remarks>
    private static bool ReferencesEnclosingScope(Expression body, ParameterExpression elementParameter)
    {
        var visitor = new FreeParameterVisitor(elementParameter);
        visitor.Visit(body);
        return visitor.FoundFreeParameter;
    }

    /// <summary>
    /// Finds a reference to a <see cref="ParameterExpression"/> that is free in the visited expression — i.e.
    /// neither the element parameter it was constructed with, nor bound by a <see cref="LambdaExpression"/>
    /// encountered while descending, nor an EF query parameter. See
    /// <see cref="ReferencesEnclosingScope"/> for why this distinction matters.
    /// </summary>
    private sealed class FreeParameterVisitor(ParameterExpression elementParameter) : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _bound = [elementParameter];

        public bool FoundFreeParameter { get; private set; }

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
                FoundFreeParameter = true;

            return node;
        }

        private bool ContainsByIdentity(ParameterExpression parameter)
        {
            foreach (var bound in _bound)
            {
                if (ReferenceEquals(bound, parameter))
                    return true;
            }

            return false;
        }
    }

    // EF wraps a quantifier's collection source in a single Queryable.AsQueryable() call; strip that one
    // layer so the hop walk sees the bare member / EF.Property chain underneath.
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
    /// Recognizes a <c>Contains</c> call over a collection: either the static
    /// <c>Enumerable.Contains(source, item)</c> form, or an instance <c>Contains(item)</c> call whose
    /// declaring type is (or implements) the generic <c>ICollection&lt;T&gt;</c> contract
    /// (<c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>).
    /// Matches by <see cref="System.Reflection.MethodInfo"/> shape, not by name string alone.
    /// </summary>
    private static bool TryMatchContainsMethod(
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

        // Instance List<T>.Contains(item) / ICollection<T>.Contains(item) / HashSet<T>.Contains(item) /
        // IList<T>.Contains(item).
        if (!call.Method.IsStatic && call.Object is not null && call.Arguments.Count == 1)
        {
            var declaringType = call.Method.DeclaringType;
            if (declaringType is { IsGenericType: true })
            {
                var def = declaringType.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>) || def == typeof(ICollection<>))
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
    /// Recognizes the single-argument, ordinal-equivalent overload of
    /// <c>string.StartsWith(string)</c>/<c>EndsWith(string)</c>/<c>Contains(string)</c> — the only
    /// overloads the driver-LINQ v3 provider translates for these methods (it throws
    /// <c>ExpressionNotSupportedException</c> for the <c>StringComparison</c>-taking overloads, confirmed
    /// empirically under <c>MongoQueryMode.DriverLinq</c>). Matching only this overload
    /// keeps native and fallback behavior identical: anything else (a <see cref="StringComparison"/> arg,
    /// or a receiver that isn't <see cref="string"/>) is left unmatched here and falls through to the
    /// driver-LINQ path unchanged.
    /// </summary>
    private static bool TryMatchRegexMethod(
        MethodCallExpression call,
        out MongoRegexKind kind,
        [NotNullWhen(true)] out Expression? receiver,
        [NotNullWhen(true)] out Expression? term)
    {
        kind = default;
        receiver = null;
        term = null;

        if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
            return false;

        if (call.Arguments.Count != 1 || call.Arguments[0].Type != typeof(string))
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

        receiver = call.Object;
        term = call.Arguments[0];
        return true;
    }

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
        if (underlyingElementType != propertyType)
            return null; // collection element type mismatches the property — not supported

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, property);

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, property);

        // EF8 hands an inline array literal (`new[] { .. }.Contains(..)`) as a NewArrayExpression
        // rather than a pre-folded ConstantExpression (the constant-folding that produces the latter
        // only happens on EF9/net9+). Recognize this shape too, but only when every element is itself
        // a constant — anything else (a captured variable reference, a computed element, etc.) is left
        // unmatched here and falls back to driver-LINQ rather than attempting to evaluate arbitrary
        // sub-expressions.
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            var values = Array.CreateInstance(elementType, newArray.Expressions.Count);
            for (var i = 0; i < newArray.Expressions.Count; i++)
            {
                if (Unwrap(newArray.Expressions[i]) is not ConstantExpression elementConstant)
                    return null; // non-constant element — not supported

                values.SetValue(elementConstant.Value, i);
            }

            return new MongoConstantExpression(values, property);
        }

        return null; // any other node shape (method call, sub-expression, etc.) is not supported
    }

    /// <summary>
    /// Translates the ITEM side of an <c>arrayField.Contains(constant)</c> call to a
    /// <see cref="MongoConstantExpression"/> serialized through the array field's own ELEMENT serializer —
    /// mirroring how <see cref="TranslateInValues"/>/<c>MongoQueryLanguageRenderer.RenderInValues</c> resolve
    /// element serialization for <c>$in</c> today, not the array field's own top-level (whole-collection) serializer.
    /// Returns <see langword="null"/> for any shape this cannot serialize correctly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not just reuse <paramref name="arrayProperty"/> as the constant's serialization context.</b>
    /// <see cref="MongoValueRenderer"/> coerces a constant to <c>property.ClrType</c> before serializing
    /// through that property's own serializer; for an array property that is the WHOLE collection type
    /// (e.g. <c>List&lt;string&gt;</c>), and the item value is a single element — coercing a scalar to
    /// <c>List&lt;string&gt;</c> would either throw or, worse, silently mismatch. Instead this resolves the
    /// array's ALREADY-BUILT serializer via <see cref="BsonSerializerFactory.GetPropertySerializationInfo"/>
    /// and asks it, via <see cref="IBsonArraySerializer.TryGetItemSerializationInfo"/>, for the serializer it
    /// actually uses for one element — the same object the array's own reads/writes use, so a value converter
    /// or a non-default <see cref="MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames"/> representation
    /// on individual elements (where the provider supports one) is automatically honored.
    /// </para>
    /// <para>
    /// <b>The correctness guard.</b> A WHOLE-COLLECTION value converter (e.g. a <c>List&lt;MyEnum&gt;</c>
    /// property converted as one unit to <c>List&lt;string&gt;</c>) produces a
    /// <c>ValueConverterSerializer&lt;,&gt;</c>, which does NOT implement <see cref="IBsonArraySerializer"/> —
    /// there is no way to decompose an arbitrary whole-list transform into a per-element one, so
    /// <see cref="IBsonArraySerializer.TryGetItemSerializationInfo"/> is unavailable and this method declines
    /// (returns <see langword="null"/>) rather than risk silently mis-serializing the item against the wrong
    /// (whole-list) shape. This is the exact hazard EF-382's review called out: a value-converted array
    /// element must never be compared using the array's own top-level (whole-collection) serializer.
    /// </para>
    /// <para>
    /// The result is eagerly rendered to a <c>BsonValue</c> at TRANSLATE time (not deferred to render time via
    /// an <see cref="IProperty"/>-carrying node) because translate time and render time are the same
    /// compile-time phase for a native query (the B2 pipeline template is built once, before execution) — see
    /// <c>MongoValueRenderer.RenderValue</c>'s <see cref="MongoConstantExpression"/> arm, which returns a
    /// <c>BsonValue</c> <see cref="MongoConstantExpression.Value"/> unchanged via <c>BsonValue.Create</c>'s
    /// identity case.
    /// </para>
    /// </remarks>
    private static MongoConstantExpression? TranslateArrayContainsItem(Expression itemExpr, IProperty arrayProperty)
    {
        // Scoped to EF-382: only a genuine constant item is supported today (see the dispatch case's own
        // remarks on why a parameterized item is left to fall through and decline).
        if (itemExpr is not ConstantExpression constant)
            return null;

        var elementType = GetEnumerableElementType(arrayProperty.ClrType);
        if (elementType is null)
            return null;

        // The C# compiler's own generic-method resolution for List<T>.Contains(T) / Enumerable.Contains<T>
        // already guarantees itemExpr.Type is T (or assignable to it); this is a cheap defensive re-check —
        // primarily for a hand-built expression tree (e.g. a unit test) rather than anything EF itself emits.
        var underlyingElementType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        var underlyingItemType = Nullable.GetUnderlyingType(itemExpr.Type) ?? itemExpr.Type;
        if (underlyingItemType != underlyingElementType)
            return null;

        // A null item has different array-membership semantics ({ field: null } also matches a MISSING
        // element, not just a stored null) that this arm does not attempt to reproduce — decline rather than
        // risk it. Narrow, not a correctness gap for the ticket's scope (a non-null constant).
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
