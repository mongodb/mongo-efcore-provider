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
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MongoDB.EntityFrameworkCore;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

public class ExpressionExtensionMethodsTests
{
    private class OneArgDto
    {
        public string Value { get; }
        public OneArgDto(string value) => Value = value;
    }

    private class TwoArgDto
    {
        public string A { get; }
        public int B { get; }
        public TwoArgDto(string a, int b) { A = a; B = b; }
    }

    private static ConstructorInfo Ctor<T>(int argCount)
        => typeof(T).GetConstructors().Single(c => c.GetParameters().Length == argCount);

    [Fact]
    public void Ctor_only_new_expression_declines_by_default()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members);

        Assert.False(result);
        Assert.Empty(members);
    }

    [Fact]
    public void Ctor_only_new_expression_declines_when_flag_explicitly_false()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: false);

        Assert.False(result);
        Assert.Empty(members);
    }

    [Fact]
    public void Ctor_only_new_expression_admits_one_positional_argument_when_flag_true()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        var member = Assert.Single(members);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "0", member.MemberName);
        Assert.Same(arg, member.Value);
    }

    [Fact]
    public void Ctor_only_new_expression_admits_multiple_positional_arguments_in_order_when_flag_true()
    {
        var argA = Expression.Constant("hello");
        var argB = Expression.Constant(42);
        var newExpr = Expression.New(Ctor<TwoArgDto>(2), argA, argB);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        Assert.Equal(2, members.Count);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "0", members[0].MemberName);
        Assert.Same(argA, members[0].Value);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "1", members[1].MemberName);
        Assert.Same(argB, members[1].Value);
    }

    [Fact]
    public void Named_members_new_expression_is_unaffected_by_the_new_flag()
    {
        // Anonymous type: NewExpression.Members is populated, so allowPositionalConstructorArguments: true must
        // not change this arm's behavior.
        Expression<System.Func<string, int, object>> lambda = (a, b) => new { A = a, B = b };
        var newExpr = (NewExpression)lambda.Body;

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        Assert.Equal(2, members.Count);
        Assert.Equal("A", members[0].MemberName);
        Assert.Equal("B", members[1].MemberName);
    }

    [Fact]
    public void Ctor_only_new_expression_with_zero_arguments_declines_even_when_flag_true()
    {
        var newExpr = Expression.New(typeof(object));

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.False(result);
        Assert.Empty(members);
    }

    [Fact]
    public void Array_container_is_read_positionally_only_with_allowContainerElements()
    {
        Expression<System.Func<int, string, object[]>> lambda = (a, b) => new object[] { a, b };

        Assert.False(lambda.Body.TryGetProjectionMembers(out _));
        Assert.False(lambda.Body.TryGetProjectionMembers(out _, allowPositionalConstructorArguments: true));

        Assert.True(lambda.Body.TryGetProjectionMembers(out var members, allowContainerElements: true));
        Assert.Equal(["_ctorArg0", "_ctorArg1"], members.Select(m => m.MemberName));
        Assert.Same(((NewArrayExpression)lambda.Body).Expressions[1], members[1].Value);

        var rebuilt = lambda.Body.RebuildProjectionMembers(
            [Expression.Convert(Expression.Constant(7), typeof(object)), Expression.Constant("x")]);
        Assert.Equal(new object[] { 7, "x" }, Expression.Lambda<System.Func<object[]>>(rebuilt).Compile()());
    }

    [Fact]
    public void List_initializer_and_empty_array_are_never_containers()
    {
        Expression<System.Func<int, List<object>>> list = a => new List<object> { a };
        Expression<System.Func<int, object[]>> empty = a => new object[] { };
        Expression<System.Func<int, object[]>> bounds = a => new object[a];

        Assert.False(list.Body.TryGetProjectionMembers(out _, allowContainerElements: true));
        Assert.False(empty.Body.TryGetProjectionMembers(out _, allowContainerElements: true));
        Assert.False(bounds.Body.TryGetProjectionMembers(out _, allowContainerElements: true));
    }

    // Family-A call sites (ordinary Select/Join projections) must never pass allowPositionalConstructorArguments:
    // true: the synthetic "_ctorArg0"-style aliases can't be resolved by EF Core's ProjectionMember/MemberInfo-keyed
    // read side, silently breaking projection reads. Otherwise enforced only by TryGetProjectionMembers' doc, so pin
    // it with a source-text check.
    [Fact]
    public void Family_A_call_sites_never_opt_in_to_allowPositionalConstructorArguments()
    {
        var repoRoot = RepoRoot();

        // Expected call counts per file, so an added or removed call site is noticed. The one opted-in (family-B)
        // site in NativeProjectionBinder.cs is the multi-argument positional-ctor-DTO arm, which is read back by
        // index (see MongoSelectDefinition.HasPositionalCtorProjectionShaper), not via ProjectionMember. The
        // allowContainerElements opt-ins are the positional-container arms (NativeProjectionBinder's container arm,
        // NativeJoinScopeProjectionBinder.TryBindProjection), also read back by index; that flag is deliberately separate
        // so the GroupBy/SelectMany result selectors sharing allowPositionalConstructorArguments are not widened.
        var sources = new (string RelativePath, int ExpectedCallCount, int ExpectedOptedInCallCount, int ExpectedContainerOptInCount)[]
        {
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs", 4, 1, 1),
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs", 3, 0, 1),
            // Family B (read by index), pinned here only so neither widens to containers.
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs", 2, 1, 0),
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs", 2, 2, 0),
        };

        foreach (var (relativePath, expectedCallCount, expectedOptedInCallCount, expectedContainerOptInCount) in sources)
        {
            var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Expected source file not found: {fullPath}");

            var callSiteLines = File.ReadLines(fullPath)
                .Where(line => line.Contains("TryGetProjectionMembers(") && !line.Contains("static bool TryGetProjectionMembers"))
                .ToList();

            Assert.True(
                callSiteLines.Count == expectedCallCount,
                $"{relativePath}: expected {expectedCallCount} TryGetProjectionMembers(...) call site(s), found "
                + $"{callSiteLines.Count}. If a call site was added/removed, update this test's expectations "
                + "(and confirm the new/removed site does not opt in to allowPositionalConstructorArguments).");

            var optedInLines = callSiteLines
                .Where(line => Regex.IsMatch(line, @"allowPositionalConstructorArguments\s*:\s*true"))
                .ToList();

            Assert.True(
                optedInLines.Count == expectedOptedInCallCount,
                $"{relativePath}: expected {expectedOptedInCallCount} call site(s) opting in to "
                + $"allowPositionalConstructorArguments: true, found {optedInLines.Count}. A family-A call site "
                + "must NOT pass allowPositionalConstructorArguments: true — doing so lets a wrapped member "
                + "resolve to a synthetic positional alias that this call site's ProjectionMember/MemberInfo-"
                + "keyed read cannot find — see TryGetProjectionMembers' parameter doc for who may pass true.");

            var containerOptedInLines = callSiteLines
                .Where(line => Regex.IsMatch(line, @"allowContainerElements\s*:\s*true"))
                .ToList();

            Assert.True(
                containerOptedInLines.Count == expectedContainerOptInCount,
                $"{relativePath}: expected {expectedContainerOptInCount} call site(s) opting in to "
                + $"allowContainerElements: true, found {containerOptedInLines.Count}. Only the plain-Select container "
                + "arms may; see TryGetProjectionMembers' parameter doc.");
        }
    }

    /// <summary>
    /// Repo root, found from this file's path via <see cref="CallerFilePathAttribute"/> so it's independent of the
    /// runner's working directory.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", "..", ".."));
}
