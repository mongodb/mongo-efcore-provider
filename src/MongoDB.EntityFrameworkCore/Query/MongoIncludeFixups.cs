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
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure; // MakeMemberAccess()/Assign() expression extensions
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Navigation fix-up for <c>Include</c>, shared by both read paths: the DOM shaper
/// (<c>MongoProjectionBindingRemovingExpressionVisitor</c>) and the one-pass streaming materializer
/// (<c>MongoStreamingEntityMaterializerRewriter</c>).
/// </summary>
/// <remarks>
/// <para>
/// These are ports of EF Core's own relational binding-remover fix-up methods, so the duplication against
/// upstream is unavoidable — but the two Mongo read paths held a SECOND copy of each other, byte-identical
/// apart from nullability annotations and comment punctuation (~110 lines). Both legs must stay
/// observationally equivalent (a query returns the same object graph whichever shaper compiled it), and
/// hand-maintaining two copies of the code that wires that graph together is the most direct way to lose that.
/// </para>
/// <para>
/// <see cref="GenerateFixup"/> is the general form: it branches on <c>IsCollection</c> for
/// both the navigation and its inverse, and so subsumes the streaming path's former separate
/// reference-only and collection-only generators (each of which was this method with one branch deleted).
/// </para>
/// </remarks>
internal static class MongoIncludeFixups
{
    public static readonly MethodInfo IncludeReferenceMethodInfo
        = typeof(MongoIncludeFixups).GetTypeInfo().GetDeclaredMethod(nameof(IncludeReference))!;

    public static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(MongoIncludeFixups).GetTypeInfo().GetDeclaredMethod(nameof(IncludeCollection))!;

    public static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(
            nameof(IReadOnlyEntityType.IsAssignableFrom), [typeof(IReadOnlyEntityType)])!;

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo().GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

    /// <summary>
    /// Reference-include fix-up: wires the materialized related entity onto the principal via
    /// <paramref name="fixup"/> and sets the navigation's loaded flag.
    /// </summary>
    /// <remarks>
    /// The trailing <c>bool</c> is the <c>SetLoaded</c> flag EF passes; it is unused on the reference path
    /// (for a non-null related entity the state manager sets the flag itself) but is part of the signature both
    /// call sites build their <see cref="Expression.Call(MethodInfo, Expression[])"/> against.
    /// </remarks>
    private static void IncludeReference<TIncludingEntity, TIncludedEntity>(
        InternalEntityEntry? entry,
        object? entity,
        IEntityType entityType,
        TIncludedEntity relatedEntity,
        INavigation navigation,
        INavigation? inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool _)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);
            if (relatedEntity != null)
            {
                fixup(includingEntity, relatedEntity);
                if (inverseNavigation != null
                    && !inverseNavigation.IsCollection)
                {
                    inverseNavigation.SetIsLoadedWhenNoTracking(relatedEntity);
                }
            }
        }
        // For a non-null relatedEntity the StateManager sets the flag.
        else if (relatedEntity == null)
        {
            entry.SetIsLoaded(navigation);
        }
    }

    /// <summary>
    /// Collection-include fix-up: adds each materialized related entity to the principal's collection
    /// navigation, and guarantees an EMPTY collection is still initialized to a real CLR object.
    /// </summary>
    private static void IncludeCollection<TIncludingEntity, TIncludedEntity>(
        InternalEntityEntry? entry,
        object? entity,
        IEntityType entityType,
        IEnumerable<TIncludedEntity>? relatedEntities,
        INavigation navigation,
        INavigation? inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool setLoaded)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);

            if (relatedEntities != null)
            {
                foreach (var relatedEntity in relatedEntities)
                {
                    fixup(includingEntity, relatedEntity);
                    inverseNavigation?.SetIsLoadedWhenNoTracking(relatedEntity!);
                }
            }
        }
        else
        {
            if (setLoaded)
            {
                entry.SetIsLoaded(navigation);
            }

            if (relatedEntities != null)
            {
                // Drain the sequence: when tracking, the state manager performs the fix-up as each related
                // entity is materialized, so enumerating is the work — there is nothing to do per element.
                using var enumerator = relatedEntities.GetEnumerator();
                while (enumerator.MoveNext())
                {
                }
            }
        }

        // Ensure empty collections still initialize a new CLR object for them.
        if (relatedEntities != null && !navigation.IsShadowProperty())
        {
            navigation.GetCollectionAccessor()!.GetOrCreate(entity, forMaterialization: true);
        }
    }

    /// <summary>
    /// Compiles the two-argument fix-up delegate that assigns <c>relatedEntity</c> onto <c>entity</c> through
    /// <paramref name="navigation"/> (and, when present, back through <paramref name="inverseNavigation"/>),
    /// choosing member assignment or collection-add per navigation cardinality.
    /// </summary>
    public static Delegate GenerateFixup(
        Type entityType,
        Type relatedEntityType,
        INavigation navigation,
        INavigation? inverseNavigation)
    {
        var entityParameter = Expression.Parameter(entityType);
        var relatedEntityParameter = Expression.Parameter(relatedEntityType);

        List<Expression> expressions =
        [
            navigation.IsCollection
                ? AddToCollectionNavigation(entityParameter, relatedEntityParameter, navigation)
                : AssignReferenceNavigation(entityParameter, relatedEntityParameter, navigation)
        ];

        if (inverseNavigation != null)
        {
            expressions.Add(
                inverseNavigation.IsCollection
                    ? AddToCollectionNavigation(relatedEntityParameter, entityParameter, inverseNavigation)
                    : AssignReferenceNavigation(relatedEntityParameter, entityParameter, inverseNavigation));
        }

        return Expression
            .Lambda(Expression.Block(typeof(void), expressions), entityParameter, relatedEntityParameter)
            .Compile();
    }

    private static Expression AssignReferenceNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => entity.MakeMemberAccess(navigation.GetMemberInfo(forMaterialization: true, forSet: true))
            .Assign(relatedEntity);

    private static Expression AddToCollectionNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => Expression.Call(
            Expression.Constant(navigation.GetCollectionAccessor()),
            CollectionAccessorAddMethodInfo,
            entity,
            relatedEntity,
            Expression.Constant(true));
}
