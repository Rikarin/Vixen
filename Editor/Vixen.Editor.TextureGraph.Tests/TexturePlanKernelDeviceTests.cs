// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A kernel a graph wrote, dispatched — the half of #729 no value can assert.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test whose <em>absence</em> was the bug.</b> Doc 48 § D6's Pixel
///         Processor had a full compile-time suite — a generated <c>.rvn</c>, the real Raven front
///         end, diagnostics mapped back to the author's line — and every one of those passed against
///         a plan that <c>TexturePlanEvaluator</c> could not run, because the op named a shader it
///         resolved through this assembly's embedded resources. A graph that looks complete and
///         throws in a background task is worse than a missing feature, and only a bake says which
///         one you have.
///     </para>
///     <para>
///         <b>The oracle is a closed form and not an eyeball.</b> The expression is <c>uv.x</c>, so
///         the red channel of the baked image must rise with x and be flat down every column — a
///         kernel that had silently fallen back to something else could match neither.
///     </para>
///     <para>
///         ⚠ Names its adapter and skips loudly without one, through
///         <see cref="TextureKernelHarness.Open" />: without a real device a headless run falls back
///         to the Null device, and what this would then be comparing is two black images.
///     </para>
/// </remarks>
public class TexturePlanKernelDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>A plan carrying its own kernel bakes the picture that kernel describes.</summary>
    [Fact]
    public void A_plan_carrying_its_own_kernel_bakes_what_that_kernel_says() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var target = graph.Add("Output/Output");

        processor.SetText("Expression", "float4(uv.x, 0f, 0f, 1f)");
        graph.Connect(new(processor.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compiler = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;

        // The kernel is the graph's own and not one this assembly ships, which is what makes the
        // evaluation below a statement about #729 rather than about the Pixel Processor.
        Assert.Equal(plan.Ops[^1].Kernel, Assert.Single(plan.Kernels).Key);
        Assert.DoesNotContain(plan.Ops[^1].Kernel, TextureKernels.Names);
        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var picture = bake.Read(plan.Outputs[0]);

        output.WriteLine($"{adapter}: {picture.Width}×{picture.Height} from '{plan.Ops[^1].Kernel}'");

        // uv.x, so: the first column is dark, the last is bright, every column is flat, and the
        // whole row rises. A fallback to any other kernel fails at least one of the four.
        Assert.True(TextureKernelHarness.At(picture, 0, 0, 0) < 8);
        Assert.True(TextureKernelHarness.At(picture, Side - 1, 0, 0) > 247);

        for (var y = 1; y < Side; y++) {
            Assert.Equal(TextureKernelHarness.At(picture, Side / 2, 0, 0), TextureKernelHarness.At(picture, Side / 2, y, 0));
        }

        for (var x = 1; x < Side; x++) {
            Assert.True(TextureKernelHarness.At(picture, x, 0, 0) > TextureKernelHarness.At(picture, x - 1, 0, 0));
        }
    }
}
