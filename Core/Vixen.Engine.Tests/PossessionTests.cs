// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class PossessionTests {
    [Fact]
    public void ANewControllerDrivesNothing() {
        using var world = new World();
        var controller = Player.Create(world);

        Assert.True(Player.PawnOf(world, controller).IsNull);
        Assert.False(world.Has<Possessing>(controller));
        Assert.True(world.Has<PlayerController>(controller));
        Assert.True(world.Has<ControlRotation>(controller));
        Assert.True(world.Has<MoveIntent>(controller));
    }

    [Fact]
    public void ADefaultControllerAcceptsInput() {
        using var world = new World();
        var controller = Player.Create(world);

        // The trap PlayerController.Default exists to avoid: a zeroed struct is deaf, so a
        // controller made with `default` would be silently broken rather than visibly so.
        Assert.True(world.Read<PlayerController>(controller).AcceptsInput);
    }

    [Fact]
    public void PossessionLinksBothDirections() {
        using var world = new World();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);

        Assert.Equal(pawn, Player.PawnOf(world, controller));
        Assert.Equal(controller, Player.ControllerOf(world, pawn));
        Assert.True(Player.IsPossessed(world, pawn));
    }

    [Fact]
    public void UnpossessingClearsBothSides() {
        using var world = new World();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);

        Assert.Equal(pawn, Player.Unpossess(world, controller));
        Assert.False(world.Has<Possessing>(controller));
        Assert.False(world.Has<PossessedBy>(pawn));
    }

    /// <summary>
    ///     Possessing an already-driven pawn steals it. Two controllers each believing they hold one
    ///     pawn has no failure visible at the call site and a very visible one a frame later.
    /// </summary>
    [Fact]
    public void PossessingATakenPawnStealsItRatherThanSharingIt() {
        using var world = new World();
        var first = Player.Create(world);
        var second = Player.Create(world, slot: 1);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, first, pawn);
        Player.Possess(world, second, pawn);

        Assert.Equal(second, Player.ControllerOf(world, pawn));
        Assert.True(Player.PawnOf(world, first).IsNull);
        Assert.Equal(pawn, Player.PawnOf(world, second));
    }

    [Fact]
    public void PossessingASecondPawnReleasesTheFirst() {
        using var world = new World();
        var controller = Player.Create(world);
        var first = world.Create(LocalTransform.Identity);
        var second = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, first);
        Player.Possess(world, controller, second);

        Assert.False(world.Has<PossessedBy>(first));
        Assert.Equal(controller, Player.ControllerOf(world, second));
    }

    [Fact]
    public void PossessingNullIsUnpossessing() {
        using var world = new World();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);
        Player.Possess(world, controller, Entity.Null);

        Assert.True(Player.PawnOf(world, controller).IsNull);
        Assert.False(world.Has<PossessedBy>(pawn));
    }

    [Fact]
    public void AControllerCannotPossessItself() {
        using var world = new World();
        var controller = Player.Create(world);

        Assert.Throws<ArgumentException>(() => Player.Possess(world, controller, controller));
    }

    [Fact]
    public void SomethingThatIsNotAControllerCannotPossess() {
        using var world = new World();
        var stranger = world.Create(LocalTransform.Identity);
        var pawn = world.Create(LocalTransform.Identity);

        Assert.Throws<ArgumentException>(() => Player.Possess(world, stranger, pawn));
    }

    [Fact]
    public void ReleaseTakesTheControllerOffFromThePawnsSide() {
        using var world = new World();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);

        Assert.Equal(controller, Player.Release(world, pawn));
        Assert.False(world.Has<Possessing>(controller));
        Assert.False(world.Has<PossessedBy>(pawn));
    }

    /// <summary>
    ///     The whole reason the controller is a separate entity: a player who dies keeps their aim,
    ///     their seat and their camera channel, and respawning is one call rather than a copy of
    ///     five fields.
    /// </summary>
    [Fact]
    public void TheControllerOutlivesItsPawn() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world, slot: 2);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);
        world.Get<ControlRotation>(controller).Turn(1.2f, 0.3f);

        world.Destroy(pawn);
        system.Apply(world);

        Assert.True(world.IsAlive(controller));
        Assert.False(world.Has<Possessing>(controller));
        Assert.Equal(1, system.ReleasedCount);
        Assert.Equal(2, world.Read<PlayerController>(controller).Slot);
        Assert.Equal(1.2f, world.Read<ControlRotation>(controller).Yaw, 5);
        Assert.Equal(0.3f, world.Read<ControlRotation>(controller).Pitch, 5);

        var respawned = world.Create(LocalTransform.Identity);
        Player.Possess(world, controller, respawned);

        Assert.Equal(respawned, Player.PawnOf(world, controller));
    }

    [Fact]
    public void ADestroyedControllerLeavesNoPawnClaimingIt() {
        using var world = new World();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);
        Player.Unpossess(world, controller);
        world.Destroy(controller);

        Assert.False(Player.IsPossessed(world, pawn));
    }

    /// <summary>
    ///     The invariant every operation has to keep, checked over sequences rather than cases:
    ///     an edge is either absent on both sides or present and agreeing on both. The same shape
    ///     <see cref="HierarchyTests" /> uses for the transform tree.
    /// </summary>
    [Fact]
    public void RandomisedSequencesNeverLeaveAHalfLinkedPair() {
        using var world = new World();
        var controllers = new List<Entity>();
        var pawns = new List<Entity>();
        var system = new PossessionSystem();

        for (var index = 0; index < 4; index++) {
            controllers.Add(Player.Create(world, (byte)index));
        }

        for (var index = 0; index < 6; index++) {
            pawns.Add(world.Create(LocalTransform.Identity));
        }

        // Deterministic rather than seeded from the clock: a possession bug that only reproduces on
        // one machine's run is a bug nobody can bisect.
        var random = new Random(20260801);

        for (var step = 0; step < 400; step++) {
            var controller = controllers[random.Next(controllers.Count)];
            var pawn = pawns[random.Next(pawns.Count)];

            switch (random.Next(5)) {
                case 0:
                    if (world.IsAlive(pawn)) {
                        Player.Possess(world, controller, pawn);
                    }

                    break;
                case 1:
                    Player.Unpossess(world, controller);
                    break;
                case 2:
                    Player.Release(world, pawn);
                    break;
                case 3:
                    if (world.IsAlive(pawn)) {
                        world.Destroy(pawn);
                        pawns[pawns.IndexOf(pawn)] = world.Create(LocalTransform.Identity);
                    }

                    break;
                default:
                    system.Apply(world);
                    break;
            }

            AssertConsistent(world, controllers, pawns);
        }
    }

    [Fact]
    public void PossessionRetargetsTheBoundShot() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world);
        var first = world.Create(LocalTransform.Identity);
        var second = world.Create(LocalTransform.Identity);
        var shot = world.Create(VirtualCamera.Default);

        Player.BindCamera(world, controller, shot);
        Player.Possess(world, controller, first);
        system.Apply(world);

        Assert.Equal(first, world.Read<CameraTargets>(shot).Follow);
        Assert.Equal(first, world.Read<CameraTargets>(shot).LookAt);

        Player.Possess(world, controller, second);
        system.Apply(world);

        // No SetViewTarget, no blend curve: the director blends because the answer changed.
        Assert.Equal(second, world.Read<CameraTargets>(shot).Follow);
    }

    [Fact]
    public void UnbindingTheCameraLeavesTheShotAlone() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);
        var other = world.Create(LocalTransform.Identity);
        var shot = world.Create(VirtualCamera.Default);

        Player.BindCamera(world, controller, shot);
        Player.Possess(world, controller, pawn);
        system.Apply(world);

        Player.BindCamera(world, controller, Entity.Null);
        world.Set(shot, CameraTargets.Both(other));
        system.Apply(world);

        Assert.False(world.Has<ViewTarget>(controller));
        Assert.Equal(other, world.Read<CameraTargets>(shot).Follow);
    }

    /// <summary>A shot aiming by POV is a first-person camera, and the player's aim is what it is for.</summary>
    [Fact]
    public void APovShotTakesThePlayersAim() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);
        var shot = world.Create(VirtualCamera.Default, PovAim.Default);

        Player.BindCamera(world, controller, shot);
        Player.Possess(world, controller, pawn);
        world.Get<ControlRotation>(controller).Turn(0.75f, -0.25f);
        system.Apply(world);

        Assert.Equal(0.75f, world.Read<PovAim>(shot).Yaw, 5);
        Assert.Equal(-0.25f, world.Read<PovAim>(shot).Pitch, 5);

        // The shot keeps its own clamps: a scripted moment may show less than the player can aim at,
        // and that is an effect rather than a disagreement.
        Assert.Equal(PovAim.Default.MaximumPitch, world.Read<PovAim>(shot).MaximumPitch, 5);
    }

    [Fact]
    public void IntentReachesThePawnOnTheFrameItIsPossessed() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);
        world.Set(controller, new MoveIntent { Move = new(0f, 1f), Yaw = 0.5f, Buttons = MoveButtons.Jump });
        system.Apply(world);

        Assert.True(world.Has<MoveIntent>(pawn));
        Assert.Equal(0.5f, world.Read<MoveIntent>(pawn).Yaw, 5);
        Assert.True(world.Read<MoveIntent>(pawn).IsHeld(MoveButtons.Jump));
    }

    [Fact]
    public void AnUnpossessedPawnKeepsItsOwnIntent() {
        using var world = new World();
        var system = new PossessionSystem();
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, controller, pawn);
        world.Set(controller, new MoveIntent { Move = new(1f, 0f) });
        system.Apply(world);
        Player.Unpossess(world, controller);

        world.Set(pawn, new MoveIntent { Move = new(0f, -1f) });
        world.Set(controller, new MoveIntent { Move = new(1f, 1f) });
        system.Apply(world);

        // Nothing possesses it, so nothing visits it — which is how a pawn driven by something other
        // than a controller stays driven by it.
        Assert.Equal(-1f, world.Read<MoveIntent>(pawn).Move.Y, 5);
    }

    static void AssertConsistent(World world, List<Entity> controllers, List<Entity> pawns) {
        foreach (var controller in controllers) {
            if (!world.TryGet<Possessing>(controller, out var possessing)) {
                continue;
            }

            // A dangling edge is legal — a controller outliving its pawn is the ordinary case, and
            // PossessionSystem is what clears it. What is never legal is a live pawn that names a
            // different controller than the one naming it.
            if (world.IsAlive(possessing.Pawn)) {
                Assert.Equal(controller, Player.ControllerOf(world, possessing.Pawn));
            }
        }

        foreach (var pawn in pawns) {
            if (!world.IsAlive(pawn) || !world.TryGet<PossessedBy>(pawn, out var possessed)) {
                continue;
            }

            Assert.True(world.IsAlive(possessed.Controller));
            Assert.Equal(pawn, Player.PawnOf(world, possessed.Controller));
        }
    }
}
