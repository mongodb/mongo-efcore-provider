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

using System.Collections;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Builds the <c>$or</c>/<c>$nor</c> clause array of a composite-key <c>Contains</c>, shared by the constant-list render
/// and the per-execution parameter substitution (<see cref="MongoPipelineFactory"/>) so the two cannot disagree.
/// </summary>
internal static class CompositeKeyClauses
{
    /// <summary>
    /// One <c>{ "_id.K1": v1, "_id.K2": v2 }</c> clause per non-null entity. Never empty: an empty (or all-null) list
    /// yields one always-false clause, because <c>$or: []</c> and <c>$nor: []</c> are server errors.
    /// </summary>
    internal static BsonArray Build(IEnumerable entities, IReadOnlyList<IProperty> keys)
    {
        var clauses = new BsonArray();
        foreach (var entity in entities)
        {
            if (entity is null)
                continue; // a null element can never match a persisted key

            var clause = new BsonDocument();
            foreach (var key in keys)
            {
                var serializer = BsonSerializerFactory.GetPropertySerializationInfo(key).Serializer;
                var coerced = BsonValueSerializer.Coerce(serializer.ValueType, key.GetGetter().GetClrValue(entity));
                clause.Add(
                    MongoExpressionTranslator.GetPropertyFieldPath(key),
                    BsonValueSerializer.SerializeNullAware(serializer, coerced));
            }

            clauses.Add(clause);
        }

        if (clauses.Count == 0)
        {
            clauses.Add(new BsonDocument(
                MongoExpressionTranslator.GetPropertyFieldPath(keys[0]), new BsonDocument("$in", new BsonArray())));
        }

        return clauses;
    }
}
