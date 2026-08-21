// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>The half of the Profile tool a person aims at: the handles, drawn and hit-tested.</summary>
/// <remarks>
///     <para>
///         <b><c>WaterEdit.HandlesOf</c> knew where the handles were for two phases and nothing drew
///         them.</b> A viewport handle that is not on screen is a number field with worse ergonomics
///         — the author has nothing to aim at, so the tool reads as doing nothing at all. This is
///         <c>SplineOverlay</c>'s job for water's three handles and <c>TransformGizmo.HitTest</c>'s
///         for the click that grabs one.
///     </para>
///     <para>
///         ⚠ <b>Hit-tested in render pixels and not in metres.</b> A tolerance in metres is a handle
///         that cannot be missed on a canal and cannot be hit on an ocean, because the same half-width
///         is forty pixels from one camera and one pixel from another. <c>TransformGizmo.GrabRadius</c>
///         is the same number for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Depth is tested before the two widths.</b> A body whose bed depth and half-width are
///         near enough equal puts the depth handle inside a width handle's grab radius from a level
///         camera, and the width is the one an author can also reach from every other angle. Ordering
///         it first is <c>TransformGizmo.HitTest</c>'s "the centre circle before the arms" rule.
///     </para>
/// </remarks>
static class WaterProfileHandles {
    /// <summary>The most control points handles are drawn and tested for, however long the curve.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a hope</b>, on <c>SplineOverlay.MaximumSamples</c>'s reasoning: a river
    ///     imported from a heightfield trace is thousands of control points, and three handles each is
    ///     a hit test walking ten thousand projections on every pointer move — which drops the editor
    ///     rather than the river.
    /// </remarks>
    public const int MaximumPoints = 256;

    /// <summary>How big a handle's cross is drawn, in render pixels.</summary>
    public const float MarkerPixels = 7f;

    /// <summary>What a width handle is drawn in.</summary>
    /// <remarks>
    ///     A green nothing else in the viewport uses. Not the cyan <c>TerrainCursor</c> takes and not
    ///     the amber a selection is: all three are on screen together while a river is being widened
    ///     on carved ground, and they mean three different things.
    /// </remarks>
    public static Color4 WidthColour { get; } = new(0.4f, 0.95f, 0.5f, 0.95f);

    /// <summary>And the depth handle, which is the one that goes down.</summary>
    public static Color4 DepthColour { get; } = new(0.95f, 0.7f, 0.3f, 0.95f);

    /// <summary>And whichever one is being held, so a drag says what it is dragging.</summary>
    public static Color4 HeldColour { get; } = new(1f, 1f, 1f, 1f);

    /// <summary>The bar from the curve out to a handle, which is what makes the width readable.</summary>
    public static Color4 BarColour { get; } = new(0.4f, 0.95f, 0.5f, 0.35f);

    /// <summary>Which handle is under the pointer, and which control point it belongs to.</summary>
    /// <param name="pane">The pane, for its camera and its render size.</param>
    /// <param name="pointer">Where the pointer is, in render pixels.</param>
    /// <param name="spline">The body's curve, in world space.</param>
    /// <param name="profile">The body's profile.</param>
    /// <param name="radius">How near is near enough, in render pixels.</param>
    /// <returns>The handle and its control point, or <see cref="WaterHandle.None" /> and −1.</returns>
    public static (WaterHandle Handle, int Point) Under(
        SceneViewport pane,
        Vector2 pointer,
        Spline spline,
        in WaterProfilePoint profile,
        float radius
    ) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(spline);

        var width = pane.Control.RenderWidth;
        var height = pane.Control.RenderHeight;
        var camera = pane.Camera;
        var nearest = radius * radius;
        var found = (Handle: WaterHandle.None, Point: -1);

        for (var index = 0; index < Math.Min(spline.Points.Length, MaximumPoints); index++) {
            var (left, right, depth) = WaterEdit.HandlesOf(spline, profile, index);

            Consider(WaterHandle.Depth, depth, index);
            Consider(WaterHandle.WidthLeft, left, index);
            Consider(WaterHandle.WidthRight, right, index);
        }

        return found;

        void Consider(WaterHandle handle, Vector3 at, int index) {
            // ⚠ Asked rather than projected, because a perspective projection has an answer for a
            // point behind the eye and the answer is mirrored through the middle of the pane — see
            // EditorCamera.TryProject. A handle folded round that way sits under the pointer
            // regularly, and grabbing it makes the drag start somewhere the author was not aiming.
            if (!camera.TryProject(at, width, height, out var screen)) {
                return;
            }

            var distance = (screen - pointer).LengthSquared();

            if (distance <= nearest) {
                nearest = distance;
                found = (handle, index);
            }
        }
    }

    /// <summary>Draws every handle of a body, with the held one lit.</summary>
    /// <param name="draw">Where the lines go — the pane's overlay channel.</param>
    /// <param name="pane">The pane, for the camera the marker size follows.</param>
    /// <param name="spline">The body's curve, in world space.</param>
    /// <param name="profile">The body's profile.</param>
    /// <param name="held">Which handle is being dragged, or <see cref="WaterHandle.None" />.</param>
    /// <param name="heldPoint">Which control point that one belongs to.</param>
    /// <remarks>
    ///     ⚠ <b>The markers are a constant size on screen rather than in metres.</b> A cross half a
    ///     metre across is invisible on an ocean and fills the pane on a canal, and the thing an author
    ///     is looking for is "where do I click", which is a pixel question.
    /// </remarks>
    public static void Draw(
        GizmoDraw draw,
        SceneViewport pane,
        Spline spline,
        in WaterProfilePoint profile,
        WaterHandle held = WaterHandle.None,
        int heldPoint = -1
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(spline);

        var height = pane.Control.RenderHeight;

        for (var index = 0; index < Math.Min(spline.Points.Length, MaximumPoints); index++) {
            var (left, right, depth) = WaterEdit.HandlesOf(spline, profile, index);
            var centre = spline.FrameAt(Math.Clamp(index, 0, spline.Points.Length - 1), Vector3.UnitY).Position;

            draw.Line(left, right, BarColour);
            draw.Line(centre, depth, BarColour);

            Marker(WaterHandle.WidthLeft, left, index, WidthColour);
            Marker(WaterHandle.WidthRight, right, index, WidthColour);
            Marker(WaterHandle.Depth, depth, index, DepthColour);
        }

        void Marker(WaterHandle handle, Vector3 at, int index, Color4 colour) {
            var size = pane.Camera.WorldPerPixel(at, height) * MarkerPixels;

            if (!(size > 0f) || !float.IsFinite(size)) {
                return;
            }

            var lit = handle == held && index == heldPoint ? HeldColour : colour;

            draw.Line(at - (Vector3.UnitX * size), at + (Vector3.UnitX * size), lit);
            draw.Line(at - (Vector3.UnitY * size), at + (Vector3.UnitY * size), lit);
            draw.Line(at - (Vector3.UnitZ * size), at + (Vector3.UnitZ * size), lit);
        }
    }
}
