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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class NavigationProxyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Navigation_proxy_single_can_use_shadow_property()
    {
        var originalAuthor = new Author {Name = "Damien"};
        var originalPost = new Post {Title = "Navigation proxy", Author = originalAuthor};

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            db.Authors.Add(originalAuthor);
            db.Posts.Add(originalPost);
            db.SaveChanges();
        }

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            var foundPost = db.Posts.First(p => p.Id == originalPost.Id);
            Assert.Equal(originalAuthor.Name, foundPost.Author.Name);
        }
    }

    [Fact]
    public void Navigation_proxy_collection_loads()
    {
        var blog = new Blog { Name = "By Proxy" };
        var originalAuthor1 = new Author {Name = "Damien"};
        var originalPost1 = new Post {Title = "Navigation 1", Author = originalAuthor1, Blog = blog};
        var originalAuthor2 = new Author {Name = "Henry"};
        var originalPost2 = new Post {Title = "Navigation 2", Author = originalAuthor2, Blog = blog};

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            db.AddRange(originalAuthor1, originalAuthor2, originalPost1, originalPost2, blog);
            db.SaveChanges();
        }

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            var foundBlog = db.Blogs.First(b => b.Id == blog.Id);
            Assert.Equal(2, foundBlog.Posts.Count);
            Assert.Single(foundBlog.Posts, p => p.Author.Name == originalAuthor1.Name);
            Assert.Single(foundBlog.Posts, p => p.Author.Name == originalAuthor2.Name);
        }
    }

    public class BloggingContext(DbContextOptions options, Action<ModelBuilder>? mb = null)
        : DbContext(options)
    {
        public DbSet<Author> Authors { get; set; }
        public DbSet<Post> Posts { get; set; }
        public DbSet<Blog> Blogs { get; set; }

        protected override void ConfigureConventions(ModelConfigurationBuilder cb)
        {
            base.ConfigureConventions(cb);
            cb.Conventions.Add(_ => new CamelCaseElementNameConvention());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            mb?.Invoke(modelBuilder);
        }
    }

    public static DbContextOptionsBuilder<BloggingContext> For(IMongoDatabase mongoDatabase) =>
        new DbContextOptionsBuilder<BloggingContext>()
            .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    public class Post
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; }
        public virtual Author Author { get; set; }
        public virtual Blog Blog { get; set; }
    }

    public class Author
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
    }

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public virtual List<Post> Posts { get; set; } = new();
    }
}
