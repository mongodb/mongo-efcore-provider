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
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Visitors;

// Pins that PopulateCollection throws an InvalidOperationException naming the navigation (not a bare
// InvalidCastException) when the collection doesn't implement ICollection<TEntity>. Invoked via reflection
// because EF model validation may reject such a navigation type earlier.
public class MongoProjectionBindingRemovingExpressionVisitorTests
{
    private class Post
    {
        public int Id { get; set; }
    }

    // Create() returns an enumerator, which doesn't implement ICollection<Post>.
    private sealed class NonCollectionAccessor : IClrCollectionAccessor
    {
        public Type CollectionType => typeof(Post[]);
        public object Create() => new Post[0].GetEnumerator();
        public object GetOrCreate(object entity, bool forMaterialization) => Create();
        public bool Add(object entity, object value, bool forMaterialization) => throw new NotImplementedException();
        public bool AddStandalone(object entity, object value) => throw new NotImplementedException();
        public bool Contains(object entity, object value) => throw new NotImplementedException();
        public bool ContainsStandalone(object entity, object value) => throw new NotImplementedException();
        public bool Remove(object entity, object value) => throw new NotImplementedException();
        public bool RemoveStandalone(object? entity, object value) => throw new NotImplementedException();
    }

    private static readonly MethodInfo PopulateCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor)
            .GetMethod("PopulateCollection", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Post), typeof(List<Post>));

    [Fact]
    public void Non_ICollection_navigation_throws_a_diagnosable_exception_naming_the_navigation()
    {
        var accessor = new NonCollectionAccessor();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            PopulateCollectionMethodInfo.Invoke(
                null, [accessor, "Blog", "Posts", Array.Empty<Post>()]));

        var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Blog.Posts", inner.Message);
        Assert.Contains("ICollection", inner.Message);
    }
}
