using Vixen.Core;
using Vixen.Live;
using Vixen.Live.Realms;

namespace VixenMmo1.Realm;

/// <summary>One map, simulated. Everything a listen server does, plus a lifecycle.</summary>
/// <remarks>
///     ⚠ The base type is spelled out because this project's own namespace ends in <c>.Realm</c>,
///     which would otherwise win the name lookup. Renaming either would be worse: the directory
///     should say what the project is, and the base type is called what the document calls it.
/// </remarks>
public sealed class VixenMmo1Realm : Vixen.Live.Realms.Realm {
    protected override void OnRealmInitialise() {
        // Host.Session is the server from docs/plan/16 — replication, RPC and interest are wired
        // here exactly as they would be in a listen server. A realm is not a different kind of
        // server; it is one that can be told what to be and asked to stop.
        //
        //   var replication = new ReplicationServer(registry);
        //   ReplicatedComponents.RegisterAll(registry);      // generated
    }

    protected override void OnRealmUpdate(GameTime time) {
        // The realm's own step. Host.Update has already run: control-plane answers applied,
        // launcher signals read, the map checked, the session polled.
    }

    protected override void OnPlayerAdmitted(RealmPlayer player) {
        // Their ticket has been checked and they are in. `player.Key` is who the database thinks
        // they are — the same on every shard they visit — and `player.Id` is this session's number
        // for them, which means nothing anywhere else.
    }

    protected override void OnPlayerReleased(RealmPlayer player) {
        // They left, or they were moved. Despawn what belongs to them.
    }

    /// <summary>Whether a draining shard may move this player right now.</summary>
    /// <remarks>
    ///     The hook that stops a rollout from ending a raid, and the engine deliberately does not
    ///     guess at it: "in a scripted encounter" is a sentence only a game can finish. Blocked
    ///     escalates to a live-ops alert at the hard deadline rather than to a disconnect — nothing
    ///     is force-disconnected by a drain.
    /// </remarks>
    protected override TransferReadiness ReadinessOf(RealmPlayer player) => TransferReadiness.Ready;

    /// <summary>The cluster key tickets are signed with.</summary>
    /// <remarks>
    ///     ⚠ Read this from whatever the deployment already uses for secrets. Returning null falls
    ///     back to a key derived from the shard's own spec, which is a development convenience and
    ///     not a security mechanism: everything it is derived from travels in plain text on a
    ///     command line. What it buys is that a deployment which forgot to configure a key gets a
    ///     fleet that refuses everybody — which is loud — rather than one that admits anybody.
    /// </remarks>
    protected override TransferTicketSigner? CreateSigner() => null;
}
