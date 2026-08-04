// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Vixen.Live.Gate;

/// <summary>One account's open service-plane socket.</summary>
/// <remarks>
///     ⚠ <b>Per account, not per character.</b> A player at character select has no character and
///     still needs to be told a catalog was published; a player with two clients open on one account
///     gets both told. Keying by character would make the socket's lifetime the session's, and the
///     socket is meant to outlive every session on it.
/// </remarks>
public interface IGateSubscriber {
    /// <summary>Whose.</summary>
    Guid Account { get; }

    /// <summary>Sends something, and does not care whether it arrives.</summary>
    /// <param name="message">What.</param>
    /// <returns>When queued.</returns>
    ValueTask PostAsync(GateEvent message);
}

/// <summary>Who is listening, and how the gate says something to them. Doc 27 § The three planes.</summary>
/// <remarks>
///     <para>
///         This is the second of the client's two connections: one UDP session to its realm, one WSS
///         to here. What travels on it is everything whose recipient may be on another realm,
///         offline, or on another continent — guild and whisper chat, party invites, a catalog that
///         has been published, a shard that is about to drain.
///     </para>
///     <para>
///         ⚠ <b>This socket is allowed to be down, and every message on it is allowed to be lost.</b>
///         Nothing a player is waiting on travels here — that is the data plane — and anything that
///         would be wrong to lose is a request the client makes rather than a push it receives. A
///         push is a hint to go and ask.
///     </para>
/// </remarks>
public sealed class ServicePlane {
    readonly ConcurrentDictionary<Guid, ConcurrentDictionary<IGateSubscriber, byte>> listeners = new();

    /// <summary>How many sockets are open.</summary>
    public int Count => listeners.Values.Sum(account => account.Count);

    /// <summary>Starts listening.</summary>
    /// <param name="subscriber">Who.</param>
    public void Join(IGateSubscriber subscriber) {
        ArgumentNullException.ThrowIfNull(subscriber);

        listeners.GetOrAdd(subscriber.Account, _ => new()).TryAdd(subscriber, 0);
    }

    /// <summary>Stops.</summary>
    /// <param name="subscriber">Who.</param>
    public void Leave(IGateSubscriber subscriber) {
        ArgumentNullException.ThrowIfNull(subscriber);

        if (listeners.TryGetValue(subscriber.Account, out var open)) {
            open.TryRemove(subscriber, out _);

            if (open.IsEmpty) {
                listeners.TryRemove(subscriber.Account, out _);
            }
        }
    }

    /// <summary>Says something to one account, on every socket it has open.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="message">What.</param>
    /// <returns>When every socket has been told, or has failed to be.</returns>
    public async ValueTask TellAsync(Guid account, GateEvent message) {
        if (!listeners.TryGetValue(account, out var open)) {
            return;
        }

        foreach (var subscriber in open.Keys) {
            await PostAsync(subscriber, message).ConfigureAwait(false);
        }
    }

    /// <summary>Says something to everybody. What a catalog publication is.</summary>
    /// <param name="message">What.</param>
    /// <returns>When everybody has been told.</returns>
    public async ValueTask TellEveryoneAsync(GateEvent message) {
        foreach (var account in listeners.Values) {
            foreach (var subscriber in account.Keys) {
                await PostAsync(subscriber, message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tells one subscriber, and drops it if telling it fails.</summary>
    /// <remarks>
    ///     ⚠ <b>The broad catch is the point.</b> One client's broken socket must not be able to fail
    ///     a broadcast to everybody else, and a broadcast is exactly where an unhandled exception is
    ///     most expensive: the listeners after the broken one never hear it, and which ones those are
    ///     depends on dictionary order. <c>WebSocketSubscriber</c> already swallows its own send
    ///     failures; this is the same guarantee for a subscriber somebody else wrote.
    /// </remarks>
    async ValueTask PostAsync(IGateSubscriber subscriber, GateEvent message) {
        try {
            await subscriber.PostAsync(message).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception) {
#pragma warning restore CA1031
            Leave(subscriber);
        }
    }
}

/// <summary>A real WebSocket, as a subscriber.</summary>
/// <remarks>
///     ⚠ <b>A send that fails is dropped rather than thrown.</b> One client's dead socket must not
///     be able to fail a broadcast to everybody else, and the socket's own receive loop is what
///     notices it is gone and leaves. That is the same shape as a replication send to a connection
///     that has stopped acknowledging.
/// </remarks>
/// <param name="account">Whose socket.</param>
/// <param name="socket">It.</param>
public sealed class WebSocketSubscriber(Guid account, WebSocket socket) : IGateSubscriber, IDisposable {
    readonly SemaphoreSlim writing = new(1, 1);

    /// <inheritdoc />
    public Guid Account => account;

    /// <inheritdoc />
    public async ValueTask PostAsync(GateEvent message) {
        if (socket.State != WebSocketState.Open) {
            return;
        }

        // One writer at a time: a WebSocket does not permit concurrent sends, and a broadcast plus a
        // keep-alive landing together is the ordinary case rather than the unlucky one.
        await writing.WaitAsync().ConfigureAwait(false);

        try {
            var json = JsonSerializer.Serialize(message, GateJson.Default.GateEvent);

            await socket
                .SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception failure) when (failure is WebSocketException or ObjectDisposedException or OperationCanceledException) {
            // Gone. The receive loop will notice and leave; nothing here is worth failing a broadcast
            // over.
        } finally {
            writing.Release();
        }
    }

    /// <summary>Releases the write gate.</summary>
    /// <remarks>The socket itself belongs to whoever accepted it.</remarks>
    public void Dispose() => writing.Dispose();
}
