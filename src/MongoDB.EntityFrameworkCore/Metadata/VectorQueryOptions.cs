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

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Query options for running an Atlas Vector Search with <see cref="Microsoft.EntityFrameworkCore.MongoQueryableExtensions.VectorSearch{TSource,TProperty}(System.Linq.IQueryable{TSource},System.Linq.Expressions.Expression{System.Func{TSource,TProperty}},System.Linq.Expressions.Expression{System.Func{TSource,bool}},MongoDB.Driver.QueryVector,int,System.Nullable{VectorQueryOptions})"/>.
/// </summary>
/// <param name="IndexName">The name of the vector search index to use.</param>
/// <param name="NumberOfCandidates">Number of nearest neighbors to use during the search. Value must be less than or equal to 10000, and greater than or equal to the document limit. We recommend that you specify a number at least 20 times higher than the number of documents to return (limit) to increase accuracy.</param>
/// <param name="Exact">If true, then an exact ENN search is used, otherwise an approximate ANN search is used. ENN must be used if the number of candidates is omitted.</param>
public readonly record struct VectorQueryOptions(
    string? IndexName = null,
    int? NumberOfCandidates = null,
    bool Exact = false)
{
}
