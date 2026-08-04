// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live;

/// <summary>Where a client opens its session. Data, never configuration.</summary>
/// <remarks>
///     <para>
///         Doc 27 § The routing question: the client learns <i>"an endpoint and a ticket"</i> from
///         the gate and nothing above the transport knows the difference between a realm's own
///         address and one a relay allocated on its behalf. That property is what makes DDoS
///         scrubbing, IPv4 exhaustion and console platform requirements a <em>placement</em>
///         decision rather than an architecture change (M-Q1) — and it only holds while this value
///         travels with the placement answer instead of being read from a config file at boot.
///     </para>
///     <para>
///         A host string rather than an <c>IPEndPoint</c>: a Kubernetes node's external address, a
///         relay's DNS name and a loopback port are all things this has to carry, and resolving them
///         is the transport's job at the moment of connecting rather than the orchestrator's at the
///         moment of deciding.
///     </para>
/// </remarks>
/// <param name="Host">A host name or address literal.</param>
/// <param name="Port">A UDP port, 1–65535, or zero for "the backend will choose".</param>
public readonly record struct RealmEndpoint(string Host, int Port) {
    /// <summary>Nowhere.</summary>
    public static RealmEndpoint None => default;

    /// <summary>The host. Null only on <c>default</c>; see <see cref="RealmInstanceId" />.</summary>
    public string Host { get; } = Host ?? "";

    /// <summary>Whether this names somewhere a client could reach.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Host) && Port is > 0 and <= 65535;

    /// <summary>Whether this is a request for an endpoint rather than one — anything with no port.</summary>
    /// <remarks>
    ///     <para>
    ///         What a <see cref="RealmSpec" /> carries before <see cref="IRealmPlacement.StartAsync" />
    ///         has run. The host may be named or not, and both are ordinary: an orchestrator placing
    ///         on a known node fills it in, and one placing on Kubernetes cannot — the node is chosen
    ///         by the scheduler and its external address is something only the backend can report.
    ///     </para>
    ///     <para>
    ///         So <c>default</c> is unbound rather than nonsense: "you decide, and tell me" is a
    ///         thing an orchestrator says.
    ///     </para>
    /// </remarks>
    public bool IsUnbound => Port == 0;

    /// <summary>The same endpoint on a different port.</summary>
    /// <param name="port">The port the backend allocated.</param>
    /// <returns>The bound endpoint.</returns>
    public RealmEndpoint On(int port) => new(Host, port);

    /// <summary>Reads one back.</summary>
    /// <param name="text"><c>host:port</c>, as <see cref="ToString" /> writes it.</param>
    /// <param name="endpoint">The endpoint, on success.</param>
    /// <returns>Whether it parsed.</returns>
    /// <remarks>
    ///     ⚠ <b>The last colon separates</b>, so a bracketed IPv6 literal —
    ///     <c>[2001:db8::1]:7777</c> — parses to the address and the port rather than to nonsense.
    ///     An unbracketed IPv6 literal does not, and cannot: <c>::1:7777</c> is a valid address on
    ///     its own and there is no way to tell which reading was meant.
    /// </remarks>
    public static bool TryParse(string? text, out RealmEndpoint endpoint) {
        endpoint = None;

        if (text is null) {
            return false;
        }

        var separator = text.LastIndexOf(':');

        if (separator <= 0
            || !int.TryParse(text.AsSpan(separator + 1), CultureInfo.InvariantCulture, out var port)
            || port is < 0 or > 65535) {
            return false;
        }

        var candidate = new RealmEndpoint(text[..separator], port);

        if (!candidate.IsValid && !candidate.IsUnbound) {
            return false;
        }

        endpoint = candidate;

        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Host)
            ? "nowhere"
            : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");
}
