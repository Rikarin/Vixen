// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Rendering.Water;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>What the zone panel edits, and the numbers it derives from them.</summary>
/// <remarks>
///     <para>
///         <b>[35 § The zone panel](../../docs/plan/35-water.md#the-zone-panel), and the derived half
///         is the point of it.</b> A resolution somebody types is meaningless and a metre per texel is
///         not; the panel shows both, updating as the resolution changes, in
///         [31](../../docs/plan/31-terrain-grass-and-trees.md)'s create-dialog style. Doc 35 § D3 puts
///         it plainly: "a number an author types into <c>render_target_resolution</c> with no idea
///         what it buys is how the reference gets configured wrongly".
///     </para>
///     <para>
///         ⚠ <b>The readouts are computed by the <em>kernel</em> rather than by the panel.</b>
///         <see cref="WaterZone.MetresPerTexel" />, <see cref="WaterZone.Bytes" /> and
///         <see cref="WaterZone.HeightQuantum" /> are the same properties the renderer sizes its
///         texture from — a panel with its own arithmetic is a panel that can be right about a
///         configuration the renderer refuses.
///     </para>
/// </remarks>
[DataContract]
public sealed class WaterZoneSettings {
    /// <summary>How wide and deep the sliding window is, in metres.</summary>
    [Inspector]
    [Header("Window")]
    [Range(16f, 8192f)]
    public float Extent { get; set; } = 512f;

    /// <summary>How many texels along each axis.</summary>
    /// <remarks>
    ///     ⚠ <b>A power of two <em>plus one</em>, and the panel refuses anything else.</b> The samples
    ///     include both edges, so 257 over 512 m is two metres exactly where 256 would be 2.0078 — and
    ///     a snap grid stated in whole metres would then land the window on a fraction of a texel,
    ///     which is a shoreline that crawls while the camera moves.
    /// </remarks>
    [Inspector]
    [Range(33f, 2049f)]
    public int Resolution { get; set; } = 257;

    /// <summary>How many bits a channel of the info texture carries.</summary>
    [Inspector]
    public WaterInfoPrecision Precision { get; set; } = WaterInfoPrecision.Full;

    /// <summary>How far the view may move before the window is re-rasterised, as a fraction.</summary>
    [Inspector]
    [Range(0.01f, 0.49f)]
    public float ScrollThreshold { get; set; } = 0.125f;

    /// <summary>How many metres a texel of the coarsest thing reading the field covers.</summary>
    /// <remarks>
    ///     ⚠ Zero means "this field's own texel", which is right until a ripple simulation samples it
    ///     at a different rate — and then the two grids beat and the shoreline crawls.
    /// </remarks>
    [Inspector]
    [Range(0f, 64f)]
    public float CoarsestTexel { get; set; }

    /// <summary>How hard the wind blows, in metres a second.</summary>
    [Inspector]
    [Header("Sea state")]
    [Range(0f, 40f)]
    public float WindSpeed { get; set; } = 8f;

    /// <summary>Which way, in degrees about the vertical axis.</summary>
    [Inspector]
    [Range(0f, 360f)]
    public float WindDirection { get; set; }

    /// <summary>How far off the wind a wave may travel, in degrees.</summary>
    /// <remarks>⚠ Zero is corrugated iron rather than a sea — see <see cref="WaterWaveSpectrum" />.</remarks>
    [Inspector]
    [Range(0f, 180f)]
    public float DirectionalSpread { get; set; } = 34f;

    /// <summary>How many waves the sum runs over.</summary>
    [Inspector]
    public WaterWaveCount WaveCount { get; set; } = WaterWaveCount.Sixteen;

    /// <summary>Which sea, of all the seas with these numbers.</summary>
    [Inspector]
    public int Seed { get; set; } = 1;

    /// <summary>The depth at which waves reach their full height, in metres.</summary>
    [Inspector]
    [Range(0.1f, 30f)]
    public float AttenuationDepth { get; set; } = 2f;

