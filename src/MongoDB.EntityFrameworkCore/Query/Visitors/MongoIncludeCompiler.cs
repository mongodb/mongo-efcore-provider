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
        string foreignKeyClrPropertyName)
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
        var dbSet = dbContext.Set<TRelated>();

        // Build: r => EF.Property<object>(r, foreignKeyClrPropertyName).Equals(pkValue)
        // EF.Property handles both CLR-backed and shadow FK properties via standard
        // translation.
        var rParam = Expression.Parameter(typeof(TRelated), "r");
        var efPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!
            .MakeGenericMethod(pkValue.GetType());
        var fkAccess = Expression.Call(
            efPropertyMethod,
            rParam,
            Expression.Constant(foreignKeyClrPropertyName));
        var equality = Expression.Equal(fkAccess, Expression.Constant(pkValue, pkValue.GetType()));
        var predicate = Expression.Lambda<Func<TRelated, bool>>(equality, rParam);

        return dbSet.Where(predicate).ToList();
    }

    /// <summary>
    /// Reflected handle for the <see cref="LoadCollection{TPrincipal, TRelated}"/>
    /// helper, used by the shaper-stage visitor when generating the loader call.
    /// </summary>
    public static readonly MethodInfo LoadCollectionMethodInfo
        = typeof(MongoIncludeCompiler).GetTypeInfo()
            .GetDeclaredMethod(nameof(LoadCollection))!;
}
