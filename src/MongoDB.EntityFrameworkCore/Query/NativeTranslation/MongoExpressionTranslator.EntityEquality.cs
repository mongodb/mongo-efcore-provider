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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — root-entity KEY-BASED equality against a DIFFERENT, non-null
/// entity value (<c>c == local</c>, <c>c == new Customer{...}</c>, and their <c>.Equals(...)</c>/<c>!=</c>
/// forms).
/// </summary>
/// <remarks>
/// EF Core has no PROVIDER-AGNOSTIC entity-equality rewrite: turning a structural (entity-to-entity)
/// comparison into a primary-key comparison is implemented separately by each provider that wants it
/// (see <c>RelationalSqlTranslatingExpressionVisitor.StructuralEquality.cs</c>, and the InMemory/Cosmos
/// equivalents) — there was previously no such rewrite here, so EVERY <c>c == entityValue</c> predicate
/// fell back to driver-LINQ, taking the whole query (including an otherwise-native trailing projection)
/// with it.
/// <para>
/// <b>Boundary with <see cref="TranslateComparisonCore"/>'s own whole-entity check.</b> A null comparand
/// (<c>c == null</c>) and a self-compare (<c>c == c</c>) are DELIBERATELY declined here and fall through to
/// <see cref="TranslateComparisonCore"/>'s own $$ROOT-vs-null / $$ROOT-vs-$$ROOT mechanism — a document-
/// identity check needing no key lookup at all. THIS file only handles the remaining case that mechanism
/// explicitly does not: comparing the root entity against a DIFFERENT, non-null entity value, which is EF's
/// key-based equality semantics, not document equality, and needs genuinely different machinery (see
/// "Mechanism" below).
/// </para>
/// <para>
/// <b>Scope, deliberately narrow.</b> Only the WHOLE-ROOT-ENTITY shape is handled: one operand must be
/// reference-identical (<see cref="object.ReferenceEquals(object, object)"/>, never by type alone) to
/// <see cref="SelfParam"/> — the query's own root lambda parameter — and the other must be a captured/inline,
/// non-null entity value (a <see cref="ConstantExpression"/>, a <see cref="MemberInitExpression"/>/
/// <see cref="NewExpression"/> object initializer, or an EF query parameter of the same CLR type). Both the
/// binary <c>==</c>/<c>!=</c> spelling and the <c>.Equals(...)</c> spelling (the ONLY one available for a
/// composite-key entity type, since C# doesn't synthesize a <c>==</c> operator for one) are recognized. A
/// reference-navigation-typed operand (<c>o.Customer == local</c>) and a two-sided join entity comparison
/// are out of scope here — the latter is EF-216 (cross-document navigation), tracked separately, and stays
/// a fallback.
/// </para>
/// <para>
/// <b>Mechanism.</b> The comparison is rewritten into a conjunction/disjunction of primary-key-property
/// comparisons, mirroring relational's own rewrite: <c>Equal</c> → AND of per-key equalities (OR of
/// per-key nullity checks against a <see langword="null"/> comparand); <c>NotEqual</c> → the exact
/// De Morgan complement (OR of per-key inequalities; AND of per-key non-nullity checks) — safe to invert
/// outright because <c>$eq</c>/<c>$ne</c> partition the value space (see the negator invariant in this
/// area's <c>AGENTS.md</c>). Composite-PK components resolve through the SAME <c>_id.&lt;element&gt;</c>
/// dotted-path convention <see cref="MongoExpressionTranslator.TryResolveMember"/> already uses, so the
/// rendered <c>$match</c> is identical in shape to an ordinary hand-written composite-key predicate.
/// </para>
/// <para>
/// <b>Extracting the comparand's key value.</b> A <see cref="ConstantExpression"/> operand (a captured
/// local, OR an inline <c>new Customer { CustomerID = "ANATR" }</c> — EF's
/// <c>ParameterExtractingExpressionVisitor</c> parameterizes both identically, as a whole object with no
/// reference to the query source) has its key member extracted immediately via
/// <see cref="IPropertyBase.GetGetter"/> into a <see cref="MongoConstantExpression"/>. A QUERY PARAMETER
/// operand can't be extracted at translate (compile) time — the whole-entity runtime value isn't known
/// until execution — so it is carried as a <see cref="MongoParameterExpression"/> with
/// <see cref="MongoParameterExpression.ExtractFromEntityValue"/> set; <see cref="PlaceholderTable"/> /
/// <see cref="MongoPipelineFactory"/> apply the same <see cref="IPropertyBase.GetGetter"/> extraction
/// per execution, once the raw parameter value is available (mirrors the deferred regex-pattern
/// computation <see cref="PlaceholderTable.CreateRegexPlaceholder"/> already does for a parameterized
/// search term — same reason: the value needed to finish translating isn't known until Build time).
/// </para>
/// <para>
/// A shadow-property primary key declines (falls back) rather than being admitted: a plain captured
/// POCO has no shadow-property storage for <see cref="IPropertyBase.GetGetter"/> to read from, so there
/// is no correct value to extract.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateEntityEquality(BinaryExpression be, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateEntityEquality(be.Left, be.Right, be.NodeType == ExpressionType.NotEqual, out result);

    /// <summary>
    /// The <c>.Equals(...)</c> spelling of entity equality — composite-key entity types have no <c>==</c>
    /// operator (C# doesn't synthesize one), so EF Core's own Northwind spec suite (and presumably real
    /// user queries over such types) spells this comparison as <c>od.Equals(local)</c> instead. Recognized
    /// as the exact same shape, minus a <c>!=</c> form (there is no "NotEquals" method) — a negated
    /// <c>!od.Equals(local)</c> arrives as a <see cref="UnaryExpression"/> wrapping this method call, which
    /// is outside this rewrite's scope and is handled (or declined) by the ordinary <c>Not</c> dispatch.
    /// </summary>
    private bool TryTranslateEntityEqualityCall(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (call.Method.Name != nameof(Equals) || call.Object is null || call.Arguments.Count != 1)
            return false;

        return TryTranslateEntityEquality(call.Object, call.Arguments[0], isNotEqual: false, out result);
    }

    private bool TryTranslateEntityEquality(Expression leftSide, Expression rightSide, bool isNotEqual, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null)
            return false;

        var left = Unwrap(leftSide);
        var right = Unwrap(rightSide);

        var leftIsSelf = ReferenceEquals(left, SelfParam);
        var rightIsSelf = ReferenceEquals(right, SelfParam);

        // Exactly one side must be the bare root entity — a self-compare (c == c, declines to the ordinary
        // path, which has no coverage for it either) or neither side being the root entity isn't this shape.
        if (leftIsSelf == rightIsSelf)
            return false;

        var otherSide = leftIsSelf ? right : left;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null)
            return false;

        var keyProperties = primaryKey.Properties;
        if (keyProperties.Any(p => p.IsShadowProperty()))
            return false;

        // A null comparand is NOT this rewrite's concern — TranslateComparisonCore's own whole-entity
        // null/self-equality check (see its remarks) already handles `c == null`/`c != null` via the
        // $$ROOT-vs-null mechanism, for both single- and composite-key entities alike, so this declines
        // and falls through to it. (A literal `null` compiles to `Expression.Constant(null)` with NO type
        // argument, which C# gives Type == typeof(object) anyway — never the entity's own CLR type — so the
        // type check below would reject it regardless; this early-out just makes the deferral explicit.)
        if (otherSide is ConstantExpression { Value: null })
            return false;

        if (otherSide.Type != _entityType.ClrType)
            return false;

        result = CombineKeyComparisons(
            keyProperties,
            keyProperty =>
            {
                if (!TryExtractEntityMemberValue(otherSide, keyProperty, out var valueExpr))
                    return null;

                return new MongoBinaryExpression(
                    isNotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
                    new MongoFieldExpression(keyProperty, GetKeyFieldPath(keyProperty)),
                    valueExpr);
            },
            // Equal: every key component must match (AND). NotEqual: the exact complement — any key
            // component differing is enough (OR).
            combineWithAnd: !isNotEqual);

        return result is not null;
    }

    private static MongoExpression? CombineKeyComparisons(
        IReadOnlyList<IProperty> keyProperties, Func<IProperty, MongoExpression?> buildComparison, bool combineWithAnd)
    {
        MongoExpression? combined = null;
        foreach (var keyProperty in keyProperties)
        {
            var comparison = buildComparison(keyProperty);
            if (comparison is null)
                return null;

            combined = combined is null
                ? comparison
                : new MongoBinaryExpression(
                    combineWithAnd ? MongoBinaryOperator.AndAlso : MongoBinaryOperator.OrElse,
                    combined, comparison);
        }

        return combined;
    }

    private static string GetKeyFieldPath(IProperty property)
        => IsCompositeKeyComponent(property) ? "_id." + property.GetElementName() : property.GetElementName();

    /// <summary>
    /// Extracts <paramref name="property"/>'s value from the entity-typed comparand <paramref name="entityValue"/>.
    /// Handles two distinct shapes EF hands us, un-normalized: a captured local (or an inline
    /// <c>new Customer{...}</c> whose OWN sub-expressions are — see remarks) survives as a
    /// <see cref="ConstantExpression"/>/query-parameter WHOLE-ENTITY value needing per-property
    /// GETTER-based extraction, whereas an inline <c>new Customer { CustomerID = "ANATR" }</c> is NEVER
    /// itself collapsed into a single parameter (there is nothing for
    /// <c>ParameterExtractingExpressionVisitor</c> to extract — the object-initializer syntax has no
    /// captured runtime state at its own top level), so it survives as a genuine
    /// <see cref="MemberInitExpression"/>/<see cref="NewExpression"/> whose PER-MEMBER bindings must be
    /// matched to <paramref name="property"/> by name instead.
    /// </summary>
    private static bool TryExtractEntityMemberValue(Expression entityValue, IProperty property, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        switch (entityValue)
        {
            case ConstantExpression { Value: { } instance }:
                result = new MongoConstantExpression(property.GetGetter().GetClrValue(instance), property);
                return true;

            case MemberInitExpression memberInit:
                var assignment = memberInit.Bindings.OfType<MemberAssignment>()
                    .FirstOrDefault(b => MemberMatchesProperty(b.Member, property));
                return assignment is not null && TryTranslateBoundMemberValue(assignment.Expression, property, out result);

            case NewExpression { Members: { } members } newExpression:
                for (var i = 0; i < members.Count; i++)
                {
                    if (MemberMatchesProperty(members[i], property))
                        return TryTranslateBoundMemberValue(newExpression.Arguments[i], property, out result);
                }

                return false;

            case var parameterNode when NativeQueryParameter.TryGetQueryParameterName(parameterNode, out var parameterName):
                result = new MongoParameterExpression(parameterName, property, extractFromEntityValue: true);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Translates the expression bound to one member of an inline <c>new Customer{...}</c> — a plain
    /// constant, or (when that ONE member was itself a separately captured variable, e.g.
    /// <c>new Customer { CustomerID = someLocal }</c>) an ordinary EF query parameter. Never itself a
    /// further whole-entity value, so — unlike <see cref="TryExtractEntityMemberValue"/>'s own parameter
    /// case — no per-execution extraction step is needed here.
    /// </summary>
    private static bool TryTranslateBoundMemberValue(Expression memberValue, IProperty property, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = memberValue switch
        {
            ConstantExpression constant => new MongoConstantExpression(constant.Value, property),
            var node when NativeQueryParameter.TryGetQueryParameterName(node, out var parameterName)
                => new MongoParameterExpression(parameterName, property),
            _ => null
        };

        return result is not null;
    }

    private static bool MemberMatchesProperty(MemberInfo member, IProperty property)
        => member == property.PropertyInfo || member == property.FieldInfo || member.Name == property.Name;

    /// <summary>
    /// <c>customers.Contains(c)</c> — the LIST generalization of
    /// <see cref="TryTranslateEntityEquality(BinaryExpression, out MongoExpression?)"/>'s single-comparand shape: the root entity tested against a CLIENT-SIDE collection of entity values
    /// (rather than exactly one). Rewritten to a primary-key <c>$in</c> rather than an OR-chain of per-element
    /// key comparisons — unlike the driver-LINQ bridge's <c>TryRewriteEntityContains</c>, which runs fresh per
    /// execution and can size an OR-chain to the list's actual runtime length, this runs ONCE at compile time
    /// and the list's length isn't known until execution, so the rewrite must be a single, list-length-
    /// independent construct. A <see langword="null"/> element (comparing the root entity, which a real
    /// fetched document is never absent-as-null, to a null list entry) can never match a real row either way,
    /// so it is passed through into the <c>$in</c> values verbatim (mirroring Cosmos's own
    /// <c>IN (null, "ALFKI")</c> translation of the same shape) rather than filtered out — cheaper than
    /// proving it dead at every call site.
    /// </summary>
    /// <remarks>
    /// Scoped to a SINGLE-property, non-shadow primary key, like
    /// <see cref="TryTranslateEntityEquality(BinaryExpression, out MongoExpression?)"/> —
    /// a composite key would need a multi-field <c>$in</c> (an array of per-key-component tuples), which
    /// <see cref="MongoInExpression"/> cannot express; that shape declines (falls back) rather than being
    /// admitted here.
    /// </remarks>
    internal bool TryTranslateEntityListContains(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null || !TryMatchContainsMethod(call, out var collection, out var item)
            || !ReferenceEquals(Unwrap(item), SelfParam))
            return false;

        var elementType = GetEnumerableElementType(Unwrap(collection).Type);
        if (elementType != _entityType.ClrType)
            return false;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1)
            return false; // composite keys need a multi-field $in — out of scope here, decline

        var keyProperty = primaryKey.Properties[0];
        if (keyProperty.IsShadowProperty())
            return false;

        var valuesNode = TranslateEntityKeyInValues(collection, keyProperty);
        if (valuesNode is null)
            return false;

        result = new MongoInExpression(
            new MongoFieldExpression(keyProperty, GetKeyFieldPath(keyProperty)), valuesNode, negated: false);
        return true;
    }

    /// <summary>
    /// Translates the collection side of <see cref="TryTranslateEntityListContains"/> into the <c>$in</c>
    /// values node: a <see cref="MongoConstantExpression"/> of extracted keys for a compile-time-known list
    /// (each element's key read immediately via <see cref="IPropertyBase.GetGetter"/>, a null element passed
    /// through as <see langword="null"/>), or a <see cref="MongoParameterExpression"/> with
    /// <see cref="MongoParameterExpression.ExtractEntityKeyFromArrayElements"/> set for a query-parameter
    /// list — the per-element analog of <see cref="TryExtractEntityMemberValue"/>'s single-entity parameter
    /// case, deferring the extraction to per-execution time via <see cref="PlaceholderTable"/> /
    /// <see cref="MongoPipelineFactory"/>.
    /// </summary>
    private static MongoExpression? TranslateEntityKeyInValues(Expression collectionExpr, IProperty keyProperty)
    {
        var unwrapped = Unwrap(collectionExpr);

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable items })
        {
            var getter = keyProperty.GetGetter();
            var keys = new List<object?>();
            foreach (var item in items)
                keys.Add(item is null ? null : getter.GetClrValue(item));

            return new MongoConstantExpression(keys, keyProperty);
        }

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, keyProperty, extractEntityKeyFromArrayElements: true);

        return null;
    }
}
