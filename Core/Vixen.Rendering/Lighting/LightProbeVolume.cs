// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>One baked sample of the indirect light arriving at a point.</summary>
/// <remarks>
///     A position and nine coefficients per channel, which is the whole of it. What makes a probe
///     useful is not the probe, it is the <see cref="LightProbeVolume" /> that knows which four of
///     them a position sits between.
/// </remarks>
public sealed class LightProbe {
    /// <summary>Where the probe was captured, in world space.</summary>
    public Vector3 Position { get; set; }

    /// <summary>The environment there, projected into order-2 spherical harmonics.</summary>
    public ShCoefficients Coefficients { get; set; }
}

/// <summary>
///     A set of light probes and the tetrahedral mesh that interpolates between them.
/// </summary>
/// <remarks>
///     <para>
///         The pragmatic answer to indirect diffuse light on moving objects: bake the environment
///         at a few dozen places in a level, and give a position somewhere between them a blend of
///         the four probes that surround it. Spherical harmonics make the blend free — a weighted
///         sum of projections is the projection of the weighted sum — so the entire cost is
///         deciding <em>which</em> four, and that is a Delaunay tetrahedralisation.
///     </para>
///     <para>
///         Delaunay specifically, rather than any tetrahedral mesh over the same points. The
///         empty-circumsphere property is what makes the four probes a position blends between its
///         natural neighbours; a mesh that grouped them any other way would light an object
///         differently depending on which arbitrary seam it was standing on.
///     </para>
///     <para>
///         <strong>Outside the mesh, the nearest probe.</strong> Outside means outside the convex
///         hull of the probes an author placed, which is a statement about the authoring rather
///         than about the position — and extrapolating a spherical-harmonic fit past the data it
///         was fitted to produces negative irradiance, which is a surface that removes light from
///         the frame. Falling back to the nearest probe is flat rather than smooth, and flat is
///         the honest answer at the edge of what was baked.
///     </para>
///     <para>
///         Probes whose positions coincide are merged, first one winning, because two probes in the
///         same place have no interpolation between them to describe. Probes that are all on one
///         plane — a single-height grid over a floor, which is a reasonable thing to author — have
///         no tetrahedralisation at all; <see cref="IsTetrahedral" /> is false and every sample is
///         the nearest probe.
///     </para>
/// </remarks>
public sealed class LightProbeVolume {
    readonly ShCoefficients[] coefficients;
    readonly DelaunayTetrahedralization mesh;

    LightProbeVolume(DelaunayTetrahedralization mesh, ShCoefficients[] coefficients) {
        this.mesh = mesh;
        this.coefficients = coefficients;
    }

    /// <summary>The distinct probe positions, in the order the tetrahedral mesh indexes them.</summary>
    public ReadOnlySpan<Vector3> Positions => mesh.Vertices;

    /// <summary>The tetrahedral mesh the interpolation walks.</summary>
    public DelaunayTetrahedralization Mesh => mesh;

    /// <summary>
    ///     Whether the probes enclose a volume, and so whether sampling interpolates at all.
    /// </summary>
    /// <remarks>
    ///     False for fewer than four distinct probes and for any number of coplanar ones. Not a
    ///     failure — see the class remarks — but worth surfacing, because a volume that always
    ///     returns its nearest probe looks like a broken bake and is in fact a flat one.
    /// </remarks>
    public bool IsTetrahedral => !mesh.IsDegenerate && mesh.FillsConvexHull;

    /// <summary>Builds the volume over <paramref name="probes" />.</summary>
    /// <param name="probes">The baked probes. Positions must be finite.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probes" /> is null.</exception>
    public static LightProbeVolume Build(IReadOnlyList<LightProbe> probes) {
        ArgumentNullException.ThrowIfNull(probes);

        var positions = new Vector3[probes.Count];
        var baked = new Dictionary<Vector3, ShCoefficients>(probes.Count);

        for (var probe = 0; probe < probes.Count; probe++) {
            positions[probe] = probes[probe].Position;
            baked.TryAdd(probes[probe].Position, probes[probe].Coefficients);
        }

        var mesh = DelaunayTetrahedralization.Build(positions);
        var coefficients = new ShCoefficients[mesh.Vertices.Length];

        // The mesh merged coincident positions, so its vertices are the probes that survived —
        // matched back by position rather than by index, so that nothing here depends on how the
        // merge happened to order them.
        for (var vertex = 0; vertex < coefficients.Length; vertex++) {
            coefficients[vertex] = baked[mesh.Vertices[vertex]];
        }

        return new(mesh, coefficients);
    }

    /// <summary>The indirect light at a position.</summary>
    /// <param name="position">Where to sample, in world space.</param>
    public ShCoefficients Sample(Vector3 position) {
        var cell = 0;

        return Sample(position, ref cell);
    }

    /// <summary>
    ///     <see cref="Sample(Vector3)" />, reusing and updating a cell hint across calls.
    /// </summary>
    /// <param name="position">Where to sample, in world space.</param>
    /// <param name="cell">
    ///     The cell the last sample landed in, updated to the one this sample landed in. Left
    ///     alone when the position is outside the mesh, so that a chain of samples that steps
    ///     outside and back keeps the hint it had.
    /// </param>
    /// <remarks>
    ///     The lookup is a walk between neighbouring cells, so its cost is the distance between
    ///     this position and where the walk starts. An object sampled every frame moves a few
    ///     centimetres between them, which is one or two cells, and passing the hint back turns a
    ///     search across the level into that. A stale or invalid hint is ignored rather than
    ///     rejected.
    /// </remarks>
    public ShCoefficients Sample(Vector3 position, ref int cell) {
        if (mesh.TryFind(position, cell, out var found, out var weights)) {
            cell = found;

            return (coefficients[mesh.CellVertices[found * 4]] * weights.X)
                + (coefficients[mesh.CellVertices[(found * 4) + 1]] * weights.Y)
                + (coefficients[mesh.CellVertices[(found * 4) + 2]] * weights.Z)
                + (coefficients[mesh.CellVertices[(found * 4) + 3]] * weights.W);
        }

        var nearest = NearestProbe(position);

        return nearest < 0 ? default : coefficients[nearest];
    }

    /// <summary>The probe closest to a position, or <c>-1</c> when there are none.</summary>
    /// <remarks>
    ///     A scan. It only runs outside the mesh, where the alternative — another spatial index,
    ///     kept in step with the first one — would be a second source of truth about where the
    ///     probes are for the sake of a case that is already the edge of the bake.
    /// </remarks>
    public int NearestProbe(Vector3 position) {
        var nearest = -1;
        var best = float.PositiveInfinity;

        for (var probe = 0; probe < mesh.Vertices.Length; probe++) {
            var distance = Vector3.DistanceSquared(position, mesh.Vertices[probe]);

            if (distance < best) {
                best = distance;
                nearest = probe;
            }
        }

        return nearest;
    }
}
