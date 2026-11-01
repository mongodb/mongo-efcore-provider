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
/// A root-level owned collection of a projection (<c>new { o.Code, o.Lines }</c>) that the cursor reads straight into a <see cref="List{T}"/>
/// of its elements, in place of the <see cref="CollectionShaperExpression"/> that built them from a <see cref="BsonArray"/>. It is a
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

    private StreamedCollectionSubstituter(IEntityType rootEntityType, MongoQueryExpression queryExpression, bool trackedQuery)
    {
        _rootEntityType = rootEntityType;
        _queryExpression = queryExpression;
        _trackedQuery = trackedQuery;
    }

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
        var substituter = new StreamedCollectionSubstituter(rootEntityType, queryExpression, trackedQuery);
        var rewritten = substituter.Visit(injectedBody);
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
            return TryStreamEntityLeaf(include) ?? Decline(node);
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

        if (entityProjection is not { ParentAccessExpression: ObjectAccessExpression { Navigation: { IsCollection: false } navigation, AccessExpression: RootReferenceExpression } access }
            || navigation.DeclaringEntityType != _rootEntityType)
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

    // A join's inner document reads like a root document of the inner type; an absent or null one (an unmatched LeftJoin) is a null entity.
    private Expression? TryStreamJoinInner(Expression leaf, IEntityType innerType, string alias)
    {
        // An Include on the inner entity (`join o in orders.Include(o => o.Customer) ...`) is not read from the joined document: the DOM shaper
        // keeps handling it exactly as it did.
        if (leaf is IncludeExpression || !StreamingEligibility.IsEligible(innerType))
        {
            return null;
        }

        IBsonSerializer serializer;
        try
        {
            var reader = new MongoStreamingEntityMaterializerRewriter(innerType, _trackedQuery).BuildRootEntityReader(leaf);
            serializer = (IBsonSerializer)CreateEntitySerializerMethod.MakeGenericMethod(innerType.ClrType).Invoke(null, [reader.Compile()])!;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return null;
        }

        RemovedVariables.AddRange(CollectVariables(leaf));
        Replaced++;
        return new MongoStreamedCollectionExpression(alias, innerType.ClrType, serializer);
    }

    private Expression? TryStreamWholeRoot(Expression leaf, string? alias)
    {
        if (alias == null || !StreamingEligibility.IsEligible(_rootEntityType))
        {
            return null;
        }

        IBsonSerializer serializer;
        try
        {
            var reader = new MongoStreamingEntityMaterializerRewriter(_rootEntityType, _trackedQuery).BuildRootEntityReader(leaf);
            serializer = (IBsonSerializer)CreateEntitySerializerMethod.MakeGenericMethod(_rootEntityType.ClrType).Invoke(null, [reader.Compile()])!;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return null;
        }

        RemovedVariables.AddRange(CollectVariables(leaf));
        Replaced++;
        return new MongoStreamedCollectionExpression(
            alias, _rootEntityType.ClrType, serializer,
            $"Field '{alias}' required but not present in BsonDocument for a '{_rootEntityType.DisplayName()}'.");
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
            || found.DeclaringEntityType != _rootEntityType
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

        var projection = _queryExpression.Projection[projectionIndex];
        if (projection.Alias is not { } projected
            || projection.Expression is not IArrayProjectionExpression { OwnerEntityType: { } owner, AccessExpression: RootReferenceExpression }
            || owner != _rootEntityType)
        {
            return false;
        }

        navigation = found;
        alias = projected;
        return true;
    }

    private Expression? TryStream(BlockExpression block, ProjectionBindingExpression binding, CollectionShaperExpression shaper)
    {
        if (!TryResolveOwnedCollection(binding, shaper, out var navigation, out var alias))
        {
            return null;
        }

        var elementClrType = navigation.TargetEntityType.ClrType;
        var listType = typeof(List<>).MakeGenericType(elementClrType);

        IBsonSerializer serializer;
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

        RemovedVariables.AddRange(CollectVariables(block));
        Replaced++;

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
