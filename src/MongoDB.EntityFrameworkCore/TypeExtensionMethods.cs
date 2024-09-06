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
    public static Type? TryGetItemType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
        this Type type)
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

    internal static ConstructorInfo? GetDeclaredConstructor(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        this Type type,
        Type[]? types)
    {
        types ??= [];

        return type.GetTypeInfo().DeclaredConstructors
            .SingleOrDefault(
                c => !c.IsStatic
                     && c.GetParameters().Select(p => p.ParameterType).SequenceEqual(types))!;
    }

    internal static IEnumerable<Type> GetGenericTypeImplementations(
        this Type type,
        Type interfaceOrBaseType)
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

    internal static bool HasInterface(this Type type, Type[] interfaces)
    {
        var typeToConsider = !type.IsGenericType || type.IsGenericTypeDefinition
            ? type
            : type.GetGenericTypeDefinition();

        return interfaces.Contains(typeToConsider) ||
               typeToConsider.GetInterfaces().Any(i => i.IsGenericType && interfaces.Contains(i.GetGenericTypeDefinition()));
    }

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
    /// Get the underlying type of a nullable <see cref="Type"/> or just the type itself
    /// if it is not nullable.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> to be considered.</param>
    /// <returns>The <see cref="Type"/> of <see cref="Nullable{T}"/> if it is nullable
    /// or the direct type that was passed in if not nullable.</returns>
    public static Type UnwrapNullableType(this Type type)
        => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>
    /// Check if a type is nullable or not.
    /// </summary>
    /// <param name="type">The <see cref="Type"/> to check.</param>
    /// <returns><see langword="true"/> is the object is nullable, otherwise <see langword="false"/>.</returns>
    public static bool IsNullableType(this Type type)
        => !type.IsValueType || type.IsNullableValueType();

    /// <summary>
    /// Check if a value type is nullable or not.
    /// </summary>
    /// <param name="type">The value <see cref="Type"/> to check.</param>
    /// <returns><see langword="true"/> is the object is nullable, otherwise <see langword="false"/>.</returns>
    public static bool IsNullableValueType(this Type type)
        => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

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
