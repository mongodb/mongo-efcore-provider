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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal sealed class MongoEFToLinqTranslatingExpressionVisitor :  System.Linq.Expressions.ExpressionVisitor
{
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethod(nameof(Mql.Field), BindingFlags.Public | BindingFlags.Static)!;

    private readonly QueryContext _queryContext;
    private readonly Expression _source;
    private readonly BsonSerializerFactory _bsonSerializerFactory;

    internal MongoEFToLinqTranslatingExpressionVisitor(
        QueryContext queryContext,
        Expression source,
        BsonSerializerFactory bsonSerializerFactory)
    {
        _queryContext = queryContext;
        _source = source;
        _bsonSerializerFactory = bsonSerializerFactory;
    }

    public MethodCallExpression Translate(
        Expression? efQueryExpression,
        ResultCardinality resultCardinality)
    {
        if (efQueryExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            return ApplyAsSerializer(_source, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        var query = (MethodCallExpression)Visit(efQueryExpression)!;

        if (resultCardinality == ResultCardinality.Enumerable)
        {
            return ApplyAsSerializer(query, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        var documentQueryableSource = ApplyAsSerializer(query.Arguments[0], BsonDocumentSerializer.Instance, typeof(BsonDocument));

        return Expression.Call(
            null,
            query.Method.GetGenericMethodDefinition().MakeGenericMethod(typeof(BsonDocument)),
            documentQueryableSource);
    }

    private static MethodCallExpression ApplyAsSerializer(
        Expression query,
        IBsonSerializer resultSerializer,
        Type resultType)
    {
        var asMethodInfo = AsMethodInfo.MakeGenericMethod(query.Type.GenericTypeArguments[0], resultType);
        var serializerExpression = Expression.Constant(resultSerializer, resultSerializer.GetType());

        return Expression.Call(
            null,
            asMethodInfo,
            query,
            serializerExpression
        );
    }

    private static bool IsAsQueryableMethod(MethodInfo method)
        => method.Name == "AsQueryable" && method.DeclaringType == typeof(Queryable);

    public override Expression? Visit(Expression? expression)
    {
        switch (expression)
        {
            // Replace materialization collection expression with the actual nav property in order for Mql.Exists etc. to work.
            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                var subQuery = Visit(materializeCollectionNavigationExpression.Subquery);
                if (subQuery is MethodCallExpression mce && IsAsQueryableMethod(mce.Method))
                {
                    return Visit(mce.Arguments[0]);
                }

                return subQuery;

            // Replace the QueryContext parameter values with constant values for this execution.
            case ParameterExpression parameterExpression:
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    if (_queryContext.ParameterValues.TryGetValue(parameterExpression.Name, out var value))
                    {
                        return ConvertIfRequired(Expression.Constant(value), expression.Type);
                    }
                }

                break;

            // Wrap OfType<T> with As(serializer) to re-attach the custom serializer in LINQ3
            case MethodCallExpression
                {
                    Method.Name: nameof(Queryable.OfType), Method.IsGenericMethod: true, Arguments.Count: 1
                } ofTypeCall
                when ofTypeCall.Method.DeclaringType == typeof(Queryable):
                var resultType = ofTypeCall.Method.GetGenericArguments()[0];
                var resultEntityType = _queryContext.Context.Model.FindEntityType(resultType)
                                       ?? throw new NotSupportedException($"OfType type '{resultType.ShortDisplayName()
                                       }' does not map to an entity type.");
                var resultSerializer = _bsonSerializerFactory.GetEntitySerializer(resultEntityType);
                var translatedOfTypeCall = Expression.Call(null, ofTypeCall.Method, Visit(ofTypeCall.Arguments[0])!);
                return ApplyAsSerializer(translatedOfTypeCall, resultSerializer, resultType);

            // Replace object.Equals(Property(p, "propName"), ConstantExpression) elements generated by EF's Find.
            case MethodCallExpression {Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2} methodCallExpression:
                var left = Visit(RemoveObjectConvert(methodCallExpression.Arguments[0]))!;
                var right = Visit(RemoveObjectConvert(methodCallExpression.Arguments[1]))!;
                var method = methodCallExpression.Method;

                if (left.Type == right.Type)
                {
                    return Expression.Equal(RemoveObjectConvert(left), RemoveObjectConvert(right));
                }

                var parameters = method.GetParameters();
                left = ConvertIfRequired(left, parameters[0].ParameterType);
                right = ConvertIfRequired(right, parameters[1].ParameterType);
                return Expression.Call(null, method, left, right);

            // Replace EF-generated Property(p, "propName") with Property(p.propName) or Mql.Field(p, "propName", serializer)
            case MethodCallExpression methodCallExpression
                when methodCallExpression.Method.IsEFPropertyMethod()
                     && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression:
                var source = Visit(methodCallExpression.Arguments[0])
                             ?? throw new InvalidOperationException("Unsupported source to EF.Property expression.");

                var propertyName = propertyNameExpression.GetConstantValue<string>();
                var entityType = _queryContext.Context.Model.FindEntityType(source.Type);
                if (entityType != null)
                {
                    // Try an EF property
                    var efProperty = entityType.FindProperty(propertyName);
                    if (efProperty != null)
                    {
                        var doc = source;

                        // Composite keys need to go via the _id document
                        var isCompositeKeyAccess = efProperty.IsPrimaryKey() && entityType.FindPrimaryKey()?.Properties.Count > 1;
                        if (isCompositeKeyAccess)
                        {
                            var mqlFieldDoc = MqlFieldMethodInfo.MakeGenericMethod(source.Type, typeof(BsonValue));
                            doc = Expression.Call(null, mqlFieldDoc, source, Expression.Constant("_id"),
                                Expression.Constant(BsonValueSerializer.Instance));
                        }

                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(doc.Type, efProperty.ClrType);
                        var serializer = BsonSerializerFactory.CreateTypeSerializer(efProperty);
                        var callExpression = Expression.Call(null, mqlField, doc,
                            Expression.Constant(efProperty.GetElementName()),
                            Expression.Constant(serializer));
                        return ConvertIfRequired(callExpression, methodCallExpression.Method.ReturnType);
                    }

                    // Try an EF navigation if no property
                    var efNavigation = entityType.FindNavigation(propertyName);
                    if (efNavigation != null)
                    {
                        var elementName = efNavigation.TargetEntityType.GetContainingElementName();
                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, efNavigation.ClrType);
                        var serializer = _bsonSerializerFactory.GetNavigationSerializer(efNavigation);
                        var callExpression = Expression.Call(null, mqlField, source,
                            Expression.Constant(elementName),
                            Expression.Constant(serializer));
                        return ConvertIfRequired(callExpression, methodCallExpression.Method.ReturnType);
                    }
                }

                // Try CLR property
                // This should not really be required but is kept here for backwards compatibility with any edge cases.
                var clrProperty = source.Type.GetProperties().FirstOrDefault(p => p.Name == propertyName);
                if (clrProperty != null)
                {
                    var propertyExpression = Expression.Property(source, clrProperty);
                    return ConvertIfRequired(propertyExpression, methodCallExpression.Method.ReturnType);
                }

                return VisitMethodCall(methodCallExpression);

            case MethodCallExpression methodCallExpression:
                return VisitMethodCall(methodCallExpression);

            // Unwrap include expressions.
            case IncludeExpression includeExpression:
                return Visit(includeExpression.EntityExpression);

            // Replace the root with the MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression:
                return _source;
        }

        return base.Visit(expression);
    }

    private static Expression ConvertIfRequired(Expression expression, Type targetType) =>
        expression.Type == targetType ? expression : Expression.Convert(expression, targetType);

    private static Expression RemoveObjectConvert(Expression expression)
        => expression is UnaryExpression {NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked} unaryExpression
           && unaryExpression.Type == typeof(object)
            ? unaryExpression.Operand
            : expression;

    private static readonly MethodInfo AsMethodInfo = typeof(MongoQueryable)
        .GetMethods()
        .First(mi => mi is {Name: nameof(MongoQueryable.As), IsPublic: true, IsStatic: true} && mi.GetParameters().Length == 2);
}
