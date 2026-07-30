// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>Every screen probe's radiance map, surface, and resolved answer.</summary>
/// <remarks>
///     <para>
///         <b>The storage does not know what filled it</b> — the same property, for the same reason,
///         as the irradiance field's pool. A texel written by the CPU reference gather and a texel a
///         compute shader will one day write are the same texel, and the resolve reads them the same
///         way. That is what lets the shader be compared against this at all.
///     </para>
///     <para>
///         <b>A probe without a surface is invalid, not black.</b> A probe whose anchor pixel shows
///         the sky has nothing to stand on and nothing to gather for; marking it black instead would
///         pull every neighbouring pixel toward darkness through the bilinear filter, which is the
///         screen-space version of the buried-probe leak the irradiance field's validity exists for.
///         Invalid probes drop out of <see cref="Irradiance(Int2, Vector3)" /> and the weights
///         renormalise over what is left.
///     </para>
///     <para>
///         <b>The resolve is a projection with exact texel weights.</b> Each map texel carries its
///         own solid angle out of <see cref="OctahedralMap.SolidAngles" />, so a map holding a
///         constant radiance resolves to exactly that constant — the closed form every test starts
///         from. The resolved probe stores what <c>SphericalHarmonicsL1.Irradiance</c> evaluates:
///         irradiance over π, the number a shading pass multiplies by albedo, the same convention as
///         the irradiance field — and clamped at zero on the way out for the same reason the field
///         clamps: four coefficients can answer below zero for a normal facing away from all of the
///         light, and a negative ambient term is a hole in the picture.
///     </para>
/// </remarks>
public sealed class ScreenProbeAtlas {
    readonly Vector3[] texels;
    readonly Vector3[] positions;
    readonly Vector3[] normals;
    readonly bool[] valid;
    readonly SphericalHarmonicsL1[] resolved;
    readonly Int2[] adaptivePixels;
    readonly Vector3[] adaptivePositions;
    readonly Vector3[] adaptiveNormals;
    readonly SphericalHarmonicsL1[] adaptiveResolved;
    readonly ReadOnlyMemory<float> solidAngles;

    /// <summary>Builds an empty atlas over a layout.</summary>
    /// <param name="layout">Where the probes stand.</param>
    public ScreenProbeAtlas(ScreenProbeLayout layout) {
        Layout = layout;
        texels = new Vector3[(layout.ProbeCount + layout.AdaptiveCapacity) * layout.MapResolution * layout.MapResolution];
        positions = new Vector3[layout.ProbeCount];
        normals = new Vector3[layout.ProbeCount];
        valid = new bool[layout.ProbeCount];
        resolved = new SphericalHarmonicsL1[layout.ProbeCount];
        adaptivePixels = new Int2[layout.AdaptiveCapacity];
        adaptivePositions = new Vector3[layout.AdaptiveCapacity];
        adaptiveNormals = new Vector3[layout.AdaptiveCapacity];
        adaptiveResolved = new SphericalHarmonicsL1[layout.AdaptiveCapacity];
        solidAngles = OctahedralMap.SolidAngles(layout.MapResolution);
    }

    /// <summary>Where the probes stand.</summary>
    public ScreenProbeLayout Layout { get; }

    /// <summary>How many adaptive probes are placed right now.</summary>
    public int AdaptiveCount { get; private set; }

