// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Remeshing;

/// <summary>docs/plan/41 § D11's exact mirror: solve one half, snap to the plane, reflect, share the seam.</summary>
/// <remarks>
///     <para>
///         <b>The difference from ZRemesher's symmetry is the whole of this file.</b> Its symmetry
///         produces mirrored <i>flow</i> — the two halves look alike and their vertices do not
///         correspond. Here the layout is solved on one half and the other half is that half's
///         reflection, so output vertex <i>k</i> and its mirror are the same vertex twice. A character
///         remeshed this way can be rigged once and mirrored, blend-shaped and weight-painted across
///         the plane without a correspondence search.
///     </para>
///     <para>
///         ⚠ <b>Vertices on the plane are snapped to it exactly and then <i>shared</i>, not welded by
///         tolerance.</b> § D11 names the failure this avoids: a tolerance weld leaves a seam whose two
///         sides are two vertices that happen to be near each other, which is a one-vertex crack that
///         nothing shows until the mesh is subdivided and the limit surface pulls them apart. Here the
///         seam is one vertex referenced by faces on both sides, so there is no pair to disagree.
///     </para>
///     <para>
///         ⚠ <b>Exactness is bit-exactness, and it is only claimed for an axis-aligned plane through
///         the origin.</b> On that plane — which is what a character's symmetry plane is — the snap is
///         a store of <c>0f</c> and the reflection is a sign-bit flip, both of which are exact for
///         every float. On a general plane the reflection is
///         <c>p − 2(n·p + d)n</c> evaluated in <c>float</c>, which rounds; the report says so rather
///         than letting a caller believe a guarantee that is not there. Exit criterion 4 is written
///         about the axis-aligned case for the same reason.
///     </para>
///     <para>
///         ⚠ <b>The cut is a plane cut with no cap, and that is deliberate.</b> A capped half would be
///         a solid whose cap is a face the remesher would happily lay a patch across, and the seam
///         would end up somewhere near the plane instead of on it. An uncapped half is a surface whose
///         open rim <i>is</i> the plane, which <see cref="RemeshSettings.FreezeBorder" /> then pins —
///         so the vertices that arrive at the snap are already there to within the extraction's error
///         rather than to within the layout's.
///     </para>
///     <para>
///         ⚠ <b>Stage seven runs here rather than inside the half's own remesh, because the
///         attributes are indexed by the <i>uncut</i> source.</b> A colour per corner and a weight per
///         position belong to the mesh the caller handed in, and the plane cut renumbered both. So the
///         inner remesh is asked for geometry alone and the transfer is made from the original source
///         onto the half's output — which is also the better question, since the cut's own rim is not
///         a surface the source ever had. <see cref="AttributeMirror" /> then reflects what came back.
///     </para>
/// </remarks>
static class SymmetryPass {
    /// <summary>How near the plane a vertex must be to count as on it, as a fraction of the diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>A tolerance for <i>classification</i> and never for welding, and the two are not the
    ///     same decision.</b> Deciding which vertices the frozen border put on the plane is a question
    ///     about the extraction's error and has to have a number. What that number must not do is
    ///     decide whether two vertices become one — that is settled by construction, because a vertex
    ///     classified onto the plane is never mirrored at all.
    /// </remarks>
    public const float SeamTolerance = 1e-3f;

