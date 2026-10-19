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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// One-pass "deserialize IS materialize" output serializer for <c>IMongoCollection.Aggregate</c>: each cursor row is
/// materialized (and tracked) into <typeparamref name="TEntity"/> during the driver's own deserialization pass.
/// <paramref name="shaper"/> (from <see cref="MongoStreamingEntityMaterializerRewriter"/>) reads exactly one document
/// without opening or disposing the reader (the cursor owns it); <paramref name="queryContext"/> carries the state
/// manager the tracked materializer needs.
/// </summary>
internal sealed class MongoEntityMaterializerSerializer<TEntity>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TEntity> shaper,
    MongoQueryContext queryContext)
    : SerializerBase<TEntity>
{
    // The per-document context is passed into the shaper so every typed property read reuses it, as the
    // driver's class-map serializer does, rather than allocating one per property per row.
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => shaper(queryContext, context.Reader, context);
}
