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

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class IdPrimaryKeyConventionTests
{
    [Fact]
    public static void Id_fields_are_identified_as_primary_keys_when_strings()
    {
        using var context = new MyDbContext();

        var keys = context.Model.FindEntityType(typeof(Vendor))!.GetKeys().ToArray();

        var expectedProperty = Utilities.GetPropertyInfo((Vendor v) => v._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    [Fact]
    public static void Id_field_are_identified_as_primary_keys_when_objectids()
    {
        using var context = new MyDbContext();

        var keys = context.Model.FindEntityType(typeof(Customer))!.GetKeys().ToArray();

        var expectedProperty = Utilities.GetPropertyInfo((Customer c) => c._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    class Vendor
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    abstract class BaseDbContext : DbContext
    {
        public DbSet<Vendor> Vendors { get; set; }
        public DbSet<Customer> Customers { get; set; }
    }

    class MyDbContext : BaseDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }
}
