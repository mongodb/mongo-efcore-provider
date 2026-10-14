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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// A thin decorator over an <see cref="IMongoCollection{TDocument}"/> that overrides only the
/// <see cref="DocumentSerializer"/> so it returns EF Core's entity serializer instead of the one
/// resolved from the global <see cref="BsonSerializer"/> registry.
/// </summary>
/// <remarks>
/// <para>
/// This exists to support cross-collection <c>Join</c> / <c>GroupJoin</c> (used by cross-collection
/// <c>Include</c> and navigation translation). The MongoDB C# driver's
/// <c>JoinMethodToPipelineTranslator</c> requires the join's inner operand to be a bare
/// <c>IMongoQueryable</c> backed by a collection (a <see cref="System.Linq.Expressions.ConstantExpression"/>);
/// it rejects an operand wrapped in <c>.As(serializer)</c> (a <c>MethodCallExpression</c>).
/// We therefore cannot use <c>.As(...)</c> to inject EF's serializer on the inner side.
/// </para>
/// <para>
/// The driver derives the inner pipeline-input serializer from <c>collection.DocumentSerializer</c>
/// (see <c>MongoQueryProvider&lt;TDocument&gt;.PipelineInputSerializer</c>). By wrapping the collection
/// and overriding only <see cref="DocumentSerializer"/>, calling <c>AsQueryable()</c> produces a
/// bare-collection queryable the join translator accepts, while still carrying EF's element-name /
/// discriminator / BSON-representation mappings on the inner side.
/// </para>
/// <para>
/// All other members delegate to the wrapped collection. The driver only ever reads
/// <see cref="CollectionNamespace"/> and <see cref="DocumentSerializer"/> from this instance during
/// query translation; the remaining members are present only to satisfy the interface.
/// </para>
/// </remarks>
internal sealed class SerializerOverrideCollection<TDocument> : IMongoCollection<TDocument>
{
    private readonly IMongoCollection<TDocument> _wrapped;
    private readonly IBsonSerializer<TDocument> _documentSerializer;

    public SerializerOverrideCollection(IMongoCollection<TDocument> wrapped, IBsonSerializer<TDocument> documentSerializer)
    {
        _wrapped = wrapped;
        _documentSerializer = documentSerializer;
    }

    public CollectionNamespace CollectionNamespace => _wrapped.CollectionNamespace;

    public IMongoDatabase Database => _wrapped.Database;

    public IBsonSerializer<TDocument> DocumentSerializer => _documentSerializer;

    public IMongoIndexManager<TDocument> Indexes => _wrapped.Indexes;

    public IMongoSearchIndexManager SearchIndexes => _wrapped.SearchIndexes;

    public MongoCollectionSettings Settings => _wrapped.Settings;

    public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Aggregate(pipeline, options, cancellationToken);

    public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Aggregate(session, pipeline, options, cancellationToken);

    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateAsync(pipeline, options, cancellationToken);

    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateAsync(session, pipeline, options, cancellationToken);

    public void AggregateToCollection<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateToCollection(pipeline, options, cancellationToken);

    public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateToCollection(session, pipeline, options, cancellationToken);

    public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateToCollectionAsync(pipeline, options, cancellationToken);

    public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.AggregateToCollectionAsync(session, pipeline, options, cancellationToken);

