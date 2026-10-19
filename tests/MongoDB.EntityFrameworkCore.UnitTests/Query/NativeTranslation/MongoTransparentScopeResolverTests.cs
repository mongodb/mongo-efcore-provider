// Copyright 2023-present MongoDB Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoTransparentScopeResolverTests
{
    private sealed class Ti
    {
        public Ti Outer { get; set; } = null!;
        public Ti Inner { get; set; } = null!;
    }

    private sealed class JoinResult
    {
        public JoinResult Left { get; set; } = null!;
        public JoinResult Right { get; set; } = null!;
    }

    [Fact]
    public void Resolves_root_scope_for_pure_Outer_chain_matching_source_count()
    {
        var ti = Expression.Parameter(typeof(Ti), "ti");
        Expression access = Expression.Property(Expression.Property(ti, "Outer"), "Outer");

        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            access, ti, hopNames: ["Outer", "Inner"], sourceCount: 2, out var scopeIndex);

        Assert.True(resolved);
        Assert.Equal(0, scopeIndex);
    }

    [Fact]
    public void Resolves_inner_scope_for_trailing_Inner_hop()
    {
        var ti = Expression.Parameter(typeof(Ti), "ti");
        Expression access = Expression.Property(Expression.Property(ti, "Outer"), "Inner");

        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            access, ti, hopNames: ["Outer", "Inner"], sourceCount: 2, out var scopeIndex);

        Assert.True(resolved);
        Assert.Equal(1, scopeIndex);
    }

    [Fact]
    public void Supports_arbitrary_hop_names_for_a_join_scope()
    {
        // Custom hop names (e.g., "Left"/"Right" for a Join result selector) prove hopNames is data-driven.
        var result = Expression.Parameter(typeof(JoinResult), "result");

        // result.Left (root scope: 1 leading "Left" hop, 0 "Right" hops)
        var rootAccess = Expression.Property(result, "Left");
        var resolved = MongoTransparentScopeResolver.TryResolveScopeDepth(
            rootAccess, result, hopNames: ["Left", "Right"], sourceCount: 1, out var rootIndex);
        Assert.True(resolved);
        Assert.Equal(0, rootIndex);

        // result.Right (inner scope 1: 0 leading "Left" hops, 1 trailing "Right" hop)
        var innerAccess = Expression.Property(result, "Right");
        var resolvedInner = MongoTransparentScopeResolver.TryResolveScopeDepth(
            innerAccess, result, hopNames: ["Left", "Right"], sourceCount: 1, out var innerIndex);
        Assert.True(resolvedInner);
        Assert.Equal(1, innerIndex);
    }
}
