// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>The corner brackets drawn round a selected object, in the object's own axes.</summary>
/// <remarks>
///     <para>
///         <b>What says "this one is selected" in a pane the editor does not shade itself.</b> The
///         viewport has two other answers and both are the tool renderer's: an amber tint on the
///         surface — <see cref="SceneMeshes.SelectedColour" /> — and, before that, an inverted hull
///         expanded in the editor's own instanced mesh shader. Neither is in a frame a
///         <c>GraphicsCompositor</c> drew, so a composed pane showed a selected object exactly like an
///         unselected one while the gizmo sitting on it said otherwise. This is lines, and lines
///         survive that frame: <c>SceneLines</c> emits them and <c>FramePresenter</c>'s <c>Tools</c>
///         pass records them over the composition.
///     </para>
///     <para>
///         ⚠ <b>An overlay rather than a change to the surface, and in a composed pane that is not a
///         compromise — it is the only correct answer.</b> A composed pane exists to show the frame a
///         game would draw. Painting the selected object amber is precisely the one edit that
///         destroys what the pane is for: the material you selected the object to look at is the
///         thing you can no longer see. The tint is right in the tool pane, which is a diagram; a
///         picture wants its annotation drawn over it.
///     </para>
///     <para>
///         ⚠ <b>Brackets, not a box, because <see cref="SceneShow.Bounds" /> is already a box.</b>
///         That flag draws twelve continuous edges round <em>every</em> shaped entity — grey, and
///         amber for a selected one — as a diagnostic about extent. A selection cage drawn the same
///         way would be a second drawing indistinguishable from the first, which is the failure a box
///         round the selection walks into. Eight corners of three short segments is a broken box: it
///         reads as a bracket at each corner rather than as an edge, at a glance and at any distance.
///     </para>
///     <para>
///         ⚠ <b>And it stands off the extent by a width in <i>pixels</i>.</b> A cage exactly on the
///         box is coplanar with the bounds box when both are on and coincident with the silhouette
///         when neither is — either way it z-fights the thing it is annotating. A standoff in world
///         units would be invisible on a building and swallow a bolt, so this is the hull outline's
///         own rule kept: <c>EditorCamera.WorldPerPixel</c> at the object, so the gap is the width it
///         was asked for at every distance and in both projections.
///     </para>
///     <para>
///         ⚠ <b>Nothing here knows what a selection is.</b> It is handed a box, a matrix and a
///         colour, which is what makes it usable from a contributed gizmo and testable without a
///         scene. Which entities get one, and what each one's extent is, is <c>SceneLines</c>'s.
///     </para>
/// </remarks>
public static class SelectionCage {
    /// <summary>How much of each edge a bracket takes, as a fraction of that edge's whole length.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A fraction rather than a length, so the cage reads the same on a bolt and on a
    ///         building.</b> A bracket of a fixed number of world units is the whole edge on one and
    ///         invisible on the other, and a bracket of a fixed number of pixels turns into a
    ///         continuous box the moment the object is small on screen — which is exactly the
    ///         collision with <see cref="SceneShow.Bounds" /> the brackets exist to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Below a half, and not by a rounding.</b> At a half the two brackets on an edge
    ///         meet in the middle and the cage <i>is</i> a wire box. A quarter leaves the middle half
    ///         of every edge empty, which is the gap that has to be visible for the shape to read as
    ///         brackets at the distance somebody works from.
    ///     </para>
    /// </remarks>
    public const float Corner = 0.25f;

    /// <summary>How far outside the extent the cage sits, in render pixels.</summary>
    /// <remarks>
    ///     Four, which is wide enough that the gap survives a line two pixels wide beside a silhouette
    ///     and narrow enough that the cage still reads as belonging to the object rather than floating
    ///     near it.
    /// </remarks>
    public const float Standoff = 4f;

    /// <summary>Draws the cage round one object's extent.</summary>
    /// <param name="draw">Where the segments go — the viewport's depth-tested line list.</param>
    /// <param name="bounds">The object's extent, in its own space, before the transform.</param>
    /// <param name="transform">Its world matrix.</param>
    /// <param name="camera">The pane's camera, for the standoff.</param>
    /// <param name="height">How tall the pane is, in render pixels.</param>
    /// <param name="colour">What to draw it in.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     Twenty-four segments: three at each of the eight corners, one along each edge meeting
    ///     there. A degenerate box — a plane, whose extent is zero on one axis — still gets a cage,
    ///     because the standoff gives every axis a size before any of this is measured.
    /// </remarks>
    public static void Draw(
        GizmoDraw draw,
        in BoundingBox bounds,
        in Matrix4x4 transform,
        EditorCamera camera,
        int height,
        Color4 colour
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(camera);

        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var extent = Standoffed(bounds, transform, camera, height);

        // ⚠ The corners are indexed by which side of each axis they are on, which is what makes the
        // three edges at a corner the three indices differing from it in one bit — the same scheme
        // `SceneLines.Boxes` uses for the twelve edges of the bounds box, so the two are drawn from
        // the same eight points and cannot disagree about where a corner is.
        Span<Vector3> corners = stackalloc Vector3[8];

        for (var index = 0; index < 8; index++) {
            corners[index] = Matrix4x4.TransformPosition(
                centre + new Vector3(
                    (index & 1) == 0 ? -extent.X : extent.X,
                    (index & 2) == 0 ? -extent.Y : extent.Y,
                    (index & 4) == 0 ? -extent.Z : extent.Z
                ),
                transform
            );
        }

        for (var from = 0; from < 8; from++) {
            for (var bit = 1; bit < 8; bit <<= 1) {
                // ⚠ Both ways round, unlike the bounds box, which walks each edge once. A bracket is
                // not an edge: the edge between two corners carries one bracket at each end, so the
                // pair has to be visited from both.
                var to = from ^ bit;

                draw.Line(corners[from], Vector3.Lerp(corners[from], corners[to], Corner), colour);
            }
        }
    }

    /// <summary>The half-size of the box the cage is drawn on, which is the extent plus the standoff.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The standoff is a world distance and the extent is in the object's own space, so
    ///         it is divided by that axis's scale on the way in.</b> A crate scaled twelvefold on X
    ///         would otherwise get twelve times the gap on X and the cage would sit visibly crooked
    ///         round it — a bracket further from one face than from the next reads as a bug in the
    ///         bracket rather than as a scale on the object.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An axis with no length gets no standoff, rather than an infinite one.</b> An
    ///         entity scaled to zero on an axis contributes nothing to any world position through
    ///         that axis, so there is no distance the division could produce that means anything —
    ///         and the division itself is what turns a flattened entity into a cage at infinity, or
    ///         into no cage at all once the vertices are not finite.
    ///     </para>
    /// </remarks>
    static Vector3 Standoffed(in BoundingBox bounds, in Matrix4x4 transform, EditorCamera camera, int height) {
        var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;
        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var gap = Standoff * camera.WorldPerPixel(Matrix4x4.TransformPosition(centre, transform), height);

        return extent + new Vector3(
            Spread(gap, transform.Right.Length()),
            Spread(gap, transform.Up.Length()),
            Spread(gap, transform.Forward.Length())
        );
    }

    static float Spread(float gap, float scale) => scale > 0f ? gap / scale : 0f;
}
