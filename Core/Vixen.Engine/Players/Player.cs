// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Engine.Players;

/// <summary>
///     Possession: the operations that keep <see cref="Possessing" /> and <see cref="PossessedBy" />
///     consistent with each other.
/// </summary>
/// <remarks>
///     <para>
///         Two components describe one relationship, and both of them can be written directly.
///         Nothing stops that, and everything that does it will eventually produce a pawn that
///         believes it is possessed by a controller that has forgotten it — the class of bug that
///         shows up as a player whose input goes nowhere after a respawn. This is the only supported
///         way to change who is driving what, and it is what the tests are written against. The same
///         discipline <see cref="Transforms.Hierarchy" /> imposes on the transform tree, for the same
///         reason.
///     </para>
///     <para>
///         [29](../../../docs/plan/29-players-and-possession.md) is the design.
///     </para>
/// </remarks>
public static class Player {
    /// <summary>Creates a player: a seat with an aim and nothing to drive yet.</summary>
    /// <param name="world">The world.</param>
    /// <param name="slot">Which seat at this machine, from zero.</param>
    /// <param name="owner">Which connection, as a <c>PlayerId</c>. Zero is the local machine.</param>
    /// <returns>The controller entity.</returns>
    /// <remarks>
    ///     The camera channel is taken from the slot, because a split-screen game that gave two
    ///     players two slots and left them sharing channel zero would have them share a camera — and
    ///     it would work perfectly until the moment one of them walked into a trigger volume. A game
    ///     wanting them to share writes the channel afterwards, which is the rarer case and the one
    ///     that should have to be said out loud.
    /// </remarks>
    public static Entity Create(World world, byte slot = 0, uint owner = 0) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Create(
            PlayerController.Default with { Slot = slot, Owner = owner, CameraChannel = slot },
            ControlRotation.Default,
            default(MoveIntent)
        );
    }

    /// <summary>What a controller is driving, or <see cref="Entity.Null" />.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The controller.</param>
    /// <returns>The pawn.</returns>
    public static Entity PawnOf(World world, Entity controller) {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<Possessing>(controller, out var possessing) ? possessing.Pawn : Entity.Null;
    }

    /// <summary>What is driving a pawn, or <see cref="Entity.Null" />.</summary>
    /// <param name="world">The world.</param>
    /// <param name="pawn">The pawn.</param>
    /// <returns>The controller.</returns>
    public static Entity ControllerOf(World world, Entity pawn) {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<PossessedBy>(pawn, out var possessed) ? possessed.Controller : Entity.Null;
    }

    /// <summary>Whether anything is driving a pawn.</summary>
    /// <param name="world">The world.</param>
    /// <param name="pawn">The pawn.</param>
    /// <returns>Whether it is possessed.</returns>
    public static bool IsPossessed(World world, Entity pawn) {
        ArgumentNullException.ThrowIfNull(world);
        return world.IsAlive(pawn) && world.Has<PossessedBy>(pawn);
    }

    /// <summary>Puts a controller in charge of a pawn.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The controller.</param>
    /// <param name="pawn">The pawn, or <see cref="Entity.Null" /> to drive nothing.</param>
    /// <exception cref="ArgumentException">
    ///     The controller is not a controller, the pawn is not alive, or the two are the same entity.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>Both sides are released first, so this steals rather than shares.</b> A pawn that
    ///         was already possessed leaves its old controller driving nothing, and a controller that
    ///         was already driving something lets it go. That is what a game means by "possess": the
    ///         alternative — two controllers each believing they hold one pawn — has no failure that
    ///         is visible at the call site and one that is very visible a frame later.
    ///     </para>
    ///     <para>
    ///         An entity may not possess itself. It is never meant, and the loop it makes is one
    ///         <c>PossessionSystem</c> would follow every frame.
    ///     </para>
    /// </remarks>
    public static void Possess(World world, Entity controller, Entity pawn) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAlive(controller) || !world.Has<PlayerController>(controller)) {
            throw new ArgumentException($"{controller} is not a player controller.", nameof(controller));
        }

        if (pawn.IsNull) {
            Unpossess(world, controller);
            return;
        }

        if (controller == pawn) {
            throw new ArgumentException("A controller cannot possess itself.", nameof(pawn));
        }

        if (!world.IsAlive(pawn)) {
            throw new ArgumentException($"{pawn} is not alive.", nameof(pawn));
        }

        Unpossess(world, controller);
        Release(world, pawn);

        Attach(world, controller, new Possessing { Pawn = pawn });
        Attach(world, pawn, new PossessedBy { Controller = controller });
    }

    /// <summary>Takes a controller off whatever it is driving.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The controller.</param>
    /// <returns>What it had been driving, or <see cref="Entity.Null" />.</returns>
    /// <remarks>
    ///     The controller keeps its aim, its slot and its camera. That is the whole point of it being
    ///     a separate entity, and it is what makes a respawn one call rather than a copy of five
    ///     fields.
    /// </remarks>
    public static Entity Unpossess(World world, Entity controller) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAlive(controller) || !world.TryGet<Possessing>(controller, out var possessing)) {
            return Entity.Null;
        }

        world.Remove<Possessing>(controller);
        var pawn = possessing.Pawn;

        // The pawn may already be gone — a controller outliving its body is the ordinary case, not
        // an error — so this is a conditional removal rather than an assertion that the link held.
        if (world.IsAlive(pawn) && world.Has<PossessedBy>(pawn)) {
            world.Remove<PossessedBy>(pawn);
        }

        return pawn;
    }

    /// <summary>Says which shot shows this player what they are driving.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The controller.</param>
    /// <param name="shot">The <c>VirtualCamera</c> entity, or <see cref="Entity.Null" /> for none.</param>
    /// <remarks>
    ///     <c>PossessionSystem</c> then points that shot at whatever the controller is driving, every
    ///     frame. There is no blend to pass and nothing to undo when the pawn changes: the director
    ///     blends because the shot's target moved, which is [26](../../../docs/plan/26-virtual-cameras.md)'s
    ///     whole argument reused rather than a second camera stack.
    /// </remarks>
    public static void BindCamera(World world, Entity controller, Entity shot) {
        ArgumentNullException.ThrowIfNull(world);

        if (shot.IsNull) {
            if (world.IsAlive(controller) && world.Has<ViewTarget>(controller)) {
                world.Remove<ViewTarget>(controller);
            }

            return;
        }

        Attach(world, controller, new ViewTarget { Shot = shot });
    }

    /// <summary>Releases whatever is driving a pawn, leaving that controller driving nothing.</summary>
    /// <param name="world">The world.</param>
    /// <param name="pawn">The pawn.</param>
    /// <returns>The controller that had been driving it, or <see cref="Entity.Null" />.</returns>
    public static Entity Release(World world, Entity pawn) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAlive(pawn) || !world.TryGet<PossessedBy>(pawn, out var possessed)) {
            return Entity.Null;
        }

        world.Remove<PossessedBy>(pawn);
        var controller = possessed.Controller;

        if (world.IsAlive(controller) && world.Has<Possessing>(controller)) {
            world.Remove<Possessing>(controller);
        }

        return controller;
    }

    static void Attach<T>(World world, Entity entity, in T value) {
        if (world.Has<T>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }
    }
}
