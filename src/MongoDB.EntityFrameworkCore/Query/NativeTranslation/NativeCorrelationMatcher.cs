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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes EF Core's FK correlation predicate (a <c>Where</c> over an <see cref="Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression"/>
/// comparing an outer key to a dependent FK) and resolves it to the single matching collection navigation off the outer entity.
/// </summary>
/// <summary>
/// One compared pair of a correlated subquery's key: the outer property, the subquery element's property, and whether the predicate
/// guards the outer property against null (<see cref="NativeCorrelationMatcher.TryMatchCorrelationKeys"/>).
/// </summary>
internal readonly record struct CorrelationKeyPart(IProperty Outer, IProperty Inner, bool Guarded);

internal static class NativeCorrelationMatcher
{
    /// <summary>
    /// Matches <paramref name="whereBody"/> as a correlation of <paramref name="outerRoot"/>'s key with a dependent FK and resolves
    /// the single collection navigation (embedded iff <paramref name="requireEmbedded"/>) whose target and single-property FK match.
    /// Declines on no match, an ambiguous match, or any extra conjunct.
    /// </summary>
    /// <remarks>
    /// <paramref name="outerRoot"/> is matched by reference: the query's lambda parameter, or the entity shaper once the binder has
    /// substituted it. The outer operand must resolve, by <see cref="IProperty"/> identity, to the navigation's principal key (either
    /// operand order): binding emits <c>$lookup {localField: principalKey, foreignField: fk}</c>, so a correlation on any other outer
    /// member would silently read the key-correlated rows. A null guard is admitted only on that same key, as EF's nav-expansion
    /// emits it; a guard on another member is a user filter the bound navigation would drop.
    /// </remarks>
    internal static bool TryMatchCorrelatedCollection(
        Expression whereBody,
        IEntityType outerEntityType,
        Expression outerRoot,
        IEntityType targetEntityType,
        bool requireEmbedded,
        out INavigation navigation)
    {
        navigation = null!;

        if (!TryResolveCorrelationSides(whereBody, outerRoot, out var dependentSide, out var outerSideExpression, out var nullGuarded))
            return false;

        var dependentPropertyName = dependentSide.TryGetSimplePropertyName();
        if (dependentPropertyName == null)
            return false;

        var candidates = outerEntityType.GetNavigations()
            .Where(n => n.IsCollection
                        && n.IsEmbedded() == requireEmbedded
                        && n.TargetEntityType == targetEntityType
                        && n.ForeignKey.Properties.Count == 1
                        && n.ForeignKey.Properties[0].Name == dependentPropertyName)
            .ToList();

        if (candidates.Count != 1)
            return false;

        var principalKeyProperty = candidates[0].ForeignKey.PrincipalKey.Properties[0];
        var outerSide = outerSideExpression;
        if (!IsOuterProperty(outerSide, outerRoot, outerEntityType, principalKeyProperty)
            || (nullGuarded != null && !IsOuterProperty(nullGuarded, outerRoot, outerEntityType, principalKeyProperty)))
        {
            return false;
        }

        navigation = candidates[0];
        return true;
    }

    /// <summary>
    /// Splits a correlation predicate into its dependent side (off the subquery's own lambda parameter), outer side (off
    /// <paramref name="outerRoot"/>) and optional null-guarded operand. Structural only; callers decide what the sides must resolve to.
    /// </summary>
    private static bool TryResolveCorrelationSides(
        Expression whereBody,
        Expression outerRoot,
        [NotNullWhen(true)] out Expression? dependentSide,
        [NotNullWhen(true)] out Expression? outerSide,
        out Expression? nullGuarded)
    {
        dependentSide = outerSide = null;

        if (!TryGetCorrelationEqualitySides(whereBody, out var side1, out var side2, out nullGuarded))
            return false;

        return TryAssignSides(side1, side2, outerRoot, out dependentSide, out outerSide);
    }

