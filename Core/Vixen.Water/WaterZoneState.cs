// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>Why a zone re-rasterised its field.</summary>
/// <remarks>
///     The diagnostic that answers "why is this costing so much" and "why is the shoreline stale" from
///     the same reading. A zone re-rasterising every frame is one whose threshold is too small or whose
///     bodies are being marked dirty by something that did not change them.
/// </remarks>
public enum WaterZoneUpdate {
    /// <summary>Nothing did. The field is the one from before.</summary>
    None,

    /// <summary>There was no field yet.</summary>
    First,

    /// <summary>The view moved past the scroll threshold.</summary>
    Scrolled,

    /// <summary>A body, the ground or the zone's own shape changed.</summary>
    Changed
}

/// <summary>
///     A zone's field, the window it covers, and the decision about when to rasterise it again.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)'s
///         sliding window, as a thing with a lifetime.</b> <see cref="WaterZone" /> is what an author
///         states and <see cref="WaterField" /> is the arithmetic; this is what holds one, moves it,
///         and says when it was last right.
///     </para>
///     <para>
///         ⚠ <b>The field is rasterised by the same code the surface query reads, and that is
///         [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)
///         rather than a shortcut.</b> § D3 describes the info texture as a top-down <em>render</em>,
///         with every body drawing into it through a material. That is the faster way to fill it and
///         it is a second rasterisation of the same bodies — one on the device for the picture, one on
///         the host for the boat — and nothing would hold the two together. Filling it from
///         <see cref="WaterField.Rasterize" /> and uploading the result means the shoreline the
///         material shades and the depth the buoyancy solver reads are the same numbers by
///         construction.
///     </para>
///     <para>
///         <b>Which is affordable because of when it happens.</b> The window is re-rasterised only
///         when the view has scrolled past <see cref="WaterZone.ScrollThreshold" /> or something has
///         changed — a hundred frames apart at a walk — so the cost is amortised over the frames
///         between, and the per-frame cost is an upload of a texture that did not change, which is
///         nothing. A device render would be the optimisation to reach for if that stops being true,
///         and it would need a seam test of its own before it could be trusted.
///     </para>
///     <para>
///         <b>Allocation-free after the first frame.</b> The field's arrays are kept and the window is
///         moved through them; a zone that reallocated on every scroll would allocate a megabyte every
///         hundred frames, forever.
///     </para>
/// </remarks>
public sealed class WaterZoneState {
    readonly List<WaterBody> bodies = [];

    WaterField? rasterised;
    Vector2 centre;
    bool dirty = true;

    /// <summary>Creates a zone's state.</summary>
    /// <param name="zone">What the author stated.</param>
    /// <exception cref="ArgumentException">The zone is not one that can be rasterised.</exception>
    public WaterZoneState(in WaterZone zone) {
        if (zone.Validate() is { } why) {
            throw new ArgumentException(why, nameof(zone));
        }

        Zone = zone;
    }

    /// <summary>What the author stated.</summary>
    public WaterZone Zone { get; private set; }

    /// <summary>The rasterised field, or null before the first update.</summary>
    public WaterField? Field => rasterised;

    /// <summary>The window the field currently covers.</summary>
    public WaterFieldDescription Window => rasterised?.Description ?? default;

    /// <summary>How many times the field has been rasterised.</summary>
    /// <remarks>
    ///     What says the threshold is doing its job: a number that tracks the frame count is a zone
    ///     paying its whole cost every frame, and a number that stops rising while the view moves is
    ///     one whose window has been left behind.
    /// </remarks>
    public int RasterCount { get; private set; }

    /// <summary>Why the last <see cref="Update" /> did what it did.</summary>
    public WaterZoneUpdate LastUpdate { get; private set; }

    /// <summary>The bodies this zone rasterises.</summary>
    public IReadOnlyList<WaterBody> Bodies => bodies;

