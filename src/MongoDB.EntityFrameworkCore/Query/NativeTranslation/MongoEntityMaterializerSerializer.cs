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
/// One-pass "deserialize IS materialize" output serializer for <c>IMongoCollection.Aggregate</c>: each cursor row is constructed
/// into <typeparamref name="TEntity"/> during the driver's own deserialization pass, with no intermediate copy of the document.
/// <paramref name="shaper"/> (from <see cref="MongoStreamingEntityMaterializerRewriter"/>) reads exactly one document without
/// opening or disposing the reader (the cursor owns it).
/// </summary>
/// <remarks>
/// The driver deserializes a whole cursor batch before yielding its first row, so anything done here happens for rows the caller may
/// never read. Construction is therefore all that is done: tracking is recorded for the enumerator to replay when it reaches the row
/// (<see cref="MongoQueryContext.BeginDeferral"/>). Stored data that does not match the model is not recovered from: the exception surfaces
/// from the driver's batch deserialization. This is the serializer for a tracked query; an untracked one uses
/// <see cref="MongoUntrackedEntitySerializer{TEntity}"/>.
/// </remarks>
internal sealed class MongoEntityMaterializerSerializer<TEntity>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TEntity> shaper,
    MongoQueryContext queryContext)
    : SerializerBase<MongoMaterializedRow<TEntity>>
{
    /// <summary>
    /// The enumerator-side shaper: completes a row the serializer constructed, by replaying its deferred tracking.
    /// </summary>
    public static readonly Func<QueryContext, MongoMaterializedRow<TEntity>, TEntity> RowShaper = static (queryContext, row) => row.Deferred switch
    {
        null => row.Instance!,
        MongoSimpleTracking simple => (TEntity)simple.Replay((MongoQueryContext)queryContext, row.Instance!),
        MongoPendingTracking pending => (TEntity)pending.Replay((MongoQueryContext)queryContext, row.Instance!),
        _ => throw new InvalidOperationException("Internal error - unexpected deferred state on a materialized row.")
    };

    // The per-document context is passed into the shaper so every typed property read reuses it, as the
    // driver's class-map serializer does, rather than allocating one per property per row.
    public override MongoMaterializedRow<TEntity> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        queryContext.BeginDeferral();
        var instance = shaper(queryContext, context.Reader, context);
        var deferred = queryContext.EndDeferral(instance);
        return new MongoMaterializedRow<TEntity>(instance, deferred);
    }
}
