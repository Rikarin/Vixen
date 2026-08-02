// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     One thing being rendered from: a camera, a shadow cascade, a reflection probe face.
/// </summary>
/// <remarks>
///     <para>
///         A view is not a camera. A frame has one main camera and a dozen views — four shadow
///         cascades, six probe faces, a UI overlay — and they differ only in a frustum and a set of
///         stages. Making the shadow renderer build a "camera" would be modelling the shadow map as
///         a thing the game can see through, which it is not.
///     </para>
///     <para>
///         Which stages a view wants is a mask rather than a list for the reason
///         <see cref="RenderStageMask" /> gives: culling asks the question once per object per view,
///         and a shadow view wanting only <c>ShadowCaster</c> is what stops it walking the UI.
///     </para>
/// </remarks>
public sealed class RenderView(string name) {
    Matrix4x4 viewProjection = Matrix4x4.Identity;
    RenderCamera? camera;

    /// <summary>The view's name, for logging and profiling.</summary>
    public string Name { get; } = name;

    /// <summary>The stages this view collects work from.</summary>
    public RenderStageMask Stages { get; set; } = RenderStageMask.None;

    /// <summary>Where the view is, which is what depth sorting measures from.</summary>
    public Vector3 Position { get; set; }

    /// <summary>What the view can see.</summary>
    public BoundingFrustum Frustum { get; set; }

    /// <summary>
    ///     The matrix that takes a world position to this view's clip space.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What a shader needs and <see cref="Frustum" /> cannot give it: six planes are what the
    ///         volume <em>is</em>, not how to project into it, and the matrix they were derived from
    ///         was thrown away. A shadow caster could not be told which cascade it was being drawn for
    ///         until this existed.
    ///     </para>
    ///     <para>
    ///         <strong>Setting it re-derives the frustum.</strong> Two properties describing one
    ///         volume is a bug waiting to be written — a view culled against last frame's planes and
    ///         drawn with this frame's matrix produces geometry that pops in and out at the edges, and
    ///         nothing anywhere would say why. Setting <see cref="Frustum" /> on its own is still
    ///         allowed, for a caller that has planes and no matrix, and then this stays whatever it
    ///         was.
    ///     </para>
    /// </remarks>
    public Matrix4x4 ViewProjection {
        get => viewProjection;
        set {
            viewProjection = value;
            Frustum = new(value);
        }
    }

    /// <summary>
    ///     How this view's projection was built, for the things that need more than the matrix.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Null for every view that is not a camera — a shadow cascade, a probe face — and that is
    ///         the ordinary case. What needs it is the cascade fit, which slices a <em>cone</em> and
    ///         therefore has to know the field of view and the aspect a matrix alone does not give
    ///         back.
    ///     </para>
    ///     <para>
    ///         <strong>Setting it sets the position and the matrix</strong>, and therefore the frustum,
    ///         for the same reason setting the matrix derives the frustum: a camera and a view that
    ///         describe different volumes is a bug nothing reports, and here it would mean a cascade
    ///         fitted to a field of view the camera no longer has.
    ///     </para>
    /// </remarks>
    public RenderCamera? Camera {
        get => camera;
        set {
            camera = value;

            if (value is { } from) {
                Position = from.Position;
                ViewProjection = from.ViewProjection;
            }
        }
    }

    /// <summary>
    ///     Where this view was looking last frame, which is what a motion vector is measured against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Here rather than on the camera component, because the component does not keep
    ///         history.</b> Whatever moved the camera overwrote it, and by the time extraction runs
    ///         there is nothing left to compare against — so the last matrix has to be kept by
    ///         something that outlives a frame, and the view is the only thing in the frame that does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Advanced explicitly rather than in <see cref="ViewProjection" />'s setter.</b> That
    ///         setter runs whenever anything touches the matrix, which in a frame with a jittered
    ///         projection or a host correcting an aspect ratio is more than once — and a "previous"
    ///         that means "the value from earlier in this same frame" is a motion vector of nearly
    ///         zero for a camera that moved. <see cref="Advance" /> is called once, by whoever owns
    ///         the view's per-frame update.
    ///     </para>
    /// </remarks>
    public Matrix4x4 PreviousViewProjection { get; set; }

    /// <summary>Moves this frame's matrix into <see cref="PreviousViewProjection" />.</summary>
    /// <remarks>
    ///     Called once per frame, <em>before</em> the new matrix is written —
    ///     <c>CameraExtractionSystem</c> does it for the scene's camera, and a host steering a view by
    ///     hand does it itself. A view nobody advances reports a previous matrix of zero, which
    ///     <c>MotionVectors.rvn</c> reads as "no history" and answers with no motion rather than with
    ///     a smear across the whole screen.
    /// </remarks>
    public void Advance() => PreviousViewProjection = viewProjection;

    /// <summary>The view's index within the frame, assigned by <see cref="RenderSystem" />.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>
    ///     Objects beyond this distance are culled, whatever the frustum says. Zero disables it.
    /// </summary>
    /// <remarks>
    ///     Per view rather than global because that is the whole point: a shadow cascade's cutoff is
    ///     far shorter than the camera's, and a shadow-distance setting that had to match the draw
    ///     distance would either over-render shadows or clip geometry.
    /// </remarks>
    public float MaximumDistance { get; set; }

    /// <summary>
    ///     Multiply by <c>radius / distance</c> to get the fraction of the viewport's height an
    ///     object covers. Zero disables screen-size work — LOD selection — for this view.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One number rather than a field of view and a viewport, because that is all any
    ///         consumer wants: for a perspective view it is <c>1 / tan(fov / 2)</c>, and the whole
    ///         projection reduces to it. It is a fraction rather than pixels so that a LOD threshold
    ///         authored on one screen means the same thing on another.
    ///     </para>
    ///     <para>
    ///         Zero by default, which is the setting a shadow cascade and a probe face want: choosing
    ///         a different mesh for a shadow than for the object casting it makes the shadow stop
    ///         matching its caster, and nobody authoring LOD thresholds was thinking about the sun.
    ///     </para>
    /// </remarks>
    public float ScreenHeightScale { get; set; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
