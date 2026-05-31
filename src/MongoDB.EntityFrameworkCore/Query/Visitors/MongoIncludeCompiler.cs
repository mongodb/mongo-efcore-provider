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
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Compiles cross-collection <c>Include</c> sub-queries (EF-117).
/// </summary>
/// <remarks>
/// <para>
/// For each <c>IncludeExpression</c> whose navigation crosses collection
/// boundaries (<see cref="MongoNavigationExtensions.IsEmbedded"/> returns
/// <see langword="false"/>), the shaper-stage visitor emits a call to one of
/// the <c>Load*</c> helpers here. The helper builds and executes a
/// parameterized sub-query against the related collection and returns the
/// materialized result, which the existing <c>IncludeReference</c> /
/// <c>IncludeCollection</c> machinery in
/// <see cref="MongoProjectionBindingRemovingExpressionVisitor"/> then wires
/// up via standard EF fixup.
/// </para>
/// <para>
/// Stage 1 implements collection navigations on the principal side
/// (e.g. <c>Customer.Orders</c>). Reference navigations on the dependent
/// side (e.g. <c>Order.Customer</c>) require JOIN-unwrap handling and land
/// in Stage 2; ThenInclude chains land in Stage 3.
/// </para>
/// </remarks>
internal static class MongoIncludeCompiler
{
    /// <summary>
    /// Partitions an <see cref="IncludeExpression"/> by type: many-to-many
    /// (skip navigation) is rejected with a clear error; everything else
    /// passes through as an <see cref="INavigation"/> for downstream stages
    /// to dispatch on <see cref="IsCrossCollection"/>.
    /// </summary>
    public static INavigation ClassifyIncludeNavigation(IncludeExpression includeExpression)
    {
        if (includeExpression.Navigation is not INavigation navigation)
        {
            var skipNavigation = includeExpression.Navigation;
            throw new InvalidOperationException(
                $"Including the many-to-many navigation '{skipNavigation.DeclaringEntityType.DisplayName()
                }.{skipNavigation.Name}' is not yet supported by the MongoDB EF Core provider. "
                + "Many-to-many Include is tracked as a follow-up to EF-117.");
        }

