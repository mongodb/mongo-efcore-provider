﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos CosmosShapedQueryCompilingExpressionVisitor.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

#nullable disable

internal abstract class ProjectionBindingRemovingExpressionVisitor : ExpressionVisitor
{
    protected readonly ParameterExpression DocParameter;
    private readonly bool _trackQueryResults;
    private readonly Dictionary<ParameterExpression, Expression> _materializationContextBindings = new();
    protected readonly Dictionary<Expression, ParameterExpression> ProjectionBindings = new();
    private readonly Dictionary<Expression, (IEntityType EntityType, Expression BsonDocExpression)> _ownerMappings = new();
    private readonly Dictionary<Expression, Expression> _ordinalParameterBindings = new();
    private List<IncludeExpression> _pendingIncludes = new();

    protected ProjectionBindingRemovingExpressionVisitor(
        ParameterExpression docParameter,
        bool trackQueryResults)
    {
        DocParameter = docParameter;
        _trackQueryResults = trackQueryResults;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case ProjectionBindingExpression projectionBindingExpression:
                {
                    ProjectionExpression projection = GetProjection(projectionBindingExpression);

                    return CreateGetValueExpression(
                        DocParameter,
                        projection.Alias,
                        !projectionBindingExpression.Type.IsNullableType(),
                        projectionBindingExpression.Type);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    ObjectArrayProjectionExpression objectArrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            ProjectionExpression projection = GetProjection(projectionBindingExpression);
                            objectArrayProjection = (ObjectArrayProjectionExpression)projection.Expression;
                            break;
                        case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                            objectArrayProjection = objectArrayProjectionExpression;
                            break;
                        default:
                            throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                    }

                    ParameterExpression bsonArray = ProjectionBindings[objectArrayProjection];
                    ParameterExpression jObjectParameter = Expression.Parameter(typeof(BsonDocument), bsonArray.Name + "Object");
                    ParameterExpression ordinalParameter = Expression.Parameter(typeof(int), bsonArray.Name + "Ordinal");

                    Expression accessExpression = objectArrayProjection.InnerProjection.ParentAccessExpression;
                    ProjectionBindings[accessExpression] = jObjectParameter;
                    _ownerMappings[accessExpression] =
                        (objectArrayProjection.Navigation.DeclaringEntityType, objectArrayProjection.AccessExpression);
                    _ordinalParameterBindings[accessExpression] = Expression.Add(
                        ordinalParameter, Expression.Constant(1, typeof(int)));

                    BlockExpression innerShaper = (BlockExpression)Visit(collectionShaperExpression.InnerShaper);

                    innerShaper = AddIncludes(innerShaper);

                    MethodCallExpression entities = Expression.Call(
                        EnumerableMethods.SelectWithOrdinal.MakeGenericMethod(typeof(BsonDocument), innerShaper.Type),
                        Expression.Call(
                            EnumerableMethods.Cast.MakeGenericMethod(typeof(BsonDocument)),
                            bsonArray),
                        Expression.Lambda(innerShaper, jObjectParameter, ordinalParameter));

                    INavigationBase navigation = collectionShaperExpression.Navigation;
                    return Expression.Call(
                        PopulateCollectionMethodInfo.MakeGenericMethod(navigation.TargetEntityType.ClrType, navigation.ClrType),
                        Expression.Constant(navigation.GetCollectionAccessor()),
                        entities);
                }

            case IncludeExpression includeExpression:
                {
                    if (!(includeExpression.Navigation is INavigation navigation)
                        || navigation.IsOnDependent
                        || navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot())
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{includeExpression.Navigation
                            }' is not supported as the navigation is not embedded in same resource.");
                    }

                    bool isFirstInclude = _pendingIncludes.Count == 0;
                    _pendingIncludes.Add(includeExpression);

                    BlockExpression bsonDocBlock = Visit(includeExpression.EntityExpression) as BlockExpression;

                    if (!isFirstInclude)
                    {
                        return bsonDocBlock;
                    }

                    ConditionalExpression bsonDocCondition = (ConditionalExpression)bsonDocBlock.Expressions[^1];

                    BlockExpression shaperBlock = (BlockExpression)bsonDocCondition.IfFalse;
                    shaperBlock = AddIncludes(shaperBlock);

                    List<Expression> jObjectExpressions = new(bsonDocBlock.Expressions);
                    jObjectExpressions.RemoveAt(jObjectExpressions.Count - 1);

                    jObjectExpressions.Add(
                        bsonDocCondition.Update(bsonDocCondition.Test, bsonDocCondition.IfTrue, shaperBlock));

