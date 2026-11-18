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
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// A value of a projection that the cursor reads straight into a typed slot instead of building it from a <see cref="BsonDocument"/>: a root-level
/// owned collection (<c>new { o.Code, o.Lines }</c>) as a <see cref="List{T}"/> of its elements, in place of the
/// <see cref="CollectionShaperExpression"/> that built them from a <see cref="BsonArray"/>, or an entity (a leaf, the row's whole document, or
/// a list of them). It is a
/// placeholder for <see cref="MongoProjectionStreamingPlanner"/>: the binding remover turns it into a
/// <c>BsonBinding.GetStreamedValueAtElement</c> read, which the planner takes over as a slot, and which has no DOM form.
/// </summary>
internal sealed class MongoStreamedCollectionExpression(string alias, Type listType, IBsonSerializer serializer, string? missingMessage = null)
    : Expression
{
    public string Alias { get; } = alias;
    public IBsonSerializer Serializer { get; } = serializer;

    /// <summary>For an entity leaf: what a null or absent sub-document throws, or <see langword="null"/> when it reads as a null entity.</summary>
    public string? MissingMessage { get; } = missingMessage;

    public override ExpressionType NodeType => ExpressionType.Extension;
    public override Type Type { get; } = listType;
    public override bool CanReduce => false;

    protected override Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor) => this;
}

/// <summary>
/// An Include of a non-owned navigation between two values of the row (<see cref="Entity"/> and what it includes, the related entity or list), each a
/// slot the cursor reads. The fix-up the DOM shaper does as it materializes is done by the row reader once both are read, while the row's tracking is still
/// deferred, so a tracked row is wired by the state manager (see <see cref="MongoIncludeFixups"/>). The planner takes this node over; it has no DOM form.
/// </summary>
internal sealed class MongoStreamedIncludeExpression(Expression entity, Expression related, INavigation navigation, bool setLoaded) : Expression
{
    public Expression Entity { get; } = entity;
    public Expression Related { get; } = related;
    public INavigation Navigation { get; } = navigation;
    public bool SetLoaded { get; } = setLoaded;

    public override ExpressionType NodeType => ExpressionType.Extension;
    public override Type Type => Entity.Type;
    public override bool CanReduce => false;

    protected override Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor)
    {
        var visitedEntity = visitor.Visit(Entity);
        var visitedRelated = visitor.Visit(Related);
        return visitedEntity == Entity && visitedRelated == Related
            ? this
            : new MongoStreamedIncludeExpression(visitedEntity, visitedRelated, Navigation, SetLoaded);
    }
}

/// <summary>A serializer whose value is built by a shaper fragment that needs the query context (EF's materializers do).</summary>
internal interface IMongoQueryContextSerializer<out T>
{
    T Deserialize(QueryContext queryContext, BsonDeserializationContext context);
}

/// <summary>Reads a sub-document into an entity with the compiled one-pass reader.</summary>
internal sealed class MongoEntityLeafSerializer<T>(Func<QueryContext, IBsonReader, BsonDeserializationContext, T> read)
    : IBsonSerializer<T>, IMongoQueryContextSerializer<T>
{
    public Type ValueType => typeof(T);

    public T Deserialize(QueryContext queryContext, BsonDeserializationContext context)
        => read(queryContext, context.Reader, context);

    public T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => throw new NotSupportedException("Only the projection reader, which has the query context, reads this entity.");

    object? IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value)
        => throw new NotSupportedException();

    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
        => throw new NotSupportedException();
}

/// <summary>
/// Reads a BSON array of another collection's documents (a correlated subquery's <c>$lookup</c> array) into a <see cref="List{T}"/> of entities, each
/// with the compiled one-pass entity reader. The entities are constructed with their tracking deferred when the query tracks, like any entity leaf.
/// </summary>
internal sealed class MongoEntityListSerializer<T>(Func<QueryContext, IBsonReader, BsonDeserializationContext, T> read)
    : IBsonSerializer<List<T>>, IMongoQueryContextSerializer<List<T>>
{
    public Type ValueType => typeof(List<T>);

    public List<T> Deserialize(QueryContext queryContext, BsonDeserializationContext context)
    {
        var reader = context.Reader;
        var list = new List<T>();
        reader.ReadStartArray();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            list.Add(read(queryContext, reader, context));
        }

        reader.ReadEndArray();
        return list;
    }

    public List<T> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => throw new NotSupportedException("Only the projection reader, which has the query context, reads this collection.");

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<T> value)
        => throw new NotSupportedException();

    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
        => throw new NotSupportedException();
}

