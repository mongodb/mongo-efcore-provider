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
/// An enum-to-enum CAST projection leaf (<c>(TargetEnum)c.SourceEnum</c> — mapping an entity enum onto an
/// unrelated DTO enum) has no <c>$toX</c> form (<see cref="MongoDB.EntityFrameworkCore.Query.Expressions.MongoConvertExpression.ToOperatorFor"/>
/// only maps <c>int</c>/<c>long</c>/<c>double</c>/<c>decimal</c>), so it used to decline the WHOLE projecting
/// <c>Select</c> outright — the gap behind
/// <c>BuiltInDataTypesMongoTest.Can_filter_projection_with_captured_enum_variable</c>/<c>_inline_enum_variable</c>.
/// It needs no server-side computation: the cast only changes the leaf's DECLARED CLR type, never the stored
/// value, so <c>NativeProjectionBinder.TryTranslateLeaf</c> now admits it as a bare field leaf (dropping the
/// cast entirely) and the existing per-property DOM read materializes the target enum correctly.
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
