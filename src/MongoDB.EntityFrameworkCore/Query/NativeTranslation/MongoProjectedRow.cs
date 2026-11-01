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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// A row of the native one-pass cursor of a scalar projection: the projected document's elements read straight off the cursor into typed
/// values (a nested <see cref="ValueTuple"/>, one slot per distinct projected alias), instead of into a <see cref="MongoDB.Bson.BsonDocument"/>.
/// <see cref="States"/> records, two bits per slot, whether the element was missing, BSON null or present, because what that means (a default,
/// a throw, an empty value) is decided when the shaper reads the slot, as it was when it read the document.
/// </summary>
/// <typeparam name="TValues">The nested value tuple of the slot types, in slot order.</typeparam>
internal readonly struct MongoProjectedRow<TValues>(TValues values, ulong states, object? deferred)
{
    public readonly TValues Values = values;

    public readonly ulong States = states;

    /// <summary>
    /// For a tracked query whose projection holds entities: the <see cref="MongoPendingTracking"/> the row recorded while the driver
    /// deserialized it (see <see cref="MongoQueryContext.BeginDeferral"/>), replayed when the enumerator reaches the row.
    /// </summary>
    public readonly object? Deferred = deferred;

    /// <summary>The state of slot <paramref name="slot"/>: <see cref="MongoProjectedRowState.Missing"/>, <see cref="MongoProjectedRowState.Null"/> or <see cref="MongoProjectedRowState.Present"/>.</summary>
    public static int StateOf(ulong states, int slot) => (int)((states >> (2 * slot)) & 3UL);
}

internal static class MongoProjectedRowState
{
    public const int Missing = 0;
    public const int Null = 1;
    public const int Present = 2;
}

/// <summary>
/// What the shaper does with a slot of a <see cref="MongoProjectedRow{TValues}"/>. Each method is the behavior of the <see cref="BsonBinding"/>
/// read it replaces, given the element's state and value in place of the document, so a projection reads exactly as it did from a
/// <see cref="MongoDB.Bson.BsonDocument"/> (including every error message).
/// </summary>
internal static class MongoProjectedRowReads
{
    // The serializer yields TValue; the shaper asked for T (the same type, or its nullable form), as the BsonBinding casts did.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T? As<T, TValue>(TValue? value)
        => typeof(T) == typeof(TValue) ? Unsafe.As<TValue?, T?>(ref value) : (T?)(object?)value;

    /// <summary><see cref="BsonBinding.GetScalarProjectionValueAtElement{T}"/>: a missing element reads <c>default</c>, an explicit null of a required property throws.</summary>
    public static T? ScalarProjection<T, TValue>(
        int state, TValue? value, string alias, IReadOnlyProperty property, bool nullReadsAsDefault)
    {
        if (state == MongoProjectedRowState.Present)
        {
            return As<T, TValue>(value);
        }

        if (state == MongoProjectedRowState.Null && !property.IsNullable && !nullReadsAsDefault)
        {
            throw new InvalidOperationException(
                $"Document element '{alias}' is null for required non-nullable property '{property.Name}'.");
        }

        return default;
    }

    /// <summary><see cref="BsonBinding.GetPropertyValueAtElement{T}"/>: a missing or null element of a required property throws.</summary>
    public static T? PropertyAtElement<T, TValue>(int state, TValue? value, string alias, IReadOnlyProperty property)
    {
        T? result;
        if (state == MongoProjectedRowState.Present)
        {
            result = As<T, TValue>(value);
        }
        else if (state == MongoProjectedRowState.Null)
        {
            result = default;
        }
        else
        {
            if (property.IsNullable)
            {
                return default;
            }

            throw new InvalidOperationException(
                $"Document element '{alias}' is missing for required non-nullable property '{property.Name}'.");
        }

        if (result == null && !property.IsNullable)
        {
            throw new InvalidOperationException(BsonBinding.RequiredPropertyNullMessage(property));
        }

        return result;
    }

    /// <summary>
    /// Replays the tracking a row deferred, as EF would have done it entity by entity while materializing, and returns what to substitute for
    /// an entity of the row that the state manager resolved to one already tracked (<see langword="null"/> when none was).
    /// </summary>
    public static Dictionary<object, object>? Replay(QueryContext queryContext, object? deferred)
        => deferred is MongoPendingTracking pending ? pending.ReplayWithRemap((MongoQueryContext)queryContext) : null;

    /// <summary>The instance to hand the shaper for an entity slot: the tracked one when the row's own instance lost to it.</summary>
    public static T? Canonical<T>(Dictionary<object, object>? remap, T? value)
        where T : class
        => remap != null && value != null && remap.TryGetValue(value, out var canonical) ? (T)canonical : value;

    /// <summary>A collection the cursor read into a list: an absent or null array reads as an empty collection, as the DOM shaper normalizes it.</summary>
    public static System.Collections.Generic.List<TElement> StreamedList<TElement>(
        int state, System.Collections.Generic.List<TElement>? value)
        => state == MongoProjectedRowState.Present ? value! : new System.Collections.Generic.List<TElement>();

    /// <summary>
    /// An entity the cursor read from a sub-document: a null or absent one reads as <c>null</c>, or throws <paramref name="missingMessage"/> when the
    /// DOM shaper's read of it was required.
    /// </summary>
    public static T? StreamedEntity<T>(int state, T? value, string? missingMessage)
        where T : class
        => state == MongoProjectedRowState.Present
            ? value
            : missingMessage == null ? null : throw new InvalidOperationException(missingMessage);

    /// <summary><see cref="BsonBinding.GetElementValue{T}"/> and its kind-aware sibling: null reads <c>default</c>, a missing element throws unless <typeparamref name="T"/> can hold absence.</summary>
    public static T? Element<T, TValue>(int state, TValue? value, string alias)
    {
        if (state == MongoProjectedRowState.Present)
        {
            return As<T, TValue>(value);
        }

        if (state == MongoProjectedRowState.Null || typeof(T).IsNullableType())
        {
            return default;
        }

        throw new InvalidOperationException($"Document element '{alias}' is missing but required.");
    }
}
