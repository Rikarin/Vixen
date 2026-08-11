// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics;

/// <summary>What <see cref="DebugDraw.Advance" /> needs of an entry to be able to age it.</summary>
/// <typeparam name="TSelf">The implementing type, so the copy comes back as itself.</typeparam>
/// <remarks>
///     Internal, and a constraint rather than a base class or a delegate, so the ageing sweep is one
///     piece of code over three lists with no boxing and no indirect call — which matters because it
///     runs over every queued primitive every frame.
/// </remarks>
interface IDebugTimed<TSelf> where TSelf : struct {
    /// <summary>Seconds left to live.</summary>
    float Remaining { get; }

    /// <summary>The same entry with a different lifetime.</summary>
    /// <param name="remaining">The new lifetime.</param>
    /// <returns>The copy.</returns>
    TSelf WithRemaining(float remaining);
}

/// <summary>One line to draw, in world space.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Colour">What colour it is.</param>
/// <param name="Remaining">Seconds left to live. Zero means "this frame only".</param>
public readonly record struct DebugLine(Vector3 From, Vector3 To, Color4 Colour, float Remaining)
    : IDebugTimed<DebugLine> {
    /// <inheritdoc />
    public DebugLine WithRemaining(float remaining) => this with { Remaining = remaining };
}

/// <summary>One line to draw in screen space, in pixels from the top-left.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Colour">What colour it is.</param>
/// <param name="Remaining">Seconds left to live. Zero means "this frame only".</param>
public readonly record struct DebugScreenLine(Vector2 From, Vector2 To, Color4 Colour, float Remaining)
    : IDebugTimed<DebugScreenLine> {
    /// <inheritdoc />
    public DebugScreenLine WithRemaining(float remaining) => this with { Remaining = remaining };
}

/// <summary>A label at a point in the world, drawn facing the camera.</summary>
/// <param name="Position">Where the label's centre-left sits.</param>
/// <param name="Text">What it says.</param>
/// <param name="Colour">What colour it is.</param>
/// <param name="Size">The cap height, in world units.</param>
/// <param name="Remaining">Seconds left to live. Zero means "this frame only".</param>
/// <remarks>
///     Kept as text rather than expanded into lines at the call site, because which way "up" is on
///     the screen is the camera's business and a call site does not have one — the renderer turns
///     these into strokes once it knows the view.
/// </remarks>
public readonly record struct DebugText(Vector3 Position, string Text, Color4 Colour, float Size, float Remaining)
    : IDebugTimed<DebugText> {
    /// <inheritdoc />
    public DebugText WithRemaining(float remaining) => this with { Remaining = remaining };
}

/// <summary>
///     Immediate-mode debug geometry: say what you want to see, from wherever you are, and it is
///     there until it expires.
/// </summary>
/// <remarks>
///     <para>
///         The point is that a call site does not have to own anything. A collision routine that
///         wants to show a contact normal calls <see cref="Line" /> and forgets about it — no
///         resource to create, nothing to dispose, and nothing to remember to remove when the
///         investigation is over except the call itself.
///     </para>
///     <para>
///         <b>This is the accumulator, not the renderer.</b> Everything lands in one of three lists —
///         world lines, world labels, and screen-space lines — and <c>DebugDrawRenderer</c> drains
///         them once a frame and turns them into two draw calls. Splitting them that way is what lets
///         a subsystem be written against this with no reference to a graphics API at all: physics,
///         navigation and audio all produce debug geometry without linking a device.
///     </para>
///     <para>
///         Every shape is lines, including the round ones and the lettered ones. A debug sphere is
///         three rings and reads as a sphere; giving it a mesh would mean a second pipeline, a depth
///         decision and a lighting decision, for geometry whose whole job is to be unmistakably not
///         part of the scene. Text is <see cref="DebugFont" />'s strokes for the same reason — and
///         because a debug overlay has to work in the frame where the font atlas is the thing that is
///         broken.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe, on purpose.</b> The lists are plain <see cref="List{T}" />s and a
///         call is a handful of stores; making them safe would mean a lock on a path taken thousands
///         of times a frame, to serve a case — two threads drawing at once — that is better answered
///         by an accumulator per job. Draw from the loop thread, or from a job that owns one of these.
///     </para>
/// </remarks>
public sealed class DebugDraw {
    const int RingSegments = 24;