    /// <summary>Remeshes half the source, reflects it, and reflects what the transfer carried onto it.</summary>
    /// <param name="source">The input, in the plane's own space.</param>
    /// <param name="attributes">The channels the mesh has no room for, indexed by the uncut source.</param>
    /// <param name="settings">The settings, whose <see cref="RemeshSettings.Symmetry" /> brought us here.</param>
    /// <param name="plane">Where to mirror.</param>
    /// <param name="report">What happened, from the half's own remesh with this pass's warnings added.</param>
    /// <param name="transferred">The colours and weights that came out, mirrored across the plane.</param>
    /// <param name="scheduler">Workers for the field solve, or null.</param>
    /// <returns>The symmetric all-quad result, or whatever the fallback produced.</returns>
    public static EditMesh Remesh(
        EditMesh source,
        SourceAttributes attributes,
        RemeshSettings settings,
        Plane plane,
        out RemeshReport report,
        out TransferResult transferred,
        JobScheduler? scheduler
    ) {
        var mirror = Plane.Normalize(plane);

        // ⚠ The half is taken from the *back*, so the kept side is the one the normal points away
        // from. Which side is kept is arbitrary and the choice has to be written down, because a
        // caller who flips their plane's normal expecting the same mesh gets the other half — and on
        // an asymmetric input that is a different model, not a different orientation.
        var half = mirror.Normal.IsZero ? null : MeshBoolean.PlaneCut(source, mirror, keepFront: false, cap: false);

        if (half is null || half.IsEmpty) {
            // ⚠ The bone map is not wanted on this branch and is not asked for. Nothing is mirrored
            // here — this is an ordinary whole-mesh remesh — so a rigged source goes through with its
            // weights intact rather than being refused for the want of a map it does not need.
            var whole = Remesher.Remesh(
                source,
                attributes,
                settings with { Symmetry = null },
                out report,
                out transferred,
                scheduler
            );

            report = With(
                report,
                "Symmetry was requested and the plane cut left nothing to solve on, so the whole mesh "
                + "was remeshed without it. docs/plan/41 § D11."
            );

            return whole;
        }

        var warnings = new List<string>();
        var refusal = settings.TransferAttributes ? AttributeMirror.Refusal(attributes) : null;

        if (refusal is not null) {
            // Refused as a *result* rather than as a stage: the mesh still gets its normals, its
            // coordinates and its groups, which no bone map is needed for. What the caller does not
            // get is a binding, and the warning says why rather than the channel being silently short.
            warnings.Add(refusal);
            attributes = SourceAttributes.None;
        }

        // Geometry only. Stage seven is run below, against the source the attributes are indexed by.
        var inner = settings with { Symmetry = null, TransferAttributes = false };
        var solved = Remesher.Remesh(half, inner, out report, scheduler);

        if (solved.IsEmpty) {
            transferred = new([], null, 0, 0, warnings);
            report = With(report, [.. warnings, "Symmetry was requested and the half the plane cut refused to remesh."]);

            return solved;
        }

        var carried = new TransferResult([], null, 0, 0, []);

        if (settings.TransferAttributes) {
            // The same expression the whole-mesh path uses: both this and the atlas write the
            // coordinate layer, and the atlas — which the inner remesh has already run — is the one
            // that wins.
            carried = AttributeTransfer.Transfer(
                source,
                attributes,
                solved,
                settings.Transfer with { KeepTexCoords = settings.Transfer.KeepTexCoords && !settings.GenerateUvs }
            );
        }

        var axis = AxisOf(mirror);

        if (axis < 0) {
            warnings.Add(
                "Symmetry was applied about a plane that is not an axis through the origin, so the "
                + "mirror is a rounded reflection rather than a sign flip and docs/plan/41's fourth "
                + "exit criterion does not hold bit-for-bit."
            );
        }

        var built = Reflect(solved, mirror, axis, Diagonal(solved), out var correspondence);

        AttributeMirror.Layers(solved, built, correspondence, mirror, axis);

        if (refusal is null) {
            transferred = AttributeMirror.Reflect(carried, correspondence, attributes.BoneMirror);
            warnings.AddRange(transferred.Warnings);
        } else {
            // ⚠ The refusal goes in both places. A caller who reads the report gets it either way; a
            // caller who only holds the result would otherwise be handed an empty binding with
            // nothing on it saying why, which is the shape of a bug rather than of a refusal.
            transferred = new([], null, 0, 0, [refusal]);
        }

        var (quads, others) = RemeshMetrics.Faces(built);

        // ⚠ The counts are recomputed rather than doubled. A face whose mirror collapsed onto the
        // seam is not emitted, so "twice the half's" is wrong by however many of those there were —
        // and a report that disagrees with the mesh it describes is worse than no report.
        report = With(
            Restage(report, settings.TransferAttributes ? built.CornerCount : 0)
                with { QuadCount = quads, NonQuadCount = others, Mesh = built.Validate() },
            [.. warnings]
        );

        return built;
    }

