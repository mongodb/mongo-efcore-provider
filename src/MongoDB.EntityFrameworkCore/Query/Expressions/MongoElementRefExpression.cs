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
/// A raw reference to a document element by its (possibly dotted) path, with no associated
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>.
/// </summary>
/// <remarks>
/// Used by the native <c>$group</c> flattening <c>$project</c> to lift grouped output (<c>_id</c>, a
/// composite <c>_id.&lt;Name&gt;</c> sub-key, or an accumulator field) into a top-level result alias.
/// Renders in the aggregation-expression dialect as <c>"$" + Path</c>.
/// </remarks>
internal sealed class MongoElementRefExpression(string path, Type clrType, bool nullSafe = false) : MongoExpression
{
    /// <summary>
    /// The <see cref="Path"/> spelling that means "the WHOLE current document", i.e. the aggregation system
    /// variable <c>$$ROOT</c> — rendered as <c>"$" + "$ROOT"</c> by the ordinary <c>"$" + Path</c> rule, so no
    /// special-casing is needed in the renderer.
    /// </summary>
    /// <remarks>
    /// SHARED between the emit side (<c>NativeProjectionBinder.TryTranslateLeaf</c>, which constructs the node
    /// for a whole-root-entity projection leaf) and the read side
    /// (<c>MongoProjectionBindingRemovingExpressionVisitor.IsWholeRootEntityAlias</c>, which recognizes it to
    /// null out the alias on the FALLBACK leg). Those two must agree EXACTLY, and the failure mode if they
    /// drift is asymmetric enough to be worth a named constant: the read side simply stops matching, the
    /// fallback null-out silently stops firing, and the explicit <c>DriverLinq</c> leg regresses to
    /// <c>Field 'c' required but not present in BsonDocument</c> — loud, but only on the non-default query
    /// mode, so a default-mode-only test run would not see it.
    /// </remarks>
    internal const string WholeRootDocumentPath = "$ROOT";

    /// <summary>The (possibly dotted) element path, e.g. <c>_id</c>, <c>_id.Country</c>, or <c>Total</c>.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// When <see langword="true"/>, the renderer wraps the reference in <c>$ifNull</c> against a literal
    /// <c>null</c> before use, so a MISSING element (an unset owned single-reference navigation, e.g.) reads
    /// the same as an explicitly-stored <c>null</c> one. Needed for an owned-nav null-equality check
    /// (<c>b.Address == null</c>): unlike <see cref="WholeRootDocumentPath"/> — which can never actually be
    /// missing, since it names the current document itself — a real element path can be entirely absent from
    /// the stored document, and <c>$expr</c>'s <c>$eq</c> does NOT treat a missing field the same as an
    /// explicit <c>null</c> (unlike the ORDINARY query-dialect <c>{field: null}</c>, which matches both). Kept
    /// as an opt-in flag (mirroring <see cref="MongoSizeExpression.NullSafe"/>'s own precedent) rather than
    /// applied unconditionally, so the pre-existing <c>WholeRootDocumentPath</c> callers' emitted MQL is
    /// unaffected.
    /// </summary>
    public bool NullSafe { get; } = nullSafe;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;
}