    /// <summary>These numbers as the kernel's own description.</summary>
    public WaterZone Zone =>
        new() {
            Extent = Extent,
            Resolution = Resolution,
            Precision = Precision,
            ScrollThreshold = ScrollThreshold,
            CoarsestTexel = CoarsestTexel
        };

    /// <summary>And the sea state.</summary>
    public WaterWaveSpectrum Spectrum =>
        WaterWaveSpectrum.Default with {
            WindSpeed = WindSpeed,
            WindDirection = WindDirection * (MathF.PI / 180f),
            DirectionalSpread = DirectionalSpread * (MathF.PI / 180f),
            Count = WaveCount,
            Seed = (uint)Math.Max(Seed, 0)
        };

    /// <summary>What a scene entity carries, built from these.</summary>
    public WaterZoneComponent Component =>
        new() {
            Extent = Extent,
            Resolution = Resolution,
            Precision = Precision,
            ScrollThreshold = ScrollThreshold,
            CoarsestTexel = CoarsestTexel,
            Waves = Spectrum,
            AttenuationDepth = AttenuationDepth
        };

    /// <summary>Why this zone cannot be created, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     The kernel's own <see cref="WaterZone.Validate" />, so the panel refuses exactly what the
    ///     renderer would — a second rule here is a create button that succeeds and a frame that does
    ///     not.
    /// </remarks>
    public string? Validate() => Zone.Validate();

    /// <summary>The derived facts the panel shows under the form, in the order it shows them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four numbers, and each is one somebody would otherwise discover from a frame
    ///         time.</b> Metres per texel is whether a shoreline can be resolved at all; the
    ///         megabytes are what a resolution costs; the vertex count is what the surface mesh draws
    ///         at its finest; and the height quantum is what half precision does to a horizon.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The maximum amplitude is beside them because it decides three other things</b> —
    ///         the node error metric, the far-mesh cut and the collision bounds — so an author raising
    ///         the wind from a breeze to a gale should see what it costs before the frame time does.
    ///     </para>
    /// </remarks>
    public IEnumerable<(string Label, string Value)> Facts() {
        var zone = Zone;

        yield return ("Metres per texel", $"{zone.MetresPerTexel:0.###} m");
        yield return ("Info texture", $"{zone.Bytes / (1024f * 1024f):0.##} MB");

        // The finest level's vertex spacing *is* the texel spacing — WaterSurfaceMesh sizes its root
        // for that — so a full window is one vertex per texel and this is a number, not an estimate.
        yield return ("Vertices, full window", $"{(long)zone.Resolution * zone.Resolution:N0}");

        yield return (
            "Height quantum",
            Precision == WaterInfoPrecision.Half
                ? $"{zone.HeightQuantum * 100f:0.##} cm  (half precision)"
                : "exact  (full precision)"
        );

        Span<GerstnerWave> waves = stackalloc GerstnerWave[(int)WaveCount];
        var spectrum = Spectrum;

        if (spectrum.Validate() is null) {
            var count = spectrum.Generate(waves);

            yield return ("Maximum amplitude", $"{WaterWaveSpectrum.MaximumAmplitude(waves[..count]):0.##} m");
        }
    }
}

/// <summary>What the body inspector edits.</summary>
/// <remarks>
///     § "The body inspector": kind, spline, material, wave asset, then the profile, then the terrain
///     group, the underwater group and the physics group. What is here is the first two groups; the
///     underwater group is a <see cref="Vixen.Rendering.Ecs.PostProcessVolume" /> block verbatim, which
///     doc 32's own inspector already draws, and the material is an asset reference the project's
///     picker supplies.
/// </remarks>
[DataContract]
public sealed class WaterBodySettings {
    /// <summary>What kind of water it is.</summary>
    [Inspector]
    [Header("Body")]
    public WaterBodyKind Kind { get; set; } = WaterBodyKind.Lake;

    /// <summary>Where a closed body's surface sits, in world units.</summary>
    /// <remarks>⚠ Ignored by a river, whose surface follows its own curve.</remarks>
    [Inspector]
    public float SurfaceHeight { get; set; }

    /// <summary>Which body wins where two overlap. Higher is on top.</summary>
    [Inspector]
    public int Priority { get; set; }

