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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions;

public sealed class CamelCasePropertyNameConventionTests : IDisposable
{
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();
    public void Dispose() => _tempDatabase.Dispose();

    class CamelCaseDbContext : DbContext
    {
        private readonly string _collectionName;

        public DbSet<RemappedEntity> Remapped { get; init; }

        public static CamelCaseDbContext Create(IMongoCollection<RemappedEntity> collection) =>
            new(new DbContextOptionsBuilder<CamelCaseDbContext>()
                .UseMongoDB(collection.Database.Client, collection.Database.DatabaseNamespace.DatabaseName)
                .Options, collection.CollectionNamespace.CollectionName);

        public CamelCaseDbContext(DbContextOptions options, string collectionName)
            : base(options)
        {
            _collectionName = collectionName;
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Add(serviceProvider => new CamelCasePropertyNameConvention(serviceProvider.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<RemappedEntity>().ToCollection(_collectionName);
        }
    }

    class IntendedStorageEntity
    {
        public ObjectId _id { get; set; }

        public string unchanged { get; set; }
        public string alsoUnchanged { get; set; }
        public string lowercaseFirstWord { get; set; }
        public string removeUnderscores { get; set; }
        public string treatUpperCase { get; set; }
        public string numeric123Separator { get; set; }
    }

    class RemappedEntity
    {
        [Column("_id")]
        public ObjectId _id { get; set; }

        public string unchanged { get; set; }
        public string alsoUnchanged { get; set; }
        public string LowercaseFirstWord { get; set; }
        public string remove_underscores { get; set; }
        public string treatUPPERCase { get; set; }
        public string numeric123separator { get; set; }
    }

    [Fact]
    public void CamelCase_redefines_element_name_for_insert_and_query()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<RemappedEntity>();

        var id = ObjectId.GenerateNewId();
        const string unchangedText = "Unchanged as is a single already-lowercase word";
        const string alsoUnchangedText = "Unchanged as is already fully camel cased";
        const string changedLowerText = "Changed as first word needs to be lower cased";
        const string underscoredText = "Changed as underscores need removing and second word capitalizing";
        const string treatUpperText = "Treated UPPER as a separate word and title cased it";
        const string numericText = "Treated 123 as part of numeric and title cased word after";

        {
            var dbContext = CamelCaseDbContext.Create(collection);
            dbContext.Remapped.Add(new RemappedEntity
            {
                _id = id,
                unchanged = unchangedText,
                alsoUnchanged = alsoUnchangedText,
                LowercaseFirstWord = changedLowerText,
                remove_underscores = underscoredText,
                treatUPPERCase = treatUpperText,
                numeric123separator = numericText
            });
            dbContext.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedStorageEntity>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(unchangedText, directFound.unchanged);
            Assert.Equal(alsoUnchangedText, directFound.alsoUnchanged);
            Assert.Equal(changedLowerText, directFound.lowercaseFirstWord);
            Assert.Equal(underscoredText, directFound.removeUnderscores);
            Assert.Equal(treatUpperText, directFound.treatUpperCase);
            Assert.Equal(numericText, directFound.numeric123Separator);
        }
    }
}
