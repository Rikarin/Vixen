// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Audio.Diagnostics;

/// <summary>
///     The audio overlay: active voices, the master level, and whether the mixer is keeping up.
/// </summary>
/// <remarks>
///     <para>
///         [13](../../../docs/plan/13-diagnostics.md) § Diagnostic overlays asks for "active voices,
///         mixer levels, 3D source positions". The first two are here, on the numbers
///         <see cref="AudioStatistics" /> already publishes; the third is world geometry rather than a
///         panel and belongs in a system drawing into <c>DebugDraw</c> beside the physics one, which
///         is why it is not in this file.
///     </para>
///     <para>
///         <b>Load is the number to watch and it is drawn first among the meters.</b> Everything else
///         on the panel is a symptom of it being close to one: a mixer at 0.8 will start dropping out
///         the first time the operating system schedules something else, and by the time the underrun
///         counters move the player has already heard it.
///     </para>
///     <para>
///         ⚠ <b>Lives here rather than in <c>Vixen.Engine</c></b> because the numbers do. The overlay
///         interface is the seam that lets a subsystem report on itself without the diagnostics layer
///         growing a reference to every subsystem there is.
///     </para>
/// </remarks>
/// <param name="engine">The engine to read.</param>
public sealed class AudioOverlay(AudioEngine engine) : IDiagnosticOverlay {
    const int Rows = 6;

    readonly AudioEngine engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public string Name => "audio";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 210f;

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var region = surface.Panel(Anchor, Width, Rows, "AUDIO");

        if (region.IsEmpty) {
            return;
        }

        // Taken once. Read a property at a time and the panel can show a voice count from this block
        // beside a load from the last one, which is the reason AudioStatistics is one value.
        var statistics = engine.Statistics;
        var theme = surface.Theme;

        Span<char> buffer = stackalloc char[48];

        if (buffer.TryWrite($"{statistics.Load,5:P0}", out var length)) {
            region.Meter(0, "load", buffer[..length], (float) statistics.Load, Level(statistics.Load, theme));
        }

        if (buffer.TryWrite($"{statistics.ActiveVoices,4}/{statistics.VoiceCapacity}", out length)) {
            var fraction = statistics.VoiceCapacity > 0
                ? statistics.ActiveVoices / (float) statistics.VoiceCapacity
                : 0f;

            region.Meter(1, "voices", buffer[..length], fraction, Level(fraction, theme));
        }

        // Above one is clipping, and it is drawn as a full bar in the bad colour rather than a bar
        // that runs off the end — a meter that silently pins at maximum says nothing happened.
        if (buffer.TryWrite($"{statistics.MasterPeak,6:F2}", out length)) {
            region.Meter(2, "peak", buffer[..length], statistics.MasterPeak, Level(statistics.MasterPeak, theme));
        }

        region.Text(3, "virtual", theme.Text);

        if (buffer.TryWrite($"{statistics.VirtualVoices,6}", out length)) {
            region.TextRight(3, buffer[..length], theme.Text);
        }

        region.Text(4, "streams", theme.Text);

        if (buffer.TryWrite($"{statistics.StreamCount,6}", out length)) {
            region.TextRight(4, buffer[..length], theme.Text);
        }

        // The three counters that only ever mean something has gone wrong, on one row: a device
        // underrun, a starved stream and a request that found nothing to displace.
        var faults = statistics.DeviceUnderruns + statistics.StreamUnderruns + statistics.DroppedRequests;

        region.Text(5, "under/starve/drop", theme.Text);

        if (buffer.TryWrite(
                $"{statistics.DeviceUnderruns}/{statistics.StreamUnderruns}/{statistics.DroppedRequests}",
                out length
            )) {
            region.TextRight(5, buffer[..length], faults > 0 ? theme.Bad : theme.Muted);
        }
    }

    static Vixen.Core.Mathematics.Color4 Level(double fraction, in OverlayTheme theme) =>
        fraction switch {
            >= 1d => theme.Bad,
            >= 0.75d => theme.Warning,
            _ => theme.Good
        };
}
