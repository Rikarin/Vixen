// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Rpc;

/// <summary>Where a router sends what it has encoded.</summary>
/// <remarks>
///     A seam rather than a direct call into the session, so the router can be tested against a fake
///     that records packets instead of a session, and so an editor's replay tooling can put itself in
///     the middle later.
/// </remarks>
public interface IRpcTransport {
    /// <summary>Sends to the server.</summary>
    /// <param name="payload">The encoded call.</param>
    /// <param name="channel">How to send it.</param>
    void SendToServer(ReadOnlySpan<byte> payload, Channel channel);

    /// <summary>Sends to one player.</summary>
    /// <param name="player">Who.</param>
    /// <param name="payload">The encoded call.</param>
    /// <param name="channel">How to send it.</param>
    void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel);

    /// <summary>Sends to everybody in the session.</summary>
    /// <param name="payload">The encoded call.</param>
    /// <param name="channel">How to send it.</param>
    void SendToAll(ReadOnlySpan<byte> payload, Channel channel);
}

/// <summary>Who can see an object, for <see cref="RpcTarget.Observers" />.</summary>
/// <remarks>
///     The same question <c>IInterestResolver</c> answers for replication, asked the other way round —
///     given an object, who is watching it. Kept separate because the answer has to be cheap here: a
///     client RPC resolves observers on the frame it is sent, not once a tick.
/// </remarks>
public interface IRpcObservers {
    /// <summary>Fills <paramref name="into" /> with everybody who can see an object.</summary>
    /// <param name="target">The object.</param>
    /// <param name="into">Where to put them. Cleared by the caller first.</param>
    void Resolve(NetworkId target, List<PlayerId> into);
}

/// <summary>What role the peer holding a router is playing.</summary>
public enum RpcRole : byte {
    /// <summary>Sends server calls, runs client ones.</summary>
    Client = 0,

    /// <summary>Runs server calls, sends client ones.</summary>
    Server = 1,

    /// <summary>Both, on one process. The loopback makes it the same code path.</summary>
    Host = 2
}

/// <summary>How many calls a connection may make.</summary>
/// <remarks>
///     A token bucket, per player. An RPC is the one thing a client can make the server do work for,
///     so it is the one thing a client can use to make the server do work for nobody else — and the
///     answer is a limit that exists by default rather than one somebody remembers to switch on.
/// </remarks>
public sealed record RpcLimits {
    /// <summary>How many calls a second a connection may sustain.</summary>
    public int CallsPerSecond { get; init; } = 120;

    /// <summary>How many it may make at once before the rate matters.</summary>
    public int Burst { get; init; } = 240;
}

/// <summary>Encodes remote calls, and refuses the ones that arrive.</summary>
/// <remarks>
///     <para>
///         <b>Nothing a packet says about who sent it is believed.</b> The sender is what the session
///         says it is — the connection the bytes arrived on — and it is what ownership and the rate
///         limit are checked against. A call carries what it is about and what to do, never who is
///         asking, because who is asking is exactly the field a client would fill in.
///     </para>
///     <para>
///         Every inbound call is checked before anything runs: the indices must name a call in the
///         manifest, the direction must be one this peer accepts, the object must be one that is
///         registered, ownership must hold if the call asks for it, the connection must be inside its
///         rate limit, and the arguments must decode and leave nothing behind. Six checks, each of
///         which is a counter, which is what the diagnostics panel will show.
///     </para>
/// </remarks>
public sealed class RpcRouter {
    readonly RpcManifest manifest;
    readonly IRpcTransport transport;
    readonly NetworkOwnership ownership;
    readonly IRpcObservers? observers;
    readonly RpcLimits limits;
    readonly byte[] sendBuffer;
    readonly Dictionary<(uint Object, uint Type), IRpcInvoker> invokers = [];
    readonly Dictionary<uint, double> tokens = [];
    readonly List<PlayerId> resolved = [];

    /// <summary>What this peer is.</summary>
    public RpcRole Role { get; set; }

    /// <summary>Who owns what.</summary>
    public NetworkOwnership Ownership => ownership;

    /// <summary>How many objects have handlers registered.</summary>
    public int RegisteredCount => invokers.Count;

    /// <summary>Calls that ran.</summary>
    public long AcceptedCount { get; private set; }

    /// <summary>Calls refused because the indices name nothing in the manifest.</summary>
    public long RefusedByManifestCount { get; private set; }

    /// <summary>Calls refused because they were going the wrong way for this peer.</summary>
    public long RefusedByDirectionCount { get; private set; }

