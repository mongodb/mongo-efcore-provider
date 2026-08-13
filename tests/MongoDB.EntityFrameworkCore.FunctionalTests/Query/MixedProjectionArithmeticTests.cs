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

using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-353: a projection mixing a whole-entity reference with a computed-arithmetic leaf over two
/// distinct scalar properties forces the client-side "mixed projection" shaper. That shaper's
/// default expression walk decomposed the arithmetic binary expression into two independent member
/// visits that clobbered a single projection-mapping slot, so the second operand silently overwrote
/// the first (e.g. Age * Score materialised as Score * Score).
/// </summary>
[XUnitCollection("QueryTests")]
public class MixedProjectionArithmeticTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Entity
    {
        public ObjectId _id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
    }

    [Fact]
    public void Select_projection_entity_and_computed_arithmetic_leaf_uses_correct_operands()
    {
        using var db = SingleEntityDbContext.Create<Entity>(database);

        db.Set<Entity>().AddRange(
            new Entity { Age = 3, Score = 7 },
                new Entity { Age = 5, Score = 2 });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var results = db.Set<Entity>()
            .Select(e => new { e, Total = e.Age * e.Score })
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(r.e.Age * r.e.Score, r.Total));

        Assert.Contains(results, r => r.e.Age == 3 && r.e.Score == 7 && r.Total == 21);
        Assert.Contains(results, r => r.e.Age == 5 && r.e.Score == 2 && r.Total == 10);
    }
}
