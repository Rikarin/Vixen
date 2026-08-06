// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Reading docs/plan/41 § D4's three carried-in sources off the source mesh and the settings.</summary>
/// <remarks>
///     <para>
///         <b>Creases, UV seams and guides all live on the <i>source</i>, and stage one renumbers it.</b>
///         The weld merges positions, the repair cuts non-manifold edges apart, the de-speck deletes
///         components and the pre-remesh splits, collapses and flips — so an edge index or a position
///         index taken before conditioning names nothing afterwards. Each of the three is turned into a
///         polyline in the mesh's own space here, and <see cref="FeatureDetector" /> resolves those
///         onto whatever surface came out.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/41 § D4 says "explicit creases — doc 24's shapes and the importer both carry
///         them", and doc 24's <see cref="EditMesh" /> does not have creases.</b> It has
///         <see cref="MeshFace.Group" />, which is § D4's separate face-group row, and
///         <see cref="MeshFace.Smoothing" />, which is the shading-group number
///         <c>EditMeshes.ToMeshData</c> splits normals on. A smoothing-group boundary <i>is</i> the
///         explicit crease an artist authored — it is what "this edge is hard" means in every format
///         that has the concept — so that is what <see cref="FromCreases" /> reads. A per-edge crease
///         <i>weight</i>, the subdivision-surface kind, exists in neither doc 24 nor the importer and
///         would be a change to both; it is not invented here.
///     </para>
/// </remarks>
static class FeatureCurves {
    /// <summary>Every carried-in source the settings have turned on.</summary>
    /// <param name="source">The mesh as it arrived, before conditioning.</param>
    /// <param name="settings">Which sources are on, and the guides.</param>
    /// <returns>The curves, in a fixed order: creases, then seams, then guides.</returns>
    public static List<FeatureCurve> All(EditMesh source, RemeshSettings settings) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var curves = new List<FeatureCurve>();

        if (settings.KeepCreases) {
            curves.AddRange(FromCreases(source));
        }

        if (settings.KeepUvSeams) {
            curves.AddRange(FromUvSeams(source));
        }

        curves.AddRange(FromGuides(settings));

        return curves;
    }

    /// <summary>Every edge whose two faces are in different smoothing groups, as a one-segment curve.</summary>
    /// <param name="source">The mesh.</param>
    /// <returns>One curve per crease edge.</returns>
    /// <remarks>
    ///     ⚠ <b>One segment each rather than chained here.</b> Chaining is
    ///     <see cref="FeatureDetector" />'s job and it does it on the conditioned surface, where the
    ///     answer is the one the rest of the pipeline needs; chaining on the source as well would be a
    ///     second implementation of the same walk, over a mesh that is about to be rewritten.
    /// </remarks>
    public static List<FeatureCurve> FromCreases(EditMesh source) {
        ArgumentNullException.ThrowIfNull(source);

        var curves = new List<FeatureCurve>();

        for (var edge = 0; edge < source.Edges.Count; edge++) {
            var faces = source.FacesOf(edge);

            if (faces.Length != 2 || source.Faces[faces[0]].Smoothing == source.Faces[faces[1]].Smoothing) {
                continue;
            }

            curves.Add(
                new(
                    [source.Positions[source.Edges[edge].A], source.Positions[source.Edges[edge].B]],
                    FeatureSource.Crease,
                    1f
                )
            );
        }

        return curves;
    }

    /// <summary>Every edge across which the source's texture coordinates jump.</summary>
    /// <param name="source">The mesh. Returns nothing when it carries no coordinates.</param>
    /// <returns>One curve per seam edge.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D4: "so that a retexture-then-remesh round trip does not shred an
    ///         atlas".</b> A seam in the input is where the atlas was cut, and a remesh that ignores it
    ///         puts output edges across the cut — which means the baked texture cannot be reused at
    ///         all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty coordinates mean absent rather than zero, which is <c>MeshData</c>'s own rule
    ///         and <see cref="EditMesh.TexCoords" /> keeps it.</b> A mesh with no coordinate layer has
    ///         no seams; a mesh whose coordinates are all zero has one enormous degenerate chart and
    ///         also no seams, and the two must not be confused with an error.
    ///     </para>
    /// </remarks>
    public static List<FeatureCurve> FromUvSeams(EditMesh source) {
        ArgumentNullException.ThrowIfNull(source);

        var curves = new List<FeatureCurve>();

        if (source.TexCoords.Length != source.CornerCount) {
            return curves;
        }

        for (var edge = 0; edge < source.Edges.Count; edge++) {
            var faces = source.FacesOf(edge);

            if (faces.Length != 2) {
                continue;
            }

            var a = source.Edges[edge].A;
            var b = source.Edges[edge].B;

            if (Coordinate(source, faces[0], a) == Coordinate(source, faces[1], a)
                && Coordinate(source, faces[0], b) == Coordinate(source, faces[1], b)) {
                continue;
            }

            curves.Add(new([source.Positions[a], source.Positions[b]], FeatureSource.UvSeam, 1f));
        }

        return curves;
    }

    /// <summary>The artist's guides, as they were authored.</summary>
    /// <param name="settings">The settings holding them.</param>
    /// <returns>One curve per guide, at its own strength.</returns>
    public static List<FeatureCurve> FromGuides(RemeshSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);

        var curves = new List<FeatureCurve>();

        foreach (var guide in settings.Guides) {
            if (guide.Points is { Count: > 1 }) {
                curves.Add(new(guide.Points, FeatureSource.Guide, Math.Clamp(guide.Strength, 0f, 1f)));
            }
        }

        return curves;
    }

    /// <summary>A face's coordinate at one of its corners, or zero when the face does not touch it.</summary>
    static Vector2 Coordinate(EditMesh source, int face, int position) {
        var corners = source.CornersOf(face);
        var entry = source.Faces[face];

        for (var corner = 0; corner < corners.Length; corner++) {
            if (corners[corner] == position) {
                return source.TexCoords[entry.Start + corner];
            }
        }

        return Vector2.Zero;
    }
}
