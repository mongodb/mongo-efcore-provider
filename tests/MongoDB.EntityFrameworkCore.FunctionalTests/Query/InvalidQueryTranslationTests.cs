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

using System.Linq.Expressions;
using System.Reflection;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class InvalidQueryTranslationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public Guid _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void ExecuteDelete_throws_invalid_operation_exception()
    {
        using var db = SingleEntityDbContext.Create(database.CreateCollection<SimpleEntity>());

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.ExecuteDelete());
        Assert.Contains("ExecuteDelete", ex.Message);
        Assert.Contains("LINQ expression", ex.Message);
    }
}

static class FakeQueryableExtensions
{
    internal static int ExecuteDelete<TSource>(this IQueryable<TSource> source)
        => source.Provider.Execute<int>(Expression.Call(ExecuteDeleteMethodInfo.MakeGenericMethod(typeof(TSource)),
            source.Expression));

    private static readonly MethodInfo ExecuteDeleteMethodInfo
        = typeof(FakeQueryableExtensions).GetTypeInfo().GetDeclaredMethod(nameof(ExecuteDelete))!;
}
