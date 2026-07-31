// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class VirtualCameraTests {
    /// <summary>A world, the pipeline and a clock, so a test reads as a sequence of frames.</summary>
    sealed class Rig : IDisposable {
        long frame;
        double total;

        public World World { get; } = new("Cameras");

        public VirtualCameraSystem System { get; } = new();

        /// <summary>Runs one frame of the pipeline.</summary>
        /// <param name="seconds">How long the frame took.</param>
        public void Tick(float seconds = 1f / 60f) {
            frame++;
            total += seconds;
            var elapsed = TimeSpan.FromSeconds(seconds);

            System.Evaluate(World, new(TimeSpan.FromSeconds(total), elapsed, elapsed, frame, 1f));
        }

        public Entity Place(Vector3 position) => Hierarchy.CreateTransform(World, LocalTransform.At(position));

        public Entity Place(Vector3 position, Quaternion rotation) =>
            Hierarchy.CreateTransform(World, LocalTransform.Identity with {
                Position = position,
                Rotation = rotation
            });

        public Entity Shot(Entity target, Vector3 at = default) =>
            VirtualCameras.Create(
                World,
                VirtualCamera.Default,
                CameraTargets.Both(target),
                LocalTransform.At(at)
            );

        public CameraShot Read(Entity shot) => World.Read<CameraShot>(shot);

        public void Dispose() => World.Dispose();
    }

    static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.NearEqual(expected, actual, tolerance), $"expected {expected}, got {actual}");

    // ── The shape of the pipeline ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheEngineAttachesTheStateAShotNeeds() {
        using var rig = new Rig();
        var entity = rig.World.Create(VirtualCamera.Default);

        rig.Tick();

        Assert.True(rig.World.Has<CameraShot>(entity));
        Assert.True(rig.World.Has<CameraTargets>(entity));
    }

    [Fact]
    public void AShotWithNoBodySitsWhereItsEntityDoes() {
        using var rig = new Rig();
        var shot = rig.Shot(Entity.Null, new(1f, 2f, 3f));

        rig.Tick();
        AssertNear(new(1f, 2f, 3f), rig.Read(shot).Position);

        rig.World.Get<LocalTransform>(shot).Position = new(4f, 5f, 6f);
        rig.Tick();
        AssertNear(new(4f, 5f, 6f), rig.Read(shot).Position);
    }

    /// <summary>
    ///     The reason the pipeline resolves targets by walking the parent chain rather than reading
    ///     <c>WorldTransform</c>: it runs in <c>LateUpdate</c>, and nothing has resolved that column
    ///     since the previous frame.
    /// </summary>
    [Fact]
    public void AShotFollowsAParentedTargetInTheFrameItMoves() {
        using var rig = new Rig();
        var parent = rig.Place(new(10f, 0f, 0f));
        var child = rig.Place(new(0f, 1f, 0f));
        Hierarchy.SetParent(rig.World, child, parent);

        var shot = rig.Shot(child);
        rig.World.Add(shot, new HardLockBody());

        rig.Tick();
        AssertNear(new(10f, 1f, 0f), rig.Read(shot).Position);

        // Moved this frame, and never seen by a transform pass.
        rig.World.Get<LocalTransform>(parent).Position = new(-5f, 0f, 0f);
        rig.Tick();
        AssertNear(new(-5f, 1f, 0f), rig.Read(shot).Position);
    }

    // ── Bodies ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFollowBodyStaysBehindItsTargetAsItTurns() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);
        rig.World.Add(shot, FollowBody.Behind(5f, 2f, damping: 0f));

        rig.Tick();
        AssertNear(new(0f, 2f, 5f), rig.Read(shot).Position);

        // A quarter turn about +Y takes the target's facing from −Z to −X, so "behind" becomes +X.
        rig.World.Get<LocalTransform>(target).Rotation =
            Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        rig.Tick();
        AssertNear(new(5f, 2f, 0f), rig.Read(shot).Position, 1e-3f);
    }

    [Fact]
    public void AWorldBoundFollowBodyIgnoresTheTargetsRotation() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);

        rig.World.Add(
            shot,
            new FollowBody { Offset = new(0f, 2f, 5f), Binding = CameraBinding.World, Damping = Vector3.Zero }
        );

        rig.Tick();
        rig.World.Get<LocalTransform>(target).Rotation =
            Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        rig.Tick();
        AssertNear(new(0f, 2f, 5f), rig.Read(shot).Position);
    }

    [Fact]
    public void AFollowBodyArrivesLateByItsDampingTime() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);

        rig.World.Add(
            shot,
            new FollowBody {
                Offset = new(0f, 0f, 10f),
                Binding = CameraBinding.World,
                Damping = new(0.5f, 0.5f, 0.5f)
            }
        );

        // The first evaluation snaps: there is no previous state to damp from.
        rig.Tick();
        AssertNear(new(0f, 0f, 10f), rig.Read(shot).Position);

        rig.World.Get<LocalTransform>(target).Position = new(100f, 0f, 0f);
        rig.Tick(0.5f);

        // One damping time later, 99 % of the way there and no further.
        Assert.Equal(99f, rig.Read(shot).Position.X, 2);
    }

    [Fact]
    public void AnOrbitBodyPutsTheCameraWhereItsAnglesSay() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);
        rig.World.Add(shot, OrbitBody.At(10f, damping: 0f));

        // Heading zero is behind the target, which is +Z.
        rig.Tick();
        AssertNear(new(0f, 0f, 10f), rig.Read(shot).Position);

        rig.World.Get<OrbitBody>(shot).Heading = MathUtil.PiOverTwo;
        rig.Tick();
        AssertNear(new(10f, 0f, 0f), rig.Read(shot).Position, 1e-3f);

        // A positive pitch rides above the pivot, looking down on it.
        rig.World.Get<OrbitBody>(shot).Heading = 0f;
        rig.World.Get<OrbitBody>(shot).Pitch = MathUtil.Pi / 6f;
        rig.Tick();

        var position = rig.Read(shot).Position;
        Assert.Equal(5f, position.Y, 3);
        Assert.Equal(MathF.Sqrt(75f), position.Z, 3);
    }

    [Fact]
    public void AHardLockBodyIsExactlyOnItsTarget() {
        using var rig = new Rig();
        var target = rig.Place(new(3f, 4f, 5f), Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo));
        var shot = rig.Shot(target);
        rig.World.Add(shot, new HardLockBody { Offset = new(0f, 0f, -1f), InTargetSpace = true });

        rig.Tick();

        // The target faces −X after a quarter turn, so its local −Z is the world's −X.
        AssertNear(new(2f, 4f, 5f), rig.Read(shot).Position, 1e-3f);
    }

    [Fact]
    public void AFramingBodyPutsTheTargetWhereItBelongs() {
        using var rig = new Rig();
        var target = rig.Place(new(3f, 0f, -10f));
        var shot = rig.Shot(target);
        rig.World.Add(shot, FramingBody.At(10f, damping: 0f) with { DeadZone = Vector2.Zero });

        rig.Tick();

        // Centred means directly in front, and framed means ten units away.
        AssertNear(new(3f, 0f, 0f), rig.Read(shot).Position, 1e-3f);
    }

    [Fact]
    public void AFramingBodyDoesNothingInsideItsDeadZone() {
        using var rig = new Rig();
        var target = rig.Place(new(0f, 0f, -10f));
        var shot = rig.Shot(target);
        rig.World.Add(shot, FramingBody.At(10f, damping: 0f));

        rig.Tick();
        var settled = rig.Read(shot).Position;

        // A twentieth of the frame's half-width, well inside a tenth-screen dead zone.
        rig.World.Get<LocalTransform>(target).Position = new(0.1f, 0f, -10f);
        rig.Tick();

        AssertNear(settled, rig.Read(shot).Position);
    }

    // ── Aims ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AHardLookAimPointsAtItsTarget() {
        using var rig = new Rig();
        var target = rig.Place(new(0f, 0f, -10f));
        var shot = rig.Shot(target, new(10f, 0f, 0f));
        rig.World.Add(shot, new HardLookAim());

        rig.Tick();

        var forward = Quaternion.Transform(Vector3.Forward, rig.Read(shot).Rotation);
        AssertNear(Vector3.Normalize(new(-10f, 0f, -10f)), forward, 1e-3f);
    }

    [Fact]
    public void AComposerLeavesTheFrameAloneInsideItsDeadZone() {
        using var rig = new Rig();
        var target = rig.Place(new(0f, 0f, -10f));
        var shot = rig.Shot(target);
        rig.World.Add(shot, ComposerAim.Centred(damping: 0f));

        rig.Tick();
        var settled = rig.Read(shot).Rotation;

        rig.World.Get<LocalTransform>(target).Position = new(0.3f, 0f, -10f);
        rig.Tick();

        Assert.True(Quaternion.SameRotation(settled, rig.Read(shot).Rotation, 1e-5f));
    }

    /// <summary>
    ///     With no damping the correction is exact: the subject ends on the dead zone's edge, not
    ///     past it and not short of it. Getting this right is what the difference-of-arctangents in
    ///     <c>CameraFraming.TurnToEdge</c> is for — the naive form lands the subject a good way
    ///     inside the zone whenever it entered from near the frame's edge.
    /// </summary>
    [Fact]
    public void AComposerBringsTheSubjectBackToTheEdgeOfItsDeadZone() {
        using var rig = new Rig();
        var target = rig.Place(new(0f, 0f, -10f));
        var shot = rig.Shot(target);
        rig.World.Add(shot, ComposerAim.Centred(damping: 0f) with { DeadZone = new(0.2f, 0.2f) });

        rig.Tick();

        rig.World.Get<LocalTransform>(target).Position = new(6f, 0f, -10f);
        rig.Tick();

        var state = rig.Read(shot);
        var lens = CameraLens.Default;

        Assert.True(
            CameraFraming.Project(
                new(6f, 0f, -10f),
                state.Position,
                state.Rotation,
                in lens,
                rig.System.AspectRatio,
                out var screen,
                out _
            )
        );

        Assert.Equal(0.2f, screen.X, 3);
    }

    [Fact]
    public void APovAimClampsItsPitch() {
        using var rig = new Rig();
        var shot = rig.Shot(Entity.Null);

        rig.World.Add(
            shot,
            PovAim.Default with { Pitch = MathUtil.Pi, MaximumPitch = MathUtil.DegreesToRadians(45f) }
        );

        rig.Tick();

        var forward = Quaternion.Transform(Vector3.Forward, rig.Read(shot).Rotation);
        Assert.Equal(MathF.Sin(MathUtil.DegreesToRadians(45f)), forward.Y, 3);
    }

    [Fact]
    public void AMatchTargetAimTakesTheTargetsOrientation() {
        using var rig = new Rig();
        var rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);
        var target = rig.Place(Vector3.Zero, rotation);
        var shot = rig.Shot(target);
        rig.World.Add(shot, new MatchTargetAim());

        rig.Tick();

        Assert.True(Quaternion.SameRotation(rotation, rig.Read(shot).Rotation, 1e-4f));
    }

    // ── Extensions ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConfinerKeepsTheShotInsideItsBox() {
        using var rig = new Rig();
        var target = rig.Place(new(100f, 0f, 0f));
        var shot = rig.Shot(target);
        rig.World.Add(shot, new HardLockBody());
        rig.World.Add(shot, CameraConfiner.Within(new(new(-10f, -10f, -10f), new(10f, 10f, 10f))));

        rig.Tick();

        AssertNear(new(10f, 0f, 0f), rig.Read(shot).Position);
    }

    sealed class Wall(float distance) : ICameraOcclusion {
        public bool Occluded(Vector3 subject, Vector3 desired, float radius, out Vector3 hit) {
            var direction = desired - subject;
            var length = direction.Length();

            if (length <= distance) {
                hit = desired;
                return false;
            }

            hit = subject + (direction / length * distance);
            return true;
        }
    }

    [Fact]
    public void AnObstaclePullsTheShotInFrontOfIt() {
        using var rig = new Rig();
        rig.System.Occlusion = new Wall(4f);

        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);

        rig.World.Add(
            shot,
            new FollowBody { Offset = new(0f, 0f, 10f), Binding = CameraBinding.World }
        );

        rig.World.Add(shot, CameraOcclusion.Default() with { PullOutDamping = 0f });

        rig.Tick();
        AssertNear(new(0f, 0f, 4f), rig.Read(shot).Position, 1e-3f);

        // Nothing in the way any more, and no damping on the way out.
        rig.System.Occlusion = new Wall(100f);
        rig.Tick();
        AssertNear(new(0f, 0f, 10f), rig.Read(shot).Position, 1e-3f);
    }

    [Fact]
    public void AShotWithNoOcclusionProviderIsUnaffectedByTheComponent() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);

        rig.World.Add(shot, new FollowBody { Offset = new(0f, 0f, 10f), Binding = CameraBinding.World });
        rig.World.Add(shot, CameraOcclusion.Default());

        rig.Tick();

        AssertNear(new(0f, 0f, 10f), rig.Read(shot).Position);
    }

    // ── Shake ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoiseNeverExceedsItsAmplitude() {
        using var rig = new Rig();
        var shot = rig.Shot(Entity.Null);

        rig.World.Add(
            shot,
            CameraNoise.Handheld with {
                PositionAmplitude = new(0.05f, 0.05f, 0.05f),
                PositionFrequency = new(3f, 5f, 7f)
            }
        );

        for (var step = 0; step < 2000; step++) {
            rig.Tick();
            var shake = rig.Read(shot).ShakePosition;

            Assert.True(
                MathF.Abs(shake.X) <= 0.05f && MathF.Abs(shake.Y) <= 0.05f && MathF.Abs(shake.Z) <= 0.05f,
                $"{shake} at step {step}"
            );
        }
    }

    [Fact]
    public void NoiseIsAFunctionOfTheClockAndTheSeed() {
        Assert.Equal(
            CameraNoiseSignal.Sample(12.25, 0, 7),
            CameraNoiseSignal.Sample(12.25, 0, 7)
        );

        Assert.NotEqual(
            CameraNoiseSignal.Sample(12.25, 0, 7),
            CameraNoiseSignal.Sample(12.25, 0, 8)
        );

        Assert.NotEqual(
            CameraNoiseSignal.Sample(12.25, 0, 7),
            CameraNoiseSignal.Sample(12.25, 1, 7)
        );
    }

    [Fact]
    public void NoiseIsContinuous() {
        var previous = CameraNoiseSignal.Sample(0.0, 0, 3);

        for (var step = 1; step <= 10_000; step++) {
            var value = CameraNoiseSignal.Sample(step / 1000d, 0, 3);
            Assert.True(MathF.Abs(value - previous) < 0.02f, $"jumped at {step}");
            previous = value;
        }
    }

    [Fact]
    public void AShakeIsNotFedBackIntoTheDamping() {
        using var rig = new Rig();
        var target = rig.Place(Vector3.Zero);
        var shot = rig.Shot(target);

        rig.World.Add(shot, new FollowBody { Offset = new(0f, 0f, 10f), Binding = CameraBinding.World });
        rig.World.Add(shot, CameraNoise.Handheld with { PositionAmplitude = new(1f, 1f, 1f) });

        for (var step = 0; step < 100; step++) {
            rig.Tick();
        }

        // The damped position is exactly where the body put it, however much the shake moved the
        // picture — which is the property that stops a hand-held camera chasing its own wobble.
        AssertNear(new(0f, 0f, 10f), rig.Read(shot).Position, 1e-3f);
        Assert.NotEqual(Vector3.Zero, rig.Read(shot).ShakePosition);
    }

    [Fact]
    public void AnImpulseIsFeltLessFurtherAway() {
        var impulses = new CameraImpulses();

        impulses.Emit(
            CameraImpulse.Bump(Vector3.Zero, new(0f, 5f, 0f), duration: 1f, dissipation: 100f)
        );

        impulses.Advance(0.02f);

        var near = impulses.Sample(new(0f, 0f, 1f)).Length();
        var far = impulses.Sample(new(0f, 0f, 50f)).Length();

        Assert.True(near > far, $"{near} against {far}");
        Assert.Equal(0f, impulses.Sample(new(0f, 0f, 200f)).Length(), 6);
    }

    [Fact]
    public void AnImpulseEndsExactlyWhenItSaysItWill() {
        var impulses = new CameraImpulses();
        impulses.Emit(CameraImpulse.Bump(Vector3.Zero, new(0f, 5f, 0f), duration: 0.5f));

        for (var step = 0; step < 25; step++) {
            impulses.Advance(0.02f);
        }

        Assert.Equal(0, impulses.Count);
        Assert.Equal(Vector3.Zero, impulses.Sample(Vector3.Zero));
    }

    [Fact]
    public void APropagatingImpulseArrivesLate() {
        var impulses = new CameraImpulses();

        impulses.Emit(
            CameraImpulse.Bump(Vector3.Zero, new(0f, 5f, 0f), duration: 1f) with {
                PropagationSpeed = 10f,
                DissipationDistance = 100f
            }
        );

        impulses.Advance(0.1f);
        Assert.Equal(0f, impulses.Sample(new(0f, 0f, 50f)).Length(), 6);

        impulses.Advance(5f);
        Assert.True(impulses.Sample(new(0f, 0f, 50f)).Length() > 0f);
    }

    [Fact]
    public void AListenerTurnsAnImpulseIntoAShake() {
        using var rig = new Rig();
        var shot = rig.Shot(Entity.Null);
        rig.World.Add(shot, CameraImpulseListener.Default);

        rig.System.Impulses.Emit(CameraImpulse.Bump(Vector3.Zero, new(0f, 10f, 0f), duration: 1f));
        rig.Tick(0.03f);

        var shake = rig.Read(shot).ShakePosition;
        Assert.True(shake.Y > 0f, $"{shake}");
    }

    // ── Configuration ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoBodiesOnOneShotIsReportedAsTheMistakeItIs() {
        using var rig = new Rig();
        var shot = rig.Shot(Entity.Null);

        Assert.True(VirtualCameras.Validate(rig.World, shot));

        rig.World.Add(shot, new HardLockBody());
        Assert.True(VirtualCameras.Validate(rig.World, shot));

        rig.World.Add(shot, FollowBody.Behind(5f, 2f));
        Assert.False(VirtualCameras.Validate(rig.World, shot));
        Assert.Equal(2, VirtualCameras.BodyCount(rig.World, shot));
    }
}
