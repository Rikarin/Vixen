// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Placement;

/// <summary>What to launch, with what, and where.</summary>
/// <param name="Executable">The realm's executable, or the runtime that will run it.</param>
/// <param name="Arguments">Its arguments, already including the encoded <see cref="RealmSpec" />.</param>
/// <param name="WorkingDirectory">Where to run it, or <see langword="null" /> for the launcher's own.</param>
/// <param name="Environment">Variables to add to the launcher's own.</param>
public sealed record RealmProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment
);

/// <summary>A realm process, as its launcher can see it.</summary>
/// <remarks>
///     Deliberately smaller than <c>System.Diagnostics.Process</c>: an identity, the two ends of
///     stdio, an exit, and a way to end it. Everything <see cref="ProcessPlacement" /> does is
///     expressed against this, which is what lets its tests run a fleet with no processes in it.
/// </remarks>
public interface IRealmProcessHandle : IDisposable {
    /// <summary>Something an operator can act on — a pid, or whatever stood in for one.</summary>
    string Id { get; }

    /// <summary>Whether it has gone.</summary>
    bool HasExited { get; }

    /// <summary>What it exited with, once <see cref="HasExited" /> is true.</summary>
    int ExitCode { get; }

    /// <summary>Every line the process wrote to stdout, as it writes them.</summary>
    /// <remarks>
    ///     ⚠ <b>Raised on whatever thread the reader runs on.</b> The launcher is not a frame loop
    ///     and does not pretend to be; what reads these turns them into events on a channel, which is
    ///     the one place the crossing happens.
    /// </remarks>
    event Action<string>? OutputLine;

    /// <summary>Says a line to the process's stdin.</summary>
    /// <param name="line">The line, without a newline.</param>
    /// <returns>Whether it was written — false if the process has gone.</returns>
    bool WriteLine(string line);

    /// <summary>Ends it, now.</summary>
    void Kill();

    /// <summary>Waits for it to exit.</summary>
    /// <param name="cancellation">Stops waiting. Does not stop the process.</param>
    /// <returns>When it has exited.</returns>
    Task WaitForExitAsync(CancellationToken cancellation);
}

/// <summary>How a realm process is actually started.</summary>
/// <remarks>
///     <para>
///         The seam, and the reason it exists is doc 27 § Testing: "<c>Vixen.Live.Placement.Process</c>
///         is to this document what <c>Vixen.Net.Transport.Local</c> is to doc 16". A test that
///         asserts a fleet converges after a randomised sequence of kills, restarts and partitions
///         cannot afford to be starting real processes — it would take minutes, it would be flaky on
///         a loaded CI machine, and killing a process at an exact moment is not something a test can
///         ask for.
///     </para>
///     <para>
///         So the backend is written against this, the default implementation is
///         <see cref="SystemProcessHost" />, and a test supplies one that starts nothing. The
///         production path and the tested path are the same code above this line, which is the
///         property that makes the seam worth its two interfaces.
///     </para>
/// </remarks>
public interface IRealmProcessHost {
    /// <summary>Starts one.</summary>
    /// <param name="request">What to launch.</param>
    /// <returns>The handle.</returns>
    /// <exception cref="InvalidOperationException">It could not be started.</exception>
    IRealmProcessHandle Start(RealmProcessRequest request);
}
