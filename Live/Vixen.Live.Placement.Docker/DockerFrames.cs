// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Vixen.Live.Placement;

/// <summary>The Engine's log framing, which is eight bytes and a payload.</summary>
/// <remarks>
///     <para>
///         A container created without a TTY has its stdout and stderr multiplexed into one stream,
///         each chunk prefixed with <c>[stream, 0, 0, 0, length…]</c> — one byte saying which stream,
///         three of padding, and a big-endian length. That is the whole format, and reading it is
///         what lets a realm's lifecycle line be told from something it wrote to stderr.
///     </para>
///     <para>
///         ⚠ <b>A frame is not a line.</b> The daemon flushes when the process does, so one frame may
///         hold several lines or half of one. Everything here exists to turn frames back into lines,
///         and getting that wrong shows up as a realm whose ready signal is never recognised because
///         it arrived split across two writes.
///     </para>
/// </remarks>
static class DockerFrames {
    /// <summary>How big a payload this will accept before giving up on the stream.</summary>
    /// <remarks>
    ///     A length field read out of alignment is an enormous number, and allocating what it asks
    ///     for is how a malformed stream becomes an out-of-memory. Sixteen megabytes is far more than
    ///     any log frame and far less than a problem.
    /// </remarks>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Reads a framed stream as lines.</summary>
    /// <param name="stream">The Engine's log stream.</param>
    /// <param name="cancellation">Ends the enumeration.</param>
    /// <returns>Each complete line, in order, from both streams.</returns>
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellation
    ) {
        var header = new byte[8];
        var pending = new StringBuilder();

        while (!cancellation.IsCancellationRequested) {
            if (!await Fill(stream, header, cancellation).ConfigureAwait(false)) {
                break;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));

            if (length is < 0 or > MaxFrameBytes) {
                break;
            }

            if (length == 0) {
                continue;
            }

            var payload = new byte[length];

            if (!await Fill(stream, payload, cancellation).ConfigureAwait(false)) {
                break;
            }

            pending.Append(Encoding.UTF8.GetString(payload));

            foreach (var line in Drain(pending)) {
                yield return line;
            }
        }

        // Whatever is left with no newline behind it. A process that exited mid-line still said
        // something, and a launcher reading its last words wants them.
        if (pending.Length > 0) {
            yield return pending.ToString();
        }
    }

    static List<string> Drain(StringBuilder pending) {
        var lines = new List<string>();
        var text = pending.ToString();
        var start = 0;

        for (var index = 0; index < text.Length; index++) {
            if (text[index] != '\n') {
                continue;
            }

            lines.Add(text[start..index].TrimEnd('\r'));
            start = index + 1;
        }

        if (lines.Count > 0) {
            pending.Clear();
            pending.Append(text[start..]);
        }

        return lines;
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken cancellation) {
        var read = 0;

        while (read < buffer.Length) {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);

            if (got == 0) {
                // The stream ended. A partial frame is not an error — it is a container that stopped
                // while the daemon was writing, which is every container eventually.
                return false;
            }

            read += got;
        }

        return true;
    }
}
