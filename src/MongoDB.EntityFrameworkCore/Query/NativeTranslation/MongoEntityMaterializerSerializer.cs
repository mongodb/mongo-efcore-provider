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
/// The one-pass "deserialize IS materialize" output serializer (SP7 P1.2). Supplied to
/// <c>IMongoCollection.Aggregate</c> as the pipeline's output serializer so that each cursor row is
/// materialized into a finished (and, on the tracked path, tracked) <typeparamref name="TEntity"/> in a
/// single forward <see cref="IBsonReader"/> pass — the driver's own deserialization pass — rather than being
/// read into a <c>RawBsonDocument</c> and materialized again in a second pass.
/// </summary>
/// <remarks>
/// <paramref name="shaper"/> is the compiled EF materializer produced by
/// <see cref="MongoStreamingEntityMaterializerRewriter"/>, rewritten to read exactly one document off the
/// reader it is handed (<c>ReadStartDocument</c> … fill loop … <c>ReadEndDocument</c>), with no open and no
/// dispose — the driver cursor owns the reader. It is compiled with EF's <c>QueryContext</c>-typed materializer
/// parameter, so the first argument is typed <see cref="QueryContext"/>; the captured
/// <paramref name="queryContext"/> is the concrete <see cref="MongoQueryContext"/> (a <see cref="QueryContext"/>)
/// for this execution, carrying the initialized state manager the tracked materializer needs.
/// </remarks>
internal sealed class MongoEntityMaterializerSerializer<TEntity>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TEntity> shaper,
    MongoQueryContext queryContext)
    : SerializerBase<TEntity>
{
    // The incoming per-document context is threaded into the compiled shaper so per-property typed reads
    // reuse ONE deserialization context (its Reader is the reader we read from) — no BsonDeserializationContext
    // is allocated per property per row (SP7 P1.3). This mirrors the driver's own class-map serializer, which
    // reuses one context for every member of a document.
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => shaper(queryContext, context.Reader, context);
}
