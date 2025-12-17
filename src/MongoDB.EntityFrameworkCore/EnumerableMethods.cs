// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally EF Core EnumerableMethods.cs

using System.Collections;
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

        GetMethod(
            nameof(Enumerable.All), 1,
            types =>
            [
                typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], typeof(bool))
            ]);

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
}
