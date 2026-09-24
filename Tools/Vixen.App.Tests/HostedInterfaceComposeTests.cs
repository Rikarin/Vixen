// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Platform.Headless;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;
using DrawCommand = Vixen.Ui.DrawCommand;

namespace Vixen.App.Tests;

/// <summary>A game's own frame names <c>!UiCompose</c> over the window the stock host lends it (#1419).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The host, not a fixture, and that is the whole of the claim.</b> <c>!UiCompose</c> —
///         the node that composes a HUD after the scene so its top-level <c>mix-blend-mode</c> and
///         <c>backdrop-filter</c> read the world (#1378) — was proved in the golden suite, whose
///         fixture declares its own target <c>Sampled</c>. No frame a game draws through
///         <c>AppGraphics</c> could name it: <c>Lend</c> imported the swapchain under the frame's
///         output name as a colour target and nothing else, an import wins over a declaration, and the
///         node refuses a source it cannot sample with a <c>CompositorBindingException</c> at build.
///         So every test here goes through <see cref="VixenApp" />, a published frame document and
///         <c>RunFrame</c>, which is the only way a shipped game's frame is ever built.
///     </para>
///     <para>
///         ⚠ <b>Both devices, and the Null one is the lesser proof.</b> It records and draws nothing, so
///         it says the frame builds and the node runs where the scene is; the Vulkan one — offscreen,
///         because a test has no window — says the picture is the HUD multiplied into the world.
///     </para>
/// </remarks>
public sealed class HostedInterfaceComposeTests : IDisposable {
    const string FrameAddress = "frames/main";

    /// <summary>The HUD panel's grey, in 0–255 sRGB — a multiply's operand, and far from both ends.</summary>
    const int Grey = 128;

    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    /// <summary>On the Null device: a frame naming the node over the lent window builds, and the node composes.</summary>
    /// <remarks>
    ///     Red before #1419 at the build, with the node's own refusal: the lent image was declared a
    ///     colour target only, so <c>source: SceneColour</c> — the node's default and the frame's —
    ///     named a target the node could not sample.
    /// </remarks>
    [Fact]
    public void AStockHostsFrameNamingUiComposeBuildsOverTheWindowItLends() {
        Publish(Document(compose: true));

        using var application = Build([]);

        application.Initialise();
        application.RunFrame();
        application.RunFrame();

        var graphics = application.Services.Graphics!;

        // The instrument: this is the stock host's lent window and not a target of the frame's own —
        // the document declares no resource at all, so the only thing named `SceneColour` is the
        // import, under the name `!StandardFrame` writes too.
        Assert.Equal(Output, new GraphicsOptions().Output);
        Assert.Equal(Output, new StandardFrameAsset().Output);
        Assert.Empty(Document(compose: true).Resources);
        Assert.NotNull(graphics.SwapChain);
        Assert.True(
            (graphics.SwapChain!.Usage & TextureUsage.Sampled) != 0,
            $"the swapchain says it is {graphics.SwapChain.Usage}, which the node cannot read"
        );

        var composer = Composer(graphics);

        Assert.Equal(2, composer.ComposeCount);
        Assert.Equal(0, graphics.Renderer.Ui.Sceneless);
    }

    /// <summary>
    ///     On a real device, through the stock host: a multiplied HUD panel is the grey multiplied into
    ///     the world, where the frame without the node lays the grey down flat.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The oracle is two captures of the same host</b>: the frame with the node and
    ///         nothing mounted is the world alone, and every pixel of the panel in the frame with the
    ///         panel mounted must be <c>grey · world</c> in linear light. The control is the frame
    ///         without the node, which composes in <c>WorldRenderer.Draw</c>'s prologue against
    ///         transparent black — § 5.1 weights the mix by the backdrop's alpha, so that is the grey
    ///         source-over, flat, and <see cref="UiRenderFeature.Sceneless" /> counts it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument has to see a difference first</b>: over a world that is black or
    ///         white under the panel the multiply and the flat grey agree or the multiply is the
    ///         world, so the world under the panel is asserted to be neither before either frame is
    ///         judged.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMultipliedHudPanelIsTheGreyMultipliedIntoTheWorldThroughTheStockHost() {
        var captures = Path.Combine(files.TemporaryDirectory, "captures");

        Directory.CreateDirectory(captures);

        var world = Capture(compose: true, hud: false, captures, "world");

        if (world is null) {
            return;
        }

        var composed = Capture(compose: true, hud: true, captures, "composed")!;
        var prologue = Capture(compose: false, hud: true, captures, "prologue")!;

