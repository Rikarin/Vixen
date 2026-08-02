// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>
///     One drag, turned into evenly spaced stamps.
/// </summary>
/// <remarks>
///     <para>
///         <b>A stroke is not the pointer samples.</b> A pointer arrives at whatever rate the frame
///         does, so stamping one per event makes a brush that is dense when the frame rate is high
///         and sparse when the artist moves quickly — the same drag producing a different result on a
///         different machine. Spacing the stamps by distance instead makes a stroke a property of the
///         path, and the leftover distance carried between segments is what keeps the spacing even
///         <em>across</em> pointer events rather than only within one.
///     </para>
///     <para>
///         <b>It is a struct that accumulates, not a function over a polyline.</b> A drag is fed to it
///         one segment at a time as the pointer moves, and it yields the stamps that segment earned.
///         The alternative — collecting the path and stamping at the end — would mean the viewport
///         showed nothing until the artist let go.
///     </para>
///     <para>
///         ⚠ <b>Deterministic, including the random rotation.</b> The angle for
///         <see cref="BrushRotation.Random" /> comes from a hash of the stamp's index and the
///         stroke's seed, not from a shared generator — so replaying a recorded stroke, or undoing
///         and redoing one, produces the same stamps. A stroke whose randomness came from
///         <c>Random.Shared</c> would be a stroke that could not be replayed, which is the same
///         property [docs/plan/31 § D8] needs of the scatter.
///     </para>
/// </remarks>
public sealed class BrushStroke {
    readonly TerrainBrush brush;
    readonly uint seed;
    Vector2 previous;
    float carried;
    bool started;

    /// <summary>Begins a stroke.</summary>
    /// <param name="brush">The brush, whose radius and spacing decide the stamp distance.</param>
    /// <param name="seed">
    ///     What the random rotations derive from. Two strokes with the same seed and the same path
    ///     produce the same stamps.
    /// </param>
    public BrushStroke(TerrainBrush brush, uint seed = 0x9E3779B9u) {
        this.brush = brush;
        this.seed = seed;
    }

    /// <summary>How many stamps the stroke has produced.</summary>
    public int StampCount { get; private set; }

    /// <summary>Everything the stroke has touched so far.</summary>
    public BrushFootprint Footprint { get; private set; }

    /// <summary>Whether anything has been stamped yet.</summary>
    public bool IsEmpty => StampCount == 0;

    /// <summary>Moves the pointer to a place, appending whatever stamps that earned.</summary>
    /// <param name="position">Where the pointer is now, in world XZ.</param>
    /// <param name="stamps">Where to append them.</param>
    /// <remarks>
    ///     The first call always stamps, because an artist who clicks without dragging expects one
    ///     stamp rather than none — which is what makes this the Single tool as well as the Paint one.
    /// </remarks>
    public void MoveTo(Vector2 position, ICollection<BrushStamp> stamps) {
        ArgumentNullException.ThrowIfNull(stamps);

        if (!started) {
            started = true;
            previous = position;
            Append(position, 0f, stamps);
            return;
        }

        var delta = position - previous;
        var length = delta.Length();

        if (!(length > 0f)) {
            return;
        }

        var direction = delta / length;
        var step = brush.StampDistance;
        var travelled = -carried;

        // Walk from where the last stamp's leftover left off, so spacing is even across the join
        // between two pointer events as well as within one.
        while (travelled + step <= length) {
            travelled += step;
            Append(previous + (direction * travelled), Angle(direction), stamps);
        }

        carried = length - travelled;
        previous = position;
    }

    void Append(Vector2 centre, float alongStroke, ICollection<BrushStamp> stamps) {
        var rotation = brush.Rotation switch {
            BrushRotation.Fixed => brush.Angle,
            BrushRotation.AlongStroke => alongStroke,
            BrushRotation.Random => RandomAngle(StampCount),
            _ => brush.Angle
        };

        var stamp = new BrushStamp(centre, rotation);
        var footprint = brush.FootprintOf(stamp);

        Footprint = StampCount == 0 ? footprint : Footprint.Union(footprint);
        StampCount++;
        stamps.Add(stamp);
    }

    static float Angle(Vector2 direction) => MathF.Atan2(direction.Y, direction.X);

    /// <summary>An angle from the stroke's seed and a stamp's index.</summary>
    /// <remarks>
    ///     A hash rather than a sequence, so the angle of stamp N does not depend on how many stamps
    ///     came before it in this <em>run</em> — only on its index. That is what lets a redo produce
    ///     the same stroke after an undo that discarded the intermediate state.
    /// </remarks>
    float RandomAngle(int index) {
        var hash = seed ^ (uint)index;

        // A round of the integer finalizer from splitmix64, narrowed. Cheap, and its low bits vary,
        // which a plain multiply-shift's do not — and the low bits are what a 0…1 mapping reads.
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return (hash / (float)uint.MaxValue) * MathF.Tau;
    }
}
