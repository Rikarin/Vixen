// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>What a particle is drawn as.</summary>
public enum VfxRendererKind {
    /// <summary>A quad, one per particle.</summary>
    Billboard
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
public readonly record struct VfxRenderer(
    VfxRendererKind Kind = VfxRendererKind.Billboard,
    VfxBillboardAlignment Alignment = VfxBillboardAlignment.Camera,
    VfxSortMode Sort = VfxSortMode.None,
    Vector3 Axis = default,
    float Stretch = 0f
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

            return attributes;
        }
    }
}
