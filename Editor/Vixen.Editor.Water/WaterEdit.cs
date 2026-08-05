// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>What a click in the water mode means right now.</summary>
/// <remarks>
///     Three, because three is what
///     [35 § One mode, not two](../../docs/plan/35-water.md#one-mode-not-two) says is not already
///     entity editing. Placing a lake is placing an entity and editing its shape is editing a spline,
///     both of which the existing tools already do.
/// </remarks>
public enum WaterTool {
    /// <summary>Click a series of points on the ground to lay a body's curve.</summary>
    /// <remarks>The one gesture that is not already served by <c>SplineEdit</c>.</remarks>
    Draw,

    /// <summary>Drag the width, depth and velocity handles on an existing body.</summary>
    /// <remarks>
    ///     Unreal's three viewport visualisations, and they are the reason its river authoring is
    ///     good: a width somebody types is a width somebody gets wrong twice before looking at it.
    /// </remarks>
    Profile,

    /// <summary>Toggle the reserved layer's contribution, to see what the water did to the ground.</summary>
    Preview
}

/// <summary>Which handle of a control point a drag is holding.</summary>
public enum WaterHandle {
    /// <summary>None — the drag is not on a handle.</summary>
    None,

    /// <summary>The half-width, on the curve's left.</summary>
    WidthLeft,

    /// <summary>And on its right. Two handles for one number, because a river is dragged from a bank.</summary>
    WidthRight,

    /// <summary>The bed depth, dragged downward.</summary>
    Depth
}

/// <summary>
///     The water mode's editing state: a draw in progress, or a profile handle under the pointer.
/// </summary>
/// <remarks>
///     <para>
///         <b>No device, no world and no document</b>, on <c>FoliageEdit</c>'s terms: everything here
///         is arithmetic over points and a profile, so the gestures are testable with synthetic
///         pointer input and no editor running — which is
///         [§ Part 4](../../docs/plan/35-water.md#part-4--testing)'s "asserting the spline and the
///         profile rather than the pixels".
///     </para>
///     <para>
///         ⚠ <b>A draw commits to a <em>curve and a profile</em>, never to an entity.</b> What the
///         mode does with them is the module's business — a scene component naming a
///         <c>.vxspline</c> the project wrote — and a gesture that created entities could not be run
///         by a test without a world.
///     </para>
/// </remarks>
public sealed class WaterEdit {
    readonly List<Vector3> drawn = [];

    /// <summary>Which tool a click runs.</summary>
    public WaterTool Tool { get; set; } = WaterTool.Draw;

    /// <summary>What kind of body the next draw lays.</summary>
    /// <remarks>
    ///     ⚠ <b>It decides whether the curve closes</b>, which is the whole difference between a lake
    ///     and a river and is a decision that cannot be made after the fact: an open curve has no
    ///     inside, so <see cref="WaterBody" /> refuses one for every kind but a river.
    /// </remarks>
    public WaterBodyKind Kind { get; set; } = WaterBodyKind.Lake;

    /// <summary>The profile the next body is given, and the one a handle drag edits.</summary>
    public WaterProfilePoint Profile { get; set; } = new() { HalfWidth = 4f, Depth = 3f };

    /// <summary>How far apart two clicks have to be to be two points, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Without it a double click lays two points in the same place</b>, and a spline segment
    ///     of no length has no tangent — which is a body whose <c>Reach</c> is finite and whose
    ///     boundary walk divides by zero. Refused at the gesture rather than at the constructor,
    ///     because the person who did it is holding the mouse.
    /// </remarks>
    public float MinimumSpacing { get; set; } = 0.5f;

    /// <summary>The points laid so far, in world space.</summary>
    public IReadOnlyList<Vector3> Points => drawn;

    /// <summary>Whether a draw is under way.</summary>
    public bool IsDrawing { get; private set; }

    /// <summary>Which handle a profile drag is holding.</summary>
    public WaterHandle Holding { get; private set; }

    /// <summary>Which control point that handle belongs to.</summary>
    public int HoldingPoint { get; private set; } = -1;

    /// <summary>Whether the carve's contribution is being shown.</summary>
    /// <remarks>
    ///     § "Preview the carve". A toggle rather than a tool that does something, because what it
    ///     changes is whether the reserved layer is composited — the ground with the water's cut in it
    ///     against the ground an author sculpted.
    /// </remarks>
    public bool CarvePreview { get; set; } = true;

    /// <summary>How near the first point a click has to be to close the curve, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Clicking the first point again is how a closed body is finished, because the UI has
    ///     no double click.</b> <c>PointerAction</c> reports moves, presses and releases and nothing
    ///     else — a click count is a fact about time that the event does not carry — so the gesture
    ///     every polygon tool offers second is the one offered first here. Enter finishes as well, and
    ///     is the only way to finish a river, which has no first point to come back to.
    /// </remarks>
    public float CloseRadius { get; set; } = 1.5f;

    /// <summary>Whether a click there would close the curve rather than lay another point.</summary>
    /// <param name="point">Where the click landed, in world space.</param>
    /// <returns>Whether it closes.</returns>
    /// <remarks>
    ///     Only for a body that closes, and only once there are enough points to enclose anything —
    ///     otherwise the second click of a lake would land inside the first's radius and finish a
    ///     curve with two points in it.
    /// </remarks>
    public bool ClosesAt(Vector3 point) {
        if (Kind == WaterBodyKind.River || !CanCommit || drawn.Count == 0) {
            return false;
        }

        var first = drawn[0];

        return new Vector2(point.X - first.X, point.Z - first.Z).Length() <= MathF.Max(CloseRadius, 0f);
    }

