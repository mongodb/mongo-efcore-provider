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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> predicate to the
/// MongoDB <c>$match</c>-filter body <see cref="BsonValue"/>
/// (e.g. <c>{ Age: { $gt: 21 } }</c> — without the outer <c>{ $match: … }</c> wrapper,
/// which is the stage-walker's responsibility).
/// </summary>
/// <remarks>
/// <para>
/// This class is <em>pure</em>: it has no dependency on <c>IEntityType</c> or
/// <c>QueryContext</c>. All parity guards (nullable-equality rejection, numeric-cast
/// rejection, etc.) were applied by <see cref="MongoExpressionTranslator"/> upstream;
/// the renderer simply emits BSON.
/// </para>
/// <para>
/// <see cref="MongoConstantExpression"/> values are serialized inline using the
/// <see cref="IProperty"/> carried inside the node, and baked into the returned template.
/// <see cref="MongoParameterExpression"/> sites are recorded as placeholder sentinels in
/// the supplied <see cref="PlaceholderTable"/> for per-execution substitution by Task 10.
/// </para>
/// </remarks>
internal sealed class MongoQueryLanguageRenderer
{
    /// <summary>
    /// Renders <paramref name="predicate"/> to a <c>$match</c>-filter body.
    /// </summary>
    /// <param name="predicate">
    /// The root <see cref="MongoExpression"/> to render. Must be a predicate-shaped node
    /// (i.e. its runtime type must be <see cref="bool"/>).
    /// </param>
    /// <param name="placeholders">
    /// Receives one entry per <see cref="MongoParameterExpression"/> encountered.
    /// Each entry's corresponding sentinel is embedded in the returned <see cref="BsonValue"/>.
    /// </param>
    /// <returns>
    /// A <see cref="BsonDocument"/> representing the filter body, suitable for use as the
    /// value of a <c>$match</c> pipeline stage document.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type not handled by this renderer (defensive; should not happen
    /// for predicates that passed the translator's acceptance set).
    /// </exception>
    public BsonValue Render(MongoExpression predicate, PlaceholderTable placeholders)
        => RenderNode(predicate, placeholders);

    // ------------------------------------------------------------------
    // Core dispatch
    // ------------------------------------------------------------------

    private BsonValue RenderNode(MongoExpression node, PlaceholderTable placeholders)
        => node switch
        {
            MongoBinaryExpression binary => RenderBinary(binary, placeholders),
            MongoUnaryExpression unary => RenderUnary(unary, placeholders),
            MongoFieldExpression field => RenderBareField(field, placeholders),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoQueryLanguageRenderer does not support node type '{node.GetType().Name}'.")
        };

    // ------------------------------------------------------------------
    // Binary nodes (AndAlso / OrElse / comparisons)
    // ------------------------------------------------------------------

