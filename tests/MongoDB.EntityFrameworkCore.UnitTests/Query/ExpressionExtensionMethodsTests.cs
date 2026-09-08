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
        // An anonymous type: NewExpression.Members IS populated by the compiler. Passing
        // allowPositionalConstructorArguments: true must not change this arm's behavior at all.
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

    // The whole safety story for family A (ordinary Select/Join projections) depends on exactly 3 call sites
    // NEVER passing allowPositionalConstructorArguments: true — see TryGetProjectionMembers' own parameter doc.
    // Passing true there would let a wrapped member's alias be a synthetic positional pseudo-name
    // ("_ctorArg0", ...) that EF Core's ProjectionMember/MemberInfo-keyed read side (which those 3 call sites
    // alone rely on) can never resolve, silently breaking projection reads. This is currently enforced only by
    // that doc comment, so pin it with a cheap source-text check: every call site outside the
    // known family-B/opt-in set must not pass true.
    [Fact]
    public void Family_A_call_sites_never_opt_in_to_allowPositionalConstructorArguments()
    {
        var repoRoot = RepoRoot();

        // The 3 family-A call sites this ticket's design doc calls out as load-bearing: NativeProjectionBinder's
        // WRAPPED arm, its document-construction leaf, and NativeJoinScopeProjectionBinder.TryBindProjection.
        // Listed with their expected call count so a call site silently added or removed doesn't go unnoticed.
        var familyASources = new (string RelativePath, int ExpectedCallCount)[]
        {
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs", 2),
            ("src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs", 1),
        };

        foreach (var (relativePath, expectedCallCount) in familyASources)
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

            foreach (var line in callSiteLines)
            {
                Assert.False(
                    Regex.IsMatch(line, @"allowPositionalConstructorArguments\s*:\s*true"),
                    $"{relativePath}: a family-A call site must NOT pass allowPositionalConstructorArguments: "
                    + $"true (line: '{line.Trim()}'). Doing so lets a wrapped member resolve to a synthetic "
                    + "positional alias that this call site's ProjectionMember/MemberInfo-keyed read cannot "
                    + "find — see TryGetProjectionMembers' parameter doc for who may pass true.");
            }
        }
    }

    /// <summary>
    /// Walks up from this test file's own on-disk path (captured via <see cref="CallerFilePathAttribute"/>, so
    /// this works regardless of the test runner's working directory or build output layout) to the repo root —
    /// three levels up from <c>tests/MongoDB.EntityFrameworkCore.UnitTests/Query/</c>.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFilePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", "..", ".."));
}
