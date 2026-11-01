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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Rewrites EF's post-injection entity-materializer block for a streaming-eligible entity so each native-path row is
/// materialized via a single forward <see cref="IBsonReader"/> pass into typed locals instead of a <see cref="BsonDocument"/> DOM.
/// </summary>
/// <remarks>
/// Handles flat entities, reference owned sub-documents (recursively), and owned collections whose element has no navigation
/// of its own (see <see cref="BuildFillLoop"/>). A non-owned collection navigation, or an owned collection element with a further
/// navigation, is rejected upstream by <see cref="StreamingEligibility"/> and surfaces here as <see cref="NativeTranslationNotSupportedException"/>.
/// EF's construction/tracking blocks are reused verbatim, with <c>ValueBufferTryReadValue</c> reads redirected to the typed locals
/// and the <see cref="MaterializationContext"/> source replaced by <see cref="ValueBuffer.Empty"/>; <see cref="IncludeExpression"/>
/// structure (and navigation fixup) is preserved.
/// </remarks>
internal sealed class MongoStreamingEntityMaterializerRewriter
{
    private readonly IEntityType _rootEntityType;

    // Whether the query tracks its results (TrackAll or NoTrackingWithIdentityResolution). The rows are constructed while the driver
    // deserializes the batch but tracked only when read, so include fix-ups must leave the wiring to the state manager (see
    // MongoPendingTracking) instead of setting navigations on entities that may turn out to be duplicates.
    private readonly bool _trackedQuery;

    // The root's non-owned collection navigations the query includes, each read from its root-level `_lookup_<Nav>` $lookup array. The
    // query gate admits only these (see MongoShapedQueryCompilingExpressionVisitor.AllPendingLookupsAreStreamable).
    private readonly IReadOnlyCollection<INavigation> _lookupCollections;

    // The collection navigations the shaper actually includes (an IncludeExpression), found when Rewrite starts. A lookup collection the query
    // only uses in a predicate or a count has an array in the document but nothing to build from it: it is skipped like any unread element.
    private HashSet<INavigationBase> _includedCollections = [];

    // The non-owned reference navigations the shaper includes (ThenInclude off a collection Include reads them from `_lookup_<Nav>` inside each
    // element of the collection's array).
    private HashSet<INavigationBase> _includedReferences = [];

    // Every navigation the shaper includes. An owned part of a lookup's element or target is read only if the shaper includes it: EF does not
    // include them for an element of an included collection, so the DOM left them unset.
    private HashSet<INavigationBase> _includedNavigations = [];

    private void FindIncludes(Expression expression)
    {
        var finder = new IncludedCollectionFinder();
        finder.Visit(expression);
        _includedCollections = finder.Collections;
        _includedReferences = finder.References;
        _includedNavigations = finder.All;
    }


    // True while building a reader of an owned collection on its own (BuildOwnedCollectionReader): the owner's document is not part of the
    // row, so the elements' shadow owner keys, which only tracking reads, have no value to read and stand as default.
    private bool _ownerKeysAbsent;

    // The root's reference navigations that a document must have: a row whose `$lookup` found none of it is not a result, so the shaper
    // returns no entity for it (see Rewrite).
    private readonly IReadOnlyCollection<INavigation> _requiredReferences;

    // No BsonSerializerFactory dependency: each property's serializer is baked into the expression tree at
    // compile time via the static BsonSerializerFactory.GetPropertySerializationInfo (see BuildTypedRead).
    public MongoStreamingEntityMaterializerRewriter(
        IEntityType rootEntityType,
        bool trackedQuery,
        IReadOnlyCollection<INavigation>? lookupCollections = null,
        IReadOnlyCollection<INavigation>? requiredReferences = null)
    {
        _rootEntityType = rootEntityType;
        _trackedQuery = trackedQuery;
        _lookupCollections = lookupCollections ?? [];
        _requiredReferences = requiredReferences ?? [];
    }

    /// <summary>
    /// The required reference navigations (of those passed to the constructor) the rewritten shaper checks: it returns
    /// <see langword="default"/> instead of an entity for a row whose lookup found no document for one of them. Set by <see cref="Rewrite"/>.
    /// </summary>
    public IReadOnlyCollection<INavigation> RowDroppingReferences { get; private set; } = [];