    /// <summary>Puts the corners this pass transferred back into the transfer stage's element count.</summary>
    /// <remarks>
    ///     ⚠ <b>The inner remesh was asked for geometry, so its transfer stage counted only the
    ///     atlas's charts.</b> Without this a symmetric remesh's report means something different from
    ///     every other remesh's, and <c>LayoutAtlasTests</c> reads the chart count out of exactly this
    ///     field by subtracting the corners back off.
    /// </remarks>
    static RemeshReport Restage(RemeshReport report, int corners) {
        if (corners == 0) {
            return report;
        }

        var stages = new List<RemeshStageTiming>(report.Stages);

        for (var at = 0; at < stages.Count; at++) {
            if (stages[at].Stage == RemeshStage.Transfer) {
                stages[at] = stages[at] with { Elements = stages[at].Elements + corners };
            }
        }

        return report with { Stages = stages };
    }

    /// <summary>Snaps the seam, reflects everything off it, and shares the seam between the halves.</summary>
    static EditMesh Reflect(
        EditMesh half,
        Plane plane,
        int axis,
        float diagonal,
        out MirrorCorrespondence correspondence
    ) {
        var built = new EditMesh();
        var tolerance = diagonal * SeamTolerance;
        var count = half.PositionCount;

        // -1 for a vertex that is its own mirror. Everything else gets an index of its own, and the
        // two arrays together are the whole correspondence docs/plan/41 § D11 promises.
        var mirrored = new int[count];

        for (var index = 0; index < count; index++) {
            var position = half.Positions[index];

            if (Math.Abs(plane.DotCoordinate(position)) <= tolerance) {
                built.AddPosition(Snap(position, plane, axis));
                mirrored[index] = -1;
            } else {
                built.AddPosition(position);
                mirrored[index] = 0;
            }
        }

        for (var index = 0; index < count; index++) {
            if (mirrored[index] == 0) {
                mirrored[index] = built.AddPosition(Mirror(built.Positions[index], plane, axis));
            } else {
                mirrored[index] = index;
            }
        }

        var positionSource = new int[built.PositionCount];
        var positionIsMirror = new bool[built.PositionCount];
        var positionIsSeam = new bool[built.PositionCount];

        for (var index = 0; index < count; index++) {
            positionSource[index] = index;

            if (mirrored[index] == index) {
                positionIsSeam[index] = true;
            } else {
                positionSource[mirrored[index]] = index;
                positionIsMirror[mirrored[index]] = true;
            }
        }

        var cornerSource = new List<int>(half.CornerCount * 2);
        var cornerIsMirror = new List<bool>(half.CornerCount * 2);

        Span<int> loop = stackalloc int[8];

        for (var face = 0; face < half.FaceCount; face++) {
            var entry = half.Faces[face];
            var corners = half.CornersOf(face);

            if (corners.Length > loop.Length) {
                continue;
            }

            for (var corner = 0; corner < corners.Length; corner++) {
                loop[corner] = corners[corner];
            }

            built.AddFace(loop[..corners.Length], entry.Group, entry.Smoothing);

            for (var corner = 0; corner < corners.Length; corner++) {
                cornerSource.Add(entry.Start + corner);
                cornerIsMirror.Add(false);
            }

            // ⚠ Reversed, because a reflection is orientation-reversing. Copying the winding gives a
            // mesh whose two halves face opposite ways — which draws as a model with one side
            // inside-out and validates as one whose every seam edge has two faces pointing the same
            // way round it, so a manifold check passes and the render is wrong.
            for (var corner = 0; corner < corners.Length; corner++) {
                loop[corner] = mirrored[corners[corners.Length - 1 - corner]];
            }

            if (Distinct(loop[..corners.Length])) {
                built.AddFace(loop[..corners.Length], entry.Group, entry.Smoothing);

                // The corner layers follow the reversal, or a mirrored quad's normals belong to its
                // opposite corners and the shading rotates a quarter turn on half the model.
                for (var corner = 0; corner < corners.Length; corner++) {
                    cornerSource.Add(entry.Start + corners.Length - 1 - corner);
                    cornerIsMirror.Add(true);
                }
            }
        }

        correspondence = new() {
            PositionSource = positionSource,
            PositionIsMirror = positionIsMirror,
            PositionIsSeam = positionIsSeam,
            CornerSource = [.. cornerSource],
            CornerIsMirror = [.. cornerIsMirror]
        };

        return built;
    }

