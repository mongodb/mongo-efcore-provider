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

using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Storage;

internal class MongoUpdateBatch(string collectionName, List<MongoUpdate> updates)
{
    public string CollectionName { get => collectionName; }
    public List<MongoUpdate> Updates { get => updates; }

    public long Modified => updates.Count(u => u.Model.ModelType == WriteModelType.UpdateOne);
    public long Inserts => updates.Count(u => u.Model.ModelType == WriteModelType.InsertOne);
    public long Deletes => updates.Count(u => u.Model.ModelType == WriteModelType.DeleteOne);

    public static IEnumerable<MongoUpdateBatch> CreateBatches(IEnumerable<MongoUpdate> updates)
    {
        MongoUpdateBatch? batch = null;
        foreach (var update in updates)
        {
            if (batch == null)
            {
                batch = Create(update);
            }
            else
            {
                if (batch.CollectionName == update.Entry.EntityType.GetCollectionName())
                {
                    batch.Updates.Add(update);
                }
                else
                {
                    yield return batch;
                    batch = Create(update);
                }
            }
        }

        if (batch != null)
        {
            yield return batch;
        }
    }

    public static MongoUpdateBatch Create(MongoUpdate update)
        => new(update.Entry.EntityType.GetCollectionName(), [update]);
}
