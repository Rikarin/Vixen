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
    /// <summary>How wide and tall the focused cases below bake, in texels.</summary>
    /// <remarks>
    ///     Each of those has an oracle of its own — a seam ratio, a count of moved texels — that is a
    ///     property of the arrangement rather than of the extent, so 64 buys them speed and costs
    ///     nothing. <see cref="RollCallSide" /> is the number the library-wide claim needs.
    /// </remarks>
    const int Side = 64;

    /// <summary>How wide and tall the roll call bakes, in texels.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The extent every shipped compound declares</b> — <c>baseWidth: 1024</c> in every one
    ///         of them — and doc 48 § D8's whole point is that a filter's numbers are texels at the
    ///         base resolution. A roll call at 64 measured the library at one sixteenth of the scale
    ///         it was authored for (<a href="https://github.com/Rikarin/Vixen/issues/1085">#1085</a>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>#1085 called it a budget question and the budget is not the obstacle.</b> Measured
    ///         as a differential on one Apple M1 Max in one process, alternating extents so the
    ///         pipelines are warm for both: thirty-one compounds cost 936 ms at 64 and 2810 ms at
    ///         1024, and over three such pairs the gap ran between 0.4 s and 1.9 s. It is 256× the
    ///         texels for under two seconds, because a roll call is one compile, one submit and one
    ///         <c>WaitIdle</c> per compound and almost none of that is texels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the extent buys is the second of #1085's two consequences and not the
    ///         first, which is backwards.</b> That issue ranks "a compound broken at 1024 and
    ///         accidentally fine at 64" above "good at 1024 and degenerate at 64", and names a
    ///         saturating radius as invisible at the smaller extent. It is the more visible one
    ///         there: a knob is texels and is <em>absolute</em>, while a generator's <c>Scale</c> is
    ///         cells across the image and is not, so at 64 every radius is sixteen times bigger
    ///         relative to what it acts on. A radius that wipes the picture wipes it at 64 first.
    ///         What 64 could not see is a knob that is a <em>no-op</em> at 1024 — and a no-op passes
    ///         its input through, which is a picture, so this bar cannot see it at either extent.
    ///     </para>
    ///     <para>
    ///         <b>So the reason to be here is the consequence #1085 ranks second, and it had already
    ///         happened before the library was committed.</b> That issue records
    ///         <c>Grunges/Grunge Rust</c> baking <b>four</b> values at 64 — its erosion is 7.75 texels
    ///         and its Worley cells are 4.5 there — and the file being softened until it read at both
    ///         extents. That is good content changed to suit an extent nothing bakes at, and at the
    ///         extent the file declares the pressure is gone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What one extent still cannot see</b>, named rather than implied: a compound that
    ///         draws at 1024 and degenerates at the size an author previews it at, and anything whose
    ///         <em>chain length</em> is a function of the extent — a jump flood is one dispatch per
    ///         halving and a reduction one per level, so those run a different op list here than in a
    ///         256 thumbnail. Baking at two extents would see the first and was rejected: with
    ///         <see cref="Stimulus" /> it is the same experiment twice, and without it the second
    ///         extent fails good compounds.
    ///     </para>
    /// </remarks>
    const int RollCallSide = 1024;

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
    ///         nearly binary — draws 6 at 64 and 21 at <see cref="RollCallSide" />. Every other
    ///         shipped compound draws more than a hundred there, so the bar is not close: the second
    ///         and third least varied are <c>Grunges/Grunge Smears</c> at 111 and
    ///         <c>Grunges/Grunge Rust</c> at 112, and the three <c>Patterns/</c> rows this batch added
    ///         draw 125, 252 and 256.
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
            var picture = Bake(device, evaluator, library, registry, path, Wired, RollCallSide);
            var distinct = Distinct(picture);

            output.WriteLine($"{adapter}: {path} → {distinct} distinct values over {RollCallSide}×{RollCallSide}");

            Assert.True(
                distinct > 3,
                $"{adapter}: '{path}' baked to {distinct} distinct values over {RollCallSide}×{RollCallSide}, "
                + "which is a flat fill plus a levels node's dither. A compound is an arrangement of nodes "
                + "that has to compute something."
            );

            baked++;
        }

        // ⚠ The instrument. Every assertion above is inside the loop, so an empty `Shipped` — a
        // narrowed glob, a folder renamed — would leave this test green over no work at all.
        Assert.Equal(TextureCompoundLibrary.Shipped.Length, baked);

        // ⚠ **And the floor is re-derived rather than left where the batch that wrote it put it.** It
        // read twelve while thirty-one shipped, so nineteen compounds could have stopped being
        // embedded with this case green: the equality above compares the loop with `Shipped`, and
        // `Shipped` is the manifest, so a glob that narrowed takes both sides down together. This is
        // the only number here that is independent of the assembly's own idea of what it ships.
        Assert.True(baked >= 34, $"only {baked} compounds were baked, and thirty-four ship.");
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

    /// <summary>
    ///     ⚠ One <c>Colour/Mix</c> computes what <c>Make It Tile</c>'s last four nodes compute, texel
    ///     for texel.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1059">#1059</a>'s worked example,
    ///         measured rather than argued.</b> That finding was filed on this compound: a masked
    ///         composite <c>offset·(1 − m) + original·m</c> authored out of the atomic set is
    ///         <c>Colour/Invert</c> plus three <c>Colour/Blend</c>s — four dispatches and three
    ///         intermediate images. <c>Colour/Mix</c> arrived as the answer, and the question this
    ///         case settles is whether the answer is the <em>same</em> answer, which nothing in the
    ///         tree had checked: a node that composites correctly and a node that composites the way
    ///         the four-node form did are different claims, and only the second one licenses the
    ///         rewrite.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both forms are in one graph, for
    ///         <see cref="Make_It_Tile_makes_a_noise_field_tile" />'s reason.</b>
    ///         <c>TexturePlan.SeedFor</c> mixes the op's identity, so the same <c>Source/Noise</c>
    ///         settings in a second graph are a different field and the two halves would be compared
    ///         over different pictures.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And two guards, because "two arrangements agree" is trivially true of two
    ///         arrangements that both do nothing.</b> The composite has to be a picture, and it has
    ///         to differ from its own background — a <c>Mix</c> whose mask never reached the kernel
    ///         would return the backdrop, and so would a four-node form whose <c>Add</c> was reading
    ///         one term. Neither guard passes over a flat fill, and neither passes over a mask that
    ///         was ignored.
    ///     </para>
    ///     <para>
    ///         <b>So the rewrite is available and <c>Make It Tile</c> deliberately does not take
    ///         it.</b> The twelve compounds are the measurement of the atomic set, and editing the
    ///         worked example out of the finding would leave nothing in the tree showing what the gap
    ///         cost — <c>Compounds/README.md</c> makes that argument and this case is what stops it
    ///         becoming an excuse for an unverified claim.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_masked_composite_computes_what_Make_It_Tiles_four_nodes_compute() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();

        NodeGraphModel graph = new();

        // Three grey fields: what is underneath, what is on top, and how much of the top each texel
        // gets. Value noise, because every read below is of the red channel and a Worley writes three
        // different things into three channels.
        var under = graph.Add("Source/Noise");
        var over = graph.Add("Source/Noise");
        var coverage = graph.Add("Source/Noise");

        foreach (var (node, scale) in new[] { (under, 5f), (over, 9f), (coverage, 3f) }) {
            node.SetText("Basis", "Value");
            node.SetValue("Scale", scale);
        }

        // The four-node form, exactly as #1059 wrote it down.
        var inverted = graph.Add("Colour/Invert");
        var keptUnder = graph.Add("Colour/Blend");
        var keptOver = graph.Add("Colour/Blend");
        var summed = graph.Add("Colour/Blend");

        keptUnder.SetText("Mode", "Multiply");
        keptOver.SetText("Mode", "Multiply");
        summed.SetText("Mode", "Add");

        graph.Connect(new(coverage.Id, "Out"), new(inverted.Id, "Input"));
        graph.Connect(new(under.Id, "Out"), new(keptUnder.Id, "Background"));
        graph.Connect(new(inverted.Id, "Out"), new(keptUnder.Id, "Foreground"));
        graph.Connect(new(over.Id, "Out"), new(keptOver.Id, "Background"));
        graph.Connect(new(coverage.Id, "Out"), new(keptOver.Id, "Foreground"));
        graph.Connect(new(keptUnder.Id, "Out"), new(summed.Id, "Background"));
        graph.Connect(new(keptOver.Id, "Out"), new(summed.Id, "Foreground"));

        // And the one-node form, over the same three fields.
        var mixed = graph.Add("Colour/Mix");

        mixed.SetText("Mode", "Copy");

        graph.Connect(new(under.Id, "Out"), new(mixed.Id, "Background"));
        graph.Connect(new(over.Id, "Out"), new(mixed.Id, "Foreground"));
        graph.Connect(new(coverage.Id, "Out"), new(mixed.Id, "Mask"));

        // ⚠ And the backdrop on its own, which is the guard rather than a third form: a mask that
        // never reached either arrangement leaves both of them equal to this.
        var four = graph.Add("Output/Output");
        var one = graph.Add("Output/Output");
        var plain = graph.Add("Output/Output");

        four.SetText("Usage", "height");
        one.SetText("Usage", "baseColor");
        plain.SetText("Usage", "roughness");

        graph.Connect(new(summed.Id, "Out"), new(four.Id, "Input"));
        graph.Connect(new(mixed.Id, "Out"), new(one.Id, "Input"));
        graph.Connect(new(under.Id, "Out"), new(plain.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 4242 };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(3, compilation.Value.Outputs.Length);

        using var bake = evaluator.Evaluate(compilation.Value);

        // ⚠ **By usage, never by position.** `TexturePlan.Outputs` is a list of image indices in
        // *image* order — the compiler's own remark says it is "a list of indices with no names on
        // it" — and image order is allocation order, which is the graph's topology rather than the
        // order the `Output` nodes were added. Reading `Outputs[0]` as "the first output I wrote" is
        // therefore a coincidence that holds until somebody adds a node upstream, and it cost this
        // case an hour: the three reads came back as backdrop, mix, four-node.
        var authored = bake.Read(Image(compiler, "height"));
        var kernel = bake.Read(Image(compiler, "baseColor"));
        var backdrop = bake.Read(Image(compiler, "roughness"));

        var worst = 0f;
        var moved = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                worst = MathF.Max(
                    worst,
                    MathF.Abs(TextureKernelHarness.At(authored, x, y, 0) - TextureKernelHarness.At(kernel, x, y, 0))
                );

                if (MathF.Abs(TextureKernelHarness.At(kernel, x, y, 0) - TextureKernelHarness.At(backdrop, x, y, 0))
                    > 2f) {
                    moved++;
                }
            }
        }

        output.WriteLine(
            $"{adapter}: four nodes against one differ by at most {worst:F1}/255; the composite moved "
            + $"{moved} of {Side * Side} texels off its backdrop"
        );

        // The first guard: the composite is a picture and not a fill, so the equality below is a
        // claim about arithmetic rather than about two flat greys.
        Assert.True(
            Distinct(kernel) > 16,
            $"{adapter}: the masked composite has {Distinct(kernel)} distinct values, which is not a picture."
        );

        // The second: the mask actually did something. A Mix that dropped its mask and a four-node
        // form that dropped a term would both return the backdrop, and would agree perfectly.
        Assert.True(
            moved > Side * Side / 2,
            $"{adapter}: only {moved} of {Side * Side} texels differ from the backdrop, so the mask made "
            + "almost no difference and the two forms agreeing says nothing."
        );

        // ⚠ Two eight-bit steps, and the tolerance is the read-back rather than a fudge: the authored
        // form stores three `rgba16f` intermediates where the kernel stores none, so the two round
        // differently at the last place. A mode, an operand or a `1 −` in the wrong position moves
        // this by tens.
        Assert.True(
            worst <= 2f,
            $"{adapter}: the four-node form and Colour/Mix differ by {worst:F1}/255 at worst, so one node "
            + "does not replace the four and #1059's worked example is not answered."
        );
    }

    /// <summary>
    ///     ⚠ <c>Utility/Highpass</c> answers exactly one half over a flat input, whatever the input is.
    /// </summary>
    /// <param name="value">What the flat input is worth.</param>
    /// <remarks>
    ///     <para>
    ///         <b>A closed form, and it is the whole of what the compound claims.</b> A highpass is
    ///         the detail about a mid grey; a picture with no detail in it is therefore the mid grey,
    ///         for every input value there is. Authored out of the atomic set that is
    ///         <c>Blur</c> → <c>Invert</c> → <c>Blend Copy</c> at half opacity, which is
    ///         <c>(a + (1 − b)) / 2</c> — the arrangement <c>Blend Subtract</c> cannot give, because
    ///         it clamps at black and a highpass is signed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the oracle is what makes this more than "it is not flat": all three nodes have
    ///         to be right for the answer to be a half.</b> A blur that did nothing, an invert that
    ///         inverted alpha too, an opacity read as one instead of a half — each moves the answer
    ///         off 128, and none of them changes whether the picture is flat.
    ///     </para>
    ///     <para>
    ///         <b>Twice, at two input values, because one is not a claim about "whatever the input
    ///         is".</b> A compound that answered its own input rather than a half would pass at 0.5
    ///         alone.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.8f)]
    public void Highpass_answers_one_half_over_a_flat_input(float value) {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out _);

        NodeGraphModel graph = new();
        var flat = graph.Add("Source/Uniform");
        var high = graph.Add("Utility/Highpass");
        var target = graph.Add("Output/Output");

        flat.SetValue("Colour", [value, value, value, 1f]);
        graph.Connect(new(flat.Id, "Out"), new(high.Id, "Input"));
        graph.Connect(new(high.Id, "Out"), new(target.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 3301,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        using var bake = evaluator.Evaluate(compilation.Value);

        var picture = bake.Read(compilation.Value.Outputs[0]);

        output.WriteLine(
            $"{adapter}: a flat {value} through Highpass is "
            + $"{TextureKernelHarness.At(picture, 0, 0, 0)}…{TextureKernelHarness.At(picture, Side - 1, Side - 1, 0)}"
        );

        // Across the whole image and not one corner: a compound that answered a half in the middle
        // and its input at the edges is a blur reading past the image, which one sample misses.
        for (var y = 0; y < Side; y += 8) {
            for (var x = 0; x < Side; x += 8) {
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 0), (byte)126, (byte)130);
            }
        }
    }

    /// <summary>
    ///     ⚠ <c>Utility/Contrast Luminosity</c>'s folded expressions reach the picture: at a contrast
    ///     of one half a ramp is clipped at the quarter and the three-quarter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The bake half of
    ///         <c>TextureCompoundLibraryTests.An_expression_in_a_shipped_compound_folds_and_an_override_moves_it</c>,
    ///         and both are worth having.</b> That one reads the number off the op the compiler
    ///         emitted, which is a claim about a value; this one reads texels, which is a claim that
    ///         the value reached a dispatch. Doc 48's own rule — verify with a picture where the
    ///         output is a picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>At a contrast of one half rather than at the declared default, because the
    ///         default is the identity and so is a <c>Levels</c> whose expressions were thrown
    ///         away.</b> An assertion at the default could not fail in the direction this is written
    ///         for. With <c>contrast</c> at 0.5 the input range is 0.25…0.75, and the input is
    ///         <c>Source/Shape</c>'s gradation — exactly <c>(x + 0.5) / width</c> — so the first
    ///         sixteen columns must be black and the last sixteen white.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Contrast_Luminositys_folded_range_clips_a_ramp_where_the_expression_says() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out _);

        NodeGraphModel graph = new();
        var ramp = graph.Add("Source/Shape");
        var tone = graph.Add("Utility/Contrast Luminosity");
        var target = graph.Add("Output/Output");

        ramp.SetText("Kind", "Gradation");
        tone.SetText("contrast", "0.5");
        graph.Connect(new(ramp.Id, "Out"), new(tone.Id, "Input"));
        graph.Connect(new(tone.Id, "Out"), new(target.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 5501,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        using var bake = evaluator.Evaluate(compilation.Value);

        var picture = bake.Read(compilation.Value.Outputs[0]);

        output.WriteLine(
            $"{adapter}: the clipped ramp runs {TextureKernelHarness.At(picture, 0, 32, 0)}, "
            + $"{TextureKernelHarness.At(picture, 15, 32, 0)}, {TextureKernelHarness.At(picture, 32, 32, 0)}, "
            + $"{TextureKernelHarness.At(picture, 48, 32, 0)}, {TextureKernelHarness.At(picture, 63, 32, 0)}"
        );

        // ⚠ Within a byte on either side, because `Colour/Levels` dithers by one 8-bit step by
        // default — the same fact that made this file's roll-call bar three rather than one.
        Assert.InRange(TextureKernelHarness.At(picture, 15, 32, 0), (byte)0, (byte)1);
        Assert.InRange(TextureKernelHarness.At(picture, 48, 32, 0), (byte)254, (byte)255);

        // And the middle is the ramp stretched over the half range rather than clipped with it: at
        // the centre the input is a half, which the folded range sends back to a half.
        Assert.InRange(TextureKernelHarness.At(picture, 32, 32, 0), (byte)125, (byte)133);
    }

    /// <summary>Which image one output usage was compiled into.</summary>
    /// <param name="compiler">The compiler, after a <c>Compile</c>.</param>
    /// <param name="usage">The <c>Output</c> node's usage — <c>baseColor</c>, <c>height</c>, …</param>
    /// <returns>The image index, to be handed to <c>TextureBake.Read</c>.</returns>
    /// <remarks>
    ///     ⚠ <b><c>TexturePlan.Outputs</c> carries no names, and its order is the image table's rather
    ///     than the graph's.</b> <c>TextureGraphCompiler.Outputs</c> is the half that knows which
    ///     usage went where, and it exists precisely because the plan deliberately does not.
    /// </remarks>
    static int Image(TextureGraphCompiler compiler, string usage) =>
        compiler.Outputs
            .Single(output => string.Equals(output.Usage, usage, StringComparison.OrdinalIgnoreCase))
            .Image;

    /// <summary>A registry holding the atomic node types.</summary>
    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>What a compound's image inputs are wired to in the roll call.</summary>
    const string Wired = "Source/Noise";

    /// <summary>How many texels across one feature of the picture a compound is fed measures.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The stimulus has to be measured in texels, because the knobs are</b> — and this is
    ///         the half of <a href="https://github.com/Rikarin/Vixen/issues/1085">#1085</a> the issue
    ///         did not predict. <c>Source/Noise</c>'s <c>Scale</c> is <em>cells across the image</em>,
    ///         so its default of 8 is scale-invariant: at 64 a cell is 8 texels and at 1024 it is 128.
    ///         A compound's radius is texels at the base resolution. So the two only meet at one
    ///         extent, and moving the roll call to 1024 with the default stimulus made
    ///         <c>Utility/Highpass</c> bake <b>2 distinct values</b> — a flat fill, below the bar.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>Utility/Highpass</c> is not broken.</b> It is <c>0.5·in + 0.5·(1 −
    ///         blur(in))</c>, which is exactly one half wherever the blur returns its input; its
    ///         default radius is 8 texels, and against features 128 texels across there is genuinely
    ///         no detail for it to keep. Tuning that file until the roll call was happy would have
    ///         been tuning content to an instrument, which is the second failure #1085 names. Fixing
    ///         the <em>stimulus</em> instead — <c>Scale = side / Stimulus</c>, so a feature is this
    ///         many texels at every extent — puts every one of the thirty-one back above the bar,
    ///         Highpass at a comfortable margin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is also self-instrumenting, which is why the setting is not spelled twice.</b>
    ///         If <c>SetValue</c> ever stopped naming a real input — a renamed port, a typo — the
    ///         node would fall back to its scale-invariant default and <c>Utility/Highpass</c> would
    ///         go flat again at 1024. The roll call goes red rather than quietly measuring something
    ///         else.
    ///     </para>
    /// </remarks>
    const int Stimulus = 8;

    /// <summary>Compiles one compound inside a graph, supplies its mesh maps and evaluates it.</summary>
    /// <param name="device">The device the mesh maps are uploaded on.</param>
    /// <param name="evaluator">The evaluator.</param>
    /// <param name="library">The published compounds.</param>
    /// <param name="registry">The registry they were published into.</param>
    /// <param name="path">The compound's node-type path.</param>
    /// <param name="source">The node type wired into each of its image inputs.</param>
    /// <param name="side">How wide and tall to bake, in texels. The stimulus scales with it.</param>
    /// <returns>What it drew.</returns>
    static Bitmap Bake(
        IGraphicsDevice device,
        TexturePlanEvaluator evaluator,
        ISubGraphSource library,
        NodeTypeRegistry registry,
        string path,
        string source,
        int side
    ) {
        NodeGraphModel graph = new();
        var used = graph.Add(path);
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        foreach (var port in registry.Types.Single(type => type.Path == path).Ports) {
            if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                var fed = graph.Add(source);

                // ⚠ In texels, because the knobs downstream of it are — see `Stimulus`. The node's
                // own default is cells across the image, which is scale-invariant, and a
                // scale-invariant stimulus against a texel-valued radius is an experiment that only
                // means anything at one extent.
                fed.SetValue("Scale", side / (float)Stimulus);
                graph.Connect(new(fed.Id, "Out"), new(used.Id, port.Name));
            }
        }

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = side,
            BaseHeight = side,
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
            uploads.Add(plan, owed.Image, side, side, Ramp(side, side));
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
