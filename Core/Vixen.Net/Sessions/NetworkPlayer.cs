// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Time;

namespace Vixen.Net.Sessions;

/// <summary>Somebody in the session, whether or not they are currently connected.</summary>
/// <remarks>
///     A player outlives the connection that carried them. <see cref="IsConnected" /> is false for
///     the length of the reconnect window while their entities are still standing there — which is
///     what makes "hold their stuff for thirty seconds" a policy rather than a rewrite.
/// </remarks>
public sealed class NetworkPlayer {
    /// <summary>Who they are, for as long as the session lasts.</summary>
    public PlayerId Id { get; }

    /// <summary>What the server was told they are, by whatever authenticated them.</summary>
    public string Identity { get; internal set; }

    /// <summary>The connection carrying them, or <see cref="ConnectionId.None" /> while they are away.</summary>
    public ConnectionId Connection { get; internal set; }

    /// <summary>Whether this is us.</summary>
    public bool IsLocal { get; internal set; }

    /// <summary>Whether they are on the other end of a live connection right now.</summary>
    public bool IsConnected => Connection.IsValid;

    /// <summary>The tick they first joined at.</summary>
    public Tick JoinedAt { get; internal set; }

    /// <summary>Their round trip, as measured by whichever side is holding this record.</summary>
    public RoundTripEstimator RoundTrip { get; } = new();

    /// <summary>How many times they have come back after dropping.</summary>
    public int ReconnectCount { get; internal set; }

    internal double LastHeardFrom { get; set; }
    internal double ReconnectDeadline { get; set; }
    internal byte[] ReconnectToken { get; set; } = [];
    internal uint PingId { get; set; }
    internal double PingSentAt { get; set; }
    internal double NextPingAt { get; set; }
    internal bool PingOutstanding { get; set; }

    internal NetworkPlayer(PlayerId id, string identity) {
        Id = id;
        Identity = identity;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Identity.Length == 0 ? Id.ToString() : $"{Id} ({Identity})";
}
