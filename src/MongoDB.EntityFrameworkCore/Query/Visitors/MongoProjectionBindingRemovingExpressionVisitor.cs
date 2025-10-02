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
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class MongoProjectionBindingRemovingExpressionVisitor : ExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly ParameterExpression _docParameter;
    private readonly bool _trackQueryResults;
    private readonly Dictionary<ParameterExpression, Expression> _materializationContextBindings = new();
    private readonly Dictionary<Expression, ParameterExpression> _projectionBindings = new();
    private readonly Dictionary<Expression, (IEntityType EntityType, Expression BsonDocExpression)> _ownerMappings = new();
    private readonly Dictionary<Expression, Expression> _ordinalParameterBindings = new();
    private List<IncludeExpression> _pendingIncludes = [];

    /// <summary>
    /// Create a <see cref="MongoProjectionBindingRemovingExpressionVisitor"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> this visitor should use.</param>
    /// <param name="docParameter">The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.</param>
    /// <param name="trackQueryResults">
    /// <see langword="true"/> if the results from this query are being tracked for changes,
    /// <see langword="false"/> if they are not.
    /// </param>
    public MongoProjectionBindingRemovingExpressionVisitor(
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        bool trackQueryResults)
    {
        _queryExpression = queryExpression;
        _docParameter = docParameter;
        _trackQueryResults = trackQueryResults;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case MongoProjectionBindingExpression projectionBindingExpression:
                {
                    var projection = GetProjection(projectionBindingExpression);

                    var memberExpression = (MemberExpression)projection.Expression;
                    while (memberExpression.Expression is MemberExpression nestedMemberExpression)
                    {
                        memberExpression = nestedMemberExpression;
                    }

                    var typeBase = ((StructuralTypeShaperExpression)memberExpression.Expression!).StructuralType;
                    var propertyBase = typeBase.FindMember(memberExpression.Member.Name);
                    var serializationInfo = BsonSerializerFactory.GetTypeSerializationInfo(memberExpression.Type);

                    return projectionBindingExpression.ProjectionMember == null
                        ? BsonBinding.CreateGetNextValueByOrdinal(
                            _docParameter, projectionBindingExpression.Index!.Value, projectionBindingExpression.ProjectionType, serializationInfo)
                        : CreateGetValueExpression(
                            _docParameter, projection.Alias, propertyBase);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    ObjectArrayProjectionExpression objectArrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case MongoProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            objectArrayProjection = (ObjectArrayProjectionExpression)projection.Expression;
                            break;
                        case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                            objectArrayProjection = objectArrayProjectionExpression;
                            break;
                        default:
                            throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                    }

                    var bsonArray = _projectionBindings[objectArrayProjection];
                    var jObjectParameter = Expression.Parameter(typeof(BsonDocument), bsonArray.Name + "Object");
                    var ordinalParameter = Expression.Parameter(typeof(int), bsonArray.Name + "Ordinal");

                    var accessExpression = objectArrayProjection.InnerProjection.ParentAccessExpression;
                    _projectionBindings[accessExpression] = jObjectParameter;
                    _ownerMappings[accessExpression] =
                        (objectArrayProjection.Navigation.DeclaringEntityType, objectArrayProjection.AccessExpression);
                    _ordinalParameterBindings[accessExpression] = Expression.Add(
                        ordinalParameter, Expression.Constant(1, typeof(int)));

                    var innerShaper = (BlockExpression)Visit(collectionShaperExpression.InnerShaper);

                    innerShaper = AddIncludes(innerShaper);

                    var entities = Expression.Call(
                        EnumerableMethods.SelectWithOrdinal.MakeGenericMethod(typeof(BsonDocument), innerShaper.Type),
                        Expression.Call(
                            EnumerableMethods.Cast.MakeGenericMethod(typeof(BsonDocument)),
                            bsonArray),
                        Expression.Lambda(innerShaper, jObjectParameter, ordinalParameter));

                    var navigation = collectionShaperExpression.Navigation!;
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

                    var isFirstInclude = _pendingIncludes.Count == 0;
                    _pendingIncludes.Add(includeExpression);

                    var bsonDocBlock = (Visit(includeExpression.EntityExpression) as BlockExpression)!;

                    if (!isFirstInclude)
                    {
                        return bsonDocBlock;
                    }

                    var bsonDocCondition = (ConditionalExpression)bsonDocBlock.Expressions[^1];

                    var shaperBlock = (BlockExpression)bsonDocCondition.IfFalse;
                    shaperBlock = AddIncludes(shaperBlock);

                    List<Expression> jObjectExpressions = [..bsonDocBlock.Expressions];
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
        if (binaryExpression.NodeType == ExpressionType.Assign)
        {
            if (binaryExpression.Left is ParameterExpression parameterExpression)
            {
                if (parameterExpression.Type == typeof(BsonDocument) || parameterExpression.Type == typeof(BsonArray))
                {
                    // "alias" will be different from the property/navigation name when mapped to a different name in the document.
                    string? alias = null;
                    IPropertyBase? propertyBase = null;

                    var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
                    if (projectionExpression is MongoProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        projectionExpression = projection.Expression;
                        alias = projection.Alias;
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
                        _projectionBindings[objectArrayProjectionExpression] = parameterExpression;
                        alias ??= objectArrayProjectionExpression.Name;
                        propertyBase = objectArrayProjectionExpression.Navigation;
                    }
                    else
                    {
                        var entityProjectionExpression = (EntityProjectionExpression)projectionExpression;
                        var accessExpression = entityProjectionExpression.ParentAccessExpression;
                        _projectionBindings[accessExpression] = parameterExpression;
                        alias ??= entityProjectionExpression.Name;

                        switch (accessExpression)
                        {
                            case ObjectAccessExpression innerObjectAccessExpression:
                                innerAccessExpression = innerObjectAccessExpression.AccessExpression;
                                _ownerMappings[accessExpression] =
                                    (innerObjectAccessExpression.Navigation.DeclaringEntityType, innerAccessExpression);
                                propertyBase = innerObjectAccessExpression.Navigation;
                                break;
                            case RootReferenceExpression:
                                innerAccessExpression = _docParameter;
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Unknown access expression type {accessExpression.Type.ShortDisplayName()}.");
                        }
                    }

                    var valueExpression = CreateGetValueExpression(innerAccessExpression, alias, propertyBase);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, valueExpression);
                }

                if (parameterExpression.Type == typeof(MaterializationContext))
                {
                    var newExpression = (NewExpression)binaryExpression.Right;

                    EntityProjectionExpression entityProjectionExpression;
                    if (newExpression.Arguments[0] is MongoProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        entityProjectionExpression = (EntityProjectionExpression)projection.Expression;
                    }
                    else
                    {
                        var projection = ((UnaryExpression)((UnaryExpression)newExpression.Arguments[0]).Operand).Operand;
                        entityProjectionExpression = (EntityProjectionExpression)projection;
                    }

                    _materializationContextBindings[parameterExpression] = entityProjectionExpression.ParentAccessExpression;

                    var updatedExpression = Expression.New(
                        newExpression.Constructor!,
                        Expression.Constant(ValueBuffer.Empty),
                        newExpression.Arguments[1]);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
                }
            }

            if (binaryExpression.Left is MemberExpression { Member: FieldInfo { IsInitOnly: true } } memberExpression)
            {
                return memberExpression.Assign(Visit(binaryExpression.Right));
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
        var method = methodCallExpression.Method;
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;

        if (genericMethod == ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
            Expression innerExpression;
            if (methodCallExpression.Arguments[0] is MongoProjectionBindingExpression projectionBindingExpression)
            {
                var projection = GetProjection(projectionBindingExpression);
                innerExpression = CreateGetValueExpression(_docParameter, projection.Alias, property);
            }
            else
            {
                innerExpression =
                    _materializationContextBindings[
                        (ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object!];
            }

            return CreateGetValueExpression(innerExpression, property, methodCallExpression.Type);
        }

        if (method.DeclaringType == typeof(Enumerable)
            && method.Name == nameof(Enumerable.Select)
            && genericMethod == EnumerableMethods.Select)
        {
            var lambda = (LambdaExpression)methodCallExpression.Arguments[1];
            if (lambda.Body is IncludeExpression includeExpression)
            {
                if (!(includeExpression.Navigation is INavigation navigation)
                    || navigation.IsOnDependent
                    || navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot())
                {
                    throw new InvalidOperationException($"Including navigation '{nameof(navigation)
                    }' is not supported as the navigation is not embedded in same resource.");
                }

                _pendingIncludes.Add(includeExpression);

                Visit(includeExpression.EntityExpression);

                // Includes on collections are processed when visiting CollectionShaperExpression
                return Visit(methodCallExpression.Arguments[0]);
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    private Expression CreateGetValueExpression(
        Expression documentExpression,
        IProperty property,
        Type type)
    {
        if (property.IsOwnedTypeKey())
        {
            var entityType = (IReadOnlyEntityType)property.DeclaringType;
            if (!entityType.IsDocumentRoot())
            {
                var ownership = entityType.FindOwnership();
                if (ownership?.IsUnique == false && property.IsOwnedTypeOrdinalKey())
                {
                    var readExpression = _ordinalParameterBindings[documentExpression];
                    if (readExpression.Type != type)
                    {
                        readExpression = Expression.Convert(readExpression, type);
                    }

                    return readExpression;
                }

                var principalProperty = property.FindFirstPrincipal();
                if (principalProperty != null)
                {
                    Expression? ownerBsonDocExpression = null;
                    if (_ownerMappings.TryGetValue(documentExpression,
                            out var ownerInfo))
                    {
                        ownerBsonDocExpression = ownerInfo.BsonDocExpression;
                    }
                    else if (documentExpression is RootReferenceExpression rootReferenceExpression)
                    {
                        ownerBsonDocExpression = rootReferenceExpression;
                    }
                    else if (documentExpression is ObjectAccessExpression objectAccessExpression)
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
            CreateGetValueExpression(documentExpression, null, property),
            type);
    }

    /// <summary>
    /// Obtain the registered <see cref="ProjectionExpression"/> for a given <see cref="ProjectionBindingExpression"/>.
    /// </summary>
    /// <param name="projectionBindingExpression">The <see cref="ProjectionBindingExpression"/> to look-up.</param>
    /// <returns>The registered <see cref="ProjectionExpression"/> this <paramref name="projectionBindingExpression"/> relates to.</returns>
    private ProjectionExpression GetProjection(MongoProjectionBindingExpression projectionBindingExpression)
        => _queryExpression.Projection[GetProjectionIndex(projectionBindingExpression)];

    /// <summary>
    /// Create a new compilable <see cref="Expression"/> the shaper can use to obtain the value from the <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="documentExpression">The <see cref="Expression"/> used to access the <see cref="BsonDocument"/>.</param>
    /// <param name="alias">The name of the property.</param>
    /// <param name="propertyBase">The <see cref="INavigation"/> or <see cref="IProperty"/> associated with the value.</param>
    /// <returns>A compilable <see cref="Expression"/> to obtain the desired value as the correct type.</returns>
    private Expression CreateGetValueExpression(
        Expression documentExpression,
        string? alias,
        IPropertyBase? propertyBase)
    {
        if (propertyBase is null && alias is null)
        {
            return documentExpression;
        }

        var innerExpression = documentExpression;
        if (_projectionBindings.TryGetValue(documentExpression, out var innerVariable))
        {
            innerExpression = innerVariable;
        }
        else
        {
            innerExpression = documentExpression switch
            {
                // TODO: handle more nesting; not currently used.
                //RootReferenceExpression => CreateGetValueExpression(DocParameter, null, required, typeof(BsonDocument), propertyBase: propertyBase, declaredType: propertyBase!.DeclaringType),
                //ObjectAccessExpression docAccessExpression => CreateGetValueExpression(docAccessExpression.AccessExpression, docAccessExpression.Name, required, typeof(BsonDocument), propertyBase: propertyBase, declaredType: propertyBase!.DeclaringType),
                _ => innerExpression
            };
        }

        return BsonBinding.CreateGetValueExpression(innerExpression, alias, propertyBase);
    }

    private BlockExpression AddIncludes(BlockExpression shaperBlock)
    {
        if (_pendingIncludes.Count == 0)
        {
            return shaperBlock;
        }

        List<Expression> shaperExpressions = [..shaperBlock.Expressions];
        var instanceVariable = shaperExpressions[^1];
        shaperExpressions.RemoveAt(shaperExpressions.Count - 1);

        var includesToProcess = _pendingIncludes;
        _pendingIncludes = [];

        foreach (var include in includesToProcess)
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
        var navigation = (INavigation)includeExpression.Navigation;
        var includeMethod = navigation.IsCollection ? IncludeCollectionMethodInfo : IncludeReferenceMethodInfo;
        var includingClrType = navigation.DeclaringEntityType.ClrType;
        var relatedEntityClrType = navigation.TargetEntityType.ClrType;
#pragma warning disable EF1001 // Internal EF Core API usage.
        Expression entityEntryVariable = _trackQueryResults
            ? shaperBlock.Variables.Single(v => v.Type == typeof(InternalEntityEntry))
            : Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        var concreteEntityTypeVariable = shaperBlock.Variables.Single(v => v.Type == typeof(IEntityType));
        var inverseNavigation = navigation.Inverse;
        var fixup = GenerateFixup(
            includingClrType, relatedEntityClrType, navigation, inverseNavigation!);

        var navigationExpression = Visit(includeExpression.NavigationExpression);

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
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeReference))!;

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
        // For non-null relatedEntity StateManager will set the flag
        else if (relatedEntity == null)
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeCollection))!;

    private static void IncludeCollection<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry? entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object? entity,
        IEntityType entityType,
        IEnumerable<TIncludedEntity>? relatedEntities,
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
#pragma warning disable EF1001 // Internal EF Core API usage.
                entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
            }

            if (relatedEntities != null)
            {
                using var enumerator = relatedEntities.GetEnumerator();
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
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(PopulateCollection))!;

    private static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(nameof(IReadOnlyEntityType.IsAssignableFrom), [
            typeof(IReadOnlyEntityType)
        ])!;

    private static TCollection PopulateCollection<TEntity, TCollection>(
        IClrCollectionAccessor accessor,
        IEnumerable<TEntity> entities)
    {
        // TODO: throw a better exception for non-ICollection navigations
        var collection = (ICollection<TEntity>)accessor.Create();
        foreach (var entity in entities)
        {
            collection.Add(entity);
        }

        return (TCollection)collection;
    }

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

    private int GetProjectionIndex(MongoProjectionBindingExpression projectionBindingExpression)
        => projectionBindingExpression.ProjectionMember != null
            ? _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember).GetConstantValue<int>()
            : projectionBindingExpression.Index
              ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");
}
