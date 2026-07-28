// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Engine;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The remote-call router, taking calls from a client that is not to be trusted.</summary>
/// <remarks>
///     <para>
///         <b>An RPC is the one thing a client can make a server do work for.</b> Everything else a
///         server does on a client's behalf, it does because it decided to; a remote call is the
///         client deciding. The router's own remarks list six checks before anything runs, and this
///         is what tests that a packet cannot get past them — in particular that a pair of indices
///         cannot name a method outside the manifest, and that arguments which do not decode leave
///         the handler unrun rather than half-run.
///     </para>
///     <para>
///         <b>The handler is a real one and it records what it was told.</b> A fuzz target whose
///         invoker returns false immediately would prove that the router refuses everything, which
///         is not the claim. This one decodes its arguments the way a generated invoker does and
///         counts the calls that reached it, so an input that talks its way through all six checks
///         is visible in the signature rather than indistinguishable from one that did not.
///     </para>
/// </remarks>
public sealed class RpcRouterTarget : IFuzzTarget {
    static readonly RpcMethod[] Methods = Build();

    readonly RpcManifest manifest = new();
    readonly RpcRouter router;
    readonly CountingInvoker invoker = new();
    readonly PlayerId caller = new(1);

    /// <summary>Creates the target with one object registered and its caller owning it.</summary>
    public RpcRouterTarget() {
        manifest.Register(Methods);

        router = new(
            manifest,
            new DroppingTransport(),
            RpcRole.Server,
            // A limit high enough that the rate limiter is not the thing every case hits. It is
            // tested elsewhere; here it would mask everything behind it.
            limits: new RpcLimits { CallsPerSecond = 1_000_000, Burst = 1_000_000 }
        );

        router.Ownership.SetOwner(new(1), caller);
        router.Register(new(1), invoker);
        router.Register(new(2), invoker);
    }

    /// <inheritdoc />
    public string Name => "rpc";

    /// <inheritdoc />
    public string What => "RpcRouter.Receive: a call from a client, through all six checks";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     A refusal allocates nothing at all — every check is a lookup and a counter. An accepted
    ///     call allocates whatever its arguments are, which for this handler is a capped string. The
    ///     constant covers the token bucket's first entry.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 2048 + (16L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // One well-formed call per method, over both a registered object and an unregistered one.
        for (var method = 0u; method < (uint)Methods.Length; method++) {
            foreach (var target in (uint[])[1, 2, 3]) {
                corpus.Add(Call(target, 0, method, arguments: true));
                corpus.Add(Call(target, 0, method, arguments: false));
            }
        }

        // Indices well past the end of the manifest, which is the check the whole closed-set design
        // exists for.
        corpus.Add(Call(1, 99, 0, arguments: true));
        corpus.Add(Call(1, 0, 99, arguments: true));
        corpus.Add(Call(uint.MaxValue, uint.MaxValue, uint.MaxValue, arguments: true));
    }

    /// <inheritdoc />
    public void Maintain() =>
        // The bucket is drained by accepted calls and refilled by time, and a run does not pass any.
        router.Advance(TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var ran = router.Receive(caller, input);

        return (ran ? 1_000_003L : 0)
            + (router.AcceptedCount * 7919L)
            + (router.RefusedByManifestCount * 131L)
            + (router.RefusedByDirectionCount * 127L)
            + (router.RefusedByUnknownObjectCount * 113L)
            + (router.RefusedByOwnershipCount * 109L)
            + (router.RefusedByArgumentsCount * 107L)
            + (router.RefusedByRateLimitCount * 103L)
            + invoker.Signature;
    }

    static byte[] Call(uint target, uint typeIndex, uint methodIndex, bool arguments) {
        var buffer = new byte[256];
        var writer = new BitWriter(buffer);

        writer.WriteVariable(target);
        writer.WriteVariable(typeIndex);
        writer.WriteVariable(methodIndex);

        if (arguments) {
            writer.WriteVariable(7);
            writer.WriteSingle(1.25f);
            writer.WriteBool(value: true);
        }

        return writer.TryFinish(out var packet) ? packet.ToArray() : [];
    }

    static RpcMethod[] Build() {
        var methods = new[] {
            new RpcMethod("Fuzzed", "Move(uint,float,bool)", RpcKind.Server, requireOwnership: true, Channel.Unreliable, RpcTarget.All),
            new RpcMethod("Fuzzed", "Say(string)", RpcKind.Server, requireOwnership: false, Channel.Reliable, RpcTarget.All),
            new RpcMethod("Fuzzed", "Nothing()", RpcKind.Server, requireOwnership: false, Channel.Reliable, RpcTarget.All)
        };

        // A manifest is ordered by method id, and registering an unordered table is refused. The
        // ids are hashes of the names, so the order is whatever the hashes say rather than whatever
        // reads well here.
        Array.Sort(methods, (left, right) => left.MethodId.CompareTo(right.MethodId));

        return methods;
    }

    /// <summary>A handler that decodes its arguments and remembers that it ran.</summary>
    sealed class CountingInvoker : IRpcInvoker {
        long calls;
        long argumentBytes;

        public uint RpcTypeId { get; } = RpcMethod.Hash("Fuzzed");

        public long Signature => (calls * 37L) + argumentBytes;

        public bool Invoke(uint methodIndex, in RpcContext context, ref BitReader reader) {
            switch (methodIndex) {
                case 0:
                    if (!reader.TryReadVariable(out var count)
                        || !reader.TryReadSingle(out _)
                        || !reader.TryReadBool(out _)) {
                        return false;
                    }

                    argumentBytes += count & 0xFF;

                    break;

                case 1:
                    // A string argument is the one a generated invoker allocates for, and the cap
                    // is the generator's — stated at the call site, never taken from the packet.
                    if (!TryReadString(ref reader, maxBytes: 64, out var text)) {
                        return false;
                    }

                    argumentBytes += text.Length;

                    break;

                case 2:
                    break;

                default:
                    return false;
            }

            calls++;

            return true;
        }

        static bool TryReadString(ref BitReader reader, int maxBytes, out string value) {
            value = string.Empty;

            if (!reader.TryReadVariable(out var length) || length > (uint)maxBytes) {
                return false;
            }

            if (!reader.TryReadBytes((int)length, out var bytes)) {
                return false;
            }

            value = bytes.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes);

            return true;
        }
    }

    /// <summary>Where a router's outgoing calls go when nobody is listening.</summary>
    sealed class DroppingTransport : IRpcTransport {
        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) { }

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) { }

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) { }
    }
}