        Keep("hud-world", world.Picture);
        Keep("hud-composed", composed.Picture);
        Keep("hud-prologue", prologue.Picture);

        var box = Panel(world.Picture.Width, world.Picture.Height);
        var measured = 0;
        var worst = 0;
        var flat = 0;

        for (var y = box.Top + 2; y < box.Bottom - 2; y += 3) {
            for (var x = box.Left + 2; x < box.Right - 2; x += 3) {
                var under = At(world.Picture, x, y);

                // Where the world is black or white under the panel the two answers cannot be told
                // apart, so those pixels are not evidence either way.
                if (Math.Max(under.R, Math.Max(under.G, under.B)) < 24 || Math.Min(under.R, Math.Min(under.G, under.B)) > 240) {
                    continue;
                }

                var right = (R: Multiply(under.R), G: Multiply(under.G), B: Multiply(under.B));
                var over = At(composed.Picture, x, y);
                var control = At(prologue.Picture, x, y);

                worst = Math.Max(worst, Distance(over, right));

                if (Distance(control, (Grey, Grey, Grey)) <= 2) {
                    flat++;
                }

                measured++;
            }
        }

        Assert.True(measured > 200, $"only {measured} panel pixels had a world under them that a multiply could be seen against");
        Assert.True(worst <= 3, $"through the stock host the panel is {worst} codes from the grey multiplied into the world");

