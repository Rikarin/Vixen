// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>What a probe's anchor pixel is looking at.</summary>
/// <remarks>
///     In a frame this is the depth buffer and the G-buffer's normal, reconstructed the way
///     <c>IndirectDiffuse</c> already reconstructs a pixel. The gather takes it as an interface for
///     the reason every reference here does: the arithmetic under test has to run against a surface
///     with a closed form, and a depth buffer is a picture of one rather than the thing itself.
/// </remarks>
public interface IScreenSurface {
    /// <summary>The surface a pixel shows, if it shows one.</summary>
    /// <param name="pixel">The pixel.</param>
    /// <param name="position">Where that surface is, in world space.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <returns>False for a pixel showing the sky.</returns>
    bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal);
}

/// <summary>How far a probe's rays look, and how they leave the surface they start on.</summary>
public readonly record struct ScreenProbeGatherSettings {
    /// <summary>The defaults: a hundred units, a small step off the surface.</summary>
    public ScreenProbeGatherSettings() { }

    /// <summary>How far a ray looks before deciding it hit nothing.</summary>
    public float MaxDistance { get; init; } = 100f;

    /// <summary>How far off its surface a probe stands, in world units.</summary>
    /// <remarks>
    ///     A screen probe stands <i>on</i> geometry — that is the difference from a field probe, which
    ///     floats in a lattice — so a ray cast from the surface itself immediately finds the surface
    ///     it started on, at distance zero, in half of all directions. Stepping out along the normal
    ///     is what makes the lower hemisphere mean "what the surface's own side of the world looks
    ///     like" rather than "here".
    /// </remarks>
    public float SurfaceBias { get; init; } = 0.01f;

    /// <summary>Throws if these settings cannot gather anything.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(SurfaceBias);
    }
}

/// <summary>Fills screen probes by marching a distance field, on the CPU.</summary>
/// <remarks>
///     <para>
///         <b>Doc 19 § L3's gather, written where it can be checked — and deliberately only its first
///         half.</b> The shipping version traces the screen against the HZB before it ever touches a
///         distance field, importance-samples the map against the BRDF and last frame's lighting, and
///         filters spatially and temporally before anything resolves. None of that is here, and that
///         is the point: one deterministic ray per octahedral texel is the arrangement with nothing
///         between it and a closed form, and it is what the compute version's unfiltered output gets
///         compared against — the same role <c>TracedIrradianceFiller</c> plays for § L2's filler,
///         and the same reason it exists.
///     </para>
///     <para>
///         <b>One ray per texel, at the texel's centre, with no jitter.</b> Two gathers of one scene
///         agree to the bit, so a test can assert an exact number rather than a tolerance around a
///         stochastic one. Importance sampling changes <i>which</i> texel a ray serves and how much it
///         counts, not what a texel means, so it belongs to the version that has a BRDF to sample
///         against.
///     </para>
///     <para>
///         <b>A probe standing inside geometry is invalid before any ray is cast</b> — the field's
///         sign says so, the same rule as the irradiance field's filler. A probe whose anchor shows
///         the sky is invalid before that, because there is no surface to stand on at all.
///     </para>
/// </remarks>
public sealed class TracedScreenProbeGather {
    readonly IDistanceField geometry;
    readonly IRadianceSource radiance;

    /// <summary>Builds a gather over a scene's geometry and its lighting.</summary>
    /// <param name="geometry">What the rays march through.</param>
    /// <param name="radiance">What they see when they stop.</param>
    /// <param name="options">How far to trace. Omitted takes the defaults.</param>
    /// <exception cref="ArgumentNullException">There is no geometry or no lighting.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    public TracedScreenProbeGather(
        IDistanceField geometry,
        IRadianceSource radiance,
        ScreenProbeGatherSettings? options = null
    ) {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(radiance);

        Settings = options ?? new ScreenProbeGatherSettings();
        Settings.Validate();

        this.geometry = geometry;
        this.radiance = radiance;
    }

    /// <summary>How this gather was told to trace.</summary>
    public ScreenProbeGatherSettings Settings { get; }

    /// <summary>Fills every probe of an atlas from what its anchor pixel shows.</summary>
    /// <param name="atlas">The atlas to fill.</param>
    /// <param name="surface">What the screen is looking at.</param>
    /// <returns>How many probes gathered — the rest were invalidated.</returns>
    /// <exception cref="ArgumentNullException">There is no atlas or no surface.</exception>
    /// <remarks>The atlas is left resolved: every valid probe's projection is ready to read.</remarks>
    public int Fill(ScreenProbeAtlas atlas, IScreenSurface surface) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(surface);

        var layout = atlas.Layout;
        var gathered = 0;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                if (!surface.TrySurface(layout.Anchor(probe), out var position, out var normal)) {
                    atlas.Invalidate(probe);

                    continue;
                }

                if (FillProbe(atlas, probe, position, normal)) {
                    gathered++;
                }
            }
        }

        atlas.Resolve();

        return gathered;
    }

    /// <summary>Gathers one probe standing on one surface.</summary>
    /// <param name="atlas">Where its map lives.</param>
    /// <param name="probe">The probe.</param>
    /// <param name="position">Where its surface is.</param>
    /// <param name="normal">Which way that surface faces, normalised.</param>
    /// <returns>Whether the probe gathered — false left it invalidated.</returns>
    /// <exception cref="ArgumentNullException">There is no atlas.</exception>
    /// <remarks>
    ///     Exposed because it is the whole of the arithmetic and a test wants it without a screen
    ///     around it, the same way the field filler exposes <c>Trace</c>. The caller resolves.
    /// </remarks>
    public bool FillProbe(ScreenProbeAtlas atlas, Int2 probe, Vector3 position, Vector3 normal) {
        ArgumentNullException.ThrowIfNull(atlas);

        var origin = position + (normal * Settings.SurfaceBias);

        if (geometry.Sample(origin) < 0f) {
            atlas.Invalidate(probe);

            return false;
        }

        atlas.SetSurface(probe, position, normal);

        var resolution = atlas.Layout.MapResolution;
        var trace = new DistanceFieldTraceSettings { MaxDistance = Settings.MaxDistance };

        for (var ty = 0; ty < resolution; ty++) {
            for (var tx = 0; tx < resolution; tx++) {
                var direction = OctahedralMap.Direction(new(tx, ty), resolution);
                var hit = DistanceFieldTracer.Trace(geometry, origin, direction, trace);

                atlas[probe, new(tx, ty)] = hit.Hit
                    ? radiance.Surface(hit.Position, hit.Normal, direction)
                    : radiance.Sky(direction);
            }
        }

        return true;
    }
}