/// <summary>A synchronised list, taking operations off the wire.</summary>
/// <remarks>
///     <para>
///         The one collection whose contents are edited by a remote peer rather than replaced by
///         one, which makes it the one place an <i>index</i> arrives from the network. Everything
///         else reads values; this reads a position in a list it is about to mutate, and an
///         unchecked one is an <c>IndexOutOfRangeException</c> on a receive path — which is precisely
///         the failure the whole never-throws design exists to prevent.
///     </para>
///     <para>
///         Both directions are fuzzed by the same input: an incremental batch of operations and a
///         whole list are the same wire shape apart from a leading count, so a mutation that
///         reinterprets one as the other is reachable and is exactly the confusion worth finding.
///     </para>
/// </remarks>
public sealed class SyncListTarget : IFuzzTarget {
    readonly SyncList<int> list = new();
    readonly byte[] buffer = new byte[512];

    /// <inheritdoc />
    public string Name => "synclist";

    /// <inheritdoc />
    public string What => "SyncList.Apply: an index off the wire, into a list it then mutates";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     Applying grows the list, and a batch of appends is legitimately an item per few bytes.
    ///     The bound is the ratio; <see cref="Maintain" /> is what stops the list itself becoming
    ///     the measurement.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 2048 + (64L * inputLength);

    /// <summary>How many items the list holds.</summary>
    public long Held => list.Count;

    /// <summary>
    ///     How many it may hold. Above where <see cref="Maintain" /> clears it, so this asserts that
    ///     the clear works rather than bounding a list whose length is the game's business.
    /// </summary>
    public long HeldCap => 8192;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var source = new SyncList<int>();
        source.Add(1);
        source.Add(2);
        source.Add(3);
        corpus.Add(Pending(source));

        source.ClearPending();
        source.Insert(1, 42);
        source.RemoveAt(0);
        source.Replace(0, -1);
        corpus.Add(Pending(source));

        source.ClearPending();
        corpus.Add(Whole(source));

        source.Clear();
        corpus.Add(Pending(source));
    }

    /// <inheritdoc />
    public void Maintain() {
        if (list.Count > 4096) {
            list.Clear();
            list.ClearPending();
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var reader = new BitReader(input);
        var applied = list.Apply(ref reader);

        return (applied ? 1_000_003L : 0) + (list.Count * 31L) + reader.BitsRead;
    }

    byte[] Pending(SyncList<int> source) {
        var writer = new BitWriter(buffer);

        return source.WritePending(ref writer) && writer.TryFinish(out var bits) ? bits.ToArray() : [];
    }

    byte[] Whole(SyncList<int> source) {
        var writer = new BitWriter(buffer);

        return source.WriteWhole(ref writer) && writer.TryFinish(out var bits) ? bits.ToArray() : [];
    }
}
