// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Placement;

/// <summary>What <see cref="ProcessPlacement" /> launches, and how patient it is.</summary>
public sealed record ProcessPlacementOptions {
    /// <summary>The realm's executable, or the runtime that runs it.</summary>
    /// <remarks>
    ///     <c>dotnet</c> with the assembly as the first argument works and is what a
    ///     <c>dotnet run</c> development loop produces; a published realm is its own executable and
    ///     is what a deployment uses. The backend does not care and deliberately does not inspect it.
    /// </remarks>
    public string Executable { get; init; } = "";

    /// <summary>
    ///     Arguments to put before the encoded <see cref="RealmSpec" />, in order.
    /// </summary>
    /// <remarks>
    ///     Where the assembly path goes when <see cref="Executable" /> is <c>dotnet</c>, and where
    ///     <c>--vixen-variant Server</c> goes for a build that did not stamp the attribute.
    /// </remarks>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Where to run them, or <see langword="null" /> for the launcher's own directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables to add to the launcher's environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The address clients are given when a spec did not name one.</summary>
    /// <remarks>
    ///     Loopback, because the ordinary use of this backend is a laptop and a test. A LAN
    ///     deployment sets the machine's own address here, which is the whole of its configuration —
    ///     this backend has no equivalent of Kubernetes's node-external-IP question because there is
    ///     only ever one node.
    /// </remarks>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The range realm ports are taken from.</summary>
    public PortPool Ports { get; init; } = new();

    /// <summary>
    ///     How long <see cref="StopMode.Drain" /> waits for a realm to finish moving its players out
    ///     before killing it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Long, and it should be.</b> Doc 27 § Drain's hard deadline is fifteen minutes,
    ///     because a raid finishing is what draining politely means. A launcher whose patience is
    ///     shorter than the readiness rules it is waiting on turns every drain into a kill, which is
    ///     the failure this default is set to avoid rather than to be tidy about.
    /// </remarks>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     How long <see cref="StopMode.Immediate" /> waits after saying so before killing.
    /// </summary>
    /// <remarks>
    ///     Enough to flush a log and release a lease, not enough to finish a fight. Nothing durable
    ///     is at risk either way (ADR-021); what this buys is a readable last line in the log.
    /// </remarks>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Called with every line a realm writes, for whoever is doing the logging.</summary>
    /// <remarks>
    ///     ⚠ <b>Raised on the reader's thread.</b> The backend does not marshal it, because the only
    ///     honest place to do that is the frame loop this launcher does not have.
    /// </remarks>
    public Action<RealmInstanceId, string>? Output { get; init; }
}
