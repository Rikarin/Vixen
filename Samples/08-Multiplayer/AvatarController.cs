// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;

namespace Vixen.Samples.Multiplayer;

/// <summary>One fighter's remote calls, on whichever machine is holding it.</summary>
/// <remarks>
///     <para>
///         The same type on both sides, and the handlers are one-sided by construction rather than by
///         an <c>if</c>. <see cref="Steer" /> and <see cref="Fire" /> are <see cref="ServerRpcAttribute" />s,
///         so a packet naming them is refused at a client before anything is decoded; <see cref="Hit" />
///         is a <see cref="ClientRpcAttribute" />, so the server refuses to run its own. A client's
///         controller therefore has no <see cref="Arena" /> to act on, and does not need one.
///     </para>
///     <para>
///         Nothing here writes a packet. The generator emits a <c>Rpc</c> accessor beside these
///         handlers with one sender each, so the call site reads <c>fighter.Rpc.Fire()</c> and says
///         out loud that it costs a packet — see <c>docs/plan/16-networking.md</c> for why that is a
///         feature rather than the ceremony it looks like.
///     </para>
/// </remarks>
internal sealed partial class AvatarController : IRpcObject {
    readonly Arena? arena;

    /// <inheritdoc />
    public NetworkId NetworkId { get; }

    /// <inheritdoc />
    public RpcRouter? RpcRouter { get; }

    /// <summary>How many hits this machine has been told about. Client-side.</summary>
    public int HitsSeen { get; private set; }

    /// <summary>How many of those finished somebody off. Client-side.</summary>
    public int KillsSeen { get; private set; }

    /// <summary>Creates a controller.</summary>
    /// <param name="id">The networked object its calls are about.</param>
    /// <param name="router">Where its calls go.</param>
    /// <param name="arena">
    ///     The rules its server-side handlers act on. Null on a client, where those handlers cannot
    ///     be reached: a packet claiming to be one is refused by direction before it is decoded.
    /// </param>
    public AvatarController(NetworkId id, RpcRouter router, Arena? arena = null) {
        NetworkId = id;
        RpcRouter = router;
        this.arena = arena;
    }

    /// <summary>Where the owner wants to go, and which way they are looking.</summary>
    /// <param name="x">Sideways, from -1 to 1.</param>
    /// <param name="z">Forwards, from -1 to 1.</param>
    /// <param name="facing">The yaw, in radians.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Intent, not position.</b> A client that sent a position would be a client that
    ///         decides where it is, and the server would have nothing left to be authoritative about.
    ///         What arrives is a direction the server is free to refuse.
    ///     </para>
    ///     <para>
    ///         Unreliable, and that is the right channel rather than a saving: input supersedes
    ///         itself thirty times a second, so a retransmit of last tick's direction would arrive
    ///         after the direction that replaced it and be worse than the loss.
    ///     </para>
    /// </remarks>
    [ServerRpc(RequireOwnership = true, Channel = Channel.Unreliable)]
    void Steer(
        [Quantize(-1f, 1f, 8)] float x,
        [Quantize(-1f, 1f, 8)] float z,
        [Quantize(-3.15f, 3.15f, 10)] float facing
    ) =>
        arena?.Steer(NetworkId, x, z, facing);

    /// <summary>The owner pulled the trigger.</summary>
    /// <param name="context">Who called, which the router fills in from the connection.</param>
    /// <remarks>
    ///     No arguments: where the shot goes is decided by where the server thinks the shooter is
    ///     looking, which it already knows from <see cref="Steer" />. A client that sent a direction
    ///     could send any direction. Reliable, because a shot that is lost is a shot the player
    ///     believes they took.
    /// </remarks>
    [ServerRpc(RequireOwnership = true, Channel = Channel.Reliable)]
    void Fire(in RpcContext context) => arena?.Fire(NetworkId, context.Sender);

    /// <summary>Somebody was hit, for whatever a client wants to do about it.</summary>
    /// <param name="shooter">The shooter's networked id.</param>
    /// <param name="fatal">Whether it finished them off.</param>
    /// <remarks>
    ///     An effect rather than a fact: the damage itself arrives as replicated <see cref="Vitals" />,
    ///     which is what makes it correct after a loss. This is the spark, and it goes unreliably
    ///     because a spark that arrives late is worse than one that never arrives.
    /// </remarks>
    [ClientRpc(Target = RpcTarget.All, Channel = Channel.Unreliable)]
    void Hit(uint shooter, bool fatal) {
        HitsSeen++;

        if (fatal) {
            KillsSeen++;
        }

        LastShooter = new(shooter);
    }

    /// <summary>Who shot this fighter last, as this machine was told. Client-side.</summary>
    public NetworkId LastShooter { get; private set; }
}
