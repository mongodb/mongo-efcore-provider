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
/// A deliberate SIBLING of <see cref="MongoFieldExpression"/> rather than that node reused with a
/// null/placeholder <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/> — <see cref="LookupAlias"/>
/// names a SYNTHETIC top-level field (<c>_lookup_&lt;Navigation&gt;</c>) that a <c>$lookup</c>+<c>$unwind</c>
/// (with <c>preserveNullAndEmptyArrays: true</c>) materializes as an entire joined sub-document or an explicit
/// <see langword="null"/> when no match existed, never a stored scalar property with a getter/value-converter
/// of its own. Query-dialect only (<c>{ alias: null }</c> / <c>{ alias: { $ne: null } }</c>) — there is no
/// aggregation-expression form because there is nothing else this node needs to express.
/// <para>
/// Produced only by <c>NativeJoinScopeTranslator.TryTranslateReferenceIncludeNullCheck</c>, recognizing the
/// EXACT shape <c>ti.Inner == null</c>/<c>!= null</c> (<c>ti</c> the join's own TransparentIdentifier
/// parameter) at the top of a <c>Where</c> predicate — never nested under a <c>Not</c>, quantifier, or
/// <c>$elemMatch</c>, so neither <see cref="Microsoft.EntityFrameworkCore.Metadata"/>-adjacent negation
/// machinery (<c>MongoExpressionNegator</c>) nor <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>
/// (nesting inside <c>$elemMatch</c>) need to recognize it; both fail closed on it via their existing
/// catch-alls, correctly declining rather than mis-rendering.
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
