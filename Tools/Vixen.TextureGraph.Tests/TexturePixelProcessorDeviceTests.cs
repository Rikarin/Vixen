// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What a Pixel Processor's expression reads and what its answer is kept in, in texels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its output used to be whatever arrived</b>, so over a grey noise an expression that
///         answered with a colour had its green dropped into a one-channel image with no message.
///     </para>
///     <para>
///         ⚠ <b>And what <c>a</c> read depended on what was wired to <c>b</c>.</b> A grey image read
///         through its own view is <c>(r, 0, 0, 1)</c>, and the same image splatted for a node that
///         resolved to colour is <c>(r, r, r, 1)</c>, so one expression drew two pictures. An input is
///         now always a colour, because the expression names a <c>float4</c>.
///     </para>
/// </remarks>
public class TexturePixelProcessorDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static (TexturePlan Plan, TextureGraphCompiler Compiler) Compile(NodeGraphModel graph) {
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = Side, BaseHeight = Side, Seed = 3 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return (compilation.Value, compiler);
    }

    /// <summary>A colour computed from a grey input arrives as a colour.</summary>
    [Fact]
    public void A_grey_input_can_answer_with_a_colour() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var processor = graph.Add("Filters/Pixel Processor");
        var sink = graph.Add("Output/Output");

        processor.SetText("Expression", "float4(a.x, 1f - a.x, 0f, 1f)");
        graph.Connect(new(noise.Id, "Out"), new(processor.Id, "A"));
        graph.Connect(new(processor.Id, "Out"), new(sink.Id, "Input"));

        var (plan, _) = Compile(graph);

        using var bake = evaluator.Evaluate(plan);

        var picture = bake.Read(plan.Outputs[0]);
        var worst = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var sum = TextureKernelHarness.At(picture, x, y, 0) + TextureKernelHarness.At(picture, x, y, 1);

                worst = Math.Max(worst, Math.Abs(sum - 255));
            }
        }

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: red and green miss a sum of 255 by {worst}");

        Assert.Equal(TextureFormat.Rgba16Float, plan.Images[plan.Outputs[0]].Format);
        Assert.InRange(worst, 0, 2);
    }

    /// <summary>A grey is the setting's other answer, and the expression's red is what it keeps.</summary>
    [Fact]
    public void Asking_for_grey_keeps_one_channel() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var processor = graph.Add("Filters/Pixel Processor");
        var sink = graph.Add("Output/Output");

        processor.SetText("Expression", "a * 2f");
        processor.SetText("Format", "Grey");
        graph.Connect(new(noise.Id, "Out"), new(processor.Id, "A"));
        graph.Connect(new(processor.Id, "Out"), new(sink.Id, "Input"));

        var (plan, _) = Compile(graph);

        Assert.Equal(TextureFormat.R16Float, plan.Images[plan.Outputs[0]].Format);
    }

    /// <summary>An output format that is not one is said, against the setting.</summary>
    [Fact]
    public void A_format_it_does_not_have_is_refused() {
        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var sink = graph.Add("Output/Output");

        processor.SetText("Format", "Bone");
        graph.Connect(new(processor.Id, "Out"), new(sink.Id, "Input"));

        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = Side, BaseHeight = Side };

        Assert.Contains(compiler.Compile(graph).Diagnostics, diagnostic => diagnostic.Message.Contains("Bone"));
    }

    /// <summary>One expression over one grey image draws one picture, whatever else is wired.</summary>
    /// <remarks>
    ///     ⚠ <b>Both are <c>a</c> and they differ only in that one has a colour at <c>b</c> it never
    ///     reads.</b> The green lane is what told them apart: zero through the grey view, and red
    ///     through the splat.
    /// </remarks>
    [Fact]
    public void What_a_reads_does_not_depend_on_what_is_wired_to_b() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var colour = graph.Add("Source/Uniform");
        var alone = graph.Add("Filters/Pixel Processor");
        var beside = graph.Add("Filters/Pixel Processor");
        var first = graph.Add("Output/Output");
        var second = graph.Add("Output/Output");

        alone.SetText("Expression", "float4(a.y, a.y, a.y, 1f)");
        beside.SetText("Expression", "float4(a.y, a.y, a.y, 1f)");
        first.SetText("Usage", "baseColor");
        second.SetText("Usage", "height");

        graph.Connect(new(noise.Id, "Out"), new(alone.Id, "A"));
        graph.Connect(new(noise.Id, "Out"), new(beside.Id, "A"));
        graph.Connect(new(colour.Id, "Out"), new(beside.Id, "B"));
        graph.Connect(new(alone.Id, "Out"), new(first.Id, "Input"));
        graph.Connect(new(beside.Id, "Out"), new(second.Id, "Input"));

        var (plan, _) = Compile(graph);

        using var bake = evaluator.Evaluate(plan);

        var solo = bake.Read(plan.Outputs[0]);
        var paired = bake.Read(plan.Outputs[1]);

        Assert.Equal(solo.Pixels, paired.Pixels);
    }
}
