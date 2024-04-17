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

using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Internal use only.
/// A query ready to be executed by the MongoDB LINQ provider.
/// </summary>
/// <param name="Query">A LINQ query <see cref="Expression"/> compatible with the MongoDB LINQ provider.</param>
/// <param name="Cardinality">Whether many or a single result are expected (or enforced) as a <see cref="ResultCardinality"/>.</param>
/// <param name="Provider">The MongoDB V3 LINQ <see cref="IQueryProvider"/> that will execute this query.</param>
/// <param name="CollectionNamespace">The <see cref="CollectionNamespace"/> this query will run against.</param>
public record MongoExecutableQuery(Expression Query, ResultCardinality Cardinality, IQueryProvider Provider, CollectionNamespace CollectionNamespace);
