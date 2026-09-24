// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Tests;

/// <summary>A document that names <c>!UiCompose</c> composes its HUD after the scene, over the scene (#1378).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Order, not a counter, is the property.</b> What was wrong with a HUD's top-level blend
///         was <i>when</i> its backdrop was captured: in <c>WorldRenderer.Draw</c>'s prologue, before
///         any pass of the frame, so the capture could only hold the interface over transparent black.
///         The null recorder keeps every render pass's name in submission order, so "the group's
///         surface is rendered after the scene's pass and exactly once" is a statement about the
///         stream that is false before the node exists and false again if the prologue still
///         composes.
///     </para>
///     <para>
///         ⚠ <b>And the scene has to reach the capture, not merely precede it.</b> The blend capture
///         of a top-level group draws <c>UiBackdropSource.Image</c> through a six-index full-screen
///         quad before it replays the interface's prefix; a node that ran at the right moment and
///         handed over no image would put the same pass in the same place and draw nothing under it.
///         So the capture pass is read for that draw, and the plain frame — no node — is the control
///         that says the draw is not there otherwise.
///     </para>
/// </remarks>
public sealed class InterfaceComposedAfterTheSceneTests : IDisposable {
    const int Side = 64;

    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    /// <summary>The frame with the node: a scene pass, the compose, and the pass that draws the HUD.</summary>
    static string Composed(bool enabled = true, bool sequenceEnabled = true) => $$"""
        version: 2
        resources:
          - name: SceneColour
            format: Bgra8UNorm
            usage: ColourTarget, Sampled
        stages:
          - name: Ui
            sortMode: ByGroup
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Scene
              colourTargets: [SceneColour]
            - !Sequence
              name: Hud
              enabled: {{(sequenceEnabled ? "true" : "false")}}
              children:
                - !UiCompose
                  name: Compose
                  enabled: {{(enabled ? "true" : "false")}}
                  source: SceneColour
            - !RenderPass
              name: Interface
              colourTargets: [SceneColour]
              loaded: [SceneColour]
              children:
                - !SingleStage
                  name: HudDraw
                  view: Camera
                  stage: Ui
        """;

    /// <summary>The same frame without it: the prologue composes, as it always did.</summary>
    const string Plain = """
        version: 2
        resources:
          - name: SceneColour
            format: Bgra8UNorm
            usage: ColourTarget, Sampled
        stages:
          - name: Ui
            sortMode: ByGroup
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Scene
              colourTargets: [SceneColour]
            - !RenderPass
              name: Interface
              colourTargets: [SceneColour]
              loaded: [SceneColour]
              children:
                - !SingleStage
                  name: HudDraw
                  view: Camera
                  stage: Ui
        """;

    [Fact]
    public void ANamedComposeRendersTheHudsGroupsAfterTheSceneOverTheScene() {
        var frame = Frame(Composed());

        // The instrument: the scene pass and the group's own surface pass are both in the stream.
        var scene = frame.Passes.IndexOf("Scene");
        var surface = frame.Passes.IndexOf("ui layer 0");

        Assert.True(scene >= 0, "the scene pass was never recorded: " + string.Join(", ", frame.Passes));
        Assert.True(surface >= 0, "the group was never composed: " + string.Join(", ", frame.Passes));

        // After the scene, and once — the prologue left it to the node.
        Assert.True(surface > scene, "the group was composed before the scene: " + string.Join(", ", frame.Passes));
        Assert.Single(frame.Passes, name => name == "ui layer 0");
        Assert.Equal(1, frame.ComposeCount);

        // Over the scene: its blend capture drew the scene under the prefix.
        Assert.True(frame.SceneDrawnUnderTheCapture, "the blend capture drew no full-screen quad of the scene");

        // And the counter that says a HUD could not see the world reads nothing, because it could.
        Assert.Equal(0, frame.Sceneless);
    }

    /// <summary>The control: with no node the prologue composes, before the scene and blind to it.</summary>
    [Fact]
    public void WithoutTheNodeTheHudIsComposedBeforeTheSceneAndCounted() {
        var frame = Frame(Plain);

        var scene = frame.Passes.IndexOf("Scene");
        var surface = frame.Passes.IndexOf("ui layer 0");

        Assert.True(scene >= 0 && surface >= 0, string.Join(", ", frame.Passes));
        Assert.True(surface < scene, "the prologue's compose came after the scene: " + string.Join(", ", frame.Passes));
        Assert.False(frame.SceneDrawnUnderTheCapture);
        Assert.Equal(1, frame.Sceneless);
    }