/// <summary>Reads a BSON array of owned documents into a <see cref="List{T}"/> with the compiled one-pass element reader.</summary>
internal sealed class MongoOwnedCollectionSerializer<TElement>(
    Func<QueryContext, IBsonReader, BsonDeserializationContext, List<TElement>> read)
    : IBsonSerializer<List<TElement>>, IMongoQueryContextSerializer<List<TElement>>
{
    public Type ValueType => typeof(List<TElement>);

    public List<TElement> Deserialize(QueryContext queryContext, BsonDeserializationContext context)
        => read(queryContext, context.Reader, context);

    public List<TElement> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => throw new NotSupportedException("Only the projection reader, which has the query context, reads this collection.");

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args);

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<TElement> value)
        => throw new NotSupportedException();

    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
        => throw new NotSupportedException();
}

/// <summary>
/// Finds the shaper of the whole root entity (a block that assigns its document variable and then tests it for null before materializing,
/// possibly inside the Includes of its references) in the injected shaper of a query that wraps the entity in client code (<c>new Dto(f)</c>).
/// </summary>
internal sealed class WholeEntityLocator(Type entityClrType) : System.Linq.Expressions.ExpressionVisitor
{
    /// <summary>The outermost entity shapers found; the caller streams only when there is exactly one.</summary>
    public List<Expression> Found { get; } = [];

    protected override Expression VisitExtension(Expression node)
    {
        if (node is IncludeExpression include && IsEntityShaper(include))
        {
            Found.Add(node);
            return node;
        }

        return base.VisitExtension(node);
    }

    protected override Expression VisitBlock(BlockExpression node)
    {
        if (IsEntityShaper(node))
        {
            Found.Add(node);
            return node;
        }

        return base.VisitBlock(node);
    }

    private bool IsEntityShaper(Expression expression)
    {
        var core = expression;
        while (core is IncludeExpression nested)
        {
            core = nested.EntityExpression;
        }

        return core is BlockExpression { Variables: [{ } documentVariable], Expressions: [BinaryExpression { NodeType: ExpressionType.Assign }, ConditionalExpression] } block
               && documentVariable.Type == typeof(BsonDocument)
               && block.Type == entityClrType;
    }
}

