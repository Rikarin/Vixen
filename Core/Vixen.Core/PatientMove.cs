// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Core;

/// <summary>
///     Moves a finished file over another in one step, as <c>File.Move(from, to, true)</c> does, but waits briefly for
///     whoever is reading the file it replaces.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every save that writes a temporary and moves it into place goes through here.</b> Windows refuses to
///         rename onto a file anybody has open, with "Access to the path is denied", and a reader sharing delete does not
///         change that. A document saved while an asset scan, a file watcher's handler or another process was reading it
///         failed outright: Patina hit it in its library (its #250 and #257) and then in every Vixen save it calls — the
///         paint canvases, the layer stacks, the texture graphs (Patina #265).
///     </para>
///     <para>
///         A read is short, so the move is tried again, backing off from one millisecond to fifty, until
///         <see cref="Patience" /> has passed, and then the last failure is thrown as it was. On Linux and macOS the first
///         try succeeds. A failure that waiting cannot cure — the file to move is gone, a folder in the way is missing —
///         is thrown at once.
///     </para>
///     <para>
///         <c>PatientMoveTests</c> walks the repository and fails on any new overwriting <c>File.Move</c> outside a test.
///     </para>
/// </remarks>
public static class PatientMove {
    /// <summary>The longest wait, in milliseconds, between two tries.</summary>
    const int LongestPause = 50;

    /// <summary>How long a move over a file waits for that file's readers, at most.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>Moves <paramref name="from" /> over <paramref name="to" />, replacing what is there.</summary>
    /// <param name="from">The finished file, which is gone afterwards.</param>
    /// <param name="to">Where it goes.</param>
    /// <exception cref="UnauthorizedAccessException">The file was still held when <see cref="Patience" /> ran out.</exception>
    /// <exception cref="IOException">The move failed for a reason waiting cannot cure, or was still refused.</exception>
    public static void Over(string from, string to) {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);

        var waited = Stopwatch.StartNew();

        for (var pause = 1;; pause = Math.Min(pause * 2, LongestPause)) {
            try {
                File.Move(from, to, true);
                return;
            } catch (Exception e) when (Held(e) && waited.Elapsed < Patience && File.Exists(from)) {
                Thread.Sleep(pause);
            }
        }
    }

    /// <summary>Whether a move failed the way it does on Windows while the file it replaces is open.</summary>
    static bool Held(Exception e) =>
        e is UnauthorizedAccessException
            or IOException and not FileNotFoundException and not DirectoryNotFoundException;
}
