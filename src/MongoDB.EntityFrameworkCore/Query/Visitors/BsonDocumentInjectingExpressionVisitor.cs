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

                    // EF-358: a MISSING or explicitly-BSON-null stored array normalizes to an EMPTY collection
                    // rather than collapsing the whole collection shaper to null. This used to be an
                    // Expression.Condition whose null branch was Expression.Constant(null,
                    // collectionShaperExpression.Type). CORRECTED (measured false): the removed conditional's
                    // comment used to say that null branch "is what made a projected collection come back null
                    // for those rows while whole-entity materialization of the SAME document yielded an empty
                    // collection" — implying whole-entity was already correct and only the projection path had
                    // this null-collapse. Pre-fix that conditional (or its equivalent computation) is what EVERY
                    // path reached, whole-entity included — MongoProjectionBindingRemovingExpressionVisitor's
                    // IncludeCollection skips its fixup loop when relatedEntities is null, so a materialized
                    // navigation kept whatever the CLR class's own field initializer left, null or [] depending
                    // on the class. There was no working whole-entity behavior this conditional diverged from;
                    // deleting it is one half of making EVERY path uniform. The normalization itself lives in
                    // MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case, which
                    // coalesces the array at its point of use; see the comment there.
                    //
                    // The Expression.Assign below must keep a UnaryExpression right-hand side: the removing
                    // visitor's VisitBinary hard-casts `binaryExpression.Right` to UnaryExpression for any
                    // Assign whose left side is a BsonDocument- or BsonArray-typed ParameterExpression. Folding
                    // the coalesce in here (Coalesce is a BinaryExpression) throws InvalidCastException for
                    // EVERY collection shaper in every query mode, not just a ragged row.
                    var expressions = new List<Expression>
                    {
                        Expression.Assign(
                            arrayVariable,
                            Expression.TypeAs(
                                collectionShaperExpression.Projection,
                                typeof(BsonArray))),
                        collectionShaperExpression
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
