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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class EncryptionBuilderExtensionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Patient
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
        public CarePlan carePlan { get; set; }
    }

    class CarePlan
    {
        public string instructions { get; set; }
    }

    [Fact]
    public void IsEncrypted_on_OwnershipBuilder_sets_encryption_metadata()
    {
        var dataKeyId = Guid.NewGuid();
        var collection = database.CreateCollection<Patient>();

        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Patient>()
                .OwnsOne(p => p.carePlan)
                .IsEncrypted(dataKeyId);
        });

        var model = db.Model;
        var entityType = model.FindEntityType(typeof(Patient));
        Assert.NotNull(entityType);

        var foreignKey = entityType.FindNavigation(nameof(Patient.carePlan))?.ForeignKey;
        Assert.NotNull(foreignKey);
        Assert.Equal(dataKeyId, foreignKey.GetEncryptionDataKeyId());
    }
}
