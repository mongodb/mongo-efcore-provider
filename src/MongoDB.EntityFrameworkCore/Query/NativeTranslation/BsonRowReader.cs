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

using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>Opens a forward-only <see cref="IBsonReader"/> over a RawBsonDocument's raw bytes (no DOM build).</summary>
/// <remarks>
/// <b>DEAD CODE — TRACKED AS EF-419.</b> The SP7 one-pass materializer made the driver's own cursor reader the
/// single reader, so nothing calls this any more and the <c>RawBsonDocument</c> streaming branch it serves
/// (<c>MongoClientWrapper.Execute</c>, <c>QueryingEnumerable.ReleaseCurrentRow</c>) is unreachable. It is
/// retained rather than deleted only because removing it touches Storage as well as Query; EF-419 covers the
/// removal of both this type and that branch. Do not build new code on it.
/// </remarks>
internal static class BsonRowReader
{
    public static BsonBinaryReader Open(RawBsonDocument row)
        => new(new ByteBufferStream(row.Slice, ownsBuffer: false));
}
