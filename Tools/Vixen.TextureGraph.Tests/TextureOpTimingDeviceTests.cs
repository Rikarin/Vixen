// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What a host needs to say what each node cost (Patina #182's timing badges): the evaluator's per-op times and
///     the compiler's owner of each op.
/// </summary>
public class TextureOpTimingDeviceTests(ITestOutputHelper output) {
    const int Side = 256;

    /// <summary>A noise into a compound (Make It Tile) into a blur, and an Output of each end.</summary>
    static (TextureGraphCompiler Compiler, TexturePlan Plan, GraphNode Noise, GraphNode Tile, GraphNode Blur) Compiled() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var library = TextureCompoundLibrary.Publish(registry, null, out _);
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var tile = graph.Add("Utility/Make It Tile");
        var blur = graph.Add("Filters/Blur");
        var sink = graph.Add("Output/Output");

        sink.SetText("Usage", "baseColor");
        graph.Connect(new(noise.Id, "Out"), new(tile.Id, "Input"));
        graph.Connect(new(tile.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(sink.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side, BaseHeight = Side, SubGraphSource = library, PreviewEveryNode = true
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics.Where(static problem => problem.Severity == NodeSeverity.Error));

        return (compiler, compilation.Value, noise, tile, blur);
    }

    [Fact]
    public void Every_op_names_the_node_of_the_document_it_was_emitted_for() {
        var (compiler, plan, noise, tile, blur) = Compiled();

        Assert.Equal(plan.Ops.Length, compiler.OpNodes.Length);

        // A compound's inner ops answer to the compound node, as its picture does, and nothing names a node
        // the document does not hold.
        HashSet<NodeId> document = [noise.Id, tile.Id, blur.Id, NodeId.None];

        Assert.All(compiler.OpNodes, owner => Assert.Contains(owner, document));
        Assert.True(compiler.OpNodes.Count(owner => owner == tile.Id) > 1);

        // The op that writes a node's picture is that node's.
        foreach (var written in compiler.NodeImages) {
            var writer = plan.Ops.Select(static (op, index) => (op, index))
                .Last(pair => pair.op.Output == written.Image);

            Assert.Equal(written.Node, compiler.OpNodes[writer.index]);
        }
    }

    [Fact]
    public void A_timed_bake_has_a_time_for_every_op_that_ran_and_an_untimed_one_none() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (_, plan, _, _, _) = Compiled();

        using var evaluator = new TexturePlanEvaluator(device);

        using (var untimed = evaluator.Evaluate(plan)) {
            Assert.Empty(untimed.OpMilliseconds);
        }

        Assert.SkipUnless(device.Features.CanTimeFrames, "This device cannot write timestamps.");

        evaluator.TimeOps = true;

        using var timed = evaluator.Evaluate(plan);

        Assert.Equal(Enumerable.Range(0, plan.Ops.Length), timed.OpMilliseconds.Keys.Order());
        Assert.All(timed.OpMilliseconds.Values, static time => Assert.InRange(time, 0, 10_000));
        Assert.True(timed.OpMilliseconds.Values.Sum() > 0);

        output.WriteLine($"{plan.Ops.Length} ops in {timed.OpMilliseconds.Values.Sum():F3} ms");

        // ⚠ Incrementally, only what ran is timed: a second evaluation of the same plan runs nothing.
        using var first = evaluator.EvaluateIncremental(plan, new Dictionary<int, TextureExternal>());
        using var again = evaluator.EvaluateIncremental(plan, new Dictionary<int, TextureExternal>());

        Assert.Equal(first.Ran.Order(), first.OpMilliseconds.Keys.Order());
        Assert.Empty(again.Ran);
        Assert.Empty(again.OpMilliseconds);
    }
}
