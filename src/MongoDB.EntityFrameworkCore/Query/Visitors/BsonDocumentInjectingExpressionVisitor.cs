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

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

internal sealed class BsonDocumentInjectingExpressionVisitor : ExpressionVisitor
{
    private int _currentEntityIndex;

    /// <summary>
    /// All BsonDocument/BsonArray variables created during injection.
    /// These are collected so they can also be declared at the lambda level,
    /// making them accessible across entity boundaries in join projections.
    /// </summary>
    public List<ParameterExpression> AllVariables { get; } = [];

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case StructuralTypeShaperExpression shaperExpression:
                {
                    _currentEntityIndex++;

                    var valueBufferExpression = shaperExpression.ValueBufferExpression;

                    var bsonDocAccess = Expression.Variable(
                        typeof(BsonDocument),
                        "bsonDoc" + _currentEntityIndex);
                    var variables = new List<ParameterExpression> {bsonDocAccess};

                    AllVariables.Add(bsonDocAccess);

                    var expressions = new List<Expression>
                    {
                        Expression.Assign(
                            bsonDocAccess,
                            Expression.TypeAs(
                                valueBufferExpression,
                                typeof(BsonDocument))),
                        Expression.Condition(
                            Expression.Equal(bsonDocAccess, Expression.Constant(null, bsonDocAccess.Type)),
                            Expression.Constant(null, shaperExpression.Type),
                            shaperExpression)
                    };

                    return Expression.Block(
                        shaperExpression.Type,
                        variables,
                        expressions);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    _currentEntityIndex++;

                    var arrayVariable = Expression.Variable(typeof(BsonArray), "bsonArray" + _currentEntityIndex);
                    var variables = new List<ParameterExpression> {arrayVariable};

                    AllVariables.Add(arrayVariable);

                    // Guard nested entities materialized per collection element — in particular cross-collection
                    // reference ThenIncludes read from "_lookup_<Nav>" sub-documents — with the "joined document
                    // is null => null entity" check. Without this the nested reference's key-presence test reads a
                    // missing key as the value type's default (e.g. 0) and materializes a phantom entity instead
                    // of null. Only the ThenInclude navigation entities are wrapped; the collection ELEMENT itself
                    // reads from a real array element (never null) and must keep its direct binding. EF-X023/X024.
                    var updatedCollectionShaper = collectionShaperExpression.Update(
                        collectionShaperExpression.Projection,
                        WrapNestedNavigations(collectionShaperExpression.InnerShaper));

                    var expressions = new List<Expression>
                    {
                        Expression.Assign(
                            arrayVariable,
                            Expression.TypeAs(
                                collectionShaperExpression.Projection,
                                typeof(BsonArray))),
                        Expression.Condition(
                            Expression.Equal(arrayVariable, Expression.Constant(null, arrayVariable.Type)),
                            Expression.Constant(null, collectionShaperExpression.Type),
                            updatedCollectionShaper)
                    };

                    return Expression.Block(
                        collectionShaperExpression.Type,
                        variables,
                        expressions);
                }
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Wrap the ThenInclude navigation entities of a collection element's inner shaper with the
    /// "joined document is null => null entity" guard, without wrapping the element entity itself. The inner
    /// shaper is an <see cref="IncludeExpression"/> chain whose innermost <c>EntityExpression</c> is the
    /// collection element (read from a real array element — never null, keeps its direct binding) and whose
    /// <c>NavigationExpression</c>s are the cross-collection reference/collection ThenIncludes that need the
    /// guard. EF-X023/X024.
    /// </summary>
    private Expression WrapNestedNavigations(Expression innerShaper)
        => innerShaper is IncludeExpression include
            ? include.Update(
                WrapNestedNavigations(include.EntityExpression),
                Visit(include.NavigationExpression))
            : innerShaper;
}
