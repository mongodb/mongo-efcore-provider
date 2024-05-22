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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// A base <see cref="ValueConverter{TModel,TProvider}"/> for converting between
/// <see cref="Decimal128"/> and <see cref="decimal"/>.
/// </summary>
/// <typeparam name="TModel">The type in the entity model.</typeparam>
/// <typeparam name="TProvider">The type to use in the provider.</typeparam>
public abstract class Decimal128DecimalConverter<TModel, TProvider> : ValueConverter<TModel, TProvider>
{
    /// <summary>
    /// Creates a new instance of the <see cref="Decimal128DecimalConverter{TModel, TProvider}"/> class.
    /// </summary>
    /// <param name="convertToProviderExpression">The expression to convert from the model to the provider.</param>
    /// <param name="convertFromProviderExpression">The expression to convert from the provider to the model.</param>
    /// <param name="mappingHints">Optional <see cref="ConverterMappingHints"/> that may be considered.</param>
    protected Decimal128DecimalConverter(
        Expression<Func<TModel, TProvider>> convertToProviderExpression,
        Expression<Func<TProvider, TModel>> convertFromProviderExpression,
        ConverterMappingHints? mappingHints = null)
        : base(
            convertToProviderExpression,
            convertFromProviderExpression,
            mappingHints)
    {
    }

    /// <summary>
    /// An expression to convert from a <see cref="decimal"/> to a <see cref="Decimal128"/>.
    /// </summary>
    /// <returns>The expression that performs the conversion.</returns>
    protected static Expression<Func<decimal, Decimal128>> ConvertToDecimal128()
        => v => new Decimal128(v);

    /// <summary>
    /// An expression to convert from an <see cref="Decimal128"/> to a <see cref="decimal"/>.
    /// </summary>
    /// <returns>The expression that performs the conversion.</returns>
    protected static Expression<Func<Decimal128, decimal>> ConvertToDecimal()
        => v => Decimal128.ToDecimal(v);
}
