// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Core.Mathematics;

/// <summary>
///     The authored form of a path: what a <c>.vxspline</c> holds and what an editor mutates.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T8], and [docs/plan/26]'s largest owed item.</b> One asset with two
///         consumers — a road that deforms a terrain and a camera dolly that follows a track — which
///         is why it is here rather than in either of them.
///     </para>
///     <para>
///         ⚠ <b>Two types, and the split is the point.</b> <see cref="Spline" /> is immutable and
///         precomputes an arc-length table; this is mutable and precomputes nothing. An editor moves a
///         control point on every frame of a drag, and rebuilding a length table sixty times a second
///         for a curve nobody is measuring is the cost that makes an editor feel heavy. Ask for
///         <see cref="Build" /> when the answer is needed.
///     </para>
///     <para>
///         ⚠ <b>An asset with one point is legal and is not a curve.</b> An author places the first
///         point of a road before they place the second, and an asset that refused to exist until it
///         had two would have to be built from a dialog rather than from the viewport.
///         <see cref="CanBuild" /> is the question a consumer asks.
///     </para>
/// </remarks>
[DataContract]
public sealed class SplineAsset {
    List<SplinePoint> points = [];

    /// <summary>An empty asset.</summary>
    public SplineAsset() { }

    /// <summary>An asset over control points.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="controlPoints">The points, in order.</param>
    /// <param name="closed">Whether the last point joins back to the first.</param>
    public SplineAsset(string name, ReadOnlySpan<SplinePoint> controlPoints, bool closed = false) {
        Name = name;
        IsClosed = closed;
        points.AddRange(controlPoints);
    }

    /// <summary>What it is called, which is what a scene names it by.</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether the last point joins back to the first.</summary>
    public bool IsClosed { get; set; }

    /// <summary>The control points, in order.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Settable, and it has to be, because this is the one member that makes the asset an
    ///         asset.</b> It was a getter-only <c>IReadOnlyList</c>, and both serialisers skip a member
    ///         they cannot write to — so a <c>.vxspline</c> round-tripped to a name, a closed flag and
    ///         <em>no curve</em>. Nothing caught it because everything downstream asks
    ///         <see cref="CanBuild" /> first and draws nothing when the answer is no: a road that never
    ///         appeared and a lake that never appeared, with no error anywhere.
    ///     </para>
    ///     <para>
    ///         Mutating the returned list is the same as mutating the asset, deliberately — the
    ///         invariants this type has are all checked in <see cref="Validate" /> rather than
    ///         maintained per operation, so there is nothing for an accessor to protect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Third in declaration order, and it must stay there.</b> The generated serializer
    ///         reads members by position under a count guard, so this appends and older data still
    ///         reads; moving it above <see cref="IsClosed" /> would make every asset written before it
    ///         read its closed flag as a list of points.
    ///     </para>
    /// </remarks>
    public List<SplinePoint> Points {
        get => points;
        set => points = value ?? [];
    }

    /// <summary>How many there are.</summary>
    public int Count => points.Count;

    /// <summary>Whether there are enough points to evaluate a curve.</summary>
    public bool CanBuild => points.Count >= 2;

    /// <summary>One control point.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>The point.</returns>
    public SplinePoint this[int index] => points[index];

    /// <summary>A path through positions, with Catmull-Rom tangents.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="positions">Where it goes.</param>
    /// <param name="closed">Whether it joins back to its start.</param>
    /// <returns>The asset.</returns>
    public static SplineAsset Through(string name, ReadOnlySpan<Vector3> positions, bool closed = false) =>
        new(name, Spline.SmoothTangents(positions, closed), closed);

    /// <summary>Replaces a control point.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="point">What it becomes.</param>
    public void Set(int index, SplinePoint point) => points[index] = point;

    /// <summary>Moves a control point, carrying its tangents with it.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="position">Where it goes.</param>
    /// <remarks>
    ///     The tangents are offsets, so they follow — which is what makes dragging a point through a
    ///     gizmo bend the curve rather than reshape it.
    /// </remarks>
    public void MoveTo(int index, Vector3 position) => points[index] = points[index] with { Position = position };

    /// <summary>Appends a control point at the end.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Its index.</returns>
    public int Add(SplinePoint point) {
        points.Add(point);

        return points.Count - 1;
    }

    /// <summary>Inserts a control point before an index.</summary>
    /// <param name="index">Where.</param>
    /// <param name="point">The point.</param>
    public void Insert(int index, SplinePoint point) => points.Insert(index, point);

    /// <summary>Removes a control point.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool RemoveAt(int index) {
        if (index < 0 || index >= points.Count) {
            return false;
        }

        points.RemoveAt(index);

