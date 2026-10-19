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
/// EF-322 step 3a, Task 1b. Unit coverage for
/// <see cref="MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback"/> — the
/// tier-conditional decision to strip the pushed-down bare <c>Select</c> when <c>TryBuildNativeFactory</c>
/// declines late, so the driver-LINQ fallback hands the already alias-addressed DOM shaper whole documents
/// instead of a <c>$project</c> the driver keyed <c>_v</c>.
/// <para>
/// WHAT THESE TESTS DO AND DO NOT PROVE, stated because the distinction is the whole point of this task. They
/// pin the DECISION, and two of them discriminate it BY MUTATION from the spike's alias-string expedient
/// (<c>alias != "_v"</c>) — see the two "not by sniffing the alias string" cases below, which that expedient
/// gets exactly backwards. They do NOT exercise the strip itself: nothing writes the alias carrier until
/// Task 2 opens the emit gate, so no query reaches this predicate with a non-null tier and
/// <c>StripPushedDownSelect</c> is never called on the late-fallback route. The behavioural proof is Task 2's
/// parameterized-<c>Where</c> legs (design §9.3), whose bare nullable-scalar and bare owned-array cases go red
/// with the strip removed. Until then the only behavioural claim available is INERTNESS, pinned by the
/// empty-carrier case here and by the byte-identical specification-suite name sets on both axes.
/// </para>
/// </summary>
public class Step3aBareProjectionFallbackStripTests
{
    [Fact]
    public void No_bare_override_does_not_strip()
    {
        // The inertness pin. This is the state of EVERY query until Task 2 registers an override, so it is
        // also the reason the whole task is behaviour-neutral.
        var select = new MongoSelectDefinition();

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_named_document_path_override_strips_even_though_it_is_not_the_bare_override()
    {
        // FLIPPED BY EF-362, and the reasoning it replaces was wrong, not merely narrow. This case used to
        // assert `False`, on the ground that "a named-member override is not a bare projection at all, so the
        // driver's own alias for it is the member name, not `_v`, and there is nothing to strip".
        //
        // The driver's alias BEING the member name is exactly the problem: the shaper reads by "Home.Notes",
        // the driver's fallback $project emits "Notes", and the read misses. MEASURED on
        // `Select(b => new { b.Title, b.Home.Notes })` behind a parameterized Where (the late-fallback route):
        // EMPTY collections under the DEFAULT Native mode, no exception, where the DriverLinq and NativeOnly
        // legs of the same query were correct.
        //
        // So the predicate reads the TIER of ANY override, not "is this the bare one" — which is also what
        // keeps it a single fact rather than a per-override-family rule. Mutating it back to
        // `select.BareProjectionTier == ProjectionAliasTier.DocumentPath` turns this case red.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("Notes", "Home.Notes", ProjectionAliasTier.DocumentPath);

        Assert.Null(select.BareProjectionTier);
        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_named_synthetic_override_does_not_strip()
    {
        // The mirror image of the case above, and what keeps the widened predicate a TIER test rather than an
        // "any override at all" test: a synthetic alias has no document path, so whole documents are exactly
        // what it cannot read. Nothing registers a named synthetic override today (tier 2 was dropped from
        // slice 3a); this pins the decision so a future one cannot arrive silently on the wrong arm.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("N", "_v", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_document_path_bare_leaf_strips()
    {
        // Tier 1: the alias IS the leaf's root-relative document path, so the whole documents an
        // un-projected fallback yields are exactly what the shaper reads.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.DocumentPath);

        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_synthetic_bare_leaf_does_not_strip()
    {
        // Tier 2: a computed leaf has no document path, so leaving the driver's own push-down in place is
        // what makes the read hit — its alias genuinely IS `_v`. Stripping here was measured to produce
        // "Document element '_v' is missing but required" for a working query.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_document_path_bare_leaf_aliased_underscore_v_still_strips_because_the_tier_is_read_not_the_alias()
    {
        // MUTATION DISCRIMINATOR 1. A tier-1 leaf whose document path happens to be spelled `_v` — a stored
        // element a user is free to name — must still strip. The spike's expedient conditional
        // (`alias != "_v"`) answers "do not strip" here, silently reinstating the wrong-data route this task
        // exists to close, so replacing the tier read with an alias-string sniff turns this case red.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.DocumentPath);

        Assert.True(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void A_synthetic_bare_leaf_not_aliased_underscore_v_still_does_not_strip_because_the_tier_is_read_not_the_alias()
    {
        // MUTATION DISCRIMINATOR 2, the mirror image. A synthetic leaf carrying any other alias must still
        // NOT strip; `alias != "_v"` answers "strip" here. Together with the case above, the two pin that the
        // decision is a function of the TIER alone and is independent of the alias string in both directions.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.Synthetic);

        Assert.False(MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback(select));
    }

    [Fact]
    public void The_decision_does_not_consume_or_mutate_the_carrier()
    {
        // The gate reads the carrier at compile time; a second read (a re-compiled query, or a future second
        // consumer) must get the same answer.
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
