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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Exhaustiveness harness for the <see cref="MongoExpression"/> node hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Seven hand-rolled <c>switch</c> dispatchers cover the hierarchy (two dialect renderers, their two
/// classifiers, the negator, the field-prefix rewriter, the serialization-safety predicate), with no compiler
/// exhaustiveness check. A missing arm falls open to <c>$expr</c> in the query renderer, throws in the
/// aggregation renderer, and falls open to <see langword="true"/> in the serialization predicate.
/// </para>
/// <para>
/// Every concrete subtype is discovered by reflection. A new node type fails
/// <see cref="Every_node_type_has_a_representative_sample"/> until sampled, then
/// <see cref="Dispatcher_coverage_matrix_is_unchanged"/> until its row is declared, forcing the author to visit all
/// seven dispatchers. The matrix characterizes current behavior (some cells are latent bugs, noted in
/// <see cref="ExpectedMatrix"/>) so no cell changes silently.
/// </para>
/// <para>
/// <see cref="Every_node_the_query_dialect_classifier_admits_renders_in_that_dialect"/> and
/// <see cref="Every_node_CanRender_admits_actually_renders_in_the_aggregation_dialect"/> assert the real contract:
/// a classifier never admits a node its renderer would throw on.
/// </para>
/// </remarks>
public class MongoExpressionNodeCoverageTests
{
    // --- Entity model used to source real IProperty instances ---

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = null!;
        public int Rank { get; set; }
        public bool Flag { get; set; }
        public List<string> Tags { get; set; } = [];
        public DateTime When { get; set; }
        public DateTimeOffset Stamp { get; set; }
    }

    /// <summary>
    /// Resolves a property of the owned collection-element type. With <paramref name="valueConverted"/> every
    /// scalar carries a value converter, which makes the <see cref="SerializationSafetyConverted"/> column
    /// discriminating: a node with a recursing arm reports <c>false</c>, a node with no arm falls open to
    /// <c>true</c>.
    /// </summary>
    private static IProperty PostProperty(string propertyName, bool valueConverted)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(
            b => b.Posts,
            ob =>
            {
                if (!valueConverted)
                    return;

                // An explicit same-type ValueConverter: HasConversion<string>() on a string property is a no-op
                // that EF drops, which would blunt the converted column for string-rooted nodes.
                ob.Property(p => p.Heading).HasConversion(new ValueConverter<string, string>(v => v, v => v));
                ob.Property(p => p.Rank).HasConversion<string>();
                ob.Property(p => p.Flag).HasConversion<string>();
                ob.Property(p => p.When).HasConversion<string>();
                ob.Property(p => p.Stamp).HasConversion<string>();
            }));

        return db.Model.FindEntityType(typeof(Blog))!
            .FindNavigation(nameof(Blog.Posts))!.TargetEntityType.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Node discovery + samples

    private static IReadOnlyList<Type> AllConcreteNodeTypes()
        => typeof(MongoExpression).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(MongoExpression).IsAssignableFrom(t) && t != typeof(MongoExpression))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One representative instance per concrete node type — the simplest legal one, since the harness checks arm
    /// coverage, not value space.
    /// </summary>
    private static Dictionary<Type, MongoExpression> BuildSamples(bool valueConverted = false)
    {
        var rank = PostProperty(nameof(Post.Rank), valueConverted);
        var flag = PostProperty(nameof(Post.Flag), valueConverted);
        var heading = PostProperty(nameof(Post.Heading), valueConverted);
        var tags = PostProperty(nameof(Post.Tags), valueConverted);
        var when = PostProperty(nameof(Post.When), valueConverted);
        var stamp = PostProperty(nameof(Post.Stamp), valueConverted);

        var rankField = new MongoFieldExpression(rank, "Rank");
        var flagField = new MongoFieldExpression(flag, "Flag");
        var headingField = new MongoFieldExpression(heading, "Heading");
        var rankConstant = new MongoConstantExpression(5, rank);

        var samples = new MongoExpression[]
        {
            rankField,
            new MongoOuterFieldExpression(rank, "Rank"),
            new MongoElementRefExpression("Total", typeof(int)),
            new MongoLookupNullCheckExpression("_lookup_Manager", isNotNull: false),
            new MongoNumericTypeBracketExpression(rankField),
            rankConstant,
            new MongoParameterExpression("p0", rank),
            new MongoBinaryExpression(MongoBinaryOperator.Equal, rankField, rankConstant),
            new MongoUnaryExpression(MongoUnaryOperator.Not, flagField),
            new MongoSizeExpression("Tags", typeof(int)),
            new MongoFilteredSizeExpression("Tags", flagField, typeof(int)),
            new MongoInExpression(rankField, new MongoConstantExpression(new[] { 1, 2 }, rank), negated: false),
            new MongoValueListExpression([new MongoParameterExpression("p0", rank), rankConstant]),
            new MongoComputedInExpression(
                headingField, new MongoConstantExpression(new[] { "a", "b" }, heading), negated: false),
            new MongoArrayContainsExpression(
                new MongoFieldExpression(tags, "Tags"), new MongoConstantExpression("a", null), negated: false),
            new MongoArrayReduceExpression("$avg", "Total", typeof(double)),
            new MongoRegexExpression(
                headingField, MongoRegexKind.StartsWith, new MongoConstantExpression("a", heading), negated: false),
            new MongoElemMatchExpression("Posts", flagField, negated: false),
            new MongoQuantifierExpression(
                new MongoElementRefExpression("Posts", typeof(List<Post>)),
                flagField,
                MongoExpressionTranslator.MongoQuantifierKind.Any),
            new MongoConvertExpression(rankField, typeof(long)),
            new MongoConditionalExpression(flagField, rankField, rankConstant),
            new MongoCoalesceExpression(rankField, rankConstant),
            new MongoDatePartExpression(new MongoFieldExpression(when, "When"), MongoDatePart.Year),
            new MongoDateAddExpression(
                new MongoFieldExpression(when, "When"), MongoDateAddUnit.Minute, new MongoConstantExpression(5, null)),
            new MongoDateTimeOffsetLocalExpression(new MongoFieldExpression(stamp, "Stamp")),
            new MongoConcatExpression([headingField, new MongoConstantExpression("x", heading)]),
            new MongoStringIndexOfExpression(headingField, new MongoConstantExpression("x", heading)),
            new MongoStringLengthExpression(headingField),
            new MongoMathExpression(MongoMathFunction.Abs, [rankField], typeof(int)),
            new MongoTrimExpression(headingField, MongoTrimSide.Both, chars: null),
            new MongoStringFirstOrLastExpression(headingField, MongoStringFirstOrLastKind.First),
            new MongoDocumentConstructionExpression(
                Expression.New(typeof(object)), [(nameof(Post.Rank), rankField)]),
            new MongoTupleExpression([rankField, rankConstant])
        };

        return samples.ToDictionary(s => s.GetType());
    }

    // ------------------------------------------------------------------
    // Dispatcher probes

    private const string QueryRenderer = "QL.Render";
    private const string QueryClassifier = "QL.IsQueryDialectRenderable";
    private const string AggRenderer = "Agg.Render";
    private const string AggClassifier = "Agg.CanRender";
    private const string Negator = "Negator.TryNegate";
    private const string PrefixRewriter = "PrefixRewriter.Rewrite";
    private const string SerializationSafety = "AllFieldsDefaultSerialized";

    /// <summary>
    /// <see cref="SerializationSafety"/> probed against the value-converted samples — the column that detects a
    /// missing arm (see <see cref="PostProperty"/>).
    /// </summary>
    private const string SerializationSafetyConverted = "AllFieldsDefaultSerialized(converted)";

    private static readonly string[] Dispatchers =
    [
        QueryRenderer, QueryClassifier, AggRenderer, AggClassifier, Negator, PrefixRewriter, SerializationSafety,
        SerializationSafetyConverted
    ];

    /// <summary>
    /// Runs one node through one dispatcher and reduces the outcome to a stable token: <c>rendered</c>,
    /// <c>true</c>/<c>false</c>, <c>declined</c> (the provider's not-supported exception), or
    /// <c>threw:&lt;ExceptionType&gt;</c> (usually an arm assuming a shape the node doesn't have).
    /// </summary>
    private static string Probe(string dispatcher, MongoExpression node)
    {
        try
        {
            switch (dispatcher)
            {
                case QueryRenderer:
                    new MongoQueryLanguageRenderer().Render(node, new PlaceholderTable());
                    return "rendered";
                case QueryClassifier:
                    return MongoQueryLanguageRenderer.IsQueryDialectRenderable(node) ? "true" : "false";
                case AggRenderer:
                    MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());
                    return "rendered";
                case AggClassifier:
                    return MongoAggregationExpressionRenderer.CanRender(node) ? "true" : "false";
                case Negator:
                    return MongoExpressionNegator.TryNegate(node, out _) ? "true" : "false";
                case PrefixRewriter:
                    return MongoFieldPrefixRewriter.TryRewrite(node, "pfx", out _) ? "rendered" : "declined";
                case SerializationSafety:
                case SerializationSafetyConverted:
                    return MongoExpressionTranslator.AllFieldsDefaultSerialized(node) ? "true" : "false";
                default:
                    throw new InvalidOperationException($"Unknown dispatcher '{dispatcher}'.");
            }
        }
        catch (NativeTranslationNotSupportedException)
        {
            return "declined";
        }
        catch (Exception e)
        {
            return "threw:" + e.GetType().Name;
        }
    }

    // ------------------------------------------------------------------
    // Tests

    [Fact]
    public void Every_node_type_has_a_representative_sample()
    {
        var samples = BuildSamples();
        var missing = AllConcreteNodeTypes().Where(t => !samples.ContainsKey(t)).Select(t => t.Name).ToList();

        Assert.True(
            missing.Count == 0,
            $"MongoExpression node type(s) with no sample in {nameof(BuildSamples)}: {string.Join(", ", missing)}. "
            + "Add a sample, then run Dispatcher_coverage_matrix_is_unchanged and declare the new row — that test "
            + "failing is the prompt to check the node against ALL seven dispatchers, not just the one your "
            + "feature needed.");

        // A sample for a type that no longer exists would be a stale row that can never fail.
        var stale = samples.Keys.Except(AllConcreteNodeTypes()).Select(t => t.Name).ToList();
        Assert.True(stale.Count == 0, $"Sample(s) for non-existent node type(s): {string.Join(", ", stale)}.");
    }

    [Fact]
    public void Dispatcher_coverage_matrix_is_unchanged()
    {
        var samples = BuildSamples();
        var convertedSamples = BuildSamples(valueConverted: true);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in AllConcreteNodeTypes())
        {
            if (!samples.TryGetValue(type, out var node))
                continue; // reported by Every_node_type_has_a_representative_sample

            foreach (var dispatcher in Dispatchers)
            {
                var probed = dispatcher == SerializationSafetyConverted ? convertedSamples[type] : node;
                actual[$"{type.Name}|{dispatcher}"] = Probe(dispatcher, probed);
            }
        }

        var differences = new List<string>();

        foreach (var (key, observed) in actual)
        {
            if (!ExpectedMatrix.TryGetValue(key, out var expected))
                differences.Add($"  + {key} = {observed}   (new cell — declare it)");
            else if (expected != observed)
                differences.Add($"  ! {key}: expected {expected}, observed {observed}");
        }

        foreach (var key in ExpectedMatrix.Keys.Where(k => !actual.ContainsKey(k)))
            differences.Add($"  - {key}   (stale cell — remove it)");

        Assert.True(
            differences.Count == 0,
            "The MongoExpression dispatcher coverage matrix changed.\n"
            + "Each cell is one node type's observed outcome in one dispatcher. A change here is either a\n"
            + "deliberate dispatcher edit (update the cell and say why in the commit) or an accident (a node\n"
            + "type added without visiting all seven dispatchers).\n\n"
            + string.Join("\n", differences));
    }

    [Fact]
    public void Every_node_the_query_dialect_classifier_admits_renders_in_that_dialect()
    {
        var samples = BuildSamples();

        foreach (var (type, node) in samples)
        {
            if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
                continue;

            var outcome = Probe(QueryRenderer, node);
            Assert.True(
                outcome == "rendered",
                $"{type.Name} is admitted by IsQueryDialectRenderable but Render produced '{outcome}'. "
                + "A classifier must never admit a node its own renderer cannot render — a node the classifier "
                + "admits and the renderer refuses is a hard failure at execution time, not a graceful decline.");
        }
    }

    [Fact]
    public void Every_node_CanRender_admits_actually_renders_in_the_aggregation_dialect()
    {
        var samples = BuildSamples();

        foreach (var (type, node) in samples)
        {
            if (!MongoAggregationExpressionRenderer.CanRender(node))
                continue;

            var outcome = Probe(AggRenderer, node);
            Assert.True(
                outcome == "rendered",
                $"{type.Name} is admitted by CanRender but Render produced '{outcome}'. CanRender's documented "
                + "contract is 'would Render throw' — an admitted node that throws breaks the translate-time "
                + "decline every caller of CanRender relies on.");
        }
    }

    /// <summary>
    /// Pins the four dispatcher answers for a field-to-field <see cref="MongoRegexExpression"/> (Term is a
    /// <see cref="MongoFieldExpression"/>, e.g. <c>c.ContactName.StartsWith(c.ContactName)</c>). The matrix holds
    /// one sample per node type (constant-term), but this node's behavior depends on the shape of its
    /// <c>Term</c>, so the field-term variant needs its own test — see <c>Query/AGENTS.md</c>.
    /// </summary>
    [Fact]
    public void Field_to_field_regex_term_shape_is_pinned_across_all_four_dispatchers()
    {
        var heading = PostProperty(nameof(Post.Heading), valueConverted: false);
        var headingField = new MongoFieldExpression(heading, "Heading");
        var fieldToFieldRegex = new MongoRegexExpression(
            headingField, MongoRegexKind.StartsWith, new MongoFieldExpression(heading, "Heading"), negated: false);

        // No query-dialect form ($regularExpression needs a literal pattern); QL.Render falls through to $expr.
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(fieldToFieldRegex));
        Assert.Equal("rendered", Probe(QueryRenderer, fieldToFieldRegex));

        // The aggregation dialect has a form ($indexOfCP/$strLenCP), so classifier and renderer agree.
        Assert.True(MongoAggregationExpressionRenderer.CanRender(fieldToFieldRegex));
        Assert.Equal("rendered", Probe(AggRenderer, fieldToFieldRegex));

        // The negator admits it past the query-dialect gate: aggregation-only by design (see
        // MongoExpressionNegator's remarks).
        Assert.Equal("true", Probe(Negator, fieldToFieldRegex));
    }

    // ------------------------------------------------------------------
    // The baked-in matrix.

    /// <summary>
    /// Observed outcome per (node type, dispatcher). Characterizes current behavior so it cannot change silently;
    /// not every cell is desirable.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>
    /// <c>QL.Render</c> is <c>rendered</c> for every node type, including those the query-dialect classifier
    /// refuses (fall-open to <c>$expr</c>). <c>IsQueryDialectRenderable</c> is the only thing keeping an illegal
    /// <c>$expr</c> out of an <c>$elemMatch</c> position.
    /// </item>
    /// <item>
    /// <c>PrefixRewriter.Rewrite</c> is <c>declined</c> (a <see langword="false"/> return, so the caller falls back
    /// to driver-LINQ) for nodes with no prefixing rule.
    /// </item>
    /// <item>
    /// In the <c>(converted)</c> column only arm-bearing node kinds report <c>false</c>. Of those that fall open to
    /// <c>true</c>, <c>In</c>, <c>Size</c> and <c>ArrayContains</c> are deliberate (see
    /// <c>AllFieldsDefaultSerialized</c>), and <c>Constant</c>, <c>Parameter</c>, <c>ElementRef</c> and
    /// <c>ArrayReduce</c> carry no backing <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>. The
    /// rest (<c>ComputedIn</c>, <c>FilteredSize</c>, <c>ElemMatch</c>, <c>Quantifier</c>, <c>Regex</c>,
    /// <c>DocumentConstruction</c>) are undocumented omissions in the one dispatcher that fails open.
    /// </item>
    /// </list>
    /// </remarks>
    private static readonly Dictionary<string, string> ExpectedMatrix = new(StringComparer.Ordinal)
    {
        ["MongoArrayContainsExpression|Agg.CanRender"] = "false",
        ["MongoArrayContainsExpression|Agg.Render"] = "declined",
        ["MongoArrayContainsExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoArrayContainsExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoArrayContainsExpression|Negator.TryNegate"] = "true",
        ["MongoArrayContainsExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoArrayContainsExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoArrayContainsExpression|QL.Render"] = "rendered",

        // Aggregation-only (over a synthetic $group alias), so no negator arm and QL.Render falls through to
        // $expr. Agg.Render has an arm but Agg.CanRender does not. FieldName has no backing IProperty, so both
        // serialization columns read true.
        ["MongoArrayReduceExpression|Agg.CanRender"] = "false",
        ["MongoArrayReduceExpression|Agg.Render"] = "rendered",
        ["MongoArrayReduceExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoArrayReduceExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoArrayReduceExpression|Negator.TryNegate"] = "false",
        ["MongoArrayReduceExpression|PrefixRewriter.Rewrite"] = "declined",
        ["MongoArrayReduceExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoArrayReduceExpression|QL.Render"] = "rendered",

        ["MongoBinaryExpression|Agg.CanRender"] = "true",
        ["MongoBinaryExpression|Agg.Render"] = "rendered",
        ["MongoBinaryExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoBinaryExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoBinaryExpression|Negator.TryNegate"] = "true",
        ["MongoBinaryExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoBinaryExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoBinaryExpression|QL.Render"] = "rendered",

        ["MongoComputedInExpression|Agg.CanRender"] = "true",
        ["MongoComputedInExpression|Agg.Render"] = "rendered",
        ["MongoComputedInExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoComputedInExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoComputedInExpression|Negator.TryNegate"] = "false",
        ["MongoComputedInExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoComputedInExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoComputedInExpression|QL.Render"] = "rendered",

        ["MongoConcatExpression|Agg.CanRender"] = "true",
        ["MongoConcatExpression|Agg.Render"] = "rendered",
        ["MongoConcatExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoConcatExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoConcatExpression|Negator.TryNegate"] = "false",
        ["MongoConcatExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoConcatExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoConcatExpression|QL.Render"] = "rendered",

        ["MongoConditionalExpression|Agg.CanRender"] = "true",
        ["MongoConditionalExpression|Agg.Render"] = "rendered",
        ["MongoConditionalExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoConditionalExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoConditionalExpression|Negator.TryNegate"] = "false",
        ["MongoConditionalExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoConditionalExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoConditionalExpression|QL.Render"] = "rendered",

        ["MongoCoalesceExpression|Agg.CanRender"] = "true",
        ["MongoCoalesceExpression|Agg.Render"] = "rendered",
        ["MongoCoalesceExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoCoalesceExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoCoalesceExpression|Negator.TryNegate"] = "false",
        ["MongoCoalesceExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoCoalesceExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoCoalesceExpression|QL.Render"] = "rendered",

        ["MongoConstantExpression|Agg.CanRender"] = "true",
        ["MongoConstantExpression|Agg.Render"] = "rendered",
        ["MongoConstantExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoConstantExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoConstantExpression|Negator.TryNegate"] = "false",
        ["MongoConstantExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoConstantExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoConstantExpression|QL.Render"] = "rendered",

        ["MongoConvertExpression|Agg.CanRender"] = "true",
        ["MongoConvertExpression|Agg.Render"] = "rendered",
        ["MongoConvertExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoConvertExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoConvertExpression|Negator.TryNegate"] = "false",
        ["MongoConvertExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoConvertExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoConvertExpression|QL.Render"] = "rendered",

        // Like MongoDatePartExpression: renders against StartDate's raw BSON, no query-dialect form, no negator arm.
        ["MongoDateAddExpression|Agg.CanRender"] = "true",
        ["MongoDateAddExpression|Agg.Render"] = "rendered",
        ["MongoDateAddExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoDateAddExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoDateAddExpression|Negator.TryNegate"] = "false",
        ["MongoDateAddExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoDateAddExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoDateAddExpression|QL.Render"] = "rendered",

        ["MongoDatePartExpression|Agg.CanRender"] = "true",
        ["MongoDatePartExpression|Agg.Render"] = "rendered",
        ["MongoDatePartExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoDatePartExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoDatePartExpression|Negator.TryNegate"] = "false",
        ["MongoDatePartExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoDatePartExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoDatePartExpression|QL.Render"] = "rendered",

        ["MongoDateTimeOffsetLocalExpression|Agg.CanRender"] = "true",
        ["MongoDateTimeOffsetLocalExpression|Agg.Render"] = "rendered",
        ["MongoDateTimeOffsetLocalExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoDateTimeOffsetLocalExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoDateTimeOffsetLocalExpression|Negator.TryNegate"] = "false",
        ["MongoDateTimeOffsetLocalExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoDateTimeOffsetLocalExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoDateTimeOffsetLocalExpression|QL.Render"] = "rendered",

        // Agg.CanRender has its own arm, matching Agg.Render.
        ["MongoDocumentConstructionExpression|Agg.CanRender"] = "true",
        ["MongoDocumentConstructionExpression|Agg.Render"] = "rendered",
        ["MongoDocumentConstructionExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoDocumentConstructionExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoDocumentConstructionExpression|Negator.TryNegate"] = "false",
        ["MongoDocumentConstructionExpression|PrefixRewriter.Rewrite"] = "declined",
        ["MongoDocumentConstructionExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoDocumentConstructionExpression|QL.Render"] = "rendered",

        ["MongoElemMatchExpression|Agg.CanRender"] = "false",
        ["MongoElemMatchExpression|Agg.Render"] = "declined",
        ["MongoElemMatchExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoElemMatchExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoElemMatchExpression|Negator.TryNegate"] = "true",
        ["MongoElemMatchExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoElemMatchExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoElemMatchExpression|QL.Render"] = "rendered",

        ["MongoElementRefExpression|Agg.CanRender"] = "true",
        ["MongoElementRefExpression|Agg.Render"] = "rendered",
        ["MongoElementRefExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoElementRefExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoElementRefExpression|Negator.TryNegate"] = "false",
        ["MongoElementRefExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoElementRefExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoElementRefExpression|QL.Render"] = "rendered",

        ["MongoFieldExpression|Agg.CanRender"] = "true",
        ["MongoFieldExpression|Agg.Render"] = "rendered",
        ["MongoFieldExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoFieldExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoFieldExpression|Negator.TryNegate"] = "false",
        ["MongoFieldExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoFieldExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoFieldExpression|QL.Render"] = "rendered",

        ["MongoFilteredSizeExpression|Agg.CanRender"] = "true",
        ["MongoFilteredSizeExpression|Agg.Render"] = "rendered",
        ["MongoFilteredSizeExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoFilteredSizeExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoFilteredSizeExpression|Negator.TryNegate"] = "false",
        ["MongoFilteredSizeExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoFilteredSizeExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoFilteredSizeExpression|QL.Render"] = "rendered",

        ["MongoInExpression|Agg.CanRender"] = "true",
        ["MongoInExpression|Agg.Render"] = "rendered",
        ["MongoInExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoInExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoInExpression|Negator.TryNegate"] = "true",
        ["MongoInExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoInExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoInExpression|QL.Render"] = "rendered",

        // Has a query-dialect form (Where position, TryMatchInnerNullCheck) and an aggregation form (Select
        // conditional Test, TryMatchScopeNullCheck). Never nested under Not/a quantifier/$elemMatch.
        ["MongoLookupNullCheckExpression|Agg.CanRender"] = "true",
        ["MongoLookupNullCheckExpression|Agg.Render"] = "rendered",
        ["MongoLookupNullCheckExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoLookupNullCheckExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoLookupNullCheckExpression|Negator.TryNegate"] = "false",
        ["MongoLookupNullCheckExpression|PrefixRewriter.Rewrite"] = "declined",
        ["MongoLookupNullCheckExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoLookupNullCheckExpression|QL.Render"] = "rendered",

        // Query-dialect-only by design: produced only as the Left conjunct of a translator-built AndAlso beside an
        // $expr sibling, so it is never negated or rendered inside $expr. AllFieldsDefaultSerialized falls open
        // to true, which is fine: it is only constructed after CanFallThroughToExpr confirmed default
        // serialization.
        ["MongoNumericTypeBracketExpression|Agg.CanRender"] = "false",
        ["MongoNumericTypeBracketExpression|Agg.Render"] = "declined",
        ["MongoNumericTypeBracketExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoNumericTypeBracketExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoNumericTypeBracketExpression|Negator.TryNegate"] = "false",
        ["MongoNumericTypeBracketExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoNumericTypeBracketExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoNumericTypeBracketExpression|QL.Render"] = "rendered",

        ["MongoOuterFieldExpression|Agg.CanRender"] = "true",
        ["MongoOuterFieldExpression|Agg.Render"] = "rendered",
        ["MongoOuterFieldExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoOuterFieldExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoOuterFieldExpression|Negator.TryNegate"] = "false",
        // Pass-through, not prefixed: root-anchored by definition.
        ["MongoOuterFieldExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoOuterFieldExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoOuterFieldExpression|QL.Render"] = "rendered",

        ["MongoParameterExpression|Agg.CanRender"] = "true",
        ["MongoParameterExpression|Agg.Render"] = "rendered",
        ["MongoParameterExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoParameterExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoParameterExpression|Negator.TryNegate"] = "false",
        ["MongoParameterExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoParameterExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoParameterExpression|QL.Render"] = "rendered",

        ["MongoQuantifierExpression|Agg.CanRender"] = "true",
        ["MongoQuantifierExpression|Agg.Render"] = "rendered",
        ["MongoQuantifierExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoQuantifierExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoQuantifierExpression|Negator.TryNegate"] = "true",
        ["MongoQuantifierExpression|PrefixRewriter.Rewrite"] = "declined",
        ["MongoQuantifierExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoQuantifierExpression|QL.Render"] = "rendered",

        // A constant-term regex is aggregation-renderable too: Agg.CanRender/Agg.Render callers are already past
        // the point where the query-dialect $regularExpression form was an option.
        ["MongoRegexExpression|Agg.CanRender"] = "true",
        ["MongoRegexExpression|Agg.Render"] = "rendered",
        ["MongoRegexExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoRegexExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoRegexExpression|Negator.TryNegate"] = "true",
        ["MongoRegexExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoRegexExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoRegexExpression|QL.Render"] = "rendered",

        ["MongoSizeExpression|Agg.CanRender"] = "true",
        ["MongoSizeExpression|Agg.Render"] = "rendered",
        ["MongoSizeExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoSizeExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoSizeExpression|Negator.TryNegate"] = "false",
        ["MongoSizeExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoSizeExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoSizeExpression|QL.Render"] = "rendered",

        // Produces an integer value, not a predicate: no query-dialect form, no negator arm.
        ["MongoStringIndexOfExpression|Agg.CanRender"] = "true",
        ["MongoStringIndexOfExpression|Agg.Render"] = "rendered",
        ["MongoStringIndexOfExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoStringIndexOfExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoStringIndexOfExpression|Negator.TryNegate"] = "false",
        ["MongoStringIndexOfExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoStringIndexOfExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoStringIndexOfExpression|QL.Render"] = "rendered",

        // $strLenCP over the operand's raw BSON; no query-dialect form, no negator arm.
        ["MongoStringLengthExpression|Agg.CanRender"] = "true",
        ["MongoStringLengthExpression|Agg.Render"] = "rendered",
        ["MongoStringLengthExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoStringLengthExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoStringLengthExpression|Negator.TryNegate"] = "false",
        ["MongoStringLengthExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoStringLengthExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoStringLengthExpression|QL.Render"] = "rendered",

        // Math/MathF operators over raw BSON; no query-dialect form (QL.Render's default arm wraps in $expr), no
        // negator arm.
        ["MongoMathExpression|Agg.CanRender"] = "true",
        ["MongoMathExpression|Agg.Render"] = "rendered",
        ["MongoMathExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoMathExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoMathExpression|Negator.TryNegate"] = "false",
        ["MongoMathExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoMathExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoMathExpression|QL.Render"] = "rendered",

        // $trim/$ltrim/$rtrim over raw BSON; no query-dialect form (QL.Render wraps in $expr), no negator arm.
        ["MongoTrimExpression|Agg.CanRender"] = "true",
        ["MongoTrimExpression|Agg.Render"] = "rendered",
        ["MongoTrimExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoTrimExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoTrimExpression|Negator.TryNegate"] = "false",
        ["MongoTrimExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoTrimExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoTrimExpression|QL.Render"] = "rendered",

        // $cond/$strLenCP/$substrCP over raw BSON; no query-dialect form (QL.Render wraps in $expr), no negator arm.
        ["MongoStringFirstOrLastExpression|Agg.CanRender"] = "true",
        ["MongoStringFirstOrLastExpression|Agg.Render"] = "rendered",
        ["MongoStringFirstOrLastExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoStringFirstOrLastExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoStringFirstOrLastExpression|Negator.TryNegate"] = "false",
        ["MongoStringFirstOrLastExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoStringFirstOrLastExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoStringFirstOrLastExpression|QL.Render"] = "rendered",

        ["MongoUnaryExpression|Agg.CanRender"] = "true",
        ["MongoUnaryExpression|Agg.Render"] = "rendered",
        ["MongoUnaryExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoUnaryExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoUnaryExpression|Negator.TryNegate"] = "true",
        ["MongoUnaryExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoUnaryExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoUnaryExpression|QL.Render"] = "rendered",

        // Not top-level-renderable: exists only as MongoInExpression.Values / MongoComputedInExpression.Values,
        // dispatched by RenderInValues/CanRenderInValues. As a bare node it falls closed everywhere.
        ["MongoValueListExpression|Agg.CanRender"] = "false",
        ["MongoValueListExpression|Agg.Render"] = "declined",
        ["MongoValueListExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoValueListExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoValueListExpression|Negator.TryNegate"] = "false",
        ["MongoValueListExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoValueListExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoValueListExpression|QL.Render"] = "declined",

        // Only ever a top-level $eq/$ne operand (a constructed-tuple comparison), so it is in the ordinary Agg
        // dispatch. No query-dialect form (array-vs-array $eq exists only in $expr), so the negator declines at its
        // IsQueryDialectRenderable gate. QL.Render wraps a bare tuple in $expr — harmless, never reached.
        ["MongoTupleExpression|Agg.CanRender"] = "true",
        ["MongoTupleExpression|Agg.Render"] = "rendered",
        ["MongoTupleExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoTupleExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoTupleExpression|Negator.TryNegate"] = "false",
        ["MongoTupleExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoTupleExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoTupleExpression|QL.Render"] = "rendered"
    };
}
