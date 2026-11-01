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
/// The one-pass output serializer of a tracked query whose projection holds entities: <paramref name="rowReader"/> (from
/// <see cref="MongoProjectionStreamingPlanner"/>) defers the tracking of the entities it constructs and ends the deferral into the row
/// (<see cref="MongoProjectedRow{TValues}.Deferred"/>), which the enumerator replays when it reaches the row. A row that fails to
/// materialize is skipped through a reader bookmark and raised only if it is read, as for <see cref="MongoUntrackedEntitySerializer{TEntity}"/>.
/// </summary>
internal sealed class MongoTrackedProjectionSerializer<TRow>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TRow> rowReader,
    MongoQueryContext queryContext)
    : SerializerBase<TRow>
{
    public override TRow Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var bookmark = reader.GetBookmark();

        try
        {
            var row = rowReader(queryContext, reader, context);
            queryContext.RowDeserialized();
            return row;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            queryContext.AbandonDeferral();

            reader.ReturnToBookmark(bookmark);
            reader.SkipValue();

            queryContext.RowFailed(ExceptionDispatchInfo.Capture(exception));
            return default!;
        }
    }
}
