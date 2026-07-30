// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>One probe's share of a pixel, out of the four around it.</summary>
/// <param name="Probe">Which probe.</param>
/// <param name="Weight">How much of the pixel's answer it carries, 0 to 1.</param>
public readonly record struct ScreenProbeTap(Int2 Probe, float Weight);

/// <summary>Where the screen probes stand, and where each one's map lives in the atlas.</summary>
/// <remarks>
///     <para>
///         <b>One probe per tile of pixels, anchored at the tile's centre.</b> Doc 19 § L3's "~16 px
///         screen grid": the gather is far too expensive per pixel, so it runs at one probe per tile
///         and every pixel interpolates the four probes around it. Sixteen is the default and a
///         parameter, because the right number is a quality setting and not a law.
///     </para>
///     <para>
///         <b>The interpolation lattice is the unclamped one.</b> An edge tile's anchor is clamped
///         into the viewport — a probe has to stand on a pixel that exists — but the bilinear weights
///         are computed on the regular lattice and clamped at the border, so a pixel outside the
///         outermost anchors takes the nearest probe whole rather than extrapolating. The same
///         shape, for the same reason, as the irradiance field repeating its last probe at the edge:
///         the answer stops changing rather than inventing.
///     </para>
///     <para>
///         Adaptive probes — extra placements where the coarse grid straddles a depth edge, doc 19's
///         "adaptive placement at disocclusions" — are deliberately not here yet. They are an
///         addition to this lattice, not a replacement for it, and the lattice has to be right first.
///     </para>
/// </remarks>
public readonly struct ScreenProbeLayout {
    /// <summary>Lays probes over a viewport.</summary>
    /// <param name="viewport">The viewport, in pixels.</param>
    /// <param name="tileSize">How many pixels one probe stands for, along each axis.</param>
    /// <param name="mapResolution">How many texels a probe's octahedral map is, along each axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">An empty viewport, tile or map.</exception>
    public ScreenProbeLayout(Int2 viewport, int tileSize = 16, int mapResolution = 8) {
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.Y, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(mapResolution, 1);

        Viewport = viewport;
        TileSize = tileSize;
        MapResolution = mapResolution;
        GridSize = new(
            (viewport.X + tileSize - 1) / tileSize,
            (viewport.Y + tileSize - 1) / tileSize
        );
    }

    /// <summary>The viewport the probes cover, in pixels.</summary>
    public Int2 Viewport { get; }

    /// <summary>How many pixels one probe stands for, along each axis.</summary>
    public int TileSize { get; }

    /// <summary>How many texels a probe's octahedral map is, along each axis.</summary>
    public int MapResolution { get; }

    /// <summary>How many probes there are, along each axis.</summary>
    public Int2 GridSize { get; }

    /// <summary>How many probes there are in all.</summary>
    public int ProbeCount => GridSize.X * GridSize.Y;

    /// <summary>The atlas holding every probe's map, in texels.</summary>
    public Int2 AtlasSize => GridSize * MapResolution;

    /// <summary>One probe's place in a flat array.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such probe.</exception>
    public int ProbeIndex(Int2 probe) {
        Validate(probe);

        return (probe.Y * GridSize.X) + probe.X;
    }

    /// <summary>The pixel a probe stands on.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>The pixel, inside the viewport.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such probe.</exception>
    /// <remarks>
    ///     The tile's centre pixel, clamped into the viewport for the partial tiles at the far edges
    ///     — a probe reads the scene at its anchor, and a pixel that does not exist has no scene to
    ///     read.
    /// </remarks>
    public Int2 Anchor(Int2 probe) {
        Validate(probe);

        return Int2.Min(
            new((probe.X * TileSize) + (TileSize / 2), (probe.Y * TileSize) + (TileSize / 2)),
            Viewport - Int2.One
        );
    }

    /// <summary>Where a probe's map starts in the atlas.</summary>
    /// <param name="probe">The probe.</param>
    /// <returns>The map's first texel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such probe.</exception>
    public Int2 AtlasOrigin(Int2 probe) {
        Validate(probe);

        return probe * MapResolution;
    }

    /// <summary>The four probes around a pixel, and how much of its answer each carries.</summary>
    /// <param name="pixel">The pixel.</param>
    /// <param name="taps">Room for four taps.</param>
    /// <exception cref="ArgumentOutOfRangeException">The pixel is outside the viewport.</exception>
    /// <exception cref="ArgumentException">Fewer than four slots.</exception>
    /// <remarks>
    ///     Always four, always summing to one. At the viewport's border the lattice is clamped, so
    ///     two or four of them can be the same probe — a caller accumulating <c>weight × probe</c>
    ///     does not care, and a list that changes length at the border is a branch every caller
    ///     would have to get right.
    /// </remarks>
    public void Bilinear(Int2 pixel, Span<ScreenProbeTap> taps) {
        ArgumentOutOfRangeException.ThrowIfNegative(pixel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(pixel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pixel.X, Viewport.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pixel.Y, Viewport.Y);

        if (taps.Length < 4) {
            throw new ArgumentException($"A pixel reads four probes, not {taps.Length}.", nameof(taps));
        }

        var (x0, x1, wx) = Axis(pixel.X, GridSize.X);
        var (y0, y1, wy) = Axis(pixel.Y, GridSize.Y);

        taps[0] = new(new(x0, y0), (1f - wx) * (1f - wy));
        taps[1] = new(new(x1, y0), wx * (1f - wy));
        taps[2] = new(new(x0, y1), (1f - wx) * wy);
        taps[3] = new(new(x1, y1), wx * wy);
    }

    /// <summary>The two probe columns (or rows) around a pixel coordinate, and the blend between them.</summary>
    (int Low, int High, float Weight) Axis(int pixel, int probes) {
        // Measured between anchor centres on the regular lattice: anchor i sits at i·tile + tile/2,
        // and the half-texel convention makes a pixel exactly on an anchor read that probe whole.
        var continuous = (pixel + 0.5f - (TileSize / 2) - 0.5f) / TileSize;

        var low = (int)MathF.Floor(continuous);
        var weight = continuous - low;

        if (low < 0) {
            return (0, 0, 0f);
        }

        if (low >= probes - 1) {
            return (probes - 1, probes - 1, 0f);
        }

        return (low, low + 1, weight);
    }

    void Validate(Int2 probe) {
        ArgumentOutOfRangeException.ThrowIfNegative(probe.X);
        ArgumentOutOfRangeException.ThrowIfNegative(probe.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(probe.X, GridSize.X);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(probe.Y, GridSize.Y);
    }
}
