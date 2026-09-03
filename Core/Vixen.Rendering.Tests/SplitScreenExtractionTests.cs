// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The half of split-screen that happens before anything is drawn: two cameras, two views.</summary>
/// <remarks>
///     <para>
///         Split-screen simulated and did not draw, and the two reasons were here: a
///         <see cref="RenderView" /> carried no viewport rectangle, and
///         <see cref="CameraExtractionSystem" /> filled exactly one view from the lowest
///         <see cref="Camera.Order" /> — so a host with two cameras could aim only one of them.
///     </para>
///     <para>
///         ⚠ <b>What is asserted here is what a single-view frame cannot satisfy.</b> Two systems at
///         two ranks producing two different matrices, a rect that reaches the view, and — the half
///         that is easiest to forget and impossible to see in a counter — an <em>aspect ratio</em>
///         that follows the rect. A split screen whose projections were not narrowed draws two
///         perfectly plausible halves in which everything is twice as wide as it should be.
///     </para>
/// </remarks>
public sealed class SplitScreenExtractionTests {
    /// <summary>
    ///     The two seats are two ranks, and they land on the two cameras rather than both on the
    ///     first one. A fallback to camera zero would be two halves showing one picture, which reads
    ///     as a broken split rather than as a missing camera.
    /// </summary>
    [Fact]
    public void TwoRanksTakeTheTwoLowestOrderedCameras() {
        using var world = new World();

        var top = new RenderView("Camera");
        var bottom = new RenderView("Camera2");

        var seatZero = new CameraExtractionSystem(top) { AspectRatio = 1f };
        var seatOne = new CameraExtractionSystem(bottom) { AspectRatio = 1f, Rank = 1 };

        world.Add(Placed(world, new(0f, 0f, 0f)), Camera.Perspective with { Order = 0 });
        world.Add(Placed(world, new(0f, 0f, 40f)), Camera.Perspective with { Order = 1 });
        Resolve(world);

        seatZero.Extract(world);
        seatOne.Extract(world);

        Assert.True(seatZero.Found);
        Assert.True(seatOne.Found);
        Assert.Equal(Vector3.Zero, top.Position);
        Assert.Equal(new Vector3(0f, 0f, 40f), bottom.Position);
        Assert.NotEqual(top.ViewProjection, bottom.ViewProjection);
    }

    /// <summary>
    ///     ⚠ Order is a priority and priorities may tie, so the rank has to break ties or the second
    ///     seat of two cameras both at order zero gets nothing at all — a black half, reported
    ///     nowhere. `PlayerCameras` sets the order from the channel, so distinct orders are the
    ///     ordinary case and this is the one that would have gone unnoticed.
    /// </summary>
    [Fact]
    public void CamerasAtTheSameOrderStillRankApart() {
        using var world = new World();

        var top = new RenderView("Camera");
        var bottom = new RenderView("Camera2");

        var seatZero = new CameraExtractionSystem(top) { AspectRatio = 1f };
        var seatOne = new CameraExtractionSystem(bottom) { AspectRatio = 1f, Rank = 1 };

        world.Add(Placed(world, Vector3.Zero), Camera.Perspective);
        world.Add(Placed(world, new(0f, 0f, 40f)), Camera.Perspective);
        Resolve(world);

        seatZero.Extract(world);
        seatOne.Extract(world);

        Assert.True(seatOne.Found);
        Assert.NotEqual(top.Position, bottom.Position);
    }

    /// <summary>
    ///     A rank past the end says so rather than falling back, and leaves the view exactly as it
    ///     was — the rule the no-camera case already followed, for the same reason: a frame drawn
    ///     from a zeroed matrix is a black screen that looks like the renderer is broken.
    /// </summary>
    [Fact]
    public void ARankPastTheLastCameraIsNotFoundAndTouchesNothing() {
        using var world = new World();
        var view = new RenderView("Camera2") { Position = new(7f, 7f, 7f) };
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f, Rank = 1 };

        world.Add(Placed(world, Vector3.Zero), Camera.Perspective);
        Resolve(world);

        system.Extract(world);