    /// <summary>
    ///     A node that will not run leaves the compose to the prologue — its own flag off, or a
    ///     sequence above it off, which does not run its children either.
    /// </summary>
    /// <remarks>
    ///     ⚠ The second case is the one a check of the node's own flag would get wrong: the frame
    ///     would skip the prologue's compose for a node that never runs, and the HUD's groups would
    ///     be drawn from surfaces nothing had filled this frame.
    /// </remarks>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ANodeThatDoesNotRunLeavesTheComposeToThePrologue(bool enabled, bool sequenceEnabled) {
        var frame = Frame(Composed(enabled, sequenceEnabled));

        Assert.Equal(0, frame.ComposeCount);
        Assert.Single(frame.Passes, name => name == "ui layer 0");
        Assert.True(frame.Passes.IndexOf("ui layer 0") < frame.Passes.IndexOf("Scene"), string.Join(", ", frame.Passes));
    }

    /// <summary>A source that cannot be sampled is refused when the frame is built, naming the fix.</summary>
    [Fact]
    public void ASourceThatCannotBeSampledIsRefused() {
        var unsampled = Composed().Replace("usage: ColourTarget, Sampled", "usage: ColourTarget", StringComparison.Ordinal);

        var refused = Assert.Throws<CompositorBindingException>(() => Frame(unsampled, sampled: false));

        Assert.Contains("Sampled", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A scene target of another size than the interface is said in the node's <c>Degraded</c>, and
    ///     "the interface's size" is the one <c>UiRenderer</c> actually allocates its surfaces at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>At a fractional density, because that is where two roundings disagree.</b> A 43-unit
    ///         interface at 1.5 is 64.5 framebuffer pixels: <c>UiRenderer.Compose</c> takes the ceiling
    ///         and allocates 65, and a check that truncated or rounded — to even, as
    ///         <c>MathF.Round</c> does — would call a 64-pixel scene a match and the 65-pixel one that
    ///         really is a mismatch. At a whole density every rounding agrees and the arithmetic is
    ///         untested.
    ///     </para>
    ///     <para>
    ///         The instrument is the group's own surface pass: the group covers the whole interface, so
    ///         the pass's render area is clamped by nothing but the surface's edge — <c>UiRenderer.Pixels</c>,
    ///         the function the surface is allocated by and the one the node compares with — and it is
    ///         read out of the recorded stream rather than restated here. ⚠ Before that function existed
    ///         the allocation, the clamp and the comparison were three copies of one line, and rounding
    ///         the allocation alone left this test green: nothing on the null device reports a
    ///         texture's size.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(65, false)]
    [InlineData(64, true)]
    [InlineData(66, true)]
    public void ASceneOfAnotherSizeThanTheInterfaceIsReportedAsTheNodesDegrade(int target, bool mismatched) {
        const int Units = 43;
        const float Scale = 1.5f;

        var frame = Frame(Composed(), scene: target, units: Units, scale: Scale);

        // The instrument: the node ran, and the surface it composed into is 65 pixels a side.
        Assert.Equal(1, frame.ComposeCount);
        Assert.Equal(new Int2(65, 65), frame.LayerArea);

        if (mismatched) {
            Assert.NotNull(frame.Degraded);
            Assert.Contains($"65×65 pixels and the scene beneath it is {target}×{target}", frame.Degraded, StringComparison.Ordinal);
        } else {
            Assert.Null(frame.Degraded);
        }
    }

    /// <summary>What one frame recorded.</summary>
    sealed record Recorded(List<string> Passes, bool SceneDrawnUnderTheCapture, int Sceneless, int ComposeCount) {
        /// <summary>The compose node's <c>Degraded</c> after the frame, or null for none or no node.</summary>
        public string? Degraded { get; init; }

        /// <summary>The render area of the first group's surface pass, or null when it had none.</summary>
        public Int2? LayerArea { get; init; }
    }

    /// <summary>Loads <paramref name="document" />, mounts a HUD with one multiplied top-level group, and draws a frame.</summary>
    /// <param name="document">The frame document.</param>
    /// <param name="sampled">Whether the imported <c>SceneColour</c> is declared <c>Sampled</c>.</param>
    /// <param name="scene">The imported target's side, in pixels.</param>
    /// <param name="units">The interface's side, in geometry units.</param>
    /// <param name="scale">The interface's density.</param>
    Recorded Frame(string document, bool sampled = true, int scene = Side, int units = Side, float scale = 1f) {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);
        using var ui = UiRendererFor(device);

        var view = Camera();

        renderer.Host.Builder.Views["Camera"] = view;
        renderer.Host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(document));
        renderer.Host.FrameSize = new(Side, Side);

        // The frame's last target belongs to somebody outside the graph, or the graph would be right
        // to cull the interface pass that writes it last.
        var description = new TextureDescription {
            Width = scene,
            Height = scene,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            SampleCount = 1,
            Format = PixelFormat.Bgra8UNorm,
            Usage = sampled ? TextureUsage.ColourTarget | TextureUsage.Sampled : TextureUsage.ColourTarget
        };

        var target = device.CreateTexture(description);

        renderer.Host.Import(
            "SceneColour",
            new(target, device.CreateTextureView(target), description, ResourceState.Undefined, ResourceState.ShaderRead)
        );

        var stage = renderer.Host.Builder.Stages["Ui"];

        view.Stages |= stage.Mask;
        renderer.Ui.Renderer = ui;

        var id = renderer.Ui.Mount(stage.Mask);
        var atlas = new GlyphAtlas(64, 64);
        var geometry = Multiplied(atlas, units);

        renderer.Ui.Set(id, new(geometry, atlas, new Int2(units, units), 0) { Scale = scale });

        var commands = device.BeginCommandList(QueueKind.Graphics, "frame");

        renderer.Draw(commands);
        commands.Finish();
        device.Recorder!.Clear();
        device.GraphicsQueue.Submit([commands]);

        var passes = new List<string>();
        var layerArea = default(Int2?);
        var drawnUnder = false;
        var inCapture = false;
        var drawsInCapture = 0;

        foreach (var command in device.Recorder.Commands) {
            if (command.Kind == RecordedCommandKind.BeginRenderPass) {
                passes.Add(command.Text ?? "");

                // The render area is packed width-high, height-low; zero is "the whole target".
                if (command.Text == "ui layer 0" && command.E != 0) {
                    layerArea = new Int2((int)(command.E >> 32), (int)(uint)command.E);
                }

                inCapture = command.Text == "ui blend backdrop 0";
                drawsInCapture = 0;
            } else if (command.Kind == RecordedCommandKind.EndRenderPass) {
                inCapture = false;
            } else if (inCapture && command.Kind == RecordedCommandKind.DrawIndexed) {
                // ⚠ The first draw of the capture, and six indices: the full-screen quad goes down
                // before the prefix replay, whose own first draw is the frame's geometry.
                if (drawsInCapture == 0 && command.A == 6 && command.C == 0) {
                    drawnUnder = true;
                }

                drawsInCapture++;
            }
        }

        var composer = UiComposeRenderer.PathTo(renderer.Host.Compositor!.Game, renderer.Ui).LastOrDefault() as UiComposeRenderer;

        return new(passes, drawnUnder, renderer.Ui.Sceneless, composer?.ComposeCount ?? 0) {
            Degraded = composer?.Degraded,
            LayerArea = layerArea
        };
    }

    /// <summary>One top-level group, multiplied, of two rectangles so it stays a group.</summary>
    /// <param name="atlas">The atlas the geometry's glyphs would go in.</param>
    /// <param name="units">The interface's side, in geometry units.</param>
    /// <remarks>
    ///     At the default side the group is a panel in the top-left; at any other it covers the whole
    ///     interface, so the pass that renders its surface is confined by nothing but the surface's
    ///     own edge and its render area is the size <c>UiRenderer</c> allocated.
    /// </remarks>
    static UiGeometry Multiplied(GlyphAtlas atlas, int units = Side) {
        var (x, y, width, height) = units == Side ? (8f, 8f, 40f, 30f) : (0f, 0f, units, (float)units);
        var list = new DrawList();

        list.BeginFrame();
        list.Add(
            new Vixen.Ui.DrawCommand(DrawCommandKind.LayerPush, x, y, width, height, new Color4(1f, 1f, 1f, 1f), 0f, 0f) {
                Blend = UiBlendMode.Multiply
            }
        );
        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.Rectangle, x, y, width, height, Color4.White, 0f, 0f));
        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.Rectangle, 12f, 12f, 10f, 8f, new Color4(1f, 0f, 0f, 1f), 0f, 0f));
        list.Add(new Vixen.Ui.DrawCommand(DrawCommandKind.LayerPop, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f));
        list.EndFrame();

        var geometry = new UiGeometryBuilder().Build(list, new GlyphFieldCache(atlas), new Rectangle(0, 0, units, units));

        // The instrument: one group, and it is the blended one.
        Assert.Equal(UiBlendMode.Multiply, Assert.Single(geometry.Layers).Blend);

        return geometry;
    }

    /// <remarks>
    ///     ⚠ <c>Blend</c> is set as well as <c>Image</c>: without a blend shader the renderer makes no
    ///     blend capture at all, so there would be no pass for the scene to be drawn into.
    /// </remarks>
    static UiRenderer UiRendererFor(NullDevice device) =>
        new(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui box"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui text"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui solid")
            ) {
                Image = device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui image"),
                Blend = device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "ui blend")
            },
            new RenderOutput([PixelFormat.Bgra8UNorm])
        );

    static RenderView Camera() {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };
    }

    public void Dispose() => device.Dispose();
}
