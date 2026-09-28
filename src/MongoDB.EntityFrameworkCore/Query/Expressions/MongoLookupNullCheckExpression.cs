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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Tests whether a single-level reference-<c>Include</c>'s <c>$lookup</c> alias field is present
/// (<c>e.Manager != null</c>) or absent (<c>e.Manager == null</c>), post-<c>$unwind</c>.
/// </summary>
/// <remarks>
/// A sibling of <see cref="MongoFieldExpression"/> rather than one with a placeholder
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>: <see cref="LookupAlias"/> names a synthetic
/// top-level field (<c>_lookup_&lt;Navigation&gt;</c>) that <c>$lookup</c>+<c>$unwind</c>
/// (<c>preserveNullAndEmptyArrays: true</c>) fills with the joined sub-document or <see langword="null"/>, not a
/// stored property with its own serializer. Renders in the query dialect (<c>RenderLookupNullCheck</c>, Where
/// position) and the aggregation dialect (Select-side conditional Test).
/// <para>
/// Produced by <c>NativeJoinScopeTranslator.TryMatchInnerNullCheck</c> (depth-1, Where only) and
/// <c>TryMatchScopeNullCheck</c> (any depth, Select side) for exactly <c>ti.Inner == null</c>/<c>!= null</c>.
/// Never nested under <c>Not</c>, a quantifier, or <c>$elemMatch</c>, so <c>MongoExpressionNegator</c> and
/// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> fail closed on it via their catch-alls.
/// </para>
/// </remarks>
internal sealed class MongoLookupNullCheckExpression(string lookupAlias, bool isNotNull) : MongoExpression
{
    /// <summary>The <c>$lookup</c>'s own <c>as</c> alias (e.g. <c>_lookup_Manager</c>).</summary>
    public string LookupAlias { get; } = lookupAlias;

    /// <summary>
    /// <see langword="true"/> for <c>!= null</c> (the joined document is present), <see langword="false"/>
    /// for <c>== null</c> (no match).
    /// </summary>
    public bool IsNotNull { get; } = isNotNull;

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
