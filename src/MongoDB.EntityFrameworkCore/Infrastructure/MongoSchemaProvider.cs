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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

public class MongoSchemaProvider : IMongoSchemaProvider
{
    private readonly IModel _model;

    public MongoSchemaProvider(IModel model)
    {
        _model = model;
    }

    public Dictionary<string, BsonDocument> GetSchema()
    {
        return QueryableEncryptionSchemaGenerator.GenerateSchemas(_model);
    }
}

public interface IMongoSchemaProvider
{
    Dictionary<string, BsonDocument> GetSchema();
}
