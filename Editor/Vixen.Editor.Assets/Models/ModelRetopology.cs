// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;
using Vixen.Geometry.Uv;
using Vixen.Rendering;

namespace Vixen.Editor.Assets.Models;

/// <summary>Retopology and unwrapping over a model file's meshes, for the importer and the CLI alike.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D16 and docs/plan/42 § D13 name four surfaces between them and three of
///         them run exactly this.</b> The importer, <c>vixen remesh</c> and <c>vixen unwrap</c> differ
///         in where the mesh came from and where it goes, and in nothing else — so the decision of what
///         to do to a mesh lives here once rather than being made the same way three times and then
///         drifting.
///     </para>
///     <para>
///         ⚠ <b>A refusal comes back as the mesh that went in, with the reason reported.</b> The
///         remesher refuses rather than throwing — an empty result with the stage named in its
///         warnings, which is docs/plan/41's seventh exit criterion — and an import that turned that
///         into an empty mesh would delete somebody's asset over a shape the layout could not partition.
///     </para>
/// </remarks>
public static class ModelRetopology {
    /// <summary>What one mesh's pass through the two stages did.</summary>
    /// <param name="Mesh">The result, which is the input when nothing ran or a stage refused.</param>
    /// <param name="Remeshed">Whether the retopology produced a new mesh.</param>
    /// <param name="Unwrapped">Whether an atlas was generated.</param>
    /// <param name="Messages">What to say about it, worst first.</param>
    public readonly record struct MeshResult(
        MeshData Mesh,
        bool Remeshed,
        bool Unwrapped,
        IReadOnlyList<string> Messages
    );

    /// <summary>Retopologises and unwraps one mesh, as the settings ask.</summary>
    /// <param name="mesh">The geometry.</param>
    /// <param name="settings">What to do to it.</param>
    /// <param name="guides">Resolved guide curves, or empty.</param>
    /// <returns>The result and what happened.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static MeshResult Run(
        MeshData mesh,
        ModelImportSettings settings,
        IReadOnlyList<RemeshGuide>? guides = null
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);

        var messages = new List<string>();
        var wants = settings.Unwrap switch {
            UnwrapMode.Always => true,
            UnwrapMode.WhenMissing => mesh.TexCoords.Length != mesh.Positions.Length || mesh.TexCoords.Length == 0,
            _ => false
        };

        if (!settings.Retopologize && !wants) {
            return new(mesh, false, false, messages);
        }

        if (mesh.Indices.Length == 0) {
            return new(mesh, false, false, messages);
        }

        var kernel = ModelGeometry.ToEditMesh(mesh);
        var remeshed = false;

        if (settings.Retopologize) {
            var quads = Remesher.Remesh(kernel, settings.ToRemeshSettings(guides), out var report);

            messages.AddRange(report.Warnings);

            if (quads.IsEmpty) {
                messages.Add($"'{mesh.Name}' was not retopologised and was kept as it arrived.");
            } else {
                kernel = quads;
                remeshed = true;

                messages.Add(
                    $"'{mesh.Name}' retopologised to {report.QuadCount} quads, "
                    + $"max deviation {report.MaxDeviation:0.####} of the diagonal."
                );
            }
        }

        if (!wants) {
            return new(remeshed ? ModelGeometry.ToMeshData(kernel, mesh.Name) : mesh, remeshed, false, messages);
        }

        var (coordinates, unwrapped) = Unwrap(kernel, settings, mesh.Name, messages);

        if (!remeshed && !unwrapped) {
            return new(mesh, false, false, messages);
        }

        return new(ModelGeometry.ToMeshData(kernel, mesh.Name, coordinates), remeshed, unwrapped, messages);
    }

    /// <summary>docs/plan/42 § D1's three stages, in order, over one mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">The resolution, the margin and the density.</param>
    /// <param name="name">What to call it in a message.</param>
    /// <param name="messages">Where the report's warnings go.</param>
    /// <returns>One coordinate per corner and whether it worked.</returns>
    /// <remarks>
    ///     ⚠ <b>The packer throws where the other two refuse</b> — <c>InvalidOperationException</c>
    ///     when the islands did not fit and could not be made to. Caught here rather than escaping,
    ///     because "this mesh has no atlas" is a message beside an asset and not a failed import: the
    ///     geometry is still perfectly good.
    /// </remarks>
    static (Vector2[] Coordinates, bool Unwrapped) Unwrap(
        EditMesh mesh,
        ModelImportSettings settings,
        string name,
        List<string> messages
    ) {
        try {
            var charts = UvUnwrap.Charts(mesh, settings.ToUvSettings());
            var islands = UvUnwrap.Flatten(mesh, charts, settings.ToUvSettings());
            var placements = UvUnwrap.Pack(islands, settings.ToPackSettings(), out var report);

            messages.AddRange(report.Warnings);
            messages.Add(
                $"'{name}' unwrapped into {report.ChartCount} charts, "
                + $"{report.EffectiveEfficiency:P0} of the atlas used after margin."
            );

            return (ModelGeometry.Atlas(mesh, islands, placements), true);
        } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
            messages.Add($"'{name}' was not unwrapped: {failure.Message}");

            return ([], false);
        }
    }

    /// <summary>A spline as a guide curve, sampled at a constant speed along its length.</summary>
    /// <param name="spline">The curve.</param>
    /// <param name="strength">How hard the field is pulled toward it, in <c>[0, 1]</c>.</param>
    /// <param name="samples">How many points to take. Two or more.</param>
    /// <returns>The guide.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes docs/plan/41 § D10's "an asset, not a paint session" true rather
    ///         than aspirational.</b> A <c>.vxspline</c> is already an asset with an importer, an
    ///         editor and a serializer — doc 31 built all three — so a guide is a curve saved beside
    ///         the mesh, and re-generating the source does not throw the direction away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sampled by <i>distance</i> and not by parameter.</b> <see cref="Spline" />'s
    ///         parameter is segment-space, so an evenly spaced parameter is unevenly spaced points —
    ///         and the feature detector claims edges by proximity to the polyline, so a stretch where
    ///         the samples thinned out is a stretch of the guide that quietly does nothing.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="spline" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples" /> is below two.</exception>
    public static RemeshGuide ToGuide(Spline spline, float strength = 1f, int samples = DefaultGuideSamples) {
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

        var points = new Vector3[samples];
        var length = spline.Length;

        for (var index = 0; index < samples; index++) {
            points[index] = spline.EvaluateAtDistance(length * index / (samples - 1));
        }

        return new(points, Math.Clamp(strength, 0f, 1f));
    }

    /// <summary>How many points a guide curve is sampled at by default.</summary>
    /// <remarks>
    ///     ⚠ <b>Dense, and the reason is the detector's tolerance rather than the curve's shape.</b>
    ///     <c>FeatureDetector</c> claims an edge whose midpoint is within one percent of the diagonal
    ///     of the polyline, so under-sampling a guide does not make it a coarser guide — it makes it a
    ///     guide with gaps in it, which chains into several short features that the prune then throws
    ///     away.
    /// </remarks>
    public const int DefaultGuideSamples = 128;
}
