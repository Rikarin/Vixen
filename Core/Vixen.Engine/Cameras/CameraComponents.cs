// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Engine.Cameras;

/// <summary>What an entity sees. Its position and orientation come from its transform.</summary>
/// <remarks>
///     <para>
///         A camera is a component and not an object, so an entity can be one, and so a scene can
///         have any number of them without the engine holding a list. Which one renders is the
///         renderer's decision, made from <see cref="Order" /> and the entity being enabled.
///     </para>
///     <para>
///         <b><c>[DataContract]</c>, because a level places its cameras</b> — that is what gives it a
///         name a <c>.vxscene</c> can write and a serializer a compiled one can be made of — and
///         <b><c>[Component]</c>, because the pair of them is what declares it to
///         <c>SceneComponentRegistry</c>.</b> A game's own components say the same two things the
///         same way and need nothing else.
///     </para>
///     <para>
///         <b>It is a physical camera, and there is no other kind.</b> A sensor and a focal length
///         are a field of view; an aperture, a shutter and an ISO are an exposure value; and the
///         aperture is in both lists, which is why they cannot be separate components. This used to
///         be two — a <c>Camera</c> holding an angle and a <c>PhysicalCamera</c> holding a lens — and
///         an entity carrying both had two answers to one question, with a rule buried in the
///         extraction system about which won. Now there is one set of numbers and everything is
///         derived from it: the projection, the exposure, and the defocus.
///     </para>
///     <para>
///         ⚠ <b><see cref="FieldOfView" /> is a view onto <see cref="FocalLength" />, not a field.</b>
///         Reading it computes the angle the lens and the sensor give; writing it solves back for the
///         focal length that produces that angle. So every line that ever said
///         <c>Camera.Perspective with { FieldOfView = x }</c> still means what it meant, and a scene
///         file stores the lens rather than the angle — which is the number that also decides the
///         depth of field.
///     </para>
///     <para>
///         ⚠ <b>A zeroed component is not a camera.</b> A sensor of zero width has an infinite field
///         of view, a far plane of zero has no depth range at all, and an aperture of zero has an
///         exposure of minus infinity — so <see cref="Perspective" /> is what a caller starts from,
///         the same rule <c>ControlRotation.Default</c> already follows. <see cref="HasLens" /> is
///         what asks, and <c>CameraExtractionSystem</c> is what asks it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct Camera : IDefaultComponent<Camera> {
    /// <summary>The lens's focal length in millimetres.</summary>
    /// <remarks>
    ///     The stored truth behind <see cref="FieldOfView" />, and the reason it is stored rather than
    ///     the angle: the same number decides how much of the scene is in frame <em>and</em> how
    ///     shallow the depth of field is, and a component holding the angle would have to derive a
    ///     focal length back out of it to answer the second — using a sensor size, which is exactly
    ///     what would then be free to disagree.
    /// </remarks>
    public float FocalLength;

    /// <summary>The sensor's width in millimetres. 36 is a full-frame stills camera.</summary>
    public float SensorWidth;

    /// <summary>The sensor's height in millimetres. 24 goes with a width of 36.</summary>
    public float SensorHeight;

    /// <summary>The f-number. Smaller is a wider opening, more light and less depth of field.</summary>
    public float Aperture;

    /// <summary>How long the shutter is open, in seconds.</summary>
    /// <remarks>
    ///     Read twice: once by <see cref="Ev100" />, and once by motion blur, where it is what decides
    ///     how far a moving object smears. A camera whose exposure and whose blur disagreed about the
    ///     shutter would be the same fault this type exists to remove.
    /// </remarks>
    public float ShutterTime;

    /// <summary>The ISO.</summary>
    public float Sensitivity;

    /// <summary>What the lens is focused on, in metres. Zero focuses at infinity.</summary>
    /// <remarks>
    ///     ⚠ Zero is what leaves a frame sharp, which is what a project that has not asked for depth
    ///     of field should get. See <see cref="CircleOfConfusion" />.
    /// </remarks>
    public float FocusDistance;

    /// <summary>How many diaphragm blades, which is the shape an out-of-focus highlight takes.</summary>
    /// <remarks>
    ///     Six or seven on most lenses, which is why bokeh is usually a hexagon rather than a disc. A
    ///     count below three means a circular opening.
    /// </remarks>
    public int BladeCount;

    /// <summary>Distance to the near plane.</summary>
    public float NearPlane;

    /// <summary>Distance to the far plane.</summary>
    public float FarPlane;

    /// <summary>Whether the projection is orthographic.</summary>
    /// <remarks>
    ///     ⚠ <b>An orthographic camera still carries a lens and ignores it.</b> A parallel projection
    ///     has no focal length — that is what makes it parallel — so <see cref="FieldOfView" /> means
    ///     nothing here and <see cref="OrthographicHeight" /> is what sizes the view. The exposure
    ///     numbers still apply, because a sensor is exposed the same way whatever the optics in front
    ///     of it do.
    /// </remarks>
    public bool Orthographic;

    /// <summary>The height the orthographic view covers, in world units.</summary>
    public float OrthographicHeight;

    /// <summary>Width over height. Zero means "ask the target", which the renderer fills in.</summary>
    /// <remarks>
    ///     Not the same thing as <see cref="SensorAspectRatio" />: this is the shape of the image
    ///     being rendered, and that is the shape of the piece of film. They agree on a camera nobody
    ///     has letterboxed.
    /// </remarks>
    public float AspectRatio;

    /// <summary>Which camera renders first. Lower is earlier.</summary>
    public int Order;

    /// <summary>
    ///     A sensible perspective camera: 60° vertical on full frame, from 0.1 to 1000, at f/2.8 and
    ///     1/60 at ISO 100.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A property rather than a <c>default</c>, because a zeroed <see cref="Camera" /> has a
    ///         zero focal length and a zero far plane, and every matrix built from it is degenerate.
    ///         See what the same mistake cost in <c>Vixen.Graphics</c>' pipeline defaults
    ///         ([14](../../../docs/plan/14-roadmap.md) § Phase 1).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The focal length is derived from the angle and not the other way round</b>, and
    ///         that is a decision about which convention the <em>default</em> follows. 60° vertical is
    ///         what a game camera is: Unity's <c>Camera</c> defaults to it and Unreal's 90° horizontal
    ///         is the same angle at 16:9. On a 24 mm sensor it is a 20.8 mm lens — an ultra-wide, and
    ///         nowhere near the 35 mm a photographer would call normal, because a game's field of view
    ///         is about twice a film camera's. Both engines dodge the clash by shipping two camera
    ///         types; this is one, so it has to pick, and it picks the default nobody has to change.
    ///     </para>
    ///     <para>
    ///         <see cref="WithLens(float)" /> is the other convention, for a project that would rather
    ///         start from a lens than from an angle.
    ///     </para>
    /// </remarks>
    public static Camera Perspective => new() {
        FocalLength = FocalLengthFor(MathUtil.DegreesToRadians(60f), 24f),
        SensorWidth = 36f,
        SensorHeight = 24f,
        Aperture = 2.8f,
        ShutterTime = 1f / 60f,
        Sensitivity = 100f,
        FocusDistance = 0f,
        BladeCount = 6,
        NearPlane = 0.1f,
        FarPlane = 1000f,
        Orthographic = false,
        OrthographicHeight = 10f,
        AspectRatio = 0f,
        Order = 0
    };

    /// <summary>A sensible orthographic camera covering ten world units of height.</summary>
    public static Camera Orthographic2D => Perspective with { Orthographic = true };

    /// <summary>What a freshly added camera holds: <see cref="Perspective" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Explicit, so this is not a third name for one of two cameras.</b> The type offers
    ///     <see cref="Perspective" /> and <see cref="Orthographic2D" /> and a reader picks between
    ///     them; a public <c>Default</c> beside those would read as a third kind. Perspective is what
    ///     an Add Component hands over because a zeroed camera has a zero far plane and every matrix
    ///     built from it is degenerate.
    /// </remarks>
    static Camera IDefaultComponent<Camera>.DefaultValue => Perspective;

    /// <summary>A camera named by its lens rather than by its angle, on a full-frame sensor.</summary>
    /// <param name="focalLength">The focal length, in millimetres.</param>
    /// <returns>Everything <see cref="Perspective" /> has, framed by that lens instead.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of what <see cref="Perspective" /> chose.</b> That default follows the
    ///         game convention — a wide angle, with the focal length falling out of it. This follows
    ///         the photographic one: a round number for the lens, with the angle falling out. Both are
    ///         standard, in different industries, and the point of naming both is that neither has to
    ///         be explained as an unexplained constant later.
    ///     </para>
    ///     <para>
    ///         On 36 × 24, which is what "full frame" means:
    ///     </para>
    ///     <list type="table">
    ///         <item><term>18 mm</term><description>67° vertical — wider than a game camera</description></item>
    ///         <item><term>24 mm</term><description>53° — a reportage wide</description></item>
    ///         <item><term>35 mm</term><description>37.8° — the classic documentary lens</description></item>
    ///         <item><term>50 mm</term><description>27° — the nifty fifty, roughly what an eye picks out</description></item>
    ///         <item><term>85 mm</term><description>16° — a portrait lens</description></item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>A longer lens is what shallow focus costs.</b> Depth of field falls with the square
    ///         of the focal length, so <see cref="Perspective" /> at f/2.8 is sharp from 2.5 m to 180 m
    ///         and has no usable defocus at all — which is correct for a 92° lens rather than a fault
    ///         in <c>!DepthOfField</c>. At 85 mm the same aperture holds about half a metre. That trade
    ///         — a narrower view for a shallower plane — is the one a cinematographer actually makes,
    ///         and it is only visible because the lens and the framing are one component.
    ///     </para>
    /// </remarks>
    public static Camera WithLens(float focalLength) => Perspective with { FocalLength = focalLength };

    /// <summary>The same, on a sensor that is not full frame.</summary>
    /// <param name="focalLength">The focal length, in millimetres.</param>
    /// <param name="sensorWidth">The sensor's width, in millimetres.</param>
    /// <param name="sensorHeight">Its height, in millimetres.</param>
    /// <returns>Everything <see cref="Perspective" /> has, framed by that lens on that sensor.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sensor is half of what a focal length means</b>, which is why a lens quoted
    ///         without one says nothing: 35 mm is a documentary wide on full frame, a normal lens on
    ///         Super 35, and a short telephoto on Micro Four Thirds. Anything reproducing a real
    ///         camera's framing has to give both.
    ///     </para>
    ///     <list type="table">
    ///         <item><term>36 × 24</term><description>full frame, and what <see cref="Perspective" /> uses</description></item>
    ///         <item><term>24.89 × 18.67</term><description>Super 35, which is what most film is shot on</description></item>
    ///         <item><term>23.76 × 13.365</term><description>Super 35 at 16:9, the digital cinema crop</description></item>
    ///         <item><term>17.3 × 13</term><description>Micro Four Thirds</description></item>
    ///     </list>
    /// </remarks>
    public static Camera WithLens(float focalLength, float sensorWidth, float sensorHeight) =>
        Perspective with { FocalLength = focalLength, SensorWidth = sensorWidth, SensorHeight = sensorHeight };

    /// <summary>Vertical field of view, in radians. Ignored when <see cref="Orthographic" />.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Computed from <see cref="FocalLength" /> and <see cref="SensorHeight" />, and
    ///         settable by solving back for the focal length.</b> Vertical because that is what a
    ///         projection matrix takes; a lens quoted as "35 mm" is quoted against the sensor's
    ///         diagonal or its width by convention, which is why the height is stored separately
    ///         rather than derived from an aspect ratio.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Setting this on a camera with no sensor gives it a full-frame one.</b> An angle
    ///         with no sensor behind it is not a lens — the focal length it implies is zero, and a
    ///         zero focal length reads back as the fallback angle rather than the one just written,
    ///         which is a value that silently refuses to be set. A default sensor is the smallest
    ///         thing that makes the write mean what it says.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not serialised</b> — <see cref="FocalLength" /> is. A scene file carrying both
    ///         would carry two answers to one question, which is the whole fault this type was merged
    ///         to remove.
    ///     </para>
    /// </remarks>
    [DataMemberIgnore]
    public float FieldOfView {
        readonly get => HasLens ? 2f * MathF.Atan(SensorHeight / (2f * FocalLength)) : MathUtil.Pi / 3f;
        set {
            if (SensorHeight <= 0f || SensorWidth <= 0f) {
                SensorWidth = 36f;
                SensorHeight = 24f;
            }

            FocalLength = FocalLengthFor(value, SensorHeight);
        }
    }

    /// <summary>Whether this describes a lens at all.</summary>
    /// <remarks>
    ///     What <c>CameraExtractionSystem</c> asks before rendering through it. A zeroed component is
    ///     what an entity gets by default and what a scene saved before this type existed
    ///     deserialises into, so "is there a camera here" has to be answerable rather than assumed.
    /// </remarks>
    public readonly bool HasLens => SensorWidth > 0f && SensorHeight > 0f && FocalLength > 0f;

    /// <summary>The horizontal field of view, in radians.</summary>
    public readonly float HorizontalFieldOfView =>
        HasLens ? 2f * MathF.Atan(SensorWidth / (2f * FocalLength)) : MathUtil.Pi / 3f;

    /// <summary>The aspect ratio the <em>sensor</em> has, which is not <see cref="AspectRatio" />.</summary>
    public readonly float SensorAspectRatio => SensorHeight > 0f ? SensorWidth / SensorHeight : 16f / 9f;

    /// <summary>The exposure value at ISO 100 this camera's settings produce.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>EV = log2(N² / t · 100 / S)</c> — the same function a light meter implements, so
    ///         f/16 at 1/125 and ISO 100 comes out at 15 and a photographer's intuition transfers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The formula is written here rather than called from
    ///         <c>Photometry.Ev100FromCamera</c>, and that is a layering fact rather than a
    ///         preference.</b> <c>Photometry</c> lives in <c>Vixen.Rendering</c>, which references
    ///         this assembly and cannot be referenced back. <c>PhotometryTests</c> holds the two
    ///         against each other so the duplicate cannot drift.
    ///     </para>
    /// </remarks>
    public readonly float Ev100 =>
        MathF.Log2(
            MathF.Max(Aperture, 0.01f) * MathF.Max(Aperture, 0.01f)
            / MathF.Max(ShutterTime, 1e-6f)
            * 100f
            / MathF.Max(Sensitivity, 1e-3f)
        );

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
    ///         <see cref="SensorWidth" /> and multiplied by the target's width. Skipping that
    ///         conversion gives a defocus that changes when the window is resized.
    ///     </para>
    ///     <para>
    ///         Zero when focused at infinity is deliberate rather than a special case: with
    ///         <see cref="FocusDistance" /> at zero there is no focal plane to be in front of or
    ///         behind, so nothing is defocused and a frame that has not been told where to focus is
    ///         sharp rather than uniformly soft.
    ///     </para>
    /// </remarks>
    public readonly float CircleOfConfusion(float distance) {
        if (!HasLens || FocusDistance <= 0f || distance <= 0f) {
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

    /// <summary>The focal length that frames a given vertical angle on a given sensor height.</summary>
    /// <param name="fieldOfView">The vertical angle, in radians.</param>
    /// <param name="sensorHeight">The sensor's height, in millimetres.</param>
    /// <returns>The focal length, in millimetres.</returns>
    /// <remarks>
    ///     The inverse of <see cref="FieldOfView" />'s getter, exposed because
    ///     <see cref="Perspective" /> needs it before there is an instance to set the property on —
    ///     and because a tool converting a designer's "make it 90°" into a lens should not have to
    ///     rediscover the half-angle.
    /// </remarks>
    public static float FocalLengthFor(float fieldOfView, float sensorHeight) =>
        sensorHeight / (2f * MathF.Tan(MathF.Max(fieldOfView, 1e-4f) * 0.5f));
}