    /// <summary>Replaces the body list and marks the field stale.</summary>
    /// <param name="replacement">The bodies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="replacement" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Wholesale rather than incremental, and for
    ///     [31 § D4](../../docs/plan/31-terrain-grass-and-trees.md)'s reason.</b> A body that moved
    ///     changes the field where it was as well as where it is, so an incremental update would have
    ///     to remember the old shape — and the one that gets forgotten is the one that leaves a river
    ///     behind after the river has gone.
    /// </remarks>
    public void SetBodies(IReadOnlyList<WaterBody> replacement) {
        ArgumentNullException.ThrowIfNull(replacement);

        bodies.Clear();
        bodies.AddRange(replacement);
        dirty = true;
    }

    /// <summary>Says that a body, the ground or something else the field reads has changed.</summary>
    /// <remarks>
    ///     Called by whoever moved it. There is no change detection here on purpose: a zone that
    ///     compared bodies every frame to find out whether to rasterise would be doing most of the
    ///     work of rasterising them.
    /// </remarks>
    public void Invalidate() => dirty = true;

    /// <summary>Changes the zone's shape, which invalidates everything about the field.</summary>
    /// <param name="zone">The new shape.</param>
    /// <exception cref="ArgumentException">The zone is not one that can be rasterised.</exception>
    public void Reshape(in WaterZone zone) {
        if (zone.Validate() is { } why) {
            throw new ArgumentException(why, nameof(zone));
        }

        // A resolution change is a different field and the old arrays cannot be moved into it.
        if (zone.Resolution != Zone.Resolution) {
            rasterised = null;
        }

        Zone = zone;
        dirty = true;
    }

    /// <summary>Brings the field up to date for a view, if it is not already.</summary>
    /// <param name="view">Where the view is, on the ground plane.</param>
    /// <param name="ground">Where the ground is.</param>
    /// <returns>What it did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ground" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The scroll test is against the window's <em>centre</em> and not against its snapped
    ///     origin.</b> The origin moves in whole coarse texels, so testing it would compare a
    ///     continuous camera against a quantised number and fire a frame early or late depending on
    ///     where in a texel the camera happened to be — which is a re-rasterisation whose timing
    ///     depends on the world origin.
    /// </remarks>
    public WaterZoneUpdate Update(Vector2 view, IWaterGround ground) {
        ArgumentNullException.ThrowIfNull(ground);

        if (rasterised is null) {
            rasterised = new(Zone.WindowFor(view));
            Rasterize(view, ground, WaterZoneUpdate.First);

            return LastUpdate;
        }

        if (dirty) {
            rasterised.Move(Zone.WindowFor(view));
            Rasterize(view, ground, WaterZoneUpdate.Changed);

            return LastUpdate;
        }

        if (Vector2.Distance(view, centre) >= Zone.ScrollDistance) {
            rasterised.Move(Zone.WindowFor(view));
            Rasterize(view, ground, WaterZoneUpdate.Scrolled);

            return LastUpdate;
        }

        LastUpdate = WaterZoneUpdate.None;

        return LastUpdate;
    }

    /// <summary>A query over this zone's field and a sea state.</summary>
    /// <param name="spectrum">The sea state.</param>
    /// <param name="attenuation">How it falls off as the ground rises.</param>
    /// <returns>The query, which reads the field this zone owns.</returns>
    /// <remarks>
    ///     ⚠ <b>The query holds the field rather than a copy of it</b>, so a window that scrolls is a
    ///     window the boat floating on it sees scroll. A query built from a snapshot would answer for
    ///     a window that no longer exists, which is a boat that keeps floating where the water used to
    ///     be.
    /// </remarks>
    public WaterQuery Query(in WaterWaveSpectrum spectrum, WaterAttenuation attenuation = default) =>
        new(rasterised, spectrum, attenuation);

    void Rasterize(Vector2 view, IWaterGround ground, WaterZoneUpdate why) {
        rasterised!.Rasterize(bodies, ground);

        centre = view;
        dirty = false;
        RasterCount++;
        LastUpdate = why;
    }
}
