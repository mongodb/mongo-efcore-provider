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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
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
        // A top-level principal→dependent collection Include routes to a
        // server-side $lookup. Anything with a nested (ThenInclude /
        // nested-collection) chain still fans out client-side.
        if (IsCrossCollection(navigation)
            && navigation.IsCollection
            && !navigation.IsOnDependent
            && !HasNestedInclude(includeExpression))
        {
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

    private static Expression Unquote(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: var inner } ? inner : e;

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
        QueryTrackingBehavior queryTrackingBehavior)
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

        return query.Where(predicate).ToList();
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
