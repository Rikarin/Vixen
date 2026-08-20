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
///         ⚠ <b>A slot nothing writes is a graph that draws one ribbon.</b> Until the library has a
///         node that writes a custom attribute, the slot's value is reachable only from a graph built
///         in code — which is a real gap and not this node's to close.
///     </para>
/// </remarks>
[Node("Vfx/Output/Ribbon", Summary = "A strip through the particles that share a custom attribute.")]
public sealed partial class RibbonOutputNode : VfxNode {
    /// <summary>Where the blocks connect.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Which custom attribute holds the strip identifier.</summary>
    [Input(Name = "Slot", Default = [0f])]
    public Scalar Slot;

    /// <inheritdoc />
    protected internal override void Contribute(VfxGraphBuilder builder) =>
        builder.Renderer = VfxRenderer.Ribbon((int)Number("Slot"));
}
