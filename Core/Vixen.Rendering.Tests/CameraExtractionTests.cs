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

    static Entity Placed(World world, Vector3 position) =>
        Hierarchy.CreateTransform(world, LocalTransform.At(position));

    static void Resolve(World world) => new TransformSystem().Resolve(world);
}
