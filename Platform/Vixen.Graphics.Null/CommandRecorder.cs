// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Graphics.Null;

/// <summary>The command stream, as a list you can assert on.</summary>
/// <remarks>
///     <para>
///         What makes an RHI test a unit test. "Did the shadow feature bind the right pipeline and
///         draw the right number of casters" is a question about a sequence of calls, and answering
///         it by rendering an image and diffing it is slower, flakier and tells you less about what
///         went wrong.
///     </para>
///     <para>
///         Off unless a device is told to record. <c>docs/plan/05</c> is explicit that
///         <c>Vixen.Graphics.Null</c> is a shipping backend as well as a test one — a dedicated
///         server runs on it — and a server that accumulated a command log would run out of memory
///         some hours in.
///     </para>
/// </remarks>
public sealed class CommandRecorder {
    readonly List<RecordedCommand> commands = [];
    readonly Lock gate = new();

    /// <summary>Everything recorded so far, in order.</summary>
    /// <remarks>A snapshot, because command lists record on their own threads and an assertion
    /// walking a live list would be walking it while it changed.</remarks>
    public IReadOnlyList<RecordedCommand> Commands {
        get {
            lock (gate) {
                return [.. commands];
            }
        }
    }

    /// <summary>How many calls have been recorded.</summary>
    public int Count {
        get {
            lock (gate) {
                return commands.Count;
            }
        }
    }

    /// <summary>Adds a command, stamping it with its position in the stream.</summary>
    /// <param name="command">The command. Its sequence is overwritten.</param>
    /// <returns>The command as recorded.</returns>
    public RecordedCommand Record(RecordedCommand command) {
        lock (gate) {
            var stamped = command with { Sequence = commands.Count };
            commands.Add(stamped);
            return stamped;
        }
    }

    /// <summary>Throws everything away.</summary>
    public void Clear() {
        lock (gate) {
            commands.Clear();
        }
    }

    /// <summary>Every recorded call of one kind, in order.</summary>
    /// <param name="kind">Which call.</param>
    public IReadOnlyList<RecordedCommand> OfKind(RecordedCommandKind kind) {
        lock (gate) {
            return [.. commands.Where(command => command.Kind == kind)];
        }
    }

    /// <summary>How many calls of one kind were recorded.</summary>
    /// <param name="kind">Which call.</param>
    public int CountOf(RecordedCommandKind kind) {
        lock (gate) {
            return commands.Count(command => command.Kind == kind);
        }
    }

    /// <summary>Whether the recorded stream contains a run of calls matching this one, in order.</summary>
    /// <param name="expected">The calls to look for. Sequence numbers are ignored.</param>
    /// <remarks>
    ///     Contiguous, and that is deliberate: a test that only asked whether the calls appeared
    ///     <em>somewhere</em> would pass when a barrier was inserted in the middle of a copy, which
    ///     is exactly the mistake worth catching.
    /// </remarks>
    public bool Contains(params ReadOnlySpan<RecordedCommand> expected) {
        if (expected.IsEmpty) {
            return true;
        }

        lock (gate) {
            for (var start = 0; start + expected.Length <= commands.Count; start++) {
                var matched = true;

                for (var offset = 0; offset < expected.Length; offset++) {
                    if (!commands[start + offset].Matches(expected[offset])) {
                        matched = false;
                        break;
                    }
                }

                if (matched) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The stream as text, one call per line.</summary>
    /// <remarks>
    ///     Indented by debug group, because a real frame's stream is hundreds of lines and the
    ///     groups are what make it navigable — the same reason they are worth recording in a
    ///     RenderDoc capture.
    /// </remarks>
    public string Dump() {
        var builder = new StringBuilder();
        var depth = 0;

        foreach (var command in Commands) {
            if (command.Kind == RecordedCommandKind.PopDebugGroup) {
                depth = Math.Max(0, depth - 1);
            }

            builder.Append(' ', depth * 2).AppendLine(command.ToString());

            if (command.Kind == RecordedCommandKind.PushDebugGroup) {
                depth++;
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => $"{Count} commands";
}
