// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>
///     What <see cref="TexturePlanEvaluator" /> <i>holds</i> rather than what it has compiled — the
///     measurement <a href="https://github.com/Rikarin/Vixen/issues/1091">#1091</a> asked for, and the
///     bound that answers it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim being measured is "a module and a pipeline per keystroke".</b>
///         <c>TexturePixelProcessor</c> names its shader after a digest of the author's expression so
///         that two expressions cannot share a module, and <c>TextureGraphPreviews.Rebuild</c>
///         re-evaluates on every <c>NodeGraphModel.Changed</c> — which a command stack raises per
///         edit. Typing <c>a*b</c> therefore compiles <c>a</c>, <c>a*</c> and <c>a*b</c>, and before
///         this file nothing could say whether the first two were still on the device.
///     </para>
///     <para>
///         ⚠ <b><c>Compilations</c> could not say it, and that is why the issue was a reasoned claim
///         rather than a measured one.</b> It counts what has been built and only ever rises, so a
///         cache that evicts perfectly and one that evicts nothing print the same number.
///         <c>TexturePlanEvaluator.Variants</c> and <c>NullDevice.LiveResourceCount</c> are the two
///         that can, and they are independent: one is that class's own dictionary and the other is the
///         device's tally of objects created and not destroyed, which is the thing an artist's laptop
///         actually runs out of.
///     </para>
///     <para>
///         <b>On the Null device, for <c>TextureEvaluationCostTests</c>' reason.</b> What is counted
///         is RHI objects, which that device records exactly and a real one does not expose at all;
///         nothing here reads a texel. ⚠ The Raven front end, <c>EffectLoader</c> and every
///         <c>CreateShader</c>/<c>CreateComputePipeline</c> call still run — the fallback this
///         repository warns about is a fallback for <i>pictures</i>, and there is no picture in this
///         file to be black.
///     </para>
/// </remarks>
public class TextureVariantCacheTests(ITestOutputHelper output) {
    const int Side = 16;

