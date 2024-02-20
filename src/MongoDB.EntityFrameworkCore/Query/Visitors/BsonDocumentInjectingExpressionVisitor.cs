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
                            collectionShaperExpression)
                    };

                    return Expression.Block(
                        collectionShaperExpression.Type,
                        variables,
                        expressions);
                }
        }

        return base.VisitExtension(extensionExpression);
    }
}