    /// <summary>How many probes have a surface.</summary>
    public int ValidCount {
        get {
            var count = 0;

            foreach (var value in valid) {
                if (value) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>One texel of one probe's map.</summary>
    /// <param name="probe">The probe.</param>
    /// <param name="texel">The texel within its map.</param>
    /// <returns>The radiance arriving from that texel's directions.</returns>
    public Vector3 this[Int2 probe, Int2 texel] {
        get => texels[TexelIndex(probe, texel)];
        set => texels[TexelIndex(probe, texel)] = value;
    }

    /// <summary>Gives a probe the surface it stands on.</summary>
    /// <param name="probe">The probe.</param>
    /// <param name="position">Where its anchor pixel's surface is, in world space.</param>
    /// <param name="normal">Which way that surface faces, normalised.</param>
    public void SetSurface(Int2 probe, Vector3 position, Vector3 normal) {
        var index = Layout.ProbeIndex(probe);

        positions[index] = position;
        normals[index] = normal;
        valid[index] = true;
    }

    /// <summary>Marks a probe as standing on nothing.</summary>
    /// <param name="probe">The probe.</param>
    /// <remarks>Its map and its resolved answer are zeroed — an invalid probe holds no leftovers.</remarks>
    public void Invalidate(Int2 probe) {
        var index = Layout.ProbeIndex(probe);

        positions[index] = default;
        normals[index] = default;
        valid[index] = false;
        resolved[index] = SphericalHarmonicsL1.Zero;

        var resolution = Layout.MapResolution;
        var start = index * resolution * resolution;

        Array.Clear(texels, start, resolution * resolution);
    }

    /// <summary>Whether a probe has a surface.</summary>
    /// <param name="probe">The probe.</param>
    public bool IsValid(Int2 probe) => valid[Layout.ProbeIndex(probe)];

    /// <summary>The surface a probe stands on, when it has one.</summary>
    /// <param name="probe">The probe.</param>
    /// <param name="position">Where.</param>
    /// <param name="normal">Which way it faces.</param>
    /// <returns>Whether it has one.</returns>
    public bool TrySurface(Int2 probe, out Vector3 position, out Vector3 normal) {
        var index = Layout.ProbeIndex(probe);

        position = positions[index];
        normal = normals[index];

        return valid[index];
    }

    /// <summary>One texel of one adaptive probe's map.</summary>
    /// <param name="adaptive">The adaptive probe, 0 to <see cref="AdaptiveCount" /> − 1.</param>
    /// <param name="texel">The texel within its map.</param>
    public Vector3 this[int adaptive, Int2 texel] {
        get => texels[AdaptiveTexelIndex(adaptive, texel)];
        set => texels[AdaptiveTexelIndex(adaptive, texel)] = value;
    }

    /// <summary>Places an adaptive probe on a pixel's surface, if the budget allows one more.</summary>
    /// <param name="pixel">The pixel it stands on.</param>
    /// <param name="position">Where that surface is, in world space.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <returns>Its index, or −1 when the capacity is spent.</returns>
    /// <remarks>
    ///     −1 rather than a throw, because the capacity is a <i>budget</i>: a frame with more broken
    ///     tiles than slots keeps the probes it placed and the rest of the screen falls back to the
    ///     lattice, which is exactly what it did before adaptive probes existed.
    /// </remarks>
    public int PlaceAdaptive(Int2 pixel, Vector3 position, Vector3 normal) {
        if (AdaptiveCount >= Layout.AdaptiveCapacity) {
            return -1;
        }

        var index = AdaptiveCount++;

        adaptivePixels[index] = pixel;
        adaptivePositions[index] = position;
        adaptiveNormals[index] = normal;
        adaptiveResolved[index] = SphericalHarmonicsL1.Zero;

        var resolution = Layout.MapResolution;

        Array.Clear(texels, (Layout.ProbeCount + index) * resolution * resolution, resolution * resolution);

        return index;
    }

    /// <summary>Forgets every adaptive probe — the start of a new placement pass.</summary>
    public void ClearAdaptive() => AdaptiveCount = 0;

    /// <summary>The surface an adaptive probe stands on.</summary>
    /// <param name="index">The adaptive probe.</param>
    /// <param name="pixel">The pixel it was placed on.</param>
    /// <param name="position">Where its surface is.</param>
    /// <param name="normal">Which way it faces.</param>
    /// <exception cref="ArgumentOutOfRangeException">No adaptive probe has that index.</exception>
    public void AdaptiveSurface(int index, out Int2 pixel, out Vector3 position, out Vector3 normal) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, AdaptiveCount);

        pixel = adaptivePixels[index];
        position = adaptivePositions[index];
        normal = adaptiveNormals[index];
    }

    /// <summary>An adaptive probe's resolved radiance, as projected by the last <see cref="Resolve" />.</summary>
    /// <param name="index">The adaptive probe.</param>
    public SphericalHarmonicsL1 ResolvedAdaptive(int index) => adaptiveResolved[index];

    /// <summary>Projects every valid probe's map into its resolved answer.</summary>
    /// <remarks>
    ///     Separate from writing the texels because the projection is linear and total: it reads the
    ///     whole map once, whoever wrote it, however many times a texel was rewritten before this.
    /// </remarks>
    public void Resolve() {
        var resolution = Layout.MapResolution;
        var weights = solidAngles.Span;

        for (var y = 0; y < Layout.GridSize.Y; y++) {
            for (var x = 0; x < Layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var index = Layout.ProbeIndex(probe);

                if (!valid[index]) {
                    resolved[index] = SphericalHarmonicsL1.Zero;

                    continue;
                }

                var projection = SphericalHarmonicsL1.Zero;

                for (var ty = 0; ty < resolution; ty++) {
                    for (var tx = 0; tx < resolution; tx++) {
                        projection = projection.Accumulated(
                            OctahedralMap.Direction(new(tx, ty), resolution),
                            this[probe, new(tx, ty)],
                            weights[(ty * resolution) + tx]
                        );
                    }
                }

                resolved[index] = projection;
            }
        }

        for (var adaptive = 0; adaptive < AdaptiveCount; adaptive++) {
            var projection = SphericalHarmonicsL1.Zero;

            for (var ty = 0; ty < resolution; ty++) {
                for (var tx = 0; tx < resolution; tx++) {
                    projection = projection.Accumulated(
                        OctahedralMap.Direction(new(tx, ty), resolution),
                        this[adaptive, new Int2(tx, ty)],
                        weights[(ty * resolution) + tx]
                    );
                }
            }

            adaptiveResolved[adaptive] = projection;
        }
    }

    /// <summary>A probe's resolved radiance, as projected by the last <see cref="Resolve" />.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>The projection, or zero for a probe without a surface.</returns>
    public SphericalHarmonicsL1 Resolved(Int2 probe) => resolved[Layout.ProbeIndex(probe)];

    /// <summary>The indirect diffuse a pixel receives, over π.</summary>
    /// <param name="pixel">The pixel.</param>
    /// <param name="normal">The way its surface faces, normalised.</param>
    /// <returns>Irradiance over π — what a shading pass multiplies by albedo. Zero where no probe knows.</returns>
    /// <remarks>
    ///     The four probes around the pixel, weighted bilinearly, with invalid probes dropped and the
    ///     weights renormalised over what is left. Blending the coefficients and evaluating once is
    ///     exact rather than convenient — the projection is linear — and it is one irradiance
    ///     evaluation instead of four.
    /// </remarks>
    public Vector3 Irradiance(Int2 pixel, Vector3 normal) {
        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        Layout.Bilinear(pixel, taps);

        var blended = SphericalHarmonicsL1.Zero;
        var weight = 0f;

        foreach (var tap in taps) {
            if (tap.Weight <= 0f || !valid[Layout.ProbeIndex(tap.Probe)]) {
                continue;
            }

            blended = new(
                blended.L00 + (resolved[Layout.ProbeIndex(tap.Probe)].L00 * tap.Weight),
                blended.L1m1 + (resolved[Layout.ProbeIndex(tap.Probe)].L1m1 * tap.Weight),
                blended.L10 + (resolved[Layout.ProbeIndex(tap.Probe)].L10 * tap.Weight),
                blended.L11 + (resolved[Layout.ProbeIndex(tap.Probe)].L11 * tap.Weight)
            );

            weight += tap.Weight;
        }

        if (weight <= 0f) {
            return Vector3.Zero;
        }

        return Vector3.Max(blended.Scaled(1f / weight).Irradiance(normal), Vector3.Zero);
    }

    /// <summary>The indirect diffuse a pixel receives, over π — knowing where the pixel's surface is.</summary>
    /// <param name="pixel">The pixel.</param>
    /// <param name="position">Where its surface is, in world space.</param>
    /// <param name="normal">The way that surface faces, normalised.</param>
    /// <param name="tolerance">How far off a probe's plane the surface may stand and still read it.</param>
    /// <returns>Irradiance over π. Zero where no probe knows.</returns>
    /// <remarks>
    ///     <para>
    ///         The adaptive half of <see cref="Irradiance(Int2, Vector3)" />: a tap whose probe stands
    ///         on a <i>different surface</i> — the pixel's position farther than
    ///         <paramref name="tolerance" /> from the probe's plane — is dropped exactly as an invalid
    ///         one is, because blending it in is how light bleeds across a depth edge.
    ///     </para>
    ///     <para>
    ///         The fallbacks, in order, each one only when the previous answered nothing: the
    ///         plane-filtered lattice; the nearest adaptive probe whose plane accepts the surface;
    ///         then the unfiltered lattice — the bleed the tolerance exists to prevent, but a smeared
    ///         answer over a black hole is the same call the dilation rule made — and zero last.
    ///     </para>
    /// </remarks>
    public Vector3 Irradiance(Int2 pixel, Vector3 position, Vector3 normal, float tolerance) {
        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        Layout.Bilinear(pixel, taps);

        var blended = SphericalHarmonicsL1.Zero;
        var weight = 0f;

        foreach (var tap in taps) {
            var index = Layout.ProbeIndex(tap.Probe);

            if (tap.Weight <= 0f || !valid[index] || Mismatch(position, positions[index], normals[index]) > tolerance) {
                continue;
            }

            blended = new(
                blended.L00 + (resolved[index].L00 * tap.Weight),
                blended.L1m1 + (resolved[index].L1m1 * tap.Weight),
                blended.L10 + (resolved[index].L10 * tap.Weight),
                blended.L11 + (resolved[index].L11 * tap.Weight)
            );

            weight += tap.Weight;
        }

        if (weight > 0f) {
            return Vector3.Max(blended.Scaled(1f / weight).Irradiance(normal), Vector3.Zero);
        }

        var nearest = -1;
        var nearestDistance = float.MaxValue;

        for (var adaptive = 0; adaptive < AdaptiveCount; adaptive++) {
            if (Mismatch(position, adaptivePositions[adaptive], adaptiveNormals[adaptive]) > tolerance) {
                continue;
            }

            var offset = adaptivePixels[adaptive] - pixel;
            var distance = (float)((offset.X * offset.X) + (offset.Y * offset.Y));

            if (distance < nearestDistance) {
                nearest = adaptive;
                nearestDistance = distance;
            }
        }

        if (nearest >= 0) {
            return Vector3.Max(adaptiveResolved[nearest].Irradiance(normal), Vector3.Zero);
        }

        return Irradiance(pixel, normal);
    }

    /// <summary>How far a surface stands off a probe's plane.</summary>
    /// <remarks>Internal so the gather's placement asks the same question the sampling fallback asks.</remarks>
    internal static float Mismatch(Vector3 position, Vector3 probePosition, Vector3 probeNormal) =>
        MathF.Abs(Vector3.Dot(position - probePosition, probeNormal));

    int AdaptiveTexelIndex(int adaptive, Int2 texel) {
        var resolution = Layout.MapResolution;

        ArgumentOutOfRangeException.ThrowIfNegative(adaptive);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(adaptive, AdaptiveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.X, resolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.Y, resolution);

        return ((Layout.ProbeCount + adaptive) * resolution * resolution) + (texel.Y * resolution) + texel.X;
    }

    int TexelIndex(Int2 probe, Int2 texel) {
        var resolution = Layout.MapResolution;

        ArgumentOutOfRangeException.ThrowIfNegative(texel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.X, resolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.Y, resolution);

        return (Layout.ProbeIndex(probe) * resolution * resolution) + (texel.Y * resolution) + texel.X;
    }
}
