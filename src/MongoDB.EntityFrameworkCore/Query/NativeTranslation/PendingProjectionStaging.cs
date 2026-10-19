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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The cross-collection state a projection binder stages while it translates leaves, applied to the query only by <see cref="CommitTo"/>
/// once every leaf has been accepted, so a later declining leaf leaves no half-registered lookup behind.
/// </summary>
internal sealed class PendingProjectionStaging
{
    /// <summary>The <c>$lookup</c>s the leaves need; registered on the query at commit.</summary>
    public List<LookupExpression> Lookups { get; } = [];

    /// <summary>Correlated-reducer leaves, each reading a staged lookup.</summary>
    public List<MongoCorrelatedReducerLeaf> ReducerLeaves { get; } = [];

    /// <summary>
    /// Lookups a projected reference-collection <c>Count</c> leaf needs stamped <see cref="LookupExpression.IsBareCountSizeSource"/>
    /// (see <c>NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup</c>).
    /// </summary>
    public List<LookupExpression> BareCountStamps { get; } = [];

    /// <summary>
    /// Element leaves (<c>c.Orders.OrderBy(..).ToList()</c>, <c>.FirstOrDefault()</c>): each one's keyed lookup and the alias the read side
    /// finds it by.
    /// </summary>
    public List<(LookupExpression Lookup, string Alias)> ElementLeaves { get; } = [];

    /// <summary>Registers everything staged on <paramref name="mongoQ"/>. Call only after every gate has passed.</summary>
    public void CommitTo(MongoQueryExpression mongoQ)
    {
        foreach (var lookup in Lookups)
            mongoQ.AddLookup(lookup);
        foreach (var reducerLeaf in ReducerLeaves)
            mongoQ.AddCorrelatedReducerLeaf(reducerLeaf);
        foreach (var lookupToStamp in BareCountStamps)
            lookupToStamp.IsBareCountSizeSource = true;
        foreach (var (elementLookup, elementAlias) in ElementLeaves)
        {
            if (!elementLookup.ElementLeafAliases.Contains(elementAlias))
                elementLookup.ElementLeafAliases.Add(elementAlias);
        }
    }
}
