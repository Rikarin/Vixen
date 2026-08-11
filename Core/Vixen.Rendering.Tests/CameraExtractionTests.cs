// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The bridge from the camera a scene placed to the view the frame is drawn through.</summary>
/// <remarks>
///     Everything that can go wrong here is a disagreement between two descriptions of one volume:
///     a view matrix that is not the transform's inverse, a frustum derived from a different matrix
///     than the one the shader gets, or a <see cref="RenderCamera" /> that says a field of view the
///     projection does not have. Each of them draws a picture, which is why they need asserting
///     rather than looking at.
/// </remarks>
public sealed class CameraExtractionTests {
    [Fact]
    public void TheViewIsAimedFromTheCamerasTransform() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 16f / 9f };

        var entity = Placed(world, new(0f, 5f, 10f));
        world.Add(entity, Camera.Perspective);
        Resolve(world);

        system.Extract(world);

        Assert.True(system.Found);
        Assert.Equal(new Vector3(0f, 5f, 10f), view.Position);

        Assert.Equal(
            CameraMath.ViewProjection(Camera.Perspective, world.Read<WorldTransform>(entity), 16f / 9f),
            view.ViewProjection
        );
    }

    /// <summary>
    ///     The frustum the culling asks and the matrix the shader is given come from one assignment,
    ///     which is <see cref="RenderView.ViewProjection" />'s own promise. Culling against last
    ///     frame's planes with this frame's matrix pops geometry at the frustum edge and nothing
    ///     anywhere says why.
    /// </summary>
    [Fact]
    public void TheFrustumIsTheMatrixTheShaderGets() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        world.Add(Placed(world, new(0f, 0f, 4f)), Camera.Perspective);
        Resolve(world);

        system.Extract(world);

        Assert.Equal(new BoundingFrustum(view.ViewProjection), view.Frustum);
    }

    /// <summary>
    ///     A camera naming its own ratio ignores the target's, which is what a letterboxed cutscene
    ///     and a fixed-aspect retro game both want.
    /// </summary>
    [Fact]
    public void ACameraWithItsOwnAspectRatioKeepsIt() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 16f / 9f };

        var camera = Camera.Perspective with { AspectRatio = 1f };
        world.Add(Placed(world, Vector3.Zero), camera);
        Resolve(world);

        system.Extract(world);

        Assert.Equal(1f, view.Camera!.Value.AspectRatio);

        // At the origin with no rotation the view matrix is the identity, so what is left of the
        // view-projection is the projection — a fair comparison rather than an inverse chased through
        // a near-singular matrix.
        Assert.Equal(CameraMath.Projection(camera), view.ViewProjection);
    }

    /// <summary>
    ///     Lowest <see cref="Camera.Order" /> wins — the field's documented meaning, read as a
    ///     priority — and the walk is over every camera rather than stopping at the first.
    /// </summary>
    [Fact]
    public void TheLowestOrderCameraWins() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        world.Add(Placed(world, new(1f, 0f, 0f)), Camera.Perspective with { Order = 10 });
        world.Add(Placed(world, new(2f, 0f, 0f)), Camera.Perspective with { Order = -3 });
        world.Add(Placed(world, new(3f, 0f, 0f)), Camera.Perspective with { Order = 0 });
        Resolve(world);

        system.Extract(world);

        Assert.Equal(3, system.CameraCount);
        Assert.Equal(new Vector3(2f, 0f, 0f), view.Position);
    }

    /// <summary>
    ///     A world with no camera leaves the view exactly as it was. Zeroing it would render through
    ///     a degenerate matrix — a black frame that reads as a broken renderer rather than as a level
    ///     nobody has finished.
    /// </summary>
    [Fact]
    public void NoCameraLeavesTheViewAloneAndSaysSo() {
        using var world = new World();
        var aimed = Matrix4x4.FromTranslation(new Vector3(7f, 7f, 7f));
        var view = new RenderView("Camera") { ViewProjection = aimed };
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        system.Extract(world);

        Assert.False(system.Found);
        Assert.Equal(0, system.CameraCount);
        Assert.Equal(aimed, view.ViewProjection);
    }

    /// <summary>
    ///     <see cref="RenderCamera" /> describes a cone, and an orthographic frustum is a box — so an
    ///     orthographic camera leaves it null rather than filling it with a field of view it does not
    ///     have. What reads it is the shadow cascade fit, which would slice the wrong shape.
    /// </summary>
    [Fact]
    public void AnOrthographicCameraDescribesNoCone() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        world.Add(Placed(world, Vector3.Zero), Camera.Orthographic2D);
        Resolve(world);

        system.Extract(world);

        Assert.Null(view.Camera);
        Assert.Equal(0f, view.ScreenHeightScale);
    }

    /// <summary>
    ///     The LOD scale is <c>1 / tan(fov / 2)</c> and nothing else: a threshold authored as a
    ///     fraction of screen height has to mean the same thing on every window, which is the whole
    ///     reason <see cref="RenderView.ScreenHeightScale" /> is one number.
    /// </summary>
    [Fact]
    public void ThePerspectiveScreenScaleIsTheProjectionsHalfAngle() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 1f };

        var camera = Camera.Perspective with { FieldOfView = MathF.PI / 2f };
        world.Add(Placed(world, Vector3.Zero), camera);
        Resolve(world);

        system.Extract(world);

        Assert.Equal(1f, view.ScreenHeightScale, 5);
    }

    // ------------------------------------------------------------- the sub-pixel offset

    /// <summary>
    ///     ⚠ <b>The whole point of the offset: it moves the picture by exactly that much of a pixel,
    ///     at every depth.</b> A jitter that shifted near geometry more than far geometry would not be
    ///     a camera shake, it would be a shear — and it would resolve into a frame that is subtly
    ///     wrong in a way no counter reports.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(7f)]
    [InlineData(400f)]
    public void TheOffsetMovesTheProjectedPointByItselfAtEveryDepth(float distance) {
        var camera = new RenderCamera(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        var jitter = new Vector2(0.013f, -0.007f);
        var offset = camera with { Jitter = jitter };
        var point = new Vector3(0.6f, -0.3f, -distance);

        var before = Project(camera.ViewProjection, point);
        var after = Project(offset.ViewProjection, point);

        Assert.Equal(jitter.X, after.X - before.X, 5);
        Assert.Equal(jitter.Y, after.Y - before.Y, 5);
    }

    /// <summary>
    ///     ⚠ <b>The one that catches the shortcut.</b> On a bare projection the offset reduces to
    ///     <c>M31 -= j.X</c>, and writing only that is tempting — but on a view-projection the fourth
    ///     column is not <c>(0, 0, −1, 0)</c>, so the same two stores shear the frame instead of
    ///     shifting it. This asserts the two orders agree, which is what lets the ambient-occlusion
    ///     pass invert <c>RenderCamera.Projection</c> to unproject a depth buffer the *view's* matrix
    ///     rasterised.
    /// </summary>
    [Fact]
    public void OffsettingTheProjectionAndOffsettingTheProductAreTheSameMatrix() {
        var camera = new RenderCamera(
            new(3f, 4f, -5f),
            Vector3.Normalize(new(0.3f, -0.2f, -1f)),
            Vector3.UnitY,
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            1000f
        );

        var jitter = new Vector2(0.013f, -0.007f);
        var product = CameraMath.Jittered(camera.View * camera.Projection, jitter);
        var separately = camera.View * CameraMath.Jittered(camera.Projection, jitter);
        var point = new Vector3(2f, 1f, -37f);

        var a = Project(product, point);
        var b = Project(separately, point);

        Assert.Equal(a.X, b.X, 5);
        Assert.Equal(a.Y, b.Y, 5);
        Assert.Equal(a.Z, b.Z, 5);
    }

    /// <summary>
    ///     Both matrices the extraction writes carry the same offset, because they are built from
    ///     different things — the transform's inverse and the field of view — and every screen-space
    ///     pass in the frame inverts one to unproject a buffer the other drew.
    /// </summary>
    [Fact]
    public void BothOfTheFramesMatricesCarryTheSameOffset() {
        using var world = new World();
        var view = new RenderView("Camera");

        var system = new CameraExtractionSystem(view) {
            AspectRatio = 16f / 9f,
            JitterTarget = new(1600, 900)
        };

        world.Add(Placed(world, new(0f, 2f, 6f)), Camera.Perspective);
        Resolve(world);

        system.Extract(world);

        Assert.NotEqual(Vector2.Zero, system.Jitter);
        Assert.Equal(system.Jitter, view.Camera!.Value.Jitter * new Vector2(800f, 450f));

        var point = new Vector3(1f, 0f, -20f);
        var throughTheView = Project(view.ViewProjection, point);
        var throughTheCamera = Project(view.Camera!.Value.ViewProjection, point);

        Assert.Equal(throughTheView.X, throughTheCamera.X, 4);
        Assert.Equal(throughTheView.Y, throughTheCamera.Y, 4);
    }

    /// <summary>
    ///     ⚠ <b>A tree with no temporal resolve in it gets no offset</b>, because a camera that shakes
    ///     by half a pixel with nothing averaging the shake out is strictly worse than a still one.
    ///     Zero is the default, so this is also the regression guard for every frame that had no TAA
    ///     before the jitter was wired at all.
    /// </summary>
    [Fact]
    public void WithNoTargetThereIsNoOffsetAtAll() {
        using var world = new World();
        var view = new RenderView("Camera");
        var system = new CameraExtractionSystem(view) { AspectRatio = 16f / 9f };

        var entity = Placed(world, new(0f, 2f, 6f));
        world.Add(entity, Camera.Perspective);
        Resolve(world);

        system.Extract(world);
        system.Extract(world);

        Assert.Equal(Vector2.Zero, system.Jitter);
        Assert.Equal(Vector2.Zero, view.Camera!.Value.Jitter);

        Assert.Equal(
            CameraMath.ViewProjection(Camera.Perspective, world.Read<WorldTransform>(entity), 16f / 9f),
            view.ViewProjection
        );
    }

    /// <summary>
    ///     ⚠ <b>Eight offsets that repeat, rather than a sequence that never comes back.</b> A history
    ///     at <c>feedback: 0.9</c> holds about twenty frames; if every one carried an offset the
    ///     resolve had never seen, the average would never reach a fixed point and one-pixel geometry
    ///     would keep wobbling. A cycle converges to an exact answer, which is what a screenshot of a
    ///     stationary scene should be.
    /// </summary>
    [Fact]
    public void TheOffsetsRepeatSoAStillCameraCanConverge() {
        using var world = new World();
        var view = new RenderView("Camera");

        var system = new CameraExtractionSystem(view) {
            AspectRatio = 1f,
            JitterTarget = new(64, 64)
        };

        world.Add(Placed(world, Vector3.Zero), Camera.Perspective);
        Resolve(world);

        var seen = new List<Vector2>();

        for (var frame = 0; frame < 8; frame++) {
            system.Extract(world);
            seen.Add(system.Jitter);
        }

        // Eight distinct points inside the pixel, and then the same eight again.
        Assert.Equal(8, seen.Distinct().Count());
        Assert.All(seen, offset => Assert.InRange(offset.X, -0.5f, 0.5f));
        Assert.All(seen, offset => Assert.InRange(offset.Y, -0.5f, 0.5f));

        for (var frame = 0; frame < 8; frame++) {
            system.Extract(world);
            Assert.Equal(seen[frame], system.Jitter);
        }
    }

    /// <summary>A world position through a matrix, divided through — where it lands in NDC.</summary>
    static Vector3 Project(in Matrix4x4 matrix, Vector3 point) {
        var clip = Matrix4x4.TransformVector4(new(point.X, point.Y, point.Z, 1f), matrix);

        return new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    static Entity Placed(World world, Vector3 position) =>
        Hierarchy.CreateTransform(world, LocalTransform.At(position));

    static void Resolve(World world) => new TransformSystem().Resolve(world);
}