    private static readonly MethodInfo ReadStartDocumentMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadStartDocument))!;

    private static readonly MethodInfo ReadEndDocumentMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadEndDocument))!;

    private static readonly MethodInfo ReadBsonTypeMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadBsonType))!;

    private static readonly MethodInfo ReadNameMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadName), [typeof(INameDecoder)])!;

    private static readonly MethodInfo GetCurrentBsonTypeMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.GetCurrentBsonType))!;

    private static readonly MethodInfo ReadNullMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadNull))!;

    private static readonly MethodInfo SkipValueMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.SkipValue))!;

    private static readonly MethodInfo DeserializeMethod =
        typeof(IBsonSerializer).GetMethod(
            nameof(IBsonSerializer.Deserialize),
            [typeof(BsonDeserializationContext), typeof(BsonDeserializationArgs)])!;

    /// <summary>
    /// A per-entity-instance materialization plan: typed locals for scalar properties, an optional "present" flag local
    /// (owned sub-documents only), and plans for its owned reference navigations.
    /// </summary>
    private sealed class EntityPlan
    {
        public required IEntityType EntityType { get; init; }
        public required Dictionary<IProperty, ParameterExpression> Locals { get; init; }

        /// <summary>
        /// Presence flag per required non-nullable scalar, set by the fill loop when the element is seen (even as BSON <c>null</c>).
        /// Still <c>false</c> after the loop means the element was missing: throws like <c>BsonBinding.GetPropertyValue</c>.
        /// </summary>
        public required Dictionary<IProperty, ParameterExpression> RequiredPresence { get; init; }

        public ParameterExpression? Present { get; init; }
        public required List<(INavigationBase Navigation, EntityPlan Child)> OwnedNavigations { get; init; }
        public required List<CollectionPlan> OwnedCollections { get; init; }

        /// <summary>
        /// For the root of a hierarchy: the properties declared by its derived types, each with its own local (in <see cref="Locals"/>). A
        /// document holds the properties of one concrete type, so a required one is checked where the materializer reads it
        /// (<see cref="ConstructionRewriter"/>), not after the fill loop.
        /// </summary>
        public required List<IProperty> DerivedProperties { get; init; }

        /// <summary>The presence flag of each required property in <see cref="DerivedProperties"/>.</summary>
        public required Dictionary<IProperty, ParameterExpression> DerivedPresence { get; init; }
        public required List<LookupReferencePlan> LookupReferences { get; init; }

        /// <summary>
        /// The property→local scope this plan's construction block reads from. The root and its owned subtree share ONE scope
        /// (owned-type keys resolve to the principal's local via <see cref="ConstructionRewriter.ResolveLocal"/>). A lookup-backed
        /// non-owned reference target gets its OWN scope: sharing the root's for a self-referential reference (e.g. <c>Employee.Manager</c>)
        /// would alias the target's locals onto the root's identical <see cref="IProperty"/> keys and corrupt both reads.
        /// </summary>
        public required Dictionary<IProperty, ParameterExpression> AllLocals { get; init; }
    }

    /// <summary>
    /// A plan for a non-owned reference navigation materialized from a cross-collection <c>$lookup</c> + <c>$unwind</c>. Unlike an owned
    /// reference, the joined sub-document is a ROOT-level element named <see cref="LookupExpression.GetLookupAlias"/> (<c>_lookup_&lt;Nav&gt;</c>).
    /// Its primary key is a normal field (no owner-key resolution) and it does its own tracking. The target plan's present flag is
    /// false when the lookup field is BSON Null (no match), yielding a null navigation.
    /// </summary>
    private sealed class LookupReferencePlan
    {
        public required INavigation Navigation { get; init; }
        public required EntityPlan Target { get; init; }
        public required string ElementName { get; init; }
    }

    /// <summary>
    /// A plan for a <em>collection</em> navigation: the element plan, a 1-based loop <see cref="Counter"/> local supplying the
    /// synthesized ordinal key, and a <see cref="List"/> accumulator local. Element locals are declared once and reassigned per iteration.
    /// An owned collection's array is the navigation's own element; a non-owned one (<see cref="LookupElementName"/> set) is the root-level
    /// <c>_lookup_&lt;Nav&gt;</c> array a <c>$lookup</c> produced, whose elements are whole entities with ordinary keys and their own locals scope.
    /// </summary>
    private sealed class CollectionPlan
    {
        public required INavigation Navigation { get; init; }
        public required EntityPlan Element { get; init; }
        public required ParameterExpression Counter { get; init; }
        public required ParameterExpression List { get; init; }

        /// <summary>The root-level element name of a non-owned collection's <c>$lookup</c> array; <see langword="null"/> for an owned collection.</summary>
        public string? LookupElementName { get; init; }

        // The rewritten per-element construction expression, set by RewriteMaterializer and consumed by BuildFillLoop for `list.Add(...)`.
        public Expression? ElementConstructor { get; set; }
    }

    private static readonly MethodInfo ReadStartArrayMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadStartArray))!;

    private static readonly MethodInfo ReadEndArrayMethod =
        typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadEndArray))!;

    // Set by Rewrite; the caller owns opening/positioning/disposing the reader. One instance serves a single Rewrite call.
    private ParameterExpression _reader = null!;

    // The per-document context from MongoEntityMaterializerSerializer.Deserialize (its Reader IS _reader), reused for every typed read,
    // including nested owned sub-documents, so no context is allocated per property.
    private ParameterExpression _context = null!;
    private readonly ParameterExpression _nameIndex = Expression.Variable(typeof(int), "__nameIndex");

    /// <summary>
    /// Rewrites the post-injection materializer into a forward-streaming one reading from
    /// <paramref name="readerParameter"/>. The caller opens, positions and disposes the reader.
    /// </summary>
    public BlockExpression Rewrite(
        Expression injectedBody, ParameterExpression readerParameter, ParameterExpression contextParameter)
    {
        _reader = readerParameter;
        _context = contextParameter;
        var resultType = injectedBody.Type;

        FindIncludes(injectedBody);

        var rootPlan = BuildPlan(_rootEntityType, present: null, new Dictionary<IProperty, ParameterExpression>(), isRootPlan: true);

        var rewrittenBody = RewriteMaterializer(injectedBody, rootPlan);

        var fillLoop = BuildFillLoop(rootPlan);

        var allLocals = new List<ParameterExpression> { _nameIndex };
        var initializers = new List<Expression>();
        CollectLocals(rootPlan, allLocals, initializers);

        var body = new List<Expression> { Expression.Call(_reader, ReadStartDocumentMethod) };
        body.AddRange(initializers);
        body.Add(fillLoop);
        body.Add(Expression.Call(_reader, ReadEndDocumentMethod));

        // The whole document is read before the row is judged, so the reader is left after it either way. A row missing a required reference is
        // dropped here rather than by a `$match` on the server (which costs about as much as the lookup it filters); the enumerator skips it.
        var missing = rootPlan.LookupReferences
            .Where(lookup => _requiredReferences.Any(required => lookup.Navigation == required || lookup.Navigation.Name == required.Name))
            .ToList();
        RowDroppingReferences = missing.Select(lookup => lookup.Navigation).ToList();
        body.Add(
            missing.Count == 0
                ? rewrittenBody
                : Expression.Condition(
                    missing.Select(lookup => (Expression)Expression.Not(lookup.Target.Present!)).Aggregate(Expression.OrElse),
                    Expression.Default(resultType),
                    rewrittenBody));

        return Expression.Block(resultType, allLocals, body);
    }

    /// <summary>
    /// Builds a reader of one owned collection of the root, for a projection that returns the collection (<c>new { o.Code, o.Lines }</c>):
    /// a lambda over the query context, the reader (positioned at the array value) and its deserialization context that returns the elements as a
    /// <see cref="List{T}"/>, empty for a null array. <paramref name="collectionExpression"/> holds the <see cref="CollectionShaperExpression"/>
    /// whose inner shaper constructs an element. Only an untracked query is supported: the elements' keys are not read from the owner.
    /// </summary>
    public LambdaExpression BuildOwnedCollectionReader(INavigation navigation, Expression collectionExpression)
    {
        _reader = Expression.Parameter(typeof(IBsonReader), "__reader");
        _context = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
        _ownerKeysAbsent = true;

        var target = navigation.TargetEntityType;
        var element = BuildPlan(target, present: null, new Dictionary<IProperty, ParameterExpression>(), allowLookupReferences: false);
        var collection = new CollectionPlan
        {
            Navigation = navigation,
            Element = element,
            Counter = Expression.Variable(typeof(int), "__counter_" + target.ShortName()),
            List = Expression.Variable(typeof(List<>).MakeGenericType(target.ClrType), "__list_" + target.ShortName())
        };

        BuildCollectionElementConstructor(collectionExpression, collection, enclosing: null);

        var locals = new List<ParameterExpression> { _nameIndex, collection.Counter, collection.List };
        var initializers = new List<Expression>
        {
            Expression.Assign(collection.Counter, Expression.Constant(0)),
            Expression.Assign(collection.List, Expression.Default(collection.List.Type))
        };
        CollectLocals(element, locals, initializers);

        var body = new List<Expression>(initializers)
        {
            BuildCollectionLoop(collection),
            // A BSON null array is consumed by the loop and leaves the accumulator null: it reads as empty, as the DOM shaper normalizes it.
            Expression.IfThen(
                Expression.Equal(collection.List, Expression.Constant(null, collection.List.Type)),
                Expression.Assign(collection.List, Expression.New(collection.List.Type))),
            collection.List
        };

        return Expression.Lambda(
            Expression.Block(collection.List.Type, locals, body), QueryCompilationContext.QueryContextParameter, _reader, _context);
    }

    /// <summary>
    /// Builds a reader of an element <c>Select</c> over one owned collection of the root (<c>o.Lines.Select(l =&gt; l.Sku).ToList()</c>): as
    /// <see cref="BuildOwnedCollectionReader"/>, but the elements are never constructed. Each element's scalar members that <paramref name="selector"/>
    /// reads are filled into locals (every other element is skipped), and the selector's body, with those members replaced by the locals, is
    /// evaluated for the <see cref="List{T}"/> of results. Throws <see cref="NativeTranslationNotSupportedException"/> when the selector reads
    /// anything but such members (see <see cref="MongoElementSelectorRewriter"/>). Only an untracked query is supported.
    /// <paramref name="elementNames"/> are the stored element names the selector reads, the only ones the reader needs from each element.
    /// </summary>
    public LambdaExpression BuildOwnedCollectionSelectReader(
        INavigation navigation, LambdaExpression selector, out IReadOnlyList<string> elementNames)
    {
        _reader = Expression.Parameter(typeof(IBsonReader), "__reader");
        _context = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
        _ownerKeysAbsent = true;

        var target = navigation.TargetEntityType;
        var full = BuildPlan(target, present: null, new Dictionary<IProperty, ParameterExpression>(), allowLookupReferences: false);

        var selectorRewriter = new MongoElementSelectorRewriter(target, full.Locals, selector.Parameters[0]);
        var result = selectorRewriter.Visit(selector.Body)!;

        // Only the members the selector reads are filled; the fill loop skips every other element, owned navigations included. A required
        // member the selector reads still throws when it is missing.
        var locals = full.Locals.Where(entry => selectorRewriter.Used.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
        elementNames = locals.Keys.Select(property => property.GetElementName()).Distinct().ToList();
        var plan = new EntityPlan
        {
            EntityType = target,
            Locals = locals,
            RequiredPresence = full.RequiredPresence.Where(entry => locals.ContainsKey(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value),
            OwnedNavigations = [],
            OwnedCollections = [],
            DerivedProperties = [],
            DerivedPresence = new Dictionary<IProperty, ParameterExpression>(),
            LookupReferences = [],
            AllLocals = locals
        };

        var listType = typeof(List<>).MakeGenericType(selector.ReturnType);
        var list = Expression.Variable(listType, "__list_select_" + target.ShortName());
        var declared = new List<ParameterExpression> { _nameIndex, list };
        var elementInitializers = new List<Expression>();
        CollectLocals(plan, declared, elementInitializers);

        var elementBreak = Expression.Label("__elemDone_select_" + target.ShortName());
        var arrayBody = Expression.Block(
            Expression.Call(_reader, ReadStartArrayMethod),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.NotEqual(
                        Expression.Call(_reader, ReadBsonTypeMethod),
                        Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                    Expression.Block(
                        // The locals are reused across elements: a member an element omits must not read the previous element's value.
                        elementInitializers.Count == 0 ? Expression.Empty() : Expression.Block(elementInitializers),
                        Expression.Call(_reader, ReadStartDocumentMethod),
                        BuildFillLoop(plan),
                        Expression.Call(_reader, ReadEndDocumentMethod),
                        Expression.Call(list, listType.GetMethod(nameof(List<object>.Add))!, result)),
                    Expression.Break(elementBreak)),
                elementBreak),
            Expression.Call(_reader, ReadEndArrayMethod));

        // A BSON null array is consumed and reads as empty, as an absent one does (the slot reader's missing state).
        var body = Expression.Block(
            listType,
            declared,
            Expression.Assign(list, Expression.New(listType)),
            Expression.IfThenElse(IsCurrentNull(), Expression.Call(_reader, ReadNullMethod), arrayBody),
            list);

        return Expression.Lambda(body, QueryCompilationContext.QueryContextParameter, _reader, _context);
    }

    /// <summary>
    /// Builds a reader of the whole root entity held as a sub-document of the row (<c>new { Item = f, f.Name.Length }</c>): the rewriter's
    /// ordinary one-document materializer, as a lambda over the query context, the reader (positioned at the sub-document) and its context.
    /// <paramref name="leafExpression"/> is the injected shaper of the entity. Untracked queries only.
    /// </summary>
    public LambdaExpression BuildRootEntityReader(Expression leafExpression)
    {
        var reader = Expression.Parameter(typeof(IBsonReader), "__reader");
        var context = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
        var body = Rewrite(leafExpression, reader, context);
        return Expression.Lambda(body, QueryCompilationContext.QueryContextParameter, reader, context);
    }

    /// <summary>
    /// Builds a reader of one owned reference of the root, for a projection that returns the entity (<c>new { o.Code, o.Shipping }</c>): a
    /// lambda over the query context, the reader (positioned at the sub-document) and its deserialization context that returns the entity.
    /// <paramref name="leafExpression"/> is the injected shaper of the reference, wrapped in the <see cref="IncludeExpression"/>s of its own
    /// owned references. Untracked queries only.
    /// </summary>
    public LambdaExpression BuildOwnedReferenceReader(INavigation navigation, Expression leafExpression)
    {
        _reader = Expression.Parameter(typeof(IBsonReader), "__reader");
        _context = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
        _ownerKeysAbsent = true;

        var present = Expression.Variable(typeof(bool), "__present_" + navigation.TargetEntityType.ShortName());
        var plan = BuildPlan(navigation.TargetEntityType, present, new Dictionary<IProperty, ParameterExpression>(), allowLookupReferences: false);
        var read = RewriteOwnedNavigation(leafExpression, navigation, plan, enclosing: null);
        return BuildEntityLeafReader(plan, read);
    }

    /// <summary>
    /// As <see cref="BuildOwnedReferenceReader"/> for a non-owned reference read from its <c>$lookup</c> field, whose entity has its own
    /// primary key stored as an ordinary element.
    /// </summary>
    public LambdaExpression BuildLookupReferenceReader(INavigation navigation, Expression leafExpression)
    {
        FindIncludes(leafExpression);

        _reader = Expression.Parameter(typeof(IBsonReader), "__reader");
        _context = Expression.Parameter(typeof(BsonDeserializationContext), "__context");

        var present = Expression.Variable(typeof(bool), "__present_lookup_" + navigation.TargetEntityType.ShortName());
        var plan = BuildPlan(
            navigation.TargetEntityType, present, new Dictionary<IProperty, ParameterExpression>(), allowLookupReferences: false, isRootPlan: true,
            onlyIncluded: true);
        var lookup = new LookupReferencePlan
        {
            Navigation = navigation,
            Target = plan,
            ElementName = LookupExpression.GetLookupAlias(navigation)
        };

        return BuildEntityLeafReader(plan, RewriteLookupReferenceNavigation(leafExpression, navigation, lookup));
    }

    // The reader sits at the sub-document (the slot reader consumed a BSON null before getting here), so the entity is always present.
    private LambdaExpression BuildEntityLeafReader(EntityPlan plan, Expression read)
    {
        var locals = new List<ParameterExpression> { _nameIndex };
        var initializers = new List<Expression>();
        CollectLocals(plan, locals, initializers);

        var body = new List<Expression>(initializers)
        {
            Expression.Assign(plan.Present!, Expression.Constant(true)),
            Expression.Call(_reader, ReadStartDocumentMethod),
            BuildFillLoop(plan),
            Expression.Call(_reader, ReadEndDocumentMethod),
            read
        };

        return Expression.Lambda(
            Expression.Block(read.Type, locals, body), QueryCompilationContext.QueryContextParameter, _reader, _context);
    }

    // The shadow foreign key to the owner of an owned type. With an explicit key (`HasKey(x => x.PostId)`) it is not part of the key, so it is
    // not an owned-type key, yet it is still never stored on the element.
    private static bool IsShadowOwnerForeignKey(IProperty property)
        => property.IsShadowProperty()
           && property.DeclaringType is IEntityType { } declaring
           && declaring.FindOwnership() is { } ownership
           && ownership.Properties.Contains(property);

    /// <summary>
    /// Builds a plan for <paramref name="entityType"/> (recursively for owned navigations). Rejects a non-owned collection navigation
    /// with <see cref="NativeTranslationNotSupportedException"/>; a non-owned navigation inside an owned collection element is rejected earlier by <see cref="StreamingEligibility"/>.
    /// </summary>
    private EntityPlan BuildPlan(
        IEntityType entityType,
        ParameterExpression? present,
        Dictionary<IProperty, ParameterExpression> allLocals,
        bool allowLookupReferences = true,
        bool readIncludedReferences = false,
        bool isRootPlan = false,
        bool onlyIncluded = false)
    {
        var locals = new Dictionary<IProperty, ParameterExpression>();
        var requiredPresence = new Dictionary<IProperty, ParameterExpression>();
        foreach (var property in entityType.GetProperties())
        {
            // Owned-type keys (shadow FKs sharing the principal's primary key) live only on the owner document: no local, no fill-loop
            // entry; reads resolve to the principal's local via ConstructionRewriter.ResolveLocal.
            if (property.IsOwnedTypeKey() || (_ownerKeysAbsent && IsShadowOwnerForeignKey(property)))
            {
                continue;
            }

            var local = Expression.Variable(property.ClrType, "__p_" + entityType.ShortName() + "_" + property.Name);
            locals[property] = local;
            allLocals[property] = local;

            // A required (non-nullable) scalar gets a presence flag so a MISSING element throws, matching the
            // DOM / driver-LINQ binding path, rather than silently materializing default(T).
            if (!property.IsNullable)
            {
                requiredPresence[property] =
                    Expression.Variable(typeof(bool), "__present_p_" + entityType.ShortName() + "_" + property.Name);
            }
        }

        // The properties of the derived types of a hierarchy's root. Eligibility admits only derived types without navigations.
        var derivedProperties = new List<IProperty>();
        var derivedPresence = new Dictionary<IProperty, ParameterExpression>();
        if (isRootPlan)
        {
            foreach (var derivedType in entityType.GetDerivedTypes())
            {
                foreach (var property in derivedType.GetDeclaredProperties())
                {
                    if (property.IsOwnedTypeKey())
                    {
                        continue;
                    }

                    var local = Expression.Variable(property.ClrType, "__p_" + derivedType.ShortName() + "_" + property.Name);
                    locals[property] = local;
                    allLocals[property] = local;
                    derivedProperties.Add(property);
                    if (!property.IsNullable)
                    {
                        var presence = Expression.Variable(typeof(bool), "__present_p_" + derivedType.ShortName() + "_" + property.Name);
                        derivedPresence[property] = presence;
                    }
                }
            }
        }

        var ownedNavigations = new List<(INavigationBase, EntityPlan)>();
        var ownedCollections = new List<CollectionPlan>();
        var lookupReferences = new List<LookupReferencePlan>();

        // The owned navigations declared by the derived types of a hierarchy's root are read with it, when the shaper includes them (a query of
        // one derived type includes only its own): a document holds those of one concrete type.
        var navigations = isRootPlan
            ? entityType.GetNavigations().Concat(
                entityType.GetDerivedTypes().SelectMany(t => t.GetDeclaredNavigations()).Where(_includedNavigations.Contains))
            : entityType.GetNavigations();
        foreach (var navigation in navigations)
        {
            var target = navigation.TargetEntityType;

            if (!target.IsOwned())
            {
                // Non-owned references are planned one level deep, off the root only: a self-referential relationship (e.g. Staff.Manager)
                // would otherwise recurse forever. A deeper (ThenInclude) reference has no LookupReferencePlan, so
                // RewriteLookupReferenceNavigation rejects it and falls back to the DOM path.
                // The element of an included lookup collection reads the references and collections the shaper includes off it (ThenInclude);
                // any other non-owned navigation of it is not read.
                if (!allowLookupReferences
                    && !(readIncludedReferences
                         && (navigation.IsCollection ? _includedCollections : _includedReferences).Contains(navigation)))
                {
                    continue;
                }

                // A non-owned navigation is streamable as a single reference backed by a cross-collection $lookup + $unwind (a root-level
                // `_lookup_<Nav>` element whose primary key is a normal field), or, when the query includes it, as a collection backed by a
                // plain $lookup (the same root-level element, an array of such entities). Any other non-owned collection is not streamable.
                if (navigation.IsCollection)
                {
                    // A collection the shaper does not include is not read: either it has no array in the document, or it has one only
                    // because the query uses it in a predicate or a count, and the fill loop skips an element it has no entry for. One off an
                    // element of an included collection (ThenInclude) is an array of that element, at its own alias, and is not a query-level lookup.
                    if (!_includedCollections.Contains(navigation) || !(_lookupCollections.Contains(navigation) || readIncludedReferences))
                    {
                        continue;
                    }

                    // Elements are independent instances: their own fresh locals scope (like a lookup reference's target), no further
                    // non-owned recursion, and no present flag (presence is per array element).
                    var lookupElement = BuildPlan(
                        target, present: null, new Dictionary<IProperty, ParameterExpression>(), allowLookupReferences: false,
                        readIncludedReferences: true, isRootPlan: true, onlyIncluded: true);
                    ownedCollections.Add(new CollectionPlan
                    {
                        Navigation = navigation,
                        Element = lookupElement,
                        Counter = Expression.Variable(typeof(int), "__counter_" + target.ShortName()),
                        List = Expression.Variable(typeof(List<>).MakeGenericType(target.ClrType), "__list_" + target.ShortName()),
                        LookupElementName = LookupExpression.GetLookupAlias(navigation)
                    });
                    continue;
                }

                // The joined target is an independent instance: it gets its own fresh locals scope, not the root's (critical for
                // self-referential references sharing IProperty keys). No further non-owned recursion.
                var lookupPresent = Expression.Variable(typeof(bool), "__present_lookup_" + target.ShortName());
                var lookupTarget = BuildPlan(
                    target, lookupPresent, new Dictionary<IProperty, ParameterExpression>(),
                    allowLookupReferences: false, isRootPlan: true, onlyIncluded: true);
                lookupReferences.Add(new LookupReferencePlan
                {
                    Navigation = navigation,
                    Target = lookupTarget,
                    ElementName = LookupExpression.GetLookupAlias(navigation)
                });
                continue;
            }

            if (onlyIncluded && !_includedNavigations.Contains(navigation))
            {
                continue;
            }

            if (navigation.IsCollection)
            {
                // Owned collection: element locals are reused across iterations (no present flag; presence is per array element). A 1-based
                // counter local supplies the synthesized ordinal key; a List<TElement> accumulator collects the elements.
                var element = BuildPlan(target, present: null, allLocals, allowLookupReferences, onlyIncluded: onlyIncluded);
                var counter = Expression.Variable(typeof(int), "__counter_" + target.ShortName());
                var listType = typeof(List<>).MakeGenericType(target.ClrType);
                var list = Expression.Variable(listType, "__list_" + target.ShortName());
                ownedCollections.Add(new CollectionPlan
                {
                    Navigation = navigation,
                    Element = element,
                    Counter = counter,
                    List = list
                });
                continue;
            }

            var childPresent = Expression.Variable(typeof(bool), "__present_" + target.ShortName());
            var child = BuildPlan(target, childPresent, allLocals, allowLookupReferences, onlyIncluded: onlyIncluded);
            ownedNavigations.Add((navigation, child));
        }

        return new EntityPlan
        {
            EntityType = entityType,
            Locals = locals,
            RequiredPresence = requiredPresence,
            Present = present,
            OwnedNavigations = ownedNavigations,
            OwnedCollections = ownedCollections,
            LookupReferences = lookupReferences,
            DerivedProperties = derivedProperties,
            DerivedPresence = derivedPresence,
            AllLocals = allLocals
        };
    }

    private void CollectLocals(EntityPlan plan, List<ParameterExpression> locals, List<Expression> initializers)
    {
        if (plan.Present != null)
        {
            locals.Add(plan.Present);
            initializers.Add(Expression.Assign(plan.Present, Expression.Constant(false)));
        }

        foreach (var local in plan.Locals.Values)
        {
            locals.Add(local);
            initializers.Add(Expression.Assign(local, Expression.Default(local.Type)));
        }

        // Required-scalar presence flags: declared + reset to false per row. The same locals are reused across
        // owned-collection iterations, so they're also re-initialized each pass inside the fill loop itself.
        foreach (var present in plan.RequiredPresence.Values)
        {
            locals.Add(present);
            initializers.Add(Expression.Assign(present, Expression.Constant(false)));
        }

        foreach (var present in plan.DerivedPresence.Values)
        {
            locals.Add(present);
            initializers.Add(Expression.Assign(present, Expression.Constant(false)));
        }

        foreach (var (_, child) in plan.OwnedNavigations)
        {
            CollectLocals(child, locals, initializers);
        }

        foreach (var lookup in plan.LookupReferences)
        {
            CollectLocals(lookup.Target, locals, initializers);
        }

        foreach (var collection in plan.OwnedCollections)
        {
            locals.Add(collection.Counter);
            initializers.Add(Expression.Assign(collection.Counter, Expression.Constant(0)));
            locals.Add(collection.List);
            initializers.Add(Expression.Assign(collection.List, Expression.Default(collection.List.Type)));

            CollectLocals(collection.Element, locals, initializers);
        }
    }

    /// <summary>
    /// Builds the forward name-dispatch fill loop for one document level (reader already after <c>ReadStartDocument</c>).
    /// </summary>
    private Expression BuildFillLoop(EntityPlan plan)
    {
        var entries = new List<(string ElementName, Expression Body)>();
        var compositeKeyParts = new List<(string ElementName, Expression Body)>();

        foreach (var property in plan.EntityType.GetProperties())
        {
            if (!plan.Locals.TryGetValue(property, out var local))
            {
                continue;
            }

            // Mark present before reading (not inside BuildTypedRead): a present-but-null required scalar must
            // take BuildTypedRead's null handling, not the post-loop missing-required throw.
            Expression read = BuildTypedRead(property, local);
            if (plan.RequiredPresence.TryGetValue(property, out var presenceFlag))
            {
                read = Expression.Block(
                    Expression.Assign(presenceFlag, Expression.Constant(true)),
                    read);
            }

            if (IsCompositeKeyPart(property))
            {
                compositeKeyParts.Add((property.GetElementName(), read));
                continue;
            }

            entries.Add((property.GetElementName(), read));
        }

        // The parts of a composite primary key are the elements of an `_id` sub-document, each under its own element name.
        if (compositeKeyParts.Count > 0)
        {
            entries.Add(("_id", BuildCompositeKeyDescent(compositeKeyParts)));
        }

        AddDerivedPropertyEntries(plan, entries);

        foreach (var (navigation, child) in plan.OwnedNavigations)
        {
            var elementName = navigation.TargetEntityType.GetContainingElementName()
                              ?? throw new NativeTranslationNotSupportedException(
                                  $"Owned navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}' has no element name.");

            entries.Add((elementName, BuildNullGuardedDescent(child)));
        }

        foreach (var lookup in plan.LookupReferences)
        {
            // The joined sub-document is a root-level `_lookup_<Nav>` element. Same null-guarded descent as an owned reference, keyed by
            // the lookup alias; BSON Null (no $lookup match) -> present = false -> null navigation.
            entries.Add((lookup.ElementName, BuildLookupReferenceDescent(lookup.Target)));
        }

        foreach (var collection in plan.OwnedCollections)
        {
            var elementName = collection.LookupElementName
                              ?? collection.Navigation.TargetEntityType.GetContainingElementName()
                              ?? throw new NativeTranslationNotSupportedException(
                                  $"Owned collection '{collection.Navigation.DeclaringEntityType.DisplayName()}.{collection.Navigation.Name}' has no element name.");

            entries.Add((elementName, BuildCollectionLoop(collection)));
        }

        var dispatch = BuildDispatch(entries);
        var breakTarget = Expression.Label("__fillDone_" + plan.EntityType.ShortName());
        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.NotEqual(
                    Expression.Call(_reader, ReadBsonTypeMethod),
                    Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    ReadNameIndex(entries),
                    dispatch),
                Expression.Break(breakTarget)),
            breakTarget);

        // Nothing to enforce or normalize after the loop: the loop is the whole fill.
        if (plan.RequiredPresence.Count == 0 && plan.OwnedCollections.Count == 0)
        {
            return loop;
        }

        // Reset presence flags and collection accumulators before each pass (owned-collection element plans reuse locals, so element N's
        // state must not leak into N+1), and enforce/normalize after. A flag still false after the loop throws like BsonBinding.GetPropertyValue.
        var body = new List<Expression>();
        foreach (var (_, presenceFlag) in plan.RequiredPresence)
        {
            body.Add(Expression.Assign(presenceFlag, Expression.Constant(false)));
        }

        foreach (var collection in plan.OwnedCollections)
        {
            body.Add(Expression.Assign(collection.List, Expression.Default(collection.List.Type)));
        }

        body.Add(loop);

        // Normalize an absent owned collection (missing, or BSON Null consumed by BuildCollectionLoop) to an empty accumulator, matching
        // the DOM shaper: a null accumulator makes IncludeCollection skip GetOrCreate, so the result would depend on the POCO's field
        // initializer. The navigation's IClrCollectionAccessor creates the real collection, so HashSet<T> etc. still work.
        foreach (var collection in plan.OwnedCollections)
        {
            body.Add(
                Expression.IfThen(
                    Expression.Equal(collection.List, Expression.Constant(null, collection.List.Type)),
                    Expression.Assign(collection.List, Expression.New(collection.List.Type))));
        }

        foreach (var (property, presenceFlag) in plan.RequiredPresence)
        {
            body.Add(
                Expression.IfThen(
                    Expression.Not(presenceFlag),
                    Expression.Throw(
                        Expression.New(
                            InvalidOperationExceptionCtor,
                            Expression.Constant(Storage.BsonBinding.RequiredPropertyMissingMessage(property))))));
        }

        return Expression.Block(body);
    }

    // A part of a composite primary key of a document root (an owned type's key is read from the owner, never stored on the element).
    private static bool IsCompositeKeyPart(IProperty property)
        => property.IsPrimaryKey()
           && property.DeclaringType is IEntityType { } entityType
           && entityType.IsDocumentRoot()
           && entityType.FindPrimaryKey()?.Properties.Count > 1;

    // Descends into the `_id` sub-document of a composite key and fills the locals of its parts. A BSON null `_id` leaves them unset, so a
    // required part is reported missing as it was when the element was absent.
    private Expression BuildCompositeKeyDescent(List<(string ElementName, Expression Body)> parts)
    {
        var breakTarget = Expression.Label("__keyDone");
        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.NotEqual(
                    Expression.Call(_reader, ReadBsonTypeMethod),
                    Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    ReadNameIndex(parts),
                    BuildDispatch(parts)),
                Expression.Break(breakTarget)),
            breakTarget);

        return Expression.IfThenElse(
            IsCurrentNull(),
            Expression.Call(_reader, ReadNullMethod),
            Expression.Block(
                Expression.Call(_reader, ReadStartDocumentMethod),
                loop,
                Expression.Call(_reader, ReadEndDocumentMethod)));
    }

    // The derived types' properties of a hierarchy root. One element name may be shared by properties of several concrete types (siblings that
    // map the same column); that is read once and handed to each, provided they store alike, otherwise the types cannot be told apart here.
    private void AddDerivedPropertyEntries(EntityPlan plan, List<(string ElementName, Expression Body)> entries)
    {
        var declaredBy = new Dictionary<string, IProperty>(StringComparer.Ordinal);
        foreach (var property in plan.EntityType.GetProperties())
        {
            declaredBy.TryAdd(property.GetElementName(), property);
        }

        foreach (var property in plan.DerivedProperties)
        {
            var local = plan.Locals[property];
            var name = property.GetElementName();
            plan.DerivedPresence.TryGetValue(property, out var presenceFlag);

            if (declaredBy.TryGetValue(name, out var first))
            {
                if (!StoredAlike(first, property))
                {
                    throw new NativeTranslationNotSupportedException(
                        $"Properties '{first.DeclaringType.DisplayName()}.{first.Name}' and '{property.DeclaringType.DisplayName()}.{property.Name}' "
                        + $"share the element '{name}' but are stored differently.");
                }

                var index = entries.FindIndex(e => e.ElementName == name);
                var firstLocal = plan.Locals[first];
                var copy = new List<Expression> { entries[index].Body, Expression.Assign(local, firstLocal.ConvertIfRequired(local.Type)) };
                if (presenceFlag != null)
                {
                    copy.Add(Expression.Assign(presenceFlag, Expression.Constant(true)));
                }

                entries[index] = (name, Expression.Block(typeof(void), copy));
                continue;
            }

            declaredBy[name] = property;
            Expression read = BuildTypedRead(property, local);
            if (presenceFlag != null)
            {
                read = Expression.Block(typeof(void), Expression.Assign(presenceFlag, Expression.Constant(true)), read);
            }

            entries.Add((name, read));
        }
    }

    // Two properties read from one element: the same CLR type, nullability and serializer, so one read answers for both.
    private static bool StoredAlike(IProperty first, IProperty second)
        => first.ClrType == second.ClrType
           && first.IsNullable == second.IsNullable
           && BsonSerializerFactory.GetPropertySerializationInfo(first).Serializer.GetType()
           == BsonSerializerFactory.GetPropertySerializationInfo(second).Serializer.GetType();

    private static readonly MethodInfo ReadNameIndexMethod =
        typeof(MongoElementNameDecoder).GetMethod(nameof(MongoElementNameDecoder.ReadIndex))!;

    // Reads the next element name into _nameIndex: its position among the entries' distinct names (the order BuildDispatch numbers its cases
    // in), or -1 for an unmapped name. The decoder is built from the same entries, so the two agree.
    private Expression ReadNameIndex(IEnumerable<(string ElementName, Expression Body)> entries)
        => Expression.Assign(
            _nameIndex,
            Expression.Call(
                ReadNameIndexMethod,
                _reader,
                Expression.Constant(new MongoElementNameDecoder(entries.Select(e => e.ElementName)), typeof(MongoElementNameDecoder))));

    // The fill loop's element-name dispatch: a jump table on the name's index (see ReadNameIndex), so the cost per element does not grow
    // with the entity's width and no name is compared; an unmapped element (-1) is skipped. When two members map to the same element name
    // the LAST one added wins, as it did when the dispatch was a chain wrapped last-first.
    private Expression BuildDispatch(List<(string ElementName, Expression Body)> entries)
    {
        var skip = (Expression)Expression.Call(_reader, SkipValueMethod);
        if (entries.Count == 0)
        {
            return skip;
        }

        var names = MongoElementNameDecoder.DistinctNames(entries.Select(e => e.ElementName));
        var cases = new List<SwitchCase>(names.Count);
        for (var index = 0; index < names.Count; index++)
        {
            var body = entries.Last(e => e.ElementName == names[index]).Body;
            cases.Add(Expression.SwitchCase(Expression.Block(typeof(void), body), Expression.Constant(index)));
        }

        return Expression.Switch(typeof(void), _nameIndex, Expression.Block(typeof(void), skip), comparison: null, cases);
    }

    private Expression IsCurrentNull()
        => Expression.Equal(
            Expression.Call(_reader, GetCurrentBsonTypeMethod),
            Expression.Constant(BsonType.Null, typeof(BsonType)));

    // Descends into an embedded sub-document read into `plan` (owned reference or joined `_lookup_<Nav>`): BSON Null means absent
    // (ReadNull + present=false); otherwise present=true and descend (ReadStartDocument / sub-fill-loop / ReadEndDocument).
    private Expression BuildNullGuardedDescent(EntityPlan plan)
        => Expression.IfThenElse(
            IsCurrentNull(),
            Expression.Block(
                Expression.Call(_reader, ReadNullMethod),
                Expression.Assign(plan.Present!, Expression.Constant(false))),
            Expression.Block(
                Expression.Assign(plan.Present!, Expression.Constant(true)),
                Expression.Call(_reader, ReadStartDocumentMethod),
                BuildFillLoop(plan),
                Expression.Call(_reader, ReadEndDocumentMethod)));

    // Descends into a `_lookup_<Nav>` field. The pipeline leaves it either as the joined document (a `$set` to the array's first element) or,
    // when the cursor does the filtering, as the `$lookup`'s own array of at most one document; both are read here. BSON Null, and an empty
    // array, are no match: present = false, as before.
    private Expression BuildLookupReferenceDescent(EntityPlan plan)
    {
        var isArray = Expression.Variable(typeof(bool), "__lookupIsArray");
        var hasDocument = Expression.Variable(typeof(bool), "__lookupHasDocument");
        var skipDone = Expression.Label("__lookupSkipDone");

        return Expression.IfThenElse(
            IsCurrentNull(),
            Expression.Block(
                Expression.Call(_reader, ReadNullMethod),
                Expression.Assign(plan.Present!, Expression.Constant(false))),
            Expression.Block(
                typeof(void),
                [isArray, hasDocument],
                Expression.Assign(
                    isArray,
                    Expression.Equal(
                        Expression.Call(_reader, GetCurrentBsonTypeMethod),
                        Expression.Constant(BsonType.Array, typeof(BsonType)))),
                Expression.IfThen(isArray, Expression.Call(_reader, ReadStartArrayMethod)),
                Expression.Assign(
                    hasDocument,
                    Expression.OrElse(
                        Expression.Not(isArray),
                        Expression.NotEqual(
                            Expression.Call(_reader, ReadBsonTypeMethod),
                            Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))))),
                Expression.Assign(plan.Present!, hasDocument),
                Expression.IfThen(
                    hasDocument,
                    Expression.Block(
                        Expression.Call(_reader, ReadStartDocumentMethod),
                        BuildFillLoop(plan),
                        Expression.Call(_reader, ReadEndDocumentMethod),
                        // A lookup that matches at most one document leaves nothing more in the array; skip whatever is there regardless.
                        // (An empty array has already reported its end, which cannot be read again.)
                        Expression.IfThen(
                            isArray,
                            Expression.Loop(
                                Expression.IfThenElse(
                                    Expression.NotEqual(
                                        Expression.Call(_reader, ReadBsonTypeMethod),
                                        Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                                    Expression.Call(_reader, SkipValueMethod),
                                    Expression.Break(skipDone)),
                                skipDone)))),
                Expression.IfThen(isArray, Expression.Call(_reader, ReadEndArrayMethod))));
    }

    /// <summary>
    /// Builds the array loop for an owned collection (reader at the array value). BSON Null is consumed and the accumulator left NULL:
    /// post-loop normalization in <see cref="BuildFillLoop"/> makes it empty, as for a MISSING array element (which never reaches here).
    /// Otherwise each element is read into the element plan's locals and appended, with the 1-based <c>counter</c> as the ordinal key.
    /// </summary>
    private Expression BuildCollectionLoop(CollectionPlan collection)
    {
        var listType = collection.List.Type;
        var addMethod = listType.GetMethod(nameof(List<object>.Add))!;
        var elementConstructor = collection.ElementConstructor
                                 ?? throw new NativeTranslationNotSupportedException(
                                     $"Owned collection '{collection.Navigation.Name}' element construction was not prepared.");

        var elementBreak = Expression.Label("__elemDone_" + collection.Element.EntityType.ShortName());

        // Element locals (scalars, owned references' presence flags and locals, nested collection state) are reused across iterations, so
        // each element starts from the same defaults a fresh row does: a missing member must not read the previous element's value.
        var elementInitializers = new List<Expression>();
        CollectLocals(collection.Element, [], elementInitializers);

        var arrayBody = Expression.Block(
            Expression.Assign(collection.Counter, Expression.Constant(0)),
            Expression.Assign(collection.List, Expression.New(listType)),
            Expression.Call(_reader, ReadStartArrayMethod),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.NotEqual(
                        Expression.Call(_reader, ReadBsonTypeMethod),
                        Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                    Expression.Block(
                        elementInitializers.Count == 0 ? Expression.Empty() : Expression.Block(elementInitializers),
                        Expression.Call(_reader, ReadStartDocumentMethod),
                        BuildFillLoop(collection.Element),
                        Expression.Call(_reader, ReadEndDocumentMethod),
                        // The ordinal key resolves to `counter + 1` (matching the DOM path's `ordinal + 1`); construct, append, then advance the counter.
                        Expression.Call(collection.List, addMethod, elementConstructor),
                        Expression.AddAssign(collection.Counter, Expression.Constant(1))),
                    Expression.Break(elementBreak)),
                elementBreak),
            Expression.Call(_reader, ReadEndArrayMethod));

        // BSON Null: consume it and leave the accumulator null; BuildFillLoop normalizes it as for a missing array.
        return Expression.IfThenElse(
            IsCurrentNull(),
            Expression.Call(_reader, ReadNullMethod),
            arrayBody);
    }

    /// <summary>
    /// Rewrites the materializer expression, preserving any <see cref="IncludeExpression"/> structure: a plain entity block is redirected
    /// to <paramref name="plan"/>'s locals; an IncludeExpression's entity block uses the parent plan and each owned navigation block its
    /// child plan, with its <c>bsonDocN == null</c> guard replaced by <c>!present</c>.
    /// </summary>
    private Expression RewriteMaterializer(
        Expression body, EntityPlan plan, IReadOnlyList<CollectionPlan>? enclosing = null)
    {
        if (body is IncludeExpression)
        {
            // A chain of includes wraps the entity, outermost first. The DOM shaper applied them in that order (its first include handles every
            // pending one, in the order it met them), so which of two failing includes throws first is the outermost's; splice them the same way.
            var chain = new List<IncludeExpression>();
            var core = body;
            while (core is IncludeExpression nested)
            {
                chain.Add(nested);
                core = nested.EntityExpression;
            }

            var entityBlock = (BlockExpression)RewriteMaterializer(core, plan, enclosing);
            foreach (var include in chain)
            {
                entityBlock = SpliceOneInclude(include, entityBlock, plan, enclosing);
            }

            return entityBlock;
        }

        // Plain entity block: { bsonDocN; bsonDocN = projection as BsonDocument; bsonDocN == null ? null : <block> }. The root row is always
        // present, so drop the local + null guard and redirect the block to this plan's locals. For a collection element, `collection` carries
        // the enclosing collection plans (innermost last) so a synthesized ordinal key resolves to its loop counter+1.
        var materializerBlock = ExtractMaterializerBlock(body, plan.EntityType);
        return new ConstructionRewriter(plan.AllLocals, enclosing, _ownerKeysAbsent, plan.DerivedPresence).Visit(materializerBlock);
    }

    // Reduces one IncludeExpression ourselves (it is not reducible). EF's binding remover would weave a fixup through a BsonDocument; there is
    // none here, so replicate the include's fixup, splicing it into the rewritten entity block before its trailing instance.
    private BlockExpression SpliceOneInclude(
        IncludeExpression include, BlockExpression entityBlock, EntityPlan plan, IReadOnlyList<CollectionPlan>? enclosing)
    {
        if (include.Navigation is not INavigation navigation)
        {
            throw new NativeTranslationNotSupportedException(
                $"Streaming materialization of navigation '{include.Navigation.Name}' is not supported.");
        }

        if (navigation.IsCollection)
        {
            var collectionPlan = FindCollectionPlan(plan, navigation);

            // Locate the CollectionShaperExpression and build the per-element construction (stored on the
            // plan for the array loop to emit); splice the fixup into the parent block, fed List<TElement>.
            BuildCollectionElementConstructor(include.NavigationExpression, collectionPlan, enclosing);

            return SpliceInclude(entityBlock, navigation, collectionPlan.List, include.SetLoaded);
        }

        // A non-owned single reference from the cross-collection $lookup field (`_lookup_<Nav>`): same IncludeExpression/fixup shape as an
        // owned reference, spliced via SpliceInclude; only the joined entity's materialization differs.
        if (!navigation.TargetEntityType.IsOwned())
        {
            var lookupPlan = FindLookupReferencePlan(plan, navigation);
            var lookupNavExpression =
                RewriteLookupReferenceNavigation(include.NavigationExpression, navigation, lookupPlan);

            return SpliceInclude(entityBlock, navigation, lookupNavExpression, include.SetLoaded);
        }

        var child = FindChildPlan(plan, navigation);
        var navExpression = RewriteOwnedNavigation(include.NavigationExpression, navigation, child, enclosing);

        return SpliceInclude(entityBlock, navigation, navExpression, include.SetLoaded);
    }

    /// <summary>
    /// Splices a navigation fixup into a rewritten entity materializer block, mirroring EF's <c>IncludeReference</c>/<c>IncludeCollection</c>.
    /// The fixup call is inserted just before the block's trailing instance expression, using its own <c>entry</c>/<c>entityType</c>/<c>instance</c>
    /// locals. For a collection, <c>relatedEntityExpression</c> is the <c>List&lt;TElement&gt;</c> local filled by the array loop.
    /// </summary>
    private BlockExpression SpliceInclude(
        BlockExpression entityBlock,
        INavigation navigation,
        Expression relatedEntityExpression,
        bool setLoaded)
    {
        // The block ends with the instance. For a navigation declared by a derived type it is typed as the type the query reads (the fixup checks
        // the concrete type at run time), so it is taken from the block rather than found by the declaring type.
        var instanceVariable = entityBlock.Expressions[^1] as ParameterExpression
                               ?? entityBlock.Variables.Single(v => v.Type == navigation.DeclaringEntityType.ClrType);
        var concreteEntityTypeVariable = entityBlock.Variables.Single(v => v.Type == typeof(IEntityType));
#pragma warning disable EF1001 // Internal EF Core API usage.
        var entryVariable = entityBlock.Variables.SingleOrDefault(v => v.Type == typeof(InternalEntityEntry));
        Expression entityEntryExpression =
            entryVariable ?? (Expression)Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        var includeCall = MongoIncludeFixups.CreateIncludeCall(
            navigation, entityEntryExpression, instanceVariable, concreteEntityTypeVariable, relatedEntityExpression,
            setLoaded,
            _trackedQuery
                ? Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(MongoQueryContext))
                : null);

        var expressions = new List<Expression>(entityBlock.Expressions);
        var trailing = expressions[^1];
        expressions[^1] = includeCall;
        expressions.Add(trailing);

        return entityBlock.Update(entityBlock.Variables, expressions);
    }

    /// <summary>
    /// Locates the <see cref="CollectionShaperExpression"/> inside an owned-collection navigation expression and builds the per-element
    /// construction expression (element locals + ordinal counter -> CLR element), storing it on <paramref name="collectionPlan"/> for <c>list.Add(...)</c>.
    /// </summary>
    private void BuildCollectionElementConstructor(
        Expression navExpression, CollectionPlan collectionPlan, IReadOnlyList<CollectionPlan>? enclosing)
    {
        // The per-element materializer; it may itself be an IncludeExpression (element owns further navigations), which RewriteMaterializer
        // handles recursively with the enclosing collection plans threaded through for the ordinal keys.
        var elementShaper = FindElementShaper(navExpression)
                            ?? throw new NativeTranslationNotSupportedException(
                                $"Unexpected owned-collection materializer shape for '{collectionPlan.Element.EntityType.DisplayName()}'.");

        collectionPlan.ElementConstructor =
            RewriteMaterializer(elementShaper, collectionPlan.Element, [.. enclosing ?? [], collectionPlan]);
    }

    /// <summary>
    /// The per-element materializer of an owned-collection navigation expression. A flat element's expression holds the
    /// <see cref="CollectionShaperExpression"/> directly; an element that owns further navigations is
    /// <c>Convert(&lt;block holding the shaper&gt;.Select(o =&gt; IncludeExpression(...)), List&lt;T&gt;)</c>, so the selector's include chain,
    /// with its parameter replaced by the shaper's inner shaper, is the element materializer.
    /// </summary>
    private static Expression? FindElementShaper(Expression navExpression)
    {
        var expression = navExpression is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : navExpression;
        if (expression is MethodCallExpression { Method.Name: nameof(Enumerable.Select), Arguments: [var source, LambdaExpression { Body: IncludeExpression } selector] })
        {
            return FindCollectionShaper(source) is { } nested
                ? ReplacingExpressionVisitor.Replace(selector.Parameters[0], nested.InnerShaper, selector.Body)
                : null;
        }

        return FindCollectionShaper(navExpression)?.InnerShaper;
    }

    private static CollectionShaperExpression? FindCollectionShaper(Expression expression)
    {
        switch (expression)
        {
            case CollectionShaperExpression shaper:
                return shaper;
            case ConditionalExpression { IfFalse: { } ifFalse } conditional:
                return FindCollectionShaper(ifFalse) ?? FindCollectionShaper(conditional.IfTrue);
            case BlockExpression block:
                for (var i = block.Expressions.Count - 1; i >= 0; i--)
                {
                    if (FindCollectionShaper(block.Expressions[i]) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case UnaryExpression unary:
                return FindCollectionShaper(unary.Operand);
            default:
                return null;
        }
    }

    private static CollectionPlan FindCollectionPlan(EntityPlan parent, INavigationBase navigation)
    {
        foreach (var collection in parent.OwnedCollections)
        {
            if (collection.Navigation == navigation || collection.Navigation.Name == navigation.Name)
            {
                return collection;
            }
        }

        throw new NativeTranslationNotSupportedException(
            $"No streaming plan for owned collection '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}'.");
    }

    /// <summary>
    /// Rewrites an owned reference navigation: the block is <c>{ bsonDocN; bsonDocN = projection as BsonDocument; return bsonDocN == null ? null : &lt;block&gt;; }</c>,
    /// wrapped in an <see cref="IncludeExpression"/> when the owned type owns further references. The value source is redirected to the
    /// child plan's locals, nested fixups are spliced in, and the null test is replaced with <c>!present</c>.
    /// </summary>
    private Expression RewriteOwnedNavigation(
        Expression navExpression, INavigation navigation, EntityPlan child, IReadOnlyList<CollectionPlan>? enclosing)
    {
        var resultType = navExpression.Type;

        // The owned type's materializer, with the fixups of whatever it owns in turn (references and collections, to any depth): the same
        // rewrite as for the root, over the child's locals (the owned subtree shares the root's scope, so owned-type keys still resolve to the
        // principal's local).
        var rewrittenBlock = (BlockExpression)RewriteMaterializer(navExpression, child, enclosing);

        // Replace the whole `{ bsonDocN; ...; bsonDocN == null ? null : <block> }` with `!present ? <absent> : <rewrittenBlock>`. The bsonDocN
        // local is dropped (its RHS is an unreduced EntityProjectionExpression). For a required owned reference, <absent> throws like the DOM
        // path (BsonBinding.GetBsonDocument) rather than yielding null.
        var absent = navigation.ForeignKey.IsRequiredDependent
            ? (Expression)Expression.Block(
                resultType,
                Expression.Throw(
                    Expression.New(
                        InvalidOperationExceptionCtor,
                        Expression.Constant(
                            $"Field '{navigation.TargetEntityType.GetContainingElementName()}' required but not present "
                            + $"in BsonDocument for a '{navigation.DeclaringEntityType.DisplayName()}'."))),
                Expression.Default(resultType))
            : Expression.Constant(null, resultType);

        return Expression.Condition(
            Expression.Not(child.Present!),
            absent,
            Expression.Convert(rewrittenBlock, resultType));
    }

    /// <summary>
    /// Rewrites a non-owned single reference navigation. The joined entity comes from the root-level <c>_lookup_&lt;Nav&gt;</c> field and is
    /// materialized from the target plan's own locals (PK read as a normal field, own tracking). The block has the same EF shape as an owned
    /// reference; its <c>bsonDocN == null</c> guard becomes <c>!present</c> (false on BSON Null, i.e. no match). Nested includes (ThenInclude)
    /// are not yet streamable.
    /// </summary>
    private Expression RewriteLookupReferenceNavigation(
        Expression navExpression,
        INavigation navigation,
        LookupReferencePlan lookup)
    {
        var resultType = navExpression.Type;

        // The joined entity's materializer over the target's own isolated locals scope: its PK is a normal local, found directly by
        // ConstructionRewriter.ResolveLocal. Sharing the root's scope would alias a self-referential reference's identical-IProperty locals.
        // Whatever the target owns is included in the same rewrite.
        var rewrittenBlock = (BlockExpression)RewriteMaterializer(navExpression, lookup.Target, enclosing: null);

        // `!present ? null : <rewrittenBlock>` — an absent (BSON Null) lookup field yields a null navigation.
        return Expression.Condition(
            Expression.Not(lookup.Target.Present!),
            Expression.Constant(null, resultType),
            Expression.Convert(rewrittenBlock, resultType));
    }

    // Collects the collection navigations of every IncludeExpression in a shaper (an IncludeExpression is an extension node, so the base
    // visitor walks into its entity and navigation expressions).
    private sealed class IncludedCollectionFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public HashSet<INavigationBase> Collections { get; } = [];

        public HashSet<INavigationBase> References { get; } = [];

        public HashSet<INavigationBase> All { get; } = [];

        protected override Expression VisitExtension(Expression node)
        {
            if (node is IncludeExpression { Navigation: INavigationBase any })
            {
                All.Add(any);
            }

            if (node is IncludeExpression { Navigation: { IsCollection: true } navigation })
            {
                Collections.Add(navigation);
            }
            else if (node is IncludeExpression { Navigation: INavigationBase reference })
            {
                References.Add(reference);
            }

            return base.VisitExtension(node);
        }
    }

    private static readonly ConstructorInfo InvalidOperationExceptionCtor =
        typeof(InvalidOperationException).GetConstructor([typeof(string)])!;

    private static LookupReferencePlan FindLookupReferencePlan(EntityPlan parent, INavigationBase navigation)
    {
        foreach (var lookup in parent.LookupReferences)
        {
            if (lookup.Navigation == navigation || lookup.Navigation.Name == navigation.Name)
            {
                return lookup;
            }
        }

        throw new NativeTranslationNotSupportedException(
            $"No streaming plan for lookup reference navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}'.");
    }

    private static EntityPlan FindChildPlan(EntityPlan parent, INavigationBase navigation)
    {
        foreach (var (nav, child) in parent.OwnedNavigations)
        {
            if (nav == navigation || nav.Name == navigation.Name)
            {
                return child;
            }
        }

        throw new NativeTranslationNotSupportedException(
            $"No streaming plan for owned navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}'.");
    }

    /// <summary>
    /// Extract the always-present materializer block from EF's injected
    /// <c>{ bsonDocN; bsonDocN = projection as BsonDocument; bsonDocN == null ? null : &lt;block&gt; }</c>.
    /// </summary>
    private BlockExpression ExtractMaterializerBlock(Expression body, IEntityType entityType)
    {
        if (body is not BlockExpression injectedBlock)
        {
            throw new NativeTranslationNotSupportedException(
                $"Unexpected materializer shape for entity '{entityType.DisplayName()}'.");
        }

        var last = injectedBlock.Expressions[^1];
        if (last is ConditionalExpression { IfFalse: BlockExpression materializerBlock })
        {
            return materializerBlock;
        }

        if (last is BlockExpression directBlock)
        {
            return directBlock;
        }

        // Collection-element block: always present (no `bsonDocN == null ? null` guard), so EF's injected block is itself the materializer
        // block (ends with the instance variable, not a conditional). Use it directly.
        if (injectedBlock.Variables.Any(v => v.Type == typeof(MaterializationContext)))
        {
            return injectedBlock;
        }

        throw new NativeTranslationNotSupportedException(
            $"Unexpected materializer shape for entity '{entityType.DisplayName()}'.");
    }

    /// <summary>
    /// Reads the value at the reader's current position via the property's serializer into <paramref name="local"/>.
    /// </summary>
    /// <remarks>
    /// An explicit BSON <c>null</c> is consumed. A non-nullable value type is left at <c>default(T)</c>, matching <see cref="Storage.BsonBinding"/>
    /// (whose null check can never fire for an unconstrained value-typed <c>T</c>); a non-nullable reference type throws the same
    /// <see cref="InvalidOperationException"/>, since <c>default(T)</c> would be an invalid null. A missing element is handled by the fill loop.
    /// </remarks>
    private Expression BuildTypedRead(IProperty property, ParameterExpression local)
    {
        var serializer = BsonSerializerFactory.GetPropertySerializationInfo(property).Serializer;

        // Reuse the per-row context rather than allocating one per property. Prefer the generic
        // IBsonSerializer<TValue>.Deserialize (no boxing); otherwise use the non-generic one + Convert.
        var valueType = serializer.ValueType;
        var genericSerializerType = typeof(IBsonSerializer<>).MakeGenericType(valueType);

        // As the driver's own Deserialize(context) extension: the nominal type is the serializer's value type. A class-map serializer (a custom
        // struct or class value) needs it to find the actual type.
        var nominalArgs = Expression.Constant(new BsonDeserializationArgs { NominalType = valueType }, typeof(BsonDeserializationArgs));

        Expression deserialize;
        if (genericSerializerType.IsInstanceOfType(serializer))
        {
            var typedDeserialize = genericSerializerType.GetMethod(
                nameof(IBsonSerializer.Deserialize),
                [typeof(BsonDeserializationContext), typeof(BsonDeserializationArgs)])!;

            Expression typedCall = Expression.Call(
                Expression.Constant(serializer, genericSerializerType),
                typedDeserialize,
                _context,
                nominalArgs);

            deserialize = typedCall.ConvertIfRequired(local.Type);
        }
        else
        {
            Expression boxedCall = Expression.Call(
                Expression.Constant(serializer, typeof(IBsonSerializer)),
                DeserializeMethod,
                _context,
                nominalArgs);

            deserialize = Expression.Convert(boxedCall, local.Type);
        }

        var readAssign = Expression.Assign(local, deserialize);

        Expression onNull = !property.IsNullable && !local.Type.IsValueType
            ? Expression.Throw(
                Expression.New(
                    InvalidOperationExceptionCtor,
                    Expression.Constant(Storage.BsonBinding.RequiredPropertyNullMessage(property))))
            : Expression.Assign(local, Expression.Default(local.Type));

        return Expression.IfThenElse(
            IsCurrentNull(),
            Expression.Block(
                Expression.Call(_reader, ReadNullMethod),
                onNull),
            readAssign);
    }

    /// <summary>
    /// Rewrites an EF construction block so <c>ValueBufferTryReadValue</c> reads become the property's streaming
    /// local (<c>Convert</c>-wrapped if the type differs) and the <c>MaterializationContext</c> source becomes
    /// <c>ValueBuffer.Empty</c>.
    /// </summary>
    private sealed class ConstructionRewriter : System.Linq.Expressions.ExpressionVisitor
    {
        private readonly Dictionary<IProperty, ParameterExpression> _locals;
        private readonly IReadOnlyList<CollectionPlan> _enclosing;
        private readonly bool _ownerKeysAbsent;

        public ConstructionRewriter(
            Dictionary<IProperty, ParameterExpression> locals, IReadOnlyList<CollectionPlan>? enclosing = null, bool ownerKeysAbsent = false,
            Dictionary<IProperty, ParameterExpression>? derivedPresence = null)
        {
            _locals = locals;
            _enclosing = enclosing ?? [];
            _ownerKeysAbsent = ownerKeysAbsent;
            _derivedPresence = derivedPresence;
        }

        private readonly Dictionary<IProperty, ParameterExpression>? _derivedPresence;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            if (method.IsGenericMethod
                && method.GetGenericMethodDefinition() == ExpressionExtensions.ValueBufferTryReadValueMethod)
            {
                var property = node.Arguments[2].GetConstantValue<IProperty>();

                // The synthesized owned-collection ordinal key isn't stored in BSON: it's the loop counter + 1 (matching the DOM path's `ordinal + 1`).
                // A key of a type owned by an element (reference or nested collection) is a copy of the enclosing element's ordinal key, so
                // follow the principal chain to the ordinal key of the enclosing collection plan it ends at.
                if (TryResolveOrdinal(property) is { } ordinal)
                {
                    return ordinal.ConvertIfRequired(node.Type);
                }

                var local = ResolveLocal(property);
                if (local != null)
                {
                    // A required property of a derived type is missing from the document of a type that does not have it, so it is checked here, where
                    // the type that does have it reads it, and throws as the DOM binding did.
                    if (_derivedPresence != null && _derivedPresence.TryGetValue(property, out var presence))
                    {
                        return Expression.Condition(
                            presence,
                            local.ConvertIfRequired(node.Type),
                            Expression.Throw(
                                Expression.New(
                                    InvalidOperationExceptionCtor,
                                    Expression.Constant(Storage.BsonBinding.RequiredPropertyMissingMessage(property))),
                                node.Type));
                    }

                    return local.ConvertIfRequired(node.Type);
                }

                // EF constructs the instance only when every key it reads is non-null, so the stand-in is a boxed default; a key type
                // whose default is null (a string) has none.
                if (_ownerKeysAbsent && (property.IsOwnedTypeKey() || IsShadowOwnerForeignKey(property))
                    && property.IsShadowProperty() && property.ClrType.IsValueType)
                {
                    return Expression.Convert(Expression.Default(property.ClrType), node.Type);
                }

                throw new NativeTranslationNotSupportedException(
                    $"Streaming materializer found a value read for property '{property.Name}' with no streaming local.");
            }

            return base.VisitMethodCall(node);
        }

        private Expression? TryResolveOrdinal(IProperty property)
        {
            var current = property;
            while (true)
            {
                if (current.IsOwnedTypeOrdinalKey())
                {
                    // Innermost first: the nearest enclosing collection whose element declares this key.
                    for (var i = _enclosing.Count - 1; i >= 0; i--)
                    {
                        if (_enclosing[i].Element.EntityType == current.DeclaringType)
                        {
                            return Expression.Add(_enclosing[i].Counter, Expression.Constant(1));
                        }
                    }

                    return null;
                }

                if (!current.IsOwnedTypeKey() || current.FindFirstPrincipal() is not { } principal || principal == current)
                {
                    return null;
                }

                current = principal;
            }
        }

        /// <summary>
        /// Resolves a read property to its streaming local. An owned-type key (shadow FK sharing the principal's primary key, stored only on
        /// the owner document) is redirected to its principal property's local, as the DOM binding remover does via <c>FindFirstPrincipal</c>.
        /// </summary>
        private ParameterExpression? ResolveLocal(IProperty property)
        {
            if (_locals.TryGetValue(property, out var local))
            {
                return local;
            }

            var current = property;
            while (current.IsOwnedTypeKey() && current.FindFirstPrincipal() is { } principal && principal != current)
            {
                if (_locals.TryGetValue(principal, out var principalLocal))
                {
                    return principalLocal;
                }

                current = principal;
            }

            return null;
        }

        // A get-only property's readonly backing field cannot be rebuilt by Expression.Assign: EF's own helper assigns it.
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Assign && node.Left is MemberExpression { Member: FieldInfo { IsInitOnly: true } } readonlyField)
            {
                return readonlyField.Assign(Visit(node.Right));
            }

            return base.VisitBinary(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (node.Type == typeof(MaterializationContext))
            {
                return Expression.New(
                    node.Constructor!,
                    Expression.Constant(ValueBuffer.Empty),
                    Visit(node.Arguments[1]));
            }

            return base.VisitNew(node);
        }
    }
}
