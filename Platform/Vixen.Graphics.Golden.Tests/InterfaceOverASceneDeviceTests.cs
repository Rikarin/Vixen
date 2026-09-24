// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>A <see cref="UiDocument" /> drawn over a standard frame's 3-D scene, on a real device.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#627's picture, which no test, sample or host in the tree had drawn.</b>
///         <c>WorldRenderer.Ui</c> has been registered since batch 12 and every step of its host
///         contract was proved on the Null device by <c>InterfaceInAWorldTests</c> — and "tests pass"
///         is not evidence for a picture. This is the host path end to end: a
///         <c>!StandardFrame</c> expanded by the builder, with a <c>Ui</c> stage sorting
///         <c>ByGroup</c> declared beside it and a pass drawing that stage spliced at the frame's
///         <c>beforeUi</c> seam; the shader table from <see cref="UiShaderLibrary.Load" />, the call
///         both desktop hosts make; a document's own builder producing the geometry; and
///         <c>WorldRenderer.Draw</c> as the only per-frame call — which now uploads and composes the
///         mounted interface itself.
///     </para>
///     <para>
///         ⚠ <b>Closed-form rather than a reference image.</b> The scene is shaded, tonemapped and
///         antialiased, so a committed picture would be pinning the tier frame's drivers as much as
///         the interface. What the interface owes is exact given the scene: a second run of the same
///         frame with nothing mounted is the backdrop, and against it
///     </para>
///     <list type="bullet">
///         <item><description>
///             an opaque panel is its own colour and nothing of the scene — magenta, which the scene
///             is not;
///         </description></item>
///         <item><description>
///             a white panel at half opacity with a red box inside it is <c>0.5 · colour + 0.5 ·
///             backdrop</c> in linear light, per pixel, where the colour is red over the box and white
///             elsewhere — the red does not show the white through, which is what a group is, and the
///             flat walk that skips <c>Compose</c> draws both at full strength;
///         </description></item>
///         <item><description>
///             and every pixel away from both is the scene, unchanged.
///         </description></item>
///     </list>
/// </remarks>
[Collection("Vulkan")]
public sealed class InterfaceOverASceneDeviceTests {
    const int Frames = 2;

    const int Side = Fixture.Side;

    /// <summary>The opaque panel, in document pixels, which are framebuffer pixels here.</summary>
    static readonly (int X, int Y, int Width, int Height) Solid = (8, 8, 48, 24);

    /// <summary>The half-transparent one.</summary>
    static readonly (int X, int Y, int Width, int Height) Faded = (64, 88, 48, 24);

    /// <summary>
    ///     A red box inside the faded panel, in the frame's pixels — the second fragment that makes
    ///     the panel a group worth a surface.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without it there is no group to compose.</b> <c>DrawListBuilder</c> opens a layer
    ///     for every translucent element and takes it back when the subtree comes to one command,
    ///     because fading one command is exactly equal to compositing it — so a lone faded panel
    ///     reaches the device as a vertex alpha and <c>Compose</c> is never asked. The first run of
    ///     this fixture had exactly that and its <c>Assert.Single(Layers)</c> said so.
    /// </remarks>
    static readonly (int X, int Y, int Width, int Height) Inner = (72, 94, 16, 12);

    /// <summary>
    ///     The frame the tier goldens draw, with a stage for the interface and a pass that draws it
    ///     after the last node has written the output.
    /// </summary>
    static GraphicsCompositorAsset Document {
        get {
            var frame = StandardFrameTierImageTests.Frame;

            return new() {
                Stages = [new RenderStageAsset { Name = "Ui", SortMode = RenderSortMode.ByGroup }],
                Game = frame with {
                    Extensions = new() {
                        BeforeUi = [
                            new RenderPassAsset {
                                Name = "Interface",
                                ColourTargets = [frame.Output],
                                Loaded = [frame.Output],
                                Children = [new SingleStageAsset { Name = "Hud", View = "Camera", Stage = "Ui" }]
                            }
                        ]
                    }
                }
            };
        }
    }

    [Fact]
    public void ADocumentIsDrawnOverTheSceneThroughTheWorldRenderer() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Bitmap backdrop;

        using (var scene = StandardFrameTierImageTests.Stage(owned, QualityTier.High, Document)) {
            backdrop = scene.Frames(Frames);
        }

        Bitmap picture;
        int composited;

