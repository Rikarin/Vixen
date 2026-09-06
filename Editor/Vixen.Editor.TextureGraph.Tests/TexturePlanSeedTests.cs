// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What <c>TexturePlan.SeedFor</c> is stable under, and what it is not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="TexturePlan.Seed" />'s own remarks claimed a property the code does not
///         have, and <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a> is the finding.</b>
///         They said a shared per-plan seed "means inserting an op upstream changes the numbers every
///         op downstream draws — so the artist moves a node and the whole material shimmers", and that
///         <c>SeedFor</c> "mixes the plan's seed with the op's own identity instead". An op's index in
///         <c>Ops</c> is not an identity: it is exactly the number an insertion moves.
///         <see cref="An_op_added_beneath_a_noise_redraws_it" /> is that shimmer, reproduced.
///     </para>
///     <para>
///         <b>So this file is the decision written down rather than a fix.</b> A stable identity has
///         to come from a front end and neither has one to give: a graph node's id and a layer
///         stack's generated node ids both move under the same edits the op index does, so the fix is
///         a change to what a front end records, not to this mix. What is owed is on #875.
///     </para>
///     <para>
///         ⚠ <b>The pin is the instrument, and what it is for is the report rather than the value.</b>
///         Every existing material with a noise in it bakes different pixels the moment an op is
///         added ahead of that noise, and #832's mask fix — three ops per masked layer per channel —
///         did exactly that with nothing anywhere saying so. A constant that has to be re-blessed is
///         what turns a silent pixel change into a line in a commit message.
///     </para>
/// </remarks>
public class TexturePlanSeedTests {
    const int Side = 128;

    /// <summary>The plan's own seed, so nothing here depends on a default moving.</summary>
    const uint Seed = 5150u;

    /// <summary>What op 0 of a plan seeded <see cref="Seed" /> draws from, measured 2026-09-06.</summary>
    const uint Pinned = 1211284530u;

    /// <summary>⚠ The seed of a bare noise, pinned — so a change to it is reported rather than shipped.</summary>
    /// <remarks>
    ///     <b>Deliberately the smallest graph that has a seeded op in it</b>, so this goes red for a
    ///     change to how the compiler emits ops and not for a change to any one node. If it is red
    ///     after a change you meant to make: the pixels of every existing material with a noise in it
    ///     have moved, which is allowed and is not silent — re-bless the number and say so.
    /// </remarks>
    [Fact]
    public void The_seed_of_a_bare_noise_is_pinned() {
        var (plan, index) = Noise(beneath: false);

        Assert.Equal(0, index);

        Assert.True(
            plan.SeedFor(index) == Pinned,
            $"The bare noise's seed is {plan.SeedFor(index)} and this pinned it at {Pinned}. That is not a "
            + "defect in whatever changed: it means an op moved in the list, and every existing material "
            + "with a noise in it now bakes different pixels. Re-bless the number and say so in the commit — "
            + "or give a seeded op an identity that an insertion cannot move (#875)."
        );

        // And the pin is a fact about the mix rather than about the plan's own seed, which a caller
        // may set: the same op index under another plan seed is another number.
        TexturePlan other = new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = plan.Images,
            Ops = plan.Ops,
            Seed = Seed + 1
        };

        Assert.NotEqual(plan.SeedFor(index), other.SeedFor(index));
    }

    /// <summary>
    ///     ⚠ A layer added <em>beneath</em> a noise changes what the noise draws, which is the
    ///     shimmer <see cref="TexturePlan.Seed" />'s remarks say cannot happen.
    /// </summary>
    /// <remarks>
    ///     <b>Asserting the defect, on purpose.</b> Until #875 has a stable identity to mix, this is
    ///     the contract, and a test that named the good behaviour would be red today and tell nobody
    ///     why. When the seed does become stable this goes red — and the reader is here, in the file
    ///     that says what the number is for.
    /// </remarks>
    [Fact]
    public void An_op_added_beneath_a_noise_redraws_it() {
        var (bare, bareIndex) = Noise(beneath: false);
        var (stacked, stackedIndex) = Noise(beneath: true);

        Assert.NotEqual(bareIndex, stackedIndex);

        Assert.NotEqual(bare.SeedFor(bareIndex), stacked.SeedFor(stackedIndex));

        // The instrument: it is the *index* that moved and not the node. Both plans hold one noise,
        // authored identically, and the graph above it is untouched — so a test that found two
        // different numbers because it had compared two different nodes would be proving nothing.
        Assert.Equal(bare.Ops[bareIndex].Kernel, stacked.Ops[stackedIndex].Kernel);
        Assert.Equal(bare.Ops[bareIndex].Parameters, stacked.Ops[stackedIndex].Parameters);
    }

    /// <summary>The plan a noise compiles to, with or without a layer composited beneath it.</summary>
    /// <remarks>
    ///     ⚠ The checker is added to the model <em>first</em>, because that is what puts its op ahead
    ///     of the noise's — which is an artist adding a layer under the one they are working on.
    /// </remarks>
    static (TexturePlan Plan, int Index) Noise(bool beneath) {
        NodeGraphModel graph = new();
        GraphNode? checker = beneath ? graph.Add("Source/Checker") : null;
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        if (checker is not null) {
            var blend = graph.Add("Colour/Blend");

            graph.Connect(new(checker.Id, "Out"), new(blend.Id, "Background"));
            graph.Connect(new(noise.Id, "Out"), new(blend.Id, "Foreground"));
            graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));
        } else {
            graph.Connect(new(noise.Id, "Out"), new(output.Id, "Input"));
        }

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = Seed
        }.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;
        var index = -1;

        for (var op = 0; op < plan.Ops.Length; op++) {
            if (string.Equals(plan.Ops[op].Kernel, "Noise", StringComparison.Ordinal)) {
                Assert.Equal(-1, index);

                index = op;
            }
        }

        Assert.True(index >= 0, "No Noise op in the plan, so there is no seed to read.");

        return (plan, index);
    }
}
