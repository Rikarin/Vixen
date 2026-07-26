// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     Writes collected samples as a Chrome <c>trace_event</c> JSON document, which opens directly
///     in <c>ui.perfetto.dev</c> and in <c>chrome://tracing</c>.
/// </summary>
/// <remarks>
///     <para>
///         A deliberate choice not to build a viewer. Perfetto's is better than anything this project
///         would write, it handles multi-gigabyte traces, and it already understands nested scopes,
///         multiple threads and counter tracks. The engine's job is to produce a file it can read.
///     </para>
///     <para>
///         The JSON form is the fallback that every tool accepts. Perfetto's protobuf format is
///         smaller and streams, and is worth adding when trace size becomes the problem — it needs a
///         protobuf dependency, and picking one up to save bytes nobody has yet measured would be the
///         wrong order.
///     </para>
/// </remarks>
public static class TraceExporter {
    /// <summary>Writes a trace document for the given samples.</summary>
    /// <param name="threads">What <see cref="Profiler.Collect" /> returned.</param>
    /// <param name="destination">Where to write the JSON.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void WriteChromeTrace(IReadOnlyList<ProfilerThreadSamples> threads, Stream destination) {
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(destination);

        using var writer = new Utf8JsonWriter(
            destination,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
        );

        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WriteStartArray("traceEvents");

        foreach (var thread in threads) {
            // Name the thread first, or the viewer labels every track with a bare number.
            writer.WriteStartObject();
            writer.WriteString("name", "thread_name");
            writer.WriteString("ph", "M");
            writer.WriteNumber("pid", 1);
            writer.WriteNumber("tid", thread.ThreadId);
            writer.WriteStartObject("args");
            writer.WriteString("name", thread.ThreadName);
            writer.WriteEndObject();
            writer.WriteEndObject();

            foreach (var sample in thread.Samples) {
                // "X" is a complete event: one record with a duration, rather than the begin/end
                // pair that has to be balanced. A truncated ring cannot produce an unmatched half.
                writer.WriteStartObject();
                writer.WriteString("name", sample.Key.Name);
                writer.WriteString("ph", "X");
                writer.WriteNumber("pid", 1);
                writer.WriteNumber("tid", thread.ThreadId);
                writer.WriteNumber("ts", ToMicroseconds(sample.BeginTicks));
                writer.WriteNumber("dur", sample.DurationMicroseconds);
                writer.WriteStartObject("args");
                writer.WriteNumber("frame", sample.FrameIndex);
                writer.WriteNumber("depth", sample.Depth);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Writes a trace document to a file.</summary>
    /// <param name="threads">What <see cref="Profiler.Collect" /> returned.</param>
    /// <param name="path">Where to write it.</param>
    public static void WriteChromeTrace(IReadOnlyList<ProfilerThreadSamples> threads, string path) {
        using var file = File.Create(path);
        WriteChromeTrace(threads, file);
    }

    /// <summary>
    ///     Renders collected samples as a text summary: total and average time per key, worst first.
    ///     What a headless run or a CI log wants, where nobody is going to open a trace viewer.
    /// </summary>
    /// <param name="threads">What <see cref="Profiler.Collect" /> returned.</param>
    /// <returns>The summary.</returns>
    public static string Summarize(IReadOnlyList<ProfilerThreadSamples> threads) {
        ArgumentNullException.ThrowIfNull(threads);

        var totals = new Dictionary<ProfilingKey, (int Count, long Ticks)>();

        foreach (var thread in threads) {
            foreach (var sample in thread.Samples) {
                var existing = totals.GetValueOrDefault(sample.Key);
                totals[sample.Key] = (existing.Count + 1, existing.Ticks + sample.DurationTicks);
            }
        }

        if (totals.Count == 0) {
            return "No samples recorded.";
        }

        var builder = new StringBuilder();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"{"Scope",-40} {"Calls",8} {"Total ms",12} {"Mean ms",12}"
        );

        foreach (var (key, (count, ticks)) in totals.OrderByDescending(static entry => entry.Value.Ticks)) {
            var total = ticks * 1000.0 / Stopwatch.Frequency;
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"{key.Name,-40} {count,8} {total,12:F3} {total / count,12:F4}"
            );
        }

        return builder.ToString();
    }

    // Stopwatch ticks are not the same unit on every platform, and a trace viewer wants microseconds
    // from an arbitrary origin.
    static double ToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
}
