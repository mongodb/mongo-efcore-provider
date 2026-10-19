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

using System.Collections.Generic;
using System.Text;
using MongoDB.Bson.IO;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Decodes the element names of one document level for the streaming materializer's fill loop. A name the entity maps comes back as the
/// canonical <see cref="string"/> held by a trie, so reading it allocates nothing; any other name is decoded to a new string, as
/// <see cref="Utf8NameDecoder"/> would, and is then skipped by the loop.
/// </summary>
/// <remarks>
/// Unlike the driver's <see cref="TrieNameDecoder{TValue}"/>, this decoder keeps no per-lookup state (no <c>Found</c>/<c>Value</c>), so ONE
/// instance is shared by every row of every execution of the compiled shaper, on any thread, and costs no allocation per document.
/// </remarks>
internal sealed class MongoElementNameDecoder : INameDecoder
{
    private readonly BsonTrie<bool> _trie = new();

    public MongoElementNameDecoder(IEnumerable<string> elementNames)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var name in elementNames)
        {
            if (seen.Add(name))
            {
                _trie.Add(name, true);
            }
        }
    }

    public string Decode(BsonStream stream, UTF8Encoding encoding)
    {
        var start = stream.Position;
        if (_trie.TryGetNode(stream, out var node))
        {
            if (node.HasValue)
            {
                return node.ElementName;
            }

            // The bytes read so far are only a prefix of a mapped name: decode the whole name again from its start.
            stream.Position = start;
        }

        return stream.ReadCString(encoding);
    }

    public void Inform(string name)
    {
    }
}
