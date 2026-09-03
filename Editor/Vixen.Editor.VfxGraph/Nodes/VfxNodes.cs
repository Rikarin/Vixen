// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Vfx;

namespace Vixen.Editor.VfxGraph.Nodes;

/// <summary>The effect itself: how many particles it may have, and how they are drawn.</summary>
/// <remarks>
///     One per graph. It is not a block — it contributes no operation — and it is here because a
///     capacity and a renderer are properties of the effect that have to be authored somewhere, and a
///     node is the only place a graph has.
/// </remarks>
[Node("Vfx/Effect", Summary = "The effect's capacity and how its particles are drawn.")]
public sealed partial class EffectNode : VfxNode {
    /// <summary>Where the blocks feeding this effect connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>The most particles that may be alive at once.</summary>
    [Input(Name = "Capacity", Default = [1024f])]
    public Scalar Capacity;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        builder.Capacity = Math.Max(1, (int)Number("Capacity"));
        builder.Renderer ??= VfxRenderer.SortedBillboard;
    }
}

/// <summary>Everything at once, when the effect starts.</summary>
[Node("Vfx/Spawn/Burst", Summary = "A number of particles, all at once.")]
public sealed partial class BurstNode : VfxNode {
    /// <summary>How many.</summary>
    [Input(Name = "Count", Default = [64f])]
    public Scalar Count;

    /// <summary>Where the next block connects.</summary>
    [Output(Name = "Out")]
    public Flow Out;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Spawners.Add(VfxSpawner.Burst((int)Number("Count")));
}

/// <summary>A steady stream.</summary>
[Node("Vfx/Spawn/Rate", Summary = "Particles a second, continuously.")]
public sealed partial class RateNode : VfxNode {
    /// <summary>How many a second.</summary>
    [Input(Name = "Rate", Default = [60f])]
    public Scalar Rate;

    /// <summary>Where the next block connects.</summary>
    [Output(Name = "Out")]
    public Flow Out;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Spawners.Add(VfxSpawner.AtRate(Number("Rate")));
}

/// <summary>The block every initializer and updater shares: an ordering wire in and one out.</summary>
/// <remarks>
///     The wire carries nothing. It says which block runs first, and the framework's topological
///     order is what turns it into a list — which is also why a cycle in it is refused as it is made.
/// </remarks>
public abstract class VfxBlockNode : VfxNode {
    /// <summary>Where the previous block connects.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Where the next one does.</summary>
    [Output(Name = "Out")]
    public Flow Out;
}

/// <summary>Particles start somewhere inside a box.</summary>
[Node("Vfx/Initialize/Position in Box", Summary = "Uniform inside an axis-aligned box.")]
public sealed partial class PositionInBoxNode : VfxBlockNode {
    /// <summary>The low corner.</summary>
    [Input(Name = "Minimum", Default = [-1f, -1f, -1f])]
    public Float3 Minimum;

    /// <summary>The high corner.</summary>
    [Input(Name = "Maximum", Default = [1f, 1f, 1f])]
    public Float3 Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.PositionInBox, Vector("Minimum")) { B = Vector("Maximum") });
}

/// <summary>Particles start somewhere inside a sphere.</summary>
[Node("Vfx/Initialize/Position in Sphere", Summary = "Uniform by volume inside a sphere.")]
public sealed partial class PositionInSphereNode : VfxBlockNode {
    /// <summary>The centre.</summary>
    [Input(Name = "Centre", Default = [0f, 0f, 0f])]
    public Float3 Centre;

    /// <summary>The radius.</summary>
    [Input(Name = "Radius", Default = [1f])]
    public Scalar Radius;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var centre = Vector("Centre");

        builder.Initializers.Add(
            new(VfxOpcode.PositionInSphere, new Vector4(centre.X, centre.Y, centre.Z, Number("Radius")))
        );
    }
}

/// <summary>Particles start moving in a random direction.</summary>
[Node("Vfx/Initialize/Random Velocity", Summary = "A random direction, at a speed in a range.")]
public sealed partial class RandomVelocityNode : VfxBlockNode {
    /// <summary>The slowest.</summary>
    [Input(Name = "Minimum", Default = [1f])]
    public Scalar Minimum;