    /// <summary>How far back from the tip an arrow's head reaches, as a fraction of its length.</summary>
    const float ArrowHeadFraction = 0.2f;

    /// <summary>The widest an arrow head is drawn, in world units, however long the arrow is.</summary>
    const float ArrowHeadLimit = 0.5f;

    readonly List<DebugLine> lines = [];
    readonly List<DebugScreenLine> screenLines = [];
    readonly List<DebugText> texts = [];

    /// <summary>Whether anything is recorded at all. Off is free: every call returns immediately.</summary>
    /// <remarks>
    ///     A field to test rather than a compile-time flag, so a release build can turn it on for a
    ///     support case — which [13](../../../docs/plan/13-diagnostics.md) § Debug rendering asks for
    ///     in as many words, because production bugs need it. The cost of that choice is one
    ///     predictable branch per call.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>What a renderer drains for the world pass.</summary>
    public ReadOnlySpan<DebugLine> Lines => CollectionsMarshal.AsSpan(lines);

    /// <summary>What a renderer drains for the screen pass.</summary>
    public ReadOnlySpan<DebugScreenLine> ScreenLines => CollectionsMarshal.AsSpan(screenLines);

    /// <summary>The labels a renderer has to face at the camera before it can draw them.</summary>
    public ReadOnlySpan<DebugText> Texts => CollectionsMarshal.AsSpan(texts);

    /// <summary>How many world lines are queued.</summary>
    public int Count => lines.Count;

    /// <summary>How many screen lines are queued.</summary>
    public int ScreenCount => screenLines.Count;

    /// <summary>How many world labels are queued.</summary>
    public int TextCount => texts.Count;

    /// <summary>Draws a line.</summary>
    /// <param name="from">Where it starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts. Zero is this frame only.</param>
    public void Line(Vector3 from, Vector3 to, Color4 colour, float seconds = 0f) {
        if (Enabled) {
            lines.Add(new(from, to, colour, seconds));
        }
    }

    /// <summary>Draws a ray from a point along a direction.</summary>
    /// <param name="origin">Where it starts.</param>
    /// <param name="direction">Which way and how far.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Ray(Vector3 origin, Vector3 direction, Color4 colour, float seconds = 0f) =>
        Line(origin, origin + direction, colour, seconds);

    /// <summary>Draws a line with a head on the far end, so its direction is readable.</summary>
    /// <param name="from">The tail.</param>
    /// <param name="to">The tip.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     ⚠ <b>The head is capped in absolute size as well as scaled.</b> A velocity vector a hundred
    ///     metres long with a proportional head is an arrow whose head is twenty metres wide, which
    ///     fills the screen and hides the thing it is pointing at.
    /// </remarks>
    public void Arrow(Vector3 from, Vector3 to, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        Line(from, to, colour, seconds);

        var shaft = to - from;
        var length = shaft.Length();

        if (length <= MathUtil.ZeroTolerance) {
            return;
        }

        var forward = shaft / length;
        Basis(forward, out var right, out var up);

        var back = MathF.Min(length * ArrowHeadFraction, ArrowHeadLimit);
        var spread = back * 0.5f;
        var neck = to - (forward * back);

        Line(to, neck + (right * spread), colour, seconds);
        Line(to, neck - (right * spread), colour, seconds);
        Line(to, neck + (up * spread), colour, seconds);
        Line(to, neck - (up * spread), colour, seconds);
    }

