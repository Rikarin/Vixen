// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     What a scene says about a region's water: how big the window is, and at what rate.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)
///         and § "What the scene sees".</b> One zone per region, a plain entity in the hierarchy with
///         a transform, duplicated and prefabbed like anything else.
///     </para>
///     <para>
///         ⚠ <b>A zone must exist or nothing renders</b>, which is Unreal's rule and is worth keeping
///         for the same reason: the field is the interchange every consumer reads, and a body with no
///         zone is a body nothing has rasterised. What is <em>not</em> kept is discovering that from a
///         blank frame — <see cref="WaterZoneSystem.ZonelessBodies" /> is a number an author can look at.
///     </para>
///     <para>
///         <b>The window is centred on the view rather than on this entity, and so is what the zone
///         claims.</b> Doc 35 makes the window a sliding one for exactly the reason Unreal added
///         local tessellation in 5.3 — a fixed-extent version does not survive an open world — and a
///         claim staked to the entity's own position would be that fixed-extent version by another
///         name: one zone would cover 512 metres of world forever, however far the camera walked. So
///         a body is claimed the frame the sliding window reaches it, the entity's transform places
///         the zone in the hierarchy for prefabs and duplication, and neither the texels nor the
///         claim read it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct WaterZoneComponent {
    /// <summary>How wide and deep the window is, in metres.</summary>
    public float Extent;

    /// <summary>How many texels along each axis.</summary>
    /// <remarks>
    ///     ⚠ <b>A power of two <em>plus one</em>.</b> The samples include both edges, so 257 over 512 m
    ///     is two metres exactly where 256 would be 2.0078 — and a snap grid stated in whole metres
    ///     would then land the window on a fraction of a texel, which is a shoreline that crawls while
    ///     the camera moves. <see cref="WaterZone.Validate" /> refuses the combination.
    /// </remarks>
    public int Resolution;

    /// <summary>How many bits a channel of the info texture carries.</summary>
    /// <remarks>
    ///     ⚠ Half precision over a large zone is a <em>quantised</em> surface — see
    ///     <see cref="WaterZone.HeightQuantum" />, which is the number in metres and is what the panel
    ///     shows beside this switch.
    /// </remarks>
    public WaterInfoPrecision Precision;

    /// <summary>How far the view may move before the window is re-rasterised, as a fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero takes the default, an eighth.</b> A component can hold zero without anyone
    ///     typing it — the ECS stores components in chunks whose columns are zeroed memory, and a
    ///     scene that never states the field deserialises the same way — and a threshold of zero is
    ///     the whole field re-rasterised every frame the view moves, visible nowhere but frame time.
    ///     <see cref="Zone" /> folds it; <see cref="WaterZone.Validate" /> refuses it.
    /// </remarks>
    public float ScrollThreshold;

    /// <summary>How many metres a texel of the coarsest thing reading the field covers.</summary>
    public float CoarsestTexel;

    /// <summary>The sea state every body in this zone is summed from.</summary>
    /// <remarks>
    ///     On the zone rather than per body, because a sea state is shared between every body in a
    ///     region and between levels — which is why <c>.vxwaves</c> is the one new asset kind
    ///     [§ D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline)
    ///     admits. Until that asset exists the spectrum is carried inline.
    /// </remarks>
    public WaterWaveSpectrum Waves;

    /// <summary>How the sea state falls off as the ground rises, in metres of depth.</summary>
    public float AttenuationDepth;

    /// <summary>A 512-metre window at two metres a texel, over an open sea.</summary>
    public static WaterZoneComponent Default =>
        new() {
            Extent = 512f,
            Resolution = 257,
            Precision = WaterInfoPrecision.Full,
            ScrollThreshold = 0.125f,
            CoarsestTexel = 0f,
            Waves = WaterWaveSpectrum.Default,
            AttenuationDepth = 2f
        };

    /// <summary>This component as the kernel's own description.</summary>
    /// <remarks>
    ///     The seam where an unset <see cref="ScrollThreshold" /> becomes the default rather than a
    ///     field re-rasterised every frame — see the field's own remarks. A negative threshold is an
    ///     authored error and is left for <see cref="WaterZone.Validate" /> to refuse.
    /// </remarks>
    public readonly WaterZone Zone =>
        new() {
            Extent = Extent,
            Resolution = Resolution,
            Precision = Precision,
            ScrollThreshold = ScrollThreshold == 0f ? WaterZone.Default.ScrollThreshold : ScrollThreshold,
            CoarsestTexel = CoarsestTexel
        };
}