    /// <summary>Calls refused because the object they are about is not registered here.</summary>
    public long RefusedByUnknownObjectCount { get; private set; }

    /// <summary>Calls refused because the caller does not own what they were calling about.</summary>
    public long RefusedByOwnershipCount { get; private set; }

    /// <summary>Calls refused because the caller is making too many.</summary>
    public long RefusedByRateLimitCount { get; private set; }

    /// <summary>Calls refused because the arguments did not decode, or left bits behind.</summary>
    public long RefusedByArgumentsCount { get; private set; }

    /// <summary>Calls this peer declined to send, because it is not the side that may send them.</summary>
    public long RefusedToSendCount { get; private set; }

    /// <summary>Creates a router.</summary>
    /// <param name="manifest">Every call this build knows about.</param>
    /// <param name="transport">Where encoded calls go.</param>
    /// <param name="role">What this peer is.</param>
    /// <param name="ownership">Who owns what. A fresh one, if null.</param>
    /// <param name="observers">Who can see what, for <see cref="RpcTarget.Observers" />.</param>
    /// <param name="limits">How many calls a connection may make.</param>
    /// <param name="maxPayloadBytes">The largest call this router will encode.</param>
    public RpcRouter(
        RpcManifest manifest,
        IRpcTransport transport,
        RpcRole role,
        NetworkOwnership? ownership = null,
        IRpcObservers? observers = null,
        RpcLimits? limits = null,
        int maxPayloadBytes = 1200
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 16);

        this.manifest = manifest;
        this.transport = transport;
        this.ownership = ownership ?? new NetworkOwnership();
        this.observers = observers;
        this.limits = limits ?? new RpcLimits();

