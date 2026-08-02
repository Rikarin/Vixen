// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Ecs;

/// <summary>A lens and a sensor, which decide the field of view and the exposure together.</summary>
/// <remarks>
///     <para>
///         <b>One set of numbers, used more than once.</b> A focal length and a sensor size are a
///         field of view; an aperture, a shutter and an ISO are an exposure value; and the aperture is
///         in both lists. Unreal derives all of it from one component and Unity from a physical camera
///         mode, and the reason both do is that the alternative — a field-of-view slider beside an
///         exposure slider beside a depth-of-field radius — lets an author write a camera that does
///         not exist, and then wonder why the defocus does not match the brightness.
///     </para>
///     <para>
///         ⚠ <b>It does not replace <c>Camera</c>, it feeds it.</b> <c>Camera.FieldOfView</c> is still
///         what the projection is built from, because a great deal of code reads it and an
///         orthographic camera has one and no lens at all. What this does is <em>compute</em> that
///         angle, so an entity carrying both has one answer rather than two.
///     </para>
///     <para>
///         ⚠ <b>A zeroed component is not a camera.</b> A sensor of zero width has an infinite field
///         of view and an aperture of zero has an exposure of minus infinity, so
///         <see cref="Default" /> is what a caller should start from — the same rule
///         <c>Camera.Perspective</c> and <c>ControlRotation.Default</c> already follow.
///         <c>CameraExtractionSystem</c> ignores a component whose sensor or focal length is zero for
///         that reason, rather than producing a frame nobody can explain.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PhysicalCamera {
    /// <summary>The sensor's width in millimetres. 36 is a full-frame stills camera.</summary>
    public float SensorWidth;

    /// <summary>The sensor's height in millimetres. 24 goes with a width of 36.</summary>
    public float SensorHeight;

    /// <summary>The lens's focal length in millimetres.</summary>
    public float FocalLength;

    /// <summary>The f-number. Smaller is a wider opening, more light and less depth of field.</summary>
    public float Aperture;

    /// <summary>How long the shutter is open, in seconds.</summary>
    public float ShutterTime;

    /// <summary>The ISO.</summary>
    public float Sensitivity;

    /// <summary>What the lens is focused on, in metres. Zero focuses at infinity.</summary>
    public float FocusDistance;

    /// <summary>How many diaphragm blades, which is the shape an out-of-focus highlight takes.</summary>
    /// <remarks>
    ///     Six or seven on most lenses, which is why bokeh is usually a hexagon rather than a disc. A
    ///     count below three means a circular opening.
    /// </remarks>
    public int BladeCount;

    /// <summary>A 35 mm lens on full frame, at f/2.8 and 1/60 at ISO 100.</summary>
    /// <remarks>
    ///     A property rather than <c>default</c>, for the reason the type's own remarks give: every
    ///     field here has a zero that is not a camera.
    /// </remarks>
    public static PhysicalCamera Default => new() {
        SensorWidth = 36f,
        SensorHeight = 24f,
        FocalLength = 35f,
        Aperture = 2.8f,
        ShutterTime = 1f / 60f,
        Sensitivity = 100f,
        FocusDistance = 0f,
        BladeCount = 6
    };

    /// <summary>Whether this describes a lens at all.</summary>
    /// <remarks>
    ///     What <c>CameraExtractionSystem</c> asks before reading any of it. A zeroed component is what
    ///     an entity gets by default and what a scene saved before this existed deserialises into, so
    ///     "is there a lens here" has to be answerable rather than assumed.
    /// </remarks>
    public readonly bool IsValid => SensorWidth > 0f && SensorHeight > 0f && FocalLength > 0f;

    /// <summary>The vertical field of view this lens and sensor give, in radians.</summary>
    /// <remarks>
    ///     Vertical because that is what <c>Camera.FieldOfView</c> means and what a projection matrix
    ///     takes. A lens quoted as "35 mm" is quoted against a sensor's <em>diagonal</em> or width by
    ///     convention, which is why the sensor's height is a separate field rather than derived from an
    ///     aspect ratio.
    /// </remarks>
    public readonly float VerticalFieldOfView =>
        IsValid ? 2f * MathF.Atan(SensorHeight / (2f * FocalLength)) : MathF.PI / 3f;

    /// <summary>The horizontal field of view, in radians.</summary>
    public readonly float HorizontalFieldOfView =>
        IsValid ? 2f * MathF.Atan(SensorWidth / (2f * FocalLength)) : MathF.PI / 3f;

    /// <summary>The aspect ratio the sensor has.</summary>
    public readonly float AspectRatio => SensorHeight > 0f ? SensorWidth / SensorHeight : 16f / 9f;

    /// <summary>The exposure value at ISO 100 this camera's settings produce.</summary>
    /// <remarks>
    ///     <c>EV = log2(N² / t · 100 / S)</c>, which is <see cref="Photometry.Ev100FromCamera" /> —
    ///     the same function a light meter implements, so f/16 at 1/125 and ISO 100 comes out at 15
    ///     and a photographer's intuition transfers.
    /// </remarks>
    public readonly float Ev100 =>
        Photometry.Ev100FromCamera(MathF.Max(Aperture, 0.01f), ShutterTime, MathF.Max(Sensitivity, 1f));

    /// <summary>How wide the circle of confusion is for a point at a given distance, in metres.</summary>
    /// <param name="distance">How far away the point is, in metres.</param>
    /// <returns>The diameter on the sensor, in metres.</returns>
    /// <remarks>
    ///     <para>
    ///         The thin-lens formula, which is what depth of field <em>is</em>:
    ///         <c>C = A · |S₂ − S₁| / S₂ · f / (S₁ − f)</c> for a focal length <c>f</c>, an aperture
    ///         diameter <c>A</c>, a focus distance <c>S₁</c> and a subject distance <c>S₂</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ It is on the <b>sensor</b>, in metres, so a blur radius in pixels is this divided by
    ///         the sensor's width and multiplied by the target's. Skipping that conversion gives a
    ///         defocus that changes when the window is resized.
    ///     </para>
    ///     <para>
    ///         Zero when focused at infinity is deliberate rather than a special case: with
    ///         <see cref="FocusDistance" /> at zero there is no focal plane to be in front of or
    ///         behind, so nothing is defocused and a frame that has not been told where to focus is
    ///         sharp rather than uniformly soft.
    ///     </para>
    /// </remarks>
    public readonly float CircleOfConfusion(float distance) {
        if (!IsValid || FocusDistance <= 0f || distance <= 0f) {
            return 0f;
        }

        // Millimetres to metres once, here, rather than in every caller.
        var focal = FocalLength * 0.001f;
        var diameter = focal / MathF.Max(Aperture, 0.01f);

        if (FocusDistance <= focal) {
            return 0f;
        }

        return diameter * (MathF.Abs(distance - FocusDistance) / MathF.Max(distance, 1e-4f))
            * (focal / (FocusDistance - focal));
    }
}
