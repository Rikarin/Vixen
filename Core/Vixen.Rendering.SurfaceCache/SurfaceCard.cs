// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>One orthographic capture of a piece of surface — doc 19 § L4's card.</summary>
/// <remarks>
///     <para>
///         <b>A card is a box and an axis.</b> It captures the surfaces inside its box whose normals
///         lean along its direction, looking straight down that direction orthographically — up to
///         six of these cover a mesh from its six sides, which is the whole trick: a surface's
///         radiance becomes a low-resolution 2D texture problem, and radiosity over 2D textures is
///         cheap where radiosity over geometry is not.
///     </para>
///     <para>
///         <b>The in-plane frame is the other two world axes, in cyclic order, whatever the sign.</b>
///         Direction ±X maps the card's UV to world (Y, Z), ±Y to (Z, X), ±Z to (X, Y). One rule
///         with no branches on sign, so a card and the shader that will someday sample it cannot
///         disagree about which way U runs — the octahedral fold's lesson, one convention over.
///     </para>
/// </remarks>
public readonly record struct SurfaceCard {
    /// <summary>Places a card.</summary>
    /// <param name="axis">Which of the six directions it faces, 0 to 5: +X, −X, +Y, −Y, +Z, −Z.</param>
    /// <param name="centre">The centre of its box, in world space.</param>
    /// <param name="halfSize">The box's half-extents — the component along the axis is the capture's
    ///     half-depth, the other two the card's half-width and half-height in cyclic order.</param>
    /// <param name="resolution">Texels along the card's U and V.</param>
    /// <exception cref="ArgumentOutOfRangeException">No such axis, an empty box or an empty map.</exception>
    public SurfaceCard(int axis, Vector3 centre, Vector3 halfSize, Int2 resolution) {
        ArgumentOutOfRangeException.ThrowIfNegative(axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(axis, 5);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfSize.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfSize.Y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfSize.Z);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution.Y, 1);

        Axis = axis;
        Centre = centre;
        HalfSize = halfSize;
        Resolution = resolution;
    }

    /// <summary>Which of the six directions the card faces: +X, −X, +Y, −Y, +Z, −Z.</summary>
    public int Axis { get; }

    /// <summary>The centre of its box, in world space.</summary>
    public Vector3 Centre { get; }

    /// <summary>The box's half-extents, world-axis aligned.</summary>
    public Vector3 HalfSize { get; }

    /// <summary>Texels along U and V.</summary>
    public Int2 Resolution { get; }

    /// <summary>The outward direction of the surfaces this card captures.</summary>
    public Vector3 Direction {
        get {
            var sign = (Axis & 1) == 0 ? 1f : -1f;

            return (Axis / 2) switch {
                0 => new(sign, 0f, 0f),
                1 => new(0f, sign, 0f),
                _ => new(0f, 0f, sign)
            };
        }
    }

    /// <summary>The world component index the card's U runs along — cyclic from the axis.</summary>
    public int UComponent => ((Axis / 2) + 1) % 3;

    /// <summary>The world component index the card's V runs along.</summary>
    public int VComponent => ((Axis / 2) + 2) % 3;

    /// <summary>The half-extent along U and V, and the half-depth along the axis.</summary>
    public (Vector2 Plane, float Depth) Extents {
        get {
            var half = HalfSize;

            return (new(Component(half, UComponent), Component(half, VComponent)), Component(half, Axis / 2));
        }
    }

    /// <summary>Where a texel's centre sits on the card's near plane — the face the capture enters.</summary>
    /// <param name="texel">The texel.</param>
    /// <exception cref="ArgumentOutOfRangeException">No such texel.</exception>
    public Vector3 TexelOrigin(Int2 texel) {
        ArgumentOutOfRangeException.ThrowIfNegative(texel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.X, Resolution.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.Y, Resolution.Y);

        var (plane, depth) = Extents;
        var u = (((texel.X + 0.5f) / Resolution.X) * 2f - 1f) * plane.X;
        var v = (((texel.Y + 0.5f) / Resolution.Y) * 2f - 1f) * plane.Y;
        var origin = Centre + (Direction * depth);

        return WithComponent(WithComponent(origin, UComponent, Component(Centre, UComponent) + u), VComponent, Component(Centre, VComponent) + v);
    }

    /// <summary>A world position in the card's own terms, if it lies inside the box.</summary>
    /// <param name="world">The position.</param>
    /// <param name="texel">The texel it lands in.</param>
    /// <param name="depth">How far inside the near plane it sits, in world units.</param>
    /// <returns>False outside the box.</returns>
    public bool TryProject(Vector3 world, out Int2 texel, out float depth) {
        texel = default;
        depth = default;

        var (plane, halfDepth) = Extents;
        var local = world - Centre;
        var u = Component(local, UComponent);
        var v = Component(local, VComponent);
        var along = Vector3.Dot(local, Direction);

        if (MathF.Abs(u) > plane.X || MathF.Abs(v) > plane.Y || MathF.Abs(along) > halfDepth) {
            return false;
        }

        texel = new(
            Math.Clamp((int)(((u / plane.X) + 1f) * 0.5f * Resolution.X), 0, Resolution.X - 1),
            Math.Clamp((int)(((v / plane.Y) + 1f) * 0.5f * Resolution.Y), 0, Resolution.Y - 1)
        );

        depth = halfDepth - along;

        return true;
    }

    static float Component(Vector3 value, int index) => index == 0 ? value.X : index == 1 ? value.Y : value.Z;

    static Vector3 WithComponent(Vector3 value, int index, float component) =>
        index switch {
            0 => new(component, value.Y, value.Z),
            1 => new(value.X, component, value.Z),
            _ => new(value.X, value.Y, component)
        };
}