    /// <summary>Whether the points laid so far are enough to make a body of the current kind.</summary>
    /// <remarks>
    ///     Three for a closed body, because two points cannot enclose anything; two for a river,
    ///     which is a line.
    /// </remarks>
    public bool CanCommit => drawn.Count >= (Kind == WaterBodyKind.River ? 2 : 3);

    /// <summary>Starts a draw, discarding whatever was half-laid.</summary>
    public void Begin() {
        drawn.Clear();
        IsDrawing = true;
    }

    /// <summary>Lays one point.</summary>
    /// <param name="point">Where, in world space — the ground's own height included.</param>
    /// <returns>Whether it was far enough from the last one to count.</returns>
    /// <remarks>
    ///     The point carries its height, because a body drawn across a valley follows the ground: a
    ///     river's surface <em>is</em> its curve, and one laid at a single height is a canal drawn in
    ///     a curve rather than a river.
    /// </remarks>
    public bool Add(Vector3 point) {
        if (!IsDrawing) {
            Begin();
        }

        if (drawn.Count > 0) {
            var last = drawn[^1];
            var apart = new Vector2(point.X - last.X, point.Z - last.Z).Length();

            if (apart < MathF.Max(MinimumSpacing, 0f)) {
                return false;
            }
        }

        drawn.Add(point);

        return true;
    }

    /// <summary>Takes the last point back.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() {
        if (drawn.Count == 0) {
            return false;
        }

        drawn.RemoveAt(drawn.Count - 1);

        return true;
    }

    /// <summary>Drops the draw.</summary>
    public void Cancel() {
        drawn.Clear();
        IsDrawing = false;
        Holding = WaterHandle.None;
        HoldingPoint = -1;
    }

    /// <summary>Turns the points laid into a curve.</summary>
    /// <returns>The curve, or <see langword="null" /> if there are not enough points.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Closed for every kind but a river, and the tangents are smoothed across the
    ///         closure.</b> A closed curve whose tangents were computed as though it were open has a
    ///         corner where its two ends meet, which is a lake with a notch in it at the place the
    ///         author started drawing — and the author has no way to know that is why.
    ///     </para>
    ///     <para>
    ///         The draw is <em>not</em> ended by this. Committing a curve and continuing to hold the
    ///         gesture is what an author does when the first attempt is nearly right, and
    ///         <see cref="Cancel" /> is the one thing that clears it.
    ///     </para>
    /// </remarks>
    public Spline? Commit() {
        if (!CanCommit) {
            return null;
        }

        var closed = Kind != WaterBodyKind.River;

        return new(Spline.SmoothTangents([.. drawn], closed, tension: 1f), closed);
    }

    /// <summary>Takes hold of a profile handle.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="point">Which control point it belongs to.</param>
    public void Grab(WaterHandle handle, int point) {
        Holding = handle;
        HoldingPoint = handle == WaterHandle.None ? -1 : point;
    }

    /// <summary>Lets go.</summary>
    public void Release() => Grab(WaterHandle.None, -1);

    /// <summary>The profile a drag of the held handle produces.</summary>
    /// <param name="current">The profile before the drag.</param>
    /// <param name="metres">How far the handle was dragged, in metres, in its own direction.</param>
    /// <returns>The profile after it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both width handles edit the <em>same</em> number, and the sign is the
    ///         difference.</b> A river's channel is symmetric about its centreline, so two handles is
    ///         two grips on one value — dragging the left bank outward and the right bank outward both
    ///         widen it. Two independent half-widths would be a second number to author and a river
    ///         whose centreline is not its centre.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clamped at zero rather than allowed negative.</b> A negative half-width inverts
    ///         the containment test, so the body covers everywhere except itself — which draws as the
    ///         whole zone flooding and reads as a renderer bug.
    ///     </para>
    /// </remarks>
    public WaterProfilePoint Drag(in WaterProfilePoint current, float metres) =>
        Holding switch {
            WaterHandle.WidthLeft => current with { HalfWidth = MathF.Max(current.HalfWidth - metres, 0f) },
            WaterHandle.WidthRight => current with { HalfWidth = MathF.Max(current.HalfWidth + metres, 0f) },
            WaterHandle.Depth => current with { Depth = MathF.Max(current.Depth - metres, 0f) },
            _ => current
        };

    /// <summary>Where a control point's handles sit, in world space.</summary>
    /// <param name="body">The body.</param>
    /// <param name="index">Which control point.</param>
    /// <returns>The left width handle, the right one, and the depth handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The side is the curve's own frame and not world X.</b> A river that bends would
    ///     otherwise have its handles cross its own bank halfway round, and dragging one would widen
    ///     the channel in the wrong direction — which is the failure that makes a viewport handle
    ///     worse than a number field.
    /// </remarks>
    public static (Vector3 Left, Vector3 Right, Vector3 Depth) HandlesOf(WaterBody body, int index) {
        ArgumentNullException.ThrowIfNull(body);

        var t = Math.Clamp(index, 0, body.Spline.Points.Length - 1);
        var profile = body.ProfileAt(t);
        var frame = body.Spline.FrameAt(t, Vector3.UnitY);

        var tangent = new Vector3(frame.Tangent.X, 0f, frame.Tangent.Z);
        var length = tangent.Length();

        // A degenerate tangent answers +X rather than a NaN: a control point whose neighbours are on
        // top of it has no direction, and a NaN handle is one that cannot be clicked and cannot be
        // seen to be missing.
        var side = length > 0f
            ? Vector3.Cross(Vector3.UnitY, tangent / length)
            : Vector3.UnitX;

        var centre = frame.Position;

        return (
            centre - (side * profile.HalfWidth),
            centre + (side * profile.HalfWidth),
            centre - new Vector3(0f, profile.Depth, 0f)
        );
    }
}
