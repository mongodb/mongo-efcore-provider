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
/// <see cref="Utf8NameDecoder"/> would, and is then skipped by the loop. <see cref="ReadIndex"/> reads a name and answers its position among
/// the mapped names, which the fill loop dispatches on with a jump table.
/// </summary>
/// <remarks>
/// Unlike the driver's <see cref="TrieNameDecoder{TValue}"/>, this decoder keeps no per-lookup state on the instance (no <c>Found</c>/<c>Value</c>),
/// so ONE instance is shared by every row of every execution of the compiled shaper, on any thread, and costs no allocation per document.
/// The position of the name just decoded is handed back through a thread-static the caller reads at once, with nothing in between.
/// </remarks>
internal sealed class MongoElementNameDecoder : INameDecoder
{
    private const int NotSet = -2;

    [System.ThreadStatic]
    private static int t_index;

    private readonly BsonTrie<int> _trie = new();
    private readonly Dictionary<string, int> _indexes = new(System.StringComparer.Ordinal);

    /// <param name="elementNames">The mapped names; a name's index is its position among the distinct names, in first-occurrence order.</param>
    public MongoElementNameDecoder(IEnumerable<string> elementNames)
    {
        foreach (var name in elementNames)
        {
            if (!_indexes.ContainsKey(name))
            {
                var index = _indexes.Count;
                _indexes.Add(name, index);
                _trie.Add(name, index);
            }
        }
    }

    /// <summary>The names in the order their indexes are assigned (the distinct names, first occurrence first).</summary>
    public static List<string> DistinctNames(IEnumerable<string> elementNames)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var name in elementNames)
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Reads the next element name and returns its index among <paramref name="decoder"/>'s mapped names, or -1 if it is not one.</summary>
    public static int ReadIndex(IBsonReader reader, MongoElementNameDecoder decoder)
    {
        t_index = NotSet;
        var name = reader.ReadName(decoder);
        var index = t_index;

        // A reader that is not the binary one gives the name to Inform and never calls Decode.
        return index != NotSet ? index : decoder._indexes.TryGetValue(name, out index) ? index : -1;
    }

    public string Decode(BsonStream stream, UTF8Encoding encoding)
    {
        var start = stream.Position;
        if (_trie.TryGetNode(stream, out var node))
        {
            if (node.HasValue)
            {
                t_index = node.Value;
                return node.ElementName;
            }

            // The bytes read so far are only a prefix of a mapped name: decode the whole name again from its start.
            stream.Position = start;
        }

        t_index = -1;
        return stream.ReadCString(encoding);
    }

    public void Inform(string name)
    {
    }
}
