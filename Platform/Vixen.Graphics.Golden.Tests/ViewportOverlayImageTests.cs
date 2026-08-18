// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     An editor's tool overlay drawn over a frame a <see cref="GraphicsCompositor" /> composed, and
///     occluded by the depth that frame wrote.
/// </summary>
/// <remarks>
///     <para>
///         <b>The one thing a compositor-driven viewport has to be able to do, as a picture.</b> The
///         editor's scene pane draws its own shapes today and puts the grid, the markers and the gizmo
///         over them in the same target — so the tools are occluded by the scene because the pane owns
///         both. A pane driven by a compositor owns neither: the picture comes out of a document's
///         nodes, and the question this fixture exists to answer is whether the tools can still be in
///         it and still be behind the things they are behind.
///     </para>
///     <para>
///         They can, and the mechanism is one already in the engine.
///         <see cref="DelegateSceneRenderer" />'s own remarks name "an editor gizmo pass" as what it is
///         for: the overlay is a node appended to the built tree, inside a
///         <see cref="RenderPassRenderer" /> that <em>loads</em> the frame's colour and the frame's
///         depth instead of clearing either. That is the same placement
///         <c>SceneRenderHost.Load</c> already uses for <c>DebugOverlayRenderer</c>, and it survives a
///         document reload for the same reason.
///     </para>
///     <para>
///         ⚠ <b>The depth is what makes it a viewport rather than a decal</b>, and it is the half that
///         cannot be argued — only rendered. The overlay's horizontal line sits <em>behind</em> the
///         composed quad and its vertical line sits in front, so a frame that shares the depth shows
///         the horizontal one interrupted and a frame that does not shows it whole.
///         <see cref="The_overlay_without_the_frames_depth_draws_through_the_scene" /> is that second
///         frame, rendered, so the assertion has something to be a difference from.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ViewportOverlayImageTests {
    /// <summary>How deep the composed quad is, under the engine's reversed-Z convention.</summary>
    /// <remarks>Larger is nearer, and zero is far — which is what both passes clear to.</remarks>
    const float QuadDepth = 0.5f;

    /// <summary>Where the line that must be hidden sits: behind the quad.</summary>
    const float BehindDepth = 0.25f;

    /// <summary>And the one that must not be: in front of it.</summary>
    const float FrontDepth = 0.75f;

    /// <summary>How far from the middle the quad reaches, in clip space.</summary>
    const float QuadExtent = 0.5f;

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>The tools, over a composed frame, behind what they are behind.</summary>
    [Fact]
    public void The_tools_survive_a_compositor_and_are_occluded_by_its_depth() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Compose(owned, sharesDepth: true);

        // The three claims the picture makes, checked before it is trusted — a reference image
        // committed from a first run is a reference to whatever came out first.
        Assert.True(IsQuad(image, 64, 64), "the compositor's own pass drew nothing");
        Assert.True(IsLine(image, 64, Row(BehindLine)) is false, "the hidden line was not occluded");
        Assert.True(IsLine(image, Column(FrontLine), 20), "the front line is missing outside the quad");
        Assert.True(IsLine(image, Column(FrontLine), 64), "the front line is missing over the quad");
        Assert.True(IsLine(image, 12, Row(BehindLine)), "the behind line is missing outside the quad");

        GoldenImage.Verify("viewport-overlay-over-compositor", image, LineTerminals);
    }

    /// <summary>
    ///     <see cref="Tolerance.Flat" />'s channel bound, with one whole pixel of room per line
    ///     endpoint and not one more.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because two conformant drivers disagree about the last fragment of a line, and
    ///         this is measured rather than assumed.</b> The reference was rendered on MoltenVK; on
    ///         lavapipe — the Linux leg's driver, and the only leg that runs these fixtures, since
    ///         macOS and Windows CI have no Vulkan and skip them — exactly one pixel of 16 384
    ///         differs: <c>(83, 6)</c>, amber on lavapipe and background on MoltenVK. That is the
    ///         top end of the vertical segment. Its <c>y = 0.9</c> lands at framebuffer 6.4 under the
    ///         backend's negative-height viewport, so lavapipe produces the fragment for row 6 and
    ///         MoltenVK stops at row 7; both then run to row 121 together. Vulkan's default line
    ///         rasterisation is Bresenham-style and leaves the terminal fragment to the
    ///         implementation, so neither is wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Four pixels, because the picture has four line endpoints</b> — one per end of the
    ///         two segments — and each can differ by at most itself. It is deliberately not
    ///         <see cref="Tolerance.Edges" />, which the suite's other line fixtures use: that allows
    ///         32 whole pixels here, and what this fixture exists to see is a 64-pixel run of the
    ///         cyan segment appearing or disappearing where the quad occludes it. A bound wider than
    ///         the signal is a bound that cannot fail for the reason the fixture was written.
    ///     </para>
    ///     <para>
    ///         The channel bound stays at <see cref="Tolerance.Flat" />'s 2/255: everything here is
    ///         flat colour, and the drivers' worst agreed pixel is one level apart on the cyan
    ///         segment. Occlusion itself is bit-identical between them — the hidden run is columns 32
    ///         to 95 on both, and the segment resumes at 96, which is the quad's extent exactly.
    ///     </para>
    /// </remarks>
    static Tolerance LineTerminals => new(Tolerance.Flat.Channel, 4d / (Fixture.Side * Fixture.Side));

    /// <summary>
    ///     The same overlay with the frame's depth taken away, which is what a preview that lost the
    ///     depth link would draw.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Kept as a rendered frame rather than as a sentence, because it is the only thing that
    ///     makes the fixture above an assertion.</b> Both pictures contain both lines and the same
    ///     quad; they differ in the 64 pixels of row 79 that the quad covers — columns 32 to 95, its
    ///     extent exactly — and a reader who has not seen the second one has no way to know the first
    ///     was not drawn by an overlay that simply ignores depth. That 64 is measured, by comparing
    ///     this frame against the other's reference; it is also what <see cref="LineTerminals" /> is
    ///     sized against, since a tolerance as wide as this run would make the fixture above unable
    ///     to fail.
    /// </remarks>
    [Fact]
    public void The_overlay_without_the_frames_depth_draws_through_the_scene() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Compose(owned, sharesDepth: false);

        // The line that the fixture above hides is whole here, over the middle of the quad.
        Assert.True(IsLine(image, 64, Row(BehindLine)), "the sabotaged frame hid the line anyway");
        Assert.True(IsQuad(image, 64, 40), "the sabotaged frame lost the quad as well, so it proves nothing");
    }

    /// <summary>Renders one quad through a compositor and one overlay node over it.</summary>
    /// <param name="fixture">The device and the graph.</param>
    /// <param name="sharesDepth">
    ///     Whether the overlay pass reads the depth the scene pass wrote. False is the sabotage: the
    ///     pass declares no depth target at all, so the line renderer's untested pipeline is used and
    ///     every segment reaches the target.
    /// </param>
    static Bitmap Compose(Fixture fixture, bool sharesDepth) {
        var device = fixture.Device;
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var system = new RenderSystem();

        var shaders = LineShaders.Default(device);

        fixture.Owns(() => {
            device.Destroy(shaders.Vertex);
            device.Destroy(shaders.Fragment);
        });

        // ⚠ The output the overlay's pipelines are built against has to be the pane's, not a
        // convenient one: a line pipeline created for a depth format the pass does not have is a
        // pipeline the driver refuses, which in a viewport is a grid that stops appearing the day
        // somebody changes the frame's depth buffer.
        var output = sharesDepth
            ? new RenderOutput([display.Description.Format], PixelFormat.Depth32Float)
            : new RenderOutput([display.Description.Format]);

        using var lines = new LineRenderer(device, shaders, output);

        lines.Upload([
            // Behind the quad, all the way across.
            new(new(-0.9f, BehindLine, BehindDepth), Cyan),
            new(new(0.9f, BehindLine, BehindDepth), Cyan),

            // In front of it, all the way down.
            new(new(FrontLine, -0.9f, FrontDepth), Amber),
            new(new(FrontLine, 0.9f, FrontDepth), Amber)
        ]);

        var surface = fixture.Pipeline(
            fixture.Shader("scene.vert.spv", ShaderStage.Vertex),
            fixture.Shader("scene.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Default,
            [
                new VertexBufferLayout(
                    Vertex.Stride,
                    [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)]
                )
            ],
            64,
            targets: [new(display.Description.Format, BlendState.Opaque)]
        );

        var vertices = fixture.Buffer<Vertex>(Vertex.Quad.AsSpan(), BufferUsage.Vertex);
        var indices = fixture.Buffer<ushort>([0, 1, 2, 2, 1, 3], BufferUsage.Index);

        var world = new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(0f, 0f, QuadDepth, 1f)
        );

        var scene = new RenderPassRenderer {
            Name = "Scene",
            DepthTarget = "SceneDepth",

            // Zero is far under reversed-Z, which is what every depth state in the engine agrees on.
            ClearDepth = 0f,
            ClearColour = Background
        };

        scene.ColourTargets.Add("Display");

        scene.Children.Add(
            new DelegateSceneRenderer {
                OnRecord = (_, context) => {
                    context.CommandList.BindPipeline(surface);

                    context.CommandList.PushConstants(
                        ShaderStage.Vertex,
                        0,
                        MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in world))
                    );

                    context.CommandList.BindVertexBuffer(0, vertices);
                    context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt16);
                    context.CommandList.DrawIndexed(6);
                }
            }
        );

        // ⚠ Load rather than Clear on both attachments, which is the whole of the arrangement. The
        // colour is the frame's finished picture and the depth is what the frame wrote — a pass that
        // cleared either would either wipe the composition or throw away the occlusion.
        var overlay = new RenderPassRenderer {
            Name = "Tools",
            Load = LoadAction.Load,
            DepthTarget = sharesDepth ? "SceneDepth" : null,
            DepthLoad = LoadAction.Load,

            // Tested and never written — LineRenderer's own pipeline is `TestOnly`, so the pass can
            // say so and the attachment stays readable by anything after it.
            ReadOnlyDepth = sharesDepth
        };

        overlay.ColourTargets.Add("Display");

        overlay.Children.Add(
            new DelegateSceneRenderer {
                OnRecord = (_, context) =>
                    lines.Record(context.CommandList, Matrix4x4.Identity, depthTested: sharesDepth)
            }
        );

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { scene, overlay } }
        };

        compositor.Resources.Add(
            new() {
                Name = "SceneDepth",
                Format = PixelFormat.Depth32Float,
                Usage = TextureUsage.DepthStencilTarget
            }
        );

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var frame = compositor.Build(fixture.Graph, new EffectSystem(), device);

        return fixture.Render(frame.Texture("harness", "Display"));
    }

    /// <summary>Where the line that has to be hidden runs, in clip space.</summary>
    /// <remarks>
    ///     Off the middle so that a picture flipped in Y is a different picture — the mistake
    ///     <c>CompositorImageTests.AssertGlow</c> records at length.
    /// </remarks>
    const float BehindLine = -0.25f;

    /// <summary>And the one that has to survive.</summary>
    const float FrontLine = 0.3f;

    static Color4 Cyan => new(0.2f, 0.9f, 0.95f, 1f);
    static Color4 Amber => new(1f, 0.65f, 0.1f, 1f);
    static Color4 Background => new(0.06f, 0.07f, 0.09f, 1f);

    /// <summary>Which row a clip-space Y lands on.</summary>
    /// <remarks>
    ///     ⚠ Inverted, because clip <c>y = +1</c> is the top and a bitmap's row zero is too — the
    ///     backend expresses the engine's +Y-up clip space with a negative-height viewport, so a
    ///     larger Y is a smaller row. Writing this the other way round is the failure that makes a
    ///     correct frame look flipped.
    /// </remarks>
    static int Row(float y) => (int)MathF.Round((1f - y) * 0.5f * Fixture.Side);

    /// <summary>Which column a clip-space X lands on.</summary>
    static int Column(float x) => (int)MathF.Round((x + 1f) * 0.5f * Fixture.Side);

    /// <summary>Whether a pixel is the composed surface rather than the background or a line.</summary>
    static bool IsQuad(in Bitmap image, int x, int y) {
        var (r, g, b) = At(image, x, y);

        return r > 0.4f && g < 0.35f && b < 0.35f;
    }

    /// <summary>
    ///     Whether a pixel is one of the overlay's segments — anything that is neither the surface nor
    ///     the background.
    /// </summary>
    /// <remarks>
    ///     A one-pixel line lands on one of two rows depending on how the rasterizer rounds, so the
    ///     neighbouring row counts. Asking for an exact row would be asserting a rasterisation rule
    ///     rather than the occlusion this fixture is about.
    /// </remarks>
    static bool IsLine(in Bitmap image, int x, int y) {
        for (var offset = -1; offset <= 1; offset++) {
            var (r, g, b) = At(image, x, y + offset);

            // Cyan and amber are both far from the red surface and far from the near-black clear;
            // what they have in common is a channel the other two do not reach.
            if (g > 0.5f && (b > 0.5f || r > 0.7f)) {
                return true;
            }
        }

        return false;
    }

    static (float R, float G, float B) At(in Bitmap image, int x, int y) {
        var at = ((y * image.Width) + x) * 4;
        var pixels = image.Pixels;

        return (pixels[at] / 255f, pixels[at + 1] / 255f, pixels[at + 2] / 255f);
    }

    /// <summary>One vertex of the composed surface: a position and a flat colour.</summary>
    /// <remarks>
    ///     The same twenty-eight-byte layout <c>scene.vert.spv</c> reads, declared here rather than
    ///     shared with <c>CompositorImageTests</c> for that file's own reason: changing geometry two
    ///     fixtures depend on moves a reference image for a reason unrelated to what it asserts.
    /// </remarks>
    struct Vertex {
        public const int Stride = 28;

        public Vector3 Position;
        public Vector4 Colour;

        static Vertex At(float x, float y) =>
            new() { Position = new(x, y, 0f), Colour = new(0.8f, 0.15f, 0.15f, 1f) };

        /// <summary>A square in the middle, which both lines cross.</summary>
        public static Vertex[] Quad => [
            At(-QuadExtent, -QuadExtent),
            At(QuadExtent, -QuadExtent),
            At(-QuadExtent, QuadExtent),
            At(QuadExtent, QuadExtent)
        ];
    }
}
