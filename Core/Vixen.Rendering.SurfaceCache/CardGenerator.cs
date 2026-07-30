// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>Fits up to six cards to a mesh's geometry — doc 19 § L4's offline half.</summary>
/// <remarks>
///     <para>
///         <b>Triangles vote by their dominant normal axis.</b> Each of the six directions collects
///         the triangles whose normals lean its way, and a direction that gathered any area gets a
///         card fitted to those triangles' bounds — grown by a margin so the capture's rays start
///         outside the surface rather than on it. Ties at exactly forty-five degrees go to the
///         smaller axis index, one rule stated once, because a tie broken by float noise is a card
///         that flickers between two shapes across a rebake.
///     </para>
///     <para>
///         <b>Resolution follows world size, clamped.</b> Texels per world unit is the quality knob
///         and a maximum keeps one huge floor from eating the atlas; a card never drops below one
///         texel, because a card that exists represents its surfaces at whatever fidelity it was
///         given — the atlas budget decides what exists, not this.
///     </para>
/// </remarks>
public static class CardGenerator {
    /// <summary>Fits cards to a triangle mesh.</summary>
    /// <param name="positions">The vertex positions.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="texelsPerUnit">Texels per world unit, along each card axis.</param>
    /// <param name="maxResolution">The most texels a card may have along either axis.</param>
    /// <param name="margin">How far past the triangles' bounds a card's box reaches, in world units.</param>
    /// <returns>The cards, at most six, in axis order.</returns>
    /// <exception cref="ArgumentException">The indices are not whole triangles.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of range.</exception>
    public static List<SurfaceCard> Generate(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<int> indices,
        float texelsPerUnit = 8f,
        int maxResolution = 64,
        float margin = 0.05f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(texelsPerUnit);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResolution, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        if (indices.Length % 3 != 0) {
            throw new ArgumentException($"{indices.Length} indices are not whole triangles.", nameof(indices));
        }

        Span<float> area = stackalloc float[6];
        Span<Vector3> minimum = stackalloc Vector3[6];
        Span<Vector3> maximum = stackalloc Vector3[6];

        for (var axis = 0; axis < 6; axis++) {
            minimum[axis] = new(float.MaxValue);
            maximum[axis] = new(float.MinValue);
        }

        for (var triangle = 0; triangle < indices.Length; triangle += 3) {
            var a = positions[indices[triangle]];
            var b = positions[indices[triangle + 1]];
            var c = positions[indices[triangle + 2]];
            var cross = Vector3.Cross(b - a, c - a);
            var size = cross.Length() * 0.5f;

            if (size <= 0f) {
                continue;
            }

            // The dominant axis, ties to the smaller index — >= on the comparisons, not >.
            var absolute = new Vector3(MathF.Abs(cross.X), MathF.Abs(cross.Y), MathF.Abs(cross.Z));
            int component;

            if (absolute.X >= absolute.Y && absolute.X >= absolute.Z) {
                component = 0;
            } else if (absolute.Y >= absolute.Z) {
                component = 1;
            } else {
                component = 2;
            }

            var positive = (component == 0 ? cross.X : component == 1 ? cross.Y : cross.Z) >= 0f;
            var axis = (component * 2) + (positive ? 0 : 1);

            area[axis] += size;
            minimum[axis] = Vector3.Min(Vector3.Min(minimum[axis], a), Vector3.Min(b, c));
            maximum[axis] = Vector3.Max(Vector3.Max(maximum[axis], a), Vector3.Max(b, c));
        }

        var cards = new List<SurfaceCard>(6);

        for (var axis = 0; axis < 6; axis++) {
            if (area[axis] <= 0f) {
                continue;
            }

            var grown = new Vector3(margin);
            var low = minimum[axis] - grown;
            var high = maximum[axis] + grown;
            var centre = (low + high) * 0.5f;

            // A perfectly flat face has zero extent along its own axis, and a card is a box — the
            // millimetre floor gives the capture somewhere to stand without moving the surface.
            var half = Vector3.Max((high - low) * 0.5f, new(0.001f));
            var card = new SurfaceCard(axis, centre, half, new(1, 1));
            var (plane, _) = card.Extents;

            cards.Add(
                new(
                    axis,
                    centre,
                    half,
                    new(
                        Math.Clamp((int)MathF.Ceiling(plane.X * 2f * texelsPerUnit), 1, maxResolution),
                        Math.Clamp((int)MathF.Ceiling(plane.Y * 2f * texelsPerUnit), 1, maxResolution)
                    )
                )
            );
        }

        return cards;
    }
}
