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
    /// <summary>The view's name, for logging and profiling.</summary>
    public string Name { get; } = name;

    /// <summary>The stages this view collects work from.</summary>
    public RenderStageMask Stages { get; set; } = RenderStageMask.None;

    /// <summary>Where the view is, which is what depth sorting measures from.</summary>
    public Vector3 Position { get; set; }

    /// <summary>What the view can see.</summary>
    public BoundingFrustum Frustum { get; set; }

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
