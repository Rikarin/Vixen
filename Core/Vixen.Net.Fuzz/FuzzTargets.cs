// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Fuzz.Targets;

namespace Vixen.Net.Fuzz;

/// <summary>Every decode path a peer can reach, in one place.</summary>
/// <remarks>
///     <para>
///         <b>The list is the claim.</b> "The packet reader is fuzzed" is a much smaller statement
///         than it sounds: the reader is the bottom of the stack, and above it sit a handshake that
///         reads four fields from an unauthenticated connection, a router that dispatches on a pair
///         of indices, an applier that creates and destroys entities, and a list that takes an index
///         off the wire and mutates itself with it. All of those are reachable by somebody who can
///         send a packet, so all of them are here.
///     </para>
///     <para>
///         Each is constructed fresh, because several hold live state — a session with a player in
///         it, a client with a world behind it — and sharing one between runs would make a run
///         depend on what ran before it.
///     </para>
/// </remarks>
public static class FuzzTargets {
    /// <summary>The names, for a command line and for a report.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["packet", "bits", "handshake", "client", "snapshot", "inspect", "delta", "rpc", "synclist", "input"];

    /// <summary>Builds every target.</summary>
    /// <returns>Them, in the order <see cref="Names" /> lists.</returns>
    public static IReadOnlyList<IFuzzTarget> All() => [
        new PacketReaderTarget(),
        new BitReaderTarget(),
        new HandshakeTarget(),
        new SessionClientTarget(),
        new SnapshotTarget(),
        new SnapshotInspectorTarget(),
        new DeltaCodecTarget(),
        new RpcRouterTarget(),
        new SyncListTarget(),
        new InputBufferTarget()
    ];

    /// <summary>Builds one target by name.</summary>
    /// <param name="name">One of <see cref="Names" />.</param>
    /// <returns>It.</returns>
    /// <exception cref="ArgumentException">There is no such target.</exception>
    public static IFuzzTarget Named(string name) {
        var built = All();
        IFuzzTarget? wanted = null;

        foreach (var target in built) {
            if (wanted is null && string.Equals(target.Name, name, StringComparison.Ordinal)) {
                wanted = target;

                continue;
            }

            // The others were built to be asked their names and hold sessions and worlds while they
            // wait. Letting them go is the difference between naming a target and starting nine.
            (target as IDisposable)?.Dispose();
        }

        return wanted
            ?? throw new ArgumentException(
                $"There is no fuzz target called '{name}'. Try one of: {string.Join(", ", Names)}.",
                nameof(name)
            );
    }
}
