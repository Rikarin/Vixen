// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     Attributes a graph declares for itself, which is where the closed set stops being closed.
/// </summary>
/// <remarks>
///     <para>
///         The whole design is one sentence: <b>a name to the author, a slot to everything else</b>.
///         The operations reference a slot, the storage is allocated by slot, and the emitted shader
///         declares its buffers in slot order — so nothing looks a name up at run time and the two
///         backends cannot come to different conclusions, because neither of them decides.
///     </para>
///     <para>
///         What this deliberately does <em>not</em> buy is an arbitrary expression over a custom
///         attribute. That needs a node graph and a lowering to add/multiply/select, which is the
///         cost the closed opcode set was chosen to avoid. What it buys is the three things that make
///         storage useful without one: write it at birth, draw it at random, and animate it over a
///         life.
///     </para>
/// </remarks>
public class VfxCustomAttributeTests {
    static VfxCustomAttribute[] One(VfxAttributeType type = VfxAttributeType.Float) =>
        [new("charge", type)];

    // --- The mapping -------------------------------------------------------

    [Fact]
    public void A_slot_is_where_the_attribute_was_declared() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            8,
            customs: [new("mass", VfxAttributeType.Float), new("tint", VfxAttributeType.Float4)]
        );

        Assert.Equal(0, graph.SlotOf("mass"));
        Assert.Equal(1, graph.SlotOf("tint"));
        Assert.Equal(-1, graph.SlotOf("nothing"));
    }

    /// <summary>Storage is per slot, at the width the slot was declared with.</summary>
    [Fact]
    public void Storage_is_as_wide_as_the_declaration() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetCustom, new Vector4(1f, 2f, 3f, 4f)) { Slot = 1 }],
            [],
            16,
            customs: [new("mass", VfxAttributeType.Float), new("axis", VfxAttributeType.Float3)]
        );

        using var system = new VfxSystem(graph);

        Assert.Equal(2, system.Particles.CustomCount);
        Assert.Equal(1, system.Particles.Lanes(0));
        Assert.Equal(3, system.Particles.Lanes(1));
        Assert.Equal(16, system.Particles.Custom(0).Length);
        Assert.Equal(48, system.Particles.Custom(1).Length);
    }

    // --- What the operations do -------------------------------------------

    [Fact]
    public void A_constant_reaches_every_lane() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(3)],
            [new(VfxOpcode.SetCustom, new Vector4(7f, 8f, 9f, 0f))],
            [],
            8,
            customs: One(VfxAttributeType.Float3)
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var values = system.Particles.Custom(0);

        for (var particle = 0; particle < system.Count; particle++) {
            Assert.Equal(7f, values[particle * 3]);
            Assert.Equal(8f, values[(particle * 3) + 1]);
            Assert.Equal(9f, values[(particle * 3) + 2]);
        }
    }

    /// <summary>
    ///     A random custom draws a different value per lane, not one value in three places.
    /// </summary>
    /// <remarks>
    ///     One salt per lane is what makes that true. Sharing a salt across lanes gives an axis that
    ///     is always a diagonal and a colour that is always grey — a correlation that reads as art
    ///     direction and is not.
    /// </remarks>
    [Fact]
    public void A_random_custom_draws_each_lane_separately() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(64)],
            [new(VfxOpcode.RandomCustom, new Vector4(0f, 0f, 0f, 0f)) { B = new(1f, 1f, 1f, 1f) }],
            [],
            128,
            customs: One(VfxAttributeType.Float3)
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var values = system.Particles.Custom(0);
        var identical = 0;

        for (var particle = 0; particle < system.Count; particle++) {
            var x = values[particle * 3];
            var y = values[(particle * 3) + 1];

            Assert.InRange(x, 0f, 1f);

            if (x == y) {
                identical++;
            }
        }

        Assert.Equal(0, identical);
    }

    [Fact]
    public void A_custom_follows_its_life() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetLifetime, new Vector4(1f, 1f, 0f, 0f)), new(VfxOpcode.SetCustom, Vector4.Zero)],
            [new(VfxOpcode.CustomOverLife, new Vector4(0f, 0f, 0f, 0f)) { B = new(10f, 0f, 0f, 0f) }],
            8,
            customs: One()
        );

        using var system = new VfxSystem(graph);

        system.Step(1f / 60f);
        Assert.Equal(0f, system.Particles.Custom(0)[0]);

        // Half a lifetime in, and the value is half way. `Step` updates before it spawns, so the
        // first of these is the step that gives the particle its age at all.
        for (var step = 0; step < 30; step++) {
            system.Step(1f / 60f);
        }

        Assert.Equal(5f, system.Particles.Custom(0)[0], 1);
    }

    /// <summary>
    ///     A custom attribute travels with its particle when a swap-removal moves one.
    /// </summary>
    /// <remarks>
    ///     The failure this catches has no symptom: the particle moved into the hole keeps somebody
    ///     else's charge and goes on looking exactly like a particle. It is the same rule every
    ///     built-in follows, and the reason to test it here is that custom storage is a second array
    ///     list that a copy could quietly not know about.
    /// </remarks>
    [Fact]
    public void A_custom_survives_a_particle_being_reaped() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(120f)],
            [
                new(VfxOpcode.SetLifetime, new Vector4(0.2f, 1f, 0f, 0f)),
                new(VfxOpcode.RandomCustom, new Vector4(100f, 0f, 0f, 0f)) { B = new(200f, 0f, 0f, 0f) }
            ],
            [],
            256,
            customs: One()
        );

        using var system = new VfxSystem(graph, seed: 11);

        for (var step = 0; step < 120; step++) {
            system.Step(1f / 60f);
        }

        Assert.True(system.Count > 0);

        // Every live particle's value is one this graph could have drawn. A copy that missed the
        // custom arrays would leave zeroes behind as particles died.
        var values = system.Particles.Custom(0);

        for (var particle = 0; particle < system.Count; particle++) {
            Assert.InRange(values[particle], 100f, 200f);
        }
    }

    // --- What Compile refuses ----------------------------------------------

    [Fact]
    public void A_name_that_is_not_an_identifier_is_refused() {
        var error = Assert.Throws<ArgumentException>(
            () => VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [new(VfxOpcode.SetPosition, Vector4.Zero)],
                [],
                8,
                customs: [new("particle size", VfxAttributeType.Float)]
            )
        );

        Assert.Contains("identifier", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_declared_twice_is_refused() {
        Assert.Throws<ArgumentException>(
            () => VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [new(VfxOpcode.SetPosition, Vector4.Zero)],
                [],
                8,
                customs: [new("mass", VfxAttributeType.Float), new("mass", VfxAttributeType.Float4)]
            )
        );
    }

    [Fact]
    public void An_unsigned_custom_is_refused() {
        Assert.Throws<ArgumentException>(
            () => VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [new(VfxOpcode.SetPosition, Vector4.Zero)],
                [],
                8,
                customs: [new("index", VfxAttributeType.UInt)]
            )
        );
    }

    /// <summary>An operation naming a slot the graph does not have is refused at compile time.</summary>
    /// <remarks>
    ///     Not at run time, where it would be an index out of range in the middle of a sweep — and not
    ///     ignored, where it would be an initializer that silently did nothing.
    /// </remarks>
    [Fact]
    public void A_slot_the_graph_does_not_have_is_refused() {
        var error = Assert.Throws<ArgumentException>(
            () => VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [new(VfxOpcode.SetCustom, Vector4.One) { Slot = 3 }],
                [],
                8,
                customs: One()
            )
        );

        Assert.Contains("slot 3", error.Message, StringComparison.Ordinal);
    }

    // --- And the other backend ---------------------------------------------

    /// <summary>
    ///     The shader declares one buffer per touched slot, under the name the author gave it.
    /// </summary>
    /// <remarks>
    ///     The name is why <c>Compile</c> insists on an identifier: a host binds by it, and a
    ///     custom attribute called "particle size" would compile on the CPU and emit a shader that
    ///     does not parse — a failure a long way from its cause.
    /// </remarks>
    [Fact]
    public void The_shader_binds_a_custom_by_its_name() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f)),
                new(VfxOpcode.RandomCustom, new Vector4(0f, 0f, 0f, 0f)) { B = new(1f, 0f, 0f, 0f) },
                new(VfxOpcode.SetCustom, new Vector4(1f, 0f, 0f, 1f)) { Slot = 1 }
            ],
            [new(VfxOpcode.CustomOverLife, new Vector4(1f, 0f, 0f, 1f)) { B = Vector4.Zero, Slot = 1 }],
            64,
            customs: [new("charge", VfxAttributeType.Float), new("stain", VfxAttributeType.Float4)]
        );

        var shader = VfxShaderEmitter.Emit(graph, "Charged");

        Assert.Contains("var charge: RWBuffer<float>", shader.Source, StringComparison.Ordinal);
        Assert.Contains("var stain: RWBuffer<float4>", shader.Source, StringComparison.Ordinal);

        // And the bindings report the slot, so a host that knows the graph knows which is which
        // without matching on a string.
        Assert.Equal(0, Assert.Single(shader.Bindings, binding => binding.Name == "charge").Slot);
        Assert.Equal(1, Assert.Single(shader.Bindings, binding => binding.Name == "stain").Slot);
        Assert.All(
            shader.Bindings.Where(binding => binding.Attribute != VfxAttribute.None),
            binding => Assert.Equal(-1, binding.Slot)
        );
    }

    /// <summary>A declared attribute nothing touches is storage on the CPU and no descriptor here.</summary>
    [Fact]
    public void A_custom_no_operation_touches_binds_nothing() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16,
            customs: One()
        );

        var shader = VfxShaderEmitter.Emit(graph, "Unused");

        Assert.DoesNotContain(shader.Bindings, binding => binding.Name == "charge");

        // The CPU still allocates it: a graph that declares an attribute and writes it from outside
        // the operation list is a legitimate thing, and the buffer is where that value would go.
        using var system = new VfxSystem(graph);
        Assert.Equal(1, system.Particles.CustomCount);
    }
}