    /// <summary>The fastest.</summary>
    [Input(Name = "Maximum", Default = [3f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.VelocityRandomDirection, new Vector4(Number("Minimum"), Number("Maximum"), 0f, 0f)));
}

/// <summary>Particles start moving in a fixed direction.</summary>
[Node("Vfx/Initialize/Set Velocity", Summary = "One velocity, for every particle.")]
public sealed partial class SetVelocityNode : VfxBlockNode {
    /// <summary>The velocity.</summary>
    [Input(Name = "Velocity", Default = [0f, 0f, 0f])]
    public Float3 Velocity;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetVelocity, Vector("Velocity")));
}

/// <summary>How long particles live.</summary>
[Node("Vfx/Initialize/Lifetime", Summary = "A lifetime in a range, in seconds.")]
public sealed partial class LifetimeNode : VfxBlockNode {
    /// <summary>The shortest.</summary>
    [Input(Name = "Minimum", Default = [1f])]
    public Scalar Minimum;

    /// <summary>The longest.</summary>
    [Input(Name = "Maximum", Default = [2f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetLifetime, new Vector4(Number("Minimum"), Number("Maximum"), 0f, 0f)));
}

/// <summary>How big particles start.</summary>
[Node("Vfx/Initialize/Size", Summary = "A size in a range.")]
public sealed partial class SizeNode : VfxBlockNode {
    /// <summary>The smallest.</summary>
    [Input(Name = "Minimum", Default = [0.1f])]
    public Scalar Minimum;

    /// <summary>The largest.</summary>
    [Input(Name = "Maximum", Default = [0.3f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetSize, new Vector4(Number("Minimum"), Number("Maximum"), 0f, 0f)));
}

/// <summary>What colour particles start.</summary>
[Node("Vfx/Initialize/Colour", Summary = "One colour, for every particle.")]
public sealed partial class ColourNode : VfxBlockNode {
    /// <summary>The colour.</summary>
    [Input(Name = "Colour", Default = [1f, 1f, 1f, 1f])]
    public Float4 Colour;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetColour, Vector("Colour")));
}

/// <summary>A constant acceleration.</summary>
[Node("Vfx/Update/Gravity", Summary = "An acceleration, every step.")]
public sealed partial class GravityNode : VfxBlockNode {
    /// <summary>The acceleration.</summary>
    [Input(Name = "Acceleration", Default = [0f, -9.81f, 0f])]
    public Float3 Acceleration;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.Gravity, Vector("Acceleration")));
}

/// <summary>Velocity decaying.</summary>
[Node("Vfx/Update/Drag", Summary = "Velocity decaying, per second.")]
public sealed partial class DragNode : VfxBlockNode {
    /// <summary>The coefficient.</summary>
    [Input(Name = "Drag", Default = [0.5f])]
    public Scalar Drag;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.Drag, new Vector4(Number("Drag"), 0f, 0f, 0f)));
}

/// <summary>Position following velocity.</summary>
[Node("Vfx/Update/Integrate", Summary = "Position moves by velocity. Nearly every graph wants one.")]
public sealed partial class IntegrateNode : VfxBlockNode {
    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.Integrate));
}

/// <summary>A pull towards a point, or a push from it.</summary>
[Node("Vfx/Update/Attract", Summary = "Towards a point, or away from it if negative.")]
public sealed partial class AttractNode : VfxBlockNode {
    /// <summary>The point.</summary>
    [Input(Name = "Centre", Default = [0f, 0f, 0f])]
    public Float3 Centre;

    /// <summary>How strongly, at the centre. Negative repels.</summary>
    [Input(Name = "Strength", Default = [5f])]
    public Scalar Strength;

    /// <summary>How far it reaches. Zero reaches everywhere.</summary>
    [Input(Name = "Radius", Default = [0f])]
    public Scalar Radius;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var centre = Vector("Centre");

        builder.Updaters.Add(
            new(VfxOpcode.Attract, new Vector4(centre.X, centre.Y, centre.Z, Number("Strength"))) {
                B = new(Number("Radius"), 0f, 0f, 0f)
            }
        );
    }
}