/// <summary>
///     What a scene says about one body of water: a spline, a kind, and eleven numbers.
/// </summary>
/// <remarks>
///     <para>
///         <b>[§ D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline),
///         and there is deliberately no new asset kind.</b>
///         [31 § D2](../../docs/plan/31-terrain-grass-and-trees.md) earned a <c>.vxterrain</c> because
///         a heightfield is tens of megabytes of binary and merging it is not a thing. A water body is
///         a spline reference and eleven numbers; putting it in a sidecar asset would mean a lake that
///         cannot be moved without opening a second document, for no merge benefit at all. <b>The rule
///         the two cases share is <em>put it where the merge is</em></b> — and it produces opposite
///         answers, which is how you can tell it is a rule rather than a preference.
///     </para>
///     <para>
///         ⚠ <b>The per-control-point profile is not here.</b> Width, depth, velocity and audio
///         intensity vary along the curve, and a component is a fixed-size struct in a column — so the
///         profile travels with the spline, and what a component carries is the <em>defaults</em> a
///         body with no per-point profile uses. That is the same split
///         <c>TerrainSplineSettings</c> makes and for the same reason.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct WaterBodyComponent {
    /// <summary>What kind of water it is.</summary>
    public WaterBodyKind Kind;

    /// <summary>Which spline asset gives it its shape.</summary>
    /// <remarks>
    ///     <para>
    ///         A name rather than a handle, for the reason every other asset reference in a scene is
    ///         one: a handle names a slot in a world that issued it, and a scene file is read by a
    ///         world that has not run yet.
    ///     </para>
    ///     <para>
    ///         ⚠ A zeroed component holds <see langword="null" /> here — a chunk's column is zeroed
    ///         memory, not constructed values — and null is not a name a source can be asked for. The
    ///         fold counts it into <see cref="WaterZoneSystem.UnresolvedBodies" /> without reaching
    ///         the source, the same number a spline that has not loaded counts into.
    ///     </para>
    /// </remarks>
    public string Spline;

    /// <summary>Where a closed body's surface sits, in world units.</summary>
    /// <remarks>
    ///     Ignored by a river, whose surface follows its own curve — which is the whole difference
    ///     between a river and a bent lake.
    /// </remarks>
    public float SurfaceHeight;

    /// <summary>Which body wins where two overlap. Higher is on top.</summary>
    public int Priority;

    /// <summary>How wide the band is over which coverage falls to zero at the boundary, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Narrower than a few of the zone's texels and the field cannot resolve it</b>,
    ///         however smooth the arithmetic is — which is what the zone panel's metres-per-texel
    ///         readout is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero takes the default two metres</b>, on <see cref="WaterZoneComponent.ScrollThreshold" />'s terms:
    ///         zero is what a zeroed component holds, and a hard edge on water reads as a cut in the
    ///         terrain from a long way off — a bug an author never typed. A deliberate near-hard edge
    ///         — a canal wall — is a small stated value, a quarter of a metre.
    ///     </para>
    /// </remarks>
    public float ShoreFalloff;

    /// <summary>How far inside the boundary the bed reaches its full depth, in metres.</summary>
    public float BedRamp;

    /// <summary>How far the channel reaches either side of the curve, where the profile says nothing.</summary>
    public float HalfWidth;

    /// <summary>How far below the surface the bed sits, where the profile says nothing.</summary>
    public float Depth;

    /// <summary>How fast the water moves downstream, where the profile says nothing.</summary>
    /// <remarks>⚠ Only an open body flows. A lake with a velocity is not a thing water does.</remarks>
    public float Velocity;

    /// <summary>How loud it is, where the profile says nothing.</summary>
    /// <remarks>
    ///     The channel everybody forgets until the river is silent. It carries no rendering meaning at
    ///     all and exists so that a rapid is louder than the pool below it without an author placing
    ///     emitters by hand.
    /// </remarks>
    public float AudioIntensity;

    /// <summary>A lake: still, three metres deep, with a two-metre shore.</summary>
    public static WaterBodyComponent Default =>
        new() {
            Kind = WaterBodyKind.Lake,
            Spline = string.Empty,
            SurfaceHeight = 0f,
            Priority = 0,
            ShoreFalloff = 2f,
            BedRamp = 4f,
            HalfWidth = 0f,
            Depth = 3f,
            Velocity = 0f,
            AudioIntensity = 0.1f
        };

    /// <summary>The profile a body with no per-control-point one uses.</summary>
    public readonly WaterProfilePoint Profile =>
        new() {
            HalfWidth = HalfWidth,
            Depth = Depth,
            Velocity = Velocity,
            AudioIntensity = AudioIntensity
        };
}