        Role = role;
        sendBuffer = new byte[maxPayloadBytes];
    }

    /// <summary>Whether this peer runs server calls.</summary>
    public bool IsServer => Role is RpcRole.Server or RpcRole.Host;

    /// <summary>Whether this peer runs client calls.</summary>
    public bool IsClient => Role is RpcRole.Client or RpcRole.Host;

    /// <summary>Attaches an object's handlers, so calls about it can be dispatched.</summary>
    /// <param name="id">The networked object.</param>
    /// <param name="invoker">Its generated dispatch table.</param>
    public void Register(NetworkId id, IRpcInvoker invoker) {
        ArgumentNullException.ThrowIfNull(invoker);
        invokers[(id.Value, invoker.RpcTypeId)] = invoker;
    }

    /// <summary>Detaches one type's handlers from an object.</summary>
    /// <param name="id">The networked object.</param>
    /// <param name="invoker">Its dispatch table.</param>
    /// <returns>Whether it was attached.</returns>
    public bool Unregister(NetworkId id, IRpcInvoker invoker) {
        ArgumentNullException.ThrowIfNull(invoker);

        return invokers.Remove((id.Value, invoker.RpcTypeId));
    }

    /// <summary>Detaches everything about an object, because it was destroyed.</summary>
    /// <param name="id">The networked object.</param>
    public void Forget(NetworkId id) {
        var mine = new List<(uint, uint)>();

        foreach (var key in invokers.Keys) {
            if (key.Object == id.Value) {
                mine.Add(key);
            }
        }

        foreach (var key in mine) {
            invokers.Remove(key);
        }

        ownership.Forget(id);
    }

    /// <summary>Refills the rate limiters.</summary>
    /// <param name="elapsed">Time since the last call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed" /> is negative.</exception>
    public void Advance(TimeSpan elapsed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (tokens.Count == 0) {
            return;
        }

        var refill = limits.CallsPerSecond * elapsed.TotalSeconds;
        var players = new List<uint>(tokens.Keys);

        foreach (var player in players) {
            tokens[player] = Math.Min(limits.Burst, tokens[player] + refill);
        }
    }

    /// <summary>Starts encoding a call, with its header already written.</summary>
    /// <param name="method">Which call.</param>
    /// <param name="target">What it is about.</param>
    /// <returns>A writer positioned where the arguments go.</returns>
    /// <remarks>
    ///     Generated senders call this, write their arguments, and hand it back to
    ///     <see cref="EndCall" />. The buffer belongs to the router and is reused, so a call is
    ///     encoded and sent before another one starts — which is what a frame-synchronous sender does
    ///     anyway.
    /// </remarks>
    public BitWriter BeginCall(RpcMethod method, NetworkId target) {
        ArgumentNullException.ThrowIfNull(method);

        var writer = new BitWriter(sendBuffer);
        writer.WriteVariable(target.Value);
        writer.WriteVariable((uint)Math.Max(0, method.TypeIndex));
        writer.WriteVariable((uint)Math.Max(0, method.MethodIndex));

        return writer;
    }

    /// <summary>Sends an encoded call to whoever it is for.</summary>
    /// <param name="method">Which call.</param>
    /// <param name="target">What it is about.</param>
    /// <param name="writer">The writer <see cref="BeginCall" /> handed out.</param>
    /// <returns>Whether it went anywhere.</returns>
    public bool EndCall(RpcMethod method, NetworkId target, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(method);

        if (method.TypeIndex < 0 || !writer.TryFinish(out var payload)) {
            RefusedToSendCount++;

            return false;
        }

        if (method.Kind == RpcKind.Server) {
            if (!IsClient) {
                // A server calling its own server RPC is a mistake worth noticing rather than
                // quietly turning into a local method call.
                RefusedToSendCount++;

                return false;
            }

            transport.SendToServer(payload, method.Channel);

            return true;
        }

        if (!IsServer) {
            RefusedToSendCount++;

            return false;
        }

        switch (method.Target) {
            case RpcTarget.All:
                transport.SendToAll(payload, method.Channel);

                return true;

            case RpcTarget.Owner:
                if (!ownership.TryGetOwner(target, out var owner)) {
                    RefusedToSendCount++;

                    return false;
                }

                transport.SendToPlayer(owner, payload, method.Channel);

                return true;

            default:
                return SendToObservers(target, payload, method.Channel);
        }
    }

    /// <summary>Takes a call off the wire, checks it, and runs it.</summary>
    /// <param name="from">
    ///     Who sent it, as the session says — never as the packet says. <see cref="PlayerId.None" />
    ///     when it came from the server.
    /// </param>
    /// <param name="payload">The bytes.</param>
    /// <returns>Whether it ran.</returns>
    public bool Receive(PlayerId from, ReadOnlySpan<byte> payload) {
        var reader = new BitReader(payload);

        if (!reader.TryReadVariable(out var objectId)
            || !reader.TryReadVariable(out var typeIndex)
            || !reader.TryReadVariable(out var methodIndex)
            || !manifest.TryGet(typeIndex, methodIndex, out var method)
            || method is null) {
            RefusedByManifestCount++;

            return false;
        }

        var target = new NetworkId(objectId);

        if (!AcceptsDirection(method, from)) {
            RefusedByDirectionCount++;

            return false;
        }

        if (method.Kind == RpcKind.Server && !TakeToken(from)) {
            RefusedByRateLimitCount++;

            return false;
        }

        if (method.Kind == RpcKind.Server && method.RequireOwnership && !ownership.IsOwnedBy(target, from)) {
            RefusedByOwnershipCount++;

            return false;
        }

        if (!invokers.TryGetValue((target.Value, method.TypeId), out var invoker)) {
            RefusedByUnknownObjectCount++;

            return false;
        }

        var context = new RpcContext(from, target, method);

        if (!invoker.Invoke(methodIndex, in context, ref reader) || reader.BitsRemaining >= 8) {
            // Bits left over means the sender and this build disagree about the signature, which the
            // manifest hash should have caught at the handshake. Refuse rather than guess.
            RefusedByArgumentsCount++;

            return false;
        }

        AcceptedCount++;

        return true;
    }

    bool AcceptsDirection(RpcMethod method, PlayerId from) =>
        method.Kind == RpcKind.Server ? IsServer && from.IsValid : IsClient && !from.IsValid;

    bool TakeToken(PlayerId player) {
        if (!tokens.TryGetValue(player.Value, out var available)) {
            available = limits.Burst;
        }

        if (available < 1) {
            tokens[player.Value] = available;

            return false;
        }

        tokens[player.Value] = available - 1;

        return true;
    }

    bool SendToObservers(NetworkId target, ReadOnlySpan<byte> payload, Channel channel) {
        if (observers is null) {
            // Nobody said who can see it, so everybody can. That matches the default interest
            // resolver, and being wrong in this direction is a bandwidth bill rather than a player
            // who cannot see an explosion.
            transport.SendToAll(payload, channel);

            return true;
        }

        resolved.Clear();
        observers.Resolve(target, resolved);

        foreach (var player in resolved) {
            transport.SendToPlayer(player, payload, channel);
        }

        return resolved.Count != 0;
    }
}
