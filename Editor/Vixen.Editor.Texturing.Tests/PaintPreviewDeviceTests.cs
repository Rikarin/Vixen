// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A painted layer's own texels reach the baked map, on a real adapter.
/// </summary>
/// <remarks>
///     <para>
///         <b>The other half of <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>, and
///         the half a compilation test cannot settle.</b> <c>PaintWiringTests</c> asserts that the
///         plan carries a <c>vxpaint:</c> reference; this asserts that something reads the file
///         behind it. The two failures it separates are a compiler that emits a reference nobody
///         resolves — which is the defect #852 is about, one level up — and a resolver that uploads
///         the wrong bytes.
///     </para>
///     <para>
///         ⚠ <b>The oracle is a boundary and not a colour, because a boundary cannot be produced by
///         a mistake.</b> The canvas is opaque on its left half and transparent on its right, over a
///         fill of a different colour: the baked map must change at the middle column and nowhere
///         else. A resolver that uploaded a blank texture bakes the fill everywhere; one that
///         uploaded a constant bakes one colour everywhere; one that never ran at all cannot bake at
///         all, because <c>TexturePlanEvaluator.Evaluate</c> refuses a plan whose external nothing
///         supplied. All three are distinguishable from "the file's texels came back".
///     </para>
///     <para>
///         ⚠ <b>Split on <em>x</em> deliberately.</b> A y split would also be an assertion about
///         which way up a bitmap node reads an external image, which is a different claim with its
///         own home; painting is not the place to discover it.
///     </para>
/// </remarks>
public class PaintPreviewDeviceTests {
    const int Side = 8;

    /// <summary>The canvas's texels are what the map is painted with.</summary>
    [Fact]
    public void A_paint_layers_canvas_is_read_off_disk_and_baked_into_the_map() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LentEvaluator evaluators = new();
        using LayerStackPreview preview = new(fixture.Graphics!, evaluators.Lease);

        var document = Opened(fixture, "Hull");

        Write(fixture, "Hull.paint.vxpaint", "baseColor");

        document.Document = Stack("Hull.paint.vxpaint");

        var shown = preview.Evaluate(document);

        Assert.NotNull(shown.Image);
        Assert.NotNull(fixture.Graphics);

        var picture = fixture.Graphics.Uploads[^1];

        Assert.Equal(Side, picture.Width);

        // ⚠ The instrument, before the claim. The canvas is red where it is opaque and the fill under
        // it is green, so a run in which the two agree is a run in which one of them did not happen.
        var left = Texel(picture, 0, Side / 2);
        var right = Texel(picture, Side - 1, Side / 2);

        Assert.True(
            left.R > 200 && left.G < 60,
            $"{Adapter(device)}: the left half baked to ({left.R}, {left.G}, {left.B}) and the canvas painted "
            + "it opaque red. A blank upload and a canvas nobody read both bake the fill here."
        );

        Assert.True(
            right.G > 200 && right.R < 60,
            $"{Adapter(device)}: the right half baked to ({right.R}, {right.G}, {right.B}) and the canvas is "
            + "transparent there, so the green fill under it is what must show through."
        );

        // And the boundary is where the canvas puts it, not where a resample would.
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var texel = Texel(picture, x, y);