/// <summary>Curl noise, which swirls rather than piling particles into its sinks.</summary>
[Node("Vfx/Update/Turbulence", Summary = "A drifting curl-noise field.")]
public sealed partial class TurbulenceNode : VfxBlockNode {
    /// <summary>How fine the field is, per axis.</summary>
    [Input(Name = "Frequency", Default = [0.3f, 0.3f, 0.3f])]
    public Float3 Frequency;

    /// <summary>How strongly it pushes.</summary>
    [Input(Name = "Strength", Default = [4f])]
    public Scalar Strength;

    /// <summary>How fast the field drifts.</summary>
    [Input(Name = "Drift", Default = [0.5f])]
    public Scalar Drift;

    /// <summary>How many octaves. One is visibly axis-aligned; three are not.</summary>
    [Input(Name = "Octaves", Default = [3f])]
    public Scalar Octaves;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var frequency = Vector("Frequency");

        builder.Updaters.Add(
            new(VfxOpcode.Turbulence, new Vector4(frequency.X, frequency.Y, frequency.Z, Number("Strength"))) {
                B = new(Number("Drift"), Number("Octaves"), 0f, 0f)
            }
        );
    }
}

/// <summary>A floor, a wall, or any other plane particles bounce off.</summary>
[Node("Vfx/Update/Collide Plane", Summary = "Keeps particles on the front side of a plane.")]
public sealed partial class CollidePlaneNode : VfxBlockNode {
    /// <summary>Which way is out.</summary>
    [Input(Name = "Normal", Default = [0f, 1f, 0f])]
    public Float3 Normal;

    /// <summary>How far along the normal the plane is.</summary>
    [Input(Name = "Distance", Default = [0f])]
    public Scalar Distance;

    /// <summary>How much of the approach comes back.</summary>
    [Input(Name = "Bounce", Default = [0.5f])]
    public Scalar Bounce;

    /// <summary>How much of the slide is lost.</summary>
    [Input(Name = "Friction", Default = [0.2f])]
    public Scalar Friction;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var normal = Vector("Normal");

        builder.Updaters.Add(
            new(VfxOpcode.CollidePlane, new Vector4(normal.X, normal.Y, normal.Z, Number("Distance"))) {
                B = new(Number("Bounce"), Number("Friction"), 0f, 0f)
            }
        );
    }
}

/// <summary>Size following age.</summary>
[Node("Vfx/Update/Size over Life", Summary = "From one size at birth to another at death.")]
public sealed partial class SizeOverLifeNode : VfxBlockNode {
    /// <summary>The size at birth.</summary>
    [Input(Name = "Start", Default = [0.3f])]
    public Scalar Start;

    /// <summary>The size at death.</summary>
    [Input(Name = "End", Default = [0f])]
    public Scalar End;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.SizeOverLife, new Vector4(Number("Start"), Number("End"), 0f, 0f)));
}

/// <summary>Colour following age.</summary>
[Node("Vfx/Update/Colour over Life", Summary = "From one colour at birth to another at death.")]
public sealed partial class ColourOverLifeNode : VfxBlockNode {
    /// <summary>The colour at birth.</summary>
    [Input(Name = "Start", Default = [1f, 1f, 1f, 1f])]
    public Float4 Start;

    /// <summary>The colour at death.</summary>
    [Input(Name = "End", Default = [1f, 1f, 1f, 0f])]
    public Float4 End;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.ColourOverLife, Vector("Start")) { B = Vector("End") });
}

/// <summary>Particles drawn as camera-facing quads.</summary>
[Node("Vfx/Output/Billboard", Summary = "A camera-facing quad per particle.")]
public sealed partial class BillboardOutputNode : VfxNode {
    /// <summary>Where the blocks connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Whether to sort back to front, which alpha blending needs.</summary>
    [Input(Name = "Sorted", Default = [1f])]
    public Bool Sorted;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Renderer = Number("Sorted") != 0f ? VfxRenderer.SortedBillboard : VfxRenderer.Billboard;
}

/// <summary>Particles that light the scene rather than being drawn in it.</summary>
[Node("Vfx/Output/Light", Summary = "A point light per particle.")]
public sealed partial class LightOutputNode : VfxNode {
    /// <summary>Where the blocks connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>How bright a particle at full alpha is, in candela.</summary>
    /// <remarks>
    ///     ⚠ <b>Candela, like every other punctual light in the engine, not a multiplier.</b> A
    ///     150 000 lm floodlight is about 12 000 cd, so the default of one is a light that exists and
    ///     changes no pixel. See <c>Photometry</c>.
    /// </remarks>
    [Input(Name = "Intensity", Default = [1f])]
    public Scalar Intensity;

