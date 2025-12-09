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

using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Encryption;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

public abstract class EncryptionTestsBase(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    protected readonly Dictionary<string, IReadOnlyDictionary<string, object>> KmsProviders =
        CreateKmsProvidersWithLocalMasterKey(CreateMasterKey());

    protected readonly CollectionNamespace KeyVaultNamespace =
        CollectionNamespace.FromFullName(database.MongoDatabase.DatabaseNamespace.DatabaseName + "._keyVault");

    static EncryptionTestsBase()
    {
        try
        {
            BsonSerializer.TryRegisterSerializer(typeof(Guid), GuidSerializer.StandardInstance);
        }
        finally
        {
            MongoClientSettings.Extensions.AddAutoEncryption();
        }
    }

    protected static byte[] CreateMasterKey()
        => RandomNumberGenerator.GetBytes(96);

    private static Guid CreateDataKey(
        IMongoClient client,
        CollectionNamespace keyVaultNamespace,
        Dictionary<string, IReadOnlyDictionary<string, object>> kmsProviders)
    {
        var clientEncryptionOptions = new ClientEncryptionOptions(client, keyVaultNamespace, kmsProviders);
        using var clientEncryption = new ClientEncryption(clientEncryptionOptions);
        return clientEncryption.CreateDataKey("local", new DataKeyOptions(), CancellationToken.None);
    }

    protected Guid CreateDataKey() =>
        CreateDataKey(database.Client, KeyVaultNamespace, KmsProviders);

    protected static BsonBinaryData AsBsonBinary(Guid dataKey)
        => new(dataKey, GuidRepresentation.Standard);

    protected static Dictionary<string,
        IReadOnlyDictionary<string, object>> CreateKmsProvidersWithLocalMasterKey(byte[] masterKey)
        => new() { { "local", new Dictionary<string, object> { { "key", masterKey } } } };

    public class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
        public string State { get; set; }
    }
}
