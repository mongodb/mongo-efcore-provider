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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>string.IsNullOrEmpty</c>/<c>IsNullOrWhiteSpace</c> (and their negations) against null, a missing field,
/// <c>""</c>, whitespace (including a non-breaking space, which .NET's <see cref="char.IsWhiteSpace(char)"/>
/// treats as whitespace), and a non-empty string. A missing field and a null field both read as <see
/// langword="null"/> in .NET, but <c>{ $eq: [ "$Text", null ] }</c> is <see langword="false"/> for a missing
/// field, so the native rewrite must still agree with the in-memory LINQ oracle for both.
/// </summary>
/// <remarks>
/// <c>!string.IsNullOrEmpty</c> negates exactly (<see cref="MongoExpressionNegator"/> De Morgan's the
/// <c>OrElse</c> rewrite into <c>AndAlso($ne, $ne)</c>, both query-native). <c>!string.IsNullOrWhiteSpace</c>
/// can't: its <c>Trim() == ""</c> disjunct isn't a query-native comparison, so there's no exact query-dialect
/// complement, and <c>MongoExpressionTranslator</c> declines natively rather than fall back to the generic
/// <c>Not</c> wrap — which renders via <c>$expr</c>'s <c>$not</c>/<c>$or</c>, whose <c>$eq</c> doesn't treat a
/// missing field as <see langword="null"/> the way the query dialect's <c>{ field: null }</c> match does, and
/// would silently misclassify a missing field.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeStringIsNullOrTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? Text { get; set; }
    }

    // (label, text) for every seeded row; a missing field reads as null in .NET, same as an explicit null.
    private static readonly (string Label, string? Text)[] Rows =
    [
        ("null", null),
        ("missing", null),
        ("empty", ""),
        ("whitespace", "   \t "),
        ("x", "x")
    ];

    [Fact]
    public void IsNullOrEmpty_matches_in_memory_oracle()
    {
        using var context = CreateSeededContext(nameof(IsNullOrEmpty_matches_in_memory_oracle));

        var actual = context.Entities.AsNoTracking().Where(x => string.IsNullOrEmpty(x.Text))
            .Select(x => x.Label).OrderBy(l => l).ToList();
        var expected = Rows.Where(r => string.IsNullOrEmpty(r.Text)).Select(r => r.Label).OrderBy(l => l).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsNullOrEmpty_negated_matches_in_memory_oracle()
    {
        using var context = CreateSeededContext(nameof(IsNullOrEmpty_negated_matches_in_memory_oracle));

        var actual = context.Entities.AsNoTracking().Where(x => !string.IsNullOrEmpty(x.Text))
            .Select(x => x.Label).OrderBy(l => l).ToList();
        var expected = Rows.Where(r => !string.IsNullOrEmpty(r.Text)).Select(r => r.Label).OrderBy(l => l).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsNullOrWhiteSpace_matches_in_memory_oracle()
    {
        using var context = CreateSeededContext(nameof(IsNullOrWhiteSpace_matches_in_memory_oracle));

        var actual = context.Entities.AsNoTracking().Where(x => string.IsNullOrWhiteSpace(x.Text))
            .Select(x => x.Label).OrderBy(l => l).ToList();
        var expected = Rows.Where(r => string.IsNullOrWhiteSpace(r.Text)).Select(r => r.Label).OrderBy(l => l).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsNullOrWhiteSpace_negated_declines_natively_rather_than_render_wrongly_for_a_missing_field()
    {
        using var context = CreateSeededContext(
            nameof(IsNullOrWhiteSpace_negated_declines_natively_rather_than_render_wrongly_for_a_missing_field));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            context.Entities.AsNoTracking().Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => x.Label).OrderBy(l => l).ToList());
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(collectionName);

        // "missing" is seeded separately below via a raw BsonDocument, bypassing the POCO mapping so its field
        // is absent entirely, not merely null.
        collection.InsertMany(
            Rows.Where(r => r.Label != "missing").Select(r => new Row { Label = r.Label, Text = r.Text }).ToList());

        var rawCollection = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        rawCollection.InsertOne(new BsonDocument { { "Label", "missing" } });

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
    }
}
