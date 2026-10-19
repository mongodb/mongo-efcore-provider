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
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// One entry of a string-keyed dictionary property (<c>d.Dictionary["key"]</c>), addressed by its dotted document path
/// (<c>Dictionary.key</c>): a dictionary is always stored as a BSON document, so an entry is a sub-field.
/// </summary>
/// <remarks>
/// Not a <see cref="MongoFieldExpression"/>: the only property is the dictionary's, and an entry is serialized by
/// that serializer's <em>value</em> serializer. A sibling type rather than a flag, since it needs different handling
/// at several sites (query-dialect rendering, negation, prefixing, the aggregation dialect declining it).
/// </remarks>
internal sealed class MongoDictionaryEntryExpression : MongoExpression
{
    public MongoDictionaryEntryExpression(IProperty dictionaryProperty, string path, Type valueType)
    {
        DictionaryProperty = dictionaryProperty;
        Path = path;
        Type = valueType;
    }

    public IProperty DictionaryProperty { get; }

    public string Path { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
