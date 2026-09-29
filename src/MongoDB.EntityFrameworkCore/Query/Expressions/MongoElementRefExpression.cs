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
/// A raw reference to a document element by its (possibly dotted) path, with no associated
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>. Renders as <c>"$" + Path</c>; used e.g. to lift
/// <c>$group</c> output (<c>_id</c>, <c>_id.&lt;Name&gt;</c>, accumulator fields) into top-level aliases.
/// </summary>
internal sealed class MongoElementRefExpression(
    string path, Type clrType, bool nullSafe = false, IProperty? valueProperty = null) : MongoExpression
{
    /// <summary>
    /// <see cref="Path"/> meaning the whole current document (<c>$$ROOT</c>).
    /// </summary>
    /// <remarks>
    /// Shared by <c>NativeProjectionBinder.TryTranslateLeaf</c> (emit) and
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.IsWholeRootEntityAlias</c> (read). If they drift, the
    /// fallback null-out stops firing and only the <c>DriverLinq</c> mode fails
    /// (<c>Field 'c' required but not present</c>), which a default-mode test run would miss.
    /// </remarks>
    internal const string WholeRootDocumentPath = "$ROOT";

    /// <summary>
    /// <see cref="Path"/> meaning <c>$$REMOVE</c>. Inside a <c>$group</c> accumulator input,
    /// <c>{"$cond": [pred, value, "$$REMOVE"]}</c> makes Min/Max/Sum/Average/$push/$addToSet skip the element;
    /// a null sentinel would instead corrupt Min (null sorts below every number/date).
    /// </summary>
    internal const string RemoveSentinelPath = "$REMOVE";

    /// <summary>The (possibly dotted) element path, e.g. <c>_id</c>, <c>_id.Country</c>, or <c>Total</c>.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// When <see langword="true"/>, the renderer wraps the reference in <c>$ifNull: [..., null]</c> so a missing
    /// element (e.g. an unset owned reference) compares equal to <c>null</c>; <c>$expr</c>'s <c>$eq</c> otherwise
    /// treats missing and <c>null</c> differently. Opt-in so existing callers' MQL is unchanged.
    /// </summary>
    public bool NullSafe { get; } = nullSafe;

    /// <summary>
    /// The property backing the element's value, when the producer knows it: a correlated reducer's reduced member
    /// under <c>_lookup_&lt;Nav&gt;.&lt;Element&gt;</c> (its unchanged stored value), or the <c>DateTimeOffset</c>
    /// property whose <c>.DateTime</c> sub-field a <c>UtcDateTime</c> read reaches. Otherwise <see langword="null"/>.
    /// Lets the read side decide whether a configured <c>DateTimeKind</c> applies
    /// (<see cref="NativeTranslation.NativeDateTimeKindReadBack"/>).
    /// </summary>
    public IProperty? ValueProperty { get; } = valueProperty;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;
}