        // The control: without the node the same panel is the flat grey at almost every pixel, and the
        // feature says the backdrop had no world in it.
        Assert.True(flat > measured * 9 / 10, $"without the node only {flat} of {measured} panel pixels were the flat grey");
        Assert.Equal(1, prologue.Sceneless);
        Assert.Equal(0, composed.Sceneless);
        Assert.True(composed.Blended > 0, "the panel never went through UiBlend");
        Assert.Equal(0, composed.Unblended);
    }

    /// <summary>What one run of the stock host produced.</summary>
    sealed record Captured(Bitmap Picture, int Sceneless, int Blended, int Unblended);

    /// <summary>Runs the stock host on Vulkan offscreen, optionally with the panel mounted, and captures a frame.</summary>
    Captured? Capture(bool compose, bool hud, string directory, string name) {
        Publish(Document(compose));

        VixenApplication application;

        try {
            application = Build(["--vixen-offscreen", "--vixen-backend", "vulkan", "--vixen-capture", directory]);
        } catch (InvalidOperationException refused) when (!Required) {
            Assert.Skip($"no Vulkan device: {refused.Message}");
            return null;
        }

        using (application) {
            var graphics = application.Services.Graphics!;

            Assert.Contains("Vulkan", graphics.Device.GetType().Name, StringComparison.Ordinal);

            application.Initialise();
            application.RunFrame();

            UiRenderer? ui = null;

            if (hud) {
                var swapChain = graphics.SwapChain!;
                var size = swapChain.Size;

                ui = new UiRenderer(graphics.Device, UiShaderLibrary.Load(graphics.Device), new RenderOutput([swapChain.Format]));

                var stage = graphics.Renderer.Host.Builder.Stages["Ui"];

                // ⚠ The camera's view collects the stage the game draws in and nothing else — see
                // `AppGraphics`' load — so a HUD in a stage of its own is culled until the view is
                // told to collect it too.
                graphics.View.Stages |= stage.Mask;
                graphics.Renderer.Ui.Renderer = ui;

                var atlas = new GlyphAtlas(64, 64);
                var geometry = new UiGeometryBuilder().Build(Panelled(size), new GlyphFieldCache(atlas), new Rectangle(0, 0, size.X, size.Y));

                Assert.Equal(UiBlendMode.Multiply, Assert.Single(geometry.Layers).Blend);

                var id = graphics.Renderer.Ui.Mount(stage.Mask);

                graphics.Renderer.Ui.Set(id, new(geometry, atlas, size, 0));
            }

            for (var frame = 0; frame < 3; frame++) {
                application.RunFrame();
            }

            Assert.True(graphics.RequestCapture(name));
            application.RunFrame();

            var path = graphics.LastCapturePath;

            Assert.NotNull(path);

            var captured = new Captured(
                PngCodec.Load(path!),
                graphics.Renderer.Ui.Sceneless,
                ui?.Blended ?? 0,
                ui?.Unblended ?? 0
            );

            graphics.Device.WaitIdle();
            ui?.Dispose();

            return captured;
        }
    }

    /// <summary>A multiplied grey panel over the middle of the window, as a real group of two rectangles.</summary>
    static DrawList Panelled(Int2 size) {
        var box = Panel(size.X, size.Y);

        // ⚠ Linear, because a `Color4` is — `Grey` is the sRGB code the capture will hold for it.
        var linear = Decode(Grey);

        var list = new DrawList();
        list.BeginFrame();
        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, box.Left, box.Top, box.Width, box.Height, Color4.White, 0, 0) {
                Blend = UiBlendMode.Multiply
            }
        );

        // Two rectangles, so the group is a group: a lone command is folded into a vertex alpha and
        // the builder takes the layer back.
        list.Add(new(DrawCommandKind.Rectangle, box.Left, box.Top, box.Width, box.Height, new Color4(linear, linear, linear, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, box.Left, box.Top, box.Width / 4f, box.Height / 4f, new Color4(linear, linear, linear, 1f), 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        return list;
    }

    static (int Left, int Top, int Right, int Bottom, int Width, int Height) Panel(int width, int height) {
        var left = width / 4;
        var top = height / 4;

        return (left, top, left + (width / 2), top + (height / 2), width / 2, height / 2);
    }

    /// <summary>The name the host lends the window under, and <c>!UiCompose</c>'s default source.</summary>
    const string Output = "SceneColour";

    /// <summary>The world under the HUD: a clear, mid-range and unequal in every channel, in linear light.</summary>
    static readonly Color3 World = new(0.6f, 0.25f, 0.4f);

    /// <summary>
    ///     A scene pass, the node when <paramref name="compose" /> says so, and the pass that draws the
    ///     interface stage — over the window, declaring nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A hand-written frame and not <c>!StandardFrame</c>, because this host cannot draw one.</b>
    ///     Its effects resolve from a shader library a published game carries and this test does not,
    ///     so the sky declares no pass and <c>Main</c> refuses to load a <c>SceneHdr</c> nothing wrote.
    ///     The world here is a clear, which is all a multiply needs to be told from a flat grey: every
    ///     channel of <see cref="World" /> is far from both ends.
    /// </remarks>
    static GraphicsCompositorAsset Document(bool compose) {
        var scene = new RenderPassAsset { Name = "Scene", ColourTargets = [Output], ClearColour = World };

        var hud = new RenderPassAsset {
            Name = "Interface",
            ColourTargets = [Output],
            Loaded = [Output],
            Children = [new SingleStageAsset { Name = "Hud", View = "Camera", Stage = "Ui" }]
        };

        return new() {
            Stages = [new RenderStageAsset { Name = "Ui", SortMode = RenderSortMode.ByGroup }],
            Game = new SequenceAsset {
                Name = "Frame",

                // ⚠ `Source` left at its default, which is the frame's output and the name the host
                // lends the window under — the arrangement a game writes and #1419 refused.
                Children = compose ? [scene, new UiComposeAsset { Name = "Compose" }, hud] : [scene, hud]
            }
        };
    }

    static UiComposeRenderer Composer(AppGraphics graphics) {
        var path = UiComposeRenderer.PathTo(graphics.Renderer.Host.Compositor?.Game, graphics.Renderer.Ui);

        Assert.NotEmpty(path);

        return Assert.IsType<UiComposeRenderer>(path[^1]);
    }

    void Publish(GraphicsCompositorAsset document) =>
        HostedRendererTests.Publish(Path.Combine(files.ApplicationDirectory, ContentMount.FolderName), FrameAddress, document);

    VixenApplication Build(string[] extra) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0", .. extra])
            .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
            .Build(new ComposingGame());

    static bool Required => Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE";

    static (int R, int G, int B) At(Bitmap bitmap, int x, int y) {
        var offset = bitmap.Offset(x, y);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    static int Distance((int R, int G, int B) a, (int R, int G, int B) b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    static int Multiply(int under) => Encode(Decode(Grey) * Decode(under));

    static float Decode(int code) {
        var c = code / 255f;

        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    static int Encode(float linear) {
        var c = linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

        return (int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
    }

    /// <summary>Saves a picture under <c>VIXEN_KEEP_PICTURES</c>, for a person; the assertions are the test.</summary>
    static void Keep(string name, Bitmap picture) {
        if (Environment.GetEnvironmentVariable("VIXEN_KEEP_PICTURES") is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, name + ".png"), picture);
        }
    }

    /// <summary>A game with no window whose frame is the published document.</summary>
    sealed class ComposingGame : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Window = null;
            config.Graphics.Compositor = FrameAddress;
        }
    }
}
