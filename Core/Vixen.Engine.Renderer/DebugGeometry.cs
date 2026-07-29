// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Rendering;

namespace Vixen.Engine.Renderer;

/// <summary>Where the camera is, which is all the geometry builder needs of a view.</summary>
/// <param name="Right">The camera's world-space right axis, unit length.</param>
/// <param name="Up">Its world-space up axis, unit length.</param>
/// <remarks>
///     Two axes rather than a whole camera, because the only thing world-space text needs is the
///     plane to lie in. Taking the basis rather than the matrix also means a caller with an
///     orthographic view, a shadow view or a hand-built one can billboard against it without first
///     having to construct something that calls itself a camera.
/// </remarks>
public readonly record struct DebugView(Vector3 Right, Vector3 Up) {
    /// <summary>The basis a world-to-view matrix implies.</summary>
    /// <param name="view">The world-to-view matrix.</param>
    /// <returns>The camera's right and up axes, in world space.</returns>
    /// <remarks>
    ///     The <i>columns</i> of the rotation part, not the rows. A world-to-view transform is the
    ///     inverse of the camera's own, and for an orthonormal rotation the inverse is the transpose
    ///     — so the camera's world-space right axis is the first column of the view matrix. Reading
    ///     the rows instead yields a basis that is correct only when the camera is at the origin
    ///     looking down −Z, which is exactly the arrangement a first test is written in.
    /// </remarks>
    public static DebugView FromView(in Matrix4x4 view) =>
        new(
            Vector3.Normalize(new(view.M11, view.M21, view.M31)),
            Vector3.Normalize(new(view.M12, view.M22, view.M32))
        );
}

/// <summary>
///     Turns a frame's <see cref="DebugDraw" /> into the two vertex spans a line pipeline draws.
/// </summary>
/// <remarks>
///     <para>
///         Split out of <see cref="DebugDrawRenderer" /> because this half is pure arithmetic over
///         two lists and is worth testing without a device — which is the same reason
///         <c>DrawListBuilder</c> is not part of <c>UiRenderer</c>.
///     </para>
///     <para>
///         <b>Two buffers, not one.</b> World geometry is drawn with the view-projection and is
///         depth-tested against the scene; screen geometry is drawn with a pixel-to-clip matrix and
///         must never be. They cannot share a draw because they do not share a transform, and that
///         is the whole reason there are exactly two — [13](../../docs/plan/13-diagnostics.md)
///         asks for "one draw call per primitive type per frame", and with everything reduced to
///         lines there are two types: the ones in the world and the ones on the glass.
///     </para>
/// </remarks>
public sealed class DebugGeometry {
    readonly List<LineVertex> world = [];
    readonly List<LineVertex> screen = [];

    /// <summary>The world-space vertices, two per segment.</summary>
    public ReadOnlySpan<LineVertex> World => CollectionsMarshal.AsSpan(world);

    /// <summary>The screen-space vertices, two per segment, in pixels with y running down.</summary>
    public ReadOnlySpan<LineVertex> Screen => CollectionsMarshal.AsSpan(screen);

    /// <summary>How many labels the last build turned into strokes.</summary>
    public int LabelCount { get; private set; }

    /// <summary>Rebuilds both spans from an accumulator.</summary>
    /// <param name="draw">What to drain.</param>
    /// <param name="view">The camera basis world-space labels are faced along.</param>
    public void Build(DebugDraw draw, in DebugView view) {
        ArgumentNullException.ThrowIfNull(draw);

        world.Clear();
        screen.Clear();
        LabelCount = 0;

        var lines = draw.Lines;
        world.EnsureCapacity(lines.Length * 2);

        foreach (var line in lines) {
            world.Add(new(line.From, line.Colour));
            world.Add(new(line.To, line.Colour));
        }

        foreach (var label in draw.Texts) {
            Label(label, view);
            LabelCount++;
        }

        var screenLines = draw.ScreenLines;
        screen.EnsureCapacity(screenLines.Length * 2);

        foreach (var line in screenLines) {
            // Zero depth, because the screen pass runs with the depth test off and its projection
            // puts every vertex on one plane anyway. Carrying a per-line depth would be a sort order
            // nothing writes and nothing reads.
            screen.Add(new(new(line.From.X, line.From.Y, 0f), line.Colour));
            screen.Add(new(new(line.To.X, line.To.Y, 0f), line.Colour));
        }
    }

    /// <summary>Throws both spans away.</summary>
    public void Clear() {
        world.Clear();
        screen.Clear();
        LabelCount = 0;
    }

    void Label(in DebugText label, in DebugView view) {
        // Vertically centred on the point, so a label attached to an entity does not sit above the
        // thing it names — and horizontally left-aligned, so a column of them lines up.
        var half = DebugFont.MeasureHeight(label.Text, label.Size) * 0.5f;
        var sink = new WorldSink(world, label.Position, view, half, label.Colour);

        world.EnsureCapacity(world.Count + (DebugFont.SegmentCount(label.Text) * 2));
        DebugFont.Emit(label.Text, Vector2.Zero, label.Size, ref sink);
    }

    /// <summary>Maps text space onto the camera's plane at a world point.</summary>
    /// <remarks>
    ///     ⚠ <b>Text space has y running down and the camera's up axis runs up</b>, so the vertical
    ///     term is subtracted. Adding it draws every label upside down, which is a mistake that looks
    ///     like a projection bug and is not one.
    /// </remarks>
    readonly struct WorldSink(
        List<LineVertex> destination,
        Vector3 origin,
        DebugView view,
        float rise,
        Color4 colour
    ) : IDebugFontSink {
        /// <inheritdoc />
        public void Segment(Vector2 head, Vector2 tail) {
            destination.Add(new(Place(head), colour));
            destination.Add(new(Place(tail), colour));
        }

        Vector3 Place(Vector2 point) =>
            origin + (view.Right * point.X) + (view.Up * (rise - point.Y));
    }
}
