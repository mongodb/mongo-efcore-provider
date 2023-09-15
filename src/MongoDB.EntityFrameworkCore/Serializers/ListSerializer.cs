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
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal class ListSerializer<TItem> :
    EnumerableSerializerBase<IReadOnlyList<TItem>, TItem>,
    IChildSerializerConfigurable
{
    public ListSerializer()
    {
    }

    public ListSerializer(IBsonSerializer<TItem> itemSerializer)
        : base(itemSerializer)
    {
    }

    public ListSerializer(IBsonSerializerRegistry serializerRegistry)
        : base(serializerRegistry)
    {
    }

    public ListSerializer<TItem> WithItemSerializer(IBsonSerializer<TItem> itemSerializer)
    {
        return new ListSerializer<TItem>(itemSerializer);
    }

    protected override void AddItem(object accumulator, TItem item)
    {
        ((List<TItem>)accumulator).Add(item);
    }

    protected override object CreateAccumulator()
    {
        return new List<TItem>();
    }

    protected override IEnumerable<TItem> EnumerateItemsInSerializationOrder(IReadOnlyList<TItem> value)
    {
        return value;
    }

    protected override List<TItem> FinalizeResult(object accumulator)
    {
        return (List<TItem>)accumulator;
    }

    IBsonSerializer IChildSerializerConfigurable.ChildSerializer
    {
        get { return ItemSerializer; }
    }

    IBsonSerializer IChildSerializerConfigurable.WithChildSerializer(IBsonSerializer childSerializer)
    {
        return WithItemSerializer((IBsonSerializer<TItem>)childSerializer);
    }
}