    /// <summary>How far a particle of unit size reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>Of <i>unit</i> size, and this is the number that catches people.</b> The reach a
    ///     particle gets is this times its own size, so that a size-over-life curve shrinks the pool
    ///     of light with the spark — which means an effect whose particles are two centimetres across
    ///     reaches four <em>centimetres</em> at the default, and lights nothing in the level at all.
    ///     Particles of a few centimetres want a range in the hundreds; see
    ///     <c>ParticleLights.Collect</c>, which measures what the difference is worth.
    /// </remarks>
    [Input(Name = "Range", Default = [4f])]
    public Scalar Range;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Renderer = VfxRenderer.Light(Number("Intensity"), Number("Range"));
}

/// <summary>Velocity turned about an axis, which is what makes a whirl rather than a pile.</summary>
/// <remarks>
///     <b><see cref="AttractNode" />'s sibling and its opposite failure.</b> A pull towards a point
///     ends with every particle at the point; a turn about an axis never converges, which is what a
///     tornado, a whirlpool or a portal wants. The opcode has shipped since the field set was written
///     and had no node, so the only way to reach it was to build the graph in code.
/// </remarks>
[Node("Vfx/Update/Vortex", Summary = "Velocity turned about an axis. A whirl rather than a pile.")]
public sealed partial class VortexNode : VfxBlockNode {
    /// <summary>A point the axis passes through.</summary>
    [Input(Name = "Centre", Default = [0f, 0f, 0f])]
    public Float3 Centre;

    /// <summary>Which way the axis points.</summary>
    [Input(Name = "Axis", Default = [0f, 1f, 0f])]
    public Float3 Axis;

    /// <summary>How hard the turn is. Negative turns the other way.</summary>
    [Input(Name = "Strength", Default = [5f])]
    public Scalar Strength;

    /// <summary>How far it reaches. Zero reaches everywhere.</summary>
    [Input(Name = "Radius", Default = [0f])]
    public Scalar Radius;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var centre = Vector("Centre");
        var axis = Vector("Axis");

        builder.Updaters.Add(
            new(VfxOpcode.Vortex, new Vector4(centre.X, centre.Y, centre.Z, Number("Strength"))) {
                B = new(axis.X, axis.Y, axis.Z, Number("Radius"))
            }
        );
    }
}

/// <summary>A ball particles bounce off, seen from outside.</summary>
/// <remarks>
///     ⚠ <b>Solid and outward-facing.</b> Keeping particles <i>inside</i> a sphere is a different
///     operation rather than a flag on this one, and the opcode set does not have it — see
///     <see cref="VfxOpcode.CollideSphere" />.
/// </remarks>
[Node("Vfx/Update/Collide Sphere", Summary = "Keeps particles outside a sphere.")]
public sealed partial class CollideSphereNode : VfxBlockNode {
    /// <summary>Where the sphere is.</summary>
    [Input(Name = "Centre", Default = [0f, 0f, 0f])]
    public Float3 Centre;

    /// <summary>How big it is.</summary>
    [Input(Name = "Radius", Default = [1f])]
    public Scalar Radius;

    /// <summary>How much of the approach comes back.</summary>
    [Input(Name = "Bounce", Default = [0.5f])]
    public Scalar Bounce;

    /// <summary>How much of the slide is lost.</summary>
    [Input(Name = "Friction", Default = [0.2f])]
    public Scalar Friction;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var centre = Vector("Centre");

        builder.Updaters.Add(
            new(VfxOpcode.CollideSphere, new Vector4(centre.X, centre.Y, centre.Z, Number("Radius"))) {
                B = new(Number("Bounce"), Number("Friction"), 0f, 0f)
            }
        );
    }
}

