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
/// The one-pass output serializer of an untracked query: each cursor row is the finished <typeparamref name="TEntity"/>, constructed
/// during the driver's own deserialization pass. There is nothing to defer, so the cursor yields the entities themselves. Stored data that
/// does not match the model (a string where the property is an <see cref="int"/>) is not recovered from: the exception surfaces from the
/// driver's batch deserialization. A tracked query uses <see cref="MongoEntityMaterializerSerializer{TEntity}"/>.
/// </summary>
internal sealed class MongoUntrackedEntitySerializer<TEntity>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, TEntity> shaper,
    MongoQueryContext queryContext)
    : SerializerBase<TEntity>
{
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => shaper(queryContext, context.Reader, context);
}
