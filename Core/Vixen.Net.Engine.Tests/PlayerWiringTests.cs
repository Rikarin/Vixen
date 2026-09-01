// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Net.Engine.Players;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;
using Vixen.Testing;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>
///     The rounding a client must apply to its own intent, as a system rather than as a sentence in
///     a manual.
/// </summary>
public sealed class PlayerInputQuantizeSystemTests : IDisposable {
    readonly World world = new("quantize");

    public void Dispose() => world.Dispose();

    [Fact]
    public void APlayersIntentIsRoundedToWhatTheWireCarries() {
        var controller = Player.Create(world);
        var system = new PlayerInputQuantizeSystem();

        world.Set(controller, new MoveIntent { Move = new(0.123456f, -0.987654f), Yaw = 1.2345678f });
        system.Round(world);

        var intent = world.Read<MoveIntent>(controller);

        Assert.Equal(1, system.RoundedCount);
        Assert.Equal(PlayerMoveInput.Round(new MoveIntent {
            Move = new(0.123456f, -0.987654f),
            Yaw = 1.2345678f
        }).Yaw, intent.Yaw, 6);
    }

    /// <summary>
    ///     Idempotent, because it runs every frame on an intent it rounded last frame — and a rounding
    ///     that crept would be a slow drift in a player's aim.
    /// </summary>
    [Fact]
    public void RoundingEveryFrameDoesNotDrift() {
        var controller = Player.Create(world);
        var system = new PlayerInputQuantizeSystem();

        world.Set(controller, new MoveIntent { Move = new(0.3f, 0.7f), Yaw = -2.1f, Pitch = 0.44f });
        system.Round(world);

        var once = world.Read<MoveIntent>(controller);

        for (var frame = 0; frame < 100; frame++) {
            system.Round(world);
        }

        var later = world.Read<MoveIntent>(controller);

        Assert.Equal(once.Yaw, later.Yaw);
        Assert.Equal(once.Pitch, later.Pitch);
        Assert.Equal(once.Move.X, later.Move.X);
        Assert.Equal(once.Move.Y, later.Move.Y);
    }

    /// <summary>
    ///     The pawn gets the rounded numbers too, because this runs between the input and the
    ///     forwarding. A pawn moving on full precision while the wire carried less is the whole bug.
    /// </summary>
    [Fact]
    public void ThePawnSeesTheRoundedIntent() {
        var controller = Player.Create(world);
        var pawn = world.Create(LocalTransform.Identity);
        var quantize = new PlayerInputQuantizeSystem();
        var possession = new PossessionSystem();

        Player.Possess(world, controller, pawn);
        world.Set(controller, new MoveIntent { Yaw = 1.2345678f });

        quantize.Round(world);
        possession.Apply(world);

        Assert.Equal(world.Read<MoveIntent>(controller).Yaw, world.Read<MoveIntent>(pawn).Yaw);
        Assert.NotEqual(1.2345678f, world.Read<MoveIntent>(pawn).Yaw);
    }

    [Fact]
    public void RoundingAWorldWithNoPlayersDoesNothing() {
        var system = new PlayerInputQuantizeSystem();

        world.Create(LocalTransform.Identity);
        system.Round(world);

        Assert.Equal(0, system.RoundedCount);
    }
}

/// <summary>Hiding a rollback, on the visuals and never on the body.</summary>
public sealed class PredictionSmoothingTests : IDisposable {
    static readonly NetworkId Id = new(1);

    readonly World world = new("smoothing");

    public void Dispose() => world.Dispose();

