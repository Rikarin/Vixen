// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The maps a bake can produce.</summary>
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

    /// <summary>The mean depth through the surface, along the hemisphere turned inward.</summary>
    Thickness,

    /// <summary>The surface point within the source's box.</summary>
    Position,

    /// <summary>The source's normal in its own space.</summary>
    WorldNormal,

    /// <summary>The source's material or island, as a colour.</summary>
    Id,

    /// <summary>The source's colour, carried as the caller gave it.</summary>
    Colour,

    /// <summary>Where a ray found the source.</summary>
    Opacity,

    /// <summary>A band along the source's marked edges.</summary>
    Edges,

    /// <summary>The target's UV islands, as a colour each.</summary>
    Islands
}

/// <summary>One baked map, ready to be written at any depth.</summary>
/// <param name="Kind">Which map.</param>
/// <param name="Resolution">Its width.</param>
/// <param name="Channels">One or three.</param>
/// <param name="Samples">
///     What was measured, in its own units, interleaved and with the <b>top</b> row first, as an image
///     file wants it: a normal in <c>[-1, 1]</c>, a displacement in model units.
/// </param>
/// <param name="Scale">
///     What a stored unit value is multiplied by to get a signed quantity back: the largest absolute
///     displacement, the curvature's <c>BakedMaps.CurvatureRange</c> (a percentile, beyond which an
///     integer file clips), or zero for a map that has no such scale.
/// </param>
/// <param name="Signed">Whether <see cref="Unit" /> has to centre this map on one half.</param>
/// <param name="Height">Its height, or null for <paramref name="Resolution" />: a square map.</param>
public sealed record MeshMapPlane(
    MeshMapKind Kind,
    int Resolution,
    int Channels,
    float[] Samples,
    float Scale,
    bool Signed,
    int? Height = null) {
    /// <summary>Its height, <see cref="Height" /> or else <see cref="Resolution" />.</summary>
    public int Rows => Height ?? Resolution;

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
            value = value * 0.5f + 0.5f;
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
        var texels = maps.Resolution * maps.Rows;
        var height = maps.Height;
        List<MeshMapPlane> planes = [
            new(MeshMapKind.Normal, size, 3, Vectors(texels, maps.Normals), 0f, true, height),
            new(
                MeshMapKind.Displacement,
                size,
                1,
                Scalars(texels, maps.Displacement),
                maps.DisplacementRange,
                true,
                height
            )
        ];

        if (maps.AmbientOcclusion is { } occlusion) {
            planes.Add(new(MeshMapKind.AmbientOcclusion, size, 1, Scalars(texels, occlusion), 0f, false, height));
        }

        if (maps.BentNormal is { } bent) {
            planes.Add(new(MeshMapKind.BentNormal, size, 3, Vectors(texels, bent), 0f, true, height));
        }

        if (maps.Curvature is { } curvature) {
            planes.Add(
                new(MeshMapKind.Curvature, size, 1, Scalars(texels, curvature), maps.CurvatureRange, true, height)
            );
        }

        if (maps.Thickness is { } thickness) {
            planes.Add(new(MeshMapKind.Thickness, size, 1, Scalars(texels, thickness), 0f, false, height));
        }

        if (maps.Position is { } position) {
            planes.Add(new(MeshMapKind.Position, size, 3, Vectors(texels, position), 0f, false, height));
        }

        if (maps.WorldNormal is { } world) {
            planes.Add(new(MeshMapKind.WorldNormal, size, 3, Vectors(texels, world), 0f, true, height));
        }

        if (maps.Ids is { } ids) {
            // ⚠ A colour per texel from its own id, here and nowhere later, because nothing may
            // interpolate an id: see MapBaker.IdColour.
            var colours = MapBaker.IdColours(ids, maps.IdsAreColours, maps.IdPalette);

            planes.Add(new(MeshMapKind.Id, size, 3, Vectors(texels, colours), 0f, false, height));
        }

        if (maps.Colour is { } colour) {
            // Red, green and blue: the coverage says where the chart is, and alpha is not asked of a colour map.
            var rgb = new Vector3[colour.Count];

            for (var at = 0; at < rgb.Length; at++) {
                rgb[at] = new(colour[at].X, colour[at].Y, colour[at].Z);
            }

            planes.Add(new(MeshMapKind.Colour, size, 3, Vectors(texels, rgb), 0f, false, height));
        }

        if (maps.Opacity is { } opacity) {
            planes.Add(new(MeshMapKind.Opacity, size, 1, Scalars(texels, opacity), 0f, false, height));
        }

        if (maps.Edges is { } edges) {
            planes.Add(new(MeshMapKind.Edges, size, 1, Scalars(texels, edges), 0f, false, height));
        }

        if (maps.Islands is { } islands) {
            // ⚠ A colour per island from its own number, as an id is, so nothing blends two islands into a third.
            var colours = MapBaker.IdColours(islands, false, IdPalette.Random);

            planes.Add(new(MeshMapKind.Islands, size, 3, Vectors(texels, colours), 0f, false, height));
        }

        return planes;
    }

    static float[] Scalars(int texels, IReadOnlyList<float> values) {
        var samples = new float[texels];

        for (var at = 0; at < samples.Length; at++) {
            samples[at] = values[at];
        }

        return samples;
    }

    static float[] Vectors(int texels, IReadOnlyList<Vector3> values) {
        var samples = new float[texels * 3];

        for (var at = 0; at < values.Count; at++) {
            var value = values[at];

            samples[at * 3] = value.X;
            samples[at * 3 + 1] = value.Y;
            samples[at * 3 + 2] = value.Z;
        }

        return samples;
    }
}