                Assert.True(
                    x < Side / 2 ? texel.R > 200 : texel.G > 200,
                    $"{Adapter(device)}: texel ({x}, {y}) baked to ({texel.R}, {texel.G}, {texel.B}), and the "
                    + $"canvas's edge is at column {Side / 2}."
                );
            }
        }
    }

    /// <summary>A canvas a layer names and nobody wrote is a sentence, not a picture.</summary>
    /// <remarks>
    ///     ⚠ <b>What this proves is that the resolver is consulted at all.</b> Without it the test
    ///     above is satisfied by a build in which painted references are quietly skipped and the
    ///     plan's external is filled by something else — the "what does the gate print on the day it
    ///     does not run" question, asked of the resolver rather than of a gate.
    /// </remarks>
    [Fact]
    public void A_named_canvas_that_is_not_there_is_said_rather_than_drawn() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LentEvaluator evaluators = new();
        using LayerStackPreview preview = new(fixture.Graphics!, evaluators.Lease);

        var document = Opened(fixture, "Hull");

        document.Document = Stack("Missing.paint.vxpaint");

        var shown = preview.Evaluate(document);

        Assert.Null(shown.Image);
        Assert.Contains("Missing.paint.vxpaint", shown.Status, StringComparison.Ordinal);
    }

    /// <summary>A channel the canvas does not hold contributes nothing rather than refusing.</summary>
    /// <remarks>
    ///     The canvas below holds base colour alone, and the stack's paint layer writes roughness
    ///     too. An artist who has painted one channel of a seven-channel set is the ordinary case,
    ///     not an error — so the map still bakes and the unpainted channel shows the fill under it.
    /// </remarks>
    [Fact]
    public void A_channel_the_canvas_has_not_been_painted_on_still_bakes() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        using LentEvaluator evaluators = new();
        using LayerStackPreview preview = new(fixture.Graphics!, evaluators.Lease);

        var document = Opened(fixture, "Hull");

        Write(fixture, "Hull.paint.vxpaint", "baseColor");

        var stack = Stack("Hull.paint.vxpaint");

        stack.Sets[0].Channels.Add(new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] });
        document.Document = stack;

        var shown = preview.Evaluate(document, "roughness");

        Assert.NotNull(shown.Image);

        // The fill under the paint layer, everywhere: the paint layer's roughness canvas is absent,
        // so it covers nothing at all.
        var picture = fixture.Graphics!.Uploads[^1];
        var texel = Texel(picture, 0, 0);

        Assert.True(
            texel.G > 200,
            $"{Adapter(device)}: the roughness map baked to ({texel.R}, {texel.G}, {texel.B}) where the fill "
            + "under an unpainted channel is what must show."
        );
    }

    /// <summary>A green fill under a paint layer that reads a canvas.</summary>
    static LayerStackAsset Stack(string canvas) =>
        new() {
            Name = "Hull",
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 3u,
            Sets = [
                new() {
                    Name = "Default",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "base",
                            Kind = LayerKind.Fill,
                            Fill = LayerFillSource.Constant,
                            Values = { ["baseColor"] = [0f, 1f, 0f, 1f], ["roughness"] = [0f, 1f, 0f, 1f] }
                        },
                        new() { Id = "paint", Kind = LayerKind.Paint, Paint = canvas }
                    ]
                }
            ]
        };

    /// <summary>Writes a canvas beside the stack: opaque red on the left, transparent on the right.</summary>
    static void Write(TexturingFixture fixture, string name, string usage) {
        PaintCanvas canvas = new(Side, Side);
        var image = canvas.Channel(usage);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side / 2; x++) {
                // 0xAABBGGRR — opaque red.
                image[(y * Side) + x] = 0xFF0000FFu;
            }
        }

        using var stream = File.Create(Path.Combine(fixture.Paths.Assets, name));

        canvas.Write(stream);
    }

    /// <summary>An empty stack document beside which a canvas can be written.</summary>
    static LayerStackDocument Opened(TexturingFixture fixture, string name) =>
        new(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, name),
            fixture.Paths.Absolute("Assets/" + name + LayerStackDocument.Extension)
        );

    static (byte R, byte G, byte B) Texel(RecordingGraphics.Uploaded picture, int x, int y) {
        var at = ((y * picture.Width) + x) * 4;

        return (picture.Pixels[at], picture.Pixels[at + 1], picture.Pixels[at + 2]);
    }

    /// <summary>A device, or a loud skip — <c>LayerStackPanelDeviceTests</c>' arrangement.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";
}
