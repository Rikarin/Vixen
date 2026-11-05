// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The nine maps a bake can produce.</summary>
public enum MeshMapKind {
    /// <summary>The source's normal, in the bake's space.</summary>
    Normal,

    /// <summary>The signed distance to the source.</summary>
    Displacement,

    /// <summary>The unoccluded fraction of the hemisphere.</summary>
    AmbientOcclusion,

    /// <summary>The average unoccluded direction.</summary>
    BentNormal,

    /// <summary>Mean curvature.</summary>
    Curvature,

    /// <summary>The occluded fraction of the hemisphere turned inward.</summary>
    Thickness,

    /// <summary>The surface point within the source's box.</summary>
    Position,

    /// <summary>The source's normal in its own space.</summary>
    WorldNormal,

    /// <summary>The source's material or island, as a colour.</summary>
    Id
}

/// <summary>One baked map, ready to be written at any depth.</summary>
/// <param name="Kind">Which map.</param>
/// <param name="Resolution">Its edge length.</param>
/// <param name="Channels">One or three.</param>
/// <param name="Samples">
///     What was measured, in its own units, interleaved and with the <b>top</b> row first, as an image
///     file wants it: a normal in <c>[-1, 1]</c>, a displacement in model units.
/// </param>
/// <param name="Scale">
///     What a stored unit value is multiplied by to get a signed quantity back: the largest absolute
///     displacement or curvature, or zero for a map that has no such scale.
/// </param>
/// <param name="Signed">Whether <see cref="Unit" /> has to centre this map on one half.</param>
public sealed record MeshMapPlane(MeshMapKind Kind, int Resolution, int Channels, float[] Samples, float Scale, bool Signed) {
    /// <summary>A sample as an integer format stores it, in <c>[0, 1]</c>.</summary>
    /// <param name="index">Which sample.</param>
    /// <returns>The stored value, clamped.</returns>
    /// <remarks>
    ///     ⚠ <b>A float file does not go through this.</b> It stores the measurement, because nothing
    ///     clips it. An integer file has to centre and scale it, and <see cref="Scale" /> goes beside
    ///     the file so that the two can be undone: half of the ways a displacement map goes wrong are
    ///     the pixels and the scale being written by different code.
    /// </remarks>
    public float Unit(int index) {
        var value = Samples[index];

        if (Signed) {
            value = Scale > 0f ? value / Scale : value;
            value = (value * 0.5f) + 0.5f;
        }

        return Math.Clamp(value, 0f, 1f);
    }
}

/// <summary>Turns a bake's arrays into planes a file writer can take, with no file in sight.</summary>
/// <remarks>
///     ⚠ <b>This is the one place that says how each map is stored</b>, for a caller outside an editor:
///     this assembly has no image format in it, so <c>Core.Imaging</c> does the bytes, and neither has
///     to know about the other.
/// </remarks>
public static class MeshMapPlanes {
    /// <summary>Every map the bake produced.</summary>
    /// <param name="maps">The bake.</param>
    /// <returns>The normal and displacement, then whichever of the rest were asked for.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="maps" /> is null.</exception>
    public static IReadOnlyList<MeshMapPlane> Of(BakedMaps maps) {
        ArgumentNullException.ThrowIfNull(maps);

        var size = maps.Resolution;
        List<MeshMapPlane> planes = [
            new(MeshMapKind.Normal, size, 3, Vectors(size, maps.Normals), 0f, true),
            new(MeshMapKind.Displacement, size, 1, Scalars(size, maps.Displacement), maps.DisplacementRange, true)
        ];

        if (maps.AmbientOcclusion is { } occlusion) {
            planes.Add(new(MeshMapKind.AmbientOcclusion, size, 1, Scalars(size, occlusion), 0f, false));
        }

        if (maps.BentNormal is { } bent) {
            planes.Add(new(MeshMapKind.BentNormal, size, 3, Vectors(size, bent), 0f, true));
        }

        if (maps.Curvature is { } curvature) {
            planes.Add(new(MeshMapKind.Curvature, size, 1, Scalars(size, curvature), maps.CurvatureRange, true));
        }

        if (maps.Thickness is { } thickness) {
            planes.Add(new(MeshMapKind.Thickness, size, 1, Scalars(size, thickness), 0f, false));
        }

        if (maps.Position is { } position) {
            planes.Add(new(MeshMapKind.Position, size, 3, Vectors(size, position), 0f, false));
        }

        if (maps.WorldNormal is { } world) {
            planes.Add(new(MeshMapKind.WorldNormal, size, 3, Vectors(size, world), 0f, true));
        }

        if (maps.Ids is { } ids) {
            // ⚠ A colour per texel from its own id, here and nowhere later, because nothing may
            // interpolate an id: see MapBaker.IdColour.
            var colours = new Vector3[ids.Count];

            for (var index = 0; index < colours.Length; index++) {
                colours[index] = MapBaker.IdColour(ids[index]);
            }

            planes.Add(new(MeshMapKind.Id, size, 3, Vectors(size, colours), 0f, false));
        }

        return planes;
    }

    static float[] Scalars(int size, IReadOnlyList<float> values) {
        var samples = new float[size * size];

        for (var at = 0; at < samples.Length; at++) {
            samples[at] = values[at];
        }

        return samples;
    }

    static float[] Vectors(int size, IReadOnlyList<Vector3> values) {
        var samples = new float[size * size * 3];

        for (var at = 0; at < values.Count; at++) {
            var value = values[at];

            samples[at * 3] = value.X;
            samples[(at * 3) + 1] = value.Y;
            samples[(at * 3) + 2] = value.Z;
        }

        return samples;
    }
}
