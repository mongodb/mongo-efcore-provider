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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An enum-to-enum cast projection leaf (<c>(TargetEnum)c.SourceEnum</c>) has no <c>$toX</c> form but needs
/// none: it changes only the declared CLR type, so <c>NativeProjectionBinder.TryTranslateLeaf</c> binds it as a
/// bare field leaf and the DOM read materializes the target enum.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeEnumCastProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum SourceStatus
    {
        Active = 0,
        Inactive = 1
    }

    public enum TargetStatusDto
    {
        Active = 0,
        Inactive = 1
    }

    public class Row
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public SourceStatus Status { get; set; }
    }

    private static readonly (string Label, SourceStatus Status)[] Rows =
    [
        ("a", SourceStatus.Active),
        ("b", SourceStatus.Inactive)
    ];

    [Fact]
    public void Wrapped_enum_to_enum_cast_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Wrapped_enum_to_enum_cast_projection_leaf_goes_native));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, Status = (TargetStatusDto)x.Status }).ToList();
        Assert.Equal(
            [("a", TargetStatusDto.Active), ("b", TargetStatusDto.Inactive)],
            nativeOnlyResult.Select(r => (r.Label, r.Status)));

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, Status = (TargetStatusDto)x.Status }).ToList();
        Assert.Equal(
            nativeOnlyResult.Select(r => (r.Label, r.Status)), nativeResult.Select(r => (r.Label, r.Status)));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, Status = (TargetStatusDto)x.Status }).ToList();
        Assert.Equal(
            nativeResult.Select(r => (r.Label, r.Status)), driverLinqResult.Select(r => (r.Label, r.Status)));
    }

    [Fact]
    public void Bare_enum_to_enum_cast_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Bare_enum_to_enum_cast_projection_leaf_goes_native));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => (TargetStatusDto)x.Status).ToList();
        Assert.Equal([TargetStatusDto.Active, TargetStatusDto.Inactive], nativeOnlyResult);
    }

    private MongoDB.Driver.IMongoCollection<Row> Seed(string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(Rows.Select(r => new Row { Label = r.Label, Status = r.Status }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(MongoDB.Driver.IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
