// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     Where the frame is seen from, as one description rather than as several copies of it.
/// </summary>
/// <remarks>
///     <para>
///         A <see cref="RenderView" /> is deliberately not a camera — a frame has a dozen views and
///         one camera, and modelling a shadow cascade as something the game can see through would be
///         wrong. But the camera <em>is</em> one of those views, and more than one thing in a frame
///         needs to know how it was built: the view's own matrix, and the cascade fit that slices its
///         frustum.
///     </para>
///     <para>
///         <strong>Two consumers, one description.</strong> Before this, a host set the view's matrix
///         from its camera and separately set seven scalars on the shadow renderer, and nothing
///         checked that they agreed. A cascade fitted to a field of view the camera no longer had is
///         a shadow that covers the wrong part of the scene — visible only as shadows fading in at a
///         distance that does not match the setting, which is the kind of wrong nobody attributes
///         correctly.
///     </para>
///     <para>
///         Perspective only, because that is what a cascade fit assumes: the slices are cones about
///         the view axis, and an orthographic camera's would be boxes. A projection this cannot
///         describe sets <see cref="RenderView.ViewProjection" /> directly and fits its shadows some
///         other way.
///     </para>
/// </remarks>
/// <param name="Position">Where the camera is.</param>
/// <param name="Forward">Which way it looks. Normalised on use rather than on construction.</param>
/// <param name="Up">Which way is up, for the view basis.</param>
/// <param name="FieldOfView">The vertical field of view, in radians.</param>
/// <param name="AspectRatio">Width over height.</param>
/// <param name="NearPlane">The near plane's distance.</param>
/// <param name="FarPlane">The far plane's distance.</param>
public readonly record struct RenderCamera(
    Vector3 Position,
    Vector3 Forward,
    Vector3 Up,
    float FieldOfView,
    float AspectRatio,
    float NearPlane,
    float FarPlane
) {
    /// <summary>The camera component this view was described by, or a zeroed one for a view that is
    ///     not a scene camera.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Beside the field of view rather than instead of it.</b> Everything that consumes a
    ///         <see cref="RenderCamera" /> reads <see cref="FieldOfView" />, and a shadow cascade
    ///         constructs one from a light — so the angle stays in the positional list and this is the
    ///         extra a real camera carries. <c>Camera.HasLens</c> is what says whether it is there.
    ///     </para>
    ///     <para>
    ///         What reads it is everything the angle cannot answer: the exposure the frame is graded
    ///         at, the aperture the defocus is gathered with, and the shutter motion blur is smeared
    ///         over. All three come off one component, which is the point of it being one.
    ///     </para>
    /// </remarks>
    public Engine.Cameras.Camera Lens { get; init; }

    /// <summary>Looking down −Z from the origin, which is the engine's forward.</summary>
    public static RenderCamera Default => new(
        Vector3.Zero,
        new(0f, 0f, -1f),
        new(0f, 1f, 0f),
        MathF.PI / 3f,
        16f / 9f,
        0.1f,
        1000f
    );

    /// <summary>The view matrix this camera describes.</summary>
    public Matrix4x4 View => Matrix4x4.LookAt(Position, Position + Vector3.Normalize(Forward), Up);

    /// <summary>
    ///     The sub-pixel offset this frame's projection carries, in normalised device coordinates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Temporal antialiasing's half of the camera.</b> The resolve averages samples taken
    ///         at different points inside the pixel, and this is where the taking happens —
    ///         <c>TemporalAntialiasingRenderer.Jitter</c> is the sequence, <c>CameraExtractionSystem</c>
    ///         turns it into these units, and <see cref="Engine.Cameras.CameraMath.Jittered" /> is the
    ///         one place it is applied to a matrix.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than only in <see cref="RenderView.ViewProjection" />, because those
    ///         two are built from different things and have to agree.</b> The view's matrix comes from
    ///         the transform's inverse and this one from the field of view; the ambient-occlusion pass
    ///         inverts <em>this</em> to turn a depth texel into a view-space position, and the depth
    ///         texel it is inverting was rasterised through the other. An offset in one and not the
    ///         other is half a pixel of disagreement in every screen-space effect — the kind of wrong
    ///         that looks like a slightly noisier frame and is never attributed.
    ///     </para>
    ///     <para>
    ///         Zero for every view that is not being temporally resolved, which is a shadow cascade, a
    ///         probe face, the editor's viewport, and any frame with no TAA node in it.
    ///     </para>
    /// </remarks>
    public Vector2 Jitter { get; init; }

    /// <summary>The projection matrix this camera describes.</summary>
    /// <remarks>
    ///     Reversed depth is not applied here: it is a property of how the near and far planes are
    ///     handed to the projection, and <see cref="Matrix4x4.PerspectiveFieldOfView" /> is where the
    ///     engine's convention already lives. <see cref="Jitter" /> is, because a projection that did
    ///     not carry it would not be the projection the frame was drawn through.
    /// </remarks>
    public Matrix4x4 Projection =>
        Engine.Cameras.CameraMath.Jittered(
            Matrix4x4.PerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane),
            Jitter
        );

    /// <summary>What a shader and a frustum are both built from.</summary>
    public Matrix4x4 ViewProjection => View * Projection;
}
