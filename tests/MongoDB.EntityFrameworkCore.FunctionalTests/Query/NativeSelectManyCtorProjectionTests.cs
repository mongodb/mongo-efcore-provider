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

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeSelectManyCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
    }

    // A ctor-only DTO over the owned-collection SelectMany's (outer, inner) pair — no member named
    // "blogTitle"/"postHeading" matches a constructor parameter by the compiler's naming rule, so
    // NewExpression.Members is null.
    private class BlogPostSummary
    {
        public string BlogTitle { get; }
        public string PostHeading { get; }

        public BlogPostSummary(string blogTitle, string postHeading)
        {
            BlogTitle = blogTitle;
            PostHeading = postHeading;
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void SelectMany_owned_collection_with_two_argument_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(SelectMany_owned_collection_with_two_argument_ctor_only_dto_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Posts", new BsonArray { new BsonDocument("Heading", "First"), new BsonDocument("Heading", "Second") } }
        });
        var collection = database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<Blog>().OwnsMany(b => b.Posts),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the ctor-only DTO result selector went native.
        //
        // NOTE: .OrderBy(...) is applied AFTER .AsEnumerable() rather than composed directly on the query.
        // A server-side sort composed directly on a native SelectMany().Select() is unconditionally
        // non-native in this codebase today (a separate, pre-existing gap unrelated to ctor-only DTOs — the
        // same one Task 2's GroupBy test hit). Composing OrderBy on the query here would make the test fail
        // for the wrong reason. The native-translation gate under test (SelectMany's ctor-only DTO result
        // selector) is still fully exercised: ToList() below is what triggers query compilation/execution,
        // and it runs BEFORE the in-memory OrderBy.
        var results = db.Entities
            .SelectMany(b => b.Posts, (b, p) => new BlogPostSummary(b.Title, p.Heading))
            .AsEnumerable()
            .OrderBy(r => r.PostHeading)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].BlogTitle);
        Assert.Equal("First", results[0].PostHeading);
        Assert.Equal("Alpha", results[1].BlogTitle);
        Assert.Equal("Second", results[1].PostHeading);
    }
}
