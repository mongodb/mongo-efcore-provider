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

#if !EF8

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A constant (application-supplied) SetProperty value must always be stored as data. When another setter in the same
/// ExecuteUpdate is self-referencing the update becomes an aggregation pipeline, where an unescaped string starting with
/// '$' is evaluated as a field path / variable rather than stored literally.
/// </summary>
public class ExecuteUpdateExpressionInjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private const string SecretEmail = "victim@example.com";

    private class Account
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; } = null!;
        public string Email { get; set; } = null!;
        public int Count { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    private (IMongoCollection<BsonDocument> Raw, SingleEntityDbContext<Account> Db) CreateSeeded(
        string caseName = "", [CallerMemberName] string? collectionName = null)
    {
        var collection = database.CreateCollection<Account>(collectionName, caseName);

        using (var seedDb = SingleEntityDbContext.Create(collection))
        {
            seedDb.Add(new Account { _id = ObjectId.GenerateNewId(), Name = "original", Email = SecretEmail, Count = 0 });
            seedDb.SaveChanges();
        }

        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        return (raw, SingleEntityDbContext.Create(collection));
    }

    [Theory]
    [InlineData("$Email", "field")]
    [InlineData("$$ROOT", "root")]
    [InlineData("$doesNotExist", "missing")]
    [InlineData("$Count", "int")]
    public void Constant_only_update_stores_dollar_string_literally(string userText, string caseName)
    {
        var (raw, db) = CreateSeeded(caseName);
        using var _ = db;

        db.Entities.ExecuteUpdate(s => s.SetProperty(e => e.Name, userText));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal(new BsonString(userText), doc.GetValue("Name", BsonNull.Value));
    }

    [Theory]
    [InlineData("$Email", "field")]
    [InlineData("$$ROOT", "root")]
    [InlineData("$doesNotExist", "missing")]
    [InlineData("$Count", "int")]
    public void Constant_setter_alongside_self_referencing_setter_stores_dollar_string_literally(string userText, string caseName)
    {
        var (raw, db) = CreateSeeded(caseName);
        using var _ = db;

        db.Entities.ExecuteUpdate(s => s
            .SetProperty(e => e.Name, userText)
            .SetProperty(e => e.Count, e => e.Count + 1));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal(1, doc["Count"].AsInt32); // the self-referencing setter ran
        Assert.Equal(new BsonString(userText), doc.GetValue("Name", BsonNull.Value));
    }

    [Fact]
    public void Constant_collection_setter_alongside_self_referencing_setter_stores_dollar_strings_literally()
    {
        var (raw, db) = CreateSeeded();
        using var _ = db;

        var userTags = new List<string> { "$Email", "safe" };
        db.Entities.ExecuteUpdate(s => s
            .SetProperty(e => e.Tags, userTags)
            .SetProperty(e => e.Count, e => e.Count + 1));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal(new BsonArray { "$Email", "safe" }, doc["Tags"]);
    }

    [Fact]
    public void Constant_setter_alongside_self_referencing_setter_does_not_copy_other_field_into_entity()
    {
        var (_, db) = CreateSeeded();
        using var __ = db;

        db.Entities.ExecuteUpdate(s => s
            .SetProperty(e => e.Name, "$Email")
            .SetProperty(e => e.Count, e => e.Count + 1));

        var reloaded = db.Entities.AsNoTracking().Single();
        Assert.NotEqual(SecretEmail, reloaded.Name);
        Assert.Equal("$Email", reloaded.Name);
    }

    [Fact]
    public void Captured_value_inside_self_referencing_setter_is_stored_literally()
    {
        var (raw, db) = CreateSeeded();
        using var _ = db;

        var userText = "$Email";
        db.Entities.ExecuteUpdate(s => s.SetProperty(e => e.Name, e => e.Name + userText));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal("original$Email", doc["Name"].AsString);
    }

    [Fact]
    public void Captured_value_in_conditional_self_referencing_setter_is_stored_literally()
    {
        var (raw, db) = CreateSeeded();
        using var _ = db;

        var userText = "$Email";
        db.Entities.ExecuteUpdate(s => s.SetProperty(e => e.Name, e => e.Count >= 0 ? userText : e.Name));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal("$Email", doc["Name"].AsString);
    }

    [Fact]
    public void Captured_collection_in_conditional_self_referencing_setter_is_stored_literally()
    {
        var (raw, db) = CreateSeeded();
        using var _ = db;

        var userTags = new List<string> { "$Email", "safe" };
        db.Entities.ExecuteUpdate(s => s.SetProperty(e => e.Tags, e => e.Count >= 0 ? userTags : e.Tags));

        var doc = raw.Find(FilterDefinition<BsonDocument>.Empty).Single();
        Assert.Equal(new BsonArray { "$Email", "safe" }, doc["Tags"]);
    }

    [Fact]
    public void Captured_value_in_expression_filter_is_compared_literally()
    {
        var (_, db) = CreateSeeded();
        using var __ = db;

        var userText = "$Email";
        Assert.Equal(0, db.Entities.Count(e => e.Name + userText == "original" + SecretEmail));
        Assert.Equal(1, db.Entities.Count(e => e.Name + userText == "original$Email"));
    }

    [Fact]
    public void Captured_value_in_server_side_projection_is_returned_literally()
    {
        var (_, db) = CreateSeeded();
        using var __ = db;

        var userText = "$Email";
        Assert.Equal("original$Email", db.Entities.Select(e => e.Name + userText).Single());
    }
}

#endif
