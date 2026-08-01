// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class PlayerInputTests {
    /// <summary>A source that plays back what a test says, so no device is needed to drive a player.</summary>
    /// <remarks>
    ///     That this is eight lines is the point of <see cref="IPlayerInputSource" /> being an
    ///     interface: a planner, a replay and a network stream are the same eight lines.
    /// </remarks>
    sealed class Scripted : IPlayerInputSource {
        public Vector2 Move { get; set; }

        public float Yaw { get; set; }

        public float Pitch { get; set; }

        public MoveButtons Buttons { get; set; }

        public int SampleCount { get; private set; }

        public void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime) {
            SampleCount++;
            rotation.Turn(Yaw, Pitch);
            intent.Move = Move;
            intent.Yaw = rotation.Yaw;
            intent.Pitch = rotation.Pitch;
            intent.Buttons = Buttons;
        }
    }

    [Fact]
    public void ABoundSourceWritesTheControllersIntent() {
        using var world = new World();
        var system = new PlayerInputSystem();
        var controller = Player.Create(world);
        var source = new Scripted { Move = new(0f, 1f), Buttons = MoveButtons.Sprint };

        system.Bind(controller, source);
        system.Sample(world, 1f / 60f);

        Assert.Equal(1f, world.Read<MoveIntent>(controller).Move.Y, 5);
        Assert.True(world.Read<MoveIntent>(controller).IsHeld(MoveButtons.Sprint));
    }

    [Fact]
    public void AimAccumulatesAcrossFrames() {
        using var world = new World();
        var system = new PlayerInputSystem();
        var controller = Player.Create(world);

        system.Bind(controller, new Scripted { Yaw = 0.1f });

        for (var frame = 0; frame < 5; frame++) {
            system.Sample(world, 1f / 60f);
        }

        Assert.Equal(0.5f, world.Read<ControlRotation>(controller).Yaw, 4);
    }

    /// <summary>
    ///     The controller keeps its aim when it stops being asked, and loses what was held. A player
    ///     who was sprinting when a menu opened should not still be sprinting behind it.
    /// </summary>
    [Fact]
    public void ADeafControllerKeepsItsAimAndLosesItsIntent() {
        using var world = new World();
        var system = new PlayerInputSystem();
        var controller = Player.Create(world);
        var source = new Scripted { Move = new(1f, 0f), Yaw = 0.4f, Buttons = MoveButtons.Sprint };

        system.Bind(controller, source);
        system.Sample(world, 1f / 60f);

        world.Get<PlayerController>(controller).AcceptsInput = false;
        system.Sample(world, 1f / 60f);

        Assert.Equal(0.4f, world.Read<ControlRotation>(controller).Yaw, 5);
        Assert.Equal(MoveButtons.None, world.Read<MoveIntent>(controller).Buttons);
        Assert.Equal(Vector2.Zero, world.Read<MoveIntent>(controller).Move);
        Assert.Equal(1, source.SampleCount);
    }

    [Fact]
    public void ADestroyedControllerIsSkippedRatherThanThrowing() {
        using var world = new World();
        var system = new PlayerInputSystem();
        var controller = Player.Create(world);

        system.Bind(controller, new Scripted());
        world.Destroy(controller);

        system.Sample(world, 1f / 60f);

        Assert.Equal(1, system.Count);
    }

    [Fact]
    public void UnbindingStopsTheSampling() {
        using var world = new World();
        var system = new PlayerInputSystem();
        var controller = Player.Create(world);
        var source = new Scripted { Yaw = 0.2f };

        system.Bind(controller, source);
        system.Sample(world, 1f / 60f);

        Assert.True(system.Unbind(controller));
        system.Sample(world, 1f / 60f);

        Assert.Equal(1, source.SampleCount);
    }

    /// <summary>
    ///     Split screen is two controllers, two sources and two camera channels in one world. Neither
    ///     player can see the other's input, which is the property `PlayerController.Slot` exists for.
    /// </summary>
    [Fact]
    public void TwoPlayersInOneWorldDoNotShareInput() {
        using var world = new World();
        var input = new PlayerInputSystem();
        var possession = new PossessionSystem();

        var one = Player.Create(world);
        var two = Player.Create(world, slot: 1);
        var firstPawn = world.Create(LocalTransform.Identity);
        var secondPawn = world.Create(LocalTransform.Identity);

        Player.Possess(world, one, firstPawn);
        Player.Possess(world, two, secondPawn);
        input.Bind(one, new Scripted { Move = new(0f, 1f) });
        input.Bind(two, new Scripted { Move = new(0f, -1f) });

        input.Sample(world, 1f / 60f);
        possession.Apply(world);

        Assert.Equal(1f, world.Read<MoveIntent>(firstPawn).Move.Y, 5);
        Assert.Equal(-1f, world.Read<MoveIntent>(secondPawn).Move.Y, 5);
        Assert.Equal(0, world.Read<PlayerController>(one).CameraChannel);
        Assert.Equal(1, world.Read<PlayerController>(two).CameraChannel);
    }

    [Fact]
    public void PitchClampsAtBothEndsAndYawWraps() {
        var rotation = ControlRotation.Default;

        for (var step = 0; step < 100; step++) {
            rotation.Turn(0.5f, 0.5f);
        }

        Assert.Equal(rotation.MaximumPitch, rotation.Pitch, 5);
        Assert.InRange(rotation.Yaw, -MathUtil.Pi, MathUtil.Pi);

        for (var step = 0; step < 200; step++) {
            rotation.Turn(-0.5f, -0.5f);
        }

        Assert.Equal(rotation.MinimumPitch, rotation.Pitch, 5);
        Assert.InRange(rotation.Yaw, -MathUtil.Pi, MathUtil.Pi);
    }

    /// <summary>
    ///     The clamps agree with <c>PovAim</c>'s, because a first-person camera whose limits
    ///     disagreed with the aim it is fed would let the player aim at something it refuses to show.
    /// </summary>
    [Fact]
    public void TheAimClampsMatchTheCamerasOwn() {
        Assert.Equal(Cameras.PovAim.Default.MinimumPitch, ControlRotation.Default.MinimumPitch, 6);
        Assert.Equal(Cameras.PovAim.Default.MaximumPitch, ControlRotation.Default.MaximumPitch, 6);
    }

    /// <summary>
    ///     <c>ControlRotation.Forward</c> and the <c>PovAim</c> stage build the same direction from
    ///     the same angles. They are two constructions of one convention, and a drift between them
    ///     would be a camera that looks somewhere other than where the player is aiming.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1.2f, 0.4f)]
    [InlineData(-2.7f, -0.9f)]
    public void ForwardAgreesWithTheCamerasConstruction(float yaw, float pitch) {
        var rotation = ControlRotation.Default;
        rotation.Turn(yaw, pitch);

        var clamped = MathUtil.Clamp(pitch, rotation.MinimumPitch, rotation.MaximumPitch);
        var cosPitch = MathF.Cos(clamped);

        var expected = new Vector3(
            -cosPitch * MathF.Sin(rotation.Yaw),
            MathF.Sin(clamped),
            -cosPitch * MathF.Cos(rotation.Yaw)
        );

        var actual = rotation.Forward();

        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }

    /// <summary>
    ///     Movement is in the yaw's frame and never the pitch's: a character walking forward while
    ///     looking at the sky walks along the ground.
    /// </summary>
    [Fact]
    public void IntentIgnoresPitchWhenItIsTurnedIntoADirection() {
        var level = new MoveIntent { Move = new(0f, 1f), Yaw = 0.7f, Pitch = 0f };
        var raised = level with { Pitch = 1.2f };

        var a = level.WorldDirection();
        var b = raised.WorldDirection();

        Assert.Equal(a.X, b.X, 6);
        Assert.Equal(0f, b.Y, 6);
        Assert.Equal(a.Z, b.Z, 6);
    }

    [Fact]
    public void AnIntentThatAsksForNothingPointsNowhere() {
        Assert.Equal(Vector3.Zero, new MoveIntent { Yaw = 1.3f }.WorldDirection());
    }

    [Fact]
    public void ForwardIntentAtZeroYawGoesTowardsNegativeZ() {
        var direction = new MoveIntent { Move = new(0f, 1f) }.WorldDirection();

        // The engine's forward is -Z (Conventions.md), and this is the one place a sign error would
        // be invisible in a test that only checked magnitudes.
        Assert.Equal(0f, direction.X, 6);
        Assert.Equal(-1f, direction.Z, 6);
    }

    /// <summary>
    ///     W walks the way the player is looking, which is the whole of the two conventions meeting.
    /// </summary>
    /// <remarks>
    ///     <c>Vixen.Input</c>'s vector2 composite is a screen vector and reports <c>up</c> as
    ///     <b>negative</b>; <see cref="MoveIntent.Move" />'s Y is forward. Copying one into the other
    ///     walks the player backwards, and every symptom of it — "W and S are swapped", a character
    ///     sliding away from the camera — is a sign rather than anything about movement. Driven from a
    ///     real key through a real composite, because a test that set <c>Move</c> by hand would agree
    ///     with whichever convention it was written under.
    /// </remarks>
    [Fact]
    public void PressingForwardWalksForwardRatherThanBackwards() {
        var actions = InputActions.Load(
            """
            name: Test
            maps:
              - name: Player
                actions:
                  - name: Move
                    type: value
                    controlType: vector2
                    bindings:
                      - composite: vector2
                        parts:
                          - part: up
                            path: <Keyboard>/w
                          - part: down
                            path: <Keyboard>/s
                          - part: left
                            path: <Keyboard>/a
                          - part: right
                            path: <Keyboard>/d
                  - name: Look
                    type: value
                    controlType: vector2
                    bindings:
                      - path: <Mouse>/delta
            """,
            "Test"
        );

        actions.Enable();

        var devices = new InputDeviceSet();
        var source = new ActionPlayerInput(actions["Player"]);
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        devices.SubmitKey(InputKey.W, true);
        actions.Update(devices, 1.0 / 60.0);
        source.Sample(ref rotation, ref intent, 1f / 60f);

        Assert.Equal(1f, intent.Move.Y, 5);
        Assert.Equal(-1f, intent.WorldDirection().Z, 5);

        devices.BeginFrame();
        devices.SubmitKey(InputKey.W, false);
        devices.SubmitKey(InputKey.D, true);
        actions.Update(devices, 2.0 / 60.0);
        source.Sample(ref rotation, ref intent, 1f / 60f);

        // Strafing is untouched: only the axis the two conventions disagree about is flipped.
        Assert.Equal(1f, intent.Move.X, 5);
        Assert.Equal(1f, intent.WorldDirection().X, 5);
    }

    [Fact]
    public void AMissingActionIsNamedRatherThanReadingZeroForever() {
        var actions = InputActions.From(
            new("Test", [new("Player", [new("Move", InputActionType.Value, InputControlType.Vector2)])])
        );

        var thrown = Assert.Throws<ArgumentException>(() => new ActionPlayerInput(actions.Maps[0]));

        Assert.Contains("Look", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Player", thrown.Message, StringComparison.Ordinal);
    }
}
