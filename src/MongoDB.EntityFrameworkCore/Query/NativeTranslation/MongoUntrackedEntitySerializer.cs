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
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The one-pass output serializer of an untracked query: each cursor row is the finished <typeparamref name="TEntity"/>, constructed
/// during the driver's own deserialization pass. There is nothing to defer, so the cursor yields the entities themselves and the only
/// addition to the plain one-pass read is that a row that fails to materialize is skipped, through a reader bookmark, and its failure
/// raised only if the enumerator reaches it (<see cref="MongoQueryContext.RowFailed"/>), so a malformed later row of the driver's batch
/// cannot fail an earlier valid one. A tracked query uses <see cref="MongoEntityMaterializerSerializer{TEntity}"/>.
/// </summary>
internal sealed class MongoUntrackedEntitySerializer<TEntity>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TEntity> shaper,
    MongoQueryContext queryContext)
    : SerializerBase<TEntity>
{
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var bookmark = reader.GetBookmark();

        try
        {
            var instance = shaper(queryContext, reader, context);
            queryContext.RowDeserialized();
            return instance;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The reader is somewhere inside the failed document: go back to its start and skip it, so the rest of the batch still reads.
            reader.ReturnToBookmark(bookmark);
            reader.SkipValue();

            queryContext.RowFailed(ExceptionDispatchInfo.Capture(exception));
            return default!;
        }
    }
}
