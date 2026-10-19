// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally EF Core EnumerableMethods.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MongoDB.EntityFrameworkCore;

internal static class EnumerableMethods
{
    static EnumerableMethods()
    {
        var queryableMethodGroups = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .GroupBy(mi => mi.Name)
            .ToDictionary(e => e.Key, l => l.ToList());

        Cast = GetMethod(nameof(Enumerable.Cast), 1, _ =>
        [
            typeof(IEnumerable)
        ]);

        Select = GetMethod(
            nameof(Enumerable.Select), 2,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], types[1])]);

        SelectWithOrdinal = GetMethod(
            nameof(Enumerable.Select), 2,
            types =>
            [
                typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,,>).MakeGenericType(types[0], typeof(int), types[1])
            ]);

        CountWithoutPredicate = GetMethod(
            nameof(Enumerable.Count), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);

        LongCountWithoutPredicate = GetMethod(
            nameof(Enumerable.LongCount), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);

        // Predicated overloads, mirroring QueryableMethods.CountWithPredicate/LongCountWithPredicate, so a
        // hand-built or Enumerable-spelled Count/LongCount(source, predicate) tree matches a canonical MethodInfo.
        CountWithPredicate = GetMethod(
            nameof(Enumerable.Count), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], typeof(bool))]);

        LongCountWithPredicate = GetMethod(
            nameof(Enumerable.LongCount), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], typeof(bool))]);

        // EF-427 item 1: the bare (no-predicate) reducer overloads, mirroring QueryableMethods.FirstWithoutPredicate
        // etc., so MongoProjectionBindingExpressionVisitor can rebuild a stranded Queryable.First/FirstOrDefault/
        // Single/SingleOrDefault/Any call against its Enumerable equivalent over a materialized CollectionShaperExpression,
        // exactly like the CountWithoutPredicate/LongCountWithoutPredicate arm above already does. Deliberately
        // NARROW: the predicated overloads (FirstWithPredicate etc.) and Sum/Min/Max/Average (per-numeric-type
        // overloads, not a single generic-in-TSource method) are out of scope for this task — see the rebuild
        // arms' own comment in MongoProjectionBindingExpressionVisitor.cs.
        FirstWithoutPredicate = GetMethod(
            nameof(Enumerable.First), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);
        FirstOrDefaultWithoutPredicate = GetMethod(
            nameof(Enumerable.FirstOrDefault), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);
        SingleWithoutPredicate = GetMethod(
            nameof(Enumerable.Single), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);
        SingleOrDefaultWithoutPredicate = GetMethod(
            nameof(Enumerable.SingleOrDefault), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);
        AnyWithoutPredicate = GetMethod(
            nameof(Enumerable.Any), 1,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0])]);

        // The fully-generic (TSource, TResult) selector overloads of Min/Max — used when the selected
        // type is not one of the fixed numeric overloads below.
        MaxWithSelector = GetMethod(
            nameof(Enumerable.Max), 2,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], types[1])]);
        MinWithSelector = GetMethod(
            nameof(Enumerable.Min), 2,
            types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], types[1])]);

        // The Sum/Average/Min/Max selector overloads that are generic only in TSource (their result type is
        // one of the fixed numeric types); overload resolution binds a numeric member selector to these
        // rather than the fully-generic forms above, so we must match against them too.
        var numericTypes = new[]
        {
            typeof(int), typeof(int?), typeof(long), typeof(long?), typeof(float), typeof(float?),
            typeof(double), typeof(double?), typeof(decimal), typeof(decimal?)
        };

        var sumWithSelector = new HashSet<MethodInfo>();
        var averageWithSelector = new HashSet<MethodInfo>();
        var minWithSelector = new HashSet<MethodInfo> { MinWithSelector };
        var maxWithSelector = new HashSet<MethodInfo> { MaxWithSelector };

        foreach (var type in numericTypes)
        {
            sumWithSelector.Add(GetMethod(
                nameof(Enumerable.Sum), 1,
                types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], type)]));
            averageWithSelector.Add(GetMethod(
                nameof(Enumerable.Average), 1,
                types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], type)]));
            minWithSelector.Add(GetMethod(
                nameof(Enumerable.Min), 1,
                types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], type)]));
            maxWithSelector.Add(GetMethod(
                nameof(Enumerable.Max), 1,
                types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], type)]));
        }

        SumWithSelectorMethods = sumWithSelector;
        AverageWithSelectorMethods = averageWithSelector;
        MinWithSelectorMethods = minWithSelector;
        MaxWithSelectorMethods = maxWithSelector;

        MethodInfo GetMethod(string name, int genericParameterCount, Func<Type[], Type[]> parameterGenerator)
        {
            return queryableMethodGroups[name].Single(
                mi => (genericParameterCount == 0 && !mi.IsGenericMethod
                       || mi.IsGenericMethod && mi.GetGenericArguments().Length == genericParameterCount)
                      && mi.GetParameters().Select(e => e.ParameterType).SequenceEqual(
                          parameterGenerator(mi.IsGenericMethod ? mi.GetGenericArguments() : [])));
        }
    }

    public static MethodInfo Cast { get; }
    public static MethodInfo Select { get; }
    public static MethodInfo SelectWithOrdinal { get; }

    public static MethodInfo CountWithoutPredicate { get; }
    public static MethodInfo LongCountWithoutPredicate { get; }
    public static MethodInfo CountWithPredicate { get; }
    public static MethodInfo LongCountWithPredicate { get; }
    public static MethodInfo MinWithSelector { get; }
    public static MethodInfo MaxWithSelector { get; }

    public static MethodInfo FirstWithoutPredicate { get; }
    public static MethodInfo FirstOrDefaultWithoutPredicate { get; }
    public static MethodInfo SingleWithoutPredicate { get; }
    public static MethodInfo SingleOrDefaultWithoutPredicate { get; }
    public static MethodInfo AnyWithoutPredicate { get; }

    private static HashSet<MethodInfo> SumWithSelectorMethods { get; }
    private static HashSet<MethodInfo> AverageWithSelectorMethods { get; }
    private static HashSet<MethodInfo> MinWithSelectorMethods { get; }
    private static HashSet<MethodInfo> MaxWithSelectorMethods { get; }

    /// <summary>True if <paramref name="method"/> is an <c>Enumerable.Sum</c> selector overload.</summary>
    public static bool IsSumWithSelector(MethodInfo method)
        => method.IsGenericMethod && SumWithSelectorMethods.Contains(method.GetGenericMethodDefinition());

    /// <summary>True if <paramref name="method"/> is an <c>Enumerable.Average</c> selector overload.</summary>
    public static bool IsAverageWithSelector(MethodInfo method)
        => method.IsGenericMethod && AverageWithSelectorMethods.Contains(method.GetGenericMethodDefinition());

    /// <summary>True if <paramref name="method"/> is an <c>Enumerable.Min</c> selector overload.</summary>
    public static bool IsMinWithSelector(MethodInfo method)
        => method.IsGenericMethod && MinWithSelectorMethods.Contains(method.GetGenericMethodDefinition());

    /// <summary>True if <paramref name="method"/> is an <c>Enumerable.Max</c> selector overload.</summary>
    public static bool IsMaxWithSelector(MethodInfo method)
        => method.IsGenericMethod && MaxWithSelectorMethods.Contains(method.GetGenericMethodDefinition());
}
