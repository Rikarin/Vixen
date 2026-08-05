// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Fuzz.Targets;

namespace Vixen.Net.Fuzz;

/// <summary>Every decode path bytes we did not write can reach, in one place.</summary>
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
///         <b>And then the files, which arrive by a different route and are the same problem.</b> A
///         bundle, a stored chunk and a heightmap PNG are not packets, and the machinery here never
///         required one — a target is a decoder with bytes pushed into it. A content update
///         downloads a bundle, a chunk comes out of it, and an importer is handed a PNG by a person
///         who cannot know what is in it; each has a length prefix that decides an allocation, which
///         is the property this harness was built to hold decoders to. See
///         <c>ContentTargets</c> for why those three catch their own documented refusal where the
///         packet targets catch nothing.
///     </para>
///     <para>
///         <b>And then the grammars, whose input is text and which needed nothing new to fuzz.</b> A
///         sidecar, a declaration value and an <c>@layer</c> rule are characters rather than bytes,
///         and the corpus, the mutator and all four oracles are indifferent to that: each target
///         decodes at its own edge, which is what the real system does with a file too. Two of them
///         also carry an oracle the byte targets have no use for — an answer that is <i>wrong</i>
///         rather than absent, checked by reading the same input twice and comparing. See
///         <c>MetaTargets</c> and <c>StylingTargets</c>.
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
    [
        "packet", "bits", "handshake", "client", "snapshot", "inspect", "delta", "rpc", "synclist", "input", "udp",
        "upgrade", "bundle", "chunk", "heightmap", "meta", "stylevalue", "layerrule", "vxml"
    ];

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
        new InputBufferTarget(),
        new UdpTransportTarget(),
        new WebSocketUpgradeTarget(),
        new BundleTarget(),
        new ChunkFormatTarget(),
        new HeightmapPngTarget(),
        new AssetMetaTarget(),
        new StyleValueTarget(),
        new LayerRuleTarget(),
        new VxmlTarget()
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
            // wait. Letting them go is the difference between naming a target and starting all of them.
            (target as IDisposable)?.Dispose();
        }

        return wanted
            ?? throw new ArgumentException(
                $"There is no fuzz target called '{name}'. Try one of: {string.Join(", ", Names)}.",
                nameof(name)
            );
    }
}
