// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Testing;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     The half of the player design that is a number rather than a behaviour.
/// </summary>
/// <remarks>
///     <para>
///         Phase 2's stress sample runs ten thousand frames with <b>zero</b> gen-0 collections, and
///         every system that runs on every frame of every game has to survive that. The player path
///         is three of them — the input sample, the possession pass and the camera write — and all
///         three walk lists and dictionaries that are easy to allocate in by accident.
///     </para>
///     <para>
///         Measured rather than reasoned about, in the shape <c>CoroutineAllocationTests</c>
///         established. These drive the systems directly rather than through <c>EngineLoop</c>, so
///         what is measured is the player machinery and not a frame's worth of everything else.
///     </para>
/// </remarks>
public sealed class PlayerAllocationTests {
    /// <summary>A source that returns a constant, so the measurement is of the system and not of it.</summary>
    sealed class Constant : IPlayerInputSource {
        public void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime) {
            rotation.Turn(0.01f, 0.005f);
            intent.Move = new(0.5f, 1f);
            intent.Yaw = rotation.Yaw;
            intent.Pitch = rotation.Pitch;
            intent.Buttons = MoveButtons.Sprint;
        }
    }

    [Fact]
    public void SamplingAPlayersInputAllocatesNothing() {
        using var world = new World("input-allocation");
        var system = new PlayerInputSystem();

        system.Bind(Player.Create(world), new Constant());
        system.Bind(Player.Create(world, slot: 1), new Constant());

        Assert.Equal(0, Measured.Bytes(() => system.Sample(world, 1f / 60f), warmUp: 16, passes: 500));
    }

    /// <summary>
    ///     The pass that walks two collections and writes three components, on every player, every
    ///     frame. Its two reusable lists are the whole reason it collects before it applies.
    /// </summary>
    [Fact]
    public void ForwardingIntentAndAimingTheCameraAllocatesNothing() {
        using var world = new World("possession-allocation");
        var system = new PossessionSystem();

        for (byte slot = 0; slot < 2; slot++) {
            var controller = Player.Create(world, slot);
            var pawn = Hierarchy.CreateTransform(world, LocalTransform.Identity);

            world.Add(pawn, default(MoveIntent));

            var shot = world.Create(VirtualCamera.Default, PovAim.Default, default(CameraTargets));

            Player.BindCamera(world, controller, shot);
            Player.Possess(world, controller, pawn);
        }

        // Settled first: the frame a pawn is possessed on attaches components, and a structural change
        // is not the steady state this is about.
        system.Apply(world);

        Assert.Equal(0, Measured.Bytes(() => system.Apply(world), warmUp: 16, passes: 500));
    }

    /// <summary>
    ///     A pawn that dies every frame is not a real game, but a pawn that dies is — and the reap
    ///     path walks a list it must not rebuild.
    /// </summary>
    [Fact]
    public void ReapingNothingAllocatesNothing() {
        using var world = new World("reap-allocation");
        var system = new PossessionSystem();
        var controller = Player.Create(world);

        Player.Possess(world, controller, Hierarchy.CreateTransform(world, LocalTransform.Identity));
        system.Apply(world);

        Assert.Equal(0, Measured.Bytes(() => system.Apply(world), warmUp: 16, passes: 500));
        Assert.Equal(0, system.ReleasedCount);
    }

    /// <summary>
    ///     Turning is what a mouse does every frame of every session, and it is the one place a
    ///     wrapped angle could have been a boxed one.
    /// </summary>
    [Fact]
    public void AimingAllocatesNothing() {
        var rotation = ControlRotation.Default;

        Assert.Equal(
            0,
            Measured.Bytes(
                () => {
                    rotation.Turn(0.01f, 0.002f);
                    _ = rotation.Forward();
                    _ = rotation.YawRotation();
                },
                warmUp: 16,
                passes: 1_000
            )
        );
    }

    [Fact]
    public void TurningAnIntentIntoADirectionAllocatesNothing() {
        var intent = new MoveIntent { Move = new(0.6f, 0.8f), Yaw = 1.1f, Buttons = MoveButtons.Sprint };

        Assert.Equal(
            0,
            Measured.Bytes(
                () => {
                    _ = intent.WorldDirection();
                    _ = intent.IsHeld(MoveButtons.Sprint);
                },
                warmUp: 16,
                passes: 1_000
            )
        );
    }
}
