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

public sealed class PlayerCameraTests {
    /// <summary>A world, both pipelines and a clock, so a test reads as a sequence of frames.</summary>
    sealed class Rig : IDisposable {
        long frame;
        double total;

        public World World { get; } = new("Cameras");

        public PossessionSystem Possession { get; } = new();

        public VirtualCameraSystem Cameras { get; } = new();

        public CameraDirectorSystem Directors { get; } = new();

        public void Tick(float seconds = 1f / 60f) {
            frame++;
            total += seconds;
            var elapsed = TimeSpan.FromSeconds(seconds);
            var time = new GameTime(TimeSpan.FromSeconds(total), elapsed, elapsed, frame, 1f);

            Possession.Apply(World);
            Cameras.Evaluate(World, time);
            Directors.Direct(World, time);
        }

        public Entity Pawn(Vector3 position) => Hierarchy.CreateTransform(World, LocalTransform.At(position));

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void AFirstPersonRigSitsAtTheEyeAndLooksWhereThePlayerAims() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(new(2f, 0f, 5f));

        var camera = PlayerCameras.FirstPerson(rig.World, controller, eyeHeight: 1.7f);
        Player.Possess(rig.World, controller, pawn);

        rig.World.Get<ControlRotation>(controller).Turn(MathUtil.PiOverTwo, 0f);
        rig.Tick();

        var shot = rig.World.Read<CameraShot>(camera.Shot);

        // Exactly at the eye: HardLockBody has nothing to damp, because there is no error.
        Assert.Equal(2f, shot.Position.X, 4);
        Assert.Equal(1.7f, shot.Position.Y, 4);
        Assert.Equal(5f, shot.Position.Z, 4);

        // A quarter turn from facing -Z is facing -X.
        var forward = Quaternion.Transform(Vector3.Forward, shot.Rotation);

        Assert.Equal(-1f, forward.X, 3);
        Assert.Equal(0f, forward.Y, 3);
    }

    [Fact]
    public void AFirstPersonCameraFollowsThePawnWithNoLag() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(Vector3.Zero);

        var camera = PlayerCameras.FirstPerson(rig.World, controller);
        Player.Possess(rig.World, controller, pawn);
        rig.Tick();

        rig.World.Get<LocalTransform>(pawn).Position = new(0f, 0f, -10f);
        rig.Tick();

