# MongoDB EF Core Provider Breaking Changes

Please note that this provider **does not follow traditional semantic versioning** of limiting breaking changes to major version numbers. This is because the major version number is used to align with releases of Entity Framework Core.

In order to evolve the provider as we introduce new features, we will be using the minor version number for breaking and significant changes to our EF Core provider. Please bear this in mind when upgrading to newer versions of the MongoDB EF Core Provider and ensure you read the release notes and this document for the latest in breaking change information.

## Breaking changes in 8.4.0 / 9.1.0 / 10.0.0

### The element name for discriminators may have changed

#### Old behavior

Discriminator properties not explicitly configured with an element name may not be mapped to `_t`. For example `modelBuilder.Entity<Foo>().HasDiscriminator<string>("_t")` when used withe the camel case naming conventions would result in an a discriminator field called `T` in BSON documents. This means that you may get exceptions because EF Core assumes the discriminator is mapped to `_t`.

#### New behavior

Discriminator properties are always mapped to `_t` unless an explicit element name is provided.

#### Why

By convention, discriminators in MongoDB document should be called `_t` to ensure interoperability between different tools, etc. This is similar to how the document ID is always in `_id`.

#### Mitigations

Consider updating discriminators in all documents to `_t` to conform with convention. If this is not possible, then the element name for a discriminator property can be explicitly configured. For example:

```c#
modelBuilder.Entity<Foo>().Property<string>("_t").HasElementName("T");
```

Note that, by default, discriminator properties in shadow state are called "Discriminator", so you may need to do this:

```c#
modelBuilder.Entity<Foo>().Property<string>("Discriminator").HasElementName("discriminator");
```

## Breaking changes in 8.3.0 / 9.0.0

Nullable properties configured with an alternative BSON representation either by the `[BsonRepresentation]` attribute or `HasBsonRepresentation()` fluent API were not being applied in previous versions. This has been fixed but you will remedy the discrepancy one of two ways:

### Continue to use the default representation

If it is not critical you use the alternative BSON representation you can do this by simply removing the configuration from your application before it starts.

or

### Update affected elements to new representation

As part of your upgrade process you can use the [updateMany](https://www.mongodb.com/docs/manual/reference/method/db.collection.updateMany/) method per affected MongoDB collection to rewrite any affected nullable properties/elements into the desired BSON representation using the `$convert`  operation.

An example of converting a `dateOfBirth` element in a collection named `people` from a BSON `date` into a BSON `int` representation would look like this:

```js
db.people.updateMany(
   { dateOfBirth: { $type: "date" } },
   [
      {
         $set: {
            dateOfBirth: {
               $convert: {
                  input: "$dateOfBirth",
                  to: "int",
                  onNull: null
               }
            }
         }
      }
   ]
)
```

## Breaking changes in 8.2.0

No explicit breaking changes are intended in this EF Core Provider release but the underlying [MongoDB.Driver has many breaking changes in the 3.0 release](https://www.mongodb.com/docs/drivers/csharp/v3.0/upgrade/v3/#version-3.0-breaking-changes). If you are using the MongoDB C# Driver explicitly you will likely be affected there and even if not you should ensure compatibilty with your application and data.

## Breaking changes in 8.1.0

This release sees a number of breaking changes deemed necessary to implement the new features and provide for a robust provider experience. They are:

- MongoDB transactions are now required by default
- Guid binary format is changing
- CreateDatabase recommended at start-up
- IMongoClientWrapper interface changes
- Convention tweaks

Please see the following sections for more details.

### MongoDB transactions are now required by default

The MongoDB EF Core Provider 8.1.0 introduces optimistic concurrency support and automatic transactions inside `SaveChanges` and `SaveChangesAsync` to ensure all changes commit together or rollback together as part of the "unit of work" philosophy of Entity Framework Core.

To ensure data integrity transactions are enabled by default in 8.1.0 which means a MongoDB server configuration that supports transactions is required.

If you are already running MongoDB 5.0 or above in load balanced, sharded, or replica set configurations you should be unaffected by this change.

If, however, you are running MongoDB Server:

- With a version prior to 5.0 you will need to upgrade, this provider is not supported with MongoDB versions prior to 5.0
- In standalone mode (perhaps for local development) you can reconfigure your standalone server to a single-instance replica set
- In a container environment switch to a single-instance replica set container if one is available

To reconfigure a standalone server please follow the [Convert a Standalone MongoDB to a Replica Set guide](https://www.mongodb.com/docs/manual/tutorial/convert-standalone-to-replica-set/).

Alternatively, if you are absolutely sure you do not wish to use transactions, (and therefore not use optimistic concurrency) then you can disable automatic transactions inside `SaveChanges` and `SaveChangesAsync` by setting `Database.AutoTransactionBehavior = AutoTransactionBehavior.Never` on your `DbContext` subclass as part of the setup.

### Guid binary format is changing

The default version for Guid storage in the MongoDB .NET/C# Driver and in prior versions of EF Core Provider is the `CSharpLegacy` binary format which has a number of issues when being read by different drivers. To alleviate this problem we are switching to the `Standard` format which does not suffer from these problems.

If your database exists specifically for your EF Core provider application and has data already in use we recommend you write a script to convert the Guids from the `CSharpLegacy` to `Standard` format. If your database is already using Guids and is shared with other non-EF Core Provider applications we recommend you switch them all to the `Standard` Guid format and not rely on any other kind of binary serialization format to avoid such incompatibilities.

Please also note that because the EF Core Provider relies on the MongoDB .NET/C# Driver to perform the low-level operations that the `BsonDefaults.GuidRepresentationMode` will be set to `GuidRepresentationMode.V3` when using the new 8.1.0 or later versions of this provider. Using the MongoDB C# Driver in your application at the same time must also use this mode.

### CreateDatabase recommended at start-up

Previous versions of this provider did not actually create either the database or collections inside `CreateDatabase` or `CreateDatabaseAsync` and instead left them to be implicitly created as documents were written.

With the move to transactional `SaveChanges` this is no longer recommended as it may cause snapshot issues in some server configurations.

Instead it is recommended you call `EnsureCreated` or `EnsureCreatedAsync` during your application start-up. This will call `CreateDatabase` (even if it already exists) which will create any missing expected collections based on the configured metadata for your `DbContext` subclass. This will not affect any existing collections or data and is recommended to avoid a `SaveChanges` or `SaveChangesAsync` operation from causing schema changes during a transaction which can result in snapshot exceptions on some configurations.

### IMongoClientWrapper interface changes

Transactions and database creation work has meant that the `IMongoClientWrapper` interface has changed. It is not recommended you implement this interface yourself as it exists solely to provide EF service registration for the concrete implementation `MongoClientWrapper` class.

### Convention tweaks

- The previously undocumented `CamelCaseElementNameConvention` was incorrectly using the class name and not the property name
- Some other conventions were not sealed and had virtual methods that made no sense, these have been corrected
- Some conventions took unnecessary constructor arguments for unneeded dependencies, these have been removed


