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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class ValueBufferToBsonBindingExpressionVisitor : ExpressionVisitor
{
    private readonly ParameterExpression _bsonDocParameter;

    /// <summary>
    /// Create a <see cref="ValueBufferToBsonBindingExpressionVisitor"/>.
    /// </summary>
    /// <param name="bsonDocParameter">
    /// The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.
    /// </param>
    public ValueBufferToBsonBindingExpressionVisitor(ParameterExpression bsonDocParameter)
    {
        _bsonDocParameter = bsonDocParameter;
    }

    /// <summary>
    /// Visits an extension expression to ensure that any <see cref="ProjectionBindingExpression"/> are
    /// correctly bound to the expected result in the <see cref="BsonDocument"/> returned from MongoDB.
    /// </summary>
    /// <param name="extensionExpression">The <see cref="Expression"/> to visit.</param>
    /// <returns>A translated <see cref="Expression"/>.</returns>
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case ProjectionBindingExpression projectionBindingExpression:
                var query = (MongoQueryExpression)projectionBindingExpression.QueryExpression;
                var projection = query.GetMappedProjection(projectionBindingExpression.ProjectionMember!);
                switch (projection)
                {
                    case EntityPropertyBindingExpression propertyBindingExpression:
                        {
                            var property = propertyBindingExpression.BoundProperty;
                            var resultValue = CreateGetValueExpression(_bsonDocParameter, property);
                            return ConvertTypeIfRequired(resultValue, projectionBindingExpression.Type);
                        }
                    case BsonElementBindingExpression elementBindingExpression:
                        {
                            var resultValue = CreateGetValueExpression(_bsonDocParameter, elementBindingExpression.ElementName, elementBindingExpression.Type);
                            return ConvertTypeIfRequired(resultValue, projectionBindingExpression.Type);
                        }
                    default:
                        throw new NotSupportedException($"Unknown projection binding type `${projection.GetType().Name}`");
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
        // Replace empty ProjectionBindingExpression with ValueBuffer.
        if (binaryExpression is {NodeType: ExpressionType.Assign, Left: ParameterExpression parameterExpression} &&
            parameterExpression.Type == typeof(MaterializationContext) &&
            binaryExpression.Right is NewExpression newExpression &&
            newExpression.Arguments[0] is ProjectionBindingExpression)
        {
            var updatedExpression = Expression.New(
                newExpression.Constructor!,
                Expression.Constant(ValueBuffer.Empty),
                newExpression.Arguments[1]);

            return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
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
        if (genericMethod != ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            return base.VisitMethodCall(methodCallExpression);
        }

        var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
        var resultValue = CreateGetValueExpression(_bsonDocParameter, property);
        return ConvertTypeIfRequired(resultValue, methodCallExpression.Type);
    }

    private static Expression CreateGetValueExpression(Expression bsonDocExpression, IReadOnlyProperty property)
    {
        return CreateGetValueExpression(bsonDocExpression, property.Name, property.GetTypeMapping().ClrType);
    }

    private static Expression CreateGetValueExpression(Expression bsonDocExpression, string name, Type mappedType)
    {
        if (mappedType.IsArray)
        {
            return CreateGetArrayOf(bsonDocExpression, name, mappedType.TryGetItemType()!);
        }

        // Support lists and variants that expose IEnumerable<T> and have a matching constructor
        if (mappedType is {IsGenericType: true, IsGenericTypeDefinition: false})
        {
            var enumerableType = mappedType.TryFindIEnumerable();
            if (enumerableType != null)
            {
                var constructor = mappedType.TryFindConstructorWithParameter(enumerableType);
                if (constructor != null)
                {
                    return Expression.New(constructor,
                        CreateGetEnumerableOf(bsonDocExpression, name, enumerableType.TryGetItemType()!));
                }
            }
        }

        return CreateGetValueAs(bsonDocExpression, name, mappedType);
    }

    private static Expression ConvertTypeIfRequired(Expression expression, Type intendedType)
        => expression.Type != intendedType
            ? Expression.Convert(expression, intendedType)
            : expression;

    private static Expression CreateGetValueAs(Expression bsonValueExpression, string name, Type type) =>
        Expression.Call(null, __getValueAsMethodInfo.MakeGenericMethod(type), bsonValueExpression, Expression.Constant(name));

    private static Expression CreateGetArrayOf(Expression bsonValueExpression, string name, Type type) =>
        Expression.Call(null, __getArrayOfMethodInfo.MakeGenericMethod(type), bsonValueExpression, Expression.Constant(name));

    private static Expression CreateGetEnumerableOf(Expression bsonValueExpression, string name, Type type) =>
        Expression.Call(null, __getEnumerableOfMethodInfo.MakeGenericMethod(type), bsonValueExpression, Expression.Constant(name));

    private static readonly MethodInfo __getValueAsMethodInfo
        = typeof(BsonConverter).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(BsonConverter.GetValueAs) && mi.GetParameters().Length == 2);

    private static readonly MethodInfo __getArrayOfMethodInfo
        = typeof(BsonConverter).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(BsonConverter.GetArrayOf) && mi.GetParameters().Length == 2);

    private static readonly MethodInfo __getEnumerableOfMethodInfo
        = typeof(BsonConverter).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(BsonConverter.GetEnumerableOf) && mi.GetParameters().Length == 2);
}