    /// <summary>A position moved onto the plane, exactly when the plane allows exactly.</summary>
    static Vector3 Snap(Vector3 position, Plane plane, int axis) =>
        axis switch {
            0 => new(0f, position.Y, position.Z),
            1 => new(position.X, 0f, position.Z),
            2 => new(position.X, position.Y, 0f),
            _ => position - plane.Normal * plane.DotCoordinate(position)
        };

    /// <summary>A position's reflection: a sign-bit flip on an axis plane, a rounded one otherwise.</summary>
    static Vector3 Mirror(Vector3 position, Plane plane, int axis) =>
        axis switch {
            0 => new(-position.X, position.Y, position.Z),
            1 => new(position.X, -position.Y, position.Z),
            2 => new(position.X, position.Y, -position.Z),
            _ => position - plane.Normal * (2f * plane.DotCoordinate(position))
        };

    /// <summary>A direction's reflection, which is the position's without the plane's offset in it.</summary>
    /// <param name="direction">The vector to reflect.</param>
    /// <param name="plane">The mirror plane.</param>
    /// <param name="axis">Which axis it is, or −1 for a general plane.</param>
    /// <returns>The reflected direction.</returns>
    /// <remarks>
    ///     ⚠ <b>A normal is a direction and reflecting it through <see cref="Plane.DotCoordinate" />
    ///     would translate it.</b> On a plane through the origin the two agree and the mistake is
    ///     invisible; on any other the normals of the mirrored half come back offset by twice the
    ///     plane's distance, which is a lighting bug that appears the day somebody mirrors about a
    ///     plane that is not at zero.
    /// </remarks>
    public static Vector3 MirrorDirection(Vector3 direction, Plane plane, int axis) =>
        axis switch {
            0 => new(-direction.X, direction.Y, direction.Z),
            1 => new(direction.X, -direction.Y, direction.Z),
            2 => new(direction.X, direction.Y, -direction.Z),
            _ => direction - (plane.Normal * (2f * Vector3.Dot(plane.Normal, direction)))
        };

    /// <summary>Which axis the plane is, or −1 when it is not one through the origin.</summary>
    /// <remarks>
    ///     ⚠ <b>Compared against exactly ±1 rather than nearly.</b> The exactness claim is about float
    ///     arithmetic, and a plane whose normal is 0.9999999 of an axis reflects to something that is
    ///     not a sign flip — so a near-miss has to take the general path and say so, rather than take
    ///     the exact path and be wrong in the last bit where nobody looks.
    /// </remarks>
    static int AxisOf(Plane plane) {
        if (plane.D != 0f) {
            return -1;
        }

        var normal = plane.Normal;

        return (normal.X, normal.Y, normal.Z) switch {
            (1f or -1f, 0f, 0f) => 0,
            (0f, 1f or -1f, 0f) => 1,
            (0f, 0f, 1f or -1f) => 2,
            _ => -1
        };
    }

    /// <summary>Whether a loop's corners are all different, which a degenerate mirror can break.</summary>
    static bool Distinct(ReadOnlySpan<int> loop) {
        for (var index = 0; index < loop.Length; index++) {
            for (var other = index + 1; other < loop.Length; other++) {
                if (loop[index] == loop[other]) {
                    return false;
                }
            }
        }

        return true;
    }

    static float Diagonal(EditMesh mesh) => mesh.Bounds.Size.Length();

    static RemeshReport With(RemeshReport report, params string[] added) =>
        report with { Warnings = [.. report.Warnings, .. added] };
}
