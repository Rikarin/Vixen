// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.9's library, evaluated: every shipped compound draws a picture, and the one whose
///     whole purpose is a property has that property measured.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>TextureCompoundLibraryTests</c> stops one step short of this and says so.</b> That
///         file proves the files parse, publish, name only ports that exist and produce a plan — and
///         a plan is a value. Nothing there would notice a compound whose arrangement of real nodes
///         computes a flat fill, which is what most of the ways of getting one of these wrong
///         produce: a mask multiplied by its own inverse, a levels curve whose two handles the
///         expressions folded onto the same number, a blend reading an unwritten port. So the claim
///         here is about texels.
///     </para>
///     <para>
///         ⚠ <b>The instrument is the roll call, not the loop.</b> A <c>foreach</c> over
///         <see cref="TextureCompoundLibrary.Shipped" /> that found nothing would satisfy every
///         assertion inside it — the shape this repository has shipped four times — so
///         <see cref="Every_shipped_compound_bakes_to_a_picture" /> counts what it actually baked and
///         compares that with what ships.
///     </para>
///     <para>
///         ⚠ Names its adapter and skips loudly without one, through
///         <see cref="TextureKernelHarness.Open" />. Without a real device a headless run falls back
///         to the Null device and every distinct-value count below would be one.
///     </para>
/// </remarks>
public class TextureCompoundBakeDeviceTests(ITestOutputHelper output) {
    /// <summary>How wide and tall every bake here is, in texels.</summary>
    const int Side = 64;

    /// <summary>Every shipped compound, inside a graph, evaluated, and none of them is a flat fill.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A low bar, and it is the right one for a roll call over content.</b> Each of these
    ///         compounds means something different, so there is no shared oracle stronger than "it is
    ///         a picture"; what the bar rules out is precisely the failure a library of hand-written
    ///         files has — a compound that compiles, bakes and produces one value everywhere. A
    ///         compound with a real oracle gets its own case, as
    ///         <see cref="Make_It_Tile_makes_a_noise_field_tile" /> is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three and not one, and the difference was found by sabotage rather than
    ///         reasoned.</b> The obvious bar — more than a single value — cannot fail:
    ///         <c>Colour/Levels</c> dithers by one 8-bit step by default, and almost every compound
    ///         here ends in one, so a flat fill comes back as <em>two</em> values. Measured:
    ///         <c>Utility/Histogram Range</c> with its <c>range</c> collapsed to zero draws 2, and
    ///         the least varied compound that is doing its job — <c>Patterns/Brick</c>, which is
    ///         nearly binary — draws 6.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mesh maps are supplied here rather than skipped, and that is what lets the
    ///         generators be in this roll call at all.</b> Every <c>Generators/</c> compound's
    ///         externals are <c>meshmap:</c> references a host resolves;
    ///         <see cref="Bake" /> fills each with a two-axis ramp, so a curvature reads as something
    ///         with edges in it rather than as an image nobody uploaded — which
    ///         <c>TexturePlanEvaluator</c> refuses outright.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_shipped_compound_bakes_to_a_picture() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);

        var baked = 0;

        foreach (var path in TextureCompoundLibrary.Shipped) {
            var picture = Bake(device, evaluator, library, registry, path, Wired);
            var distinct = Distinct(picture);

            output.WriteLine($"{adapter}: {path} → {distinct} distinct values over {Side}×{Side}");

            Assert.True(
                distinct > 3,
                $"{adapter}: '{path}' baked to {distinct} distinct values over {Side}×{Side}, which is a flat "
                + "fill plus a levels node's dither. A compound is an arrangement of nodes that has to "
                + "compute something."
            );

            baked++;
        }