    (Entity Body, Entity Visual) Rig() {
        var body = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 0f, 10f)));
        var visual = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        world.Add(body, Id);
        Hierarchy.SetParent(world, visual, body);
        world.Add(visual, PredictionSmoothing.Of(body));

        return (body, visual);
    }

    [Fact]
    public void ACorrectionBecomesAnOffsetOnTheVisualsThatDecays() {
        var (_, visual) = Rig();
        var system = new PredictionSmoothingSystem();

        // The body was three metres from where the server said, and the body has already been moved.
        system.Take([new PredictionCorrection { Id = Id, From = new(3f, 0f, 0f), To = Vector3.Zero }]);
        system.Apply(world, TimeSpan.Zero);

        var initial = world.Read<LocalTransform>(visual).Position.X;

        Assert.Equal(3f, initial, 3);

        for (var frame = 0; frame < 60; frame++) {
            system.Apply(world, TimeSpan.FromSeconds(1d / 60d));
        }

        var later = world.Read<LocalTransform>(visual).Position.X;

        Assert.True(later < initial, $"the error did not decay: {initial} → {later}");
        Assert.True(later >= 0f);
    }

    /// <summary>
    ///     ⚠ The body is never written. <c>PhysicsScene</c> adopts a written transform as a teleport,
    ///     so an offset applied to the body would be taken as the truth on the next fixed step and the
    ///     error the smoothing was hiding would become one the simulation had made.
    /// </summary>
    [Fact]
    public void TheBodyIsNeverMoved() {
        var (body, _) = Rig();
        var system = new PredictionSmoothingSystem();
        var before = world.Read<LocalTransform>(body).Position;

        system.Take([new PredictionCorrection { Id = Id, From = new(5f, 0f, 0f), To = Vector3.Zero }]);
        system.Apply(world, TimeSpan.Zero);

        Assert.Equal(before, world.Read<LocalTransform>(body).Position);
    }

    [Fact]
    public void WithNoCorrectionTheVisualsSitAtTheirRest() {
        var (_, visual) = Rig();
        var system = new PredictionSmoothingSystem();

        world.Get<PredictionSmoothing>(visual).Rest = new(0f, 1.2f, 0f);
        system.Apply(world, TimeSpan.FromSeconds(1d / 60d));

        Assert.Equal(new Vector3(0f, 1.2f, 0f), world.Read<LocalTransform>(visual).Position);
    }

    /// <summary>
    ///     A correction bigger than the snap distance is not hidden. A mesh sliding thirty metres
    ///     across a level is worse than a cut, and it hides that the player is somewhere else.
    /// </summary>
    [Fact]
    public void AJumpTooLargeToHideIsNotHidden() {
        var (_, visual) = Rig();
        var system = new PredictionSmoothingSystem { Smoother = new() { SnapDistance = 2f } };

        system.Take([new PredictionCorrection { Id = Id, From = new(30f, 0f, 0f), To = Vector3.Zero }]);
        system.Apply(world, TimeSpan.Zero);

        Assert.Equal(Vector3.Zero, world.Read<LocalTransform>(visual).Position);
        Assert.Equal(1, system.Smoother.SnapCount);
    }

    /// <summary>
    ///     The error is measured in world space and written in the parent's, so a body that turns with
    ///     its aim does not drag its own smoothing round with it.
    /// </summary>
    [Fact]
    public void TheOffsetIsInTheBodysFrameNotTheWorlds() {
        var (body, visual) = Rig();
        var system = new PredictionSmoothingSystem();

        // A quarter turn about +Y takes the body's local +Z to the world's +X, so a world-space error
        // of +X reads as +Z in the body's frame. The conjugate is what performs that world → local
        // step, and getting it the other way round would swing the visuals to the wrong side of a
        // turning character — visible immediately and easy to write backwards.
        world.Get<LocalTransform>(body).Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        system.Take([new PredictionCorrection { Id = Id, From = new(1f, 0f, 0f), To = Vector3.Zero }]);
        system.Apply(world, TimeSpan.Zero);

        var local = world.Read<LocalTransform>(visual).Position;

        Assert.Equal(0f, local.X, 3);
        Assert.Equal(1f, local.Z, 3);

        // And it really is the body's frame rather than the world's: unrotated, the same error is +X.
        world.Get<LocalTransform>(body).Rotation = Quaternion.Identity;
        system.Apply(world, TimeSpan.Zero);

        Assert.Equal(1f, world.Read<LocalTransform>(visual).Position.X, 3);
    }

    [Fact]
    public void ADeadBodyLeavesItsVisualsAlone() {
        var (body, visual) = Rig();
        var system = new PredictionSmoothingSystem();

        world.Get<PredictionSmoothing>(visual).Rest = new(0f, 2f, 0f);
        world.Destroy(body);

        system.Take([new PredictionCorrection { Id = Id, From = new(1f, 0f, 0f), To = Vector3.Zero }]);
        system.Apply(world, TimeSpan.Zero);

        Assert.Equal(0, system.SmoothedCount);
    }

    /// <summary>
    ///     Every frame, on every predicted object, for as long as a correction is decaying — which on
    ///     a connection that is working at all is most of a session. This is what caught
    ///     <c>PredictionSmoother.Advance</c> building a fresh <c>List&lt;uint&gt;</c> per call.
    /// </summary>
    /// <remarks>
    ///     The half life is long on purpose, so the correction stays live for every measured pass.
    ///     Letting it settle would measure the early-out instead — and the settle path's one-off list
    ///     growth is initialisation rather than a steady-state allocation.
    /// </remarks>
    [Fact]
    public void SmoothingAllocatesNothingPerFrameWhileACorrectionDecays() {
        Rig();

        var system = new PredictionSmoothingSystem {
            Smoother = new() { HalfLife = TimeSpan.FromMinutes(1) }
        };

        var elapsed = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

        system.Take([new PredictionCorrection { Id = Id, From = new(0.5f, 0f, 0f), To = Vector3.Zero }]);

        Measured.NothingAllocated(() => system.Apply(world, elapsed), warmUp: 16, passes: 500);
        Assert.Equal(1, system.SmoothedCount);
    }
}
