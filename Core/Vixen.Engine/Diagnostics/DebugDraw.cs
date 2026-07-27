// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics;

/// <summary>One line to draw, in world space.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Colour">What colour it is.</param>
/// <param name="Remaining">Seconds left to live. Zero means "this frame only".</param>
public readonly record struct DebugLine(Vector3 From, Vector3 To, Color4 Colour, float Remaining);

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
///         <b>This is the accumulator, not the renderer.</b> Everything lands in one list of world
///         space lines, and a renderer drains <see cref="Lines" /> once a frame and turns it into
///         draw calls. There is no renderer yet — Phase 2 draws nothing — so what this closes is the
///         half that every subsystem needs to be able to *call*, and what it leaves is the half that
///         needs a pipeline. A subsystem written against this today needs no change when the drawing
///         arrives.
///     </para>
///     <para>
///         Every shape is lines, including the round ones. A debug sphere is three rings and reads
///         as a sphere; giving it a mesh would mean a second pipeline, a depth decision and a
///         lighting decision, for geometry whose whole job is to be unmistakably not part of the
///         scene.
///     </para>
/// </remarks>
public sealed class DebugDraw {
    const int RingSegments = 24;

    readonly List<DebugLine> lines = [];

    /// <summary>Whether anything is recorded at all. Off is free: every call returns immediately.</summary>
    /// <remarks>
    ///     A field to test rather than a compile-time flag, so a release build can turn it on for a
    ///     support case. The cost of that choice is one predictable branch per call.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>What a renderer drains.</summary>
    public ReadOnlySpan<DebugLine> Lines => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines);

    /// <summary>How many lines are queued.</summary>
    public int Count => lines.Count;

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

        for (var index = 0; index < 4; index++) {
            var next = (index + 1) % 4;
            Line(corners[index], corners[next], colour, seconds);
            Line(corners[index + 4], corners[next + 4], colour, seconds);
            Line(corners[index], corners[index + 4], colour, seconds);
        }
    }

    /// <summary>Draws a sphere as three great circles.</summary>
    /// <param name="sphere">The sphere.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="seconds">How long it lasts.</param>
    public void Sphere(BoundingSphere sphere, Color4 colour, float seconds = 0f) {
        if (!Enabled || sphere.IsEmpty) {
            return;
        }

        Ring(sphere.Center, sphere.Radius, Vector3.UnitX, Vector3.UnitY, colour, seconds);
        Ring(sphere.Center, sphere.Radius, Vector3.UnitY, Vector3.UnitZ, colour, seconds);
        Ring(sphere.Center, sphere.Radius, Vector3.UnitZ, Vector3.UnitX, colour, seconds);
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

    /// <summary>
    ///     Ages everything by a frame: one-frame lines go, timed lines lose <paramref name="seconds" />.
    /// </summary>
    /// <param name="seconds">How much time passed.</param>
    /// <remarks>
    ///     Called after a renderer has drained, not before, or a line asked for during a frame would
    ///     never be seen. <c>DebugDrawSystem</c> runs it in <c>PostRender</c> for that reason.
    /// </remarks>
    public void Advance(float seconds) {
        for (var index = 0; index < lines.Count; index++) {
            var line = lines[index];
            var remaining = line.Remaining - seconds;

            if (line.Remaining <= 0f || remaining <= 0f) {
                // Swap-back, so ageing out a thousand lines is a thousand writes rather than a
                // thousand shifts of the tail.
                lines[index] = lines[^1];
                lines.RemoveAt(lines.Count - 1);
                index--;
                continue;
            }

            lines[index] = line with { Remaining = remaining };
        }
    }

    /// <summary>Throws everything away.</summary>
    public void Clear() => lines.Clear();

    void Ring(Vector3 centre, float radius, Vector3 right, Vector3 up, Color4 colour, float seconds) {
        var previous = centre + (right * radius);

        for (var step = 1; step <= RingSegments; step++) {
            var angle = step / (float)RingSegments * MathUtil.TwoPi;
            var next = centre + (right * (MathF.Cos(angle) * radius)) + (up * (MathF.Sin(angle) * radius));
            Line(previous, next, colour, seconds);
            previous = next;
        }
    }
}
