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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

// ReSharper disable once CheckNamespace
namespace System;

internal static class TypeExtensions
{
    public static Type? TryGetItemType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
        => type.TryGetItemType(typeof(IEnumerable<>))
           ?? type.TryGetItemType(typeof(IAsyncEnumerable<>));

    public static Type? TryGetItemType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
        this Type type,
        Type interfaceOrBaseType)
    {
        if (type.IsGenericTypeDefinition)
        {
            return null;
        }

        var implementations = GetGenericTypeImplementations(type, interfaceOrBaseType).Take(2).ToArray();
        return implementations.Length != 1 ? null : implementations[0].GenericTypeArguments.FirstOrDefault();
    }

    public static IEnumerable<Type> GetGenericTypeImplementations(this Type type, Type interfaceOrBaseType)
    {
        if (type.IsGenericTypeDefinition)
        {
            yield break;
        }

        var implementationTypes = interfaceOrBaseType.IsInterface
            ? type.GetInterfaces()
            : type.GetBaseTypes();

        foreach (var baseType in implementationTypes.Append(type))
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == interfaceOrBaseType)
            {
                yield return baseType;
            }
        }
    }

    public static ConstructorInfo? TryFindConstructorWithParameter(this Type sequenceType, Type parameterType)
    {
        foreach (var constructor in sequenceType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(parameterType))
            {
                return constructor;
            }
        }

        return null;
    }

    public static Type? TryFindIEnumerable(this Type sequenceType)
    {
        var itemType = sequenceType.TryGetItemType();
        if (itemType == null || sequenceType == typeof(string))
        {
            return null;
        }

        if (sequenceType.IsArray)
        {
            return typeof(IEnumerable<>).MakeGenericType(itemType);
        }

        var findIEnumerable = typeof(IEnumerable<>).MakeGenericType(itemType);
        foreach (var candidateInterface in sequenceType.GetInterfaces())
        {
            if (candidateInterface.IsAssignableFrom(sequenceType))
                return findIEnumerable;
        }

        return null;
    }

    private static IEnumerable<Type> GetBaseTypes(this Type type)
    {
        var currentType = type.BaseType;
        while (currentType != null)
        {
            yield return currentType;
            currentType = currentType.BaseType;
        }
    }
}