        Assert.False(system.Found);
        Assert.Equal(1, system.CameraCount);
        Assert.Equal(new Vector3(7f, 7f, 7f), view.Position);
    }

    /// <summary>Rank zero is what every single-camera game already had, and it still is.</summary>
    [Fact]
    public void RankZeroIsTheLowestOrderWhichIsWhatItAlwaysWas() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        world.Add(Placed(world, new(0f, 0f, 40f)), Camera.Perspective with { Order = 5 });
        world.Add(Placed(world, Vector3.Zero), Camera.Perspective with { Order = 1 });
        Resolve(world);

        system.Extract(world);

        Assert.True(system.Found);
        Assert.Equal(2, system.CameraCount);
        Assert.Equal(Vector3.Zero, view.Position);
    }

    /// <summary>
    ///     ⚠ The closed form, and the assertion this whole feature turns on. A camera drawn into the
    ///     top half of a 16:9 screen is looking through a 32:9 window — twice as wide as it is tall
    ///     relative to before — and a projection that kept 16:9 would draw the same cone into half
    ///     the pixels, which is every object stretched to twice its width. Nothing about that frame
    ///     is missing, so no counter anywhere can see it.
    /// </summary>
    [Fact]
    public void AHalfHeightRectDoublesTheProjectionsAspectRatio() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 16f / 9f };

        world.Add(
            Placed(world, Vector3.Zero),
            Camera.Perspective with { ViewportRect = new(0f, 0f, 1f, 0.5f) }
        );

        Resolve(world);
        system.Extract(world);

        Assert.Equal(32f / 9f, view.Camera!.Value.AspectRatio, 4);

        // And the matrix a shader actually rasterises through, not only the description handed to the
        // cascade fit. Those two disagreeing is the shape this engine has already been bitten by.
        var wide = Project(view.ViewProjection, new(1f, 0f, -1f));
        var tall = Project(view.ViewProjection, new(0f, 1f, -1f));

        // Half the height means a point one unit up is twice as far up the clip cube as a point one
        // unit across is across it, times the target's own 16:9 — which is the definition of 32:9.
        Assert.Equal(32f / 9f, tall.Y / wide.X, 3);
    }

    /// <summary>
    ///     ⚠ Null, not a zeroed rectangle. A <see cref="Rectangle" /> of zero width is a viewport
    ///     that rasterises nothing, so a camera that named no region arriving downstream as
    ///     <c>default</c> would be every scene saved before the field existed silently drawing
    ///     nothing at all.
    /// </summary>
    [Fact]
    public void ACameraWithNoRectLeavesTheViewsRectNullRatherThanZeroed() {
        using var world = new World();
        var view = new RenderView("Camera") { ViewportRect = new(0f, 0f, 1f, 0.5f) };
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        world.Add(Placed(world, Vector3.Zero), Camera.Perspective);
        Resolve(world);

        system.Extract(world);

        Assert.Null(view.ViewportRect);
        Assert.Equal(1f, view.Camera!.Value.AspectRatio, 4);
    }

    /// <summary>The rect reaches the view, which is what the compositor reads.</summary>
    [Fact]
    public void TheCamerasRectReachesTheView() {
        using var world = new World();
        var view = new RenderView("Camera2");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f, Rank = 1 };

        world.Add(Placed(world, Vector3.Zero), Camera.Perspective with { Order = 0 });

        world.Add(
            Placed(world, new(0f, 0f, 40f)),
            Camera.Perspective with { Order = 1, ViewportRect = new(0f, 0.5f, 1f, 0.5f) }
        );

        Resolve(world);
        system.Extract(world);

        Assert.Equal(new Rectangle(0f, 0.5f, 1f, 0.5f), view.ViewportRect);
    }

    /// <summary>
    ///     A camera that names both its own aspect and a rect gets both — a letterboxed cutscene
    ///     inside a split screen is a real combination, and the two corrections compose rather than
    ///     one winning.
    /// </summary>
    [Fact]
    public void AnAuthoredAspectRatioAndARectCompose() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 16f / 9f };

        world.Add(
            Placed(world, Vector3.Zero),
            Camera.Perspective with { AspectRatio = 1f, ViewportRect = new(0f, 0f, 0.5f, 1f) }
        );

        Resolve(world);
        system.Extract(world);

        Assert.Equal(0.5f, view.Camera!.Value.AspectRatio, 4);
    }

    // --- The seat layout ----------------------------------------------------

    /// <summary>
    ///     Two seats are the top and the bottom, and ⚠ seat zero is the top: a viewport's Y is
    ///     measured down from the top edge, unlike clip space, whose +1 is the top. It is the one
    ///     place a split screen comes out upside down for a reason nothing reports.
    /// </summary>
    [Fact]
    public void TwoSeatsSplitHorizontallyWithSeatZeroOnTop() {
        Assert.Equal(new Rectangle(0f, 0f, 1f, 0.5f), PlayerCameras.SeatRect(0, 2));
        Assert.Equal(new Rectangle(0f, 0.5f, 1f, 0.5f), PlayerCameras.SeatRect(1, 2));
    }

    /// <summary>Three and four seats are quadrants, and three leaves the fourth empty.</summary>
    [Fact]
    public void ThreeAndFourSeatsAreQuadrants() {
        Assert.Equal(new Rectangle(0f, 0f, 0.5f, 0.5f), PlayerCameras.SeatRect(0, 4));
        Assert.Equal(new Rectangle(0.5f, 0f, 0.5f, 0.5f), PlayerCameras.SeatRect(1, 4));
        Assert.Equal(new Rectangle(0f, 0.5f, 0.5f, 0.5f), PlayerCameras.SeatRect(2, 4));
        Assert.Equal(new Rectangle(0.5f, 0.5f, 0.5f, 0.5f), PlayerCameras.SeatRect(3, 4));
        Assert.Equal(PlayerCameras.SeatRect(2, 4), PlayerCameras.SeatRect(2, 3));
    }

    /// <summary>
    ///     ⚠ The seats tile the screen exactly. A layout leaving a gap draws an uncleared strip that
    ///     holds the previous frame, and one that overlaps draws the later seat over the earlier —
    ///     both are pictures, and neither reports anything.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheSeatsCoverTheScreenWithoutOverlapping(int seats) {
        var area = 0f;

        for (var seat = 0; seat < seats; seat++) {
            var rect = PlayerCameras.SeatRect(seat, seats);

            Assert.InRange(rect.X, 0f, 1f);
            Assert.InRange(rect.Y, 0f, 1f);
            Assert.InRange(rect.X + rect.Width, 0f, 1f);
            Assert.InRange(rect.Y + rect.Height, 0f, 1f);

            for (var other = 0; other < seat; other++) {
                Assert.False(Overlaps(rect, PlayerCameras.SeatRect(other, seats)));
            }

            area += rect.Width * rect.Height;
        }

        // Three seats deliberately leave a quarter empty; every other count fills the screen.
        Assert.Equal(seats == 3 ? 0.75f : 1f, area, 4);
    }

    /// <summary>A single seat is the whole screen, which is the rect a lone camera would not set.</summary>
    [Fact]
    public void OneSeatIsTheWholeScreen() =>
        Assert.Equal(new Rectangle(0f, 0f, 1f, 1f), PlayerCameras.SeatRect(0, 1));

    /// <summary><see cref="PlayerCameras.SplitScreen" /> writes the rects onto the cameras themselves.</summary>
    [Fact]
    public void SplitScreenWritesEachSeatsRectOntoItsCamera() {
        using var world = new World();

        var top = PlayerCameras.CreateEye(world);
        var bottom = PlayerCameras.CreateEye(world, channel: 1);

        PlayerCameras.SplitScreen(world, [top, bottom]);

        Assert.Equal(new Rectangle(0f, 0f, 1f, 0.5f), world.Read<Camera>(top).ViewportRect);
        Assert.Equal(new Rectangle(0f, 0.5f, 1f, 0.5f), world.Read<Camera>(bottom).ViewportRect);
        Assert.True(world.Read<Camera>(bottom).HasViewportRect);
    }

    /// <summary>A camera that was never given a rect reports it has none, whatever else is zeroed.</summary>
    [Fact]
    public void AnUnsetRectIsNotARegion() {
        Assert.False(Camera.Perspective.HasViewportRect);
        Assert.False(default(Camera).HasViewportRect);
        Assert.False((Camera.Perspective with { ViewportRect = new(0f, 0f, 1f, 0f) }).HasViewportRect);
        Assert.True((Camera.Perspective with { ViewportRect = new(0f, 0f, 1f, 1f) }).HasViewportRect);
    }

    static bool Overlaps(in Rectangle a, in Rectangle b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>A world position through a matrix, divided through — where it lands in NDC.</summary>
    static Vector3 Project(in Matrix4x4 matrix, Vector3 point) {
        var clip = Matrix4x4.TransformVector4(new(point.X, point.Y, point.Z, 1f), matrix);

        return new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    static Entity Placed(World world, Vector3 position) =>
        Hierarchy.CreateTransform(world, LocalTransform.At(position));

    static void Resolve(World world) => new TransformSystem().Resolve(world);
}