        using (var scene = StandardFrameTierImageTests.Stage(owned, QualityTier.High, Document)) {
            using var ui = new UiRenderer(
                owned.Device,
                UiShaderLibrary.Load(owned.Device),
                new RenderOutput([PixelFormat.Rgba8UNormSrgb])
            );

            var stage = scene.Stages["Ui"];

            // The view the pass draws from has to collect the stage, exactly as it collects Opaque.
            scene.View.Stages |= stage.Mask;
            scene.Renderer.Ui.Renderer = ui;

            var id = scene.Renderer.Ui.Mount(stage.Mask);
            var (geometry, atlas) = Hud();

            // The instrument: the frame really has a group in it to compose.
            Assert.Single(geometry.Layers);

            scene.Renderer.Ui.Set(id, new(geometry, atlas, new Int2(Side, Side), 0));

            picture = scene.Frames(Frames);
            composited = ui.Composited;
        }

        Keep("interface-over-a-scene.backdrop", backdrop);
        Keep("interface-over-a-scene", picture);

        // The opaque panel: its own colour, whatever the scene behind it was.
        foreach (var (x, y) in Inside(Solid)) {
            var pixel = At(picture, x, y);

            Assert.True(
                pixel is { R: 255, G: 0, B: 255 },
                $"({x}, {y}) inside the opaque panel is {pixel}, not magenta"
            );
        }

        // The faded panel: half of its own colour over the backdrop, in linear light, per pixel —
        // white where only the panel is, and ⚠ half of *red* where the box inside it is. A group's
        // two fragments do not show through each other; faded one at a time instead, the red would
        // be half red over half white over the backdrop, and the flat walk that skips Compose draws
        // both at full strength.
        var worst = 0;
        var measured = 0;

        foreach (var (x, y) in Inside(Faded)) {
            var inner = Near(Inner, x, y);

            if (inner && !Within(Inner, x, y, 2)) {
                continue;
            }

            var under = At(backdrop, x, y);
            var over = At(picture, x, y);
            var colour = inner ? (R: 1f, G: 0f, B: 0f) : (R: 1f, G: 1f, B: 1f);

            worst = Math.Max(worst, Math.Abs(over.R - HalfOver(colour.R, under.R)));
            worst = Math.Max(worst, Math.Abs(over.G - HalfOver(colour.G, under.G)));
            worst = Math.Max(worst, Math.Abs(over.B - HalfOver(colour.B, under.B)));
            measured++;
        }

        Assert.True(measured > 400, $"only {measured} pixels of the faded panel were measured");
        Assert.True(worst <= 3, $"the faded group is {worst} codes from half of itself over the scene");

        // Everything clear of both panels is the scene, untouched.
        var changed = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (Near(Solid, x, y) || Near(Faded, x, y)) {
                    continue;
                }

                var a = At(backdrop, x, y);
                var b = At(picture, x, y);

