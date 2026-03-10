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

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class OwnedEntityElementNameTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Order
    {
        public ObjectId _id { get; set; }
        public string description { get; set; }
        public Address shippingAddress { get; set; }
    }

    class Address
    {
        public string street { get; set; }
        public string city { get; set; }
    }

    [Fact]
    public void HasElementName_on_owned_navigation_changes_stored_element_name()
    {
        var collection = database.CreateCollection<Order>();

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Order>().OwnsOne(o => o.shippingAddress, r => r.HasElementName("addr"));
        }))
        {
            db.Entities.Add(new Order
            {
                description = "test",
                shippingAddress = new Address { street = "123 Main", city = "Springfield" }
            });
            db.SaveChanges();
        }

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace)
            .Find(FilterDefinition<BsonDocument>.Empty).First();

        Assert.True(raw.Contains("addr"), "Expected element name 'addr' in stored document");
        Assert.False(raw.Contains("shippingAddress"), "Should not contain original property name");
        Assert.Equal("123 Main", raw["addr"]["street"].AsString);
    }

    [Fact]
    public void Owned_entity_with_custom_element_name_round_trips()
    {
        var collection = database.CreateCollection<Order>();

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Order>().OwnsOne(o => o.shippingAddress, r => r.HasElementName("addr"));
        }))
        {
            db.Entities.Add(new Order
            {
                description = "round-trip",
                shippingAddress = new Address { street = "789 Elm", city = "Capital City" }
            });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Order>().OwnsOne(o => o.shippingAddress, r => r.HasElementName("addr"));
        }))
        {
            var order = db.Entities.First();
            Assert.Equal("round-trip", order.description);
            Assert.Equal("789 Elm", order.shippingAddress.street);
            Assert.Equal("Capital City", order.shippingAddress.city);
        }
    }
}
