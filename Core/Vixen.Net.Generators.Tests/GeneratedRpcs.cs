// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Replication;
using Vixen.Net.Rpc;

namespace Vixen.Net.Generators.Tests;

/// <summary>
///     A type declaring remote calls, so the generator runs over it as part of building the tests
///     and the emitted senders and dispatch table are compiled in.
/// </summary>
public sealed partial class GeneratedTurret : IRpcObject {
    /// <summary>What <c>Fire</c> was called with.</summary>
    public List<int> Fired { get; } = [];

    /// <summary>What <c>PlayEffect</c> was called with.</summary>
    public List<(float At, float Intensity)> Effects { get; } = [];

    /// <summary>What <c>Claim</c> was called with.</summary>
    public List<(ulong Shooter, ulong Target, long At)> Claims { get; } = [];

    /// <summary>Who made the last call that arrived.</summary>
    public Sessions.PlayerId LastCaller { get; private set; }

    /// <inheritdoc />
    public NetworkId NetworkId { get; }

    /// <inheritdoc />
    public RpcRouter? RpcRouter { get; }

    /// <summary>Creates a turret.</summary>
    /// <param name="id">The networked object its calls are about.</param>
    /// <param name="router">Where its calls go.</param>
    public GeneratedTurret(NetworkId id, RpcRouter? router) {
        NetworkId = id;
        RpcRouter = router;
    }

    [ServerRpc(RequireOwnership = true, Channel = Channel.Reliable)]
    void Fire(int damage) => Fired.Add(damage);

    [ClientRpc(Target = RpcTarget.Observers, Channel = Channel.Unreliable)]
    void PlayEffect(float at, [Quantize(0f, 1f, 8)] float intensity) => Effects.Add((at, intensity));

    // Sixty-four bit arguments, which the wire had no kind for until #514. The shape is doc 28's hit
    // claim: two identities the size Vixen.Gameplay.PlayerId is, and a signed number beside them.
    [ServerRpc(RequireOwnership = false)]
    void Claim(ulong shooter, ulong target, long at) => Claims.Add((shooter, target, at));

    // The context is not read from the wire — it is what the router knows about the connection the
    // bytes arrived on. A handler that took the caller's id as an ordinary argument would be asking
    // the caller who they are.
    [ServerRpc(RequireOwnership = false)]
    void Salute(in RpcContext context) => LastCaller = context.Sender;
}
