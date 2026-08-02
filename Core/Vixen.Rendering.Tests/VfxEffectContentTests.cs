// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Vfx;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The content form of a compiled graph: what a build writes and a game loads.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every failure here is silent, which is the whole reason for the file.</b> A row that
///         drops a field produces an effect that still runs — with a spawn rate of zero, a lifetime of
///         zero, a colour of black — and nothing anywhere reports a missing member. So the assertions
///         are field for field rather than "it round-trips", and the fixture uses a value in every
///         field that is not its default.
///     </para>
///     <para>
///         The serialisation itself is the generator's business and is tested where the generator is.
///         What is tested here is the <em>mapping</em>: two shapes for one thing, written by hand, in
///         a file that has to be edited twice whenever the runtime type gains a member.
///     </para>
/// </remarks>
public class VfxEffectContentTests {
    /// <summary>A graph with something in every field a row carries.</summary>
    /// <remarks>
    ///     ⚠ <b>No value here is a default and no two are equal.</b> A mapping that crossed two fields
    ///     — reading <c>Time</c> into <c>Interval</c>, <c>A</c> into <c>B</c> — is invisible against a
    ///     fixture whose values repeat, and that is the mistake a hand-written mirror actually makes.
    /// </remarks>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(37f), VfxSpawner.Repeating(11, 2.5f, 0.75f)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -2f, -3f, 0f)) { B = new(4f, 5f, 6f, 0f) },
                new(VfxOpcode.SetLifetime, new Vector4(1.5f, 3.25f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.125f, 0.375f, 0f, 0f)),
                new(VfxOpcode.SetColour, new Vector4(0.1f, 0.2f, 0.3f, 0.4f))
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0.5f, -9.81f, 0.25f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.625f, 0f, 0f, 0f)),
                new(VfxOpcode.Integrate)
            ],
            512,
            VfxRenderer.Streak(0.875f)
        );

    /// <summary>Every field of every row survives the trip out and back.</summary>
    [Fact]
    public void A_graph_survives_the_round_trip_field_for_field() {
        var original = Graph();
        var restored = VfxEffectContent.From(original).ToGraph();

        Assert.Equal(original.Capacity, restored.Capacity);

        // Derived rather than stored, and this is what says the derivation still agrees: the
        // attributes come out of what the operations read and write, so a mapping that lost an
        // operation would produce a graph that allocates less storage than the original.
        Assert.Equal(original.Attributes, restored.Attributes);

        Assert.Equal(original.Spawners.Length, restored.Spawners.Length);

        for (var index = 0; index < original.Spawners.Length; index++) {
            Assert.Equal(original.Spawners[index], restored.Spawners[index]);
        }

        Assert.Equal(original.Initializers.Length, restored.Initializers.Length);

        for (var index = 0; index < original.Initializers.Length; index++) {
            Assert.Equal(original.Initializers[index], restored.Initializers[index]);
        }

        Assert.Equal(original.Updaters.Length, restored.Updaters.Length);

        for (var index = 0; index < original.Updaters.Length; index++) {
            Assert.Equal(original.Updaters[index], restored.Updaters[index]);
        }

        Assert.Equal(original.Renderer, restored.Renderer);
    }

    /// <summary>
    ///     The salts come back as they went out, so a shipped effect looks like the authored one.
    /// </summary>
    /// <remarks>
    ///     <b>The one field whose loss would be invisible <em>and</em> would change the picture.</b>
    ///     <c>VfxCompiledGraph.Compile</c> assigns a salt only where one is zero — so a content form
    ///     that dropped them would compile a graph whose every random value is different, which is a
    ///     working effect that does not look like the one somebody authored. Nothing else in this
    ///     file's round trip would notice.
    /// </remarks>
    [Fact]
    public void The_salts_are_carried_rather_than_reassigned() {
        var original = Graph();
        var restored = VfxEffectContent.From(original).ToGraph();

        var salted = 0;

        for (var index = 0; index < original.Initializers.Length; index++) {
            Assert.Equal(original.Initializers[index].Salt, restored.Initializers[index].Salt);

            if (original.Initializers[index].Salt != 0) {
                salted++;
            }
        }

        // And the fixture actually has salted operations in it, or the loop above is vacuous.
        Assert.True(salted > 0, "no initializer in the fixture draws on randomness");
    }

    /// <summary>An effect nothing draws round-trips as one, rather than gaining a renderer.</summary>
    /// <remarks>
    ///     A simulation used to drive something else has no renderer, no colour and no size — storage
    ///     is what is used — so "no renderer" has to survive as a distinct state. A flag and a value
    ///     rather than a nullable, because a chunk has no shape for the latter; this is what says the
    ///     flag is read.
    /// </remarks>
    [Fact]
    public void An_effect_nothing_draws_keeps_no_renderer() {
        var original = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16
        );

        Assert.Null(original.Renderer);

        var content = VfxEffectContent.From(original);

        Assert.False(content.Drawn);
        Assert.Null(content.ToGraph().Renderer);
    }

    /// <summary>A graph's own attributes survive with their slots, which is what an operation indexes.</summary>
    /// <remarks>
    ///     Slot order <i>is</i> declaration order, and an operation carries the slot it was compiled
    ///     with — so a mapping that reordered the customs would leave every ribbon reading another
    ///     attribute's buffer, and the graph would still compile.
    /// </remarks>
    [Fact]
    public void The_custom_attributes_keep_their_slots() {
        var original = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(10f)],
            [new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f))],
            [],
            32,
            VfxRenderer.Ribbon(1),
            [new("strip", VfxAttributeType.Float), new("heat", VfxAttributeType.Float)]
        );

        var restored = VfxEffectContent.From(original).ToGraph();

        Assert.Equal(original.Customs.Length, restored.Customs.Length);
        Assert.Equal(0, restored.SlotOf("strip"));
        Assert.Equal(1, restored.SlotOf("heat"));

        for (var index = 0; index < original.Customs.Length; index++) {
            Assert.Equal(original.Customs[index], restored.Customs[index]);
        }
    }
}
