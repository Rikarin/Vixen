// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What one slider drag costs on a forty-node graph, before and after.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A measurement in the costume of a test, and the assertion is not the number.</b> A
///         budget on a clock is this repository's largest source of flakes, so what is asserted is the
///         dispatch count, at any size. The times are printed; with <c>VIXEN_MEASURE=1</c> this runs at
///         2K, which is the table in the README, and without it at 256 so that the suite exercises the
///         harness and the printing cannot rot.
///     </para>
///     <para>
///         <b>Before</b> is what a slider cost until this change: compile the graph, evaluate the whole
///         plan. <b>After</b> is <c>Refresh</c> and <c>EvaluateIncremental</c>. Both include disposing
///         the bake, and both stop with the device idle, so neither is hiding work in a queue. Neither
///         reads pixels back; that is one more line in the table, because it is the same on both sides.
///     </para>
/// </remarks>
public class TextureSliderMeasurementTests(ITestOutputHelper output) {
    const int Drag = 24;

    static (int Side, bool Full) Scale() =>
        Environment.GetEnvironmentVariable("VIXEN_MEASURE") is "1" ? (2048, true) : (256, false);

    /// <summary>Forty nodes: four sources, a height chain, a colour chain, and the maps they end in.</summary>
    /// <returns>The graph, and the node a slider sits on at the end, in the middle and at the start.</returns>
    static (NodeGraphModel Graph, GraphNode Late, GraphNode Middle, GraphNode Early) Forty() {
        NodeGraphModel graph = new();

        GraphNode After(string type, GraphNode from, string input = "Input") {
            var node = graph.Add(type);

            graph.Connect(new(from.Id, "Out"), new(node.Id, input));

            return node;
        }

        GraphNode Mix(GraphNode background, GraphNode foreground) {
            var node = graph.Add("Colour/Blend");

            graph.Connect(new(background.Id, "Out"), new(node.Id, "Background"));
            graph.Connect(new(foreground.Id, "Out"), new(node.Id, "Foreground"));

            return node;
        }

        void Output(GraphNode from, string usage) {
            var node = After("Output/Output", from);

            node.SetText("Usage", usage);
        }

        var coarse = graph.Add("Source/Noise");
        var fine = graph.Add("Source/Noise");
        var cells = graph.Add("Source/Noise");
        var checker = graph.Add("Source/Checker");

        coarse.SetValue("Scale", 4f);
        coarse.SetValue("Octaves", 5f);
        fine.SetValue("Scale", 32f);
        fine.SetValue("Octaves", 4f);
        cells.SetValue("Scale", 12f);
        cells.SetValue("Octaves", 3f);

        var early = After("Filters/Blur", coarse);

        early.SetValue("Radius", 4f);

        var height = early;

        for (var layer = 0; layer < 5; layer++) {
            var levels = After("Colour/Levels", height);

            levels.SetValue("Gamma", 0.8f + (0.1f * layer));

            var blurred = After("Filters/Blur", layer % 2 == 0 ? fine : cells);

            blurred.SetValue("Radius", 2f + layer);
            height = Mix(levels, blurred);
        }

        var sharpened = After("Filters/Sharpen", height);
        var middle = After("Colour/Levels", sharpened);
        var normal = After("Surface/Height to Normal", middle, "Height");
        var occlusion = After("Surface/Ambient Occlusion", middle, "Height");
        var curvature = After("Surface/Curvature", normal, "Normal");
        var mask = After("Colour/Levels", curvature);
        var worn = Mix(checker, mask);
        var soft = After("Filters/Blur", worn);

        soft.SetValue("Radius", 3f);

        var hue = After("Colour/HSL", soft);
        var grey = After("Colour/Grayscale", hue);
        var inverted = After("Colour/Invert", grey);
        var rough = After("Colour/Levels", inverted);
        var dirt = Mix(rough, occlusion);
        var polish = After("Filters/Sharpen", dirt);
        var late = After("Colour/Levels", polish);
        var final = After("Colour/Invert", late);

        Output(normal, "normal");
        Output(hue, "baseColor");
        Output(final, "roughness");
        Output(middle, "height");

        Assert.Equal(40, graph.Nodes.Count);

        return (graph, late, middle, early);
    }

