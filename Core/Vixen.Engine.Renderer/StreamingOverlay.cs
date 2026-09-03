// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Engine.Renderer;

/// <summary>`stat streaming`: what is resident, what is on its way, and what was refused.</summary>
/// <remarks>
///     <para>
///         Doc 13's third overlay — "which assets are resident, being loaded, or evicted" — and the
///         one of its three that was never actually blocked. The overview said the two remaining
///         panels needed <c>Vixen.Ui</c> and <c>Vixen.Assets</c> to report and that neither may
///         reference <c>Vixen.Engine</c>, so each wanted a join assembly of its own. ⚠ <b>That is
///         true of the UI panel and false of this one</b>, twice over: the numbers are not
///         <c>Vixen.Assets</c>' at all — <c>Vixen.Assets</c>' own README says so, and points at
///         <c>Vixen.Rendering</c>'s <c>PageResidency</c> as the one budget every streamer is a
///         consumer of — and this assembly already references <c>Vixen.Engine</c>,
///         <c>Vixen.Rendering</c> and <c>Vixen.Assets</c> together. The join assembly the blocker
///         asked for is the one this class is in, and <see cref="GpuOverlay" /> has lived in it since
///         before the blocker was written.
///     </para>
///     <para>
///         <b>Three rows that answer different questions, and the last is the one worth opening the
///         panel for.</b> Resident against budget says whether the pool is full; loading and pending
///         say whether the streamer is keeping up with where the camera is pointed; and refusals say
///         the pool is <em>too small for this scene</em> rather than that anything is broken. A frame
///         with refusals climbing sampled a coarser texture than it asked for and looked merely
///         slightly soft — which is the failure this panel exists to make visible, because nothing
///         else about it is.
///     </para>
///     <para>
///         ⚠ <b>Dashes rather than zeroes where nothing is measured</b>, which is
///         <see cref="FrameStatsOverlay" />'s convention and matters more here than usual. Texture
///         streaming is only stood up where there is a bindless table to put the textures in — see
///         <c>WorldRenderer.Mount</c> — so on a target without one, "no streamer" and "a streamer
///         that has loaded nothing" are different answers and a panel of zeroes would make them one.
///         Geometry residency, which is unconditional, is reported either way.
///     </para>
/// </remarks>
/// <param name="renderer">The frame whose streamers this reports on.</param>
public sealed class StreamingOverlay(WorldRenderer renderer) : IDiagnosticOverlay {
    readonly WorldRenderer renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    /// <inheritdoc />
    public string Name => "streaming";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 260f;

    /// <summary>
    ///     What fraction of the texture budget is resident, or −1 when nothing is streaming textures.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Negative rather than zero for "no streamer", and it is the same distinction the
    ///     dashes draw.</b> A pool that is empty and a pool that does not exist are the two states
    ///     this panel is for telling apart, and zero is a legitimate value of the first.
    /// </remarks>
    public float ResidentFraction { get; private set; } = -1f;

    /// <summary>How many rows the last <see cref="Draw" /> put on screen.</summary>
    public int DrawnRows { get; private set; }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var theme = surface.Theme;
        var streamer = renderer.Painted?.Streaming;
        var region = surface.Panel(Anchor, Width, 5, "STREAMING");

        Span<char> buffer = stackalloc char[48];

        if (streamer is null) {
            ResidentFraction = -1f;

            region.Text(0, "textures", theme.Heading);
            region.Text(1, "not streaming — no bindless table", theme.Muted);
        } else {
            ResidentFraction = streamer.Budget > 0
                ? (float)((double)streamer.ResidentBytes / streamer.Budget)
                : 0f;

            if (buffer.TryWrite($"{Megabytes(streamer.ResidentBytes):F1}/{Megabytes(streamer.Budget):F0} MB", out var sized)) {
                region.Meter(0, "resident", buffer[..sized], ResidentFraction, Scale(theme, ResidentFraction));
            }

            if (buffer.TryWrite($"{streamer.Loading} + {streamer.PendingRequests} queued", out var inFlight)) {
                region.Text(1, "loading", theme.Heading);
                region.TextRight(1, buffer[..inFlight], theme.Text);
            }

            if (buffer.TryWrite($"{streamer.Loads} / {streamer.Evictions}", out var churn)) {
                region.Text(2, "loads / evictions", theme.Heading);
                region.TextRight(2, buffer[..churn], theme.Text);
            }

            // ⚠ The row worth opening the panel for. A positive number is a pool too small for the
            // scene, and its only other symptom is a frame that looks very slightly soft.
            if (buffer.TryWrite($"{streamer.Rejections}", out var refused)) {
                region.Text(3, "refused", theme.Heading);
                region.TextRight(3, buffer[..refused], streamer.Rejections > 0 ? theme.Bad : theme.Muted);
            }
        }

        // Geometry is not conditional on anything, so it is reported whether or not textures are.
        if (buffer.TryWrite($"{renderer.Residency.Count} ({renderer.Residency.Claims} claims)", out var meshes)) {
            region.Text(4, "meshes", theme.Heading);
            region.TextRight(4, buffer[..meshes], theme.Text);
        }

        DrawnRows = 5;
    }

    static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>Green below three quarters, amber to nine tenths, red past it.</summary>
    /// <remarks>
    ///     A full pool is not an error — a streamer is meant to fill its budget — so the red is at
    ///     the point where the next request has nothing cheap left to evict, not at the point where
    ///     the pool is being used.
    /// </remarks>
    static Color4 Scale(OverlayTheme theme, float fraction) => fraction switch {
        < 0.75f => theme.Good,
        < 0.90f => theme.Warning,
        _ => theme.Bad
    };
}
