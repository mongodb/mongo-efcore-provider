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

using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Which kind of collection a terminal native SelectMany unwinds.
/// </summary>
internal enum MongoUnwindSourceKind
{
    /// <summary>An embedded array already on the root document.</summary>
    Owned,

    /// <summary>A separate collection, unwound from its own <c>$lookup</c>'s output array.</summary>
    Reference
}

/// <summary>
/// A terminal SelectMany's collection source: the scope to <c>$unwind</c> before the result-selector
/// <c>$project</c>. Construct via <see cref="Owned"/>/<see cref="Reference"/>, which enforce that
/// <see cref="Lookup"/> is null for owned and non-null for reference sources.
/// </summary>
internal sealed class MongoUnwindSource
{
    private MongoUnwindSource(MongoUnwindSourceKind kind, string innerScopePath, IEntityType innerEntityType, LookupExpression? lookup)
    {
        Kind = kind;
        InnerScopePath = innerScopePath;
        InnerEntityType = innerEntityType;
        Lookup = lookup;
    }

    /// <summary>An owned source; <paramref name="innerScopePath"/> is the element path (e.g. <c>"Items"</c>).</summary>
    public static MongoUnwindSource Owned(string innerScopePath, IEntityType innerEntityType)
        => new(MongoUnwindSourceKind.Owned, innerScopePath, innerEntityType, lookup: null);

    /// <summary>A reference source; <paramref name="innerScopePath"/> is the <c>$lookup</c> alias
    /// (e.g. <c>"_lookup_Orders"</c>) that <paramref name="lookup"/> writes to and <c>$unwind</c> reads.</summary>
    public static MongoUnwindSource Reference(string innerScopePath, IEntityType innerEntityType, LookupExpression lookup)
        => new(MongoUnwindSourceKind.Reference, innerScopePath, innerEntityType, lookup);

    /// <summary>Owned (embedded) or reference (cross-collection).</summary>
    public MongoUnwindSourceKind Kind { get; }

    /// <summary>The scope to unwind (element path or <c>$lookup</c> alias), rendered as
    /// <c>$&lt;InnerScopePath&gt;</c>.</summary>
    public string InnerScopePath { get; }

    /// <summary>The unwound element's entity type, used to resolve member accesses in the trailing
    /// projection.</summary>
    public IEntityType InnerEntityType { get; }

    /// <summary>The <c>$lookup</c> for a reference source; <see langword="null"/> for owned.</summary>
    public LookupExpression? Lookup { get; }

    /// <summary>
    /// Set by <c>TranslateSelect</c> when the selector returns the whole inner element (<c>from o in q from i in
    /// o.Items select i</c>); the lowerer then appends a <c>$replaceRoot</c> promoting it to the root. Owned uses a
    /// <c>$mergeObjects</c> sentinel-carrying form; reference needs none, since the looked-up element is already an
    /// independently-keyed document.
    /// </summary>
    public bool WholeElement { get; set; }

    /// <summary>
    /// An inner-element-only filter (e.g. <c>o.Items.Where(i =&gt; i.Price &gt; 100)</c>), already prefixed with
    /// <see cref="InnerScopePath"/>. Emitted as a <c>$match</c> right after the <c>$unwind</c>, before the
    /// <c>$replaceRoot</c>/<c>$project</c>. <see langword="null"/> when unfiltered.
    /// </summary>
    public MongoExpression? Filter { get; set; }
}
