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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// EF-337: the driver compares, sorts and reduces a property's stored BSON, so over a value-converted or
// non-default-represented property a relational comparison, OrderBy/ThenBy key or Sum/Min/Max/Average silently answers
// on the stored form ("9" > "100", $sum of strings is 0). The native translator declines these shapes, which only moves
// them here, so this path refuses them loudly. The property tests are StoredOrdering.PreservesClrOrdering (comparison)
// and StoredOrdering.PreservesClrOrderingForAggregateAndSort, shared with the native declines. Equality (==, !=,
// Contains) is exact whatever the storage and is left alone.
//
// The whole key/operand is walked, not just a bare member: through casts, ??, ?:, arithmetic, members of a projected
// (anonymous/DTO) element, g.Key back to the GroupBy key selector, and Concat/Union sources. A construct the walk can't
// classify is refused when the query reads any such property at all, so an unknown shape never passes silently.
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor
{
    private enum StoredOrderingUse
    {
        Comparison,
        SortOrAggregate
    }

    // Lambda parameter -> the sequence(s) whose elements it ranges over; collected once per query root.
    private Dictionary<ParameterExpression, Expression[]>? _storedOrderingParameterSources;

    // The captured query being translated (and its rewritten form); scanned when a key/operand can't be classified.
    private Expression[] _storedOrderingQueryRoots = [];

    private void SetStoredOrderingQueryRoots(params Expression?[] roots)
    {
        _storedOrderingQueryRoots = roots.OfType<Expression>().Distinct().ToArray();
        _storedOrderingParameterSources = null;
    }

    private static bool IsRelationalOperator(ExpressionType nodeType)
        => nodeType is ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    // x.P > v (either side, any computed operand), and string.Compare(x.P, v) > 0 / x.P.CompareTo(v) > 0.
    private void ThrowIfRelationalComparisonOverStoredOrdering(BinaryExpression node)
    {
        if (!IsRelationalOperator(node.NodeType))
        {
            return;
        }

        foreach (var operand in new[] { node.Left, node.Right })
        {
            if (StripConverts(operand) is MethodCallExpression
                {
                    Method.Name: nameof(string.Compare) or nameof(IComparable.CompareTo)
                } compare)
            {
                if (compare.Object is { } receiver)
                {
                    ThrowIfReadsStoredOrdering(receiver, StoredOrderingUse.Comparison, "comparison");
                }

                foreach (var argument in compare.Arguments)
                {
                    ThrowIfReadsStoredOrdering(argument, StoredOrderingUse.Comparison, "comparison");
                }
            }
            else
            {
                ThrowIfReadsStoredOrdering(operand, StoredOrderingUse.Comparison, "comparison");
            }
        }
    }

    // OrderBy/ThenBy keys, Sum/Min/Max/Average/MinBy/MaxBy selectors (Queryable or Enumerable, including the
    // selector-less forms, which reduce the source's element).
    private void ThrowIfOrderingOrAggregateOverStoredOrdering(MethodCallExpression node)
    {
        if (node.Method.DeclaringType != typeof(Queryable) && node.Method.DeclaringType != typeof(Enumerable))
        {
            return;
        }

        switch (node.Method.Name)
        {
            case nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)
                when node.Arguments.Count >= 2:
            case nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max) or nameof(Queryable.Average)
                or nameof(Enumerable.MinBy) or nameof(Enumerable.MaxBy)
                when node.Arguments.Count >= 2:
                if (UnwrapLambda(node.Arguments[1]) is { } selector)
                {
                    ThrowIfReadsStoredOrdering(selector.Body, StoredOrderingUse.SortOrAggregate, node.Method.Name);
                }

                break;

            case nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max) or nameof(Queryable.Average)
                when node.Arguments.Count == 1:
                // Selector-less: the reduced value is the source's element.
                if (FindElementExpressions(node.Arguments[0]) is { } elements)
                {
                    foreach (var element in elements)
                    {
                        ThrowIfReadsStoredOrdering(element, StoredOrderingUse.SortOrAggregate, node.Method.Name);
                    }
                }
                else
                {
                    ThrowIfQueryReadsStoredOrdering(node.Arguments[0], StoredOrderingUse.SortOrAggregate, node.Method.Name);
                }

                break;
        }
    }

    private void ThrowIfReadsStoredOrdering(Expression expression, StoredOrderingUse use, string operation)
        => new StoredOrderingWalker(this, use, operation).Walk(expression);

    private void ThrowStoredOrdering(IProperty property, string operation)
        => throw new NotSupportedException(
            $"'{operation}' over the property '{property.DeclaringType.DisplayName()}.{property.Name}' cannot be "
            + "translated to a MongoDB query: the property is stored through a value converter or a BsonRepresentation "
            + "whose stored values do not compare, sort or aggregate like the .NET values, so the server would return "
            + "wrong results. Only equality comparisons are supported on such a property; evaluate this operation on "
            + "the client, for example after AsEnumerable().");

    // The walk couldn't classify `scope`: refuse if any property that can reach it has a differing stored ordering.
    // Reachable = read inside `scope`, or inside the source sequence(s) of a lambda parameter used in it (recursively).
    // Only a parameter with no recorded source falls back to the whole query.
    private void ThrowIfQueryReadsStoredOrdering(Expression scope, StoredOrderingUse use, string operation)
    {
        var finder = new StoredOrderingPropertyFinder(this, use);
        finder.Visit(scope);
        if (finder.Found is { } property)
        {
            ThrowStoredOrdering(property, operation);
        }
    }

    private bool PreservesStoredOrdering(IProperty property, StoredOrderingUse use)
        => use == StoredOrderingUse.Comparison
            ? StoredOrdering.PreservesClrOrdering(property)
            : StoredOrdering.PreservesClrOrderingForAggregateAndSort(property);

    // A mapped scalar property `name` of an entity type with this CLR type, or null.
    private IEnumerable<IProperty> FindMappedProperties(Type ownerType, string name)
        => _queryContext.Context.Model.FindEntityTypes(ownerType)
            .Select(t => t.FindProperty(name))
            .OfType<IProperty>();

    private bool IsMappedEntityType(Type type)
        => _queryContext.Context.Model.FindEntityTypes(type).Any();

    private Dictionary<ParameterExpression, Expression[]> ParameterSources
        => _storedOrderingParameterSources ??= LambdaParameterSourceCollector.Collect(_storedOrderingQueryRoots);

    /// <summary>
    /// The expressions each element of <paramref name="sequence"/> is computed as (a <c>Select</c> body; a
    /// <c>GroupBy</c> call standing for its groupings; the element selector for a grouping parameter), through operators
    /// that keep the element; an empty list for a sequence of entities; <see langword="null"/> if unknown.
    /// </summary>
    private List<Expression>? FindElementExpressions(Expression sequence, int depth = 0)
    {
        if (depth > 32)
        {
            return null;
        }

        sequence = StripConverts(sequence);
        if (sequence.Type.TryGetItemType() is { } itemType && IsMappedEntityType(itemType))
        {
            return [];
        }

        // A mapped primitive collection (`x.Scores`, EF.Property(x, "Scores")): its elements are stored values of that
        // property.
        if (sequence.Type.TryGetItemType() is { } primitiveItemType)
        {
            var collectionProperties = sequence switch
            {
                MemberExpression { Expression: { } collectionOwner, Member: var collectionMember }
                    when IsMappedEntityType(StripConverts(collectionOwner).Type)
                    => FindMappedProperties(StripConverts(collectionOwner).Type, collectionMember.Name).ToList(),
                MethodCallExpression { Method: var efProperty, Arguments: [{ } collectionSource, ConstantExpression { Value: string collectionName }, ..] }
                    when efProperty.IsEFPropertyMethod() && IsMappedEntityType(collectionSource.Type)
                    => FindMappedProperties(collectionSource.Type, collectionName).ToList(),
                _ => []
            };

            if (collectionProperties.Count > 0)
            {
                return [new StoredCollectionElementExpression(collectionProperties, primitiveItemType)];
            }
        }

        switch (sequence)
        {
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray:
                return [.. newArray.Expressions];

            case ListInitExpression listInit when listInit.Initializers.All(i => i.Arguments.Count == 1):
                return [.. listInit.Initializers.Select(i => i.Arguments[0])];

            case ParameterExpression parameter
                when parameter.Type.IsGenericType && parameter.Type.GetGenericTypeDefinition() == typeof(IGrouping<,>):
                // A grouping ranges over the GroupBy's elements.
                if (!ParameterSources.TryGetValue(parameter, out var groupingSources))
                {
                    return null;
                }

                var groupElements = new List<Expression>();
                foreach (var groupingSource in groupingSources)
                {
                    var groupings = FindElementExpressions(groupingSource, depth + 1);
                    if (groupings is null)
                    {
                        return null;
                    }

                    foreach (var grouping in groupings)
                    {
                        if (grouping is not MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) } groupBy)
                        {
                            return null;
                        }

                        if (groupBy.Arguments.Count >= 3 && UnwrapLambda(groupBy.Arguments[2]) is { Parameters.Count: 1 } elementSelector)
                        {
                            groupElements.Add(elementSelector.Body);
                        }
                        else if (FindElementExpressions(groupBy.Arguments[0], depth + 1) is { } sourceElements)
                        {
                            groupElements.AddRange(sourceElements);
                        }
                        else
                        {
                            return null;
                        }
                    }
                }

                return groupElements;

            case ParameterExpression sequenceParameter when ParameterSources.TryGetValue(sequenceParameter, out var sequenceSources):
                // A sequence-typed parameter (a GroupJoin's inner group): its bound sources' elements.
                var sequenceElements = new List<Expression>();
                foreach (var sequenceSource in sequenceSources)
                {
                    if (FindElementExpressions(sequenceSource, depth + 1) is not { } parts)
                    {
                        return null;
                    }

                    sequenceElements.AddRange(parts);
                }

                return sequenceElements;

            case MethodCallExpression call
                when (call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable))
                     && call.Arguments.Count >= 1:
                switch (call.Method.Name)
                {
                    case nameof(Queryable.Select) when call.Arguments.Count == 2
                                                        && UnwrapLambda(call.Arguments[1]) is { Parameters.Count: 1 } selector:
                        return [selector.Body];

                    case nameof(Queryable.GroupBy)
                        when call.Arguments.Skip(2).Select(UnwrapLambda).FirstOrDefault(l => l is { Parameters.Count: 2 }) is { } resultSelector:
                        // GroupBy(source, key, [element,] (key, group) => result): the result (its parameters are bound
                        // by LambdaParameterSourceCollector).
                        return [resultSelector.Body];

                    case nameof(Queryable.GroupBy) when call.Arguments.Count == 2
                                                         || (call.Arguments.Count == 3 && UnwrapLambda(call.Arguments[2]) is { Parameters.Count: 1 }):
                        return [call];

                    case nameof(Queryable.Distinct) or nameof(Queryable.Where) or nameof(Queryable.OrderBy)
                        or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)
                        or nameof(Queryable.Skip) or nameof(Queryable.Take) or nameof(Queryable.AsQueryable)
                        or nameof(Queryable.Reverse) or nameof(Queryable.DefaultIfEmpty) or nameof(Enumerable.ToList)
                        or nameof(Enumerable.ToArray):
                        return FindElementExpressions(call.Arguments[0], depth + 1);

                    case nameof(Queryable.Concat) or nameof(Queryable.Union) or nameof(Queryable.Intersect)
                        or nameof(Queryable.Except) when call.Arguments.Count >= 2:
                        var left = FindElementExpressions(call.Arguments[0], depth + 1);
                        var right = FindElementExpressions(call.Arguments[1], depth + 1);
                        return left is null || right is null ? null : [.. left, .. right];
                }

                return null;

            default:
                return null;
        }
    }

    private static Expression StripConverts(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
                   or ExpressionType.Quote
               } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    /// <summary>
    /// Walks a key/operand value and throws on the first mapped property it reads whose stored ordering differs. Only
    /// the value's ordering matters: a bool-valued sub-expression (a test, an equality) and a nested subquery (checked
    /// when its own call is visited) aren't descended into.
    /// </summary>
    private sealed class StoredOrderingWalker(
        MongoEFToLinqTranslatingExpressionVisitor owner, StoredOrderingUse use, string operation)
    {
        private readonly HashSet<Expression> _visiting = new(ReferenceEqualityComparer.Instance);

        public void Walk(Expression? expression)
        {
            if (expression is null || !_visiting.Add(expression))
            {
                return;
            }

            try
            {
                WalkCore(expression);
            }
            finally
            {
                _visiting.Remove(expression);
            }
        }

        private void WalkCore(Expression expression)
        {
            var isBool = (Nullable.GetUnderlyingType(expression.Type) ?? expression.Type) == typeof(bool);

            switch (expression)
            {
                case ConstantExpression or DefaultExpression or LambdaExpression:
                    return;

                case StoredCollectionElementExpression element:
                    foreach (var collection in element.Collections)
                    {
                        // The collection's own storage decides its elements' (element-level converters aren't
                        // supported by the provider: the model fails validation).
                        if (!owner.PreservesStoredOrdering(collection, use))
                        {
                            owner.ThrowStoredOrdering(collection, operation);
                        }
                    }

                    return;

                case UnaryExpression { NodeType: ExpressionType.Not } not when isBool:
                    WalkTruth(not.Operand);
                    return;

                case UnaryExpression unary:
                    if (!isBool || unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
                    {
                        Walk(unary.Operand);
                    }

                    return;

                case MemberExpression { Expression: null }:
                    return; // static member

                case MemberExpression { Expression: { } nullable, Member.Name: "Value" or "HasValue" } nullableMember
                    when Nullable.GetUnderlyingType(nullable.Type) != null:
                    if (nullableMember.Member.Name == "Value")
                    {
                        Walk(nullable);
                    }

                    return;

                case MemberExpression { Expression: { } memberOwner, Member: PropertyInfo or FieldInfo } member:
                    WalkMember(memberOwner, member.Member);
                    return;

                case MethodCallExpression { Method: var efProperty, Arguments: [{ } source, ConstantExpression { Value: string name }, ..] }
                    when efProperty.IsEFPropertyMethod():
                    if (owner.IsMappedEntityType(source.Type))
                    {
                        CheckProperties(owner.FindMappedProperties(source.Type, name));
                    }
                    else
                    {
                        WalkMemberOfValue(source, name);
                    }

                    return;

                case MethodCallExpression call
                    when call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable):
                    WalkSequenceOperatorValue(call);
                    return;

                case ParameterExpression parameter:
                    if (!owner.IsMappedEntityType(parameter.Type))
                    {
                        WalkValues(parameter, parameter);
                    }

                    return;

                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                    WalkTruth(logical.Left);
                    WalkTruth(logical.Right);
                    return;

                case BinaryExpression when isBool:
                    return; // relational parts are checked by VisitBinary; equality is exact

                case BinaryExpression binary:
                    Walk(binary.Left);
                    Walk(binary.Right);
                    return;

                case ConditionalExpression conditional:
                    WalkTruth(conditional.Test);
                    Walk(conditional.IfTrue);
                    Walk(conditional.IfFalse);
                    return;

                case MethodCallExpression call:
                    // Any other method (string/DateTime/Math members, bool-valued or not) computes on the stored form.
                    Walk(call.Object);
                    foreach (var argument in call.Arguments)
                    {
                        Walk(argument);
                    }

                    return;

                case NewExpression newExpression:
                    foreach (var argument in newExpression.Arguments)
                    {
                        Walk(argument);
                    }

                    return;

                case MemberInitExpression memberInit:
                    Walk(memberInit.NewExpression);
                    foreach (var binding in memberInit.Bindings.OfType<MemberAssignment>())
                    {
                        Walk(binding.Expression);
                    }

                    return;

                case { NodeType: ExpressionType.Extension }:
                    return; // query roots, query parameters, EF's navigation nodes: no scalar property read

                default:
                    // Unclassified: refuse if the query reads any such property.
                    owner.ThrowIfQueryReadsStoredOrdering(expression, use, operation);
                    return;
            }
        }

        // A Queryable/Enumerable call used as a value. An element-returning operator (ElementAt, First, Single, Last,
        // selector-less Min/Max) yields one of its source's elements, so walk those; a selector aggregate is checked when
        // its own call is visited; a count/test or a sequence reads no ordered stored value; anything else is refused if
        // the query reads such a property.
        private void WalkSequenceOperatorValue(MethodCallExpression call)
        {
            switch (call.Method.Name)
            {
                case nameof(Enumerable.ElementAt) or nameof(Enumerable.ElementAtOrDefault)
                    or nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault)
                    or nameof(Enumerable.Last) or nameof(Enumerable.LastOrDefault)
                    or nameof(Enumerable.Single) or nameof(Enumerable.SingleOrDefault)
                    or nameof(Enumerable.Min) or nameof(Enumerable.Max) or nameof(Enumerable.Sum) or nameof(Enumerable.Average)
                    when call.Arguments.Count == 1
                         || call.Method.Name is nameof(Enumerable.ElementAt) or nameof(Enumerable.ElementAtOrDefault):
                    if (owner.FindElementExpressions(call.Arguments[0]) is { } elements)
                    {
                        foreach (var element in elements)
                        {
                            Walk(element);
                        }
                    }
                    else
                    {
                        owner.ThrowIfQueryReadsStoredOrdering(call, use, operation);
                    }

                    return;

                case nameof(Enumerable.Sum) or nameof(Enumerable.Min) or nameof(Enumerable.Max) or nameof(Enumerable.Average)
                    or nameof(Enumerable.MinBy) or nameof(Enumerable.MaxBy)
                    or nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                    or nameof(Enumerable.Any) or nameof(Enumerable.All) or nameof(Enumerable.Contains):
                    return;

                default:
                    if (call.Type.TryGetItemType() is null)
                    {
                        owner.ThrowIfQueryReadsStoredOrdering(call, use, operation);
                    }

                    return;
            }
        }

        // A bool used for its truth (a Not operand, a && / || operand, a ?: test, a bool sort key): the server tests the
        // stored value's truthiness, so a bool member must be default-stored (a converted "1"/"0" is always truthy).
        // Comparisons and equalities are exact or checked by VisitBinary; other computations are walked as values.
        private void WalkTruth(Expression expression)
        {
            switch (StripConverts(expression))
            {
                case UnaryExpression { NodeType: ExpressionType.Not } not:
                    WalkTruth(not.Operand);
                    return;

                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                    WalkTruth(logical.Left);
                    WalkTruth(logical.Right);
                    return;

                case BinaryExpression:
                    return;

                case ConditionalExpression conditional:
                    WalkTruth(conditional.Test);
                    WalkTruth(conditional.IfTrue);
                    WalkTruth(conditional.IfFalse);
                    return;

                case var other:
                    Walk(other);
                    return;
            }
        }

        private void WalkMember(Expression memberOwner, MemberInfo member)
        {
            var ownerType = StripConverts(memberOwner).Type;
            if (owner.IsMappedEntityType(ownerType))
            {
                // A scalar property is checked; a navigation yields an entity/collection, not an ordered value.
                CheckProperties(owner.FindMappedProperties(ownerType, member.Name));
                return;
            }

            WalkMemberOfValue(memberOwner, member.Name);
        }

        // `value.name` where `value` isn't an entity: resolve what `value` is and take its `name` member.
        private void WalkMemberOfValue(Expression value, string name)
        {
            value = StripConverts(value);
            if (!ContainsParameter(value))
            {
                return; // a closure or constant: no stored value
            }

            // A member computed from a stored value (x.Name.Length, x.When.Year, x.Dur.TotalMinutes, x.N.Value.X): the
            // server computes it on the stored form, so the value it is read from must itself pass.
            if (value is not ParameterExpression && !IsProjectionChain(value))
            {
                Walk(value);
                return;
            }

            var definitions = ResolveValues(value);
            if (definitions is null)
            {
                owner.ThrowIfQueryReadsStoredOrdering(value, use, operation);
                return;
            }

            foreach (var definition in definitions)
            {
                switch (StripConverts(definition))
                {
                    case NewExpression { Members: { } members } newExpression:
                        var index = members.ToList().FindIndex(m => m.Name == name);
                        if (index >= 0)
                        {
                            Walk(newExpression.Arguments[index]);
                        }
                        else
                        {
                            Walk(newExpression);
                        }

                        break;

                    case MemberInitExpression memberInit
                        when memberInit.Bindings.OfType<MemberAssignment>().FirstOrDefault(b => b.Member.Name == name) is { } binding:
                        Walk(binding.Expression);
                        break;

                    case MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) } groupBy
                        when name == nameof(IGrouping<object, object>.Key) && UnwrapLambda(groupBy.Arguments[1]) is { } keySelector:
                        Walk(keySelector.Body);
                        break;

                    case var other when owner.IsMappedEntityType(other.Type):
                        CheckProperties(owner.FindMappedProperties(other.Type, name));
                        break;

                    case StoredCollectionElementExpression element:
                        Walk(element); // a member of a stored element (d.Year over List<DateTime>)
                        break;

                    case var other:
                        // Unknown shape of the value: walk all of it.
                        Walk(other);
                        break;
                }
            }
        }

        // A non-entity parameter used as a value: walk what it ranges over.
        private void WalkValues(ParameterExpression parameter, Expression scope)
        {
            var definitions = ResolveValues(parameter);
            if (definitions is null)
            {
                owner.ThrowIfQueryReadsStoredOrdering(scope, use, operation);
                return;
            }

            foreach (var definition in definitions)
            {
                if (definition is MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) })
                {
                    continue; // a grouping as a value; its aggregates are checked where they are called
                }

                Walk(definition);
            }
        }

        // What a non-entity value expression is computed as: a parameter's source elements, a projected member, g.Key.
        private List<Expression>? ResolveValues(Expression value)
        {
            switch (StripConverts(value))
            {
                case ParameterExpression parameter:
                    if (!owner.ParameterSources.TryGetValue(parameter, out var sources))
                    {
                        return null;
                    }

                    var elements = new List<Expression>();
                    foreach (var source in sources)
                    {
                        if (owner.FindElementExpressions(source) is not { } sourceElements)
                        {
                            return null;
                        }

                        elements.AddRange(sourceElements);
                    }

                    return elements;

                case MemberExpression { Expression: { } inner, Member: var member }:
                    if (ResolveValues(inner) is not { } innerDefinitions)
                    {
                        return null;
                    }

                    var resolved = new List<Expression>();
                    foreach (var definition in innerDefinitions)
                    {
                        switch (StripConverts(definition))
                        {
                            case NewExpression { Members: { } members } newExpression
                                when members.ToList().FindIndex(m => m.Name == member.Name) is var index and >= 0:
                                resolved.Add(newExpression.Arguments[index]);
                                break;

                            case MemberInitExpression memberInit
                                when memberInit.Bindings.OfType<MemberAssignment>().FirstOrDefault(b => b.Member.Name == member.Name) is { } binding:
                                resolved.Add(binding.Expression);
                                break;

                            case MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) } groupBy
                                when member.Name == nameof(IGrouping<object, object>.Key) && UnwrapLambda(groupBy.Arguments[1]) is { } keySelector:
                                resolved.Add(keySelector.Body);
                                break;

                            case StoredCollectionElementExpression element:
                                resolved.Add(element);
                                break;

                            default:
                                return null;
                        }
                    }

                    return resolved;

                case var other:
                    return [other];
            }
        }

        private void CheckProperties(IEnumerable<IProperty> properties)
        {
            foreach (var property in properties)
            {
                if (!owner.PreservesStoredOrdering(property, use))
                {
                    owner.ThrowStoredOrdering(property, operation);
                }
            }
        }

        // `p`, `p.A`, `p.A.B`, ... over a non-entity parameter: resolvable through the parameter's projection.
        private bool IsProjectionChain(Expression expression)
            => StripConverts(expression) switch
            {
                ParameterExpression parameter => !owner.IsMappedEntityType(parameter.Type),
                MemberExpression { Expression: { } inner }
                    => !owner.IsMappedEntityType(StripConverts(inner).Type) && IsProjectionChain(inner),
                _ => false
            };

        private static bool ContainsParameter(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder.Found;
        }
    }

    // The element of a mapped primitive collection (`x.Scores`): a stored value of that property.
    private sealed class StoredCollectionElementExpression(IReadOnlyList<IProperty> collections, Type elementType) : Expression
    {
        public IReadOnlyList<IProperty> Collections { get; } = collections;

        public override ExpressionType NodeType => ExpressionType.Extension;

        public override Type Type { get; } = elementType;

        protected override Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor) => this;
    }

    private sealed class ParameterFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }

        protected override Expression VisitExtension(Expression node) => node;
    }

    // Any mapped property that can reach a tree whose stored ordering differs for `use`: read in the tree, or in the
    // bound source of a lambda parameter used in it (the whole query for an unbound non-entity parameter).
    private sealed class StoredOrderingPropertyFinder(MongoEFToLinqTranslatingExpressionVisitor owner, StoredOrderingUse use)
        : System.Linq.Expressions.ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _followed = [];
        private bool _scannedRoots;

        public IProperty? Found { get; private set; }

        public override Expression? Visit(Expression? node)
            => Found is null ? base.Visit(node) : node;

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!owner.IsMappedEntityType(node.Type) && _followed.Add(node))
            {
                if (owner.ParameterSources.TryGetValue(node, out var sources))
                {
                    foreach (var source in sources)
                    {
                        Visit(source);
                    }
                }
                else if (!_scannedRoots)
                {
                    _scannedRoots = true;
                    foreach (var root in owner._storedOrderingQueryRoots)
                    {
                        Visit(root);
                    }
                }
            }

            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } memberOwner)
            {
                Check(StripConverts(memberOwner).Type, node.Member.Name);
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsEFPropertyMethod()
                && node.Arguments is [{ } source, ConstantExpression { Value: string name }, ..])
            {
                Check(source.Type, name);
            }

            // Only what flows into a sequence's elements can reach a value read from it: a filter predicate, a sort key
            // or a join key selects/orders rows but never becomes an element value, so its reads (e.g. an equality over
            // a converted enum) are not counted.
            if (node.Method.DeclaringType == typeof(Queryable) || node.Method.DeclaringType == typeof(Enumerable))
            {
                switch (node.Method.Name)
                {
                    case nameof(Queryable.Where) or nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
                        or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) or nameof(Queryable.SkipWhile)
                        or nameof(Queryable.TakeWhile) or nameof(Queryable.Skip) or nameof(Queryable.Take)
                        or nameof(Queryable.Distinct) or nameof(Queryable.AsQueryable):
                        Visit(node.Arguments[0]);
                        return node;

                    case nameof(Queryable.Join) when node.Arguments.Count >= 5:
                        Visit(node.Arguments[0]);
                        Visit(node.Arguments[1]);
                        Visit(node.Arguments[4]);
                        return node;
                }
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitExtension(Expression node)
            => node.CanReduce ? base.VisitExtension(node) : node;

        private void Check(Type ownerType, string name)
            => Found ??= owner.FindMappedProperties(ownerType, name).FirstOrDefault(p => !owner.PreservesStoredOrdering(p, use));
    }

    // Records, for each lambda parameter of a Queryable/Enumerable operator, the sequence(s) it ranges over.
    private sealed class LambdaParameterSourceCollector : System.Linq.Expressions.ExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, Expression[]> _sources = new();

        public static Dictionary<ParameterExpression, Expression[]> Collect(IEnumerable<Expression> roots)
        {
            var collector = new LambdaParameterSourceCollector();
            foreach (var root in roots)
            {
                collector.Visit(root);
            }

            return collector._sources;
        }

        protected override Expression VisitExtension(Expression node)
            => node.CanReduce ? base.VisitExtension(node) : node;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if ((node.Method.DeclaringType == typeof(Queryable) || node.Method.DeclaringType == typeof(Enumerable))
                && node.Arguments.Count >= 2)
            {
                if (node.Method.Name is nameof(Queryable.Join) or nameof(Queryable.GroupJoin) && node.Arguments.Count >= 5)
                {
                    Bind(node.Arguments[2], 0, node.Arguments[0]);
                    Bind(node.Arguments[3], 0, node.Arguments[1]);
                    Bind(node.Arguments[4], 0, node.Arguments[0]);
                    if (node.Method.Name == nameof(Queryable.Join))
                    {
                        Bind(node.Arguments[4], 1, node.Arguments[1]);
                    }
                }
                else if (node.Method.Name == nameof(Queryable.SelectMany) && node.Arguments.Count >= 3)
                {
                    Bind(node.Arguments[1], 0, node.Arguments[0]);
                    Bind(node.Arguments[2], 0, node.Arguments[0]);
                    if (UnwrapLambda(node.Arguments[1]) is { } collectionSelector)
                    {
                        Bind(node.Arguments[2], 1, collectionSelector.Body);
                    }
                }
                else if (node.Method.Name == nameof(Queryable.GroupBy)
                         && node.Arguments.Skip(2).Select(UnwrapLambda).FirstOrDefault(l => l is { Parameters.Count: 2 }) is { } resultSelector)
                {
                    // (key, group) => result: `key` is the key selector's value (a one-element "sequence" of it); `group`
                    // ranges over the element selector's value, or the source's elements.
                    Bind(node.Arguments[1], 0, node.Arguments[0]);
                    var elementSelector = node.Arguments.Count >= 4 ? UnwrapLambda(node.Arguments[2]) : null;
                    if (elementSelector is { Parameters.Count: 1 })
                    {
                        Bind(node.Arguments[2], 0, node.Arguments[0]);
                    }

                    if (UnwrapLambda(node.Arguments[1]) is { } keySelector)
                    {
                        BindParameter(resultSelector.Parameters[0], Expression.NewArrayInit(keySelector.ReturnType, keySelector.Body));
                    }

                    BindParameter(
                        resultSelector.Parameters[1],
                        elementSelector is { Parameters.Count: 1 }
                            ? Expression.NewArrayInit(elementSelector.ReturnType, elementSelector.Body)
                            : node.Arguments[0]);
                }
                else if (node.Method.Name is nameof(Queryable.Zip) or nameof(Queryable.Aggregate))
                {
                    // Multi-source / accumulator lambdas: left unbound (walk refuses conservatively).
                }
                else
                {
                    for (var i = 1; i < node.Arguments.Count; i++)
                    {
                        if (UnwrapLambda(node.Arguments[i]) is { Parameters.Count: 1 })
                        {
                            Bind(node.Arguments[i], 0, node.Arguments[0]);
                        }
                    }
                }
            }

            return base.VisitMethodCall(node);
        }

        private void Bind(Expression lambdaArgument, int parameterIndex, Expression source)
        {
            if (UnwrapLambda(lambdaArgument) is { } lambda && lambda.Parameters.Count > parameterIndex)
            {
                BindParameter(lambda.Parameters[parameterIndex], source);
            }
        }

        private void BindParameter(ParameterExpression parameter, Expression source)
        {
            if (!_sources.TryGetValue(parameter, out var existing))
            {
                _sources[parameter] = [source];
            }
            else if (!existing.Contains(source))
            {
                _sources[parameter] = [.. existing, source];
            }
        }
    }
}
