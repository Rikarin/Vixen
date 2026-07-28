// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>What a particle is drawn as.</summary>
public enum VfxRendererKind {
    /// <summary>A quad, one per particle.</summary>
    Billboard,

    /// <summary>
    ///     An instance of a mesh, one per particle: a transform and a colour rather than geometry.
    /// </summary>
    /// <remarks>
    ///     The mesh itself belongs to whoever draws — this module has no idea what a vertex buffer is.
    ///     What it produces is the per-instance data, which is the only part that depends on the
    ///     particles.
    /// </remarks>
    Mesh,

    /// <summary>
    ///     A strip joining the particles of one ribbon, ordered oldest first.
    /// </summary>
    /// <remarks>
    ///     The one renderer that needs particles to know about each other. Which ribbon a particle
    ///     belongs to is a custom attribute — <see cref="VfxRenderer.RibbonSlot" /> — and where it sits
    ///     within one is its age, which is a built-in the runtime already keeps.
    /// </remarks>
    Ribbon
}

/// <summary>Which way a billboard faces.</summary>
public enum VfxBillboardAlignment {
    /// <summary>Square to the camera. The ordinary case, and what a spark or a puff of smoke wants.</summary>
    Camera,

    /// <summary>
    ///     Stretched along the particle's own velocity, still facing the camera about that axis. What a
    ///     spark or a raindrop wants, and it reads as motion rather than as speed.
    /// </summary>
    Velocity,

    /// <summary>
    ///     Rotating only about a fixed world axis. What a column of smoke or a wall of fire wants —
    ///     the axis is <see cref="VfxRenderer.Axis" />.
    /// </summary>
    FixedAxis
}

/// <summary>The order particles are drawn in.</summary>
/// <remarks>
///     A drawing decision rather than a simulation one, which is why it lives on the renderer. It also
///     costs something — a key per particle and a sort — so the default is the one that costs nothing.
/// </remarks>
public enum VfxSortMode {
    /// <summary>
    ///     Whatever order they are in. Correct for additive blending, where the result does not depend
    ///     on the order, and the only sensible choice there.
    /// </summary>
    None,

    /// <summary>Furthest from the camera first, which is what alpha blending needs.</summary>
    ByDepth,

    /// <summary>Oldest first, which keeps a trail layered consistently however the camera moves.</summary>
    ByAge
}

/// <summary>How a system's particles are turned into geometry.</summary>
/// <param name="Kind">What each particle is drawn as.</param>
/// <param name="Alignment">Which way it faces, for a billboard.</param>
/// <param name="Sort">What order they are drawn in.</param>
/// <param name="Axis">The axis for <see cref="VfxBillboardAlignment.FixedAxis" />.</param>
/// <param name="Stretch">
///     For <see cref="VfxBillboardAlignment.Velocity" />: how much a metre per second of speed
///     lengthens the quad, over and above its size.
/// </param>
/// <remarks>
///     Part of the compiled graph, and it declares the attributes it reads the same way an operation
///     does — so a velocity-aligned billboard is what makes a graph allocate velocity even if nothing
///     in the simulation would have.
/// </remarks>
/// <param name="RibbonSlot">
///     For <see cref="VfxRendererKind.Ribbon" />: which custom attribute holds the strip a particle
///     belongs to. Particles sharing a value are one ribbon, ordered by age.
/// </param>
public readonly record struct VfxRenderer(
    VfxRendererKind Kind = VfxRendererKind.Billboard,
    VfxBillboardAlignment Alignment = VfxBillboardAlignment.Camera,
    VfxSortMode Sort = VfxSortMode.None,
    Vector3 Axis = default,
    float Stretch = 0f,
    int RibbonSlot = 0
) {
    /// <summary>A camera-facing quad, unsorted. What an additive effect wants.</summary>
    public static VfxRenderer Billboard => new();

    /// <summary>A camera-facing quad, sorted back to front. What an alpha-blended effect wants.</summary>
    public static VfxRenderer SortedBillboard => new(Sort: VfxSortMode.ByDepth);

    /// <summary>A quad stretched along its velocity.</summary>
    /// <param name="stretch">How much a metre per second lengthens it.</param>
    /// <returns>The renderer.</returns>
    public static VfxRenderer Streak(float stretch = 0.1f) =>
        new(Alignment: VfxBillboardAlignment.Velocity, Stretch: stretch);

    /// <summary>An instance of a mesh per particle, oriented by its alignment.</summary>
    /// <param name="alignment">
    ///     Which way the mesh's local +Y points: nowhere in particular for
    ///     <see cref="VfxBillboardAlignment.Camera" />, along the velocity, or along
    ///     <see cref="Axis" />.
    /// </param>
    /// <param name="axis">The axis, when the alignment is a fixed one.</param>
    /// <returns>The renderer.</returns>
    public static VfxRenderer Instanced(
        VfxBillboardAlignment alignment = VfxBillboardAlignment.Camera,
        Vector3 axis = default
    ) =>
        new(VfxRendererKind.Mesh, alignment, Axis: axis);

    /// <summary>A strip through the particles that share a value in one custom attribute.</summary>
    /// <param name="slot">The custom attribute holding the strip identifier.</param>
    /// <returns>The renderer.</returns>
    /// <remarks>
    ///     Sorted by age, always, because that is the ribbon's own order rather than a drawing
    ///     preference — a strip drawn in the order the particles happen to sit in the buffer is a
    ///     tangle. <see cref="Sort" /> is left alone for that reason: it says nothing here.
    /// </remarks>
    public static VfxRenderer Ribbon(int slot) => new(VfxRendererKind.Ribbon, RibbonSlot: slot);

    /// <summary>The attributes it needs in order to draw anything.</summary>
    /// <remarks>
    ///     Position and size always; colour always, because a particle with no colour is one nothing
    ///     can tint and every effect tints something. Velocity only when the alignment or the stretch
    ///     asks for it, and rotation is <i>not</i> here — a graph that never rolls its particles draws
    ///     them unrolled, and paying for an attribute to multiply by zero would defeat the point of
    ///     deriving storage at all.
    /// </remarks>
    public VfxAttribute Reads {
        get {
            var attributes = VfxAttribute.Position | VfxAttribute.Size | VfxAttribute.Colour;

            if (Alignment == VfxBillboardAlignment.Velocity || Stretch != 0f) {
                attributes |= VfxAttribute.Velocity;
            }

            // A ribbon's order is its particles' ages, so drawing one is what makes a graph keep them
            // — the same rule as the velocity above, and the reason a renderer declares its reads at
            // all rather than hoping the simulation happened to want the same things.
            if (Kind == VfxRendererKind.Ribbon) {
                attributes |= VfxAttribute.Age;
            }

            return attributes;
        }
    }
}
