// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Transport;

namespace Vixen.Net.Tests.Transport;

/// <summary>What a transport reported, kept so a test can assert on it.</summary>
public enum RecordedKind {
    Connected,
    Disconnected,
    Data
}

/// <summary>One thing a transport reported.</summary>
/// <remarks>
///     The payload is a copy. It has to be: the span a transport hands to
///     <see cref="ITransportEvents.OnData" /> is only valid for that call, and a recorder that kept
///     the span would be the first thing to prove it.
/// </remarks>
public sealed record RecordedEvent(
    RecordedKind Kind,
    TransportRole Role,
    ConnectionId Connection,
    Channel Channel,
    DisconnectReason Reason,
    byte[] Payload
) {
    /// <summary>The payload read as UTF-8, which is what the tests send.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>Records everything a transport reports.</summary>
public sealed class EventRecorder : ITransportEvents {
    /// <summary>Everything reported, in the order it was reported.</summary>
    public List<RecordedEvent> Events { get; } = [];

    /// <inheritdoc />
    public void OnConnected(TransportRole role, ConnectionId connection) =>
        Events.Add(new(RecordedKind.Connected, role, connection, Channel.Reliable, DisconnectReason.Requested, []));

    /// <inheritdoc />
    public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) =>
        Events.Add(new(RecordedKind.Disconnected, role, connection, Channel.Reliable, reason, []));

    /// <inheritdoc />
    public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) =>
        Events.Add(new(RecordedKind.Data, role, connection, channel, DisconnectReason.Requested, payload.ToArray()));

    /// <summary>The connections reported as established on one half.</summary>
    /// <param name="role">The half to look at.</param>
    /// <returns>The connection ids, in the order they connected.</returns>
    public List<ConnectionId> Connects(TransportRole role) {
        var result = new List<ConnectionId>();

        foreach (var recorded in Events) {
            if (recorded is { Kind: RecordedKind.Connected } && recorded.Role == role) {
                result.Add(recorded.Connection);
            }
        }

        return result;
    }

    /// <summary>The disconnections reported on one half.</summary>
    /// <param name="role">The half to look at.</param>
    /// <returns>Each connection that ended and why, in the order they ended.</returns>
    public List<(ConnectionId Connection, DisconnectReason Reason)> Disconnects(TransportRole role) {
        var result = new List<(ConnectionId, DisconnectReason)>();

        foreach (var recorded in Events) {
            if (recorded is { Kind: RecordedKind.Disconnected } && recorded.Role == role) {
                result.Add((recorded.Connection, recorded.Reason));
            }
        }

        return result;
    }

    /// <summary>The payloads that arrived on one half.</summary>
    /// <param name="role">The half to look at.</param>
    /// <returns>The payloads, in the order they arrived.</returns>
    public List<RecordedEvent> Payloads(TransportRole role) {
        var result = new List<RecordedEvent>();

        foreach (var recorded in Events) {
            if (recorded is { Kind: RecordedKind.Data } && recorded.Role == role) {
                result.Add(recorded);
            }
        }

        return result;
    }

    /// <summary>The payloads that arrived on one half, as the text they were sent as.</summary>
    /// <param name="role">The half to look at.</param>
    /// <returns>The payloads decoded as UTF-8, in the order they arrived.</returns>
    public List<string> Texts(TransportRole role) {
        var result = new List<string>();

        foreach (var recorded in Payloads(role)) {
            result.Add(recorded.Text);
        }

        return result;
    }

    /// <summary>Forgets everything, so the next assertion is about what happens next.</summary>
    public void Clear() => Events.Clear();
}