        // One frame, no smoothing. A first-person camera that lagged the body would swim.
        Assert.Equal(-10f, rig.World.Read<CameraShot>(camera.Shot).Position.Z, 4);
    }

    [Fact]
    public void AThirdPersonRigSitsBehindTheAimAndLooksAtTheShoulder() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(Vector3.Zero);

        var camera = PlayerCameras.ThirdPerson(rig.World, controller, distance: 4f, shoulderHeight: 1.4f, damping: 0f);
        Player.Possess(rig.World, controller, pawn);
        rig.Tick();

        var shot = rig.World.Read<CameraShot>(camera.Shot);

        // Facing -Z at rest, so the camera is four metres behind at +Z, level with the shoulder.
        Assert.Equal(0f, shot.Position.X, 3);
        Assert.Equal(1.4f, shot.Position.Y, 3);
        Assert.Equal(4f, shot.Position.Z, 3);

        var forward = Quaternion.Transform(Vector3.Forward, shot.Rotation);

        Assert.Equal(-1f, forward.Z, 3);
    }

    [Fact]
    public void TurningSwingsTheThirdPersonCameraRound() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(Vector3.Zero);

        var camera = PlayerCameras.ThirdPerson(rig.World, controller, distance: 4f, damping: 0f);
        Player.Possess(rig.World, controller, pawn);

        rig.World.Get<ControlRotation>(controller).Turn(MathUtil.PiOverTwo, 0f);
        rig.Tick();

        // A quarter turn left puts the player facing -X, so the camera goes to +X.
        Assert.Equal(4f, rig.World.Read<CameraShot>(camera.Shot).Position.X, 3);
        Assert.Equal(0f, rig.World.Read<CameraShot>(camera.Shot).Position.Z, 3);
    }

    /// <summary>
    ///     The sign that is easy to get wrong and impossible to miss in play: aiming up drops the
    ///     camera and looks up past the character, rather than raising it and looking down.
    /// </summary>
    [Fact]
    public void AimingUpDropsTheThirdPersonCameraRatherThanRaisingIt() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(Vector3.Zero);

        var camera = PlayerCameras.ThirdPerson(rig.World, controller, distance: 4f, shoulderHeight: 1.4f, damping: 0f);
        Player.Possess(rig.World, controller, pawn);
        rig.Tick();

        var level = rig.World.Read<CameraShot>(camera.Shot).Position.Y;

        rig.World.Get<ControlRotation>(controller).Turn(0f, MathUtil.DegreesToRadians(45f));
        rig.Tick();

        var raised = rig.World.Read<CameraShot>(camera.Shot).Position.Y;

        Assert.True(raised < level, $"aiming up moved the camera from {level} to {raised}");
        Assert.Equal(-MathUtil.DegreesToRadians(45f), rig.World.Read<OrbitBody>(camera.Shot).Pitch, 4);
    }

    [Fact]
    public void TheDirectorDrivesTheRealCameraToTheShot() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var pawn = rig.Pawn(new(0f, 0f, -3f));

        var camera = PlayerCameras.FirstPerson(rig.World, controller, eyeHeight: 1.6f);
        Player.Possess(rig.World, controller, pawn);

        for (var frame = 0; frame < 5; frame++) {
            rig.Tick();
        }

        var eye = rig.World.Read<LocalTransform>(camera.Eye);

        Assert.Equal(1.6f, eye.Position.Y, 3);
        Assert.Equal(-3f, eye.Position.Z, 3);
    }

    /// <summary>
    ///     Split screen simulates. Two players get two directors, two shots and two cameras, and
    ///     neither can take the other's — which is what the channel is for and what one set of shots
    ///     with a rule about who may see which would fail at the first trigger volume.
    /// </summary>
    [Fact]
    public void TwoPlayersGetTwoCamerasOnTwoChannels() {
        using var rig = new Rig();
        var one = Player.Create(rig.World);
        var two = Player.Create(rig.World, slot: 1);
        var firstPawn = rig.Pawn(new(-5f, 0f, 0f));
        var secondPawn = rig.Pawn(new(5f, 0f, 0f));

        var first = PlayerCameras.FirstPerson(rig.World, one, eyeHeight: 1.6f);
        var second = PlayerCameras.FirstPerson(rig.World, two, eyeHeight: 1.6f);

        Player.Possess(rig.World, one, firstPawn);
        Player.Possess(rig.World, two, secondPawn);
        rig.Tick();

        Assert.Equal(0, rig.World.Read<VirtualCamera>(first.Shot).Channel);
        Assert.Equal(1, rig.World.Read<VirtualCamera>(second.Shot).Channel);
        Assert.Equal(0, rig.World.Read<CameraDirector>(first.Eye).Channel);
        Assert.Equal(1, rig.World.Read<CameraDirector>(second.Eye).Channel);

        // Each director drove its own player's camera and neither saw the other's shot.
        Assert.Equal(-5f, rig.World.Read<LocalTransform>(first.Eye).Position.X, 3);
        Assert.Equal(5f, rig.World.Read<LocalTransform>(second.Eye).Position.X, 3);
    }

    /// <summary>
    ///     Camera.Order is the channel, so seat zero is the one CameraExtractionSystem picks — the
    ///     honest answer while a RenderView has no viewport rectangle to give the second player.
    /// </summary>
    [Fact]
    public void SeatZerosCameraRendersFirst() {
        using var rig = new Rig();
        var one = Player.Create(rig.World);
        var two = Player.Create(rig.World, slot: 1);

        var first = PlayerCameras.ThirdPerson(rig.World, one);
        var second = PlayerCameras.ThirdPerson(rig.World, two);

        Assert.Equal(0, rig.World.Read<Camera>(first.Eye).Order);
        Assert.Equal(1, rig.World.Read<Camera>(second.Eye).Order);
    }

    /// <summary>A rig has one body and one aim, or the last stage to run silently wins.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryRigIsWellFormed(bool firstPerson) {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);

        var camera = firstPerson
            ? PlayerCameras.FirstPerson(rig.World, controller)
            : PlayerCameras.ThirdPerson(rig.World, controller);

        Assert.True(VirtualCameras.Validate(rig.World, camera.Shot));
    }

    [Fact]
    public void SomethingThatIsNotAPlayerCannotHaveARig() {
        using var rig = new Rig();
        var stranger = rig.Pawn(Vector3.Zero);

        Assert.Throws<ArgumentException>(() => PlayerCameras.FirstPerson(rig.World, stranger));
        Assert.Throws<ArgumentException>(() => PlayerCameras.ThirdPerson(rig.World, stranger));
    }

    /// <summary>
    ///     The rig survives the pawn, because the controller does. A player who dies keeps their
    ///     camera and their aim, and respawning re-points the shot with no camera code involved.
    /// </summary>
    [Fact]
    public void TheCameraFollowsThePlayerThroughAPawnSwap() {
        using var rig = new Rig();
        var controller = Player.Create(rig.World);
        var first = rig.Pawn(new(0f, 0f, 0f));

        var camera = PlayerCameras.FirstPerson(rig.World, controller, eyeHeight: 1.6f);
        Player.Possess(rig.World, controller, first);
        rig.Tick();

        rig.World.Destroy(first);
        rig.Tick();

        var second = rig.Pawn(new(20f, 0f, 0f));
        Player.Possess(rig.World, controller, second);
        rig.Tick();

        Assert.Equal(20f, rig.World.Read<CameraShot>(camera.Shot).Position.X, 3);
    }
}
