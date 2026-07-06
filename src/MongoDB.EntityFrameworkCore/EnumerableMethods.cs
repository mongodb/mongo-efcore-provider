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

        // Canonical set of the standard aggregate operators (every overload, including the numeric
        // specializations of Sum/Min/Max/Average) declared on both Enumerable and Queryable, keyed by
        // the generic method definition so a constructed call can be matched by reference rather than by
        // name. Grouping aggregates can surface as either Enumerable or Queryable calls. See IsAggregate.
        bool IsAggregateName(string name)
            => name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                or nameof(Enumerable.Sum) or nameof(Enumerable.Min)
                or nameof(Enumerable.Max) or nameof(Enumerable.Average);

        AggregateMethods = new[] { typeof(Enumerable), typeof(Queryable) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(mi => IsAggregateName(mi.Name))
            .ToHashSet();

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

    private static HashSet<MethodInfo> AggregateMethods { get; }

    /// <summary>
    /// Whether <paramref name="method"/> is one of the standard LINQ aggregate operators
    /// (<c>Count</c>/<c>LongCount</c>/<c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c>) declared on
    /// <see cref="Enumerable"/> or <see cref="Queryable"/>, matched by reference against the canonical
    /// definitions rather than by name.
    /// </summary>
    public static bool IsAggregate(MethodInfo method)
        => AggregateMethods.Contains(method.IsGenericMethod ? method.GetGenericMethodDefinition() : method);
}
