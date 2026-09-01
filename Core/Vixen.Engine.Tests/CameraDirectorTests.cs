// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Frames;
using Vixen.Engine.Transforms;
using Vixen.Testing;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class CameraDirectorTests {
    /// <summary>A camera, a director, the pipeline behind it, and a clock.</summary>
    sealed class Rig : IDisposable {
        long frame;
        double total;

        public World World { get; } = new("Director");

        public VirtualCameraSystem Cameras { get; } = new();

        public CameraDirectorSystem Director { get; } = new();

        /// <summary>The real camera the director drives.</summary>
        public Entity Eye { get; }

        public Rig(CameraDirector? settings = null) {
            Eye = Hierarchy.CreateTransform(World, LocalTransform.Identity);
            World.Add(Eye, Camera.Perspective);
            World.Add(Eye, settings ?? CameraDirector.Default);
        }

        public void Tick(float seconds = 1f / 60f) {
            frame++;
            total += seconds;
            var elapsed = TimeSpan.FromSeconds(seconds);
            var time = new GameTime(TimeSpan.FromSeconds(total), elapsed, elapsed, frame, 1f);

            Cameras.Evaluate(World, time);
            Director.Direct(World, time);
        }

        /// <summary>A shot that sits still, so a test can name where the camera should end up.</summary>
        public Entity Shot(Vector3 at, int priority, bool enabled = true, int channel = 0) =>
            VirtualCameras.Create(
                World,
                VirtualCamera.Default with { Priority = priority, Enabled = enabled, Channel = channel },
                default,
                LocalTransform.At(at)
            );

        public Vector3 Position => World.Read<LocalTransform>(Eye).Position;

        public void Dispose() => World.Dispose();
    }

    static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.NearEqual(expected, actual, tolerance), $"expected {expected}, got {actual}");

    [Fact]
    public void TheHighestPriorityShotIsTheOneRendered() {
        using var rig = new Rig();
        rig.Shot(new(1f, 0f, 0f), priority: 10);
        var winner = rig.Shot(new(2f, 0f, 0f), priority: 20);

        rig.Tick();

        Assert.Equal(winner, rig.Director.LiveCameraOf(rig.Eye));
        AssertNear(new(2f, 0f, 0f), rig.Position);
    }

    [Fact]
    public void ADisabledShotIsNotACandidate() {
        using var rig = new Rig();
        var enabled = rig.Shot(new(1f, 0f, 0f), priority: 10);
        rig.Shot(new(2f, 0f, 0f), priority: 20, enabled: false);

        rig.Tick();

        Assert.Equal(enabled, rig.Director.LiveCameraOf(rig.Eye));
    }

    /// <summary>
    ///     Two shots, one number between them, and the answer is the one the designer just switched
    ///     on. Breaking the tie by entity id would be equally deterministic and would make the second
    ///     trigger in a level appear to be broken.
    /// </summary>
    [Fact]
    public void EqualPrioritiesGoToWhicheverWasEnabledLast() {
        using var rig = new Rig(CameraDirector.Default with { DefaultBlend = CameraBlend.Cut });
        var first = rig.Shot(new(1f, 0f, 0f), priority: 10);
        var second = rig.Shot(new(2f, 0f, 0f), priority: 10, enabled: false);

        rig.Tick();
        Assert.Equal(first, rig.Director.LiveCameraOf(rig.Eye));

        rig.World.Get<VirtualCamera>(second).Enabled = true;
        rig.Tick();
        Assert.Equal(second, rig.Director.LiveCameraOf(rig.Eye));

        // And switching the first one off and on again hands it back.
        rig.World.Get<VirtualCamera>(first).Enabled = false;
        rig.Tick();
        rig.World.Get<VirtualCamera>(first).Enabled = true;
        rig.Tick();
        Assert.Equal(first, rig.Director.LiveCameraOf(rig.Eye));
    }

    [Fact]
    public void ACutArrivesAtOnce() {
        using var rig = new Rig(CameraDirector.Default with { DefaultBlend = CameraBlend.Cut });
        rig.Shot(new(1f, 0f, 0f), priority: 10);
        var next = rig.Shot(new(50f, 0f, 0f), priority: 0);

        rig.Tick();
        rig.World.Get<VirtualCamera>(next).Priority = 20;
        rig.Tick();

        AssertNear(new(50f, 0f, 0f), rig.Position);
        Assert.Equal(0f, rig.Director.BlendProgressOf(rig.Eye));
    }

    [Fact]
    public void ABlendTakesTheTimeItSaysAndEndsWhereItSaid() {
        using var rig = new Rig(
            CameraDirector.Default with {
                DefaultBlend = new() { Style = CameraBlendStyle.Linear, Duration = 1f }
            }
        );

        var from = rig.Shot(Vector3.Zero, priority: 10);
        var to = rig.Shot(new(10f, 0f, 0f), priority: 0);

        rig.Tick();
        AssertNear(Vector3.Zero, rig.Position);

        rig.World.Get<VirtualCamera>(to).Priority = 20;

        rig.Tick(0.5f);
        AssertNear(new(5f, 0f, 0f), rig.Position);
        Assert.Equal(from, rig.Director.BlendingFrom(rig.Eye));

        rig.Tick(0.5f);
        AssertNear(new(10f, 0f, 0f), rig.Position);
        Assert.Equal(Entity.Null, rig.Director.BlendingFrom(rig.Eye));
    }

    [Fact]
    public void AnEasedBlendIsSymmetricalAboutItsMidpoint() {
        var blend = new CameraBlend { Style = CameraBlendStyle.EaseInOut, Duration = 2f };

        Assert.Equal(0f, blend.Evaluate(0f), 5);
        Assert.Equal(0.5f, blend.Evaluate(1f), 5);
        Assert.Equal(1f, blend.Evaluate(2f), 5);
        Assert.Equal(1f, blend.Evaluate(9f), 5);
    }

    /// <summary>
    ///     A cut in the middle of a blend must not move the picture on the frame it happens. The
    ///     outgoing side becomes a snapshot of what was already on screen, which is what makes the
    ///     interruption invisible.
    /// </summary>
    [Fact]
    public void AnInterruptedBlendDoesNotPop() {
        using var rig = new Rig(
            CameraDirector.Default with {
                DefaultBlend = new() { Style = CameraBlendStyle.Linear, Duration = 2f }
            }
        );

        rig.Shot(Vector3.Zero, priority: 10);
        var second = rig.Shot(new(100f, 0f, 0f), priority: 0);
        var third = rig.Shot(new(0f, 0f, 100f), priority: 0);

        rig.Tick();
        rig.World.Get<VirtualCamera>(second).Priority = 20;

        rig.Tick(1f);
        var midway = rig.Position;
        AssertNear(new(50f, 0f, 0f), midway);

        rig.World.Get<VirtualCamera>(third).Priority = 30;
        rig.Tick(0.001f);

        Assert.True(Vector3.Distance(midway, rig.Position) < 0.2f, $"{midway} jumped to {rig.Position}");
    }

    [Fact]
    public void ChannelsKeepTwoDirectorsApart() {
        using var rig = new Rig();

        var other = Hierarchy.CreateTransform(rig.World, LocalTransform.Identity);
        rig.World.Add(other, Camera.Perspective);
        rig.World.Add(other, CameraDirector.Default with { Channel = 1 });

        rig.Shot(new(1f, 0f, 0f), priority: 10);
        rig.Shot(new(9f, 0f, 0f), priority: 99, channel: 1);

        rig.Tick();

        AssertNear(new(1f, 0f, 0f), rig.Position);
        AssertNear(new(9f, 0f, 0f), rig.World.Read<LocalTransform>(other).Position);
    }

    [Fact]
    public void TheLiveShotsLensReachesTheCamera() {
        using var rig = new Rig();

        var shot = rig.Shot(Vector3.Zero, priority: 10);

        rig.World.Get<VirtualCamera>(shot).Lens = CameraLens.Default with {
            FieldOfView = MathUtil.DegreesToRadians(30f),
            FarPlane = 500f
        };

        rig.Tick();

        var camera = rig.World.Read<Camera>(rig.Eye);
        Assert.Equal(MathUtil.DegreesToRadians(30f), camera.FieldOfView, 5);
        Assert.Equal(500f, camera.FarPlane, 3);
    }

    [Fact]
    public void ADirectorThatDoesNotOwnTheLensLeavesItAlone() {
        using var rig = new Rig(CameraDirector.Default with { WriteLens = false });
        rig.World.Get<Camera>(rig.Eye).FieldOfView = MathUtil.DegreesToRadians(12f);
        rig.Shot(Vector3.Zero, priority: 10);

        rig.Tick();

        Assert.Equal(MathUtil.DegreesToRadians(12f), rig.World.Read<Camera>(rig.Eye).FieldOfView, 5);
    }

    /// <summary>
    ///     The shot is in world space and the camera's transform is not. Composing the local rotation
    ///     the wrong way round is right only while the two rotations commute, which is why the parent
    ///     here is turned about a different axis than the shot.
    /// </summary>
    [Fact]
    public void ACameraUnderARotatedParentStillEndsUpWhereTheShotIs() {
        using var rig = new Rig();

        var parent = Hierarchy.CreateTransform(
            rig.World,
            LocalTransform.Identity with {
                Position = new(3f, 4f, 5f),
                Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo)
            }
        );

        Hierarchy.SetParent(rig.World, rig.Eye, parent);

        var target = Hierarchy.CreateTransform(rig.World, LocalTransform.At(new(0f, 0f, -20f)));
        var shot = rig.Shot(new(10f, 2f, 0f), priority: 10);
        rig.World.Get<CameraTargets>(shot).LookAt = target;
        rig.World.Add(shot, new HardLookAim());

        rig.Tick();

        var local = rig.World.Read<LocalTransform>(rig.Eye);
        var world = local.ToMatrix() * Hierarchy.ResolveWorldMatrix(rig.World, parent);

        AssertNear(new(10f, 2f, 0f), world.Translation);

        Assert.True(Matrix4x4.Decompose(world, out _, out var rotation, out _));
        var forward = Quaternion.Transform(Vector3.Forward, rotation);
        AssertNear(Vector3.Normalize(new(-10f, -2f, -20f)), forward);
    }

    [Fact]
    public void ResettingADirectorMakesItsNextFrameACut() {
        using var rig = new Rig(
            CameraDirector.Default with {
                DefaultBlend = new() { Style = CameraBlendStyle.Linear, Duration = 10f }
            }
        );

        rig.Shot(Vector3.Zero, priority: 10);
        var next = rig.Shot(new(80f, 0f, 0f), priority: 0);

        rig.Tick();
        rig.World.Get<VirtualCamera>(next).Priority = 20;
        rig.Director.Reset(rig.Eye);
        rig.Tick();

        AssertNear(new(80f, 0f, 0f), rig.Position);
    }

    [Fact]
    public void ADirectorWithNoShotsLeavesItsCameraWhereItIs() {
        using var rig = new Rig();
        rig.World.Get<LocalTransform>(rig.Eye).Position = new(7f, 7f, 7f);

        rig.Tick();

        AssertNear(new(7f, 7f, 7f), rig.Position);
    }

    /// <summary>
    ///     The whole thing through the real frame loop: the pipeline and the director in
    ///     <c>LateUpdate</c> in that order, and the transform pass in <c>PreRender</c> resolving the
    ///     camera they moved — in one frame, with nothing registered by hand.
    /// </summary>
    [Fact]
    public void ThePipelineRunsAsPartOfAFrame() {
        using var loop = new EngineLoop();
        loop.Add(new VirtualCameraSystem());
        loop.Add(new CameraDirectorSystem());

        var eye = Hierarchy.CreateTransform(loop.World, LocalTransform.Identity);
        loop.World.Add(eye, Camera.Perspective);
        loop.World.Add(eye, CameraDirector.Default);

        var target = Hierarchy.CreateTransform(loop.World, LocalTransform.At(new(20f, 0f, 0f)));

        var shot = VirtualCameras.Create(loop.World, VirtualCamera.Default, CameraTargets.Both(target));
        loop.World.Add(shot, new HardLockBody());
        loop.World.Add(shot, new HardLookAim());

        loop.Frame(TimeSpan.FromSeconds(1d / 60d));

        AssertNear(new(20f, 0f, 0f), loop.World.Read<LocalTransform>(eye).Position);
        AssertNear(new(20f, 0f, 0f), loop.World.Read<WorldTransform>(eye).Position);
    }

    /// <summary>
    ///     A hundred shots, every stage between them, stepped five hundred times without asking the
    ///     allocator for a byte — the bar <c>TransformSystem</c> is already held to.
    /// </summary>
    [Fact]
    public void ASteadyStateOfShotsAllocatesNothing() {
        using var rig = new Rig();
        var target = Hierarchy.CreateTransform(rig.World, LocalTransform.At(new(5f, 0f, 0f)));

        for (var index = 0; index < 100; index++) {
            var shot = rig.Shot(new(index, 0f, 0f), priority: index);
            rig.World.Get<CameraTargets>(shot) = CameraTargets.Both(target);

            switch (index % 4) {
                case 0:
                    rig.World.Add(shot, FollowBody.Behind(6f, 2f));
                    rig.World.Add(shot, ComposerAim.Centred());
                    break;

                case 1:
                    rig.World.Add(shot, OrbitBody.At(8f));
                    rig.World.Add(shot, new HardLookAim());
                    break;

                case 2:
                    rig.World.Add(shot, FramingBody.At(12f));
                    rig.World.Add(shot, CameraNoise.Handheld);
                    break;

                default:
                    rig.World.Add(shot, new HardLockBody());
                    rig.World.Add(shot, PovAim.Default);
                    break;
            }
        }

        Measured.NothingAllocated(Frame, warmUp: 8, passes: 500);

        return;

        void Frame() => rig.Tick();
    }

    [Fact]
    public void TheBlendTablePrefersTheRuleThatNamesBothShots() {
        using var world = new World();
        var from = world.Create();
        var to = world.Create();

        var table = new CameraBlendTable()
            .Add(Entity.Null, to, CameraBlend.Over(4f))
            .Add(from, Entity.Null, CameraBlend.Over(3f))
            .Add(from, to, CameraBlend.Over(1f));

        Assert.Equal(1f, table.Resolve(from, to, CameraBlend.Default).Duration);
        Assert.Equal(3f, table.Resolve(from, world.Create(), CameraBlend.Default).Duration);
        Assert.Equal(4f, table.Resolve(world.Create(), to, CameraBlend.Default).Duration);
        Assert.Equal(2f, table.Resolve(world.Create(), world.Create(), CameraBlend.Default).Duration);
    }
}
