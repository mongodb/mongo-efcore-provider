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
/// Ported from EF Core's relational binding-remover fix-up methods. Kept in one place because both read paths
/// must produce the same object graph for a given query.
/// </remarks>
internal static class MongoIncludeFixups
{
    private static readonly MethodInfo IncludeReferenceMethodInfo
        = typeof(MongoIncludeFixups).GetTypeInfo().GetDeclaredMethod(nameof(IncludeReference))!;

    private static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(MongoIncludeFixups).GetTypeInfo().GetDeclaredMethod(nameof(IncludeCollection))!;

    private static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(
            nameof(IReadOnlyEntityType.IsAssignableFrom), [typeof(IReadOnlyEntityType)])!;

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo().GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

    /// <summary>
    /// Reference-include fix-up: wires the materialized related entity onto the principal via
    /// <paramref name="fixup"/> and sets the navigation's loaded flag.
    /// </summary>
    /// <remarks>
    /// The trailing <c>bool</c> (<c>SetLoaded</c>) is unused here but keeps the signature identical to
    /// <c>IncludeCollection</c> so <see cref="CreateIncludeCall"/> builds both the same way.
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
    /// navigation, and guarantees an empty collection is still initialized to a real CLR object.
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
                // When tracking, the state manager does the fix-up as each entity materializes; just enumerate.
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
    /// Builds the fix-up call for an <c>Include</c> of <paramref name="navigation"/>:
    /// <c>if (navigation.DeclaringEntityType.IsAssignableFrom(concreteEntityType)) IncludeReference|IncludeCollection(...)</c>,
    /// choosing <c>IncludeCollection</c> for a collection navigation.
    /// </summary>
    /// <param name="navigation">The included navigation.</param>
    /// <param name="entityEntry">
    /// The including entity's <see cref="InternalEntityEntry"/>, or a <see langword="null"/> constant of that type
    /// when not tracking.
    /// </param>
    /// <param name="instance">The materialized including entity.</param>
    /// <param name="concreteEntityType">The including entity's concrete <see cref="IEntityType"/>.</param>
    /// <param name="relatedEntity">
    /// The materialized related entity (reference) or <c>IEnumerable&lt;TIncluded&gt;</c> of them (collection).
    /// </param>
    /// <param name="setLoaded">The <c>IncludeExpression.SetLoaded</c> flag.</param>
    public static Expression CreateIncludeCall(
        INavigation navigation,
        Expression entityEntry,
        Expression instance,
        Expression concreteEntityType,
        Expression relatedEntity,
        bool setLoaded)
    {
        var includingClrType = navigation.DeclaringEntityType.ClrType;
        var relatedEntityClrType = navigation.TargetEntityType.ClrType;
        var inverseNavigation = navigation.Inverse;
        var fixup = GenerateFixup(includingClrType, relatedEntityClrType, navigation, inverseNavigation);
        var includeMethod = navigation.IsCollection ? IncludeCollectionMethodInfo : IncludeReferenceMethodInfo;

        return Expression.IfThen(
            Expression.Call(
                Expression.Constant(navigation.DeclaringEntityType, typeof(IReadOnlyEntityType)),
                IsAssignableFromMethodInfo,
                Expression.Convert(concreteEntityType, typeof(IReadOnlyEntityType))),
            Expression.Call(
                includeMethod.MakeGenericMethod(includingClrType, relatedEntityClrType),
                entityEntry,
                instance,
                concreteEntityType,
                relatedEntity,
                Expression.Constant(navigation),
                Expression.Constant(inverseNavigation, typeof(INavigation)),
                Expression.Constant(fixup),
                Expression.Constant(setLoaded)));
    }

    /// <summary>
    /// Compiles the fix-up delegate that wires <c>relatedEntity</c> onto <c>entity</c> through
    /// <paramref name="navigation"/> and, when present, <paramref name="inverseNavigation"/>.
    /// </summary>
    private static Delegate GenerateFixup(
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
