// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;

namespace Vixen.Texturing.Layers;

/// <summary>
///     A decal's or a path's <see cref="LayerSurfaceSymmetry" /> as the copies the stack compiles (Patina #132): each the
///     decal's box or the path's points moved by one of <see cref="PaintSymmetryGroup" />'s motions.
/// </summary>
/// <remarks>
///     ⚠ <b>The same motions a symmetric stroke makes</b>, so a decal mirrored on x lands where a brush stroke mirrored
///     on x through the same point would: a mirror is one reflection, a radial symmetry of <i>n</i> is <i>n</i> − 1
///     turns.
/// </remarks>
static class LayerSurfaceCopies {
    /// <summary>The copies a symmetry makes, the original not among them, or a reason it makes none.</summary>
    /// <param name="symmetry">The symmetry, or null for none.</param>
    /// <param name="problem">Why a symmetry that was given makes no copies, or null.</param>
    /// <returns>The motions, empty for none.</returns>
    public static IReadOnlyList<PaintIsometry> Of(LayerSurfaceSymmetry? symmetry, out string? problem) {
        problem = null;

        if (symmetry is null) {
            return [];
        }

        if (symmetry.Axis is not { Length: 3 } axis
            || symmetry.Through is not { Length: 3 } through
            || !axis.All(float.IsFinite)
            || !through.All(float.IsFinite)
            || !(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2] > 1e-12f)) {
            problem = "This symmetry has no axis, so nothing is copied: its axis and the point it passes through are "
                + "three numbers each, and the axis has a length.";

            return [];
        }

        if (symmetry.Kind == LayerSymmetryKind.Radial
            && symmetry.Count is < 2 or > LayerSurfaceSymmetry.MaximumCount) {
            problem = $"A radial symmetry makes 2 to {LayerSurfaceSymmetry.MaximumCount} copies, the original among "
                + "them, so nothing is copied.";

            return [];
        }

        Vector3 line = new(axis[0], axis[1], axis[2]);
        Vector3 point = new(through[0], through[1], through[2]);
        var group = symmetry.Kind == LayerSymmetryKind.Radial
            ? PaintSymmetryGroup.Radial(line, point, symmetry.Count)
            : PaintSymmetryGroup.Mirror(PaintSymmetry.Across(line, point));

        return group.Copies;
    }

    /// <summary>A decal's box moved: its centre as a point, its facing and its up as directions.</summary>
    /// <param name="decal">The decal.</param>
    /// <param name="copy">The motion.</param>
    /// <returns>The copy, its picture squared again from the moved facing and up when compiled.</returns>
    public static LayerDecal Moved(LayerDecal decal, PaintIsometry copy) =>
        decal with {
            Centre = Numbers(copy.Point(Vector(decal.Centre))),
            Facing = Numbers(copy.Direction(Vector(decal.Facing))),
            Up = Numbers(copy.Direction(Vector(decal.Up)))
        };

    /// <summary>A path's points moved, each place as a point and each normal as a direction.</summary>
    /// <param name="path">The path.</param>
    /// <param name="copy">The motion.</param>
    /// <returns>The copy.</returns>
    public static LayerSurfacePath Moved(LayerSurfacePath path, PaintIsometry copy) =>
        path with {
            Points = [
                ..path.Points.Select(point => {
                        var place = copy.Point(new(point.X, point.Y, point.Z));
                        var normal = copy.Direction(new(point.NormalX, point.NormalY, point.NormalZ));

                        return point with {
                            X = place.X,
                            Y = place.Y,
                            Z = place.Z,
                            NormalX = normal.X,
                            NormalY = normal.Y,
                            NormalZ = normal.Z
                        };
                    }
                )
            ]
        };

    static Vector3 Vector(float[] numbers) =>
        new(
            numbers.Length > 0 ? numbers[0] : 0f,
            numbers.Length > 1 ? numbers[1] : 0f,
            numbers.Length > 2 ? numbers[2] : 0f
        );

    static float[] Numbers(Vector3 vector) => [vector.X, vector.Y, vector.Z];
}
