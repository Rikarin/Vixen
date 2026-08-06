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

    /// <summary>Remeshes half the source and reflects it.</summary>
    /// <param name="source">The input, in the plane's own space.</param>
    /// <param name="settings">The settings, whose <see cref="RemeshSettings.Symmetry" /> brought us here.</param>
    /// <param name="plane">Where to mirror.</param>
    /// <param name="report">What happened, from the half's own remesh with this pass's warnings added.</param>
    /// <param name="scheduler">Workers for the field solve, or null.</param>
    /// <returns>The symmetric all-quad result, or whatever the fallback produced.</returns>
    public static EditMesh Remesh(
        EditMesh source,
        RemeshSettings settings,
        Plane plane,
        out RemeshReport report,
        JobScheduler? scheduler
    ) {
        var mirror = Plane.Normalize(plane);

        // ⚠ The half is taken from the *back*, so the kept side is the one the normal points away
        // from. Which side is kept is arbitrary and the choice has to be written down, because a
        // caller who flips their plane's normal expecting the same mesh gets the other half — and on
        // an asymmetric input that is a different model, not a different orientation.
        var half = mirror.Normal.IsZero ? null : MeshBoolean.PlaneCut(source, mirror, keepFront: false, cap: false);

        if (half is null || half.IsEmpty) {
            var whole = Remesher.Remesh(source, settings with { Symmetry = null }, out report, scheduler);

            report = With(
                report,
                "Symmetry was requested and the plane cut left nothing to solve on, so the whole mesh "
                + "was remeshed without it. docs/plan/41 § D11."
            );

            return whole;
        }

        var solved = Remesher.Remesh(half, settings with { Symmetry = null }, out report, scheduler);

        if (solved.IsEmpty) {
            report = With(report, "Symmetry was requested and the half the plane cut refused to remesh.");

            return solved;
        }

        var axis = AxisOf(mirror);
        var warnings = new List<string>();

        if (axis < 0) {
            warnings.Add(
                "Symmetry was applied about a plane that is not an axis through the origin, so the "
                + "mirror is a rounded reflection rather than a sign flip and docs/plan/41's fourth "
                + "exit criterion does not hold bit-for-bit."
            );
        }

        var built = Reflect(solved, mirror, axis, Diagonal(solved));
        var (quads, others) = RemeshMetrics.Faces(built);

        // ⚠ The counts are recomputed rather than doubled. A face whose mirror collapsed onto the
        // seam is not emitted, so "twice the half's" is wrong by however many of those there were —
        // and a report that disagrees with the mesh it describes is worse than no report.
        report = With(report with { QuadCount = quads, NonQuadCount = others, Mesh = built.Validate() }, [.. warnings]);

        return built;
    }

    /// <summary>Snaps the seam, reflects everything off it, and shares the seam between the halves.</summary>
    static EditMesh Reflect(EditMesh half, Plane plane, int axis, float diagonal) {
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

            // ⚠ Reversed, because a reflection is orientation-reversing. Copying the winding gives a
            // mesh whose two halves face opposite ways — which draws as a model with one side
            // inside-out and validates as one whose every seam edge has two faces pointing the same
            // way round it, so a manifold check passes and the render is wrong.
            for (var corner = 0; corner < corners.Length; corner++) {
                loop[corner] = mirrored[corners[corners.Length - 1 - corner]];
            }

            if (Distinct(loop[..corners.Length])) {
                built.AddFace(loop[..corners.Length], entry.Group, entry.Smoothing);
            }
        }

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