    /// <summary>
    /// Assigns the two compared sides of one equality to the subquery's own element (a lambda parameter) and the outer entity
    /// (<paramref name="outerRoot"/>), in either order.
    /// </summary>
    private static bool TryAssignSides(
        Expression side1, Expression side2, Expression outerRoot,
        [NotNullWhen(true)] out Expression? dependentSide, [NotNullWhen(true)] out Expression? outerSide)
    {
        dependentSide = outerSide = null;

        var side1Root = GetRootExpression(side1);
        var side2Root = GetRootExpression(side2);

        if (ReferenceEquals(side2Root, outerRoot) && side1Root is ParameterExpression && !ReferenceEquals(side1Root, outerRoot))
        {
            dependentSide = side1;
            outerSide = side2;
            return true;
        }

        if (ReferenceEquals(side1Root, outerRoot) && side2Root is ParameterExpression && !ReferenceEquals(side2Root, outerRoot))
        {
            dependentSide = side2;
            outerSide = side1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Matches <paramref name="whereBody"/> as an equality between one scalar property of the subquery's entity and one of the outer
    /// entity, neither a collection navigation's foreign key (or the model has none): same shape as the navigation correlation
    /// (either operand order, optional <c>outerKey != null</c> guard on that same property, no other conjunct).
    /// </summary>
    /// <param name="whereBody">The predicate body.</param>
    /// <param name="outerEntityType">The outer entity type.</param>
    /// <param name="outerRoot">The expression the outer entity is rooted on.</param>
    /// <param name="targetEntityType">The subquery's entity type.</param>
    /// <param name="outerProperty">The matched outer property.</param>
    /// <param name="innerProperty">The matched subquery property.</param>
    /// <param name="guarded">Whether the predicate carries the null guard (EF's <c>GroupJoin</c> rewrite does; an explicit subquery does not).</param>
    /// <remarks>
    /// The properties must compare in the database as in C#: a scalar CLR type each, stored alike, no collection; otherwise declines,
    /// so the keyed <c>$lookup</c> cannot answer differently from the LINQ the author wrote.
    /// </remarks>
    internal static bool TryMatchRawKeyCorrelation(
        Expression whereBody,
        IEntityType outerEntityType,
        Expression outerRoot,
        IEntityType targetEntityType,
        [NotNullWhen(true)] out IProperty? outerProperty,
        [NotNullWhen(true)] out IProperty? innerProperty,
        out bool guarded)
    {
        outerProperty = innerProperty = null;
        guarded = false;

        if (!TryResolveCorrelationSides(whereBody, outerRoot, out var dependentSide, out var outerSide, out var nullGuarded)
            || !TryResolveKeyProperties(dependentSide, outerSide, outerEntityType, outerRoot, targetEntityType, out var outer, out var dependent))
        {
            return false;
        }

        if (nullGuarded != null && !IsOuterProperty(nullGuarded, outerRoot, outerEntityType, outer))
            return false;

        outerProperty = outer;
        innerProperty = dependent;
        guarded = nullGuarded != null;
        return true;
    }

    /// <summary>
    /// Resolves the two compared sides of one equality to the outer and the dependent property: each a direct property of its own
    /// side (an <c>EF.Property</c> or member access), a scalar CLR type, and the two stored alike, so the keyed lookup built from them
    /// cannot answer differently from the LINQ the author wrote.
    /// </summary>
    private static bool TryResolveKeyProperties(
        Expression dependentSide, Expression outerSide, IEntityType outerEntityType, Expression outerRoot, IEntityType targetEntityType,
        [NotNullWhen(true)] out IProperty? outerProperty, [NotNullWhen(true)] out IProperty? dependentProperty)
    {
        outerProperty = dependentProperty = null;

        if (!dependentSide.RemoveConvert().TryGetMemberOrEFProperty(out var dependentReceiver, out var dependentName)
            || dependentReceiver.RemoveConvert() is not ParameterExpression
            || targetEntityType.FindProperty(dependentName) is not { } dependent
            || !outerSide.RemoveConvert().TryGetMemberOrEFProperty(out var outerReceiver, out var outerName)
            || !ReferenceEquals(outerReceiver.RemoveConvert(), outerRoot)
            || outerEntityType.FindProperty(outerName) is not { } outer)
        {
            return false;
        }

        if (!IsScalarKey(outer) || !IsScalarKey(dependent) || !StoredSerialization.StoredAlike(outer, dependent))
            return false;

        outerProperty = outer;
        dependentProperty = dependent;
        return true;

        static bool IsScalarKey(IProperty property)
        {
            var underlying = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            return (underlying.IsValueType || underlying == typeof(string)) && !property.IsPrimitiveCollection;
        }
    }

    /// <summary>
    /// The key of a correlated subquery over properties that are not a collection navigation's foreign key: the single strict equality
    /// of <see cref="TryMatchRawKeyCorrelation"/>, or a CONJUNCTION of such equalities (an anonymous-type <c>GroupJoin</c> key, or an
    /// explicit <c>r.A == s.A &amp;&amp; r.B == s.B</c>). Each outer property may carry its own null guard
    /// (<c>!(s.A == null)</c>, as EF's rewrite writes one per part); a guarded part matches nothing for a null outer value, an unguarded
    /// one equates null with null. Any other conjunct, a property used twice, or a guard on a property not compared declines.
    /// </summary>
    internal static bool TryMatchCorrelationKeys(
        Expression whereBody,
        IEntityType outerEntityType,
        Expression outerRoot,
        IEntityType targetEntityType,
        [NotNullWhen(true)] out IReadOnlyList<CorrelationKeyPart>? parts)
    {
        parts = null;

        if (TryMatchRawKeyCorrelation(whereBody, outerEntityType, outerRoot, targetEntityType, out var outerProperty, out var innerProperty, out var guarded))
        {
            parts = [new CorrelationKeyPart(outerProperty, innerProperty, guarded)];
            return true;
        }

        var conjuncts = new List<Expression>();
        FlattenAndAlso(whereBody, conjuncts);

        var keyParts = new List<CorrelationKeyPart>();
        var guards = new List<Expression>();
        foreach (var conjunct in conjuncts)
        {
            var stripped = conjunct.RemoveConvert();
            if (TryExtractEqualitySides(stripped, out var left, out var right))
            {
                // EF8 and EF9 keep a GroupJoin's anonymous key whole (`Equals(new { s.A, s.B }, new { r.A, r.B })`): member-wise equality,
                // where a null member equals a null member, so each member pair is an unguarded part.
                var pairs = TryExpandAnonymousKeys(left, right, out var memberPairs) ? memberPairs : [(left, right)];
                foreach (var (leftSide, rightSide) in pairs)
                {
                    if (!TryAssignSides(leftSide, rightSide, outerRoot, out var dependentSide, out var outerSide)
                        || !TryResolveKeyProperties(dependentSide, outerSide, outerEntityType, outerRoot, targetEntityType, out var outer, out var dependent))
                    {
                        return false;
                    }

                    keyParts.Add(new CorrelationKeyPart(outer, dependent, Guarded: false));
                }
            }
            else if (TryGetNullGuardedOperand(stripped, out var guardedOperand))
            {
                // `!(new { s.A, s.B } == null)`, EF8/EF9's guard on the whole key: an object built in the query is never null, so it
                // excludes nothing.
                if (guardedOperand.RemoveConvert() is not NewExpression)
                    guards.Add(guardedOperand);
            }
            else
            {
                return false;
            }
        }

        if (keyParts.Count < 1
            || keyParts.Select(p => p.Outer).Distinct().Count() != keyParts.Count
            || keyParts.Select(p => p.Inner).Distinct().Count() != keyParts.Count)
        {
            return false;
        }

        foreach (var guard in guards)
        {
            var index = keyParts.FindIndex(p => !p.Guarded && IsOuterProperty(guard, outerRoot, outerEntityType, p.Outer));
            if (index < 0)
                return false;

            keyParts[index] = keyParts[index] with { Guarded = true };
        }

        parts = keyParts;
        return true;
    }

    /// <summary>
    /// The member pairs of an equality between two constructions of the same anonymous type (<c>new { s.A, s.B }</c> and
    /// <c>new { r.A, r.B }</c>): anonymous-type equality is member-wise.
    /// </summary>
    private static bool TryExpandAnonymousKeys(
        Expression left, Expression right, out List<(Expression Left, Expression Right)> pairs)
    {
        pairs = [];

        if (left.RemoveConvert() is not NewExpression { Arguments.Count: > 0 } leftNew
            || right.RemoveConvert() is not NewExpression rightNew
            || leftNew.Type != rightNew.Type
            || rightNew.Arguments.Count != leftNew.Arguments.Count
            || !leftNew.Type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
            || !leftNew.Type.Name.Contains("AnonymousType", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < leftNew.Arguments.Count; i++)
            pairs.Add((leftNew.Arguments[i], rightNew.Arguments[i]));

        return true;
    }

    private static void FlattenAndAlso(Expression expression, List<Expression> conjuncts)
    {
        if (expression.RemoveConvert() is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            FlattenAndAlso(andAlso.Left, conjuncts);
            FlattenAndAlso(andAlso.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expression);
        }
    }

    /// <summary>
    /// Builds the keyed, not-unwound <c>$lookup</c> behind a correlated subquery matched by <see cref="TryMatchRawKeyCorrelation"/>.
    /// The alias encodes the target, both key paths and whether a null local key is excluded, so two subqueries over the same
    /// collection with different keys never share an array and identical ones deduplicate.
    /// </summary>
    internal static LookupExpression BuildCorrelatedArrayLookup(
        IEntityType targetEntityType, IProperty outerProperty, IProperty innerProperty, bool guarded,
        IReadOnlyList<LambdaExpression>? filters = null, MongoExpression? filterPredicate = null, string aliasSuffix = "",
        IReadOnlyList<(string Field, int Direction)>? elementSort = null, int? elementLimit = null, int? elementSkip = null)
        => BuildCorrelatedArrayLookup(
            targetEntityType, [new CorrelationKeyPart(outerProperty, innerProperty, guarded)], filters, filterPredicate, aliasSuffix,
            elementSort, elementLimit, elementSkip);

    /// <summary>
    /// <see cref="BuildCorrelatedArrayLookup(IEntityType, IProperty, IProperty, bool, IReadOnlyList{LambdaExpression}?, MongoExpression?, string, IReadOnlyList{ValueTuple{string, int}}?, int?, int?)"/>
    /// over one or several key pairs. Several pairs are a composite-key lookup (the <c>let</c> + <c>$expr</c> form), each pair
    /// null-normalized when either property is nullable and optionally excluding a null outer value.
    /// </summary>
    internal static LookupExpression BuildCorrelatedArrayLookup(
        IEntityType targetEntityType, IReadOnlyList<CorrelationKeyPart> parts,
        IReadOnlyList<LambdaExpression>? filters = null, MongoExpression? filterPredicate = null, string aliasSuffix = "",
        IReadOnlyList<(string Field, int Direction)>? elementSort = null, int? elementLimit = null, int? elementSkip = null)
    {
        LookupExpression lookup;
        if (parts.Count == 1)
        {
            var (outerProperty, innerProperty, guarded) = parts[0];
            var localField = LookupExpression.GetFieldPath(outerProperty);
            var foreignField = LookupExpression.GetFieldPath(innerProperty);

            // A null outer key matches nothing only when EF guarded the predicate AND the key can be null; a non-nullable key can't be
            // null, so the plain (indexable) form is exact there.
            var excludesNull = guarded && outerProperty.IsNullable;

            var alias = LookupExpression.LookupAliasPrefix
                        + targetEntityType.ShortName() + "_" + foreignField.Replace('.', '_') + "_" + localField.Replace('.', '_')
                        + (excludesNull ? "_nn" : "") + aliasSuffix;

            lookup = new LookupExpression(
                targetEntityType, targetEntityType.GetCollectionName(), localField, foreignField, alias, forceUnwind: false)
            {
                IsCorrelatedArraySource = true,
                ExcludesNullLocalKey = excludesNull,
                ElementFilters = filters ?? [],
                ElementSort = elementSort ?? [],
                ElementLimit = elementLimit,
                ElementSkip = elementSkip
            };
        }
        else
        {
            var pairs = new List<LookupKeyPair>();
            var aliasParts = new List<string>();
            foreach (var (outerProperty, innerProperty, guarded) in parts)
            {
                var localField = LookupExpression.GetFieldPath(outerProperty);
                var foreignField = LookupExpression.GetFieldPath(innerProperty);
                var excludesNull = guarded && outerProperty.IsNullable;

                pairs.Add(new LookupKeyPair(
                    localField, foreignField, outerProperty.IsNullable || innerProperty.IsNullable, excludesNull));
                aliasParts.Add(foreignField.Replace('.', '_') + "_" + localField.Replace('.', '_') + (excludesNull ? "_nn" : ""));
            }

            // Distinct from a single-pair alias (`_c<N>_`), and the order of the pairs is part of it.
            var alias = LookupExpression.LookupAliasPrefix
                        + targetEntityType.ShortName() + "_c" + parts.Count + "_" + string.Join("__", aliasParts) + aliasSuffix;

            lookup = new LookupExpression(targetEntityType, targetEntityType.GetCollectionName(), pairs, alias, forceUnwind: false)
            {
                IsCorrelatedArraySource = true,
                ElementFilters = filters ?? [],
                ElementSort = elementSort ?? [],
                ElementLimit = elementLimit,
                ElementSkip = elementSkip
            };
        }

        // Uncorrelated filters ride inside the lookup's sub-pipeline (LookupPipelineKind.FilteredJoinInner), so a parameter in one is read per execution.
        if (filterPredicate is not null)
        {
            lookup.InnerPredicate = filterPredicate;
            lookup.PipelineKind = LookupPipelineKind.FilteredJoinInner;
        }

        return lookup;
    }

    /// <summary>Whether <paramref name="expression"/> is a direct (not nested) property access on <paramref name="outerRoot"/> resolving to exactly <paramref name="property"/>.</summary>
    private static bool IsOuterProperty(
        Expression expression, Expression outerRoot, IEntityType outerEntityType, IProperty property)
        => expression.RemoveConvert().TryGetMemberOrEFProperty(out var receiver, out var name)
           && ReferenceEquals(receiver.RemoveConvert(), outerRoot)
           && ReferenceEquals(outerEntityType.FindProperty(name), property);

    /// <summary>
    /// Extracts the sides of a bare equality, or one guarded by exactly one null check (<c>k != null &amp;&amp; equality</c>, either
    /// order, as EF emits for a nullable outer key), returning the guarded operand in <paramref name="nullGuarded"/>. Any other
    /// conjunct declines, so a user-filtered count isn't mistaken for a plain FK correlation.
    /// </summary>
    private static bool TryGetCorrelationEqualitySides(
        Expression body, out Expression left, out Expression right, out Expression? nullGuarded)
    {
        var stripped = body.RemoveConvert();
        nullGuarded = null;

        if (TryExtractEqualitySides(stripped, out left!, out right!))
            return true;

        if (stripped is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            if (TryGetNullGuardedOperand(andAlso.Left, out nullGuarded)
                && TryExtractEqualitySides(andAlso.Right, out left!, out right!))
                return true;
            if (TryGetNullGuardedOperand(andAlso.Right, out nullGuarded)
                && TryExtractEqualitySides(andAlso.Left, out left!, out right!))
                return true;
        }

        left = right = null!;
        nullGuarded = null;
        return false;
    }

    /// <summary>
    /// Extracts the two sides of an equality in the spellings EF's nav-expansion produces: <c>==</c>, static <c>object.Equals(x, y)</c>
    /// and instance <c>x.Equals(y)</c>. Also used by <c>NativeSelectManyBinder</c>.
    /// </summary>
    internal static bool TryExtractEqualitySides(Expression node, out Expression left, out Expression right)
    {
        switch (node.RemoveConvert())
        {
            case BinaryExpression { NodeType: ExpressionType.Equal } eq:
                left = eq.Left;
                right = eq.Right;
                return true;

            case MethodCallExpression
            {
                Method: { Name: nameof(Equals), IsStatic: true, DeclaringType: var declaringType },
                Arguments: [var arg0, var arg1]
            } when declaringType == typeof(object):
                left = arg0;
                right = arg1;
                return true;

            case MethodCallExpression
            {
                Method.Name: nameof(Equals),
                Object: { } instance,
                Arguments: [var arg]
            }:
                left = instance;
                right = arg;
                return true;

            default:
                left = right = null!;
                return false;
        }
    }

    /// <summary>
    /// Matches the correlated subquery nav-expansion leaves of a reference-collection navigation: <c>Queryable.Where(EntityQueryRootExpression&lt;Target&gt;, predicate)</c>
    /// with a single-parameter predicate. Structural only: the predicate isn't checked to be an FK correlation.
    /// </summary>
    internal static bool TryMatchRootWhere(
        Expression expression,
        [NotNullWhen(true)] out EntityQueryRootExpression? root,
        [NotNullWhen(true)] out LambdaExpression? predicate)
    {
        if (expression is MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            && whereDeclaring == typeof(Queryable)
            && predicateArg.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } predicateLambda)
        {
            root = rootExpression;
            predicate = predicateLambda;
            return true;
        }

        root = null;
        predicate = null;
        return false;
    }

    /// <summary>
    /// Matches a chain of <c>Queryable.Where</c> calls over a query root, innermost first (<c>Orders.Where(filter).Where(correlation)</c>).
    /// Structural only: which one is the correlation is the caller's decision.
    /// </summary>
    internal static bool TryMatchRootWhereChain(
        Expression expression,
        [NotNullWhen(true)] out EntityQueryRootExpression? root,
        [NotNullWhen(true)] out List<LambdaExpression>? wheres)
    {
        root = null;
        wheres = null;

        var chain = new List<LambdaExpression>();
        var current = expression;
        while (current is MethodCallExpression
               {
                   Method: { Name: nameof(Queryable.Where), DeclaringType: var declaringType },
                   Arguments: [var source, var predicateArgument]
               }
               && declaringType == typeof(Queryable)
               && predicateArgument.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } lambda)
        {
            chain.Insert(0, lambda);
            current = source;
        }

        if (chain.Count == 0 || current is not EntityQueryRootExpression rootExpression)
            return false;

        root = rootExpression;
        wheres = chain;
        return true;
    }

    /// <summary>
    /// Matches <see cref="TryMatchRootWhere"/>'s <c>Where(root, correlationPredicate)</c> as the FK correlation of
    /// <paramref name="outerParameter"/> with a non-embedded collection navigation and resolves it: the shape nav-expansion wraps
    /// around a reference-collection <c>Count</c> (see <see cref="MongoExpressionTranslator.TryMatchCountExpression"/>), list or
    /// reducer. Shared by <see cref="NativeProjectionBinder"/> and <see cref="NativeReferenceCollectionCountPredicateBinder"/>.
    /// </summary>
    internal static bool TryMatchCorrelatedRootWhere(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression whereArg,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;

        return TryMatchRootWhere(whereArg, out var rootExpression, out var predicate)
               && TryMatchCorrelatedCollection(
                   predicate.Body, mongoQ.CollectionExpression.EntityType, outerParameter, rootExpression.EntityType,
                   requireEmbedded: false, out navigation!);
    }

    /// <summary>
    /// Stages <paramref name="lookup"/> into <paramref name="pendingLookups"/>, or reuses an existing lookup at the same alias (staged
    /// or pending on the query) of the same kind; <paramref name="stagedLookup"/> is whichever backs the leaf. Declines a lookup that
    /// isn't a plain native collection lookup (TPH-derived target, <see cref="LookupPipelineKind.DiscriminatorNarrowed"/>) and a
    /// collision with a different kind (filtered Include, or a <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup, which
    /// unwinds to one document): reading it would silently read the wrong shape.
    /// </summary>
    internal static bool TryStageNativeCollectionLookup(
        MongoQueryExpression mongoQ,
        LookupExpression lookup,
        List<LookupExpression> pendingLookups,
        [NotNullWhen(true)] out LookupExpression? stagedLookup)
    {
        stagedLookup = null;

        if (!lookup.IsNativeCollectionLookup)
            return false;

        var collidingLookup = FindStagedOrPendingLookup(mongoQ, pendingLookups, lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
            stagedLookup = lookup;
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }
        else
        {
            stagedLookup = collidingLookup;
        }

        return true;
    }

    /// <summary>The lookup at <paramref name="alias"/> staged in <paramref name="pendingLookups"/> or pending on <paramref name="mongoQ"/> (staged first), or <see langword="null"/>.</summary>
    internal static LookupExpression? FindStagedOrPendingLookup(
        MongoQueryExpression mongoQ, List<LookupExpression> pendingLookups, string alias)
        => pendingLookups.FirstOrDefault(l => l.As == alias)
           ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == alias);

