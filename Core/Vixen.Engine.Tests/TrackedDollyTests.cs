// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     The dolly that rides a spline — [docs/plan/31 § T8], and [docs/plan/26]'s largest owed item.
/// </summary>
/// <remarks>
///     Doc 26 declined to invent a spline for its dolly track, because "it would make it the second
///     spline in the engine the moment anything else needs one". This is the camera stage that
///     document said would be small once the asset existed, and these are the tests that say it is.
/// </remarks>
public sealed class TrackedDollyTests {
    /// <summary>A spline source over a dictionary, which is what a host's asset table is.</summary>
    sealed class Tracks : ISplineSource {
        readonly Dictionary<string, Spline> splines = new(StringComparer.Ordinal);

        public void Add(string name, Spline spline) => splines[name] = spline;

        public bool TryGet(string name, out Spline? spline) {
            var found = splines.TryGetValue(name, out var value);

            spline = value;

            return found;
        }
    }

    sealed class Rig : IDisposable {
        long frame;
        double total;

        public World World { get; } = new("Dolly");

        public VirtualCameraSystem System { get; } = new();

        public Tracks Splines { get; } = new();

        public Rig() => System.Splines = Splines;

        public void Tick(float seconds = 1f / 60f) {
            frame++;
            total += seconds;

            var elapsed = TimeSpan.FromSeconds(seconds);

            System.Evaluate(World, new(TimeSpan.FromSeconds(total), elapsed, elapsed, frame, 1f));
        }

        public Entity Place(Vector3 position) => Hierarchy.CreateTransform(World, LocalTransform.At(position));

        public CameraShot Read(Entity shot) => World.Read<CameraShot>(shot);

        public void Dispose() => World.Dispose();
    }

    /// <summary>A straight sixty-metre track along +X at the origin's height.</summary>
    static Spline Straight() =>
        new([
            SplinePoint.Smooth(new(0f, 0f, 0f), new(20f, 0f, 0f)),
            SplinePoint.Smooth(new(60f, 0f, 0f), new(20f, 0f, 0f))
        ]);

    static Entity Dolly(Rig rig, TrackedDollyBody body, Entity target = default) {
        var entity = VirtualCameras.Create(
            rig.World,
            VirtualCamera.Default,
            CameraTargets.Both(target),
            LocalTransform.Identity
        );

        rig.World.Add(entity, body);

        return entity;
    }

    [Fact]
    public void ACameraSitsWhereItsTrackSaysItDoes() {
        using var rig = new Rig();

        rig.Splines.Add("Track", Straight());

        var shot = Dolly(rig, TrackedDollyBody.On("Track", 30f));

        rig.Tick();

        var position = rig.Read(shot).Position;

        Assert.Equal(30f, position.X, 1);
        Assert.Equal(0f, position.Z, 1);
    }

    /// <summary>The position is a distance, not a parameter.</summary>
    /// <remarks>
    ///     ⚠ <b>The classic bug in every dolly ever written.</b> A camera moving at a constant
    ///     parameter rate speeds up through the wide-open segments of its own track and crawls
    ///     through the tight ones. Measured on a track whose two segments are deliberately different
    ///     lengths: equal steps in distance have to cover equal ground.
    /// </remarks>
    [Fact]
    public void EqualStepsCoverEqualGround() {
        using var rig = new Rig();

        rig.Splines.Add(
            "Track",
            new([
                SplinePoint.Smooth(new(0f, 0f, 0f), new(4f, 0f, 0f)),
                SplinePoint.Smooth(new(10f, 0f, 0f), new(4f, 0f, 0f)),
                SplinePoint.Smooth(new(60f, 0f, 0f), new(30f, 0f, 0f))
            ])
        );

        var shot = Dolly(rig, TrackedDollyBody.On("Track"));
        var previous = default(Vector3?);

        for (var step = 0; step <= 8; step++) {
            rig.World.Set(shot, TrackedDollyBody.On("Track", step * 5f));
            rig.Tick();

            var position = rig.Read(shot).Position;

            if (previous is { } last) {
                Assert.InRange(Vector3.Distance(last, position), 4.5f, 5.5f);
            }

            previous = position;
        }
    }