    /// <summary>How far the channel reaches either side of the curve, in metres.</summary>
    [Inspector]
    [Header("Profile")]
    [Range(0f, 500f)]
    public float HalfWidth { get; set; } = 4f;

    /// <summary>How far below the surface the bed sits, in metres.</summary>
    [Inspector]
    [Range(0f, 200f)]
    public float Depth { get; set; } = 3f;

    /// <summary>How fast the water moves downstream, in metres a second.</summary>
    /// <remarks>⚠ Only an open body flows. A lake with a velocity is not a thing water does.</remarks>
    [Inspector]
    [Range(0f, 20f)]
    public float Velocity { get; set; }

    /// <summary>How loud it is.</summary>
    /// <remarks>
    ///     The channel everybody forgets until the river is silent. It carries no rendering meaning at
    ///     all and exists so a rapid is louder than the pool below it without somebody placing
    ///     emitters by hand.
    /// </remarks>
    [Inspector]
    [Range(0f, 1f)]
    public float AudioIntensity { get; set; } = 0.1f;

    /// <summary>How wide the band is over which coverage falls to zero at the boundary, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Narrower than a few of the zone's texels and the field cannot resolve it</b>, however
    ///     smooth the arithmetic is — which is what the zone panel's metres-per-texel readout is for.
    /// </remarks>
    [Inspector]
    [Header("Shore")]
    [Range(0f, 64f)]
    public float ShoreFalloff { get; set; } = 2f;

    /// <summary>How far inside the boundary the bed reaches its full depth, in metres.</summary>
    [Inspector]
    [Range(0f, 128f)]
    public float BedRamp { get; set; } = 4f;

    /// <summary>How much of its bed the terrain takes, 0…1.</summary>
    /// <remarks>⚠ Zero is Unreal's <c>Affects Landscape</c> — a body that floats over ground somebody sculpted.</remarks>
    [Inspector]
    [Header("Terrain")]
    [Range(0f, 1f)]
    public float CarveStrength { get; set; } = 1f;

    /// <summary>Which paint layer the bed is, or −1 for none.</summary>
    [Inspector]
    public int BedLayer { get; set; } = -1;

    /// <summary>The per-control-point profile this describes.</summary>
    public WaterProfilePoint Profile =>
        new() {
            HalfWidth = HalfWidth,
            Depth = Depth,
            Velocity = Velocity,
            AudioIntensity = AudioIntensity
        };

    /// <summary>How much of the bed the ground takes, and what it paints along it.</summary>
    public WaterCarveProfile Carve =>
        WaterCarveProfile.Default with { Strength = CarveStrength, BedLayer = BedLayer };

    /// <summary>What a scene entity carries, built from these and a spline's name.</summary>
    /// <param name="spline">What the body's curve is called.</param>
    /// <returns>The component.</returns>
    public WaterBodyComponent ComponentFor(string spline) =>
        new() {
            Kind = Kind,
            Spline = spline ?? string.Empty,
            SurfaceHeight = SurfaceHeight,
            Priority = Priority,
            ShoreFalloff = ShoreFalloff,
            BedRamp = BedRamp,
            HalfWidth = HalfWidth,
            Depth = Depth,
            Velocity = Velocity,
            AudioIntensity = AudioIntensity
        };

    /// <summary>Why this body cannot be made, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     ⚠ <b>The velocity one is the whole difference between a river and a bent lake</b>, and it is
    ///     refused rather than ignored: a closed body carrying a velocity is a number an author typed
    ///     and will look for on screen, and silently dropping it is worse than saying so.
    /// </remarks>
    public string? Validate() {
        if (Kind != WaterBodyKind.River && Velocity != 0f) {
            return $"A {Kind} has no direction to flow in, so its velocity does nothing. A body of "
                + "water that runs downhill is a River; a lake whose whole surface drifted one way is "
                + "not a thing water does.";
        }

        if (Kind == WaterBodyKind.River && HalfWidth <= 0f) {
            return "A river is a channel about its centreline, so it needs a half-width. A closed "
                + "body takes its shape from its own curve and can leave this at zero.";
        }

        return null;
    }
}