    /// <summary>
    /// Builds (or reuses) the <c>$lookup</c> a reference-collection <c>Count</c> needs and stages it via
    /// <see cref="TryStageNativeCollectionLookup"/>; colliding with a <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup
    /// declines rather than emit a <c>$size</c> over the wrong shape.
    /// </summary>
    /// <remarks>
    /// The backing lookup must be stamped <see cref="LookupExpression.IsBareCountSizeSource"/> so a later paged Include for the same
    /// navigation is rerouted instead of merged. Not stamped here (recognizers must not mutate then decline): it returns
    /// <paramref name="lookupToStamp"/> for the caller to stamp at its commit point.
    /// </remarks>
    internal static bool TryBuildReferenceCollectionCountLookup(
        MongoQueryExpression mongoQ,
        INavigation navigation,
        List<LookupExpression> pendingLookups,
        Type resultType,
        out LookupExpression? lookupToStamp,
        [NotNullWhen(true)] out MongoSizeExpression? result)
    {
        result = null;
        lookupToStamp = null;

        if (!TryStageNativeCollectionLookup(
                mongoQ, new LookupExpression(navigation) { InjectAfterRoot = true }, pendingLookups, out var stagedLookup))
        {
            return false;
        }

        lookupToStamp = stagedLookup;
        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), resultType);
        return true;
    }

    private static bool TryGetNullGuardedOperand(Expression node, [NotNullWhen(true)] out Expression? guarded)
    {
        guarded = null;
        var stripped = node.RemoveConvert();
        if (stripped is BinaryExpression { NodeType: ExpressionType.NotEqual } bin)
        {
            guarded = IsNullConstant(bin.Right) ? bin.Left : IsNullConstant(bin.Left) ? bin.Right : null;
        }
        else if (stripped is UnaryExpression { NodeType: ExpressionType.Not, Operand: var negated }
                 && TryExtractEqualitySides(negated, out var left, out var right))
        {
            // The spelling EF's GroupJoin rewrite emits for a nullable outer key: !(key == null), as !object.Equals(key, null).
            guarded = IsNullConstant(right) ? left : IsNullConstant(left) ? right : null;
        }

        return guarded != null;
    }

    internal static bool IsNullConstant(Expression node)
        => node.RemoveConvert() is ConstantExpression { Value: null };

    /// <summary>The receiver of a member access or <c>EF.Property</c> (one hop, after stripping conversions), or the expression itself if a parameter; otherwise <see langword="null"/>.</summary>
    private static Expression? GetRootExpression(Expression expression)
    {
        var stripped = expression.RemoveConvert();
        return stripped switch
        {
            MemberExpression { Expression: { } inner } => inner.RemoveConvert(),
            MethodCallExpression call when call.Method.IsEFPropertyMethod() && call.Arguments.Count == 2
                => call.Arguments[0].RemoveConvert(),
            ParameterExpression parameter => parameter,
            _ => null
        };
    }
}
