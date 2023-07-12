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

/// <summary>
/// Various helper method extensions relating to <see cref="Type"/>/.
/// </summary>
internal static class TypeExtensions
{
    /// <summary>
    /// Determine the item (sequence) type of an <see cref="IEnumerable{T}"/> or <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> to be examined.</param>
    /// <returns>The <see cref="Type"/> of items in the sequence.</returns>
    public static Type? TryGetItemType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
        => type.TryGetItemType(typeof(IEnumerable<>))
           ?? type.TryGetItemType(typeof(IAsyncEnumerable<>));

    /// <summary>
    /// Determine the generic item type of a given type.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> being examined.</param>
    /// <param name="interfaceOrBaseType">The generic <see cref="Type"/> it implements.</param>
    /// <returns>The item <see cref="Type"/> that generic interface uses.</returns>
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

    private static IEnumerable<Type> GetGenericTypeImplementations(this Type type, Type interfaceOrBaseType)
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

    /// <summary>
    /// Try to find a constructor for the <paramref name="sequenceType"/> that takes an parameter of
    /// <paramref name="parameterType"/>.
    /// </summary>
    /// <param name="sequenceType">The sequence type being examined.</param>
    /// <param name="parameterType">The parameter the constructor must support.</param>
    /// <returns>The <see cref="ConstructorInfo"/> if a matching constructor is found, otherwise <seealso langref="null"/>.</returns>
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

    public static Type GetPropertyOrFieldType(this MemberInfo member)
        => member.MemberType switch
        {
            MemberTypes.Property => ((PropertyInfo)member).PropertyType,
            MemberTypes.Field => ((FieldInfo)member).FieldType,
            _ => throw new NotSupportedException("Only properties and fields are supported as binding targets")
        };

    /// <summary>
    /// Create a nullable version of a <see cref="Type"/>.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> to be made nullable.</param>
    /// <returns>The nullable version of <paramref name="type"/>.</returns>
    public static Type MakeNullable(this Type type)
        => type.IsNullableType()
            ? type
            : typeof(Nullable<>).MakeGenericType(type);

    /// <summary>
    /// Check with a type is nullable or not.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> to check.</param>
    /// <returns><see langref="true"/> is the object is nullable, otherwise <see langref="false"/>.</returns>
    public static bool IsNullableType(this Type type)
        => !type.IsValueType || type.IsNullableValueType();

    private static bool IsNullableValueType(this Type type)
        => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

    /// <summary>
    /// Find the <see cref="IEnumerable{T}"/> interface on a given <paramref name="sequenceType"/>.
    /// </summary>
    /// <param name="sequenceType">The sequence type to examine.</param>
    /// <returns>The <see cref="Type"/> of <see cref="IEnumerable{T}"/> found on the <paramref name="sequenceType"/>
    /// or <see langref="null"/> if none could be found.</returns>
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
