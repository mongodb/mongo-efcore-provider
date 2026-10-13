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

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

// EF-459: the server evaluates date parts and calendar adds on the stored UTC instant, but a Local-kind property
// materializes as local time, so these queries fail loudly rather than return wrong values.
[XUnitCollection("QueryTests")]
public class LocalKindDateTimeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static readonly DateTime Stored = new(2024, 3, 15, 11, 0, 0, DateTimeKind.Utc);

    private SingleEntityDbContext<Entity> CreateContext([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Entity>(name);
        collection.InsertOne(new Entity { Id = ObjectId.GenerateNewId(), Local = Stored, NullableLocal = Stored, Utc = Stored, Plain = Stored,
            LocalNest = new Nest { When = Stored }, UtcNest = new Nest { When = Stored } });
        return SingleEntityDbContext.Create(collection, model =>
        {
            model.Entity<Entity>().Property(e => e.Local).HasDateTimeKind(DateTimeKind.Local);
            model.Entity<Entity>().Property(e => e.NullableLocal).HasDateTimeKind(DateTimeKind.Local);
            model.Entity<Entity>().Property(e => e.Utc).HasDateTimeKind(DateTimeKind.Utc);
            model.Entity<Entity>().OwnsOne(e => e.LocalNest, b => b.Property(n => n.When).HasDateTimeKind(DateTimeKind.Local));
            model.Entity<Entity>().OwnsOne(e => e.UtcNest, b => b.Property(n => n.When).HasDateTimeKind(DateTimeKind.Utc));
        });
    }

    private static void AssertThrowsLocal(Action query, string operation, string property = "Entity.Local")
    {
        var message = Assert.Throws<InvalidOperationException>(query).Message;
        Assert.Contains($"'{operation}' over the Local-kind DateTime property '{property}'", message);
    }

    private static void AssertThrowsUnresolved(Action query, string operation)
    {
        var message = Assert.Throws<InvalidOperationException>(query).Message;
        Assert.Contains($"'{operation}' over a DateTime member of a projected", message);
    }

    [Fact]
    public void Select_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.Hour).ToList(), "Hour");
    }

    [Fact]
    public void Where_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => e.Local.Hour == 11).ToList(), "Hour");
    }

    [Fact]
    public void Select_Date_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.Date).ToList(), "Date");
    }

    [Fact]
    public void Select_AddDays_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.AddDays(1)).ToList(), "AddDays");
    }

    [Fact]
    public void Where_AddMonths_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => e.Local.AddMonths(1) > Stored).ToList(), "AddMonths");
    }

    [Fact]
    public void Nullable_Value_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => e.NullableLocal != null).Select(e => e.NullableLocal!.Value.Hour).ToList(), "Hour", "Entity.NullableLocal");
    }

    [Fact]
    public void Date_then_AddDays_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.Date.AddDays(1)).ToList(), "AddDays");
    }

    [Fact]
    public void Hour_over_utc_kind_property_still_works()
    {
        using var db = CreateContext();
        Assert.Equal([11], db.Entities.Select(e => e.Utc.Hour).ToList());
    }

    [Fact]
    public void Hour_over_unconfigured_property_still_works()
    {
        using var db = CreateContext();
        Assert.Equal([11], db.Entities.Where(e => e.Plain.Hour == 11).Select(e => e.Plain.Hour).ToList());
    }

    [Fact]
    public void Select_AddHours_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.AddHours(1)).ToList(), "AddHours");
    }

    [Fact]
    public void Where_AddMinutes_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => e.Local.AddMinutes(1) > Stored).ToList(), "AddMinutes");
    }

    [Fact]
    public void Select_AddSeconds_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.AddSeconds(1)).ToList(), "AddSeconds");
    }

    [Fact]
    public void Select_AddMilliseconds_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.AddMilliseconds(1)).ToList(), "AddMilliseconds");
    }

    [Fact]
    public void AddHours_over_utc_kind_property_and_local_comparison_still_work()
    {
        using var db = CreateContext();
        Assert.Single(db.Entities.Where(e => e.Utc.AddHours(1) == Stored.AddHours(1)).ToList());
        Assert.Empty(db.Entities.Where(e => e.Utc.AddHours(1) == Stored.AddHours(2)).ToList());
        Assert.Single(db.Entities.Where(e => e.Local == Stored.ToLocalTime()).ToList());
    }

    [Fact]
    public void Date_part_after_AsEnumerable_works()
    {
        using var db = CreateContext();
        Assert.Equal([Stored.ToLocalTime().Hour], db.Entities.AsEnumerable().Select(e => e.Local.Hour).ToList());
    }

    [Fact]
    public void Select_AddYears_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => e.Local.AddYears(1)).ToList(), "AddYears");
    }

    [Fact]
    public void EF_Property_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => EF.Property<DateTime>(e, "Local").Hour == 11).ToList(), "Hour");
    }

    [Fact]
    public void Owned_local_kind_property_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Where(e => e.LocalNest!.When.Hour == 11).ToList(), "Hour", "Entity.LocalNest#Nest.When");
    }

    [Fact]
    public void Owned_type_shared_by_owners_resolves_the_navigation_reached()
    {
        using var db = CreateContext();
        Assert.Equal([11], db.Entities.Select(e => e.UtcNest!.When.Hour).ToList());
    }

    [Fact]
    public void Anonymous_projection_then_member_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsLocal(() => db.Entities.Select(e => new { e.Local }).Select(x => x.Local.Hour).ToList(), "Hour");
    }

    [Fact]
    public void Tuple_projection_then_member_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsUnresolved(
            () => db.Entities.Select(e => new Tuple<DateTime, int>(e.Local, 1)).Where(x => x.Item1.Hour == 11).ToList(), "Hour");
    }

    [Fact]
    public void ValueTuple_projection_then_member_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsUnresolved(
            () => db.Entities.Select(e => ValueTuple.Create(e.Local, 1)).Select(x => x.Item1.Hour).ToList(), "Hour");
    }

    [Fact]
    public void Record_projection_then_member_Hour_throws()
    {
        using var db = CreateContext();
        AssertThrowsUnresolved(() => db.Entities.Select(e => new DateRecord(e.Local)).Where(x => x.D.Hour == 11).ToList(), "Hour");
    }

    [Fact]
    public void Record_in_namespace_starting_with_System_still_declined()
    {
        // Framework types are identified by assembly, so a user type in "Systems.Domain" is not mistaken for a BCL type.
        using var db = CreateContext();
        AssertThrowsUnresolved(
            () => db.Entities.Select(e => new Systems.Domain.SystemsRecord(e.Local)).Where(x => x.D.Hour == 11).ToList(), "Hour");
    }

    [Fact]
    public void Hour_over_utc_kind_property_through_a_tuple_is_declined_conservatively()
    {
        // The tuple member's source cannot be resolved, so even a Utc-kind source is declined rather than guessed at.
        using var db = CreateContext();
        AssertThrowsUnresolved(() => db.Entities.Select(e => ValueTuple.Create(e.Utc, 1)).Select(x => x.Item1.Hour).ToList(), "Hour");
    }

    [Fact]
    public void Member_of_client_side_aggregate_result_still_works()
    {
        using var db = CreateContext();
        Assert.Equal(Stored.ToLocalTime().Hour, db.Entities.Max(e => e.Local).Hour);
    }

