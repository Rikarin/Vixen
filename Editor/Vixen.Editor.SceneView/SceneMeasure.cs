// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>A tape measure: click two points and read the distance, three and read the angle.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24 puts this in the group that "separates a toolset a professional will use from one
///         they will try".</b> Everything a level designer asserts is a measurement — the player fits
///         through the gap, the jump is makeable, the sightline reaches — and an editor with no way to
///         ask "how far is that" makes every one of them a guess or a temporary cube.
///     </para>
///     <para>
///         ⚠ <b>It snaps like everything else, and that is the whole of why it is worth having.</b> A
///         measurement between two points the pointer happened to land on is a number nobody can act
///         on; between two corners it is the width of the doorway. The points come in already snapped
///         — whoever is driving the gesture asks the same <see cref="SnapContext" /> a drag does — so
///         this holds points rather than resolving them.
///     </para>
///     <para>
///         ⚠ <b>Three points, and the third is not a longer tape.</b> Two points is a distance and
///         three is an angle at the middle one, which is the second question anybody asks of a corner.
///         A fourth starts a new measurement rather than extending the old one: a polyline's total
///         length is a different tool and nobody reaches for it between playtests.
///     </para>
/// </remarks>
public sealed class SceneMeasure {
    /// <summary>How many points one measurement holds.</summary>
    public const int Capacity = 3;

    readonly List<Vector3> points = [];

    /// <summary>The points taken so far, in the order they were taken.</summary>
    public IReadOnlyList<Vector3> Points => points;

    /// <summary>Whether the tool is taking points.</summary>
    public bool IsActive { get; set; }

    /// <summary>Whether there is anything to read.</summary>
    public bool HasMeasurement => points.Count >= 2;

    /// <summary>How long the tape is, in world units.</summary>
    /// <remarks>
    ///     The whole run rather than the last leg, because with three points what is being asked about
    ///     is the corner and both legs are part of it.
    /// </remarks>
    public float Distance {
        get {
            var total = 0f;

            for (var index = 1; index < points.Count; index++) {
                total += Vector3.Distance(points[index - 1], points[index]);
            }

            return total;
        }
    }

    /// <summary>The angle at the middle point, in degrees, or <see langword="null" /> with fewer than three.</summary>
    public float? Angle {
        get {
            if (points.Count < 3) {
                return null;
            }

            var first = points[0] - points[1];
            var second = points[2] - points[1];

            if (first.IsZero || second.IsZero) {
                return null;
            }

            var cosine = Vector3.Dot(Vector3.Normalize(first), Vector3.Normalize(second));

            return MathUtil.RadiansToDegrees(MathF.Acos(Math.Clamp(cosine, -1f, 1f)));
        }
    }

    /// <summary>Takes a point.</summary>
    /// <param name="point">Where, in world space and already snapped.</param>
    /// <remarks>
    ///     ⚠ <b>A fourth point starts again rather than being refused.</b> The gesture after reading a
    ///     measurement is measuring the next thing, and a tool that had to be cleared first is one
    ///     people clear by turning off and on again.
    /// </remarks>
    public void Add(Vector3 point) {
        if (points.Count >= Capacity) {
            points.Clear();
        }

        points.Add(point);
    }

    /// <summary>Throws the measurement away.</summary>
    public void Clear() => points.Clear();

    /// <summary>What a readout should say, or <see langword="null" /> when there is nothing to say.</summary>
    /// <remarks>
    ///     Metres to two places, which is a millimetre-and-a-bit — finer than anything a block-out is
    ///     built to and coarse enough that the number does not jitter while a point is being placed.
    /// </remarks>
    public string? Describe() {
        if (!HasMeasurement) {
            return null;
        }

        var text = Distance.ToString("0.00", CultureInfo.CurrentCulture) + " m";

        return Angle is { } angle
            ? text + "  " + angle.ToString("0.0", CultureInfo.CurrentCulture) + "°"
            : text;
    }
}
