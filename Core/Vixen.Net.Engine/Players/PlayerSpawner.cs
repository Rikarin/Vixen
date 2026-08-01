// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Sessions;

namespace Vixen.Net.Engine.Players;

/// <summary>Gives a connection a body, and takes it away again.</summary>
/// <remarks>
///     <para>
///         <b>Unreal's <c>AGameModeBase</c>, minus the god object.</b> That class owns spawning, class
///         substitution, login, match state and the HUD; what is genuinely load-bearing in it is
///         "when a player joins, make a controller, make a pawn, and put one in charge of the other",
///         which is this. Everything else it owns already lives somewhere better here — the spawn is
///         <see cref="NetworkSpawner" />'s, the ownership is <c>NetworkOwnership</c>'s, the camera is
///         <c>PlayerCameras</c>'s, and match state is the game's.
///     </para>
///     <para>
///         <b>Server side only.</b> A client never decides who has a body; it is told, and
///         <see cref="LocalPlayerSystem" /> is the half that notices. Splitting them means a
///         dedicated-server build links this and a client build links that, rather than both linking
///         one class with half its methods throwing.
///     </para>
///     <para>
///         [29](../../../docs/plan/29-players-and-possession.md) § Authority is the table this
///         implements one row of.
///     </para>
/// </remarks>
/// <param name="spawner">What builds the pawn and puts it on the wire.</param>
public sealed class PlayerSpawner(NetworkSpawner spawner) {
    readonly NetworkSpawner spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    readonly Dictionary<uint, Entity> controllers = [];

    /// <summary>How many players have a controller.</summary>
    public int Count => controllers.Count;

    /// <summary>The players that have one.</summary>
    public IEnumerable<PlayerId> Players => controllers.Keys.Select(id => new PlayerId(id));

    /// <summary>The controller a player was given, or <see cref="Entity.Null" />.</summary>
    /// <param name="player">The connection.</param>
    /// <returns>Their controller.</returns>
    public Entity ControllerOf(PlayerId player) =>
        controllers.TryGetValue(player.Value, out var controller) ? controller : Entity.Null;

    /// <summary>Gives a player a controller and a body, and possesses one with the other.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">The connection.</param>
    /// <param name="pawnAddress">The address of the pawn prefab.</param>
    /// <param name="at">Where the pawn goes, or null for the prefab's own transform.</param>
    /// <param name="scene">Which scene the pawn belongs to.</param>
    /// <returns>The controller.</returns>
    /// <exception cref="ArgumentException">
    ///     The player is not a real connection, or already has a controller.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>The pawn is spawned owned.</b> That one argument is what makes the client's copy an
    ///         autonomous proxy rather than a simulated one — it is what
    ///         <c>PredictedOwnershipSystem</c> reads to decide what to predict, and what
    ///         <c>[ServerRpc(RequireOwnership = true)]</c> checks. Unreal reaches the same place by
    ///         making the player controller the <c>NetConnection</c> owner and letting ownership flow
    ///         down; here it is said once, at the spawn.
    ///     </para>
    ///     <para>
    ///         A player joining twice is an error rather than a second body. Respawning is
    ///         <see cref="Respawn" />, which keeps the controller — and therefore the aim, the slot
    ///         and the camera — exactly as a death should.
    ///     </para>
    /// </remarks>
    public Entity Join(
        World world,
        PlayerId player,
        string pawnAddress,
        LocalTransform? at = null,
        SceneHandle scene = default
    ) {
        ArgumentNullException.ThrowIfNull(world);

        if (!player.IsValid) {
            throw new ArgumentException("A player needs a real connection id.", nameof(player));
        }

        if (controllers.ContainsKey(player.Value)) {
            throw new ArgumentException($"{player} already has a controller.", nameof(player));
        }

        var controller = Player.Create(world, owner: player.Value);
        controllers[player.Value] = controller;

        var pawn = spawner.Spawn(world, pawnAddress, at, scene, player);

        // Marked here as well as on the prefab, so a game whose pawn prefab predates this still gets
        // a client that can find its body. Adding a tag an entity already has is a no-op.
        if (!world.Has<PlayerPawn>(pawn)) {
            world.Add(pawn, default(PlayerPawn));
        }

        Player.Possess(world, controller, pawn);
        return controller;
    }

    /// <summary>Gives a player a new body, keeping the controller they already had.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">The connection.</param>
    /// <param name="pawnAddress">The address of the pawn prefab.</param>
    /// <param name="at">Where the new pawn goes.</param>
    /// <param name="scene">Which scene it belongs to.</param>
    /// <returns>The new pawn.</returns>
    /// <exception cref="ArgumentException">The player has no controller.</exception>
    /// <remarks>
    ///     The old body is despawned if it is still alive, which is the case for a vehicle swap and
    ///     not for a death — a dead pawn is usually already gone, and <c>PossessionSystem</c> has
    ///     already cleared the edge. Either way the controller keeps its aim.
    /// </remarks>
    public Entity Respawn(
        World world,
        PlayerId player,
        string pawnAddress,
        LocalTransform? at = null,
        SceneHandle scene = default
    ) {
        ArgumentNullException.ThrowIfNull(world);

        if (!controllers.TryGetValue(player.Value, out var controller)) {
            throw new ArgumentException($"{player} has no controller to respawn.", nameof(player));
        }

        Despawn(world, controller);

        var pawn = spawner.Spawn(world, pawnAddress, at, scene, player);

        if (!world.Has<PlayerPawn>(pawn)) {
            world.Add(pawn, default(PlayerPawn));
        }

        Player.Possess(world, controller, pawn);
        return pawn;
    }

    /// <summary>Takes a player's body and controller away.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">The connection.</param>
    /// <returns>Whether they had one.</returns>
    public bool Leave(World world, PlayerId player) {
        ArgumentNullException.ThrowIfNull(world);

        if (!controllers.Remove(player.Value, out var controller)) {
            return false;
        }

        Despawn(world, controller);

        if (world.IsAlive(controller)) {
            world.Destroy(controller);
        }

        return true;
    }

    /// <summary>Forgets every player, for a server that is shutting a match down.</summary>
    public void Clear() => controllers.Clear();

    void Despawn(World world, Entity controller) {
        var pawn = Player.Unpossess(world, controller);

        if (!pawn.IsNull && world.IsAlive(pawn)) {
            spawner.Despawn(world, pawn);
        }
    }
}
