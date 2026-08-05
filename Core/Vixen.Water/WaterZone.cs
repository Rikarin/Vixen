// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>How many bits a channel of the info texture carries.</summary>
/// <remarks>
///     ⚠ <b>A half float over a large zone is a <em>quantised</em> surface, and the panel says so
///     rather than leaving somebody to discover a stepped horizon.</b> Sixteen bits give about three
///     decimal digits, so a surface height of 2000 m resolves to a metre — visible as terracing along
///     a shoreline, and invisible in a screenshot taken from the middle of a lake.
///     <see cref="WaterZone.HeightQuantum" /> is the number to look at, and it is in metres.
/// </remarks>
public enum WaterInfoPrecision {
    /// <summary>Sixteen bits a channel. Half the memory, and a quantised surface over a large zone.</summary>
    Half,

    /// <summary>Thirty-two. What a zone bigger than a few kilometres needs.</summary>
    Full
}

/// <summary>
///     A region's water: the window the field is rasterised over, and what that costs.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render).</b>
///         The zone is the render unit because transitions between bodies must be resolved
///         <em>once</em>, by rasterising them in priority order into one field, rather than being
///         blended per pixel by a material that has to know which two bodies it is between.
///     </para>
///     <para>
///         <b>Always a sliding window.</b> Unreal made local tessellation a mode in 5.3 because the
///         fixed-extent version does not survive an open world; there is no reason to build the
///         version that does not survive first.
///     </para>
///     <para>
///         <b>The resolution is derived and stated, not typed.</b> <see cref="MetresPerTexel" /> and
///         <see cref="Bytes" /> are what a panel shows, in the style of
///         [31](../../docs/plan/31-terrain-grass-and-trees.md)'s create dialog. A number an author
///         types into a resolution with no idea what it buys is how the reference gets configured
///         wrongly — and "four metres a texel" is a sentence where "512" is not.
///     </para>
/// </remarks>
[DataContract("WaterZone")]
public readonly record struct WaterZone {
    /// <summary>How wide and deep the window is, in metres.</summary>
    public float Extent { get; init; }

    /// <summary>How many texels along each axis.</summary>
    public int Resolution { get; init; }

    /// <summary>How many bits a channel carries.</summary>
    public WaterInfoPrecision Precision { get; init; }

    /// <summary>
    ///     How far the view may move before the window is re-rasterised, as a fraction of the extent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A threshold and not zero, because rasterising is not free and the window is much
    ///         larger than the view.</b> Re-rasterising every frame spends the whole field's cost on a
    ///         camera that has moved a centimetre; never re-rasterising leaves the far edge of the
    ///         window behind the view. An eighth of the extent is about a hundred frames of walking,
    ///         which is the right order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It has to be well under a half.</b> The window is centred on the view, so the view
    ///         is at worst this fraction from the centre — and at a half it is at the edge, looking out
    ///         over water the field has never been rasterised for.
    ///     </para>
    /// </remarks>
    public float ScrollThreshold { get; init; }

    /// <summary>
    ///     How many metres a texel of the <em>coarsest</em> thing that reads the field covers.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The snap is to this and not to the field's own texel</b> — [§ D3]'s warning, and the
    ///     reason it is a stated number rather than something derived. Snapping to the window's own
    ///     grid is not enough once a ripple simulation samples it at a different resolution: the two
    ///     grids beat against each other and produce a crawl along the shoreline that appears only
    ///     while the camera moves. Zero means "nothing coarser than me", which is true until a ripple
    ///     window exists.
    /// </remarks>
    public float CoarsestTexel { get; init; }

    /// <summary>A 512-metre window at two metres a texel, in full precision.</summary>
    /// <remarks>
    ///     <para>
    ///         Sized for a lake district rather than an ocean: two metres a texel puts the shoreline
    ///         decision at two metres, which is soft for a rocky shore and right for a beach. An ocean
    ///         raises the extent and accepts the coarser texel, which is what the panel's readout is
    ///         for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>257 and not 256, which is a "power of two <em>plus one</em>" for the terrain
    ///         quadtree's reason.</b> The samples include both edges, so 257 of them over 512 m are two
    ///         metres apart exactly — where 256 would be 2.0078, and a snap grid stated in whole metres
    ///         would then land the window on a fraction of a texel.
    ///     </para>
    /// </remarks>
    public static WaterZone Default =>
        new() {
            Extent = 512f,
            Resolution = 257,
            Precision = WaterInfoPrecision.Full,
            ScrollThreshold = 0.125f,
            CoarsestTexel = 0f
        };

    /// <summary>How many metres apart two neighbouring texels are.</summary>
    /// <remarks>
    ///     The number a panel shows instead of a resolution — see the type's remarks. ⚠ Divided by
    ///     <c>Resolution − 1</c> because the samples include both edges, which is
    ///     <see cref="WaterFieldDescription.MetresPerTexel" />'s convention and has to stay the same
    ///     one: the snap grid is stated in these units, and a snap to a grid that is not a whole number
    ///     of texels is the crawl the snap exists to prevent.
    /// </remarks>
    public float MetresPerTexel => Resolution > 1 ? Extent / (Resolution - 1) : 0f;

    /// <summary>How many bytes the info texture occupies.</summary>
    /// <remarks>Four channels — surface height, flow in two, and the ground beneath.</remarks>
    public long Bytes => (long)Resolution * Resolution * 4 * (Precision == WaterInfoPrecision.Half ? 2 : 4);

    /// <summary>
    ///     The smallest height difference the info texture can represent at this zone's scale, in
    ///     metres.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What the panel shows beside the half-precision switch, and the reason the switch
    ///         is not free.</b> A float's resolution is relative, so this is the quantum <em>at the
    ///         window's own extent</em> — the worst case a surface at the far corner of the zone
    ///         faces. At full precision it is fractions of a millimetre and nobody needs to think
    ///         about it; at half precision over a two-kilometre zone it is a metre, and the horizon
    ///         terraces.
    ///     </para>
    ///     <para>
    ///         Derived from the extent rather than from an authored height range, because the extent
    ///         is what the coordinates actually reach: a zone 2 km across has surfaces at 2 km from
    ///         its own origin whatever height they sit at.
    ///     </para>
    /// </remarks>
    public float HeightQuantum {
        get {
            var magnitude = MathF.Max(Extent, 1f);
            var mantissa = Precision == WaterInfoPrecision.Half ? 10 : 23;

            // The gap between adjacent floats at this magnitude: 2^(exponent − mantissa bits).
            return MathF.Pow(2f, MathF.Floor(MathF.Log2(magnitude)) - mantissa);
        }
    }

    /// <summary>How far the view may move before the window is re-rasterised, in metres.</summary>
    public float ScrollDistance => Extent * MathF.Max(ScrollThreshold, 0f);

    /// <summary>The shape the field is rasterised at, centred and snapped for a view.</summary>
    /// <param name="view">Where the view is, on the ground plane.</param>
    /// <returns>The window.</returns>
    public WaterFieldDescription WindowFor(Vector2 view) =>
        new WaterFieldDescription { Extent = Extent, Resolution = Resolution }.Snap(view, CoarsestTexel);

    /// <summary>Why this zone cannot be rasterised, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (Resolution < 2) {
            return $"A zone of {Resolution} texels has no interior to interpolate across.";
        }

        if (!(Extent > 0f)) {
            return "A zone of no extent covers nothing.";
        }

        if (ScrollThreshold < 0f || ScrollThreshold >= 0.5f) {
            return $"A scroll threshold of {ScrollThreshold} lets the view reach the window's own edge, "
                + "where it would be looking out over water the field has never been rasterised for. "
                + "It is a fraction of the extent and has to be well under a half.";
        }

        // ⚠ Zero is not "scroll eagerly", it is the whole field re-rasterised every frame the view
        // moves at all — the exact cost the threshold exists to amortise, and it shows up nowhere but
        // frame time. Refused rather than paid invisibly; a zeroed component takes the default at
        // WaterZoneComponent.Zone instead of reaching this.
        if (ScrollThreshold == 0f) {
            return "A scroll threshold of zero re-rasterises the whole field every frame the view "
                + $"moves — the cost the threshold exists to amortise. The default is {Default.ScrollThreshold}, "
                + "an eighth of the extent; a threshold that genuinely wants to be eager states a "
                + "small positive fraction.";
        }

        if (CoarsestTexel < 0f) {
            return $"A coarsest texel of {CoarsestTexel} m is not a size.";
        }

        // ⚠ The snap grid has to be a whole number of this field's own texels, and this is the check
        // that says so. A four-metre grid over a field whose texels are 1.992 m apart lands the window
        // on a fraction of a texel, the two grids beat against each other, and the shoreline crawls
        // while the camera moves — which is § D3's warning and is invisible in a screenshot.
        if (CoarsestTexel > 0f && MetresPerTexel > 0f) {
            var texels = CoarsestTexel / MetresPerTexel;

            if (MathF.Abs(texels - MathF.Round(texels)) > 1e-3f) {
                return $"A coarsest texel of {CoarsestTexel} m is {texels} of this zone's own "
                    + $"{MetresPerTexel} m texels. Snapping to a grid that is not a whole number of "
                    + "texels lands the window on a fraction of one, and the two grids then beat "
                    + "against each other as the view moves — which is a shoreline that crawls.";
            }
        }

        // ⚠ Not a refusal: a snap grid finer than the field's own is harmless, it just does nothing.
        // A grid that is *not a multiple* of the field's texel is the case that beats — but the field
        // is snapped to the coarse grid, so the fine one lands wherever it lands and the coarse one
        // is stable, which is the direction that matters.
        return null;
    }
}
