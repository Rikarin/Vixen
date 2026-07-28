// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Diagnostics;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rules;
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
    readonly NetworkRulesRegistry rules;
    readonly IRpcObservers? observers;
    readonly RpcLimits limits;
    readonly byte[] sendBuffer;
    readonly Dictionary<(uint Object, uint Type), IRpcInvoker> invokers = [];
    readonly Dictionary<uint, double> tokens = [];
    readonly List<PlayerId> resolved = [];
    readonly PendingCalls pending = new();

    /// <summary>What this peer is.</summary>
    public RpcRole Role { get; set; }

    /// <summary>Who owns what.</summary>
    public NetworkOwnership Ownership => ownership;

    /// <summary>Who is allowed to do what.</summary>
    public NetworkRulesRegistry Rules => rules;

    /// <summary>How many objects have handlers registered.</summary>
    public int RegisteredCount => invokers.Count;

    /// <summary>Where the bandwidth went, or null to not ask.</summary>
    /// <remarks>The same ledger the replication server writes into, so one report covers both.</remarks>
    public BandwidthLedger? Ledger { get; set; }

    /// <summary>Calls that ran.</summary>
    public long AcceptedCount { get; private set; }

    /// <summary>Calls refused because the indices name nothing in the manifest.</summary>
    public long RefusedByManifestCount { get; private set; }

    /// <summary>Calls refused because they were going the wrong way for this peer.</summary>
    public long RefusedByDirectionCount { get; private set; }

    /// <summary>Calls refused because the object they are about is not registered here.</summary>
    public long RefusedByUnknownObjectCount { get; private set; }

    /// <summary>
    ///     Calls refused because the caller was not allowed to make them — by the object's rules, or
    ///     by the call's own <c>RequireOwnership</c>, whichever was stricter.
    /// </summary>
    public long RefusedByOwnershipCount { get; private set; }

    /// <summary>Calls refused because the caller is making too many.</summary>
    public long RefusedByRateLimitCount { get; private set; }

    /// <summary>Calls refused because the arguments did not decode, or left bits behind.</summary>
    public long RefusedByArgumentsCount { get; private set; }

    /// <summary>Calls this peer declined to send, because it is not the side that may send them.</summary>
    public long RefusedToSendCount { get; private set; }

    /// <summary>How long an awaited call waits before giving up, unless told otherwise.</summary>
    /// <remarks>
    ///     Five seconds. Long enough that a bad connection and a slow handler both finish inside it,
    ///     short enough that a player is not left staring at a spinner wondering whether the game
    ///     has stopped. It is a ceiling on being wrong rather than a latency budget.
    /// </remarks>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many awaited calls are outstanding.</summary>
    /// <remarks>
    ///     Should be small and should come back to zero. A number that climbs is a peer that is not
    ///     replying and a caller that keeps asking, which the timeout bounds but does not fix.
    /// </remarks>
    public int PendingCallCount => pending.Count;

    /// <summary>Awaited calls that were answered.</summary>
    public long AnsweredCount { get; private set; }

    /// <summary>Awaited calls that nobody answered in time.</summary>
    public long TimedOutCount { get; private set; }

    /// <summary>Replies that arrived addressed to no outstanding call.</summary>
    /// <remarks>
    ///     Not an error on its own — an answer that arrives after its timeout looks exactly like
    ///     this, and so does a duplicate over an unreliable channel. Worth counting because a lot of
    ///     them means the timeout is too short for the connection.
    /// </remarks>
    public long UnmatchedReplyCount { get; private set; }

    /// <summary>Creates a router.</summary>
    /// <param name="manifest">Every call this build knows about.</param>
    /// <param name="transport">Where encoded calls go.</param>
    /// <param name="role">What this peer is.</param>
    /// <param name="ownership">Who owns what. A fresh one, if null.</param>
    /// <param name="observers">Who can see what, for <see cref="RpcTarget.Observers" />.</param>
    /// <param name="rules">Who is allowed to do what. Server-authoritative, if null.</param>
    /// <param name="limits">How many calls a connection may make.</param>
    /// <param name="maxPayloadBytes">The largest call this router will encode.</param>
    public RpcRouter(
        RpcManifest manifest,
        IRpcTransport transport,
        RpcRole role,
        NetworkOwnership? ownership = null,
        IRpcObservers? observers = null,
        RpcLimits? limits = null,
        NetworkRulesRegistry? rules = null,
        int maxPayloadBytes = 1200
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 16);

        this.manifest = manifest;
        this.transport = transport;
        this.ownership = ownership ?? new NetworkOwnership();
        this.rules = rules ?? new NetworkRulesRegistry(this.ownership);
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

    /// <summary>Hands an object to somebody else, if the asker is allowed to.</summary>
    /// <param name="id">The object.</param>
    /// <param name="requester">
    ///     Who is asking. <see cref="PlayerId.None" /> is the server, which is always allowed.
    /// </param>
    /// <param name="newOwner">Who is to have it.</param>
    /// <returns>Whether it changed hands.</returns>
    /// <remarks>
    ///     The enforcement point for <c>NetworkRules.ChangeOwner</c>. Setting an owner through
    ///     <see cref="Ownership" /> directly is the server doing it and is not checked — the check is
    ///     for requests that came from a client, and a client cannot reach that method.
    /// </remarks>
    public bool TryTransferOwnership(NetworkId id, PlayerId requester, PlayerId newOwner) {
        if (!rules.MayChangeOwner(id, requester)) {
            RefusedByRulesCount++;

            return false;
        }

        return ownership.SetOwner(id, newOwner);
    }

    /// <summary>Requests refused because the rules did not allow them.</summary>
    public long RefusedByRulesCount { get; private set; }

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

        TimedOutCount += pending.Advance(elapsed);

        if (tokens.Count == 0) {
            return;
        }

        var refill = limits.CallsPerSecond * elapsed.TotalSeconds;
        var players = new List<uint>(tokens.Keys);

        foreach (var player in players) {
            tokens[player] = Math.Min(limits.Burst, tokens[player] + refill);
        }
    }

    /// <summary>Asks a question and waits for the answer.</summary>
    /// <typeparam name="T">What the answer is.</typeparam>
    /// <param name="method">Which call. Must be one declared with <c>expectsReply</c>.</param>
    /// <param name="target">What it is about.</param>
    /// <param name="arguments">Its arguments, or null for a call that takes none.</param>
    /// <param name="result">How to read the answer.</param>
    /// <param name="timeout">How long to wait, or null for <see cref="DefaultTimeout" />.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method" /> or <paramref name="result" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="method" /> was not declared awaitable.</exception>
    /// <remarks>
    ///     <para>
    ///         The failure is an exception rather than a nullable, deliberately: a call that did not
    ///         answer is not a call that answered "nothing", and the two are different often enough
    ///         that conflating them is how a timeout gets treated as a "no". <c>RpcFailedException</c>
    ///         says which of the four ways it went wrong.
    ///     </para>
    ///     <para>
    ///         Sent reliably whatever the method's channel says — see <c>RpcMethod.ExpectsReply</c>.
    ///     </para>
    ///     <para>
    ///         <b>A <c>Task&lt;T&gt;</c> rather than the <c>ValueTask&lt;T&gt;</c> doc 16 asked for,
    ///         and the deviation is deliberate.</b> A <c>ValueTask</c> earns its keep when a result
    ///         is often already available, because it avoids allocating a task for the synchronous
    ///         case. This result is <i>never</i> already available — it is a network round trip — so
    ///         the completion source is allocated either way and the <c>ValueTask</c> would be a
    ///         wrapper around it that buys nothing. What it would cost is real: a
    ///         <c>ValueTask</c> may be consumed only once, so storing several and awaiting them
    ///         together — asking three questions at once, which is an obvious thing to want — becomes
    ///         a hazard the compiler warns about. Paying one small allocation per round trip to
    ///         avoid that is not a close decision.
    ///     </para>
    /// </remarks>
    public Task<T> CallAsync<T>(
        RpcMethod method,
        NetworkId target,
        RpcArguments? arguments,
        RpcResult<T> result,
        TimeSpan? timeout = null
    ) {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(result);

        if (!method.ExpectsReply) {
            throw new ArgumentException(
                $"'{method}' was not declared awaitable, so nothing will reply to it and this would "
                    + "wait for the timeout. Declare it with expectsReply.",
                nameof(method)
            );
        }

        if (method.Kind == RpcKind.Server ? !IsClient : !IsServer) {
            RefusedToSendCount++;

            return Task.FromException<T>(new RpcFailedException(RpcFailure.NotSent, method.ToString()));
        }

        // The peer a reply is expected from. A server call is answered by the server, which is
        // nobody in particular; a client call is answered by its owner.
        var peer = method.Kind == RpcKind.Server
            ? PlayerId.None
            : ownership.TryGetOwner(target, out var owner) ? owner : PlayerId.None;

        var correlation = pending.Add(method, peer, result, timeout ?? DefaultTimeout, out Task<T> task);
        var writer = new BitWriter(sendBuffer);

        writer.WriteVariable(target.Value);
        writer.WriteVariable((uint)Math.Max(0, method.TypeIndex));
        writer.WriteVariable((uint)Math.Max(0, method.MethodIndex));
        writer.WriteVariable(correlation);
        arguments?.Invoke(ref writer);

        if (!writer.TryFinish(out var payload)) {
            RefusedToSendCount++;

            return Task.FromException<T>(new RpcFailedException(RpcFailure.NotSent, method.ToString()));
        }

        if (method.Kind == RpcKind.Server) {
            transport.SendToServer(payload, Channel.Reliable);
        } else if (peer.IsValid) {
            transport.SendToPlayer(peer, payload, Channel.Reliable);
        } else {
            transport.SendToAll(payload, Channel.Reliable);
        }

        return task;
    }

    /// <summary>Answers a call that was waiting for one.</summary>
    /// <param name="context">The call being answered.</param>
    /// <param name="result">The answer.</param>
    /// <returns>Whether it was sent.</returns>
    /// <remarks>
    ///     Does nothing for a context with no correlation id, so a handler shared between an
    ///     awaitable call and a fire-and-forget one can reply unconditionally.
    /// </remarks>
    public bool Reply(in RpcContext context, RpcArguments? result) {
        if (!context.ExpectsReply) {
            return false;
        }

        var writer = new BitWriter(sendBuffer);
        writer.WriteVariable(context.CorrelationId);
        result?.Invoke(ref writer);

        if (!writer.TryFinish(out var payload)) {
            return false;
        }

        // Back the way it came. The sender is what the session said, which is the only thing that
        // decides where an answer goes — a reply routed by anything the packet carried would be a
        // client choosing who hears the answer to its own question.
        if (context.Sender.IsValid) {
            transport.SendToPlayer(context.Sender, payload, Channel.Reliable);
        } else {
            transport.SendToServer(payload, Channel.Reliable);
        }

        return true;
    }

    /// <summary>Takes a reply off the wire and completes whatever was waiting for it.</summary>
    /// <param name="from">Who sent it, as the session says.</param>
    /// <param name="payload">The bytes.</param>
    /// <returns>Whether it completed an outstanding call.</returns>
    public bool ReceiveReply(PlayerId from, ReadOnlySpan<byte> payload) {
        var reader = new BitReader(payload);

        if (!reader.TryReadVariable(out var correlation) || correlation == 0) {
            UnmatchedReplyCount++;

            return false;
        }

        if (!pending.Complete(correlation, ref reader)) {
            // Either nothing was waiting — a late answer, or a duplicate — or the answer did not
            // decode, which Complete has already failed the call for.
            UnmatchedReplyCount++;

            return false;
        }

        AnsweredCount++;

        return true;
    }

    /// <summary>Fails everything waiting on a peer that has gone.</summary>
    /// <param name="player">Who left.</param>
    /// <returns>How many calls were waiting on them.</returns>
    /// <remarks>
    ///     Call it from the session's <c>PlayerLeft</c>. Without it a caller waits out the full
    ///     timeout for an answer from somebody who is demonstrably not going to send one.
    /// </remarks>
    public int CancelPending(PlayerId player) => pending.Cancel(player);

    /// <summary>Fails every outstanding call, for a session that is stopping.</summary>
    public void CancelAllPending() => pending.Clear();

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

        // Counted where the size is known and before the branch on who it goes to, so a call that
        // fans out to forty observers is attributed once for what it cost to encode rather than
        // forty times. The transport's own framing is the transport's to account for.
        Ledger?.RecordCall(PlayerId.None, $"{method.DeclaringType}.{method.Signature}", writer.BitsWritten);

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

        if (method.Kind == RpcKind.Server && !rules.MayCallServerRpc(target, from, method.RequireOwnership)) {
            RefusedByOwnershipCount++;

            return false;
        }

        if (!invokers.TryGetValue((target.Value, method.TypeId), out var invoker)) {
            RefusedByUnknownObjectCount++;

            return false;
        }

        var correlation = 0u;

        if (method.ExpectsReply && !reader.TryReadVariable(out correlation)) {
            RefusedByArgumentsCount++;

            return false;
        }

        var context = new RpcContext(from, target, method, correlation);

        if (!invoker.Invoke(methodIndex, in context, ref reader) || reader.BitsRemaining >= 8) {
            // Bits left over means the sender and this build disagree about the signature, which the
            // manifest hash should have caught at the handshake. Refuse rather than guess.
            RefusedByArgumentsCount++;

            return false;
        }

        AcceptedCount++;
        Ledger?.RecordCall(from, $"{method!.DeclaringType}.{method.Signature}", payload.Length * 8);

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
