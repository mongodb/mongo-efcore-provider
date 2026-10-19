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

using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

/// <summary>
/// Unit coverage for <see cref="MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback"/>:
/// the tier-conditional decision to strip the pushed-down <c>Select</c> when <c>TryBuildNativeFactory</c>
/// declines late, so the driver-LINQ fallback hands the alias-addressed DOM shaper whole documents instead of the
/// driver's own <c>$project</c>. The decision reads the alias tier, never the alias string.
/// </summary>
public class Step3aBareProjectionFallbackStripTests
{
    [Fact]
    public void No_bare_override_does_not_strip()
    {
        // With no override registered, nothing is stripped.
        var select = new MongoSelectDefinition();

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_named_document_path_override_strips_even_though_it_is_not_the_bare_override()
    {
        // The predicate reads the tier of any override, not just the bare one. For
        // `Select(b => new { b.Title, b.Home.Notes })` behind a parameterized Where (late fallback), the shaper
        // reads "Home.Notes" but the driver's $project emits "Notes", returning empty collections under Native
        // with no error.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("Notes", "Home.Notes", ProjectionAliasTier.DocumentPath);

        Assert.Null(select.BareProjectionTier);
        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_named_synthetic_override_does_not_strip()
    {
        // Mirror of the case above: a synthetic alias has no document path, so whole documents can't satisfy it.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("N", "_v", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_document_path_bare_leaf_strips()
    {
        // Tier 1: the alias is the leaf's root-relative document path, so whole documents are what the shaper
        // reads.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.DocumentPath);

        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_synthetic_bare_leaf_does_not_strip()
    {
        // Tier 2: a computed leaf has no document path; the driver's push-down genuinely writes `_v`. Stripping
        // yields "Document element '_v' is missing but required".
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_document_path_bare_leaf_aliased_underscore_v_still_strips_because_the_tier_is_read_not_the_alias()
    {
        // A tier-1 leaf whose stored element is named `_v` must still strip; an `alias != "_v"` check would
        // silently reinstate the wrong-data route.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.DocumentPath);

        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_synthetic_bare_leaf_not_aliased_underscore_v_still_does_not_strip_because_the_tier_is_read_not_the_alias()
    {
        // Mirror image: a synthetic leaf with any other alias must not strip. Together with the case above,
        // this pins that the decision depends on the tier alone.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void The_decision_does_not_consume_or_mutate_the_carrier()
    {
        // Repeated reads (e.g. a re-compiled query) must give the same answer.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Posts", ProjectionAliasTier.DocumentPath);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
            Assert.Equal(ProjectionAliasTier.DocumentPath, select.BareProjectionTier);
        }
    }
}