/// <summary>Particles drawn as instances of a mesh.</summary>
/// <remarks>
///     <para>
///         <b>Which mesh is the emitter's, not this node's.</b> A <c>.vxvfx</c> says how particles
///         move; the same debris effect is worn by the rock, the crate and the glass, so the asset
///         sits on <c>VfxEmitter.Mesh</c> beside the effect reference. This node says only that the
///         particles are geometry rather than quads, and which way that geometry is turned.
///     </para>
///     <para>
///         ⚠ <b>The mesh's local +Y is the axis that gets aligned</b>, which is the same axis a
///         velocity-aligned billboard stretches along. A model built the other way up is a rotation
///         in the asset rather than a flag here.
///     </para>
/// </remarks>
[Node("Vfx/Output/Mesh", Summary = "An instance of a mesh per particle. The emitter says which mesh.")]
public sealed partial class MeshOutputNode : VfxNode {
    /// <summary>Where the blocks connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Whether each instance's +Y follows its own velocity.</summary>
    /// <remarks>What a shard thrown from an explosion wants, and it wins over <see cref="Axis" />.</remarks>
    [Input(Name = "Align to Velocity", Default = [0f])]
    public Bool AlignToVelocity;

    /// <summary>A fixed world axis to align +Y to, or zero for none.</summary>
    /// <remarks>
    ///     Zero — the default — leaves the instances turned to face the camera about their own +Y,
    ///     which is what an unremarkable chunk of debris wants. A three-way choice expressed as two
    ///     ports because a node port is lanes of float and has no room for a name.
    /// </remarks>
    [Input(Name = "Axis", Default = [0f, 0f, 0f])]
    public Float3 Axis;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var lanes = Vector("Axis");
        var axis = new Vector3(lanes.X, lanes.Y, lanes.Z);

        builder.Renderer = Number("Align to Velocity") != 0f
            ? VfxRenderer.Instanced(VfxBillboardAlignment.Velocity)
            : axis.LengthSquared() > 0f
                ? VfxRenderer.Instanced(VfxBillboardAlignment.FixedAxis, axis)
                : VfxRenderer.Instanced();
    }
}

/// <summary>Particles joined into strips, oldest first.</summary>
/// <remarks>
///     <para>
///         <b>The one renderer that needs particles to know about each other.</b> Which strip a
///         particle belongs to is a custom attribute — <see cref="Slot" /> names it — and where it
///         sits within one is its age, which the runtime already keeps. Particles sharing a value are
///         one ribbon.
///     </para>
///     <para>
///         ⚠ <b>Always sorted by age, whatever a billboard node's sort port would have said.</b> That
///         is the ribbon's own order rather than a drawing preference: a strip drawn in the order the
///         particles happen to sit in the buffer is a tangle.
///     </para>
///     <para>
///         ⚠ <b>It names the attribute rather than numbering it.</b> A slot is where a name landed in
///         the graph's declaration list, and that position moves the moment somebody adds a block
///         above — so a number typed here would have silently pointed at a different attribute. The
///         name is resolved to a slot after every block has contributed, which is also why an output
///         dropped on the canvas before the block that writes its attribute still compiles.
///     </para>
///     <para>
///         ⚠ <b>An attribute nothing writes is refused.</b> Storage nothing has written is zero for
///         every particle, so every particle would be in one strip — one tangle rather than the many
///         ribbons that were drawn, and no error to search for. <c>VfxGraphBuilder.SlotOf</c> says so
///         instead.
///     </para>
/// </remarks>
[Node("Vfx/Output/Ribbon", Summary = "A strip through the particles that share a custom attribute.")]
public sealed partial class RibbonOutputNode : VfxNode {
    /// <summary>Where the blocks connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Which custom attribute holds the strip identifier.</summary>
    [Setting(Name = "Attribute", Summary = "The custom attribute holding the strip. Particles sharing a value are one ribbon.")]
    public string Attribute = "";

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        // The slot is not looked up here: see VfxGraphBuilder.RibbonAttribute.
        builder.RibbonAttribute = Text("Attribute");
        builder.Renderer = VfxRenderer.Ribbon(0);
    }
}