        // ⚠ The instrument. Every assertion above is inside the loop, so an empty `Shipped` — a
        // narrowed glob, a folder renamed — would leave this test green over no work at all.
        Assert.Equal(TextureCompoundLibrary.Shipped.Length, baked);
        Assert.True(baked >= 12, $"only {baked} compounds were baked, and twelve ship.");
    }

    /// <summary>
    ///     ⚠ <c>Utility/Make It Tile</c> makes a picture that does not tile into one that does, and the
    ///     oracle is a closed form rather than a look.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 40 § D2's first row, and the highest-value thing in § 4.9 to have authored out
    ///         of the atomic set.</b> What the compound does is an offset-wrap with an edge mask: a
    ///         <c>Space/Transform 2D</c> at half the image under <c>Wrap</c> is exactly periodic at
    ///         the border by construction — <c>out(0)</c> and <c>out(1)</c> both read <c>in(0.5)</c>
    ///         — and the discontinuity it moves to the middle is hidden by blending the untransformed
    ///         picture back over a band centred there, where the picture is continuous.
    ///     </para>
    ///     <para>
    ///         <b>So the property is a comparison of two differences and not a tolerance.</b> A
    ///         picture tiles when the step across the wrap is the same size as a step between two
    ///         neighbouring columns inside it; it does not when the step across the wrap is much
    ///         larger. Both numbers are measured on the same bake on the same adapter in the same
    ///         run, which is what makes the claim independent of the noise's amplitude, the
    ///         adapter's rounding and the eight-bit read-back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The untiled noise is read out of the <em>same</em> graph, and it has to be.</b>
    ///         <c>TexturePlan.SeedFor</c> mixes the op's identity, which the compiler derives from
    ///         the node that emitted it, so the same <c>Source/Noise</c> settings in a second graph
    ///         are a different field. One graph with two outputs is the only arrangement in which
    ///         "before" and "after" are the same picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the before half is the instrument.</b> If the input noise already tiled — a
    ///         <c>Tiling</c> flag left on, a scale that happens to land on the lattice — then "the
    ///         output tiles" would be true of a compound that did nothing whatever, which is exactly
    ///         the assertion-that-cannot-fail this workstream keeps producing. So the seam of the
    ///         input is asserted to be large before the seam of the output is asserted to be small.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Make_It_Tile_makes_a_noise_field_tile() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out _);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var tile = graph.Add("Utility/Make It Tile");
        var raw = graph.Add("Output/Output");
        var tiled = graph.Add("Output/Output");

        // ⚠ Gradient rather than value noise, and `Tiling` deliberately off: a field whose lattice
        // wraps is one the compound has nothing to do, and this case would then be measuring the
        // noise node.
        noise.SetText("Basis", "Gradient");
        noise.SetValue("Scale", 5f);
        noise.SetValue("Octaves", 3f);
        noise.SetValue("Tiling", 0f);
        raw.SetText("Usage", "height");
        tiled.SetText("Usage", "baseColor");

        graph.Connect(new(noise.Id, "Out"), new(raw.Id, "Input"));
        graph.Connect(new(noise.Id, "Out"), new(tile.Id, "Input"));
        graph.Connect(new(tile.Id, "Out"), new(tiled.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 90210,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(2, compilation.Value.Outputs.Length);

        using var bake = evaluator.Evaluate(compilation.Value);

        var before = bake.Read(compilation.Value.Outputs[0]);
        var after = bake.Read(compilation.Value.Outputs[1]);

        var (seamBefore, stepBefore) = Seam(before);
        var (seamAfter, stepAfter) = Seam(after);

        output.WriteLine(
            $"{adapter}: untiled seam {seamBefore:F2} against a neighbour step of {stepBefore:F2}; "
            + $"tiled seam {seamAfter:F2} against {stepAfter:F2}"
        );

        // The instrument: the input really does not tile. Three times a neighbouring step is far
        // below what a non-tiling gradient noise produces — it measured about 4.6 against 21.5 on the
        // adapter this was written on — and far above what rounding could reach.
        Assert.True(
            seamBefore > 3f * stepBefore,
            $"{adapter}: the untiled noise's wrap seam is {seamBefore:F2} against a neighbour step of "
            + $"{stepBefore:F2}, so it already tiles and the assertion below would be true of a compound "
            + "that did nothing."
        );

        // And the output's wrap is one ordinary step, because the border of an offset-wrapped picture
        // is two samples that were neighbours in the source.
        Assert.True(
            seamAfter <= 2f * stepAfter + 1f,
            $"{adapter}: after Make It Tile the wrap seam is {seamAfter:F2} against a neighbour step of "
            + $"{stepAfter:F2}, so the left edge still does not meet the right one."
        );

        // And the same claim as a differential between the two halves, which is the form that does
        // not depend on the ratio's constant at all: the seam got much smaller and the ordinary step
        // did not move, so what changed is the wrap rather than the picture's contrast.
        Assert.True(
            seamAfter < seamBefore * 0.6f,
            $"{adapter}: the seam went from {seamBefore:F2} to {seamAfter:F2}, which is not the collapse a "
            + "picture that has started tiling shows."
        );

        // ⚠ And it is still a picture. A compound that returned a flat grey would tile perfectly.
        Assert.True(
            Distinct(after) > 16,
            $"{adapter}: the tiled picture has {Distinct(after)} distinct values, which is not a picture."
        );
    }

    /// <summary>A registry holding the atomic node types.</summary>
    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>What a compound's image inputs are wired to in the roll call.</summary>
    const string Wired = "Source/Noise";

    /// <summary>Compiles one compound inside a graph, supplies its mesh maps and evaluates it.</summary>
    /// <param name="device">The device the mesh maps are uploaded on.</param>
    /// <param name="evaluator">The evaluator.</param>
    /// <param name="library">The published compounds.</param>
    /// <param name="registry">The registry they were published into.</param>
    /// <param name="path">The compound's node-type path.</param>
    /// <param name="source">The node type wired into each of its image inputs.</param>
    /// <returns>What it drew.</returns>
    static Bitmap Bake(
        IGraphicsDevice device,
        TexturePlanEvaluator evaluator,
        ISubGraphSource library,
        NodeTypeRegistry registry,
        string path,
        string source
    ) {
        NodeGraphModel graph = new();
        var used = graph.Add(path);
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        foreach (var port in registry.Types.Single(type => type.Path == path).Ports) {
            if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                graph.Connect(new(graph.Add(source).Id, "Out"), new(used.Id, port.Name));
            }
        }

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 7717,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;

        using TextureUploads uploads = new(device);

        foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
            // A mesh map, which no database here resolves. A two-axis ramp is a picture with a
            // gradient in both directions, so a curvature scan, an occlusion levels and a triplanar
            // all have something to read that is not flat.
            //
            // ⚠ At this test's own extent rather than at `owed.Width`, which is zero. An entry naming
            // an asset carries no size, because the size is the *picture's* and the compiler has not
            // seen it — doc 48 § D8's one absolute size, and `Bitmap.rvn` resamples whatever arrives
            // into the image the op writes. A run that read the field would upload nothing at all.
            uploads.Add(plan, owed.Image, Side, Side, Ramp(Side, Side));
        }

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return bake.Read(plan.Outputs[0]);
    }

    /// <summary>An RGBA8 two-axis ramp, as a stand-in for a baked mesh map.</summary>
    /// <param name="width">How wide, in texels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The texels.</returns>
    static byte[] Ramp(int width, int height) {
        var texels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = ((y * width) + x) * 4;

                texels[at] = (byte)(x * 255 / Math.Max(width - 1, 1));
                texels[at + 1] = (byte)(y * 255 / Math.Max(height - 1, 1));
                texels[at + 2] = (byte)((x + y) * 255 / Math.Max(width + height - 2, 1));
                texels[at + 3] = 255;
            }
        }

        return texels;
    }

    /// <summary>How far the left edge is from the right one, and how far one column is from its neighbour.</summary>
    /// <param name="picture">The picture.</param>
    /// <returns>The mean absolute difference across the wrap, and across one ordinary step.</returns>
    /// <remarks>
    ///     Both are means over the same rows of the same image, so the pair is a ratio rather than a
    ///     number with a unit — which is what lets the assertion be about tiling rather than about
    ///     how contrasty the noise happened to be.
    /// </remarks>
    static (float Seam, float Step) Seam(Bitmap picture) {
        var seam = 0f;
        var step = 0f;

        for (var y = 0; y < picture.Height; y++) {
            seam += MathF.Abs(
                TextureKernelHarness.At(picture, 0, y, 0) - TextureKernelHarness.At(picture, picture.Width - 1, y, 0)
            );

            step += MathF.Abs(TextureKernelHarness.At(picture, 0, y, 0) - TextureKernelHarness.At(picture, 1, y, 0));
        }

        return (seam / picture.Height, step / picture.Height);
    }

    /// <summary>How many different values the red channel holds, so "this is a picture" is a claim.</summary>
    /// <param name="picture">The picture.</param>
    /// <returns>The count.</returns>
    static int Distinct(Bitmap picture) {
        HashSet<byte> values = [];

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                values.Add(TextureKernelHarness.At(picture, x, y, 0));
            }
        }

        return values.Count;
    }
}