    private BsonDocument RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders)
    {
        switch (binary.Operator)
        {
            case MongoBinaryOperator.AndAlso:
            {
                var left = (BsonDocument)RenderNode(binary.Left, placeholders);
                var right = (BsonDocument)RenderNode(binary.Right, placeholders);
                return CombineAnd(left, right);
            }

            case MongoBinaryOperator.OrElse:
            {
                var left = (BsonDocument)RenderNode(binary.Left, placeholders);
                var right = (BsonDocument)RenderNode(binary.Right, placeholders);
                return CombineOr(left, right);
            }

            default:
                return RenderComparison(binary, placeholders);
        }
    }

    private BsonDocument RenderComparison(MongoBinaryExpression binary, PlaceholderTable placeholders)
    {
        // MongoExpressionTranslator always places the MongoFieldExpression on the Left
        // with the operator already mirrored when necessary (see TranslateComparison).
        if (binary.Left is not MongoFieldExpression field)
            throw new NativeTranslationNotSupportedException(
                $"Expected MongoFieldExpression on the left side of a comparison; got '{binary.Left.GetType().Name}'.");

        var elementName = field.ElementName;
        var value = RenderValue(binary.Right, placeholders);

        var op = binary.Operator switch
        {
            MongoBinaryOperator.Equal => null,              // bare { field: value }
            MongoBinaryOperator.NotEqual => "$ne",
            MongoBinaryOperator.LessThan => "$lt",
            MongoBinaryOperator.LessThanOrEqual => "$lte",
            MongoBinaryOperator.GreaterThan => "$gt",
            MongoBinaryOperator.GreaterThanOrEqual => "$gte",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported comparison operator '{binary.Operator}'.")
        };

        return op is null
            ? new BsonDocument(elementName, value)
            : new BsonDocument(elementName, new BsonDocument(op, value));
    }

    // ------------------------------------------------------------------
    // Unary nodes (Not)
    // ------------------------------------------------------------------

    private BsonDocument RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException(
                $"Unsupported unary operator '{unary.Operator}'.");

        if (unary.Operand is not MongoFieldExpression field)
            throw new NativeTranslationNotSupportedException(
                "MongoQueryLanguageRenderer only supports Not over a MongoFieldExpression.");

        // !boolProperty → { field: { $ne: true } }
        // (Matches driver-LINQ rendering; also matches missing/null-field semantics.)
        var trueValue = ToBsonValue(field.Property, true);
        return new BsonDocument(field.ElementName, new BsonDocument("$ne", trueValue));
    }

    // ------------------------------------------------------------------
    // Bare boolean field (used as a top-level predicate)
    // ------------------------------------------------------------------

    private BsonDocument RenderBareField(MongoFieldExpression field, PlaceholderTable placeholders)
    {
        // A bare bool property used as a predicate → { field: true }
        var trueValue = ToBsonValue(field.Property, true);
        return new BsonDocument(field.ElementName, trueValue);
    }

    // ------------------------------------------------------------------
    // Value rendering (constants vs. parameters)
    // ------------------------------------------------------------------

    internal BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)
    {
        switch (node)
        {
            case MongoConstantExpression constant:
            {
                if (constant.ForSerialization is null)
                    return BsonValue.Create(constant.Value);
                return ToBsonValue(constant.ForSerialization, constant.Value);
            }

            case MongoParameterExpression parameter:
            {
                if (parameter.ForSerialization is null)
                    return placeholders.CreatePlaceholder(parameter.Name, serializer: null);
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization);
                return placeholders.CreatePlaceholder(parameter.Name, info.Serializer);
            }

            default:
                throw new NativeTranslationNotSupportedException(
                    $"Cannot render value node of type '{node.GetType().Name}'.");
        }
    }

    // ------------------------------------------------------------------
    // Serialization helper (ported verbatim from the spike MongoPredicateTranslator)
    // ------------------------------------------------------------------

    /// <summary>
    /// Serializes <paramref name="value"/> to a <see cref="BsonValue"/> using the property's
    /// serializer, coercing the CLR type first so the serializer's hard cast succeeds.
    /// </summary>
    private static BsonValue ToBsonValue(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        try
        {
            // Coerce to the property's CLR type (compile-time path); the factory coerces to the
            // serializer's ValueType — these differ for value-converted properties, so each call site
            // passes its own target into the shared helper.
            value = BsonValueSerializer.Coerce(property.ClrType, value);
            return BsonValueSerializer.SerializeThroughWriter(info.Serializer, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            throw new NativeTranslationNotSupportedException(
                $"Native predicate translation cannot serialize value '{value}' for property '{property.Name}'.");
        }
    }

    // ------------------------------------------------------------------
    // AND / OR combining helpers (ported verbatim from the spike MongoPredicateTranslator)
    // ------------------------------------------------------------------

    /// <summary>
    /// Combines two filter documents with AND, merging fields into a single document when all top-level
    /// keys are distinct and non-operator. Falls back to an explicit <c>$and</c> array when keys collide
    /// and operator sub-documents cannot be merged (e.g. two <c>$gt</c> on the same field), or when
    /// either document contains multiple elements or an operator key at the top level.
    /// Nested <c>$and</c> operands are flattened so chained predicates do not nest redundantly.
    /// Ported verbatim from the spike.
    /// </summary>
    private static BsonDocument CombineAnd(BsonDocument left, BsonDocument right)
    {
        var clauses = new List<BsonDocument>();
        AddAndOperand(clauses, left);
        AddAndOperand(clauses, right);

        var merged = new BsonDocument();
        foreach (var clause in clauses)
        {
            // A clause is mergeable only if it is a single-field document whose key is not an operator.
            if (clause.ElementCount != 1 || clause.GetElement(0).Name.StartsWith('$'))
                return new BsonDocument("$and", new BsonArray(clauses));

            var element = clause.GetElement(0);
            if (!merged.Contains(element.Name))
            {
                merged.Add(element);
                continue;
            }

            // Same field appears twice (e.g. x > a && x < b). Merge the operator sub-documents when
            // possible: { x: { $gt: a, $lt: b } }. Fall back to $and on conflict or non-operator values.
            if (TryMergeOperatorDocs(merged[element.Name], element.Value, out var combined))
                merged[element.Name] = combined;
            else
                return new BsonDocument("$and", new BsonArray(clauses));
        }

        return merged;
    }

    private static bool TryMergeOperatorDocs(BsonValue existing, BsonValue addition, out BsonValue combined)
    {
        combined = BsonNull.Value;
        if (existing is not BsonDocument ed || addition is not BsonDocument ad)
            return false;
        if (!IsAllOperators(ed) || !IsAllOperators(ad))
            return false;

        var result = new BsonDocument();
        result.AddRange(ed);
        foreach (var op in ad)
        {
            if (result.Contains(op.Name))
                return false; // overlapping operator (e.g. two $gt) cannot merge
            result.Add(op);
        }

        combined = result;
        return true;
    }

    private static bool IsAllOperators(BsonDocument doc)
    {
        if (doc.ElementCount == 0)
            return false;
        foreach (var e in doc)
        {
            if (!e.Name.StartsWith('$'))
                return false;
        }

        return true;
    }

    private static void AddAndOperand(List<BsonDocument> clauses, BsonDocument doc)
    {
        if (doc.ElementCount == 1 && doc.GetElement(0).Name == "$and" && doc[0] is BsonArray array)
        {
            foreach (var item in array)
                clauses.Add((BsonDocument)item);
        }
        else
        {
            clauses.Add(doc);
        }
    }

    /// <summary>
    /// Combines two filter documents with OR into a flat <c>$or</c> array,
    /// flattening any nested <c>$or</c> operands to match driver-LINQ rendering.
    /// Ported verbatim from the spike.
    /// </summary>
    private static BsonDocument CombineOr(BsonDocument left, BsonDocument right)
    {
        var clauses = new BsonArray();
        AddOrOperand(clauses, left);
        AddOrOperand(clauses, right);
        return new BsonDocument("$or", clauses);
    }

    private static void AddOrOperand(BsonArray clauses, BsonDocument doc)
    {
        if (doc.ElementCount == 1 && doc.GetElement(0).Name == "$or" && doc[0] is BsonArray array)
        {
            foreach (var item in array)
                clauses.Add(item);
        }
        else
        {
            clauses.Add(doc);
        }
    }
}