/// <summary>The block every custom-attribute node shares: a name and a width.</summary>
/// <remarks>
///     <para>
///         <b>The name is a <c>[Setting]</c>, not a port, and that is the gap this closes.</b> A port
///         is lanes of float and a custom attribute is a <i>name</i> — it names a binding in the
///         emitted shader and a host binds by it — so until the node graph could describe a
///         string-valued field, <c>SetCustom</c>, <c>RandomCustom</c> and <c>CustomOverLife</c> were
///         reachable only from a graph built in code.
///     </para>
///     <para>
///         ⚠ <b>An attribute exists because something writes it.</b> There is no declaration node and
///         no list to keep in step: the first block to name one declares it, and its slot is where it
///         landed. That is the rule the built-in attributes already follow — see
///         <c>VfxCompiledGraph</c>, whose storage is derived from what the operations touch rather
///         than from a declaration an author has to remember.
///     </para>
///     <para>
///         ⚠ <b>Lanes is one, three or four.</b> A custom attribute is a float, a float3 or a float4;
///         two is refused rather than rounded, because <c>VfxAttributes.Lanes</c> would silently make
///         it one.
///     </para>
/// </remarks>
public abstract class VfxCustomNode : VfxBlockNode {
    /// <summary>What the attribute is called. An identifier, because the shader binds by it.</summary>
    [Setting(Name = "Attribute", Summary = "The attribute's name. An identifier — the emitted shader binds by it.")]
    public string Attribute = "";

    /// <summary>How wide it is: one, three or four floats.</summary>
    [Input(Name = "Lanes", Default = [1f])]
    public Int Lanes;
}

/// <summary>A custom attribute set to one value for every particle.</summary>
[Node("Vfx/Initialize/Set Custom", Summary = "A custom attribute, the same for every particle.")]
public sealed partial class SetCustomNode : VfxCustomNode {
    /// <summary>The value. Only the first <c>Lanes</c> of it are stored.</summary>
    [Input(Name = "Value", Default = [0f, 0f, 0f, 0f])]
    public Float4 Value;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetCustom, 0u, Vector("Value"), Vector4.Zero, Custom(builder)));
}

/// <summary>A custom attribute drawn uniformly between two values, lane by lane.</summary>
/// <remarks>
///     What a ribbon wants for its strip identifier — a handful of strips, chosen at birth and never
///     changed — and what a per-particle seed for anything else looks like.
/// </remarks>
[Node("Vfx/Initialize/Random Custom", Summary = "A custom attribute, uniform between two values.")]
public sealed partial class RandomCustomNode : VfxCustomNode {
    /// <summary>The low end.</summary>
    [Input(Name = "Minimum", Default = [0f, 0f, 0f, 0f])]
    public Float4 Minimum;

    /// <summary>The high end.</summary>
    [Input(Name = "Maximum", Default = [1f, 1f, 1f, 1f])]
    public Float4 Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(
            new(VfxOpcode.RandomCustom, 0u, Vector("Minimum"), Vector("Maximum"), Custom(builder))
        );
}

/// <summary>A custom attribute following age, from one value at birth to another at death.</summary>
/// <remarks>
///     ⚠ <b>An updater, so it needs a lifetime to count against.</b> A graph with no
///     <c>Vfx/Initialize/Lifetime</c> block has immortal particles and no age to interpolate, and
///     <c>VfxCompiledGraph.Compile</c> refuses it in a sentence rather than producing an effect that
///     sits at its birth value forever.
/// </remarks>
[Node("Vfx/Update/Custom over Life", Summary = "A custom attribute, from one value at birth to another at death.")]
public sealed partial class CustomOverLifeNode : VfxCustomNode {
    /// <summary>The value at birth.</summary>
    [Input(Name = "Start", Default = [0f, 0f, 0f, 0f])]
    public Float4 Start;

    /// <summary>The value at death.</summary>
    [Input(Name = "End", Default = [1f, 1f, 1f, 1f])]
    public Float4 End;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.CustomOverLife, 0u, Vector("Start"), Vector("End"), Custom(builder)));
}

/// <summary>Particles start at one point.</summary>
/// <remarks>
///     The degenerate emitter, and the one an author reaches for first: a shape block is what turns a
///     point into a volume, and until there was one of these the only way to author a point source was
///     a box with both corners in the same place.
/// </remarks>
[Node("Vfx/Initialize/Position", Summary = "One point, for every particle.")]
public sealed partial class PositionNode : VfxBlockNode {
    /// <summary>The point, in the effect's own space.</summary>
    [Input(Name = "Position", Default = [0f, 0f, 0f])]
    public Float3 Position;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(new(VfxOpcode.SetPosition, Vector("Position")));
}