                    return bsonDocBlock.Update(bsonDocBlock.Variables, jObjectExpressions);
                }
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Visits a <see cref="BinaryExpression"/> replacing empty ProjectionBindingExpressions
    /// while passing through visitation of all others.
    /// </summary>
    /// <param name="binaryExpression">The <see cref="BinaryExpression"/> to visit.</param>
    /// <returns>A <see cref="BinaryExpression"/> with any necessary adjustments.</returns>
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        if (binaryExpression.NodeType == ExpressionType.Assign && binaryExpression.Left is ParameterExpression parameterExpression)
        {
            if (parameterExpression.Type == typeof(BsonDocument) || parameterExpression.Type == typeof(BsonArray))
            {
                string fieldName = null;
                bool fieldRequired = true;

                Expression projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
                if (projectionExpression is ProjectionBindingExpression projectionBindingExpression)
                {
                    ProjectionExpression projection = GetProjection(projectionBindingExpression);
                    projectionExpression = projection.Expression;
                    fieldName = projection.Alias;
                }
                else if (projectionExpression is UnaryExpression convertExpression &&
                         convertExpression.NodeType == ExpressionType.Convert)
                {
                    projectionExpression = ((UnaryExpression)convertExpression.Operand).Operand;
                }

                Expression innerAccessExpression;
                if (projectionExpression is ObjectArrayProjectionExpression objectArrayProjectionExpression)
                {
                    innerAccessExpression = objectArrayProjectionExpression.AccessExpression;
                    ProjectionBindings[objectArrayProjectionExpression] = parameterExpression;
                    fieldName ??= objectArrayProjectionExpression.Name;
                }
                else
                {
                    EntityProjectionExpression entityProjectionExpression = (EntityProjectionExpression)projectionExpression;
                    Expression accessExpression = entityProjectionExpression.ParentAccessExpression;
                    ProjectionBindings[accessExpression] = parameterExpression;
                    fieldName ??= entityProjectionExpression.Name;

                    switch (accessExpression)
                    {
                        case ObjectAccessExpression innerObjectAccessExpression:
                            innerAccessExpression = innerObjectAccessExpression.AccessExpression;
                            _ownerMappings[accessExpression] =
                                (innerObjectAccessExpression.Navigation.DeclaringEntityType, innerAccessExpression);
                            fieldRequired = innerObjectAccessExpression.Required;
                            break;
                        case RootReferenceExpression:
                            innerAccessExpression = DocParameter;
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown access expression type {accessExpression.Type.ShortDisplayName()}.");
                    }
                }

                Expression valueExpression =
                    CreateGetValueExpression(innerAccessExpression, fieldName, fieldRequired, parameterExpression.Type);

                return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, valueExpression);
            }

            if (parameterExpression.Type == typeof(MaterializationContext))
            {
                NewExpression newExpression = (NewExpression)binaryExpression.Right;

                EntityProjectionExpression entityProjectionExpression;
                if (newExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
                {
                    ProjectionExpression projection = GetProjection(projectionBindingExpression);
                    entityProjectionExpression = (EntityProjectionExpression)projection.Expression;
                }
                else
                {
                    Expression projection = ((UnaryExpression)((UnaryExpression)newExpression.Arguments[0]).Operand).Operand;
                    entityProjectionExpression = (EntityProjectionExpression)projection;
                }

                _materializationContextBindings[parameterExpression] = entityProjectionExpression.ParentAccessExpression;

                NewExpression updatedExpression = Expression.New(
                    newExpression.Constructor,
                    Expression.Constant(ValueBuffer.Empty),
                    newExpression.Arguments[1]);

                return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
            }
        }

        return base.VisitBinary(binaryExpression);
    }

    /// <summary>
    /// Visits a <see cref="MethodCallExpression"/> replacing calls to <see cref="ValueBuffer"/>
    /// with replacement alternatives from <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="Expression"/> to replace the original method call with.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        MethodInfo method = methodCallExpression.Method;
        MethodInfo genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;

        // Ensure ".AsQueryable" is not injected around collections as sometimes they are null and will throw
        if (method.DeclaringType == typeof(Queryable) && genericMethod == QueryableMethods.AsQueryable)
        {
            return Visit(methodCallExpression.Arguments[0]);
        }

        if (genericMethod == ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            IProperty property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
            Expression innerExpression;
            if (methodCallExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
            {
                ProjectionExpression projection = GetProjection(projectionBindingExpression);
                innerExpression =
                    CreateGetValueExpression(DocParameter, projection.Alias, projection.Required, typeof(BsonDocument));
            }
            else
            {
                innerExpression =
                    _materializationContextBindings[
                        (ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object];
            }

            return CreateGetValueExpression(innerExpression, property, methodCallExpression.Type);
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    private Expression CreateGetValueExpression(
        Expression docExpression,
        IProperty property,
        Type type)
    {
        if (property.IsOwnedTypeKey())
        {
            IEntityType entityType = property.DeclaringEntityType;
            if (!entityType.IsDocumentRoot())
            {
                IForeignKey ownership = entityType.FindOwnership();
                if (ownership?.IsUnique == false && property.IsOwnedCollectionShadowKey())
                {
                    Expression readExpression = _ordinalParameterBindings[docExpression];
                    if (readExpression.Type != type)
                    {
                        readExpression = Expression.Convert(readExpression, type);
                    }

                    return readExpression;
                }

                IProperty principalProperty = property.FindFirstPrincipal();
                if (principalProperty != null)
                {
                    Expression ownerBsonDocExpression = null;
                    if (_ownerMappings.TryGetValue(docExpression,
                            out (IEntityType EntityType, Expression BsonDocExpression) ownerInfo))
                    {
                        ownerBsonDocExpression = ownerInfo.BsonDocExpression;
                    }
                    else if (docExpression is RootReferenceExpression rootReferenceExpression)
                    {
                        ownerBsonDocExpression = rootReferenceExpression;
                    }
                    else if (docExpression is ObjectAccessExpression objectAccessExpression)
                    {
                        ownerBsonDocExpression = objectAccessExpression.AccessExpression;
                    }

                    if (ownerBsonDocExpression != null)
                    {
                        return CreateGetValueExpression(ownerBsonDocExpression, principalProperty, type);
                    }
                }
            }

            return Expression.Default(type);
        }

        return Expression.Convert(
            CreateGetValueExpression(docExpression, property.Name, !type.IsNullableType(), type, property.GetTypeMapping()),
            type);
    }

    /// <summary>
    /// Obtain the registered <see cref="ProjectionExpression"/> for a given <see cref="ProjectionBindingExpression"/>.
    /// </summary>
    /// <param name="projectionBindingExpression">The <see cref="ProjectionBindingExpression"/> to look-up.</param>
    /// <returns>The registered <see cref="ProjectionExpression"/> this <paramref name="projectionBindingExpression"/> relates to.</returns>
    protected abstract ProjectionExpression GetProjection(ProjectionBindingExpression projectionBindingExpression);

    /// <summary>
    /// Create a new compilable <see cref="Expression"/> the shaper can use to obtain the value from the <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="docExpression">The <see cref="Expression"/> used to access the <see cref="BsonDocument"/>.</param>
    /// <param name="fieldName">The name of the field within the document.</param>
    /// <param name="fieldRequired"><see langref="true"/> if the field is required, <see langref="false"/> if it is optional.</param>
    /// <param name="type">The <see cref="Type"/> of the value as it is within the document.</param>
    /// <param name="typeMapping">Any associated <see cref="CoreTypeMapping"/> to be used in mapping the value.</param>
    /// <returns>A compilable <see cref="Expression"/> to obtain the desired value as the correct type.</returns>
    protected abstract Expression CreateGetValueExpression(
        Expression docExpression,
        string fieldName,
        bool fieldRequired,
        Type type,
        CoreTypeMapping typeMapping = null);

    private BlockExpression AddIncludes(BlockExpression shaperBlock)
    {
        if (_pendingIncludes.Count == 0)
        {
            return shaperBlock;
        }

        List<Expression> shaperExpressions = new(shaperBlock.Expressions);
        Expression instanceVariable = shaperExpressions[^1];
        shaperExpressions.RemoveAt(shaperExpressions.Count - 1);

        List<IncludeExpression> includesToProcess = _pendingIncludes;
        _pendingIncludes = new List<IncludeExpression>();

        foreach (IncludeExpression include in includesToProcess)
        {
            AddInclude(shaperExpressions, include, shaperBlock, instanceVariable);
        }

        shaperExpressions.Add(instanceVariable);
        shaperBlock = shaperBlock.Update(shaperBlock.Variables, shaperExpressions);
        return shaperBlock;
    }

    private void AddInclude(
        List<Expression> shaperExpressions,
        IncludeExpression includeExpression,
        BlockExpression shaperBlock,
        Expression instanceVariable)
    {
        INavigation navigation = (INavigation)includeExpression.Navigation;
        MethodInfo includeMethod = navigation.IsCollection ? IncludeCollectionMethodInfo : IncludeReferenceMethodInfo;
        Type includingClrType = navigation.DeclaringEntityType.ClrType;
        Type relatedEntityClrType = navigation.TargetEntityType.ClrType;
#pragma warning disable EF1001 // Internal EF Core API usage.
        Expression entityEntryVariable = _trackQueryResults
            ? shaperBlock.Variables.Single(v => v.Type == typeof(InternalEntityEntry))
            : Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        ParameterExpression concreteEntityTypeVariable = shaperBlock.Variables.Single(v => v.Type == typeof(IEntityType));
        INavigation inverseNavigation = navigation.Inverse;
        Delegate fixup = GenerateFixup(
            includingClrType, relatedEntityClrType, navigation, inverseNavigation);

        Expression navigationExpression = Visit(includeExpression.NavigationExpression);

        shaperExpressions.Add(
            Expression.IfThen(
                Expression.Call(
                    Expression.Constant(navigation.DeclaringEntityType, typeof(IReadOnlyEntityType)),
                    IsAssignableFromMethodInfo,
                    Expression.Convert(concreteEntityTypeVariable, typeof(IReadOnlyEntityType))),
                Expression.Call(
                    includeMethod.MakeGenericMethod(includingClrType, relatedEntityClrType),
                    entityEntryVariable,
                    instanceVariable,
                    concreteEntityTypeVariable,
                    navigationExpression,
                    Expression.Constant(navigation),
                    Expression.Constant(inverseNavigation, typeof(INavigation)),
                    Expression.Constant(fixup),
#pragma warning disable EF1001 // Internal EF Core API usage.
                    Expression.Constant(includeExpression.SetLoaded))));
#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    private static readonly MethodInfo IncludeReferenceMethodInfo
        = typeof(ProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeReference));

    private static void IncludeReference<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object entity,
        IEntityType entityType,
        TIncludedEntity relatedEntity,
        INavigation navigation,
        INavigation inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool __)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            TIncludingEntity includingEntity = (TIncludingEntity)entity;
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
        // For non-null relatedEntity StateManager will set the flag
        else if (relatedEntity == null)
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(ProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeCollection));

    private static void IncludeCollection<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object entity,
        IEntityType entityType,
        IEnumerable<TIncludedEntity> relatedEntities,
        INavigation navigation,
        INavigation inverseNavigation,
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
            TIncludingEntity includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);

            if (relatedEntities != null)
            {
                foreach (TIncludedEntity relatedEntity in relatedEntities)
                {
                    fixup(includingEntity, relatedEntity);
                    inverseNavigation?.SetIsLoadedWhenNoTracking(relatedEntity);
                }
            }
        }
        else
        {
            if (setLoaded)
            {
#pragma warning disable EF1001 // Internal EF Core API usage.
                entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
            }

            if (relatedEntities != null)
            {
                using IEnumerator<TIncludedEntity> enumerator = relatedEntities.GetEnumerator();
                while (enumerator.MoveNext())
                {
                }

                // Ensure empty collections still initialize a new CLR object for them
                if (!navigation.IsShadowProperty())
                {
                    navigation.GetCollectionAccessor()!.GetOrCreate(entity, forMaterialization: true);
                }
            }
        }
    }

    private static Delegate GenerateFixup(
        Type entityType,
        Type relatedEntityType,
        INavigation navigation,
        INavigation inverseNavigation)
    {
        ParameterExpression entityParameter = Expression.Parameter(entityType);
        ParameterExpression relatedEntityParameter = Expression.Parameter(relatedEntityType);
        List<Expression> expressions = new()
        {
            navigation.IsCollection
                ? AddToCollectionNavigation(entityParameter, relatedEntityParameter, navigation)
                : AssignReferenceNavigation(entityParameter, relatedEntityParameter, navigation)
        };

        if (inverseNavigation != null)
        {
            expressions.Add(
                inverseNavigation.IsCollection
                    ? AddToCollectionNavigation(relatedEntityParameter, entityParameter, inverseNavigation)
                    : AssignReferenceNavigation(relatedEntityParameter, entityParameter, inverseNavigation));
        }

        return Expression.Lambda(Expression.Block(typeof(void), expressions), entityParameter, relatedEntityParameter)
            .Compile();
    }

    private static Expression AssignReferenceNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => entity.MakeMemberAccess(navigation.GetMemberInfo(true, true)).Assign(relatedEntity);

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

    private static readonly MethodInfo PopulateCollectionMethodInfo
        = typeof(ProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(PopulateCollection));

    private static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(nameof(IReadOnlyEntityType.IsAssignableFrom), new[]
        {
            typeof(IReadOnlyEntityType)
        })!;

    private static TCollection PopulateCollection<TEntity, TCollection>(
        IClrCollectionAccessor accessor,
        IEnumerable<TEntity> entities)
    {
        // TODO: throw a better exception for non-ICollection navigations
        ICollection<TEntity> collection = (ICollection<TEntity>)accessor.Create();
        foreach (TEntity entity in entities)
        {
            collection.Add(entity);
        }

        return (TCollection)collection;
    }

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IClrCollectionAccessor.Add));
}