                if (Math.Abs(a.R - b.R) > 2 || Math.Abs(a.G - b.G) > 2 || Math.Abs(a.B - b.B) > 2) {
                    changed++;
                }
            }
        }

        Assert.Equal(0, changed);

        // ⚠ And the backdrop is a scene rather than a clear: a flat frame would make "the scene is
        // untouched" true of a pass that drew over everything in the backdrop's own colour.
        Assert.NotEqual(At(backdrop, 4, 4), At(backdrop, Side - 4, Side - 4));

        // Last, because the picture is the verdict and this is the diagnosis: a faded group drawn
        // solid is the pixel assertion above failing, and this says which half of the prologue
        // did not run.
        Assert.True(composited > 0, "the faded panel's group was never composed into a surface of its own");
    }

    /// <summary>A multiplied panel at the top level of a HUD, over the 3-D scene: the picture #1378 describes, and its counter.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This pins a defect, so that the day it is fixed is a red line and not a
    ///         silence.</b> <c>WorldRenderer.Draw</c> composes the interface before the scene is
    ///         drawn, so the panel's blend capture holds the interface over transparent black, a
    ///         backdrop of alpha zero weights § 5.1 to nothing, and the panel lands source-over on the
    ///         world: its own grey, where <c>multiply</c> would have darkened the scene by it. The
    ///         closed form for that is exact — the grey's own code at every panel pixel, whatever the
    ///         scene — and the right answer is <c>grey · scene</c> in linear light, which the
    ///         instrument checks is far from it on this scene before anything is asserted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And no counter the renderer has can see it</b>: the panel reads
    ///         <c>UiRenderer.Blended</c> one and <c>Unblended</c> zero, exactly as a correct blend
    ///         would. <c>UiRenderFeature.Sceneless</c> is the one that does, and this is the frame it
    ///         is one on. A fix that gives the group the scene inverts the pixel half to the multiply
    ///         and the counter to zero.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATopLevelBlendedHudPanelLandsSourceOverTheSceneAndIsCountedAsSeeingNone() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Bitmap backdrop;

        using (var scene = StandardFrameTierImageTests.Stage(owned, QualityTier.High, Document)) {
            backdrop = scene.Frames(Frames);
        }

        Bitmap picture;
        int sceneless;
        int blended;
        int unblended;

        using (var scene = StandardFrameTierImageTests.Stage(owned, QualityTier.High, Document)) {
            using var ui = new UiRenderer(
                owned.Device,
                UiShaderLibrary.Load(owned.Device),
                new RenderOutput([PixelFormat.Rgba8UNormSrgb])
            );

            var stage = scene.Stages["Ui"];

            scene.View.Stages |= stage.Mask;
            scene.Renderer.Ui.Renderer = ui;

            var id = scene.Renderer.Ui.Mount(stage.Mask);
            var (geometry, atlas) = Multiplied();

            // The instrument: one group, at the top level, and it is the blended one.
            var layer = Assert.Single(geometry.Layers);
            Assert.Equal(UiBlendMode.Multiply, layer.Blend);

            scene.Renderer.Ui.Set(id, new(geometry, atlas, new Int2(Side, Side), 0));

            picture = scene.Frames(Frames);
            sceneless = scene.Renderer.Ui.Sceneless;
            blended = ui.Blended;
            unblended = ui.Unblended;
        }

        Keep("hud-blend-over-a-scene.backdrop", backdrop);
        Keep("hud-blend-over-a-scene", picture);

        var worst = 0;
        var measured = 0;
        var distinguishable = 0;

        foreach (var (x, y) in Inside(Blended)) {
            if (Near(BlendedInner, x, y)) {
                continue;
            }

            var under = At(backdrop, x, y);
            var over = At(picture, x, y);

            // What multiply over the scene would be, per channel, in linear light.
            var right = (
                R: Encode(Decode(Grey) * Decode(under.R)),
                G: Encode(Decode(Grey) * Decode(under.G)),
                B: Encode(Decode(Grey) * Decode(under.B))
            );

            if (Math.Max(Math.Abs(right.R - Grey), Math.Max(Math.Abs(right.G - Grey), Math.Abs(right.B - Grey))) >= 10) {
                distinguishable++;
            }

            worst = Math.Max(worst, Math.Abs(over.R - Grey));
            worst = Math.Max(worst, Math.Abs(over.G - Grey));
            worst = Math.Max(worst, Math.Abs(over.B - Grey));
            measured++;
        }

        // The instrument: on this scene the right answer and the pinned one are pictures apart.
        Assert.True(measured > 400, $"only {measured} pixels of the panel were measured");
        Assert.True(
            distinguishable > measured / 2,
            $"multiply over this scene is within 10 codes of the grey at {measured - distinguishable} of {measured} pixels, "
            + "so the pin could not tell a scene-aware blend from the source-over it pins"
        );

        // The defect: the grey, unmixed with the scene beneath it.
        Assert.True(worst <= 2, $"the panel is {worst} codes from its own grey; #1378's source-over would be exact");

        // And its visibility: the renderer's counters say a correct blend happened; the feature's says
        // the backdrop had no scene in it.
        Assert.True(blended > 0, "the panel never went through UiBlend, so this is not the arrangement #1378 describes");
        Assert.Equal(0, unblended);
        Assert.Equal(1, sceneless);
    }

    /// <summary>The multiplied panel, in document pixels, which are framebuffer pixels here.</summary>
    static readonly (int X, int Y, int Width, int Height) Blended = (24, 40, 80, 48);

    /// <summary>A box inside it, so the panel's subtree is more than one command and stays a group.</summary>
    static readonly (int X, int Y, int Width, int Height) BlendedInner = (32, 48, 16, 12);

    /// <summary>The panel's grey as an sRGB code: <c>#808080</c>.</summary>
    const byte Grey = 0x80;

    /// <summary>A HUD whose only panel is multiplied onto whatever it lands on.</summary>
    static (UiGeometry Geometry, GlyphAtlas Atlas) Multiplied() {
        var document = new UiDocument(Side, Side);

        document.Load(
            $$"""
            root { width: {{Side}}px; height: {{Side}}px; }
            .blended {
                position: absolute; left: {{Blended.X}}px; top: {{Blended.Y}}px;
                width: {{Blended.Width}}px; height: {{Blended.Height}}px; background-color: #808080;
                mix-blend-mode: multiply;
            }
            .inner {
                position: absolute; left: {{BlendedInner.X - Blended.X}}px; top: {{BlendedInner.Y - Blended.Y}}px;
                width: {{BlendedInner.Width}}px; height: {{BlendedInner.Height}}px; background-color: #ff0000;
            }
            """
        );

        document.Root.Add("div", classNames: "blended").Add("div", classNames: "inner");
        document.Update();
        document.Draw();

        var atlas = new GlyphAtlas(256, 256);

        var geometry = new UiGeometryBuilder().Build(
            document.Drawing,
            new GlyphFieldCache(atlas),
            new Rectangle(0, 0, Side, Side)
        );

        return (geometry, atlas);
    }

    /// <summary>The HUD: a document with an opaque panel and a half-transparent one, built as a host builds it.</summary>
    static (UiGeometry Geometry, GlyphAtlas Atlas) Hud() {
        var document = new UiDocument(Side, Side);

        document.Load(
            $$"""
            root { width: {{Side}}px; height: {{Side}}px; }
            .solid {
                position: absolute; left: {{Solid.X}}px; top: {{Solid.Y}}px;
                width: {{Solid.Width}}px; height: {{Solid.Height}}px; background-color: #ff00ff;
            }
            .faded {
                position: absolute; left: {{Faded.X}}px; top: {{Faded.Y}}px;
                width: {{Faded.Width}}px; height: {{Faded.Height}}px; background-color: #ffffff; opacity: 0.5;
            }
            .inner {
                position: absolute; left: {{Inner.X - Faded.X}}px; top: {{Inner.Y - Faded.Y}}px;
                width: {{Inner.Width}}px; height: {{Inner.Height}}px; background-color: #ff0000;
            }
            """
        );

        document.Root.Add("div", classNames: "solid");
        document.Root.Add("div", classNames: "faded").Add("div", classNames: "inner");
        document.Update();
        document.Draw();

        var atlas = new GlyphAtlas(256, 256);

        // Scale one on both halves — the builder's defaults and the interface's — because the target
        // is 128 pixels a side and so is the document. See `UiRenderFeature.Soft`.
        var geometry = new UiGeometryBuilder().Build(
            document.Drawing,
            new GlyphFieldCache(atlas),
            new Rectangle(0, 0, Side, Side)
        );

        return (geometry, atlas);
    }

    /// <summary>Half of a linear colour composited over a backdrop code, in linear light, as an sRGB code.</summary>
    static int HalfOver(float colour, byte backdrop) => Encode((0.5f * colour) + (0.5f * Decode(backdrop)));

    static float Decode(byte code) {
        var c = code / 255f;

        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    static int Encode(float linear) {
        var c = linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

        return (int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
    }

    /// <summary>A panel's pixels, two in from each edge so the antialiasing fringe is not asked about.</summary>
    static IEnumerable<(int X, int Y)> Inside((int X, int Y, int Width, int Height) box) {
        for (var y = box.Y + 2; y < box.Y + box.Height - 2; y++) {
            for (var x = box.X + 2; x < box.X + box.Width - 2; x++) {
                yield return (x, y);
            }
        }
    }

    /// <summary>Whether a pixel is inside a panel or within two pixels of its edge.</summary>
    static bool Near((int X, int Y, int Width, int Height) box, int x, int y) => Within(box, x, y, -2);

    /// <summary>Whether a pixel is inside a box shrunk by <paramref name="inset" /> on every side.</summary>
    static bool Within((int X, int Y, int Width, int Height) box, int x, int y, int inset) =>
        x >= box.X + inset && x < box.X + box.Width - inset && y >= box.Y + inset && y < box.Y + box.Height - inset;

    static (byte R, byte G, byte B) At(in Bitmap image, int x, int y) {
        var offset = image.Offset(x, y);

        return (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);
    }

    /// <summary>Writes a picture where a person can look at it, when somebody asked for that.</summary>
    /// <remarks>
    ///     Only under <c>VIXEN_KEEP_PICTURES</c>: the assertions above are the test, and a suite that
    ///     wrote files on every run would be leaving litter for a question nobody asked.
    /// </remarks>
    static void Keep(string name, in Bitmap image) {
        if (Environment.GetEnvironmentVariable("VIXEN_KEEP_PICTURES") is { Length: > 0 } directory) {
            PngCodec.Save(Path.Combine(directory, $"{name}.png"), image);
        }
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