    static double Median(List<double> samples) {
        samples.Sort();

        return samples[samples.Count / 2];
    }

    [Fact]
    public void One_slider_on_a_forty_node_graph() {
        var (side, full) = Scale();

        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var (graph, late, middle, early) = Forty();
        var sliders = new (string Name, GraphNode Node, string Port)[] {
            ("late", late, "Gamma"),
            ("middle", middle, "Gamma"),
            ("early", early, "Radius")
        };

        TextureGraphCompiler compiler = new(registry) { BaseWidth = side, BaseHeight = side, Seed = 41823 };

        var plan = compiler.Compile(graph).Value;

        output.WriteLine(
            $"{graph.Nodes.Count} nodes, {plan.Ops.Length} ops, {side}×{side}, {TextureKernelHarness.Adapter(device)}, "
            + $"validation {device.ValidationEnabled}"
        );

        Console.WriteLine($"{plan.Ops.Length} ops, validation {device.ValidationEnabled}");

        // The first of each is a cold start for kernels and pool on both sides, and is not a drag.
        using (evaluator.Evaluate(plan, new Dictionary<int, TextureExternal>())) { }
        using (evaluator.EvaluateIncremental(plan, new Dictionary<int, TextureExternal>())) { }

        foreach (var (name, node, port) in sliders) {
            var rest = node.Values.TryGetValue(port, out var lanes) ? lanes[0] : 1f;

            float Position(int step) => rest * (1f + (0.25f * MathF.Sin(step * 0.7f)));

            List<double> before = [];
            List<double> after = [];
            List<int> ran = [];

            for (var step = 1; step <= Drag; step++) {
                node.SetValue(port, Position(step));

                var clock = Stopwatch.StartNew();
                var compilation = compiler.Compile(graph);

                using (evaluator.Evaluate(compilation.Value, new Dictionary<int, TextureExternal>())) { }

                before.Add(clock.Elapsed.TotalMilliseconds);
            }

            // ⚠ Armed once: `Refresh` answers from the last compile, and the drag below is what a
            // real one is. Offset, so that no step lands on a number the loop above already cached
            // — those were whole-plan evaluations and cached nothing, but a drag revisiting its own
            // positions would be a hit this table should not be quietly counting.
            compiler.Compile(graph);

            for (var step = 1; step <= Drag; step++) {
                node.SetValue(port, Position(step) * 1.01f);

                var clock = Stopwatch.StartNew();
                var refreshed = compiler.Refresh(graph);

                Assert.NotNull(refreshed);

                using (var bake = evaluator.EvaluateIncremental(refreshed, new Dictionary<int, TextureExternal>())) {
                    ran.Add(bake.Ran.Length);
                }

                after.Add(clock.Elapsed.TotalMilliseconds);
            }

            node.SetValue(port, rest);

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-7} before {Median(before),8:F1} ms   after {Median(after),8:F1} ms   ops run {Median(ran.ConvertAll(count => (double)count)),3} of {plan.Ops.Length}"
            );

            output.WriteLine(line);
            Console.WriteLine(line);

            // The claim, as work rather than time. Every slider here has something upstream or beside
            // it, so "ran fewer than the plan" cannot be true of a path that re-ran everything.
            Assert.True(ran.Max() < plan.Ops.Length, $"{name} ran {ran.Max()} of {plan.Ops.Length} ops.");
        }

        // What a drag leaves behind has to have stayed under the budget, or the numbers are lying.
        Assert.True(evaluator.Results.Bytes <= evaluator.Results.Budget);

        // One read at this size, which is the same either way and is what a viewport adds.
        var final = plan.Outputs[^1];

        using var read = evaluator.EvaluateIncremental(compiler.Refresh(graph)!, new Dictionary<int, TextureExternal>());

        var timer = Stopwatch.StartNew();

        read.ReadTexels(final);

        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"one read-back {timer.Elapsed.TotalMilliseconds:F1} ms, {evaluator.Results.Count} results, {evaluator.Results.Bytes >> 20} MiB held, {evaluator.Results.Evictions} evicted, full size {full}"
        );

        output.WriteLine(note);
        Console.WriteLine(note);
    }
}