    /// <summary>Draws a small three-armed cross, for marking a point.</summary>
    /// <param name="position">Where it is.</param>
    /// <param name="size">How far each arm reaches.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Cross(Vector3 position, float size, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        Line(position - (Vector3.UnitX * size), position + (Vector3.UnitX * size), colour, seconds);
        Line(position - (Vector3.UnitY * size), position + (Vector3.UnitY * size), colour, seconds);
        Line(position - (Vector3.UnitZ * size), position + (Vector3.UnitZ * size), colour, seconds);
    }

    /// <summary>Draws an axis-aligned box as its twelve edges.</summary>
    /// <param name="box">The box.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Box(BoundingBox box, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        var min = box.Minimum;
        var max = box.Maximum;

        Span<Vector3> corners = [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)
        ];

        Edges(corners, colour, seconds);
    }

    /// <summary>Draws a box that has been moved and turned — an oriented bounding box.</summary>
    /// <param name="box">The box, in the transform's own space.</param>
    /// <param name="transform">Where that space is.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     The corners are transformed, not the extents: a rotated box's world-space extents are a
    ///     larger axis-aligned box, which is a different and usually misleading picture.
    /// </remarks>
    public void Box(BoundingBox box, in Matrix4x4 transform, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        var min = box.Minimum;
        var max = box.Maximum;

        Span<Vector3> corners = [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)
        ];

        for (var index = 0; index < corners.Length; index++) {
            corners[index] = Matrix4x4.TransformPosition(corners[index], transform);
        }

        Edges(corners, colour, seconds);
    }

    /// <summary>Draws a sphere as three great circles.</summary>
    /// <param name="sphere">The sphere.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Sphere(BoundingSphere sphere, Color4 colour, float seconds = 0f) {
        if (!Enabled || sphere.IsEmpty) {
            return;
        }

        Ring(sphere.Center, sphere.Radius, Vector3.UnitX, Vector3.UnitY, colour, seconds, RingSegments);
        Ring(sphere.Center, sphere.Radius, Vector3.UnitY, Vector3.UnitZ, colour, seconds, RingSegments);
        Ring(sphere.Center, sphere.Radius, Vector3.UnitZ, Vector3.UnitX, colour, seconds, RingSegments);
    }

    /// <summary>Draws a circle in the plane a normal defines.</summary>
    /// <param name="centre">Where its middle is.</param>
    /// <param name="normal">Which way the plane faces. Need not be unit length.</param>
    /// <param name="radius">How big it is.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Circle(Vector3 centre, Vector3 normal, float radius, Color4 colour, float seconds = 0f) {
        if (!Enabled || radius <= 0f) {
            return;
        }

        Basis(Vector3.Normalize(normal), out var right, out var up);
        Ring(centre, radius, right, up, colour, seconds, RingSegments);
    }

    /// <summary>Draws a capsule: a segment with a hemisphere on each end.</summary>
    /// <param name="from">The centre of one cap.</param>
    /// <param name="to">The centre of the other.</param>
    /// <param name="radius">How thick it is.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     The commonest character collider there is, and the shape that is hardest to read from a
    ///     bounding box — so it is drawn as what it is: two rings, four sides, and four arcs over each
    ///     cap so that the ends do not look flat.
    /// </remarks>
    public void Capsule(Vector3 from, Vector3 to, float radius, Color4 colour, float seconds = 0f) {
        if (!Enabled || radius <= 0f) {
            return;
        }

        var axis = to - from;
        var length = axis.Length();

        // A capsule whose caps have met is a sphere, and asking for a basis from a zero axis is how
        // a debug shape turns into a NaN that poisons the whole vertex buffer.
        if (length <= MathUtil.ZeroTolerance) {
            Sphere(new(from, radius), colour, seconds);
            return;
        }

        var forward = axis / length;
        Basis(forward, out var right, out var up);

        Ring(from, radius, right, up, colour, seconds, RingSegments);
        Ring(to, radius, right, up, colour, seconds, RingSegments);

        Line(from + (right * radius), to + (right * radius), colour, seconds);
        Line(from - (right * radius), to - (right * radius), colour, seconds);
        Line(from + (up * radius), to + (up * radius), colour, seconds);
        Line(from - (up * radius), to - (up * radius), colour, seconds);

        Arc(from, radius, right, -forward, colour, seconds);
        Arc(from, radius, up, -forward, colour, seconds);
        Arc(to, radius, right, forward, colour, seconds);
        Arc(to, radius, up, forward, colour, seconds);
    }

    /// <summary>Draws a cone as its base circle and four lines to the apex.</summary>
    /// <param name="apex">The point.</param>
    /// <param name="direction">From the apex to the middle of the base. Its length is the height.</param>
    /// <param name="radius">How wide the base is.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>A spotlight, a view cone, a sensor arc — all the same shape and all worth seeing.</remarks>
    public void Cone(Vector3 apex, Vector3 direction, float radius, Color4 colour, float seconds = 0f) {
        if (!Enabled || radius <= 0f) {
            return;
        }

        var height = direction.Length();

        if (height <= MathUtil.ZeroTolerance) {
            return;
        }

        var forward = direction / height;
        Basis(forward, out var right, out var up);

        var centre = apex + direction;
        Ring(centre, radius, right, up, colour, seconds, RingSegments);

        Line(apex, centre + (right * radius), colour, seconds);
        Line(apex, centre - (right * radius), colour, seconds);
        Line(apex, centre + (up * radius), colour, seconds);
        Line(apex, centre - (up * radius), colour, seconds);
    }

    /// <summary>Draws a view volume as its twelve edges.</summary>
    /// <param name="frustum">The volume.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     Takes the frustum rather than a matrix, so "what is this camera actually culling against"
    ///     and "what is this shadow cascade actually fitting" are the same call — and so that a caller
    ///     with only a view-projection can build one with <see cref="BoundingFrustum(in Matrix4x4)" />
    ///     and get the reverse-Z handling that type already gets right.
    /// </remarks>
    public void Frustum(in BoundingFrustum frustum, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[BoundingFrustum.CornerCount];
        frustum.GetCorners(corners);

        // GetCorners hands back the near face first, anticlockwise, then the far face in the same
        // order — which is the same layout Edges expects for a box's bottom and top rings.
        Edges(corners, colour, seconds);
    }

    /// <summary>Draws a transform's three axes, red for X, green for Y, blue for Z.</summary>
    /// <param name="transform">The local-to-world matrix.</param>
    /// <param name="length">How long each axis is drawn.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     The colours are the ones every tool uses, and a debug overlay that picked different ones
    ///     would be read wrong by everybody who has ever used another one.
    /// </remarks>
    public void Axes(Matrix4x4 transform, float length = 1f, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        var origin = transform.Translation;
        Line(origin, origin + (Matrix4x4.TransformDirection(Vector3.UnitX, transform) * length), Color4.Red, seconds);
        Line(origin, origin + (Matrix4x4.TransformDirection(Vector3.UnitY, transform) * length), Color4.Green, seconds);
        Line(origin, origin + (Matrix4x4.TransformDirection(Vector3.UnitZ, transform) * length), Color4.Blue, seconds);
    }

    /// <summary>Puts a label at a point in the world, facing whatever camera draws it.</summary>
    /// <param name="position">Where the left end of the first line sits, vertically centred.</param>
    /// <param name="text">What it says. <c>\n</c> starts a new line.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="size">The cap height, in world units.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Text(Vector3 position, string text, Color4 colour, float size = 0.25f, float seconds = 0f) {
        if (Enabled && !string.IsNullOrEmpty(text)) {
            texts.Add(new(position, text, colour, size, seconds));
        }
    }

    /// <summary>Draws a line in screen space, in pixels from the top-left.</summary>
    /// <param name="from">Where it starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void ScreenLine(Vector2 from, Vector2 to, Color4 colour, float seconds = 0f) {
        if (Enabled) {
            screenLines.Add(new(from, to, colour, seconds));
        }
    }

    /// <summary>Draws the outline of a screen-space rectangle.</summary>
    /// <param name="topLeft">Its corner.</param>
    /// <param name="size">How wide and tall it is.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void ScreenRect(Vector2 topLeft, Vector2 size, Color4 colour, float seconds = 0f) {
        if (!Enabled) {
            return;
        }

        var topRight = new Vector2(topLeft.X + size.X, topLeft.Y);
        var bottomRight = topLeft + size;
        var bottomLeft = new Vector2(topLeft.X, topLeft.Y + size.Y);

        ScreenLine(topLeft, topRight, colour, seconds);
        ScreenLine(topRight, bottomRight, colour, seconds);
        ScreenLine(bottomRight, bottomLeft, colour, seconds);
        ScreenLine(bottomLeft, topLeft, colour, seconds);
    }

    /// <summary>Fills a screen-space rectangle, as scanlines.</summary>
    /// <param name="topLeft">Its corner.</param>
    /// <param name="size">How wide and tall it is.</param>
    /// <param name="colour">What colour, alpha included.</param>
    /// <param name="spacing">How far apart the scanlines are, in pixels.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     ⚠ <b>A fill is horizontal lines, one per <paramref name="spacing" /> pixels, and that is a
    ///     deliberate trade rather than an oversight.</b> There is one debug pipeline and it draws
    ///     lines; a filled quad would mean a second pipeline, a second vertex format and a second
    ///     buffer, to put a dark panel behind some text. At the default spacing a 300 × 200 panel is
    ///     two hundred segments — well inside a budget measured in tens of thousands — and it is the
    ///     difference between a readable overlay and white strokes lost in a bright scene.
    /// </remarks>
    public void ScreenFill(Vector2 topLeft, Vector2 size, Color4 colour, float spacing = 1f, float seconds = 0f) {
        if (!Enabled || size.X <= 0f || size.Y <= 0f || spacing <= 0f) {
            return;
        }

        // Half a step in, so the first and last scanlines sit inside the rectangle rather than on its
        // edge — a fill whose top line straddles the outline draws a brighter border nobody asked for.
        for (var y = topLeft.Y + (spacing * 0.5f); y < topLeft.Y + size.Y; y += spacing) {
            ScreenLine(new(topLeft.X, y), new(topLeft.X + size.X, y), colour, seconds);
        }
    }

    /// <summary>Draws text in screen space, in pixels from the top-left.</summary>
    /// <param name="position">The top-left of the first glyph's cap box.</param>
    /// <param name="text">What it says. <c>\n</c> starts a new line.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="size">The cap height, in pixels.</param>
    /// <param name="seconds">How long it lasts.</param>
    /// <remarks>
    ///     Expanded into strokes here rather than kept as text, unlike <see cref="Text" />: screen
    ///     space needs no camera, so there is nothing a renderer would know that this does not.
    /// </remarks>
    public void ScreenText(
        Vector2 position,
        ReadOnlySpan<char> text,
        Color4 colour,
        float size = 12f,
        float seconds = 0f
    ) {
        if (!Enabled || text.IsEmpty) {
            return;
        }

        screenLines.EnsureCapacity(screenLines.Count + DebugFont.SegmentCount(text));

        var sink = new ScreenSink(this, colour, seconds);
        DebugFont.Emit(text, position, size, ref sink);
    }

    /// <summary>
    ///     Ages everything by a frame: one-frame geometry goes, timed geometry loses
    ///     <paramref name="seconds" />.
    /// </summary>
    /// <param name="seconds">How much time passed.</param>
    /// <remarks>
    ///     <para>
    ///         Called after a renderer has drained, not before, or a line asked for during a frame
    ///         would never be seen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="DebugDrawSystem" /> is only that call for a host that records its
    ///         frame inside the loop, and the shipped one does not.</b> This used to say the
    ///         <c>PostRender</c> phase was "after the drain" — it is after the drain only if the
    ///         drain is a system. <c>VixenApplication</c> runs <c>EngineLoop.Frame</c> to completion,
    ///         <em>every</em> phase including <c>PostRender</c>, and records the GPU frame afterwards
    ///         through <c>AppGraphics.Begin</c>; a <see cref="DebugDrawSystem" /> in that loop deletes
    ///         every one-frame primitive between the overlay that drew it and the renderer that would
    ///         have. The symptom is the whole feature drawing nothing with every count reading
    ///         correct. Such a host calls this itself, once, after its frame is recorded.
    ///     </para>
    /// </remarks>
    public void Advance(float seconds) {
        Age(lines, seconds);
        Age(screenLines, seconds);
        Age(texts, seconds);
    }

    /// <summary>Throws everything away.</summary>
    public void Clear() {
        lines.Clear();
        screenLines.Clear();
        texts.Clear();
    }

    /// <summary>The twelve edges of an eight-corner box, given as two four-corner rings.</summary>
    /// <param name="corners">The near or bottom ring first, then the matching far or top one.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    void Edges(ReadOnlySpan<Vector3> corners, Color4 colour, float seconds) {
        for (var index = 0; index < 4; index++) {
            var next = (index + 1) % 4;
            Line(corners[index], corners[next], colour, seconds);
            Line(corners[index + 4], corners[next + 4], colour, seconds);
            Line(corners[index], corners[index + 4], colour, seconds);
        }
    }

    void Ring(Vector3 centre, float radius, Vector3 right, Vector3 up, Color4 colour, float seconds, int segments) {
        var previous = centre + (right * radius);

        for (var step = 1; step <= segments; step++) {
            var angle = step / (float) segments * MathUtil.TwoPi;
            var next = centre + (right * (MathF.Cos(angle) * radius)) + (up * (MathF.Sin(angle) * radius));
            Line(previous, next, colour, seconds);
            previous = next;
        }
    }

    /// <summary>Half a ring, from <paramref name="right" /> round to <paramref name="up" /> and back.</summary>
    void Arc(Vector3 centre, float radius, Vector3 right, Vector3 up, Color4 colour, float seconds) {
        var half = RingSegments / 2;
        var previous = centre + (right * radius);

        for (var step = 1; step <= half; step++) {
            var angle = step / (float) half * MathF.PI;
            var next = centre + (right * (MathF.Cos(angle) * radius)) + (up * (MathF.Sin(angle) * radius));
            Line(previous, next, colour, seconds);
            previous = next;
        }
    }

    /// <summary>Two unit vectors perpendicular to a third and to each other.</summary>
    /// <param name="forward">The direction to build around. Must be unit length.</param>
    /// <param name="right">One perpendicular.</param>
    /// <param name="up">The other.</param>
    /// <remarks>
    ///     ⚠ <b>The seed axis is chosen from which component of <paramref name="forward" /> is
    ///     smallest.</b> Crossing with a fixed axis produces a zero-length vector whenever the
    ///     direction happens to be that axis — which for a capsule standing upright, the commonest
    ///     one there is, would be every frame.
    /// </remarks>
    static void Basis(Vector3 forward, out Vector3 right, out Vector3 up) {
        var absolute = Vector3.Abs(forward);

        var seed = absolute.X <= absolute.Y && absolute.X <= absolute.Z
            ? Vector3.UnitX
            : absolute.Y <= absolute.Z
                ? Vector3.UnitY
                : Vector3.UnitZ;

        right = Vector3.Normalize(Vector3.Cross(forward, seed));
        up = Vector3.Cross(forward, right);
    }

    /// <summary>Ages one list, removing what has run out.</summary>
    /// <remarks>
    ///     Swap-back, so ageing out a thousand entries is a thousand writes rather than a thousand
    ///     shifts of the tail. Order is not preserved and does not matter: nothing here is drawn on
    ///     top of anything else in a way an index decides.
    /// </remarks>
    static void Age<T>(List<T> entries, float seconds) where T : struct, IDebugTimed<T> {
        for (var index = 0; index < entries.Count; index++) {
            var entry = entries[index];
            var remaining = entry.Remaining - seconds;

            if (entry.Remaining <= 0f || remaining <= 0f) {
                entries[index] = entries[^1];
                entries.RemoveAt(entries.Count - 1);
                index--;
                continue;
            }

            entries[index] = entry.WithRemaining(remaining);
        }
    }

    /// <summary>Turns <see cref="DebugFont" />'s strokes into screen lines of one colour and lifetime.</summary>
    readonly struct ScreenSink(DebugDraw draw, Color4 colour, float seconds) : IDebugFontSink {
        /// <inheritdoc />
        public void Segment(Vector2 head, Vector2 tail) => draw.ScreenLine(head, tail, colour, seconds);
    }
}
