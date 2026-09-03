// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>Two views into one target, which is what a split screen is once it reaches a device.</summary>
/// <remarks>
///     <para>
///         <see cref="RenderSystem" /> was already an N-view machine — one extracted store, a bitset
///         per view index, a work list per (view, stage) — and the editor has drawn four panes
///         through it since #151. What it could not do was draw two of them into <em>one</em> target,
///         because nothing carried a rectangle: the editor gives each pane a texture of its own.
///     </para>
///     <para>
///         ⚠ <b>What is asserted here is what a full-screen frame cannot satisfy.</b> A frame that
///         ignored every rect draws the same number of draws into the same pass and reports the same
///         counters — the difference lives entirely in two <c>SetViewport</c> calls, so those are
///         what the assertions read, at the exact pixels the rects imply.
///     </para>
/// </remarks>
public sealed class SplitScreenViewportTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    public SplitScreenViewportTests() => effects.AddProvider(new AlwaysCompiles());

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Two seats, one pass, one target: the viewport is narrowed to each seat's half in turn.
    /// </summary>
    /// <remarks>
    ///     The target is 16 × 16, so a half-height rect is 16 × 8 — at the top for seat zero and at
    ///     y = 8 for seat one. ⚠ Y measured down from the top edge, unlike clip space, so seat zero
    ///     really is the upper half.
    /// </remarks>
    [Fact]
    public void TwoSeatsNarrowTheViewportToTheirOwnHalfOfOneTarget() {
        using var h = Build();

        AddMesh(h, 10f, h.Opaque.Mask);

        h.Compositor.Game = Pass(
            h,
            new SingleStageRenderer {
                Name = "SeatZero",
                View = Seat(h, "Top", new(0f, 0f, 1f, 0.5f)),
                Stage = h.Opaque
            },
            new SingleStageRenderer {
                Name = "SeatOne",
                View = Seat(h, "Bottom", new(0f, 0.5f, 1f, 0.5f)),
                Stage = h.Opaque
            }
        );

        Frame(h);

        // One object, two views, so two draws — the render system's own N-view behaviour, asserted so
        // that a viewport claim below cannot be satisfied by a frame that drew nothing in one half.
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));

        var narrowed = Viewports().Where(v => v.Height < 16).ToList();

        Assert.Equal(2, narrowed.Count);
        Assert.Equal((16, 8, 0, 0), narrowed[0]);
        Assert.Equal((16, 8, 0, 8), narrowed[1]);
    }

    /// <summary>
    ///     ⚠ The scissor moves with the viewport, and this is the assertion that says so. A viewport
    ///     transforms clip space; it does not clip. Narrowing one and leaving the other at the whole
    ///     target draws seat one's geometry over seat zero's wherever anything crosses the seam —
    ///     which reads as a depth bug rather than as a missing rectangle.
    /// </summary>
    [Fact]
    public void TheScissorFollowsTheViewport() {
        using var h = Build();

        AddMesh(h, 10f, h.Opaque.Mask);

        h.Compositor.Game = Pass(
            h,
            new SingleStageRenderer { View = Seat(h, "Top", new(0f, 0f, 1f, 0.5f)), Stage = h.Opaque }
        );

        Frame(h);

        var scissors = device.Recorder!
            .OfKind(RecordedCommandKind.SetScissor)
            .Select(command => ((int)command.A, (int)command.B, (int)command.C, (int)command.D))
            .Where(rect => rect.Item2 < 16)
            .ToList();

        Assert.Equal((16, 8, 0, 0), Assert.Single(scissors));
    }

    /// <summary>
    ///     ⚠ The narrowing is put back, and this is the sibling it protects. A UI or sky node drawing
    ///     the whole target after two seats would otherwise inherit the second seat's half — a frame
    ///     that draws, keeps every counter healthy, and is wrong in three quarters of the screen.
    /// </summary>
    [Fact]
    public void ASiblingWithNoRectGetsTheWholePassBack() {
        using var h = Build();

        AddMesh(h, 10f, h.Opaque.Mask | h.Transparent.Mask);

        var whole = new RenderView("Whole") { Stages = RenderStageMask.None };

        h.Compositor.Game = Pass(
            h,
            new SingleStageRenderer { View = Seat(h, "Top", new(0f, 0f, 1f, 0.5f)), Stage = h.Opaque },
            new SingleStageRenderer { View = whole, Stage = h.Transparent }
        );

        Frame(h);

        var viewports = Viewports();

        // Narrowed to the half, then put back to the whole 16 × 16 before the sibling records. The
        // sibling itself sets nothing, which is what "no rect" has to mean.
        Assert.Equal(2, viewports.Count);
        Assert.Equal((16, 8, 0, 0), viewports[0]);
        Assert.Equal((16, 16, 0, 0), viewports[1]);
    }

    /// <summary>
    ///     A frame in which no view asks for a region sets no viewport at all, which is exactly what
    ///     every frame this engine already draws did before any of this existed.
    /// </summary>
    /// <remarks>
    ///     The regression assertion. A backend defaults the viewport to the whole attachment when a
    ///     pass opens — <c>VulkanCommandList.BeginRenderPass</c> says so — so an unasked-for
    ///     <c>SetViewport</c> here would be new state on the hot path of every existing frame.
    /// </remarks>
    [Fact]
    public void AFrameWithNoRectsSetsNoViewport() {
        using var h = Build();

        AddMesh(h, 10f, h.Opaque.Mask);

        h.Compositor.Game = Pass(
            h,
            new SingleStageRenderer { View = new RenderView("Camera"), Stage = h.Opaque }
        );

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
        Assert.Empty(Viewports());
    }

    /// <summary>
    ///     ⚠ The rect is a fraction of the <em>target</em>, not of the window, and this is the trap
    ///     that makes it so. A scene plane declared with a render scale below one is smaller than the
    ///     window; a viewport in the window's pixels then runs off its right and bottom edges, which
    ///     rasterises nothing and reports nothing anywhere.
    /// </summary>
    [Fact]
    public void TheRectIsAFractionOfTheTargetRatherThanOfTheWindow() {
        using var h = Build();

        AddMesh(h, 10f, h.Opaque.Mask);

        // The compositor's frame size stays 16 × 16; the pass draws into an 8 × 8 plane, which is a
        // render scale of one half.
        h.Compositor.Game = Sized(
            h,
            8,
            new SingleStageRenderer { View = Seat(h, "Top", new(0f, 0f, 1f, 0.5f)), Stage = h.Opaque }
        );

        Frame(h);

        Assert.Equal(new Int2(16, 16), h.Compositor.FrameSize);
        Assert.Equal((8, 4, 0, 0), Viewports().First(v => v.Height < 8));
    }

    // --- Fixture ------------------------------------------------------------

    List<(int Width, int Height, int X, int Y)> Viewports() =>
        [.. device.Recorder!
            .OfKind(RecordedCommandKind.SetViewport)
            .Select(command => ((int)command.A, (int)command.B, (int)command.C, (int)command.D))];

    /// <summary>A view that draws into part of the target, aimed so the fixture's mesh is visible.</summary>
    static RenderView Seat(Harness h, string name, Rectangle rect) =>
        new(name) { Frustum = h.Frustum, ViewportRect = rect };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            new() {
                Key = key,
                Stages = [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                ]
            };
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderStage Transparent { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }
        public required BoundingFrustum Frustum { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    Harness Build() {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);

        var eye = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new() {
            System = system,
            Compositor = new(system) { FrameSize = new(16, 16) },
            Graph = new(device),
            Opaque = system.AddStage(new("Opaque")),
            Transparent = system.AddStage(new("Transparent")),
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex }),
            Frustum = new(eye * projection)
        };
    }

    static void AddMesh(Harness h, float z, RenderStageMask stages) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = stages,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new Material("Lit"));
    }

    RenderPassRenderer Pass(Harness h, params SceneRenderer[] children) => Sized(h, 16, children);

    RenderPassRenderer Sized(Harness h, int extent, params SceneRenderer[] children) {
        var name = $"Target{extent}#{h.Compositor.Imports.Count}";

        var description = new TextureDescription(
            PixelFormat.Rgba8UNorm,
            extent,
            extent,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);
        h.Compositor.Imports[name] = new(texture, device.CreateTextureView(texture), description);

        var pass = new RenderPassRenderer { Name = $"Scene{extent}" };
        pass.ColourTargets.Add(name);

        foreach (var child in children) {
            pass.Children.Add(child);
        }

        return pass;
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}
