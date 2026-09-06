// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The one thing about the compiler that a value cannot prove: that the plan it emits is the plan
///     the evaluator already draws correctly.
/// </summary>
/// <remarks>
///     <para>
///         <b>A differential, and it is the only shape that answers the question.</b>
///         <c>TextureGraphCompilerTests</c> asserts what the compiler produces against what a reader
///         expects it to produce — which is a claim about two descriptions agreeing, and both could be
///         wrong together. What is compared here is a <em>picture</em>: the same three operations,
///         once through a graph and once as a plan written out by hand, evaluated on the same adapter
///         in the same run, required to come back byte for byte identical.
///     </para>
///     <para>
///         ⚠ <b>And the picture is checked for being a picture</b>, because two flat images are also
///         identical. A compiler that emitted a blur of radius zero, or a levels curve that maps
///         everything to black, would pass an equality against a hand-built plan that made the same
///         mistake only if the mistake were in both — but a compiler that emitted <em>nothing</em>
///         would produce an unwritten image on both sides of a comparison that never ran. The
///         distinct-value count is what makes the equality mean something.
///     </para>
///     <para>
///         ⚠ Every test here names its adapter and skips loudly without one, through
///         <see cref="TextureKernelHarness.Open" /> — the one instrument in this project, not a fourth
///         copy of it. Without a real device a headless run falls back to the Null device and prints
///         identical healthy counters, and a comparison of two black images is what that would prove.
///     </para>
/// </remarks>
public class TextureGraphDeviceTests(ITestOutputHelper output) {
    const int Side = 128;
    const float Radius = 6f;

    /// <summary>What a compiled graph draws is what the same plan written by hand draws.</summary>
    [Fact]
    public void A_compiled_graph_draws_what_the_hand_built_plan_draws() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var compiled = Compiled();
        var written = HandBuilt(compiled);

        // ⚠ The plans are compared as values first, because a picture that matches tells you nothing
        // about *why*. If this line fails, the two are different programs and the pixels below would
        // only say whether the difference happened to show.
        Assert.Equal(Describe(written), Describe(compiled));

        var fromGraph = Draw(evaluator, compiled);
        var fromHand = Draw(evaluator, written);

        output.WriteLine($"{adapter}: {Distinct(fromGraph)} distinct values over {Side}×{Side}");

        // A blurred noise field through a levels curve is not a flat fill on any adapter. Sixteen is
        // far below what it actually produces and far above what a broken chain would.
        Assert.True(
            Distinct(fromGraph) > 16,
            $"{adapter}: the compiled graph drew {Distinct(fromGraph)} distinct values, which is not a "
            + "picture — so an equality against the hand-built plan would prove nothing."
        );