        return true;
    }

    /// <summary>Empties it.</summary>
    public void Clear() => points.Clear();

    /// <summary>Sets a point's outgoing tangent, optionally mirroring the incoming one.</summary>
    /// <param name="index">Which point.</param>
    /// <param name="tangentOut">The outgoing tangent, as an offset from the point.</param>
    /// <param name="mirror">Whether the incoming tangent becomes its negation.</param>
    /// <remarks>
    ///     ⚠ <b>Mirroring is a choice per drag, not a property of the point.</b> A road is smooth
    ///     almost everywhere and has the occasional hard corner, and the corner is exactly
    ///     <c>TangentIn ≠ −TangentOut</c>. A handle that always mirrored could not express one; one
    ///     that never did would make every ordinary edit two drags.
    /// </remarks>
    public void SetTangentOut(int index, Vector3 tangentOut, bool mirror = true) =>
        points[index] = points[index] with {
            TangentOut = tangentOut,
            TangentIn = mirror ? -tangentOut : points[index].TangentIn
        };

    /// <summary>And the incoming one.</summary>
    /// <param name="index">Which point.</param>
    /// <param name="tangentIn">The incoming tangent, as an offset from the point.</param>
    /// <param name="mirror">Whether the outgoing tangent becomes its negation.</param>
    public void SetTangentIn(int index, Vector3 tangentIn, bool mirror = true) =>
        points[index] = points[index] with {
            TangentIn = tangentIn,
            TangentOut = mirror ? -tangentIn : points[index].TangentOut
        };

    /// <summary>Recomputes every tangent from the positions, making the path smooth throughout.</summary>
    /// <param name="tension">0 is Catmull-Rom; 1 makes every tangent zero, which is a polyline.</param>
    /// <remarks>
    ///     Destroys every corner an author made by hand, which is why it is an explicit operation
    ///     rather than something that happens when a point moves.
    /// </remarks>
    public void Smooth(float tension = 0f) {
        if (points.Count < 2) {
            return;
        }

        var positions = new Vector3[points.Count];

        for (var index = 0; index < points.Count; index++) {
            positions[index] = points[index].Position;
        }

        var smoothed = Spline.SmoothTangents(positions, IsClosed, tension);

        for (var index = 0; index < points.Count; index++) {
            points[index] = smoothed[index] with { Roll = points[index].Roll };
        }
    }

    /// <summary>Inserts a control point on the curve without changing its shape.</summary>
    /// <param name="parameter">Where, as <see cref="Spline.Evaluate" /> takes it.</param>
    /// <returns>The index of the new point, or −1 if there was no segment there.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The shape is preserved, and that is the whole reason this is not
    ///         <see cref="Insert" /> with an evaluated position.</b> Dropping a point onto the curve
    ///         and leaving the tangents alone moves the road, because the two halves are now
    ///         parameterised over half the span each — so the author's next act is to drag it back to
    ///         where it already was.
    ///     </para>
    ///     <para>
    ///         The segment is converted to its Bézier form, subdivided by de Casteljau, and converted
    ///         back. That changes three points: the segment's start keeps its position and gets a
    ///         shorter outgoing tangent, the new point gets both of its own, and the segment's end
    ///         gets a shorter incoming one.
    ///     </para>
    /// </remarks>
    public int InsertOn(float parameter) {
        if (!CanBuild) {
            return -1;
        }

        var segments = IsClosed ? points.Count : points.Count - 1;
        var clamped = Math.Clamp(parameter, 0f, segments);
        var segment = Math.Min((int)MathF.Floor(clamped), segments - 1);
        var t = clamped - segment;

        if (t <= 1e-4f || t >= 1f - 1e-4f) {
            // Landing on a control point would insert a duplicate at zero distance, which is a
            // degenerate segment the tangent falls back out of rather than an edit anybody wanted.
            return -1;
        }

        var startIndex = segment;
        var endIndex = IsClosed ? (segment + 1) % points.Count : segment + 1;

        var start = points[startIndex];
        var end = points[endIndex];

        // Hermite to Bézier. Evaluate's basis uses M0 = TangentOut and M1 = −TangentIn, and a cubic
        // Bézier's inner controls are one third of those along the segment.
        var b0 = start.Position;
        var b1 = start.Position + (start.TangentOut / 3f);
        var b2 = end.Position + (end.TangentIn / 3f);
        var b3 = end.Position;

        var a = Vector3.Lerp(b0, b1, t);
        var b = Vector3.Lerp(b1, b2, t);
        var c = Vector3.Lerp(b2, b3, t);
        var d = Vector3.Lerp(a, b, t);
        var e = Vector3.Lerp(b, c, t);
        var split = Vector3.Lerp(d, e, t);

        points[startIndex] = start with { TangentOut = (a - b0) * 3f };
        points[endIndex] = end with { TangentIn = (c - b3) * 3f };

        var inserted = new SplinePoint(
            split,
            (d - split) * 3f,
            (e - split) * 3f,
            float.Lerp(start.Roll, end.Roll, t)
        );

        var at = startIndex + 1;

        points.Insert(at, inserted);

        return at;
    }

    /// <summary>Cuts the path at a control point, keeping the head and returning the tail.</summary>
    /// <param name="index">Which point to cut at. It appears in both halves.</param>
    /// <param name="name">What the tail is called.</param>
    /// <returns>The tail, or <see langword="null" /> if the cut would leave a half with one point.</returns>
    /// <remarks>
    ///     ⚠ <b>The point appears in both halves, because a cut is not a deletion.</b> Splitting a
    ///     road at a junction and moving one half should leave the other half ending where it did;
    ///     giving the point to one side would shorten the other by a segment.
    ///     ⚠ <b>A closed path opens rather than splitting</b>, at the cut, because a ring cut once is
    ///     one path and not two.
    /// </remarks>
    public SplineAsset? Split(int index, string? name = null) {
        if (index <= 0 || index >= points.Count - 1) {
            if (!IsClosed) {
                return null;
            }
        }

        if (IsClosed) {
            // Re-root the ring at the cut and open it, repeating the cut point at the far end.
            var opened = new List<SplinePoint>(points.Count + 1);

            for (var step = 0; step <= points.Count; step++) {
                opened.Add(points[(index + step) % points.Count]);
            }

            points.Clear();
            points.AddRange(opened);
            IsClosed = false;

            return null;
        }

        var tail = new SplineAsset(name ?? $"{Name} (2)", System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)[index..]);

        points.RemoveRange(index + 1, points.Count - index - 1);

        return tail;
    }

    /// <summary>Appends another path onto the end of this one.</summary>
    /// <param name="other">The path to append. Not modified.</param>
    /// <param name="reversed">Whether to walk it backwards, for two paths that meet head to head.</param>
    /// <returns>Whether it joined.</returns>
    /// <remarks>
    ///     ⚠ <b>Coincident ends are merged into one point rather than left as two.</b> Two control
    ///     points at the same place make a segment of zero length, which has no tangent — so a road
    ///     joined without the merge has a frame that flips at the seam and a mesh placement that
    ///     stacks everything it puts there.
    ///     ⚠ <b>A closed path cannot be joined onto</b>: it has no end to append to.
    /// </remarks>
    public bool Join(SplineAsset other, bool reversed = false) {
        ArgumentNullException.ThrowIfNull(other);

        if (IsClosed || other.IsClosed || other.Count == 0) {
            return false;
        }

        var incoming = new List<SplinePoint>(other.points);

        if (reversed) {
            incoming.Reverse();

            for (var index = 0; index < incoming.Count; index++) {
                var point = incoming[index];

                // Walking a path backwards swaps which tangent is arrival and which is departure.
                incoming[index] = point with { TangentIn = point.TangentOut, TangentOut = point.TangentIn };
            }
        }

        if (points.Count > 0 && Vector3.DistanceSquared(points[^1].Position, incoming[0].Position) < 1e-6f) {
            points[^1] = points[^1] with { TangentOut = incoming[0].TangentOut };
            incoming.RemoveAt(0);
        }

        points.AddRange(incoming);

        return true;
    }

    /// <summary>The curve this asset describes.</summary>
    /// <returns>The spline.</returns>
    /// <exception cref="InvalidOperationException">There are fewer than two points.</exception>
    /// <remarks>
    ///     Builds an arc-length table, so a caller holding one across frames should rebuild it when
    ///     the asset changes rather than calling this per sample.
    /// </remarks>
    public Spline Build() {
        if (!CanBuild) {
            throw new InvalidOperationException(
                $"'{Name}' has {points.Count} control point(s) and a curve needs two. Ask CanBuild "
                + "first — an asset with one point is a road somebody has started drawing."
            );
        }

        return new(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), IsClosed);
    }

    /// <summary>An independent copy.</summary>
    /// <returns>The copy.</returns>
    public SplineAsset Clone() =>
        new(Name, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), IsClosed);

    /// <summary>Why this asset cannot be used, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Name)) {
            return "A spline needs a name; it is what a scene refers to it by.";
        }

        if (!CanBuild) {
            return $"'{Name}' has {points.Count} control point(s); a curve needs two.";
        }

        if (IsClosed && points.Count < 3) {
            return $"'{Name}' is closed with {points.Count} points, which is a curve doubled back on "
                + "itself rather than a loop.";
        }

        for (var index = 1; index < points.Count; index++) {
            if (Vector3.DistanceSquared(points[index].Position, points[index - 1].Position) < 1e-8f) {
                return $"'{Name}' has two control points at the same place ({index - 1} and {index}), "
                    + "which makes a segment with no length and therefore no direction.";
            }
        }

        return null;
    }
}

/// <summary>Where a consumer looks up a spline by name.</summary>
/// <remarks>
///     ⚠ <b>An interface, because a name is not a handle.</b> A scene names a spline and is read by a
///     world that has not run yet — the reason <c>TerrainComponent</c> and <c>FoliageType</c> name
///     their assets rather than holding handles. A consumer that resolved names itself would need an
///     asset database in a class whose job is placing a camera.
/// </remarks>
public interface ISplineSource {
    /// <summary>The curve a name refers to.</summary>
    /// <param name="name">What the scene called it.</param>
    /// <param name="spline">The curve.</param>
    /// <returns>Whether there is one.</returns>
    bool TryGet(string name, out Spline? spline);
}