    /// <summary>
    ///     ⚠ Every distinct expression is a distinct held variant, and the per-edit cost is measured
    ///     rather than argued.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The ceiling is put out of the way first</b>, so that this case measures the leak
    ///         #1091 describes rather than the bound that now answers it. The two are separate claims
    ///         and a single case that conflated them would pass on an evaluator that compiled nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The device count is what makes it a measurement.</b> "One variant per expression"
    ///         is a statement about a dictionary; what it costs is objects on a device, and the
    ///         difference between the two is exactly where the pipeline layout was hiding — see
    ///         <c>TexturePlanEvaluator.Destroy</c>. The number is printed rather than asserted
    ///         exactly, because it is per-backend arithmetic and this file's claim is that it grows
    ///         with the typing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_authored_kernel_costs_a_held_variant_and_device_objects_per_distinct_expression() {
        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device) { AuthoredVariantCeiling = 1000 };

        // ⚠ Typing a *number*, and the choice is a correction to #1091 rather than a convenience.
        // That issue's example is "a, a*, a*b, …" and two of those three do not compile — a Pixel
        // Processor with a syntax error in it is refused by the front end and never reaches an
        // evaluator, so the leak is one variant per intermediate state that *parses* rather than one
        // per key. Every prefix of a decimal literal parses, which is what an artist dragging a
        // constant produces, and it is the shape that makes the growth real rather than rhetorical.
        var typed = (string[])["0.1f", "0.12f", "0.125f", "0.1259f", "0.12595f"];
        var costs = new int[typed.Length];

        for (var keystroke = 0; keystroke < typed.Length; keystroke++) {
            var before = device.LiveResourceCount;

            Bake(evaluator, Written(typed[keystroke]));

            // The bake's own textures and views go with it, so what is left is the variant.
            costs[keystroke] = device.LiveResourceCount - before;

            Assert.Equal(keystroke + 1, evaluator.Variants);
            Assert.Equal(keystroke + 1, evaluator.AuthoredCount);
            Assert.Equal(keystroke + 1, evaluator.Compilations);
        }

        output.WriteLine(
            $"Null device: {typed.Length} intermediate expressions cost {string.Join(" + ", costs)} = "
            + $"{costs.Sum()} device objects, held."
        );

        // ⚠ The instrument, and it is the half that decides whether any of this is a leak at all: an
        // edit that cost nothing on the device would satisfy every equality above.
        Assert.All(costs, cost => Assert.True(cost > 0, "an expression that cost no device object is not a leak"));

        // And the same expression twice is one variant, so what is counted is the *distinct* text and
        // not the number of bakes. Without this, the growth above is equally true of a cache that
        // keys on nothing at all.
        Bake(evaluator, Written(typed[0]));

        Assert.Equal(typed.Length, evaluator.Variants);
        Assert.Equal(typed.Length, evaluator.Compilations);
    }

    /// <summary>
    ///     ⚠ Past the ceiling the held count stops rising and the device gets the objects back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two assertions and neither implies the other.</b> A cache that dropped its entries
    ///         without destroying anything would hold the count flat and leak exactly as before — the
    ///         shape <c>TextureGraphPreviews.Drop</c>'s own remarks warn about one level up — so the
    ///         device's tally is read as well, and it is the one that would catch an eviction that
    ///         forgot the pipeline layout.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Twice the ceiling, so the plateau is a plateau rather than a coincidence.</b> A
    ///         ceiling asserted at exactly its own value is satisfied by an off-by-one and by a cache
    ///         that happens to hold that many.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Authored_variants_past_the_ceiling_are_destroyed_rather_than_kept() {
        const int ceiling = 3;

        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device) { AuthoredVariantCeiling = ceiling };

        var settled = 0;

        for (var keystroke = 0; keystroke < ceiling * 2; keystroke++) {
            Bake(evaluator, Written($"uv.x*{keystroke}f"));

            // ⚠ Ceiling *plus one*, because the eviction runs at the top of the next evaluation and
            // never during this one: `EvictAuthored`'s remarks say why, and a variant this bake bound
            // is one the eviction must not have been free to choose.
            Assert.Equal(Math.Min(keystroke + 1, ceiling + 1), evaluator.Variants);

            if (keystroke == ceiling) {
                settled = device.LiveResourceCount;
            }
        }

        Assert.Equal(ceiling * 2, evaluator.Compilations);
        Assert.Equal(ceiling - 1, evaluator.Evictions);

        // The device is where it has to show: flat from the moment the ceiling was first crossed, so
        // every expression typed after it cost nothing that outlived its own bake.
        Assert.Equal(settled, device.LiveResourceCount);

        output.WriteLine(
            $"Null device: {ceiling * 2} expressions, {evaluator.Compilations} compiles, "
            + $"{evaluator.Evictions} evictions, {evaluator.Variants} held, "
            + $"{device.LiveResourceCount} live device objects."
        );
    }

    /// <summary>
    ///     ⚠ An embedded kernel is never evicted, however much authored churn goes past it.
    /// </summary>
    /// <remarks>
    ///     <b>The asymmetry is the fix, and a ceiling over the whole dictionary would have broken
    ///     this.</b> The library is forty-odd names times a handful of output formats — bounded, and
    ///     re-compiled on every use if it were evicted, which is the Raven front end per op on the
    ///     path a panel redraws. So the queue holds authored keys only, and this is the case that goes
    ///     red if somebody simplifies it into one cache with one ceiling.
    /// </remarks>
    [Fact]
    public void An_embedded_kernel_survives_a_ceiling_full_of_authored_churn() {
        using var device = new NullDevice(new());
        var source = Source(device);

        using var evaluator = new TexturePlanEvaluator(device) { AuthoredVariantCeiling = 1 };

        BakeLevels(evaluator, source);

        Assert.Equal(1, evaluator.Variants);
        Assert.Equal(0, evaluator.AuthoredCount);

        for (var keystroke = 0; keystroke < 6; keystroke++) {
            Bake(evaluator, Written($"uv.y*{keystroke}f"));
        }

        var compilations = evaluator.Compilations;

        // Still there, which is what compiling nothing says. `Compilations` is the right instrument
        // for this one and the wrong one for the cases above, and the difference is that here the
        // claim is about a *hit*.
        BakeLevels(evaluator, source);

        Assert.Equal(compilations, evaluator.Compilations);
        Assert.Equal(1, evaluator.Variants - evaluator.AuthoredCount);

        device.Destroy(source);
    }

    /// <summary>The queue is least recently <i>used</i>, so re-using an old expression keeps it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half an insertion-ordered queue gets wrong, and it is the ordinary
    ///         interaction:</b> an author types, undoes to an earlier expression and carries on from
    ///         there. Under insertion order the expression they have just gone back to is the oldest
    ///         and is the next one destroyed — so this case is red under that implementation with the
    ///         two probes' answers exactly swapped, which is what makes it a test of the order rather
    ///         than of the ceiling.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ceiling is raised before the probes.</b> An eviction runs at the top of every
    ///         evaluation, so a probe bake taken under the low ceiling would itself destroy whatever
    ///         is at the front of the queue — and the probe would then be reporting on its own effect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Re_using_an_expression_moves_it_off_the_front_of_the_queue() {
        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device) { AuthoredVariantCeiling = 2 };

        Bake(evaluator, Written("uv.x"));
        Bake(evaluator, Written("uv.y"));

        // The undo: the older of the two is asked for again. It compiles nothing and moves to the
        // back of the queue, which is the whole claim.
        var reused = evaluator.Compilations;

        Bake(evaluator, Written("uv.x"));

        Assert.Equal(reused, evaluator.Compilations);

        // Two more expressions, so the ceiling of two evicts. `uv.y` is now the front.
        Bake(evaluator, Written("uv.x+uv.y"));
        Bake(evaluator, Written("uv.x-uv.y"));

        evaluator.AuthoredVariantCeiling = 100;

        var before = evaluator.Compilations;

        Bake(evaluator, Written("uv.x"));

        Assert.Equal(before, evaluator.Compilations);

        Bake(evaluator, Written("uv.y"));

        Assert.Equal(before + 1, evaluator.Compilations);
    }

    /// <summary>
    ///     ⚠ A graph carrying more authored kernels than the ceiling does not evict its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The regression the first shape of this fix would have shipped, and it would have
    ///         been worse than the leak.</b> An eviction that took the least recently used key without
    ///         asking what the incoming plan needs would, on a graph with more Pixel Processors than
    ///         the ceiling, destroy some of that graph's own variants at the top of every bake and
    ///         recompile them a few lines later — the Raven front end per surplus kernel per
    ///         keystroke, on the interactive path.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the ceiling is a bound on history and is deliberately soft.</b> The assertion
    ///         is that a repeated bake of one plan compiles nothing at all, however far that plan is
    ///         over the ceiling, and <c>Compilations</c> is the right instrument here because the
    ///         claim is about cache <em>hits</em>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_plan_carrying_more_kernels_than_the_ceiling_does_not_evict_its_own() {
        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device) { AuthoredVariantCeiling = 1 };

        var plan = TwoProcessors("uv.x", "uv.y");

        Assert.Equal(2, plan.Kernels.Count);

        Bake(evaluator, plan);

        // Two authored, and the blend is a third variant this assembly ships — which is why the
        // compile count is taken as a baseline rather than written down.
        Assert.Equal(2, evaluator.AuthoredCount);

        var compiled = evaluator.Compilations;

        // Twice more, and neither pays a compile: the plan's own kernels are exempt from the ceiling
        // it is over.
        Bake(evaluator, plan);
        Bake(evaluator, plan);

        Assert.Equal(compiled, evaluator.Compilations);
        Assert.Equal(0, evaluator.Evictions);

        // ⚠ And the exemption is *this plan's*, not a blanket amnesty: a different expression evicts
        // the pair the moment they stop being wanted.
        Bake(evaluator, Written("0.5f"));

        Assert.True(evaluator.Evictions > 0, "nothing was ever evicted, so the ceiling does nothing");
    }

    /// <summary>A ceiling of zero or less is refused rather than emptying the cache every bake.</summary>
    [Fact]
    public void A_ceiling_that_is_not_positive_is_refused() {
        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device);

        Assert.Throws<ArgumentOutOfRangeException>(() => { evaluator.AuthoredVariantCeiling = 0; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { evaluator.AuthoredVariantCeiling = -1; });
    }

    static void Bake(TexturePlanEvaluator evaluator, TexturePlan plan) {
        using var bake = evaluator.Evaluate(plan);
    }

    static void BakeLevels(TexturePlanEvaluator evaluator, TextureHandle source) {
        using var bake = evaluator.Evaluate(Levels(), new Dictionary<int, TextureHandle> { [0] = source });
    }

    static TextureHandle Source(NullDevice device) =>
        device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                Side,
                Side,
                TextureUsage.Sampled | TextureUsage.CopyDestination | TextureUsage.CopySource,
                Name: "variant cache source"
            )
        );

    /// <summary>A plan whose only op names a kernel this assembly ships.</summary>
    static TexturePlan Levels() =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Levels",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("inputBlack", 0f),
                        new("inputWhite", 1f),
                        new("gamma", 1f),
                        new("outputBlack", 0f),
                        new("outputWhite", 1f),
                        new("dither", 0f)
                    ]
                }
            ],
            Outputs = [1]
        };

    /// <summary>A plan carrying two Pixel Processors' kernels, blended together.</summary>
    static TexturePlan TwoProcessors(string first, string second) {
        NodeGraphModel graph = new();
        var left = graph.Add("Filters/Pixel Processor");
        var right = graph.Add("Filters/Pixel Processor");
        var blend = graph.Add("Colour/Blend");
        var target = graph.Add("Output/Output");

        left.SetText("Expression", $"float4({first}, 0f, 0f, 1f)");
        right.SetText("Expression", $"float4({second}, 0f, 0f, 1f)");

        graph.Connect(new(left.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(right.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 }
            .Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    /// <summary>A plan carrying a Pixel Processor's own kernel, named after a digest of it.</summary>
    static TexturePlan Written(string expression) {
        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var target = graph.Add("Output/Output");

        processor.SetText("Expression", $"float4({expression}, 0f, 0f, 1f)");
        graph.Connect(new(processor.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 }
            .Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }
}
