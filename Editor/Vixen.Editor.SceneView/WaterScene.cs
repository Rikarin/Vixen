// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Water;

namespace Vixen.Editor.SceneView;

/// <summary>What a viewport needs to turn a scene's water components into a surface it can draw.</summary>
/// <remarks>
///     <para>
///         <b><c>IVegetationScene</c>'s sibling, one subsystem over, and it exists for exactly the
///         reason that one does.</b> The water mode's draw gesture writes a real <c>.vxspline</c> and
///         creates a <c>WaterBodyComponent</c> naming it, and the viewport drew nothing at all — so
///         laying a lake was an act of faith. This is the seam: the module answers the three questions
///         a fold cannot answer for itself, and the presenter draws what it is handed.
///     </para>
///     <para>
///         ⚠ <b>Three questions and not a surface, because the fold is not duplicated.</b>
///         <c>WaterZoneSystem.Fold</c> is what turns a world's zones and bodies into fields, and the
///         editor runs that very object over the scene document's world rather than a second
///         implementation of it — [35 § D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s
///         rule applied to the authoring host. What the fold cannot supply is what a <em>name</em>
///         means, and that is the editor's project database speaking.
///     </para>
///     <para>
///         ⚠ <b>Kernel types only, on <c>IVegetationScene</c>'s terms.</b> This assembly references
///         <c>Vixen.Water</c> — arithmetic, no device — and never <c>Vixen.Rendering.Water</c>, whose
///         <c>IWaterSplineSource</c> and <c>IWaterWaveSource</c> these two methods are adapted onto by
///         the presenter's assembly. The split is the same one the terrain seam makes.
///     </para>
/// </remarks>
public interface IWaterScene {
    /// <summary>The curve a body names, or <see langword="null" /> if nothing can supply it.</summary>
    /// <param name="name">What the component named — a <c>.vxspline</c> beside the scene, ordinarily.</param>
    /// <param name="placement">Where the entity carrying the body is.</param>
    /// <returns>The curve in world space, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>Null is a body that draws nothing and is not an error</b> — the fold counts it into
    ///     <c>UnresolvedBodies</c>, which is a number an author can be shown rather than a frame that
    ///     failed.
    /// </remarks>
    Spline? SplineFor(string name, in Matrix4x4 placement);

    /// <summary>The sea state a zone names, or <see langword="null" /> if nothing can supply it.</summary>
    /// <param name="name">What the component's <c>waveAsset</c> named.</param>
    /// <returns>The spectrum, or null to fall back to the zone's inline one.</returns>
    WaterWaveSpectrum? SpectrumFor(string name);

    /// <summary>How high the ground is under a place, in world units.</summary>
    /// <param name="ground">Where, on the ground plane — world X and Z.</param>
    /// <returns>The height.</returns>
    /// <remarks>
    ///     ⚠ <b>A flat plane at zero is right for an ocean and visibly wrong for a lake in a
    ///     valley</b>, which is why this is asked rather than assumed: the shoreline the field
    ///     rasterises is where the surface meets <em>this</em> answer.
    /// </remarks>
    float GroundAt(Vector2 ground);
}
