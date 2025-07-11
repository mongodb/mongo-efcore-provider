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

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Names for well-known Mongo model annotations. Applications should not use these names
/// directly, but should instead use the extension methods on metadata objects.
/// </summary>
public static class MongoAnnotationNames
{
    /// <summary>
    /// The prefix used for all MongoDB annotations.
    /// </summary>
    public const string Prefix = "Mongo:";

    /// <summary>
    /// The key for collection name annotations.
    /// </summary>
    public const string CollectionName = Prefix + nameof(CollectionName);

    /// <summary>
    /// The key for document element name annotations.
    /// </summary>
    public const string ElementName = Prefix + nameof(ElementName);

    /// <summary>
    /// The key for <see cref="DateTimeKind"/> annotations.
    /// </summary>
    public const string DateTimeKind = Prefix + nameof(DateTimeKind);

    /// <summary>
    /// The key for marking annotations as not supported.
    /// </summary>
    public const string NotSupportedAttributes = Prefix + nameof(NotSupportedAttributes);

    /// <summary>
    /// The key for Bson representation annotations.
    /// </summary>
    public const string BsonRepresentation = Prefix + nameof(BsonRepresentation);

    /// <summary>
    /// The key for create index options annotations.
    /// </summary>
    public const string CreateIndexOptions = Prefix + nameof(CreateIndexOptions);

    /// <summary>
    /// The key for the id of the data key used for encryption.
    /// </summary>
    public const string EncryptionDataKeyId = Prefix + nameof(EncryptionDataKeyId);

    /// <summary>
    /// The key for the kind of Queryable Encryption is used for this property/field.
    /// </summary>
    public const string QueryableEncryptionType = Prefix + nameof(QueryableEncryptionType);

    /// <summary>
    /// The key for the minimum allowed value for a Queryable Encrypted range property/field.
    /// </summary>
    public const string QueryableEncryptionRangeMin = Prefix + nameof(QueryableEncryptionRangeMin);

    /// <summary>
    /// The key for the maximum allowed value for a Queryable Encrypted range property/field.
    /// </summary>
    public const string QueryableEncryptionRangeMax = Prefix + nameof(QueryableEncryptionRangeMax);

    /// <summary>
    /// The key for the contention factor specified for a Queryable Encrypted property/field.
    /// </summary>
    public const string QueryableEncryptionContention = Prefix + nameof(QueryableEncryptionContention);

    /// <summary>
    /// The key for the trim factor specified for a Queryable Encrypted range property/field.
    /// </summary>
    public const string QueryableEncryptionTrimFactor = Prefix + nameof(QueryableEncryptionTrimFactor);

    /// <summary>
    /// The key for the precision specified for a Queryable Encrypted range property/field.
    /// </summary>
    public const string QueryableEncryptionPrecision = Prefix + nameof(QueryableEncryptionPrecision);

    /// <summary>
    /// The key for the sparsity specified for a Queryable Encrypted range property/field.
    /// </summary>
    public const string QueryableEncryptionSparsity = Prefix + nameof(QueryableEncryptionSparsity);
}
