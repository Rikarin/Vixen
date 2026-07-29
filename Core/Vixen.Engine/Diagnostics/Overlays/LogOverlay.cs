// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>The tail of the log ring, on screen, with a level filter.</summary>
/// <remarks>
///     <para>
///         The overlay that makes a build self-describing. <c>RingBufferSink</c> is on in every build
///         — that is the whole point of it — so the last thirty lines are always there to be shown,
///         including on a device with no console attached and no way to get a file off it, which is
///         the case this exists for.
///     </para>
///     <para>
///         ⚠ <b>The tail is copied, not snapshotted.</b> The ring holds a hundred thousand records by
///         default; calling <c>Snapshot</c> once a frame to read the last thirty would allocate
///         megabytes a frame. <see cref="RingBufferSink.CopyTail" /> fills a fixed buffer this
///         overlay owns.
///     </para>
///     <para>
///         ⚠ <b>Long lines are cut rather than wrapped.</b> Wrapping a stack trace into a corner
///         panel turns thirty records into three; the cut end is what a log tail is for — the
///         beginning of the message says which one it is, and the full text is in the ring for
///         whoever reads it properly.
///     </para>
/// </remarks>
public sealed class LogOverlay : IDiagnosticOverlay {
    /// <summary>How many records the overlay can show at once.</summary>
    public const int MaxLines = 40;

    readonly RingBufferSink sink;
    readonly LogRecord[] tail = new LogRecord[MaxLines];

    /// <summary>Reads from a sink.</summary>
    /// <param name="sink">The always-on log ring.</param>
    public LogOverlay(RingBufferSink sink) {
        ArgumentNullException.ThrowIfNull(sink);
        this.sink = sink;
    }

    /// <inheritdoc />
    public string Name => "log";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 460f;

    /// <summary>How many lines are shown.</summary>
    public int Lines { get; set; } = 12;

    /// <summary>The level below which records are not shown. Does not change what is recorded.</summary>
    /// <remarks>
    ///     Separate from the sink's own minimum on purpose: turning the overlay down to warnings must
    ///     not stop the ring recording the information the crash reporter will want.
    /// </remarks>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Whether the logger's category is shown before the message.</summary>
    public bool ShowCategory { get; set; } = true;

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var wanted = Math.Clamp(Lines, 1, MaxLines);
        var theme = surface.Theme;

        // ⚠ The filter is applied to the last MaxLines records rather than searched backwards through
        // the ring, so a panel set to warnings only shows the warnings among the newest forty lines
        // and not the newest twelve warnings. That is the right answer for a tail — it stays in step
        // with what is happening now — and hunting an old warning is what the ring itself is for.
        var count = sink.CopyTail(tail);
        var region = surface.Panel(Anchor, Width, wanted, "LOG");

        if (region.IsEmpty) {
            return;
        }

        var characters = Math.Max(1, (int) (region.ContentWidth / DebugFont.AdvanceFor(theme.TextSize)));

        Span<char> buffer = stackalloc char[256];
        var row = wanted - 1;

        for (var index = count - 1; index >= 0 && row >= 0; index--) {
            var record = tail[index];

            if (record.Level < MinimumLevel) {
                continue;
            }

            var length = Compose(buffer, record, characters);
            region.Text(row, buffer[..length], Colour(record.Level, theme));
            row--;
        }

        var dropped = sink.DroppedCount;

        // The ring having overwritten its beginning is worth saying: "nothing was logged before this"
        // and "the beginning is gone" look identical otherwise, and only one of them is a problem.
        if (dropped > 0 && row >= 0 && buffer.TryWrite($"… {dropped} earlier records overwritten", out var note)) {
            region.Text(row, buffer[..note], theme.Muted);
        }
    }

    /// <summary>Lays one record out as a single line, cut to the width available.</summary>
    int Compose(Span<char> destination, LogRecord record, int characters) {
        var limit = Math.Min(destination.Length, characters);
        var written = 0;

        Append(destination, ref written, limit, Abbreviation(record.Level));
        Append(destination, ref written, limit, " ");

        if (ShowCategory) {
            // The last segment only: "Vixen.Graphics.Vulkan.VulkanDevice" is most of a line on its
            // own, and the part that identifies it is the end.
            var category = record.Category.AsSpan();
            var dot = category.LastIndexOf('.');

            Append(destination, ref written, limit, dot >= 0 ? category[(dot + 1)..] : category);
            Append(destination, ref written, limit, ": ");
        }

        Append(destination, ref written, limit, record.Message);

        if (record.Exception is not null) {
            Append(destination, ref written, limit, " <");
            Append(destination, ref written, limit, record.Exception.GetType().Name);
            Append(destination, ref written, limit, ">");
        }

        return written;
    }

    static void Append(Span<char> destination, ref int written, int limit, ReadOnlySpan<char> text) {
        var room = Math.Min(text.Length, limit - written);

        if (room > 0) {
            text[..room].CopyTo(destination[written..]);
            written += room;
        }
    }

    static string Abbreviation(LogLevel level) =>
        level switch {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---"
        };

    static Color4 Colour(LogLevel level, in OverlayTheme theme) =>
        level switch {
            LogLevel.Trace or LogLevel.Debug => theme.Muted,
            LogLevel.Warning => theme.Warning,
            LogLevel.Error or LogLevel.Critical => theme.Bad,
            _ => theme.Text
        };
}
