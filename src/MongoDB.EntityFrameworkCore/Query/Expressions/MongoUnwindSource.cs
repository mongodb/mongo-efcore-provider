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
/// Which kind of collection a terminal native SelectMany's <see cref="MongoUnwindSource"/> unwinds:
/// <see cref="Owned"/> — an embedded array already present on the root document (EF-347 slice 3/4) — or
/// <see cref="Reference"/> — a separate collection reached via a <c>$lookup</c> (EF-347 slice 5).
/// </summary>
internal enum MongoUnwindSourceKind
{
    /// <summary>An owned (embedded) collection element path, unwound directly from the root document.</summary>
    Owned,

    /// <summary>A reference (cross-collection) navigation, unwound from its own <c>$lookup</c>'s output array.</summary>
    Reference
}

/// <summary>
/// A terminal SelectMany's collection source: the document scope to <c>$unwind</c> before the result-selector
/// <c>$project</c>. EF-347 slice 3 introduced this for an owned (embedded) collection (<see cref="Kind"/> ==
/// <see cref="MongoUnwindSourceKind.Owned"/> — no <see cref="Lookup"/>); EF-347 slice 5 generalizes it to also
/// cover a reference (cross-collection) navigation (<see cref="Kind"/> == <see cref="MongoUnwindSourceKind.Reference"/>
/// — unwound from its own <c>$lookup</c>, carried in <see cref="Lookup"/>). Whole native SelectMany is
/// terminal-only, in either case. Construct via <see cref="Owned"/>/<see cref="Reference"/> rather than the
/// constructor directly — they make the <see cref="Lookup"/> invariant (null for Owned, non-null for Reference)
/// impossible to get wrong at the call site.
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

    /// <summary>An owned (embedded) collection source: <paramref name="innerScopePath"/> is the element path
    /// to unwind (e.g. <c>"Items"</c>), rendered as <c>$Items</c>.</summary>
    public static MongoUnwindSource Owned(string innerScopePath, IEntityType innerEntityType)
        => new(MongoUnwindSourceKind.Owned, innerScopePath, innerEntityType, lookup: null);

    /// <summary>A reference (cross-collection) source: <paramref name="innerScopePath"/> is the
    /// <c>$lookup</c> alias (e.g. <c>"_lookup_Orders"</c>) that <paramref name="lookup"/> writes its joined
    /// array to and that the <c>$unwind</c> reads from.</summary>
    public static MongoUnwindSource Reference(string innerScopePath, IEntityType innerEntityType, LookupExpression lookup)
        => new(MongoUnwindSourceKind.Reference, innerScopePath, innerEntityType, lookup);

    /// <summary>Whether this source is an owned (embedded) collection or a reference (cross-collection) one.</summary>
    public MongoUnwindSourceKind Kind { get; }

    /// <summary>The document scope to unwind: an owned-collection element path (<see cref="MongoUnwindSourceKind.Owned"/>,
    /// e.g. <c>"Items"</c>) or a <c>$lookup</c> alias (<see cref="MongoUnwindSourceKind.Reference"/>, e.g.
    /// <c>"_lookup_Orders"</c>) — either way, rendered as <c>$&lt;InnerScopePath&gt;</c>.</summary>
    public string InnerScopePath { get; }

    /// <summary>The inner entity type unwound from <see cref="InnerScopePath"/> — used to resolve
    /// ti.Inner member accesses to element names in the trailing SelectMany projection (EF-347 slice 4).</summary>
    public IEntityType InnerEntityType { get; }

    /// <summary>The <c>$lookup</c> this source unwinds, for <see cref="MongoUnwindSourceKind.Reference"/>;
    /// <see langword="null"/> for <see cref="MongoUnwindSourceKind.Owned"/> (no cross-collection join needed —
    /// the array is already on the root document).</summary>
    public LookupExpression? Lookup { get; }

    /// <summary>
    /// <see langword="true"/> when the trailing SelectMany selector returns the WHOLE inner element
    /// entity (e.g. <c>from o in q from i in o.Items select i</c>) rather than a member projection. Set by
    /// <c>TranslateSelect</c> once it recognizes the whole-inner-entity selector; drives the lowerer to
    /// append a <c>$replaceRoot</c> that promotes the unwound element to the root document. Owned-only in
    /// this slice (reference is deferred).
    /// </summary>
    public bool WholeElement { get; set; }
}