/// <summary>
/// Replaces the root-level owned collections of a projection's injected shaper with <see cref="MongoStreamedCollectionExpression"/>s, when
/// the collection can be read in one pass. Declines (returns <see langword="null"/>) when anything in the shaper is not understood, so the
/// caller keeps the DOM shaper.
/// </summary>
internal sealed class StreamedCollectionSubstituter : System.Linq.Expressions.ExpressionVisitor
{
    private static readonly MethodInfo CreateSerializerMethod =
        typeof(StreamedCollectionSubstituter).GetMethod(nameof(CreateSerializer), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo PopulateCollectionMethod =
        typeof(MongoProjectionBindingRemovingExpressionVisitor).GetMethod(
            nameof(MongoProjectionBindingRemovingExpressionVisitor.PopulateCollection), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IEntityType _rootEntityType;
    private readonly MongoQueryExpression _queryExpression;
    private bool _declined;

    // Whether the query tracks: an entity is then constructed with its tracking deferred, which only an entity that stands on its own can be
    // (a whole root entity, a joined one); an owned entity or collection projected without its owner is not tracked by EF either (it throws).
    private readonly bool _trackedQuery;

    private StreamedCollectionSubstituter(
        IEntityType rootEntityType, MongoQueryExpression queryExpression, bool trackedQuery, HashSet<string> unfusedAliases)
    {
        _rootEntityType = rootEntityType;
        _queryExpression = queryExpression;
        _trackedQuery = trackedQuery;
        _unfusedAliases = unfusedAliases;
    }

    // One element of the cursor is read once: an owned array that is also read whole (or through a second element Select) cannot also be read
    // through a fused element Select. Those aliases are found by a first pass and read whole, with the Select re-applied to the list.
    private readonly HashSet<string> _unfusedAliases;
    private readonly List<string> _fusedAliasReads = [];
    private readonly List<string> _wholeAliasReads = [];
    private readonly Dictionary<string, IBsonSerializer> _wholeSerializers = [];

    /// <summary>The variables declared by the replaced subtrees, which no longer exist in the shaper.</summary>
    public List<ParameterExpression> RemovedVariables { get; } = [];

    /// <summary>
    /// For each owned array an element <c>Select</c> reads in place (see <see cref="TryStreamElementSelect"/>), the alias it is projected under and the
    /// stored element names the Select needs from each element. Nothing else of the array is read, so once the shaper is in use the pipeline may
    /// project just these (<see cref="MongoSelectDefinition.TryNarrowArrayProjection"/>).
    /// </summary>
    public List<(string Alias, IReadOnlyList<string> ElementNames)> NarrowedArrays { get; } = [];

    public int Replaced { get; private set; }

    /// <summary>The shaper with its streamable owned collections replaced, or <see langword="null"/> when there are none or one cannot be read.</summary>
    public static Expression? TryRewrite(
        Expression injectedBody,
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        bool trackedQuery,
        out List<ParameterExpression> removedVariables,
        out List<(string Alias, IReadOnlyList<string> ElementNames)> narrowedArrays)
    {
        var substituter = new StreamedCollectionSubstituter(rootEntityType, queryExpression, trackedQuery, []);
        var rewritten = substituter.Visit(injectedBody);
        var shared = substituter._fusedAliasReads
            .Where(alias => substituter._wholeAliasReads.Contains(alias) || substituter._fusedAliasReads.Count(a => a == alias) > 1)
            .ToHashSet();
        if (shared.Count > 0)
        {
            substituter = new StreamedCollectionSubstituter(rootEntityType, queryExpression, trackedQuery, shared);
            rewritten = substituter.Visit(injectedBody);
        }

        removedVariables = substituter.RemovedVariables;
        narrowedArrays = substituter.NarrowedArrays;
        return substituter._declined || substituter.Replaced == 0 ? null : rewritten;
    }

    // The injector wraps every collection shaper as { bsonArrayN; bsonArrayN = <projection> as BsonArray; <CollectionShaperExpression> }.
    protected override Expression VisitBlock(BlockExpression node)
    {
        if (node is
            {
                Variables: [{ } arrayVariable],
                Expressions: [BinaryExpression { NodeType: ExpressionType.Assign, Right: UnaryExpression { NodeType: ExpressionType.TypeAs, Operand: ProjectionBindingExpression binding } }, CollectionShaperExpression shaper]
            }
            && arrayVariable.Type == typeof(BsonArray))
        {
            return TryStream(node, binding, shaper) ?? Decline(node);
        }

        // The same block over an array addressed inline, by the name of its field (a navigation's `$lookup` array).
        if (node is
            {
                Variables: [{ } inlineVariable],
                Expressions: [BinaryExpression { NodeType: ExpressionType.Assign, Right: UnaryExpression { NodeType: ExpressionType.TypeAs, Operand: IArrayProjectionExpression inlineArray } }, CollectionShaperExpression inlineShaper]
            }
            && inlineVariable.Type == typeof(BsonArray))
        {
            return TryStreamEntityCollection(node, null, inlineArray, inlineShaper) ?? Decline(node);
        }

        // An owned or joined reference with no owned references of its own is a bare block, not wrapped in an Include.
        if (node.Variables is [{ } documentVariable] && documentVariable.Type == typeof(BsonDocument)
            && TryStreamEntityLeaf(node) is { } streamedEntity)
        {
            return streamedEntity;
        }

        return base.VisitBlock(node);
    }

    // An owned or joined reference is wrapped in the Includes of its own owned references. Any other Include (a collection fixed up into a
    // navigation) keeps the DOM: its shaper is wired to the include, not to a projected value.
    protected override Expression VisitExtension(Expression node)
    {
        if (node is IncludeExpression include)
        {
            return TryStreamEntityLeaf(include) ?? TryStreamIncludeAfterRow(include) ?? Decline(node);
        }

        return base.VisitExtension(node);
    }

    // `ToList(Select(Convert(<owned collection block>), l => body))`: the binding visitor's re-application of an element Select over a
    // root-level owned collection (NativeProjectionBinder.TryMatchOwnedArraySelectLeaf). When the lambda reads only scalar members of its
    // element (MongoElementSelectorRewriter), the cursor evaluates it per element straight off the reader and yields the List<TResult>, so no
    // owned entity (nor its nested owned types) is constructed. Anything else leaves the collection to be read whole and the Select to run on it.
    protected override Expression VisitMethodCall(MethodCallExpression node)
        => !_trackedQuery && TryStreamElementSelect(node) is { } fused ? fused : base.VisitMethodCall(node);

    private Expression? TryStreamElementSelect(MethodCallExpression toList)
    {
        if (toList is not
            {
                Method: { Name: nameof(Enumerable.ToList), DeclaringType: var toListType },
                Arguments:
                [
                    MethodCallExpression
                    {
                        Method: { Name: nameof(Enumerable.Select), DeclaringType: var selectType },
                        Arguments:
                        [
                            UnaryExpression
                            {
                                NodeType: ExpressionType.Convert,
                                Operand: BlockExpression
                                {
                                    Variables: [{ } arrayVariable],
                                    Expressions:
                                    [
                                        BinaryExpression { NodeType: ExpressionType.Assign, Right: UnaryExpression { NodeType: ExpressionType.TypeAs, Operand: ProjectionBindingExpression binding } },
                                        CollectionShaperExpression shaper
                                    ]
                                } block
                            },
                            LambdaExpression { Parameters.Count: 1 } selector
                        ]
                    }
                ]
            }
            || toListType != typeof(Enumerable)
            || selectType != typeof(Enumerable)
            || arrayVariable.Type != typeof(BsonArray)
            || !TryResolveOwnedCollection(binding, shaper, out var navigation, out var alias))
        {
            return null;
        }

        if (_unfusedAliases.Contains(alias))
        {
            return null;
        }

        IBsonSerializer serializer;
        IReadOnlyList<string> elementNames;
        try
        {
            var reader = new MongoStreamingEntityMaterializerRewriter(_rootEntityType, trackedQuery: false)
                .BuildOwnedCollectionSelectReader(navigation, selector, out elementNames);
            serializer = (IBsonSerializer)CreateSerializerMethod.MakeGenericMethod(selector.ReturnType).Invoke(null, [reader.Compile()])!;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return null;
        }

        RemovedVariables.AddRange(CollectVariables(block));
        Replaced++;
        _fusedAliasReads.Add(alias);
        NarrowedArrays.Add((alias, elementNames));
        return new MongoStreamedCollectionExpression(alias, typeof(List<>).MakeGenericType(selector.ReturnType), serializer);
    }

    private static readonly MethodInfo CreateEntitySerializerMethod =
        typeof(StreamedCollectionSubstituter).GetMethod(nameof(CreateEntitySerializer), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IBsonSerializer CreateEntitySerializer<T>(Delegate reader)
        => new MongoEntityLeafSerializer<T>((Func<QueryContext, IBsonReader, BsonDeserializationContext, T>)reader);

    // `{ bsonDocN; bsonDocN = <entity projection> as BsonDocument; bsonDocN == null ? null : <materializer> }`, possibly inside the Includes of
    // the entity's owned references: an owned reference of the root, or a root reference read from its `_lookup_<Nav>` field. The entity
    // projection is a bound projection leaf or, for an entity nested in a construction, written inline.
    private Expression? TryStreamEntityLeaf(Expression leaf)
    {
        var core = leaf;
        while (core is IncludeExpression nested)
        {
            core = nested.EntityExpression;
        }

        if (core is not BlockExpression
            {
                Variables: [{ } documentVariable],
                Expressions: [BinaryExpression { NodeType: ExpressionType.Assign, Right: UnaryExpression { NodeType: ExpressionType.TypeAs } assigned }, ConditionalExpression]
            }
            || documentVariable.Type != typeof(BsonDocument)
            || _queryExpression.UsesDriverJoinFields)
        {
            return null;
        }

        string? projectedAlias = null;
        EntityProjectionExpression? entityProjection;
        if (assigned.Operand is ProjectionBindingExpression binding)
        {
            var index = binding.ProjectionMember == null
                ? binding.Index
                : _queryExpression.GetMappedProjection(binding.ProjectionMember) is ConstantExpression { Value: int mapped } ? mapped : null;
            if (index is not { } projectionIndex)
            {
                return null;
            }

            var projection = _queryExpression.Projection[projectionIndex];
            projectedAlias = projection.Alias;
            entityProjection = projection.Expression as EntityProjectionExpression;
        }
        else
        {
            // The DOM binding remover's reading of the same shape: Convert(Convert(<entity projection>)).
            entityProjection = (assigned.Operand as UnaryExpression)?.Operand is UnaryExpression { Operand: EntityProjectionExpression inline }
                ? inline
                : null;
        }

        // The whole root entity under an alias (`$$ROOT`), beside other projected values.
        if (entityProjection is { ParentAccessExpression: RootReferenceExpression { EntityType: { } wholeType } } wholeRoot
            && wholeType == _rootEntityType)
        {
            return TryStreamWholeRoot(leaf, projectedAlias ?? wholeRoot.Name);
        }

        // The inner entity of an explicit Join / LeftJoin: its whole document, staged at the root under its join level's prefix.
        if (entityProjection is { ParentAccessExpression: EntityTypeObjectAccessExpression { AccessExpression: RootReferenceExpression } innerAccess })
        {
            return TryStreamJoinInner(leaf, innerAccess.EntityType, projectedAlias ?? innerAccess.Name);
        }

        // The inner entity of a Join / LeftJoin onto a collection navigation (`Customers.Join(Orders, …)`): the unwound `$lookup`'s one document, at
        // the join's alias, which is read exactly like the inner entity of a navigation-less join.
        if (entityProjection is { ParentAccessExpression: ObjectAccessExpression { Navigation: { IsCollection: true } joinedNavigation, AccessExpression: RootReferenceExpression } joinAccess }
            && !joinedNavigation.TargetEntityType.IsOwned())
        {
            return TryStreamJoinInner(leaf, joinedNavigation.TargetEntityType, projectedAlias ?? joinAccess.Name);
        }

        if (entityProjection is not { ParentAccessExpression: ObjectAccessExpression { Navigation: { IsCollection: false } navigation, AccessExpression: RootReferenceExpression } access }
            || (navigation.DeclaringEntityType != _rootEntityType && navigation.TargetEntityType.IsOwned()))
        {
            return null;
        }

        var target = navigation.TargetEntityType;
        var owned = target.IsOwned() && navigation.IsEmbedded();
        string alias;
        string? missingMessage = null;
        if (owned)
        {
            if (_trackedQuery || !StreamingEligibility.IsEligibleOwnedElement(target))
            {
                return null;
            }

            alias = projectedAlias ?? entityProjection.Name ?? access.Name;
            if (access.Required)
            {
                missingMessage = $"Field '{alias}' required but not present in BsonDocument for a '{_rootEntityType.DisplayName()}'.";
            }
        }
        else
        {
            // A joined entity: the root-level `_lookup_<Nav>` field of a $lookup + $unwind.
            if (target.IsOwned() || navigation.IsEmbedded() || !StreamingEligibility.IsEligibleLookupElement(target))
            {
                return null;
            }

            alias = access.Name;
        }

        IBsonSerializer serializer;
        try
        {
            var rewriter = new MongoStreamingEntityMaterializerRewriter(_rootEntityType, _trackedQuery);
            var reader = owned
                ? rewriter.BuildOwnedReferenceReader(navigation, leaf)
                : rewriter.BuildLookupReferenceReader(navigation, leaf);
            serializer = (IBsonSerializer)CreateEntitySerializerMethod.MakeGenericMethod(target.ClrType).Invoke(null, [reader.Compile()])!;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return null;
        }

        RemovedVariables.AddRange(CollectVariables(leaf));
        Replaced++;
        return new MongoStreamedCollectionExpression(alias, target.ClrType, serializer, missingMessage);
    }

    // An Include of a non-owned navigation on an entity that is a slot of its own (the whole root beside other values, a join's inner entity): the
    // entity and the related entity (or list) are two slots, and the row reader does the fix-up once both are read (MongoStreamedIncludeExpression).
    private Expression? TryStreamIncludeAfterRow(IncludeExpression include)
    {
        if (include.Navigation is not INavigation navigation
            || navigation.TargetEntityType.IsOwned()
            || navigation.DeclaringEntityType.IsInHierarchy()
            || navigation.DeclaringEntityType.FindPrimaryKey() == null)
        {
            return null;
        }

        var entity = Visit(include.EntityExpression);
        var related = Visit(include.NavigationExpression);
        if (_declined || !IsStreamedEntity(entity))
        {
            return null;
        }

        // A collection comes back wrapped in the navigation's own collection type; the fix-up adds the elements to the entity's collection itself.
        if (navigation.IsCollection)
        {
            if (related is not MethodCallExpression { Method.Name: nameof(MongoProjectionBindingRemovingExpressionVisitor.PopulateCollection), Arguments: [_, _, _, MongoStreamedCollectionExpression list] })
            {
                return null;
            }

            related = list;
        }
        else if (!IsStreamedEntity(related))
        {
            return null;
        }

        return new MongoStreamedIncludeExpression(entity, related, navigation, include.SetLoaded);
    }

    private static bool IsStreamedEntity(Expression expression)
        => expression is MongoStreamedCollectionExpression or MongoStreamedIncludeExpression
           || expression is UnaryExpression { NodeType: ExpressionType.Convert, Operand: MongoStreamedCollectionExpression };

    // A join's inner document reads like a root document of the inner type; an absent or null one (an unmatched LeftJoin) is a null entity.
    private Expression? TryStreamJoinInner(Expression leaf, IEntityType innerType, string alias)
    {
        // A reference Include on the inner entity (`join o in orders.Include(o => o.Customer) ...`) is a `$lookup` of its own at the document's root,
        // not part of the joined document: the DOM shaper keeps handling it. A collection Include is the array inside the joined document.
        if (!StreamingEligibility.IsEligible(innerType, []) || MongoStreamingEntityMaterializerRewriter.IncludesNonOwnedReference(leaf))
        {
            return null;
        }

        var includes = MongoStreamingEntityMaterializerRewriter.IncludeSignature(leaf);
        if (!_plainEntityLeaves.TryGetValue((alias, innerType.ClrType, includes), out var serializer))
        {
            try
            {
                var reader = new MongoStreamingEntityMaterializerRewriter(innerType, _trackedQuery)
                    .BuildRootEntityReader(leaf, readIncludedCollections: true);
                serializer = (IBsonSerializer)CreateEntitySerializerMethod.MakeGenericMethod(innerType.ClrType).Invoke(null, [reader.Compile()])!;
            }
            catch (NativeTranslationNotSupportedException)
            {
                return null;
            }

            if (_trackedQuery)
            {
                _plainEntityLeaves[(alias, innerType.ClrType, includes)] = serializer;
            }
        }

        RemovedVariables.AddRange(CollectVariables(leaf));
        Replaced++;
        Expression streamed = new MongoStreamedCollectionExpression(alias, innerType.ClrType, serializer);

        // A derived entity set joined in (`OfType<Dog>()`) is read as its hierarchy's entity; the shaper wants the derived type.
        return leaf.Type == streamed.Type ? streamed : Expression.Convert(streamed, leaf.Type);
    }

    // The plain (no Include) entities already streamed from a sub-document, by alias and type: a tracked query that reads one twice
    // (`new { A = ti.Inner, B = ti.Inner }`) reads the document once, and both uses get the instance the state manager tracks.
    private readonly Dictionary<(string Alias, Type EntityType, string Includes), IBsonSerializer> _plainEntityLeaves = [];

    private Expression? TryStreamWholeRoot(Expression leaf, string? alias)
    {
        // The root's own included collections are arrays inside its document, as for a whole entity.
        var lookupCollections = MongoShapedQueryCompilingExpressionVisitor.GetStreamingLookupCollections(_queryExpression);
        // With no `$project` the root entity is the row's whole document.
        alias ??= MongoProjectionStreamingPlanner.WholeRowAlias;
        if (!StreamingEligibility.IsEligible(_rootEntityType, lookupCollections)
            || (_rootEntityType.FindPrimaryKey() == null && leaf is IncludeExpression))
        {
            return null;
        }

        var includes = MongoStreamingEntityMaterializerRewriter.IncludeSignature(leaf);
        if (!(_trackedQuery && _plainEntityLeaves.TryGetValue((alias, _rootEntityType.ClrType, includes), out var serializer)))
        {
            try
            {
                var reader = new MongoStreamingEntityMaterializerRewriter(
                        _rootEntityType, _trackedQuery, lookupCollections,
                        collectionAliases: MongoShapedQueryCompilingExpressionVisitor.GetStreamingCollectionAliases(_queryExpression))
                    .BuildRootEntityReader(leaf);
                serializer = (IBsonSerializer)CreateEntitySerializerMethod.MakeGenericMethod(_rootEntityType.ClrType).Invoke(null, [reader.Compile()])!;
            }
            catch (NativeTranslationNotSupportedException)
            {
                return null;
            }

            if (_trackedQuery)
            {
                _plainEntityLeaves[(alias, _rootEntityType.ClrType, includes)] = serializer;
            }
        }

        RemovedVariables.AddRange(CollectVariables(leaf));
        Replaced++;
        Expression streamed = new MongoStreamedCollectionExpression(
            alias, _rootEntityType.ClrType, serializer,
            $"Field '{alias}' required but not present in BsonDocument for a '{_rootEntityType.DisplayName()}'.");

        // A derived entity set (`OfType<Dog>()`) is read as its hierarchy's entity; the shaper wants the derived type.
        return leaf.Type == streamed.Type ? streamed : Expression.Convert(streamed, leaf.Type);
    }

    private Expression Decline(Expression node)
    {
        // A collection that cannot be streamed leaves the DOM in charge of the whole shaper.
        _declined = true;
        return node;
    }

    // An untracked, streamable owned collection of the root, projected under its own alias: its navigation and the alias it is read under.
    private bool TryResolveOwnedCollection(
        ProjectionBindingExpression binding,
        CollectionShaperExpression shaper,
        [NotNullWhen(true)] out INavigation? navigation,
        [NotNullWhen(true)] out string? alias)
    {
        navigation = null;
        alias = null;
        if (_trackedQuery
            || shaper.Navigation is not INavigation { IsCollection: true } found
            || !found.TargetEntityType.IsOwned()
            || !StreamingEligibility.IsEligibleOwnedElement(found.TargetEntityType))
        {
            return false;
        }

        var index = binding.ProjectionMember == null
            ? binding.Index
            : _queryExpression.GetMappedProjection(binding.ProjectionMember) is ConstantExpression { Value: int mapped } ? mapped : null;
        if (index is not { } projectionIndex)
        {
            return false;
        }

        // The array of the root's owned collection, or of an owned collection under a chain of the root's owned references (`b.Home.Notes`), which
        // is projected under its dotted path.
        var projection = _queryExpression.Projection[projectionIndex];
        if (projection.Alias is not { } projected
            || projection.Expression is not IArrayProjectionExpression { OwnerEntityType: { } owner, AccessExpression: var access }
            || owner != found.DeclaringEntityType)
        {
            return false;
        }

        if (access is not RootReferenceExpression)
        {
            return false;
        }

        for (var current = owner; current != _rootEntityType;)
        {
            if (current.FindOwnership() is not { IsUnique: true } ownership)
            {
                return false;
            }

            current = ownership.PrincipalEntityType;
        }

        navigation = found;
        alias = projected;
        return true;
    }

    // A list of another collection's entities (`new { c.Id, Orders = orders.Where(o => o.CustomerId == c.Id).ToList() }`): the correlated `$lookup`'s
    // array, projected under its own alias, whose elements are whole documents of the element type. It materializes into no navigation.
    private Expression? TryStreamEntityCollection(
        BlockExpression block, ProjectionBindingExpression? binding, IArrayProjectionExpression? inlineArray, CollectionShaperExpression shaper)
    {
        var index = binding == null
            ? 0
            : binding.ProjectionMember == null
                ? binding.Index
                : _queryExpression.GetMappedProjection(binding.ProjectionMember) is ConstantExpression { Value: int mapped } ? mapped : null;
        string? alias;
        IArrayProjectionExpression array;
        if (inlineArray != null)
        {
            alias = inlineArray.ArrayFieldName;
            array = inlineArray;
        }
        else if (index is { } projectionIndex
                 && _queryExpression.Projection[projectionIndex] is { Alias: { } projectedAlias, Expression: IArrayProjectionExpression projected })
        {
            alias = projectedAlias;
            array = projected;
        }
        else
        {
            return null;
        }

        if (alias == null || array.AccessExpression is not RootReferenceExpression)
        {
            return null;
        }

        // Either the filtered, ordered or paged list of a correlated subquery, which materializes into no navigation, or the collection navigation
        // itself (`new { c.Id, c.Orders }`), whose elements are another collection's documents.
        var navigation = shaper.Navigation as INavigation;
        if (navigation == null
                ? array.OwnerEntityType != null || array is not ArrayAliasProjectionExpression
                : !navigation.IsCollection || navigation.TargetEntityType.IsOwned() || navigation.DeclaringEntityType != _rootEntityType)
        {
            return null;
        }

        var elementType = array.InnerProjection.EntityType;
        if (elementType.IsOwned() || !StreamingEligibility.IsEligible(elementType))
        {
            return null;
        }

        IBsonSerializer serializer;
        try
        {
            var reader = new MongoStreamingEntityMaterializerRewriter(elementType, _trackedQuery)
                .BuildRootEntityReader(shaper.InnerShaper, readIncludedCollections: true);
            serializer = (IBsonSerializer)CreateEntityListSerializerMethod.MakeGenericMethod(elementType.ClrType).Invoke(null, [reader.Compile()])!;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return null;
        }

        RemovedVariables.AddRange(CollectVariables(block));
        Replaced++;
        var listType = typeof(List<>).MakeGenericType(elementType.ClrType);
        Expression streamed = new MongoStreamedCollectionExpression(alias, listType, serializer);
        if (navigation == null)
        {
            return streamed;
        }

        // The navigation's collection is created by its accessor, as the DOM does, so a HashSet or custom collection type is honored.
        return Expression.Call(
            PopulateCollectionMethod.MakeGenericMethod(elementType.ClrType, navigation.ClrType),
            Expression.Constant(navigation.GetCollectionAccessor()),
            Expression.Constant(navigation.DeclaringEntityType.DisplayName()),
            Expression.Constant(navigation.Name),
            streamed);
    }

    private static readonly MethodInfo CreateEntityListSerializerMethod =
        typeof(StreamedCollectionSubstituter).GetMethod(nameof(CreateEntityListSerializer), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IBsonSerializer CreateEntityListSerializer<T>(Delegate reader)
        => new MongoEntityListSerializer<T>((Func<QueryContext, IBsonReader, BsonDeserializationContext, T>)reader);

    private Expression? TryStream(BlockExpression block, ProjectionBindingExpression binding, CollectionShaperExpression shaper)
    {
        if (!TryResolveOwnedCollection(binding, shaper, out var navigation, out var alias))
        {
            return TryStreamEntityCollection(block, binding, null, shaper);
        }

        var elementClrType = navigation.TargetEntityType.ClrType;
        var listType = typeof(List<>).MakeGenericType(elementClrType);

        // One serializer per array: the array is read once, so two uses of it must agree on how.
        if (!_wholeSerializers.TryGetValue(alias, out var serializer))
        {
            try
            {
                var rewriter = new MongoStreamingEntityMaterializerRewriter(_rootEntityType, trackedQuery: false);
                var reader = rewriter.BuildOwnedCollectionReader(navigation, block);
                serializer = (IBsonSerializer)CreateSerializerMethod.MakeGenericMethod(elementClrType).Invoke(null, [reader.Compile()])!;
            }
            catch (NativeTranslationNotSupportedException)
            {
                return null;
            }

            _wholeSerializers[alias] = serializer;
        }

        RemovedVariables.AddRange(CollectVariables(block));
        Replaced++;
        _wholeAliasReads.Add(alias);

        Expression streamed = new MongoStreamedCollectionExpression(alias, listType, serializer);

        // The navigation's collection is created by its accessor, as the DOM does, so a HashSet or custom collection type is honored.
        return Expression.Call(
            PopulateCollectionMethod.MakeGenericMethod(elementClrType, navigation.ClrType),
            Expression.Constant(navigation.GetCollectionAccessor()),
            Expression.Constant(navigation.DeclaringEntityType.DisplayName()),
            Expression.Constant(navigation.Name),
            streamed);
    }


    private static IBsonSerializer CreateSerializer<TElement>(Delegate reader)
        => new MongoOwnedCollectionSerializer<TElement>((Func<QueryContext, IBsonReader, BsonDeserializationContext, List<TElement>>)reader);

    private static List<ParameterExpression> CollectVariables(Expression expression)
    {
        var collector = new VariableCollector();
        collector.Visit(expression);
        return collector.Variables;
    }

    private sealed class VariableCollector : System.Linq.Expressions.ExpressionVisitor
    {
        public List<ParameterExpression> Variables { get; } = [];

        protected override Expression VisitBlock(BlockExpression node)
        {
            Variables.AddRange(node.Variables);
            return base.VisitBlock(node);
        }
    }
}
