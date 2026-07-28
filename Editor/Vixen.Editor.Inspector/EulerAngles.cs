// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Inspector;

/// <summary>Rotations as the three numbers a person edits.</summary>
/// <remarks>
///     <para>
///         <b>Only the editor does this, and only at the edge.</b> Nothing in the engine stores Euler
///         angles — <c>Quaternion</c>'s own remarks say why — but an inspector has to show three
///         boxes, because "rotate this fifteen degrees about Y" is a thing people say and
///         <c>(0, 0.13, 0, 0.99)</c> is not.
///     </para>
///     <para>
///         <b>The order is <c>Quaternion.FromYawPitchRoll</c>'s</b>, which is yaw about Y, then pitch
///         about X, then roll about Z. It has to be: three different orders give three different
///         rotations from the same numbers, and an inspector that read them back in an order the
///         engine does not write them in would rotate an object every time it was redrawn.
///     </para>
///     <para>
///         ⚠ <b>Gimbal lock is resolved, not avoided.</b> At ninety degrees of pitch, yaw and roll
///         describe the same turn and only their sum is recoverable; the whole of it is put in yaw
///         and roll is reported as zero. That is a real loss of the numbers the user typed, and it is
///         why the <i>stored</i> value stays a quaternion: only the display round-trips imperfectly,
///         never the model.
///     </para>
/// </remarks>
public static class EulerAngles {
    /// <summary>The rotation described by three angles in degrees.</summary>
    /// <param name="degrees">Pitch about X, yaw about Y, roll about Z — in that component order.</param>
    /// <returns>The rotation.</returns>
    /// <remarks>
    ///     ⚠ <b>The vector is in XYZ component order and the rotations are applied Y, X, Z.</b> Those
    ///     are two different things and both are right: a person reading a row of three boxes expects
    ///     them labelled X, Y, Z, and the engine's composition order is what it is.
    /// </remarks>
    public static Quaternion ToRotation(Vector3 degrees) =>
        Quaternion.FromYawPitchRoll(
            MathUtil.DegreesToRadians(degrees.Y),
            MathUtil.DegreesToRadians(degrees.X),
            MathUtil.DegreesToRadians(degrees.Z)
        );

    /// <summary>The three angles, in degrees, that would rebuild a rotation.</summary>
    /// <param name="rotation">The rotation.</param>
    /// <returns>Pitch about X, yaw about Y, roll about Z — in that component order.</returns>
    public static Vector3 FromRotation(Quaternion rotation) {
        var unit = Quaternion.Normalize(rotation);

        // The rotation's matrix, read as the images of the basis vectors. Built from the library's
        // own Transform rather than written out as a formula, so this cannot disagree with how the
        // engine actually rotates things — which is the failure mode a hand-expanded matrix has.
        var x = Quaternion.Transform(Vector3.UnitX, unit);
        var y = Quaternion.Transform(Vector3.UnitY, unit);
        var z = Quaternion.Transform(Vector3.UnitZ, unit);

        var sinPitch = Math.Clamp(y.Z, -1f, 1f);
        var pitch = MathF.Asin(sinPitch);
        var cosPitch = MathF.Sqrt(MathF.Max(0f, 1f - (sinPitch * sinPitch)));

        float yaw;
        float roll;

        if (cosPitch > 1e-4f) {
            roll = MathF.Atan2(-y.X, y.Y);
            yaw = MathF.Atan2(-x.Z, z.Z);
        } else {
            // Locked: yaw and roll turn about the same axis and only their sum survives. All of it
            // goes to yaw, because yaw is the one an artist is nearly always reaching for.
            roll = 0f;
            yaw = sinPitch > 0f ? MathF.Atan2(x.Y, x.X) : -MathF.Atan2(x.Y, x.X);
        }

        return new(
            MathUtil.RadiansToDegrees(pitch),
            MathUtil.RadiansToDegrees(yaw),
            MathUtil.RadiansToDegrees(roll)
        );
    }
}
