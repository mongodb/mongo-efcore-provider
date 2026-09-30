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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.CanRebindConstantLeafToDocument</c>: only a bare
/// constant/parameter leaf of a round-trip-safe type is rebound to read its projected value per document when it is
/// the first operand of a native Union/Concat. Checked on each operand's real translation. In the set-op gate the
/// predicate is consulted only once <c>HasShaperUnsafeConstantLeaf</c> is true, so the non-constant cases here are
/// the only way to exercise its leaf-kind conjunct. (Parameter leaves need EF's funcletizer, which this harness
/// skips; they are covered by the functional tests.)
/// </summary>
public class NativeSetOperationConstantLeafRebindTests
{
    private class Entity
    {
        public int Id { get; set; }
        public int N { get; set; }
    }

    private static (ShapedQueryExpression Shaped, MongoQueryExpression Mongo) Translate(
        Func<IQueryable<Entity>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<Entity>();
        var query = buildQuery(db.Set<Entity>());

        var compilationContext = db.GetService<IQueryCompilationContextFactory>().Create(async: false);
        var preprocessed = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext)
            .Process(query.Expression);
        var result = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext)
            .Visit(preprocessed);

        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return (shaped, Assert.IsType<MongoQueryExpression>(shaped.QueryExpression));
    }

    private static bool CanRebind(Func<IQueryable<Entity>, IQueryable> operand)
    {
        var (shaped, mongo) = Translate(operand);
        return MongoQueryableMethodTranslatingExpressionVisitor.CanRebindConstantLeafToDocument(shaped, mongo);
    }

    private static MongoExpression SingleLeaf(Func<IQueryable<Entity>, IQueryable> operand)
        => Assert.Single(Translate(operand).Mongo.Select.Projection).Expression;

    [Fact]
    public void Bare_constants_of_round_trip_safe_types_rebind()
    {
        Assert.IsType<MongoConstantExpression>(SingleLeaf(q => q.Select(x => 8)));

        Assert.True(CanRebind(q => q.Select(x => 8)));
        Assert.True(CanRebind(q => q.Select(x => 8L)));
        Assert.True(CanRebind(q => q.Select(x => 0.5)));
        Assert.True(CanRebind(q => q.Select(x => true)));
        Assert.True(CanRebind(q => q.Select(x => "A")));
        Assert.True(CanRebind(q => q.Select(x => (string?)null)));
    }

    // A grouped constant result is a bare constant leaf too, but the rebind's plain projection member isn't what the
    // grouped shaper reads.
    [Fact]
    public void Grouped_constant_result_does_not_rebind()
    {
        Assert.IsType<MongoConstantExpression>(SingleLeaf(q => q.GroupBy(x => x.N).Select(g => 8)));

        Assert.False(CanRebind(q => q.GroupBy(x => x.N).Select(g => 8)));
    }

    // Bare scalars that aren't a constant/parameter leaf: nothing is baked into the shaper, so nothing to rebind.
    [Fact]
    public void Bare_non_constant_leaves_do_not_rebind()
    {
        Assert.False(CanRebind(q => q.Select(x => x.N + 1)));
        Assert.False(CanRebind(q => q.Select(x => x.Id)));
    }

    // This harness skips EF's funcletizer, so a constant of a non-literal type is built as a ConstantExpression here.
    private static IQueryable SelectConstant<T>(IQueryable<Entity> q, T value)
    {
        var x = Expression.Parameter(typeof(Entity), "x");
        return q.Select(Expression.Lambda<Func<Entity, T>>(Expression.Constant(value, typeof(T)), x));
    }

    // The leaf renders (it is a server-side `$literal`), but the value read back would differ from the C# constant.
    [Fact]
    public void Bare_DateTime_constant_does_not_rebind()
    {
        Assert.IsType<MongoConstantExpression>(SingleLeaf(q => SelectConstant(q, new DateTime(2020, 1, 1))));
        Assert.False(CanRebind(q => SelectConstant(q, new DateTime(2020, 1, 1))));
    }

    [Fact]
    public void Bare_decimal_constant_does_not_rebind()
    {
        Assert.IsType<MongoConstantExpression>(SingleLeaf(q => SelectConstant(q, 1.5m)));
        Assert.False(CanRebind(q => SelectConstant(q, 1.5m)));
    }

    [Fact]
    public void Nullable_int_constant_does_not_rebind()
        => Assert.False(CanRebind(q => q.Select(x => (int?)8)));

    [Fact]
    public void Constant_inside_a_construction_does_not_rebind()
    {
        Assert.False(CanRebind(q => q.Select(x => new { A = 8 })));
        Assert.False(CanRebind(q => q.Select(x => new { A = 8, x.N })));
    }

    // A construction whose own type is round-trip safe (string) and whose first argument is a constant: only the
    // bare-scalar conjunct rejects it.
    [Fact]
    public void String_constructor_with_a_constant_first_argument_does_not_rebind()
    {
        var projection = Translate(q => q.Select(x => new string('a', x.N))).Mongo.Select.Projection;
        Assert.Equal(2, projection.Count);
        Assert.IsType<MongoConstantExpression>(projection[0].Expression);

        Assert.False(CanRebind(q => q.Select(x => new string('a', x.N))));
    }
}