#if !EF8
    // EF10 hands the lambda to the provider, so the Local-kind message surfaces; EF9's own translation of the lambda fails first
    // with a generic "could not be translated" error. Either way the query fails loudly rather than updating on UTC values.
    private static void AssertBulkThrows(Action query, string operation)
    {
#if EF10
        AssertThrowsLocal(query, operation);
#else
        Assert.Throws<InvalidOperationException>(query);
#endif
    }

    [Fact]
    public void ExecuteUpdate_filter_over_local_kind_Hour_throws()
    {
        using var db = CreateContext();
        AssertBulkThrows(
            () => db.Entities.Where(e => e.Local.Hour == 11).ExecuteUpdate(s => s.SetProperty(e => e.Plain, Stored)), "Hour");
    }

    [Fact]
    public void ExecuteUpdate_setter_over_local_kind_AddDays_throws()
    {
        using var db = CreateContext();
        AssertBulkThrows(() => db.Entities.ExecuteUpdate(s => s.SetProperty(e => e.Plain, e => e.Local.AddDays(1))), "AddDays");
    }
#endif

    private record DateRecord(DateTime D);

    private class Nest
    {
        public DateTime When { get; set; }
    }

    private class Entity
    {
        public ObjectId Id { get; set; }
        public DateTime Local { get; set; }
        public DateTime? NullableLocal { get; set; }
        public DateTime Utc { get; set; }
        public DateTime Plain { get; set; }
        public Nest? LocalNest { get; set; }
        public Nest? UtcNest { get; set; }
    }
}