        return navigation;
    }

    /// <summary>
    /// <see langword="true"/> when the navigation crosses collection
    /// boundaries — i.e. is not embedded in the principal's BSON document.
    /// Cross-collection includes need a fan-out loader; embedded includes
    /// traverse the same document and use the existing path.
    /// </summary>
    public static bool IsCrossCollection(INavigation navigation)
        => !navigation.IsEmbedded();

    /// <summary>
    /// Decides whether a cross-collection include should use a server-side
    /// <c>$lookup</c> or fall back to the client-side fan-out loader.
    /// Stage 0: always fan-out. Later stages enable shapes incrementally.
    /// </summary>
    public static IncludeStrategy ChooseStrategy(IncludeExpression? includeExpression, INavigation navigation)
    {
        // A top-level principal→dependent collection Include with a single-column foreign key
        // routes to a server-side $lookup. Anything with a nested (ThenInclude /
        // nested-collection) chain still fans out client-side. A composite (multi-column) FK
        // must fall through to fan-out — the single-field $lookup the ServerLookup path emits
        // (built from Properties[0] only) would be wrong — mirroring the reference branch below.
        if (IsCrossCollection(navigation)
            && navigation.IsCollection
            && !navigation.IsOnDependent
            && navigation.ForeignKey.Properties.Count == 1
            && !HasNestedInclude(includeExpression))
        {
            // A USER Where predicate inside the include lambda (Include(c => c.Orders.Where(o => ...)))
            // cannot be rendered to a server-side $match (the provider has no predicate→BSON renderer),
            // so it MUST route to the client-side fan-out loader where the driver's LINQ translates the
            // predicate. This takes precedence over the ordering/paging pipeline check — a filtered
            // include that ALSO orders/pages is run end-to-end on fan-out so the predicate is honored.
            // (Without this, a Where-only include would fall through to the simple $lookup below and
            // SILENTLY IGNORE the predicate — a correctness hole.)
            if (HasUserWhereInclude(includeExpression, navigation))
            {
                return IncludeStrategy.ClientFanOut;
            }

            // A FILTERED collection include (ordering / paging inside the include lambda) routes to
            // the pipeline-form $lookup only when its sub-query is fully translatable to
            // element-name-aware $sort/$skip/$limit stages. A projecting Select, Distinct, or a
            // non-constant Skip/Take count is NOT translated here and must stay on the client-side
            // fan-out loader rather than emit wrong MQL.
            if (IsFilteredCollectionInclude(includeExpression)
                && !TryExtractFilteredCollectionPipeline(includeExpression!.NavigationExpression, navigation, out _))
            {
                return IncludeStrategy.ClientFanOut;
            }

            return IncludeStrategy.ServerLookup;
        }

        // A dependent→principal REFERENCE Include with a single-column foreign key routes to a
        // server-side $lookup + $unwind (preserveNullAndEmptyArrays). Reference-ROOTED chains
        // (Order.Customer.ThenInclude(...)) also route here when the whole nested chain is
        // lookup-able — i.e. all single-FK references with AT MOST ONE terminal collection and
        // no collection / composite FK in a non-terminal position (see IsNestedChainLookupable).
        // Composite-key references at the root (Properties.Count > 1) stay fan-out for now.
        if (IsCrossCollection(navigation)
            && !navigation.IsCollection
            && navigation.IsOnDependent
            && navigation.ForeignKey.Properties.Count == 1
            && (includeExpression is null || IsNestedChainLookupable(includeExpression)))
        {
            return IncludeStrategy.ServerLookup;
        }

        return IncludeStrategy.ClientFanOut;
    }

    /// <summary>
    /// <see langword="true"/> when the include carries a nested ThenInclude /
    /// nested-collection chain (encoded by EF nav-expansion inside the outer
    /// <c>NavigationExpression</c>). Such shapes are not yet supported by the
    /// $lookup path and must continue to fan out.
    /// </summary>
    private static bool HasNestedInclude(IncludeExpression? includeExpression)
        => includeExpression?.NavigationExpression is { } navigationExpression
           && EnumerateNestedIncludes(navigationExpression).Any();

    /// <summary>
    /// Extracts the chained ThenInclude navigations from an outer
    /// <see cref="IncludeExpression"/>'s <c>NavigationExpression</c>. EF Core
    /// nav-expansion encodes ThenInclude as nested
    /// <c>Select(t =&gt; IncludeExpression(t, ..., nestedNav))</c> shapes inside
    /// the outer <c>MaterializeCollectionNavigationExpression.Subquery</c>;
    /// this helper walks that nesting and returns a dot-separated path
    /// (e.g. <c>"Items.Tag"</c>) suitable for passing to
    /// <c>EntityFrameworkQueryableExtensions.Include(string)</c> when the
    /// loader runs its sub-query.
    /// </summary>
    public static string? ExtractIncludeChainPath(IncludeExpression includeExpression)
    {
        var chain = EnumerateNestedIncludes(includeExpression.NavigationExpression).Select(n => n.Name).ToList();
        return chain.Count > 0 ? string.Join(".", chain) : null;
    }

    /// <summary>
    /// Walks an outer Include's <c>NavigationExpression</c> yielding each nested
    /// (ThenInclude / nested-collection) navigation in chain order. EF nav-expansion
    /// encodes ThenInclude as a tail <c>Select(t =&gt; IncludeExpression(t, ..., nav))</c>
    /// inside the (optionally <see cref="MaterializeCollectionNavigationExpression"/>-wrapped)
    /// sub-query. Both <see cref="ExtractIncludeChainPath"/> (joins names) and
    /// <c>HasNestedInclude</c> (tests <c>.Any()</c>) drive off this single walker.
    /// </summary>
    private static IEnumerable<INavigation> EnumerateNestedIncludes(Expression expression)
    {
        // A reference-ROOTED ThenInclude chain (post ReferenceChainJoinUnwrapper) nests the
        // child directly as the parent include's NavigationExpression — no Select wrapper.
        if (expression is IncludeExpression directNestedInclude
            && directNestedInclude.Navigation is INavigation directNav)
        {
            yield return directNav;
            foreach (var deeper in EnumerateNestedIncludes(directNestedInclude.NavigationExpression))
            {
                yield return deeper;
            }

            yield break;
        }

        // Collection navigations wrap the sub-query in a
        // MaterializeCollectionNavigationExpression — unwrap to the inner query.
        // Reference navigations don't wrap, so we operate on the expression as-is.
        if (expression is MaterializeCollectionNavigationExpression mcne)
        {
            expression = mcne.Subquery;
        }

        if (expression is MethodCallExpression mc
            && mc.Method.IsGenericMethod
            && mc.Method.GetGenericMethodDefinition() == QueryableMethods.Select
            && mc.Arguments.Count == 2
            && Unquote(mc.Arguments[1]) is LambdaExpression selectorLambda
            && selectorLambda.Body is IncludeExpression nestedInclude
            && nestedInclude.Navigation is INavigation nav)
        {
            yield return nav;
            foreach (var deeper in EnumerateNestedIncludes(nestedInclude.NavigationExpression))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// Walks the nested-include chain (via <see cref="EnumerateNestedIncludes"/>) and decides
    /// whether the whole chain can be served by chained <c>$lookup</c> stages. A chain is
    /// lookup-able when every nested level is a single-column foreign key, references appear in
    /// any position, and at most one collection appears — and only as the TERMINAL level (a
    /// dotted path into a <c>$lookup</c> array output is clobbered server-side, so a collection
    /// can never be an intermediate level). An empty chain is trivially lookup-able.
    /// </summary>
    public static bool IsNestedChainLookupable(IncludeExpression includeExpression)
    {
        var nestedNavigations = includeExpression.NavigationExpression is { } navigationExpression
            ? EnumerateNestedIncludes(navigationExpression).ToList()
            : [];

        for (var i = 0; i < nestedNavigations.Count; i++)
        {
            var nav = nestedNavigations[i];

            // Cross-collection only; composite FK levels stay on fan-out for now.
            if (!IsCrossCollection(nav) || nav.ForeignKey.Properties.Count != 1)
            {
                return false;
            }

            // A collection may only be the terminal level — never followed by anything.
            if (nav.IsCollection && i != nestedNavigations.Count - 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see langword="true"/> when a top-level collection include applies ORDERING or PAGING inside
    /// its lambda (OrderBy / ThenBy / Skip / Take), which the pipeline-form <c>$lookup</c> realizes
    /// as <c>$sort</c> / <c>$skip</c> / <c>$limit</c> stages.
    /// </summary>
    /// <remarks>
    /// This intentionally tests ONLY ordering/paging. A user <c>Where</c> predicate is handled
    /// separately by <see cref="HasUserWhereInclude"/> (it routes to fan-out, where the driver
    /// translates the predicate); a bare correlation <c>Where</c> (realized by the <c>$match $expr</c>)
    /// or an EF-injected GLOBAL QUERY FILTER is neither ordering/paging nor a user filter and so does
    /// not trigger the pipeline form here.
    /// </remarks>
    public static bool IsFilteredCollectionInclude(IncludeExpression? includeExpression)
    {
        if (includeExpression?.NavigationExpression
            is not MaterializeCollectionNavigationExpression { Subquery: var subquery })
        {
            return false;
        }

        for (var current = subquery; current is MethodCallExpression { Method.IsGenericMethod: true } call;
             current = call.Arguments[0])
        {
            var definition = call.Method.GetGenericMethodDefinition();
            if (definition == QueryableMethods.OrderBy
                || definition == QueryableMethods.OrderByDescending
                || definition == QueryableMethods.ThenBy
                || definition == QueryableMethods.ThenByDescending
                || definition == QueryableMethods.Skip
                || definition == QueryableMethods.Take)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see langword="true"/> when a top-level collection include's navigation sub-query carries a
    /// USER <c>Where</c> predicate — i.e. a <c>Where</c> whose lambda does NOT reference the outer
    /// principal shaper (that one is the FK CORRELATION condition, realized by the simple/pipeline
    /// <c>$lookup</c>). Such a predicate cannot be rendered to a server-side <c>$match</c>, so the
    /// include must run on the client-side fan-out loader, where the driver's LINQ translates it.
    /// </summary>
    /// <remarks>
    /// EF nav-expansion wraps the sub-query in a <see cref="MaterializeCollectionNavigationExpression"/>
    /// and emits the FK correlation as the innermost <c>Where</c> (references the outer
    /// <see cref="StructuralTypeShaperExpression"/> / the threaded <see cref="QueryContext"/>); a user
    /// filter is an ADDITIONAL <c>Where</c> directly over <c>IQueryable&lt;TDependent&gt;</c> whose lambda
    /// references only the dependent. <see cref="IsUserWhereOnDependent"/> distinguishes the two and ALSO
    /// excludes EF-internal Wheres introduced by other rewrites — most importantly a model-level GLOBAL
    /// QUERY FILTER, which EF expands over a TRANSPARENT IDENTIFIER element type (e.g. when the filter
    /// touches a navigation) rather than over <c>TDependent</c>. Such filters stay on the existing
    /// simple-/pipeline-<c>$lookup</c> routing (EF applies the global filter separately), so this method
    /// targets ONLY a genuine user predicate written inside the include lambda.
    /// </remarks>
    public static bool HasUserWhereInclude(IncludeExpression? includeExpression, INavigation navigation)
    {
        if (includeExpression?.NavigationExpression
            is not MaterializeCollectionNavigationExpression { Subquery: var subquery })
        {
            return false;
        }

        var dependentClrType = navigation.TargetEntityType.ClrType;
        for (var current = subquery; current is MethodCallExpression { Method.IsGenericMethod: true } call;
             current = call.Arguments[0])
        {
            if (IsUserWhereOnDependent(call, dependentClrType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="call"/> is a <c>Queryable.Where</c> whose single
    /// generic type argument is exactly the dependent CLR type AND whose predicate is self-contained
    /// (no correlation / <see cref="QueryContext"/> reference). This is the only <c>Where</c> shape the
    /// fan-out path captures and recomposes onto <c>DbContext.Set&lt;TDependent&gt;()</c>: the FK
    /// correlation <c>Where</c>, and any EF-internal <c>Where</c> over a transparent-identifier element
    /// type (e.g. a global query filter spanning a navigation), are both excluded.
    /// </summary>
    private static bool IsUserWhereOnDependent(MethodCallExpression call, Type dependentClrType)
        => call.Method.IsGenericMethod
           && call.Method.GetGenericMethodDefinition() == QueryableMethods.Where
           && call.Method.GetGenericArguments() is [var elementType] && elementType == dependentClrType
           && !ReferencesCorrelation(call.Arguments[1]);

    /// <summary>
    /// <see langword="true"/> when an ordering/paging operator call's SOURCE element type is the
    /// dependent CLR type — i.e. its first generic type argument is <paramref name="dependentClrType"/>
    /// (<c>OrderBy</c>/<c>ThenBy</c> carry the element type first, then the key type; <c>Skip</c>/<c>Take</c>
    /// carry only the element type). Operators over a transparent-identifier element type (EF rewrites)
    /// return <see langword="false"/> and are not recomposed onto the stand-alone dependent query.
    /// </summary>
    private static bool IsOperatorOnDependent(MethodCallExpression call, Type dependentClrType)
        => call.Method.IsGenericMethod
           && call.Method.GetGenericArguments() is [var elementType, ..] && elementType == dependentClrType;

    /// <summary>
    /// Attempts to translate a FILTERED top-level collection Include's navigation sub-query into
    /// element-name-aware <c>$lookup</c> pipeline stages (<c>$sort</c> / <c>$skip</c> / <c>$limit</c>),
    /// which the emission side wraps after the correlation <c>$match</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF nav-expansion encodes a filtered include as
    /// <c>MaterializeCollectionNavigation(DbSet&lt;TDependent&gt;().Where(&lt;join&gt;).OrderBy(...).Skip(n).Take(m))</c>.
    /// The outermost <c>Where</c> is the CORRELATION condition (it references the outer principal
    /// shaper) and is already realized by the pipeline-form <c>$lookup</c>'s
    /// <c>$match { $expr: { $eq: [...] } }</c>, so it is dropped here.
    /// </para>
    /// <para>
    /// Only ordering and paging operators are translated — they map cleanly to element names via
    /// <see cref="MongoPropertyExtensions.GetElementName"/>, requiring no predicate→<c>$match</c>
    /// machinery. A USER <c>Where</c> filter (one that does not reference the outer principal), a
    /// projecting <c>Select</c>, <c>Distinct</c>, a non-constant <c>Skip</c>/<c>Take</c> count, or an
    /// ordering whose key is not a simple mapped scalar property all return <see langword="false"/>
    /// so the caller falls back to the client-side fan-out loader rather than emitting wrong MQL.
    /// </para>
    /// </remarks>
    public static bool TryExtractFilteredCollectionPipeline(
        Expression? navigationExpression,
        INavigation navigation,
        out List<BsonDocument> stages)
    {
        stages = [];

        if (navigationExpression is not MaterializeCollectionNavigationExpression { Subquery: var subquery })
        {
            return false;
        }

        var targetEntityType = navigation.TargetEntityType;

        // Walk from the OUTERMOST operator inward, prepending each produced stage so the final list
        // is in execution (innermost-first) order. ThenBy/ThenByDescending appear OUTSIDE their
        // OrderBy when walking inward, so each ordering operator builds a fresh single-key $sort and
        // a following ThenBy merges into the $sort it sits directly outside of — tracked via
        // pendingSortKeys, the keys document of the $sort being built for the current ordering group.
        var ordered = new List<BsonDocument>();
        BsonDocument? pendingSortKeys = null;
        var current = subquery;
        var sawAny = false;

        while (current is MethodCallExpression call && call.Method.IsGenericMethod)
        {
            var definition = call.Method.GetGenericMethodDefinition();

            if (definition == QueryableMethods.OrderBy || definition == QueryableMethods.OrderByDescending)
            {
                if (!TryGetSortKey(call, targetEntityType, out var key))
                {
                    return false;
                }

                var direction = definition == QueryableMethods.OrderByDescending ? -1 : 1;
                if (pendingSortKeys is null)
                {
                    pendingSortKeys = new BsonDocument();
                    ordered.Add(new BsonDocument("$sort", pendingSortKeys));
                }

                // OrderBy is the PRIMARY key of its group — it must sort before any ThenBy already
                // merged into the keys doc. Walking outermost-inward, ThenBy was seen first, so
                // prepend OrderBy to keep document order = sort precedence.
                pendingSortKeys.InsertAt(0, new BsonElement(key, direction));
                pendingSortKeys = null; // group complete; a further ordering op starts a new $sort
                sawAny = true;
                current = call.Arguments[0];
            }
            else if (definition == QueryableMethods.ThenBy || definition == QueryableMethods.ThenByDescending)
            {
                if (!TryGetSortKey(call, targetEntityType, out var key))
                {
                    return false;
                }

                var direction = definition == QueryableMethods.ThenByDescending ? -1 : 1;
                if (pendingSortKeys is null)
                {
                    pendingSortKeys = new BsonDocument();
                    ordered.Add(new BsonDocument("$sort", pendingSortKeys));
                }

                // ThenBy is a SECONDARY key; it sorts after the (inner) OrderBy and after any
                // earlier (more-outer) ThenBy already merged. Walking outermost-inward we see the
                // last ThenBy first, so prepend to keep document order = sort precedence.
                pendingSortKeys.InsertAt(0, new BsonElement(key, direction));
                sawAny = true;
                current = call.Arguments[0];
            }
            else if (definition == QueryableMethods.Skip)
            {
                if (call.Arguments[1] is not ConstantExpression { Value: int skip })
                {
                    return false;
                }

                pendingSortKeys = null;
                ordered.Add(new BsonDocument("$skip", skip));
                sawAny = true;
                current = call.Arguments[0];
            }
            else if (definition == QueryableMethods.Take)
            {
                if (call.Arguments[1] is not ConstantExpression { Value: int take })
                {
                    return false;
                }

                pendingSortKeys = null;
                ordered.Add(new BsonDocument("$limit", take));
                sawAny = true;
                current = call.Arguments[0];
            }
            else if (definition == QueryableMethods.Where)
            {
                // Only the correlation Where (references the outer principal shaper) is allowed —
                // it is realized by the pipeline $lookup's $match $expr, so we drop it. A user
                // filter predicate would need predicate→$match translation we don't do here; bail.
                if (!IsCorrelationPredicate(call.Arguments[1]))
                {
                    return false;
                }

                pendingSortKeys = null;
                current = call.Arguments[0];
            }
            else
            {
                // Any other operator (Select projection, Distinct, etc.) is not translatable here.
                return false;
            }
        }

        // The innermost expression must be the dependent DbSet root; anything else (a deeper
        // sub-query the walk didn't recognize) means we can't safely translate.
        if (sawAny && IsDependentSetRoot(current, targetEntityType))
        {
            // ordered is execution-order already because each stage was Add-ed while walking from the
            // outermost operator inward — i.e. later (inner) operators were appended later. Reverse
            // to get innermost-first (the order the server must apply them).
            ordered.Reverse();
            stages = ordered;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the <c>$sort</c> field name for an OrderBy/ThenBy key selector, returning
    /// <see langword="false"/> when the key is not a simple mapped scalar property (e.g. a computed
    /// expression, a navigation, or a non-mapped member) — such keys can't be expressed as a plain
    /// element-name <c>$sort</c> and must fall back to fan-out.
    /// </summary>
    private static bool TryGetSortKey(MethodCallExpression orderingCall, IEntityType entityType, out string elementName)
    {
        elementName = string.Empty;
        if (Unquote(orderingCall.Arguments[1]) is not LambdaExpression lambda)
        {
            return false;
        }

        var body = lambda.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            body = convert.Operand;
        }

        // o => o.Member
        if (body is MemberExpression { Expression: ParameterExpression } member)
        {
            var property = entityType.FindProperty(member.Member.Name);
            if (property is not null && !property.IsShadowProperty())
            {
                elementName = property.GetElementName();
                return true;
            }

            return false;
        }

        // o => EF.Property<T>(o, "Member")
        if (body is MethodCallExpression { Arguments: [ParameterExpression, ConstantExpression { Value: string propName }] } efCall
            && efCall.Method.IsEFPropertyMethod())
        {
            var property = entityType.FindProperty(propName);
            if (property is not null)
            {
                elementName = property.GetElementName();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see langword="true"/> when a <c>Where</c> predicate is the include correlation condition
    /// (it references the outer principal via a <see cref="StructuralTypeShaperExpression"/>) rather
    /// than a user filter. The correlation is realized by the pipeline <c>$lookup</c>'s
    /// <c>$match { $expr }</c>, so such a predicate is dropped from the extracted stages.
    /// </summary>
    private static bool IsCorrelationPredicate(Expression predicate)
        => Unquote(predicate) is LambdaExpression lambda && ReferencesOuterShaper(lambda.Body);

    private static bool ReferencesOuterShaper(Expression expression)
    {
        var finder = new OuterShaperFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class OuterShaperFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            if (node is StructuralTypeShaperExpression)
            {
                Found = true;
            }

            return Found ? node : base.Visit(node);
        }
    }

    /// <summary>
    /// <see langword="true"/> when an operator's argument (a quoted lambda or a constant) carries a
    /// CORRELATION reference — to the outer principal (<see cref="StructuralTypeShaperExpression"/>) or
    /// to the <see cref="QueryContext"/> parameter EF threads through a navigation sub-query. Such an
    /// argument cannot be recomposed onto a stand-alone <c>DbContext.Set&lt;TRelated&gt;()</c> query and
    /// must not be treated as a user filter. This is a superset of <see cref="IsCorrelationPredicate"/>
    /// (which the Stage 6.1 pipeline path uses for shaper-only detection) and is used by the fan-out
    /// routing/composition so the FK correlation in either encoding is reliably excluded.
    /// </summary>
    private static bool ReferencesCorrelation(Expression operatorArgument)
    {
        var finder = new CorrelationFinder();
        finder.Visit(Unquote(operatorArgument));
        return finder.Found;
    }

    private sealed class CorrelationFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            if (node is StructuralTypeShaperExpression
                || (node is ParameterExpression p && typeof(QueryContext).IsAssignableFrom(p.Type)))
            {
                Found = true;
            }

            return Found ? node : base.Visit(node);
        }
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="expression"/> is the dependent <c>DbSet</c> root
    /// the filtered-include sub-query is built over (an <see cref="EntityQueryRootExpression"/> for
    /// the navigation's target type).
    /// </summary>
    private static bool IsDependentSetRoot(Expression expression, IEntityType targetEntityType)
        => expression is EntityQueryRootExpression root
           && (root.EntityType == targetEntityType || root.EntityType.ClrType == targetEntityType.ClrType);

    private static Expression Unquote(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: var inner } ? inner : e;

    /// <summary>
    /// Builds (and compiles) the filtered-include composition for a top-level collection Include that
    /// routes to the client-side fan-out loader: a delegate that, given the FK-correlated
    /// <c>IQueryable&lt;TRelated&gt;</c>, re-applies the include lambda's USER <c>Where</c> predicate(s),
    /// <c>OrderBy</c>/<c>ThenBy</c>, <c>Skip</c> and <c>Take</c> — in the SAME order the user wrote them
    /// (EF semantic order: Where → OrderBy/ThenBy → Skip → Take). Returns <see langword="null"/> when the
    /// include carries no such operators (a plain, unfiltered collection include).
    /// </summary>
    /// <remarks>
    /// Each operator is replayed by reusing the ORIGINAL sub-query <see cref="MethodCallExpression"/>'s
    /// <see cref="MethodCallExpression.Method"/> and argument lambdas verbatim, substituting only the
    /// source (<c>Arguments[0]</c>) with the running query. This avoids any predicate/key rewriting:
    /// the lambdas reference only the dependent parameter, so they recompose cleanly onto
    /// <c>DbContext.Set&lt;TRelated&gt;()</c>, where the driver's LINQ translates them. The innermost FK
    /// CORRELATION <c>Where</c> (references the outer principal shaper) is dropped — the loader already
    /// applies the FK match. A projecting <c>Select</c>, <c>Distinct</c>, or any other operator falls
    /// outside the recognized set and is left in place: the build returns the partial composition it
    /// could form and lets EF/driver translation surface any unsupported residue at execution.
    /// </remarks>
    public static Delegate? TryBuildFanOutComposition(Expression? navigationExpression, Type relatedClrType)
    {
        if (navigationExpression is not MaterializeCollectionNavigationExpression { Subquery: var subquery })
        {
            return null;
        }

        // Walk outermost → inner, recording each recognized operator. We re-apply them innermost-first
        // (reverse of the walk) so the running query rebuilds in the order the user authored them.
        var operators = new List<MethodCallExpression>();
        for (var current = subquery; current is MethodCallExpression { Method.IsGenericMethod: true } call;
             current = call.Arguments[0])
        {
            var definition = call.Method.GetGenericMethodDefinition();
            if (definition == QueryableMethods.Where)
            {
                // Keep only a self-contained USER predicate over IQueryable<TRelated> (see
                // IsUserWhereOnDependent). The FK correlation Where (references the outer principal /
                // QueryContext — realized by the loader's FK match) and any EF-internal Where over a
                // transparent-identifier element type (e.g. a global query filter spanning a navigation)
                // are skipped: the former is already applied, the latter is re-applied by EF when the
                // sub-query re-runs through DbContext.Set<TRelated>().
                if (IsUserWhereOnDependent(call, relatedClrType))
                {
                    operators.Add(call);
                }
            }
            else if (definition == QueryableMethods.OrderBy
                     || definition == QueryableMethods.OrderByDescending
                     || definition == QueryableMethods.ThenBy
                     || definition == QueryableMethods.ThenByDescending
                     || definition == QueryableMethods.Skip
                     || definition == QueryableMethods.Take)
            {
                // Only recompose ordering/paging that sits directly on IQueryable<TRelated> with a
                // self-contained key/count. Anything over a transparent-identifier element type, or a
                // correlation/context reference (rare), can't be recomposed onto a stand-alone DbSet
                // query — bail so the include materializes unfiltered rather than crashing.
                if (!IsOperatorOnDependent(call, relatedClrType) || ReferencesCorrelation(call.Arguments[1]))
                {
                    return null;
                }

                operators.Add(call);
            }

            // Any other operator (Select projection, Distinct, etc.) is simply not recorded — it is
            // neither a user filter nor ordering/paging we recompose; the unfiltered FK-matched query
            // is materialized in that case (the routing layer only sends recognizable shapes here).
        }

        if (operators.Count == 0)
        {
            return null;
        }

        operators.Reverse();

        var queryParam = Expression.Parameter(typeof(IQueryable<>).MakeGenericType(relatedClrType), "q");
        Expression running = queryParam;
        foreach (var op in operators)
        {
            // Reuse the original generic method and the original argument lambda(s)/constant(s); only
            // the source operand changes to the running query.
            var args = new Expression[op.Arguments.Count];
            args[0] = running;
            for (var i = 1; i < op.Arguments.Count; i++)
            {
                args[i] = op.Arguments[i];
            }

            running = Expression.Call(op.Method, args);
        }

        var funcType = typeof(Func<,>).MakeGenericType(queryParam.Type, queryParam.Type);
        return Expression.Lambda(funcType, running, queryParam).Compile();
    }

    /// <summary>
    /// Resolves the CLR <see cref="PropertyInfo"/> for an EF property,
    /// throwing a clear "not yet supported" error if the property is a
    /// shadow property (no CLR representation). Stage 1 only supports
    /// single-column CLR-backed keys; shadow / composite keys are
    /// out-of-scope for now.
    /// </summary>
    public static PropertyInfo GetClrPropertyOrThrow(IProperty property, INavigation navigation)
    {
        var clr = property.PropertyInfo;
        if (clr is null)
        {
            throw new NotSupportedException(
                $"Cross-collection Include of '{navigation.DeclaringEntityType.DisplayName()
                }.{navigation.Name}' requires a CLR-backed key/FK property; '{property.DeclaringType.DisplayName()
                }.{property.Name}' is a shadow property. Shadow-key Include is tracked as a "
                + "follow-up to EF-117.");
        }
        return clr;
    }

    /// <summary>
    /// Runtime helper invoked from the compiled shaper. Loads the related
    /// dependents of a single principal via a <c>$match</c> on the foreign
    /// key, materialized through the standard driver-LINQ pipeline so that
    /// MQL logging, transaction binding, and serializer registration all
    /// flow through the existing infrastructure.
    /// </summary>
    public static IEnumerable<TRelated> LoadCollection<TPrincipal, TRelated>(
        QueryContext queryContext,
        TPrincipal? principal,
        Func<TPrincipal, object?> principalKeyExtractor,
        string foreignKeyClrPropertyName,
        string? thenIncludeChainPath,
        QueryTrackingBehavior queryTrackingBehavior,
        Func<IQueryable<TRelated>, IQueryable<TRelated>>? filterComposition)
        where TPrincipal : class
        where TRelated : class
    {
        if (principal is null)
        {
            return [];
        }

        var pkValue = principalKeyExtractor(principal);
        if (pkValue is null)
        {
            return [];
        }

        // Run the sub-query through EF's standard query pipeline (via DbContext.Set<TRelated>)
        // rather than the raw driver. This way all EF mappings — element names, value
        // converters, discriminator, owned-type nesting — apply identically to the
        // include results and to a stand-alone DbSet query against the same type.
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var dbContext = mongoQueryContext.Context;
        IQueryable<TRelated> query = ApplyTrackingBehavior(dbContext.Set<TRelated>(), queryTrackingBehavior);

        // Apply any chained ThenInclude path so the loader recursively materializes
        // nested navigations through the same pipeline (and our preprocessor handles
        // a second round of join-unwrap or collection fan-out as needed).
        if (!string.IsNullOrEmpty(thenIncludeChainPath))
        {
            query = query.Include(thenIncludeChainPath);
        }

        // Build: r => EF.Property<object>(r, foreignKeyClrPropertyName).Equals(pkValue)
        var rParam = Expression.Parameter(typeof(TRelated), "r");
        var efPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!
            .MakeGenericMethod(pkValue.GetType());
        var fkAccess = Expression.Call(
            efPropertyMethod,
            rParam,
            Expression.Constant(foreignKeyClrPropertyName));
        var equality = Expression.Equal(fkAccess, Expression.Constant(pkValue, pkValue.GetType()));
        var predicate = Expression.Lambda<Func<TRelated, bool>>(equality, rParam);

        // FK correlation first (scopes the dependents to THIS principal), then the user's
        // filtered-include composition (Where → OrderBy/ThenBy → Skip → Take) so the included
        // collection is filtered/ordered/paged exactly as the include lambda specified. Composing
        // through DbContext.Set<TRelated> means the driver's LINQ translates the user predicate —
        // no provider-side BSON rendering is needed.
        var filtered = query.Where(predicate);
        if (filterComposition is not null)
        {
            filtered = filterComposition(filtered);
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Runtime helper invoked from the compiled shaper for reference (single-related)
    /// navigations. Loads the related principal of a dependent via a
    /// <c>FirstOrDefault</c> query keyed on the dependent's foreign key, materialized
    /// through the standard EF pipeline so that all mappings, tracking, and identity
    /// resolution apply.
    /// </summary>
    /// <remarks>
    /// EF Core's nav-expansion produces a synthetic <c>Queryable.Join</c> for the
    /// dependent → principal reference case. The provider's preprocessor
    /// (<c>IncludeJoinUnwrapper</c>) rewrites that into a plain
    /// <c>Select(p =&gt; IncludeExpression(p, default(TRelated), nav))</c>, after which
    /// the shaper-stage visitor emits the call to this helper.
    /// </remarks>
    public static TRelated? LoadReference<TPrincipal, TRelated>(
        QueryContext queryContext,
        TPrincipal? principal,
        Func<TPrincipal, object?> foreignKeyExtractor,
        string principalKeyClrPropertyName,
        string? thenIncludeChainPath,
        QueryTrackingBehavior queryTrackingBehavior)
        where TPrincipal : class
        where TRelated : class
    {
        if (principal is null)
        {
            return null;
        }

        var fkValue = foreignKeyExtractor(principal);
        if (fkValue is null)
        {
            return null;
        }

        var mongoQueryContext = (MongoQueryContext)queryContext;
        var dbContext = mongoQueryContext.Context;
        IQueryable<TRelated> query = ApplyTrackingBehavior(dbContext.Set<TRelated>(), queryTrackingBehavior);

        if (!string.IsNullOrEmpty(thenIncludeChainPath))
        {
            query = query.Include(thenIncludeChainPath);
        }

        // Build: r => EF.Property<TKey>(r, principalKeyClrPropertyName).Equals(fkValue)
        var rParam = Expression.Parameter(typeof(TRelated), "r");
        var efPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!
            .MakeGenericMethod(fkValue.GetType());
        var pkAccess = Expression.Call(
            efPropertyMethod,
            rParam,
            Expression.Constant(principalKeyClrPropertyName));
        var equality = Expression.Equal(pkAccess, Expression.Constant(fkValue, fkValue.GetType()));
        var predicate = Expression.Lambda<Func<TRelated, bool>>(equality, rParam);

        return query.Where(predicate).FirstOrDefault();
    }

    /// <summary>
    /// Applies the outer query's per-query tracking behavior to a cross-collection
    /// include sub-query. Without this, <c>AsNoTracking</c> on the principal does
    /// nothing for the dependents the loader materializes — the sub-query inherits
    /// the DbContext default (TrackAll) and the related entities end up tracked.
    /// </summary>
    private static IQueryable<T> ApplyTrackingBehavior<T>(IQueryable<T> query, QueryTrackingBehavior trackingBehavior)
        where T : class
        => trackingBehavior switch
        {
            QueryTrackingBehavior.NoTracking => query.AsNoTracking(),
            QueryTrackingBehavior.NoTrackingWithIdentityResolution => query.AsNoTrackingWithIdentityResolution(),
            _ => query, // TrackAll — leave default
        };

    /// <summary>
    /// Reflected handle for the <see cref="LoadCollection{TPrincipal, TRelated}"/>
    /// helper, used by the shaper-stage visitor when generating the loader call.
    /// </summary>
    public static readonly MethodInfo LoadCollectionMethodInfo
        = typeof(MongoIncludeCompiler).GetTypeInfo()
            .GetDeclaredMethod(nameof(LoadCollection))!;

    /// <summary>
    /// Reflected handle for the <see cref="LoadReference{TPrincipal, TRelated}"/>
    /// helper, used by the shaper-stage visitor when generating the loader call.
    /// </summary>
    public static readonly MethodInfo LoadReferenceMethodInfo
        = typeof(MongoIncludeCompiler).GetTypeInfo()
            .GetDeclaredMethod(nameof(LoadReference))!;
}
