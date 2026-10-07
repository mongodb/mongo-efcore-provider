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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.CanAlignBareScalarAliases</c>: only bare scalar operands are
/// re-aliased for a native Union/Concat. EF requires both operands of a set op to share a CLR type, and an
/// anonymous/DTO member aliases by its name, so a construction pair with different aliases can't be built through
/// LINQ; the predicate is checked directly on each operand's translation instead.
/// </summary>
public class NativeSetOperationAliasAlignmentTests
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

    private static bool CanAlign(
        Func<IQueryable<Entity>, IQueryable> operand1, Func<IQueryable<Entity>, IQueryable> operand2)
    {
        var (s1, m1) = Translate(operand1);
        var (s2, m2) = Translate(operand2);
        return MongoQueryableMethodTranslatingExpressionVisitor.CanAlignBareScalarAliases(s1, m1, s2, m2);
    }

    [Fact]
    public void Bare_column_and_bare_computed_scalar_align()
    {
        var (_, column) = Translate(q => q.Select(x => x.Id));
        var (_, computed) = Translate(q => q.Select(x => x.N + 1));
        Assert.Equal("_id", Assert.Single(column.Select.Projection).Alias);
        Assert.Equal("_v", Assert.Single(computed.Select.Projection).Alias);

        Assert.True(CanAlign(q => q.Select(x => x.Id), q => q.Select(x => x.N + 1)));
        Assert.True(CanAlign(q => q.Select(x => x.N + 1), q => q.Select(x => x.Id)));
    }

    [Fact]
    public void Bare_column_and_grouped_aggregate_align()
        => Assert.True(CanAlign(q => q.Select(x => x.Id), q => q.GroupBy(x => x.N).Select(g => g.Count())));

    // One projection, but a construction shaper: its member is read by name, not re-aliasable.
    [Fact]
    public void Single_member_anonymous_operand_does_not_align()
    {
        var (_, anonymous) = Translate(q => q.Select(x => new { x.Id }));
        Assert.Equal("Id", Assert.Single(anonymous.Select.Projection).Alias);

        Assert.False(CanAlign(q => q.Select(x => new { x.Id }), q => q.Select(x => x.N + 1)));
        Assert.False(CanAlign(q => q.Select(x => x.N + 1), q => q.Select(x => new { x.Id })));
    }

    [Fact]
    public void Multi_member_anonymous_operands_do_not_align()
        => Assert.False(CanAlign(q => q.Select(x => new { x.Id, x.N }), q => q.Select(x => new { A = x.N + 1, B = x.Id })));
}