        Assert.Equal(fromHand.Pixels, fromGraph.Pixels);
    }

    /// <summary>The graph: noise, blurred, through a levels curve, kept as a height map.</summary>
    static TexturePlan Compiled() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new() { Name = "Differential" };
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        noise.SetValue("Scale", 6f);
        noise.SetValue("Octaves", 3f);
        blur.SetValue("Radius", Radius);
        levels.SetValue("Input Black", 0.2f);
        levels.SetValue("Input White", 0.8f);
        levels.SetValue("Gamma", 0.7f);
        output.SetText("Usage", "height");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 41823 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal("height", Assert.Single(compiler.Outputs).Usage);

        return compilation.Value;
    }

    /// <summary>The same three operations, written out the way every other suite here writes one.</summary>
    /// <param name="compiled">The compiled plan, for the one field a hand-written plan cannot invent.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The op <em>names</em> are taken from the compiled plan, and that is a real
    ///         narrowing of what this test proves</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>.
    ///         <c>TexturePlan.SeedFor</c> used to mix the op's <em>index</em>, which a hand-written
    ///         plan reproduces by putting its ops in the same order; it now mixes
    ///         <c>TextureOp.Identity</c>, which the compiler derives from the <c>NodeId</c> that
    ///         emitted the op. There is no node here, so there is no identity to write down — and a
    ///         seeded op whose name differed would fail the pixel comparison for a reason that has
    ///         nothing to do with the compiler being wrong.
    ///     </para>
    ///     <para>
    ///         <b>So the seed half of this differential is circular now and the rest is not.</b> The
    ///         identities are copied positionally, so an op count or an op order that disagreed still
    ///         fails — loudly, on the <c>Describe</c> comparison, which lists the identity. What is no
    ///         longer proved here is that the compiler names its ops <em>well</em>;
    ///         <c>TexturePlanSeedTests</c> is where that lives, and it is the file that says what a
    ///         name has to survive.
    ///     </para>
    /// </remarks>
    static TexturePlan HandBuilt(TexturePlan compiled) {
        // Before the indices below, so a compiler that emitted a different number of ops says that
        // rather than throwing out of a name lookup.
        Assert.Equal(4, compiled.Ops.Length);

        return new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            Images = [
                new(TextureFormat.R16Float),
                new(TextureFormat.R16Float),
                new(TextureFormat.R16Float),
                new(TextureFormat.R16Float)
            ],
            Ops = [
                new() {
                    Kernel = "Noise",
                    Identity = compiled.Ops[0].Identity,
                    Output = 0,
                    Parameters = [
                        new("basis", 0f),
                        new("scale", 6f),
                        new("octaves", 3f),
                        new("lacunarity", 2f),
                        new("gain", 0.5f),
                        new("tiling", 0f)
                    ]
                },
                new() {
                    Kernel = "Blur",
                    Identity = compiled.Ops[1].Identity,
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("radius", Radius, TextureParameterUnit.TexelsAtBase),
                        new("stepX", 1f),
                        new("stepY", 0f)
                    ]
                },
                new() {
                    Kernel = "Blur",
                    Identity = compiled.Ops[2].Identity,
                    Output = 2,
                    Inputs = [1],
                    Parameters = [
                        new("radius", Radius, TextureParameterUnit.TexelsAtBase),
                        new("stepX", 0f),
                        new("stepY", 1f)
                    ]
                },
                new() {
                    Kernel = "Levels",
                    Identity = compiled.Ops[3].Identity,
                    Output = 3,
                    Inputs = [2],
                    Parameters = [
                        new("inputBlack", 0.2f),
                        new("inputWhite", 0.8f),
                        new("gamma", 0.7f),
                        new("outputBlack", 0f),
                        new("outputWhite", 1f),
                        new("dither", 1f)
                    ]
                }
            ],
            Outputs = [3]
        };
    }

    static Bitmap Draw(TexturePlanEvaluator evaluator, TexturePlan plan) {
        Assert.Empty(plan.Validate());

        using var bake = evaluator.Evaluate(plan);

        return bake.Read(plan.Outputs[0]);
    }

    /// <summary>How many different values the red channel holds, so "this is a picture" is a claim.</summary>
    static int Distinct(Bitmap picture) {
        HashSet<byte> values = [];

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                values.Add(TextureKernelHarness.At(picture, x, y, 0));
            }
        }

        return values.Count;
    }

    /// <summary>A plan as text, so two of them compare as one value with a readable difference.</summary>
    static string Describe(TexturePlan plan) =>
        string.Join(
            "\n",
            plan.Images.Select((image, index) => $"image {index}: {image.Format} {image.LevelOffset} {image.External}")
                .Concat(
                    plan.Ops.Select((op, index) =>
                        $"op {index}: {op.Kernel} -> {op.Output} <- [{string.Join(", ", op.Inputs)}] "
                        + $"{{{string.Join(", ", op.Parameters.Select(parameter => $"{parameter.Name}={parameter.Value}:{parameter.Unit}"))}}} "
                        + $"named {op.Identity?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}")
                )
                .Append($"outputs: {string.Join(", ", plan.Outputs)}")
        );
}
