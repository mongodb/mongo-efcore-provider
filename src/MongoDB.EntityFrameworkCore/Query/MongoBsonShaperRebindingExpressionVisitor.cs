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
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and associated methods
/// instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
public class MongoBsonShaperRebindingExpressionVisitor : ExpressionVisitor
{
    private readonly ParameterExpression _bsonDocParameter;

    /// <summary>
    /// Create a <see cref="MongoBsonShaperRebindingExpressionVisitor"/>.
    /// </summary>
    /// <param name="bsonDocParameter">
    /// The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.
    /// </param>
    public MongoBsonShaperRebindingExpressionVisitor(ParameterExpression bsonDocParameter)
    {
        _bsonDocParameter = bsonDocParameter;
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

        var property = GetConstantValue<IProperty>(methodCallExpression.Arguments[2]);
        return CreateGetValueExpression(_bsonDocParameter, property, methodCallExpression.Type);
    }

    private Expression CreateGetValueExpression(
        Expression bsonDocExpression,
        IProperty property,
        Type type)
    {
        var expectedType = property.GetTypeMapping()?.ClrType ?? type;

        // TODO: Support json property names with EF method/convention
        var valueExpression = CreateGetBsonValueExpression(bsonDocExpression, property.Name);
        valueExpression = ConvertToExpectedType(expectedType, valueExpression);

        if (valueExpression.Type != type)
        {
            valueExpression = Expression.Convert(valueExpression, type);
        }

        return valueExpression;
    }

    private static Expression ConvertToExpectedType(Type expectedType, Expression valueExpression)
    {
        // Shortcut arrays for performance
        if (expectedType.IsArray)
        {
            valueExpression = Expression.Convert(InitializeCollectionIfNull(valueExpression), typeof(BsonArray));
            return BsonConverter.BsonArrayToArray(valueExpression, expectedType.TryGetItemType()!);
        }

        // Support any lists and variants that expose IEnumerable<T> and have a matching constructor
        if (expectedType.IsGenericType && !expectedType.IsGenericTypeDefinition)
        {
            var enumerableType = expectedType.TryFindIEnumerable();
            if (enumerableType != null)
            {
                var constructor = expectedType.TryFindConstructorWithParameter(enumerableType);
                if (constructor != null)
                {
                    valueExpression = Expression.Convert(InitializeCollectionIfNull(valueExpression), typeof(BsonArray));
                    return BsonConverter.BsonArrayToEnumerable(valueExpression, constructor, expectedType.TryGetItemType()!);
                }
            }
        }

        // Try CLR basic types supported by BsonDocument
        return BsonConverter.BsonValueToType(valueExpression, expectedType);
    }

    private static Expression InitializeCollectionIfNull(Expression valueExpression)
    {
        // TODO: Consider making this behavior configurable through the fluent API
        return Expression.Condition(
            Expression.Equal(valueExpression, Expression.Constant(BsonNull.Value)),
            Expression.New(typeof(BsonArray)),
            valueExpression,
            typeof(BsonValue));
    }

    private static Expression CreateGetBsonValueExpression(Expression bsonDocExpression, string propertyName)
        => Expression.Call(bsonDocExpression, __getValueMethodInfo, Expression.Constant(propertyName),
            Expression.Constant(BsonNull.Value));

    private static readonly MethodInfo __getValueMethodInfo
        = typeof(BsonDocument).GetMethods()
            .Single(mi => mi.Name == "GetValue" && mi.GetParameters().Length == 2 &&
                          mi.GetParameters()[0].ParameterType == typeof(string));

    private static T GetConstantValue<T>(Expression expression)
        => expression is ConstantExpression constantExpression
            ? (T)constantExpression.Value!
            : throw new InvalidOperationException();
}