/// <summary>Particles start moving somewhere inside a cone.</summary>
/// <remarks>
///     ⚠ <b>The jet, the fountain and the muzzle flash — and the opcode for it had shipped in both
///     backends with nothing able to author it.</b> <c>Vfx/Initialize/Random Velocity</c> is this with
///     a half-angle of π, which is a sphere, so every directional emitter in the library was a sphere
///     somebody had aimed with a Gravity block.
/// </remarks>
[Node("Vfx/Initialize/Velocity in Cone", Summary = "A random direction inside a cone, at a speed in a range.")]
public sealed partial class VelocityInConeNode : VfxBlockNode {
    /// <summary>Which way the cone points. Normalized by the simulation, so any length will do.</summary>
    [Input(Name = "Axis", Default = [0f, 1f, 0f])]
    public Float3 Axis;

    /// <summary>Half the cone's opening, in radians. π is a sphere.</summary>
    [Input(Name = "Angle", Default = [0.4f])]
    public Scalar Angle;

    /// <summary>The slowest.</summary>
    [Input(Name = "Minimum", Default = [1f])]
    public Scalar Minimum;

    /// <summary>The fastest.</summary>
    [Input(Name = "Maximum", Default = [3f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) {
        var axis = Vector("Axis");

        builder.Initializers.Add(
            new(VfxOpcode.VelocityInCone, new Vector4(axis.X, axis.Y, axis.Z, Number("Angle"))) {
                B = new(Number("Minimum"), Number("Maximum"), 0f, 0f)
            }
        );
    }
}

/// <summary>What roll particles start at.</summary>
/// <remarks>
///     ⚠ <b>Roll only, and that is the whole rotation model rather than a first instalment of one.</b>
///     A particle is a billboard or a ribbon segment, so it has one angle about the view axis rather
///     than an orientation — <c>VfxAttribute.Rotation</c> is a single float and <c>VfxGeometry</c>
///     spins the expanded quad by it. A mesh output reading a full orientation is a different feature.
/// </remarks>
[Node("Vfx/Initialize/Rotation", Summary = "A roll in a range, in radians.")]
public sealed partial class RotationNode : VfxBlockNode {
    /// <summary>The least.</summary>
    [Input(Name = "Minimum", Default = [0f])]
    public Scalar Minimum;

    /// <summary>The most. Two π is "any angle".</summary>
    [Input(Name = "Maximum", Default = [6.2831855f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(
            new(VfxOpcode.SetRotation, new Vector4(Number("Minimum"), Number("Maximum"), 0f, 0f))
        );
}

/// <summary>How fast particles spin.</summary>
/// <remarks>
///     ⚠ <b>Sets the attribute; it does not apply it.</b> Nothing turns until a
///     <c>Vfx/Update/Rotate</c> integrates it — exactly as a velocity does nothing without
///     <c>Vfx/Update/Integrate</c>, which is the same arrangement and the same first surprise.
/// </remarks>
[Node("Vfx/Initialize/Angular Velocity", Summary = "A spin rate in a range, in radians per second.")]
public sealed partial class AngularVelocityNode : VfxBlockNode {
    /// <summary>The slowest. Negative spins the other way.</summary>
    [Input(Name = "Minimum", Default = [-1f])]
    public Scalar Minimum;

    /// <summary>The fastest.</summary>
    [Input(Name = "Maximum", Default = [1f])]
    public Scalar Maximum;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Initializers.Add(
            new(VfxOpcode.SetAngularVelocity, new Vector4(Number("Minimum"), Number("Maximum"), 0f, 0f))
        );
}

/// <summary>Roll following angular velocity.</summary>
/// <remarks>
///     <c>Vfx/Update/Integrate</c>'s counterpart for the other attribute pair, and parameterless for
///     the same reason: what to advance by is on the particle. A graph with this and no
///     <c>Vfx/Initialize/Angular Velocity</c> advances every particle by zero, which is a still
///     billboard rather than an error.
/// </remarks>
[Node("Vfx/Update/Rotate", Summary = "Roll advances by angular velocity, every step.")]
public sealed partial class RotateNode : VfxBlockNode {
    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Updaters.Add(new(VfxOpcode.Rotate));
}
