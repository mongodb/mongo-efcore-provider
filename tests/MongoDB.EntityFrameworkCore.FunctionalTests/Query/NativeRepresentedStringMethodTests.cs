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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// F5: a regex runs on the STORED value, so a string stored as a BSON ObjectId (<c>[BsonRepresentation(ObjectId)]</c>)
/// cannot be searched with <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>/<c>Like</c>/<c>Regex.IsMatch</c>/case-folded
/// equality. The native translator used to emit a regex that silently matched nothing (<c>[]</c>); it now declines, so
/// NativeOnly throws and Native falls back to the driver (which raises the server's loud error, as on main).
/// A plain string property of the same shapes still translates natively.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeRepresentedStringMethodTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Doc
    {
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = "";

        public string Code { get; set; } = "";
    }

    private static readonly ObjectId Oid1 = ObjectId.Parse("507f1f77bcf86cd799439011");
    private static readonly ObjectId Oid2 = ObjectId.Parse("607f1f77bcf86cd799439022");

    private IMongoCollection<Doc> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", Oid1 }, { "Code", "Alpha" } },
            new BsonDocument { { "_id", Oid2 }, { "Code", "beta" } }
        ]);
        return database.MongoDatabase.GetCollection<Doc>(name);
    }

    private static SingleEntityDbContext<Doc> Create(IMongoCollection<Doc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static List<string> Run(SingleEntityDbContext<Doc> db, Expression<Func<Doc, bool>> predicate)
        => db.Entities.AsNoTracking().Where(predicate).Select(d => d.Code).ToList();

    private void AssertDeclined(Expression<Func<Doc, bool>> predicate, Type? expectedDriverException = null)
    {
        var collection = Seed();

        using (var nativeOnly = Create(collection, MongoQueryMode.NativeOnly))
            Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly, predicate));

        using var driver = Create(collection, MongoQueryMode.DriverLinq);
        var driverEx = Assert.ThrowsAny<Exception>(() => Run(driver, predicate));

        using var native = Create(collection, MongoQueryMode.Native);
        var nativeEx = Assert.ThrowsAny<Exception>(() => Run(native, predicate));

        Assert.IsType(expectedDriverException ?? typeof(MongoCommandException), driverEx);
        Assert.Equal(driverEx.GetType(), nativeEx.GetType());
    }

    private void AssertNativeAndExpected(Expression<Func<Doc, bool>> predicate, string[] expected, bool driverSupported = true)
    {
        var collection = Seed();
        foreach (var mode in driverSupported
                     ? new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq }
                     : new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(collection, mode);
            Assert.Equal(expected, Run(db, predicate).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact] public void StartsWith_declines() => AssertDeclined(d => d.Id.StartsWith("507f"));
    [Fact] public void EndsWith_declines() => AssertDeclined(d => d.Id.EndsWith("011"));
    [Fact] public void Contains_declines() => AssertDeclined(d => d.Id.Contains("f1f7"));
    [Fact] public void ToLower_equality_declines() => AssertDeclined(d => d.Id.ToLower() == "507f1f77bcf86cd799439011");

    // The driver has no translation for EF.Functions.Like, Regex.IsMatch or a field-to-field StartsWith (the
    // ExpressionNotSupportedException cases below): Native falls back to it and gets the same
    // ExpressionNotSupportedException as DriverLinq (never a silent []).
    [Fact]
    public void Like_declines()
        => AssertDeclined(d => EF.Functions.Like(d.Id, "507f%"), typeof(ExpressionNotSupportedException));

    [Fact] public void Regex_IsMatch_declines() => AssertDeclined(d => Regex.IsMatch(d.Id, "^507f"), typeof(ExpressionNotSupportedException));

    [Fact]
    public void Equals_ignore_case_declines()
        => AssertDeclined(d => string.Equals(d.Id, "507F1F77BCF86CD799439011", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Reversed_Regex_IsMatch_declines()
        => AssertDeclined(d => Regex.IsMatch("507f1f77bcf86cd799439011", d.Id), typeof(ExpressionNotSupportedException));

    [Fact]
    public void Regex_IsMatch_with_represented_pattern_field_declines()
        => AssertDeclined(d => Regex.IsMatch(d.Code, d.Id), typeof(ExpressionNotSupportedException));

    [Fact]
    public void Field_to_field_StartsWith_declines()
        => AssertDeclined(d => d.Code.StartsWith(d.Id), typeof(ExpressionNotSupportedException));

    // Controls: the same shapes over a plain string property still translate natively and return correct rows.
    [Fact] public void Control_StartsWith() => AssertNativeAndExpected(d => d.Code.StartsWith("Al"), ["Alpha"]);
    [Fact] public void Control_EndsWith() => AssertNativeAndExpected(d => d.Code.EndsWith("eta"), ["beta"]);
    [Fact] public void Control_Contains() => AssertNativeAndExpected(d => d.Code.Contains("lph"), ["Alpha"]);
    [Fact] public void Control_ToLower_equality() => AssertNativeAndExpected(d => d.Code.ToLower() == "alpha", ["Alpha"]);
    [Fact] public void Control_Like() => AssertNativeAndExpected(d => EF.Functions.Like(d.Code, "Al%"), ["Alpha"], driverSupported: false);
    [Fact] public void Control_Regex_IsMatch() => AssertNativeAndExpected(d => Regex.IsMatch(d.Code, "^Al"), ["Alpha"]);

    [Fact]
    public void Control_Equals_ignore_case()
        => AssertNativeAndExpected(d => string.Equals(d.Code, "ALPHA", StringComparison.OrdinalIgnoreCase), ["Alpha"]);
}