    /// <summary>An open track clamps at its ends and a closed one wraps.</summary>
    [Fact]
    public void AnOpenTrackClampsAndAClosedOneWraps() {
        using var rig = new Rig();

        rig.Splines.Add("Open", Straight());
        rig.Splines.Add(
            "Loop",
            new(
                [
                    SplinePoint.Smooth(new(0f, 0f, 0f), new(10f, 0f, 0f)),
                    SplinePoint.Smooth(new(20f, 0f, 0f), new(0f, 0f, 10f)),
                    SplinePoint.Smooth(new(20f, 0f, 20f), new(-10f, 0f, 0f)),
                    SplinePoint.Smooth(new(0f, 0f, 20f), new(0f, 0f, -10f))
                ],
                closed: true
            )
        );

        var open = Dolly(rig, TrackedDollyBody.On("Open", 10_000f));
        var loop = Dolly(rig, TrackedDollyBody.On("Loop", 0f));

        rig.Tick();

        var end = rig.Read(open).Position;
        var start = rig.Read(loop).Position;

        Assert.Equal(60f, end.X, 0);

        // A whole lap plus nothing lands where it started.
        var lap = rig.Splines.TryGet("Loop", out var spline) && spline is not null ? spline.Length : 0f;

        rig.World.Set(loop, TrackedDollyBody.On("Loop", lap));
        rig.Tick();

        Assert.True(
            Vector3.Distance(start, rig.Read(loop).Position) < 0.5f,
            "a full lap did not return the camera to where it started."
        );
    }

    /// <summary>Auto-dolly slides to the point on the track nearest the target.</summary>
    [Fact]
    public void AutoDollySlidesToTheNearestPoint() {
        using var rig = new Rig();

        rig.Splines.Add("Track", Straight());

        var target = rig.Place(new(42f, 0f, 15f));
        var shot = Dolly(rig, TrackedDollyBody.Following("Track", damping: 0f), target);

        rig.Tick();

        var position = rig.Read(shot).Position;

        Assert.Equal(42f, position.X, 0);
        Assert.Equal(0f, position.Z, 1);
    }

    /// <summary>And it writes the position back, so switching to manual carries on from there.</summary>
    [Fact]
    public void AutoDollyWritesThePositionBack() {
        using var rig = new Rig();

        rig.Splines.Add("Track", Straight());

        var target = rig.Place(new(42f, 0f, 15f));
        var shot = Dolly(rig, TrackedDollyBody.Following("Track", damping: 0f), target);

        rig.Tick();

        Assert.Equal(42f, rig.World.Read<TrackedDollyBody>(shot).Position, 0);
    }

    /// <summary>The offset is in the track's own frame, so a banked track carries the camera round.</summary>
    [Fact]
    public void TheOffsetIsInTheTracksFrame() {
        using var rig = new Rig();

        rig.Splines.Add("Track", Straight());

        var body = TrackedDollyBody.On("Track", 30f) with { Offset = new(0f, 5f, 0f) };
        var shot = Dolly(rig, body);

        rig.Tick();

        var upright = rig.Read(shot).Position;

        Assert.Equal(5f, upright.Y, 1);

        // Rolled a quarter turn, the track's up points sideways and the camera goes with it.
        rig.Splines.Add(
            "Track",
            new([
                SplinePoint.Smooth(new(0f, 0f, 0f), new(20f, 0f, 0f), MathF.PI / 2f),
                SplinePoint.Smooth(new(60f, 0f, 0f), new(20f, 0f, 0f), MathF.PI / 2f)
            ])
        );

        rig.Tick();

        var banked = rig.Read(shot).Position;

        Assert.True(MathF.Abs(banked.Y) < 1f, $"a fully banked track still lifted the camera to {banked.Y}.");
        Assert.True(MathF.Abs(banked.Z) > 4f, $"a fully banked track did not carry the camera sideways.");
    }

    /// <summary>A camera whose track cannot be found holds its position.</summary>
    /// <remarks>
    ///     ⚠ <b>Falling back to the origin would send it through the level the first frame after
    ///     somebody renamed an asset.</b> A camera that has not moved is a thing an author notices
    ///     and can attribute.
    /// </remarks>
    [Fact]
    public void AMissingTrackHoldsThePosition() {
        using var rig = new Rig();

        var shot = Dolly(rig, TrackedDollyBody.On("Nowhere", 30f));

        rig.Tick();

        var held = rig.Read(shot).Position;

        rig.Tick();

        Assert.Equal(held, rig.Read(shot).Position);
    }

    /// <summary>And so does one in a world with no spline source at all.</summary>
    [Fact]
    public void NoSplineSourceIsNotACrash() {
        using var rig = new Rig();

        rig.System.Splines = null;
        rig.Splines.Add("Track", Straight());

        var shot = Dolly(rig, TrackedDollyBody.On("Track", 30f));

        rig.Tick();

        Assert.Equal(Vector3.Zero, rig.Read(shot).Position);
    }

    /// <summary>A dolly needs no target, which is what a cutscene track is.</summary>
    [Fact]
    public void ADollyWithNothingToFollowStillRides() {
        using var rig = new Rig();

        rig.Splines.Add("Track", Straight());

        var entity = rig.World.Create(VirtualCamera.Default);

        rig.World.Add(entity, TrackedDollyBody.On("Track", 15f));
        rig.Tick();

        Assert.Equal(15f, rig.Read(entity).Position.X, 1);
    }
}
