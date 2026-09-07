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
/// Seven separate hand-rolled <c>switch</c> statements dispatch over the node hierarchy: the two dialect
/// renderers, their two matching classifiers, the negator, the field-prefix rewriter, and the
/// serialization-safety predicate. C# gives no exhaustiveness check across them, and the consequences of
/// forgetting one arm differ per dispatcher and are all bad: the query-dialect renderer falls open to
/// <c>$expr</c>, the aggregation renderer and the prefix rewriter <b>throw</b> (turning what would have been a
/// graceful decline into a hard failure in every <see cref="MongoQueryMode"/>), and the serialization
/// predicate falls open to <see langword="true"/> (presuming a new node is safe to truthiness-test).
/// </para>
/// <para>
/// This class is the mechanical substitute for that missing check. It discovers every concrete
/// <see cref="MongoExpression"/> subtype by <b>reflection</b>, so a newly added node type cannot go unnoticed,
/// and characterizes each node's observed outcome in all seven dispatchers against a baked-in matrix. Adding a
/// node type reddens <see cref="Every_node_type_has_a_representative_sample"/> until a sample is supplied, then
/// reddens <see cref="Dispatcher_coverage_matrix_is_unchanged"/> until its row is declared — which is what
/// forces an author to visit all seven dispatchers rather than only the one their feature needed.
/// </para>
/// <para>
/// The matrix is a <b>characterization</b> of current behaviour, not an assertion that current behaviour is
/// correct: some cells record a throw or a fall-open that is a latent bug. Those are called out in
/// <see cref="ExpectedMatrix"/>'s own comments. Changing a dispatcher deliberately means updating the cell and
/// saying why in the commit; the point is that no cell can change silently.
/// </para>
/// <para>
/// The two invariant tests below (<see cref="Every_node_the_query_dialect_classifier_admits_renders_in_that_dialect"/>
/// and <see cref="Every_node_CanRender_admits_actually_renders_in_the_aggregation_dialect"/>) assert the real
/// contract the area's durable invariants describe — that a classifier never admits a node its own renderer
/// would throw on — rather than merely pinning the status quo.
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
    /// Resolves a property of the owned collection-element type. When <paramref name="valueConverted"/> is
    /// <see langword="true"/> every scalar carries a <c>HasConversion&lt;string&gt;()</c> value converter, which
    /// is what makes the <see cref="SerializationSafetyConverted"/> column discriminating: a node whose arm
    /// recurses into its children reports <c>false</c>, while a node the predicate has no arm for falls open to
    /// <c>true</c>. Without a converted variant that whole column reads <c>true</c> everywhere and could never
    /// detect a missing arm.
    /// </summary>
    private static IProperty PostProperty(string propertyName, bool valueConverted)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(
            b => b.Posts,
            ob =>
            {
                if (!valueConverted)
                    return;

                // An explicit same-type ValueConverter, NOT HasConversion<string>() — the latter is a no-op on a
                // string property and EF drops it, which would leave every string-rooted node in this sample set
                // reporting "default serialization" and silently blunt the column.
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
    /// One representative instance per concrete node type. Deliberately the SIMPLEST legal instance of each —
    /// the harness characterizes dispatcher <em>arm coverage</em>, not the full value space of any one node, so
    /// a richer instance would only make failures harder to read.
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
            rankConstant,
            new MongoParameterExpression("p0", rank),
            new MongoBinaryExpression(MongoBinaryOperator.Equal, rankField, rankConstant),
            new MongoUnaryExpression(MongoUnaryOperator.Not, flagField),
            new MongoSizeExpression("Tags", typeof(int)),
            new MongoFilteredSizeExpression("Tags", flagField, typeof(int)),
            new MongoInExpression(rankField, new MongoConstantExpression(new[] { 1, 2 }, rank), negated: false),
            new MongoComputedInExpression(
                headingField, new MongoConstantExpression(new[] { "a", "b" }, heading), negated: false),
            new MongoArrayContainsExpression(
                new MongoFieldExpression(tags, "Tags"), new MongoConstantExpression("a", null), negated: false),
            new MongoRegexExpression(
                headingField, MongoRegexKind.StartsWith, new MongoConstantExpression("a", heading), negated: false),
            new MongoElemMatchExpression("Posts", flagField, negated: false),
            new MongoQuantifierExpression(
                new MongoElementRefExpression("Posts", typeof(List<Post>)),
                flagField,
                MongoExpressionTranslator.MongoQuantifierKind.Any),
            new MongoConvertExpression(rankField, typeof(long)),
            new MongoConditionalExpression(flagField, rankField, rankConstant),
            new MongoDatePartExpression(new MongoFieldExpression(when, "When"), MongoDatePart.Year),
            new MongoDateTimeOffsetLocalExpression(new MongoFieldExpression(stamp, "Stamp")),
            new MongoConcatExpression([headingField, new MongoConstantExpression("x", heading)]),
            new MongoDocumentConstructionExpression(
                Expression.New(typeof(object)), [(nameof(Post.Rank), rankField)])
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
    /// The same predicate as <see cref="SerializationSafety"/>, probed against the value-converted sample set.
    /// This is the column that actually detects a missing arm — see <see cref="PostProperty"/>.
    /// </summary>
    private const string SerializationSafetyConverted = "AllFieldsDefaultSerialized(converted)";

    private static readonly string[] Dispatchers =
    [
        QueryRenderer, QueryClassifier, AggRenderer, AggClassifier, Negator, PrefixRewriter, SerializationSafety,
        SerializationSafetyConverted
    ];

    /// <summary>
    /// Runs one node through one dispatcher and reduces the outcome to a short, stable token: <c>rendered</c>,
    /// <c>true</c>/<c>false</c>, <c>declined</c> (the provider's own not-supported exception, i.e. a deliberate
    /// arm or a deliberate default), or <c>threw:&lt;ExceptionType&gt;</c> (anything else — usually the sign of a
    /// node reaching an arm that assumes a shape it doesn't have).
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

        // Guard the other direction too: a sample for a type that no longer exists means the matrix is carrying
        // a stale row that can never fail.
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

    // ------------------------------------------------------------------
    // The baked-in matrix.

    /// <summary>
    /// Observed outcome per (node type, dispatcher). See the class remarks: this characterizes current
    /// behaviour so it cannot change silently; it does not claim every cell is desirable.
    /// </summary>
    /// <remarks>
    /// <para>Four cross-cutting facts this matrix makes visible, all of them pre-existing:</para>
    /// <list type="number">
    /// <item>
    /// <c>QL.Render</c> is <c>rendered</c> for <b>every</b> node type, including the 12 the query-dialect
    /// classifier refuses. That is the documented fall-open to <c>$expr</c>: the query-dialect renderer never
    /// declines, so <c>IsQueryDialectRenderable</c> is the ONLY thing standing between a node with no
    /// query-dialect form and an illegal <c>$expr</c> in an <c>$elemMatch</c> position.
    /// </item>
    /// <item>
    /// <c>PrefixRewriter.Rewrite</c> is <c>declined</c> for <c>MongoQuantifierExpression</c> and
    /// <c>MongoDocumentConstructionExpression</c> — no prefixing rule exists for either yet, so the caller falls
    /// back to driver-LINQ. That decline is now a <see langword="false"/> return rather than the throw it used to
    /// be (which converted a would-be graceful fallback into a hard failure in every
    /// <see cref="MongoQueryMode"/>).
    /// </item>
    /// <item>
    /// <c>MongoDocumentConstructionExpression</c> has <c>Agg.Render = rendered</c> but
    /// <c>Agg.CanRender = false</c> — the renderer has an arm the classifier does not. Not unsafe (it only
    /// declines a shape that would in fact render), but it is a missing <c>CanRender</c> arm.
    /// </item>
    /// <item>
    /// In the <c>(converted)</c> column, exactly the nine arm-bearing node kinds report <c>false</c>. The other
    /// twelve fall open to <c>true</c>. Three of those are deliberate and documented in
    /// <c>AllFieldsDefaultSerialized</c>'s own remarks (<c>In</c>, <c>Size</c>, <c>ArrayContains</c>) and three
    /// carry no reachable property (<c>Constant</c>, <c>Parameter</c>, <c>ElementRef</c>). The remaining six —
    /// <c>ComputedIn</c>, <c>FilteredSize</c>, <c>ElemMatch</c>, <c>Quantifier</c>, <c>Regex</c>,
    /// <c>DocumentConstruction</c> — are undocumented omissions in the one dispatcher that fails OPEN.
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

        // Agg.Render has an arm this node reaches; Agg.CanRender does not — see remark 3 above.
        ["MongoDocumentConstructionExpression|Agg.CanRender"] = "false",
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

        // Query-dialect-only by design (see the node's own remarks): it is produced only for the exact
        // `ti.Inner == null`/`!= null` top-level Where shape, never nested under Not/a quantifier/$elemMatch,
        // so none of the other six dispatchers need an arm for it — each fails closed/open exactly the way an
        // unrecognized node already does, safely, because this recognizer never hands them one.
        ["MongoLookupNullCheckExpression|Agg.CanRender"] = "false",
        ["MongoLookupNullCheckExpression|Agg.Render"] = "declined",
        ["MongoLookupNullCheckExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoLookupNullCheckExpression|AllFieldsDefaultSerialized(converted)"] = "true",
        ["MongoLookupNullCheckExpression|Negator.TryNegate"] = "false",
        ["MongoLookupNullCheckExpression|PrefixRewriter.Rewrite"] = "declined",
        ["MongoLookupNullCheckExpression|QL.IsQueryDialectRenderable"] = "false",
        ["MongoLookupNullCheckExpression|QL.Render"] = "rendered",

        ["MongoOuterFieldExpression|Agg.CanRender"] = "true",
        ["MongoOuterFieldExpression|Agg.Render"] = "rendered",
        ["MongoOuterFieldExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoOuterFieldExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoOuterFieldExpression|Negator.TryNegate"] = "false",
        // Pass-through, NOT prefixed: root-anchored by definition. See remark 2.
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

        ["MongoRegexExpression|Agg.CanRender"] = "false",
        ["MongoRegexExpression|Agg.Render"] = "declined",
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

        ["MongoUnaryExpression|Agg.CanRender"] = "true",
        ["MongoUnaryExpression|Agg.Render"] = "rendered",
        ["MongoUnaryExpression|AllFieldsDefaultSerialized"] = "true",
        ["MongoUnaryExpression|AllFieldsDefaultSerialized(converted)"] = "false",
        ["MongoUnaryExpression|Negator.TryNegate"] = "true",
        ["MongoUnaryExpression|PrefixRewriter.Rewrite"] = "rendered",
        ["MongoUnaryExpression|QL.IsQueryDialectRenderable"] = "true",
        ["MongoUnaryExpression|QL.Render"] = "rendered"
    };
}
