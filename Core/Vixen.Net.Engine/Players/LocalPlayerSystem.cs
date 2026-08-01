// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Players;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Engine.Players;

/// <summary>Notices which of the bodies this client owns is the one it drives.</summary>
/// <remarks>
///     <para>
///         <b>The client's half of <see cref="PlayerSpawner" />.</b> A client is told about a spawn
///         through <c>NetworkSpawn</c>, which carries the owner; a <see cref="PlayerPawn" /> tag rides
///         on the prefab, so it is present the moment <c>NetworkSpawnSystem</c> builds the instance.
///         An entity carrying both, owned by this connection and possessed by nothing, is this
///         player's body — and that inference costs no wire bytes and no second message.
///     </para>
///     <para>
///         <b>Why an inference rather than an RPC saying "you are pawn 47".</b> A message can arrive
///         before the spawn it names, after the pawn has been destroyed, or twice; every one of those
///         is a case somebody has to write. A query over state that is already replicated has none of
///         them, because it is re-evaluated every frame and is right whenever it runs.
///     </para>
///     <para>
///         It does not create the controller. A client's controller exists from the moment it has a
///         <see cref="PlayerId" /> — before any pawn, and still there after one dies — which is the
///         whole point of the controller being separate.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class LocalPlayerSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription pawns = new QueryDescription()
        .WithAll<PlayerPawn, NetworkSpawn, NetworkInstance>()
        .WithNone<PossessedBy>();

    readonly List<Entity> adopted = [];

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PlayerPawn>()
        .Read<NetworkSpawn>()
        .Read<NetworkInstance>()
        .Write<PossessedBy>()
        .Write<Possessing>()
        .Build();

    /// <summary>Which connection this machine is.</summary>
    /// <remarks>
    ///     <c>NetworkSession.LocalPlayer</c>'s id, set once the session has accepted. Left at
    ///     <see cref="PlayerId.None" /> nothing is adopted, which is the right answer for a client
    ///     that has not finished connecting rather than an error to report.
    /// </remarks>
    public PlayerId Local { get; set; }

    /// <summary>The controller this machine's player drives with.</summary>
    /// <remarks>
    ///     The game's, made with <c>Player.Create</c> and usually given a camera before any pawn
    ///     exists — so the first frame after a spawn arrives is already looking at the right place.
    /// </remarks>
    public Entity Controller { get; set; } = Entity.Null;

    /// <summary>How many pawns this has taken charge of.</summary>
    public long AdoptedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Adopt(context.World);
        return dependency;
    }

    /// <summary>Possesses any owned, unpossessed player pawn with the local controller.</summary>
    /// <param name="world">The client's world.</param>
    /// <returns>How many were taken this pass, which is almost always zero.</returns>
    /// <remarks>
    ///     Public so a test or a tool can settle a client without standing up a runner — the same
    ///     reason <c>PossessionSystem.Apply</c> is.
    /// </remarks>
    public int Adopt(World world) {
        ArgumentNullException.ThrowIfNull(world);

        if (!Local.IsValid || Controller.IsNull || !world.IsAlive(Controller)) {
            return 0;
        }

        adopted.Clear();

        foreach (var chunk in world.Chunks(pawns)) {
            var spawns = chunk.ReadValues<NetworkSpawn>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (spawns[index].Owner == Local.Value) {
                    adopted.Add(entities[index]);
                }
            }
        }

        // Collected first: possessing is structural on both entities, and the pawn's archetype is one
        // the query above is walking.
        foreach (var pawn in adopted) {
            Player.Possess(world, Controller, pawn);
            AdoptedCount++;
        }

        return adopted.Count;
    }
}
