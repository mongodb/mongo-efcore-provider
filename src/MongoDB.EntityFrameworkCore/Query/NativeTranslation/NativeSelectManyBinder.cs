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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Binds the INNER-Select form of an owned-collection SelectMany (EF-347 slice 3) —
/// <c>o => o.Items.AsQueryable().Select(i => new { o.X, i.Y })</c> — to a native <c>$unwind</c> +
/// <c>$project</c>: sets <see cref="MongoSelectDefinition.UnwindSource"/> and populates
/// <see cref="MongoSelectDefinition.Projection"/>. Outer (closed-over) members resolve to root field refs
/// (<c>$X</c>); inner (Select-parameter) members resolve to the unwound element, prefixed with the unwind
/// path (<c>$Items.Y</c>). Returns false (select untouched) for any other shape — the caller returns null
/// and EF hard-fails translation, as before. The explicit-result-selector form (body is a bare
/// <c>Property(o,Nav).AsQueryable()</c>, projection in a separate subsequent Select) is out of scope and
/// rejected here (body is not a nested Select).
/// </summary>
internal static class NativeSelectManyBinder
{
    internal static bool TryBind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Body must be Queryable.Select(<source>, innerLambda).
        if (collectionSelector.Body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Select), DeclaringType: var selDecl },
                Arguments: [var selectSource, var innerLambdaArg]
            }
            || selDecl != typeof(System.Linq.Queryable))
            return false;

        // <source> must resolve to the outer parameter's owned-collection navigation. EF's nav-expansion
        // rewrites navigation access to EF.Property(o, "Nav") (shadow-nav-safe) rather than leaving a plain
        // MemberExpression, so both forms must be accepted here.
        var navExpr = UnwrapAsQueryable(selectSource);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navName);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        if (navigation.TargetEntityType.GetContainingElementName() is not { } unwindPath)
            return false;

        var innerLambda = innerLambdaArg.UnwrapLambdaFromQuote();
        if (innerLambda.Parameters.Count != 1)
            return false;
        var innerParam = innerLambda.Parameters[0];

        if (!TryReadProjection(innerLambda.Body, out var members))
            return false;

        var outerTranslator = new MongoExpressionTranslator(outerEntityType);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            if (!TryGetMemberAccess(argExpr, out var root, out _))
                return false;

            MongoFieldExpression field;
            if (ReferenceEquals(root, outerParam))
            {
                if (!outerTranslator.TryTranslateField(argExpr, out var f)) return false;
                field = f;
            }
            else if (ReferenceEquals(root, innerParam))
            {
                if (!innerTranslator.TryTranslateField(argExpr, out var innerF)) return false;
                field = new MongoFieldExpression(innerF.Property, unwindPath + "." + innerF.ElementName);
            }
            else
            {
                return false;
            }

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, field));
        }

        mongoQ.Select.UnwindSource = new MongoUnwindSource(unwindPath);
        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    private static Expression UnwrapAsQueryable(Expression e)
        => e is MethodCallExpression { Method.Name: nameof(System.Linq.Queryable.AsQueryable), Arguments: [var inner] }
            ? inner : e;

    /// <summary>
    /// Matches a member/navigation access in either of the two shapes EF Core produces: a plain
    /// <see cref="MemberExpression"/> (used for ordinary scalar property access, e.g. the leaves of the
    /// nested <c>Select</c>'s projection) or an <c>EF.Property(root, "Name")</c> call (the shadow-nav-safe
    /// form EF's nav-expansion rewrites navigation access into, e.g. <c>o.Items</c> becoming
    /// <c>EF.Property(o, "Items")</c>). Returns the accessed root expression and member name for either shape.
    /// </summary>
    private static bool TryGetMemberAccess(Expression expression, out Expression root, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } inner } member:
                root = inner;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var rootArg, ConstantExpression { Value: string propName }]:
                root = rootArg;
                name = propName;
                return true;

            default:
                root = null!;
                name = null!;
                return false;
        }
    }

    // new {...} (NewExpression with Members) or a parameterless MemberInit — mirrors NativeProjectionBinder.
    private static bool TryReadProjection(Expression body, out IReadOnlyList<(string Alias, Expression Arg)> members)
    {
        members = null!;
        var list = new List<(string, Expression)>();
        switch (body)
        {
            case NewExpression ne when ne.Members != null && ne.Members.Count == ne.Arguments.Count && ne.Arguments.Count > 0:
                for (var i = 0; i < ne.Arguments.Count; i++) list.Add((ne.Members[i].Name, ne.Arguments[i]));
                break;
            case MemberInitExpression mi when mi.NewExpression.Arguments.Count == 0 && mi.Bindings.Count > 0:
                foreach (var b in mi.Bindings)
                {
                    if (b is not MemberAssignment ma) return false;
                    list.Add((b.Member.Name, ma.Expression));
                }
                break;
            default:
                return false;
        }
        members = list;
        return true;
    }
}