    public BulkWriteResult<TDocument> BulkWrite(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.BulkWrite(requests, options, cancellationToken);

    public BulkWriteResult<TDocument> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.BulkWrite(session, requests, options, cancellationToken);

    public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.BulkWriteAsync(requests, options, cancellationToken);

    public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.BulkWriteAsync(session, requests, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public long Count(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Count(filter, options, cancellationToken);

    public long Count(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Count(session, filter, options, cancellationToken);

    public Task<long> CountAsync(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountAsync(filter, options, cancellationToken);

    public Task<long> CountAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountAsync(session, filter, options, cancellationToken);
#pragma warning restore CS0618

    public long CountDocuments(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountDocuments(filter, options, cancellationToken);

    public long CountDocuments(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountDocuments(session, filter, options, cancellationToken);

    public Task<long> CountDocumentsAsync(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountDocumentsAsync(filter, options, cancellationToken);

    public Task<long> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.CountDocumentsAsync(session, filter, options, cancellationToken);

    public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => _wrapped.DeleteMany(filter, cancellationToken);

    public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => _wrapped.DeleteMany(filter, options, cancellationToken);

    public DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DeleteMany(session, filter, options, cancellationToken);

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => _wrapped.DeleteManyAsync(filter, cancellationToken);

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => _wrapped.DeleteManyAsync(filter, options, cancellationToken);

    public Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DeleteManyAsync(session, filter, options, cancellationToken);

    public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOne(filter, cancellationToken);

    public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOne(filter, options, cancellationToken);

    public DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOne(session, filter, options, cancellationToken);

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOneAsync(filter, cancellationToken);

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOneAsync(filter, options, cancellationToken);

    public Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DeleteOneAsync(session, filter, options, cancellationToken);

    public IAsyncCursor<TField> Distinct<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Distinct(field, filter, options, cancellationToken);

    public IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Distinct(session, field, filter, options, cancellationToken);

    public Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctAsync(field, filter, options, cancellationToken);

    public Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctAsync(session, field, filter, options, cancellationToken);

    public IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctMany(field, filter, options, cancellationToken);

    public IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctMany(session, field, filter, options, cancellationToken);

    public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctManyAsync(field, filter, options, cancellationToken);

    public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.DistinctManyAsync(session, field, filter, options, cancellationToken);

    public long EstimatedDocumentCount(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.EstimatedDocumentCount(options, cancellationToken);

    public Task<long> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.EstimatedDocumentCountAsync(options, cancellationToken);

    public IAsyncCursor<TProjection> FindSync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindSync(filter, options, cancellationToken);

    public IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindSync(session, filter, options, cancellationToken);

    public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindAsync(filter, options, cancellationToken);

    public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindAsync(session, filter, options, cancellationToken);

    public TProjection FindOneAndDelete<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndDelete(filter, options, cancellationToken);

    public TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndDelete(session, filter, options, cancellationToken);

    public Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndDeleteAsync(filter, options, cancellationToken);

    public Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndDeleteAsync(session, filter, options, cancellationToken);

    public TProjection FindOneAndReplace<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndReplace(filter, replacement, options, cancellationToken);

    public TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndReplace(session, filter, replacement, options, cancellationToken);

    public Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndReplaceAsync(filter, replacement, options, cancellationToken);

    public Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndReplaceAsync(session, filter, replacement, options, cancellationToken);

    public TProjection FindOneAndUpdate<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndUpdate(filter, update, options, cancellationToken);

    public TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndUpdate(session, filter, update, options, cancellationToken);

    public Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndUpdateAsync(filter, update, options, cancellationToken);

    public Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.FindOneAndUpdateAsync(session, filter, update, options, cancellationToken);

    public void InsertOne(TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertOne(document, options, cancellationToken);

    public void InsertOne(IClientSessionHandle session, TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertOne(session, document, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public Task InsertOneAsync(TDocument document, CancellationToken _cancellationToken)
        => _wrapped.InsertOneAsync(document, _cancellationToken);
#pragma warning restore CS0618

    public Task InsertOneAsync(TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertOneAsync(document, options, cancellationToken);

    public Task InsertOneAsync(IClientSessionHandle session, TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertOneAsync(session, document, options, cancellationToken);

    public void InsertMany(IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertMany(documents, options, cancellationToken);

    public void InsertMany(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertMany(session, documents, options, cancellationToken);

    public Task InsertManyAsync(IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertManyAsync(documents, options, cancellationToken);

    public Task InsertManyAsync(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.InsertManyAsync(session, documents, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.MapReduce(map, reduce, options, cancellationToken);

    public IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.MapReduce(session, map, reduce, options, cancellationToken);

    public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.MapReduceAsync(map, reduce, options, cancellationToken);

    public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default)
        => _wrapped.MapReduceAsync(session, map, reduce, options, cancellationToken);
#pragma warning restore CS0618

    public IFilteredMongoCollection<TDerivedDocument> OfType<TDerivedDocument>() where TDerivedDocument : TDocument
        => _wrapped.OfType<TDerivedDocument>();

    public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOne(filter, replacement, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOne(filter, replacement, options, cancellationToken);
#pragma warning restore CS0618

    public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOne(session, filter, replacement, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOne(session, filter, replacement, options, cancellationToken);
#pragma warning restore CS0618

    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOneAsync(filter, replacement, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOneAsync(filter, replacement, options, cancellationToken);
#pragma warning restore CS0618

    public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOneAsync(session, filter, replacement, options, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
    public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => _wrapped.ReplaceOneAsync(session, filter, replacement, options, cancellationToken);
#pragma warning restore CS0618

    public UpdateResult UpdateMany(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateMany(filter, update, options, cancellationToken);

    public UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateMany(session, filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateManyAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateManyAsync(filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateManyAsync(session, filter, update, options, cancellationToken);

    public UpdateResult UpdateOne(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateOne(filter, update, options, cancellationToken);

    public UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateOne(session, filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateOneAsync(filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.UpdateOneAsync(session, filter, update, options, cancellationToken);

    public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Watch(pipeline, options, cancellationToken);

    public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.Watch(session, pipeline, options, cancellationToken);

    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.WatchAsync(pipeline, options, cancellationToken);

    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default)
        => _wrapped.WatchAsync(session, pipeline, options, cancellationToken);

    public IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern)
        => new SerializerOverrideCollection<TDocument>(_wrapped.WithReadConcern(readConcern), _documentSerializer);

    public IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference)
        => new SerializerOverrideCollection<TDocument>(_wrapped.WithReadPreference(readPreference), _documentSerializer);

    public IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern)
        => new SerializerOverrideCollection<TDocument>(_wrapped.WithWriteConcern(writeConcern), _documentSerializer);
}
