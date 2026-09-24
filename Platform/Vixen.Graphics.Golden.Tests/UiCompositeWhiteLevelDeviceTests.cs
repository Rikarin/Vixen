// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>A composited group on the device, in a pass whose white is BT.2408's 203 cd/m² rather than one.</summary>
/// <remarks>
///     <para>
///         <b>#1418.</b> The three quads that composite a group's already-drawn surface — the composite
///         itself, the drop shadow and the filtered backdrop — sent their white tint through
///         <c>UiGeometryBuilder.Show</c>, which lights every colour by the white level. Every stage that
///         draws them multiplies the sampled surface by that tint, and the surface was written by the
///         same pipelines at the same white, so a group on a float pass came out lit twice: 203 times
///         too bright. <c>SoftwareUiRasterizer</c> reads only the tint's alpha, so the two executors
///         agreed at a white of one — the only white any device test drew a group at.
///     </para>
///     <para>
///         ⚠ <b>The colour matrix is the other half, and fixing the tint alone would have regressed
///         it.</b> <c>UiComposite.Filter</c> and <see cref="UiColorMatrix.Apply(Color4)" /> clamp to
///         the alpha — correct for a white of one, a cap at one candela for any other — and add the
///         offset at the alpha, so <c>invert</c> and a drop shadow's colour, which are offsets, landed
///         a factor of the white too dark on both executors. The double-lit tint happened to re-light a
///         shadow on the device, which made the shadow the one composite that was right there by
///         accident. Both now normalise by the white and re-light.
///     </para>
///     <para>
///         ⚠ <b>The oracle is the frame's own at a white of one, scaled.</b> Everything in these
///         fixtures is drawn by the interface — the field too, and the clear is transparent black — so
///         <see cref="UiGeometryBuilder.WhiteLevel" /> is a pure change of units and the frame at 203
///         must be 203 times the frame at one, pixel for pixel, in every channel but alpha. Every
///         mode, matrix and mask the renderer applies is homogeneous in the white by construction
///         (#1209's <c>Every_mode_scales_with_the_frames_white</c> holds <see cref="UiBlend.Apply" />
///         to it), so a pixel that fails this is a unit error and not a rounding. The software
///         executor at 203 is held to the same frame, which is the independent half.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiCompositeWhiteLevelDeviceTests {
    const int Side = Fixture.Side;

    /// <summary>BT.2408's reference white, which is what <see cref="UiRenderer.WhiteLevelFor" /> hands a float pass.</summary>
    const float DiffuseWhite = 203f;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>The clear: transparent black, the one colour every white level agrees on.</summary>
    static readonly Color4 Clear = new(0f, 0f, 0f, 0f);

    /// <summary>What the group lands on, drawn by the interface so that it scales with the frame.</summary>
    static readonly Color4 Field = new(0.8f, 0.3f, 0.55f, 1f);

    /// <summary>The group's own paint, a different hue and luma from the field.</summary>
    static readonly Color4 Paint = new(0.2f, 0.9f, 0.4f, 1f);

    static readonly Color4 Grey = new(0.5f, 0.5f, 0.5f, 1f);

    const float Opacity = 0.8f;

    public static TheoryData<string> Arrangements() => ["opacity", "blend", "filter", "shadow", "backdrop", "mask"];

    /// <summary>Each composite arrangement at a white of 203 is the same arrangement at one, times 203.</summary>
    [Theory]
    [MemberData(nameof(Arrangements))]
    public void ACompositedGroupAtAWhiteAboveOneIsLitOnce(string arrangement) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = Group(arrangement);

        var (one, _) = Frame(owned, list, 1f, $"white-1-{arrangement}");
        var (lit, renderer) = Frame(owned, list, DiffuseWhite, $"white-203-{arrangement}");

        // The instrument first: the arrangement took the path it names, so a frame that declined it
        // to a plain composite is not what is being measured.
        Assert.Equal(1, renderer.Composited);

        switch (arrangement) {
            case "blend":
                Assert.Equal(1, renderer.Blended);
                Assert.Equal(0, renderer.Unblended);
                break;
            case "filter":
                Assert.Equal(1, renderer.Filtered);
                break;
            case "shadow":
                Assert.Equal(1, renderer.Shadowed);
                break;
            case "backdrop":
                Assert.Equal(1, renderer.Backdropped);
                break;
            case "mask":
                Assert.Equal(1, renderer.Masked);
                break;
        }

        // And the fixture can see a unit error at all: the middle is the group, it is not black, and
        // it differs from the bare field — a group that never drew would satisfy the scaling below
        // exactly as well as one that drew right.
        var middle = At(one, Side / 2, Side / 2);
        var field = At(one, 4, 4);

        Assert.True(middle.X + middle.Y + middle.Z > 0.05f, $"{arrangement}: the group's middle is black at a white of one");
        Assert.True(
            MathF.Abs(middle.X - field.X) + MathF.Abs(middle.Y - field.Y) + MathF.Abs(middle.Z - field.Z) > 0.05f,
            $"{arrangement}: the group's middle {middle} is the bare field {field}, so nothing composited"
        );

        if (arrangement == "opacity") {
            // An independent closed form for the plainest case, so the scaling below is not the only
            // statement: the group's paint at its opacity over the field, in cd/m².
            var expected = ((Paint.G * Opacity) + (Field.G * (1f - Opacity))) * DiffuseWhite;
            Assert.True(
                MathF.Abs(At(lit, Side / 2, Side / 2).Y - expected) <= expected * 0.01f,
                $"the opacity group's middle is {At(lit, Side / 2, Side / 2).Y} cd/m² in green where the closed form is {expected}"
            );
        }

        AssertScaled(arrangement, "the device", one, lit);

        // The independent executor, at the same white, over the same geometry.
        var geometry = Build(list, DiffuseWhite, out var atlas);
        var software = SoftwareUiRasterizer.RenderLinear(geometry, atlas, Side, Side, Clear);

        AssertScaled(arrangement, "the software renderer", one, ToPixels(software));
    }

    public static TheoryData<UiBlendMode> Modes() {
        var data = new TheoryData<UiBlendMode>();

        foreach (var mode in Enum.GetValues<UiBlendMode>()) {
            if (mode != UiBlendMode.Normal) {
                data.Add(mode);
            }
        }

        return data;
    }

    /// <summary>Every <c>mix-blend-mode</c> at a white of 203 is the same blend at one, times 203 — on the device and in software.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>#783's white-level half, which #1418 was blocking.</b> <c>UiBlend</c> normalises both
    ///         operands by the white it is pushed and re-lights the answer — <see cref="UiBlend.Apply" />
    ///         transcribed — and <c>UiBlendDeviceTests</c> held all fifteen modes to § 5.1 only at a
    ///         white of one, where the normalisation is the identity and a stage that dropped it, or
    ///         read the white from the wrong lane, draws the same picture. At 203 it does not: a
    ///         <c>multiply</c> that forgot the white squares the units, and one handed a white of one
    ///         clamps both operands to a candela.
    ///     </para>
    ///     <para>
    ///         The instrument: on these operands each mode is not source-over at a white of one, so a
    ///         frame that stopped blending — rather than blending at the wrong white — is not what
    ///         passes the scaling.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryBlendModeAtAWhiteAboveOneIsLitOnce(UiBlendMode mode) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = Group($"blend-{mode}");

        var (plain, _) = Frame(owned, Group("opacity"), 1f, $"white-1-plain-{mode}");
        var (one, _) = Frame(owned, list, 1f, $"white-1-{mode}");
        var (lit, renderer) = Frame(owned, list, DiffuseWhite, $"white-203-{mode}");

        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var blended = At(one, Side / 2, Side / 2);
        var over = At(plain, Side / 2, Side / 2);

        Assert.True(
            MathF.Max(MathF.Abs(blended.X - over.X), MathF.Max(MathF.Abs(blended.Y - over.Y), MathF.Abs(blended.Z - over.Z))) > 0.02f,
            $"{mode} is source-over on these operands at a white of one ({blended} against {over}), so the scaling cannot tell it ran"
        );

        // ⚠ Away from the rectangles' edges, and the software executor against its own frame at one
        // rather than the device's. `hue` is discontinuous at a grey source: § 5.3's `SetSat` gives any
        // colour with non-zero saturation the backdrop's whole saturation, so a grey texel beside the
        // paint that picks up a rounding's worth of it — the device's bilinear tap a hair off a texel
        // centre on a float surface, or the software's own at a different white — comes out in the
        // paint's hue at full strength. That is a one-pixel fringe at every white, and neither a unit
        // error nor this test's to hold; it is filed on its own. Measured on the RTX 4060 Ti: column 88
        // at (0.293, 0.593, 0.360) on the device against (0.542, 0.442, 0.492) in software, at a white
        // of one and at 203 alike.
        AssertScaled(mode.ToString(), "the device", one, lit, Edge);

        var geometry = Build(list, DiffuseWhite, out var atlas);
        var softOne = ToPixels(SoftwareUiRasterizer.RenderLinear(Build(list, 1f, out var atlasOne), atlasOne, Side, Side, Clear));

        AssertScaled(mode.ToString(), "the software renderer", softOne, ToPixels(SoftwareUiRasterizer.RenderLinear(geometry, atlas, Side, Side, Clear)), Edge);
    }

    public static TheoryData<string> GlassArrangements() => ["rounded", "masked"];

    /// <summary>
    ///     A scene above the white, in cd/m² and premultiplied: what a HUD's glass reads through
    ///     <c>!UiCompose</c> over an HDR world, handed over here as the host's clear.
    /// </summary>
    static readonly Color4 Scene = new(650f, 244f, 447f, 1f);

    /// <summary>A glass panel with no matrix passes a scene above the frame's white through, whichever module draws it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Found reviewing #1418, which narrowed it rather than caused it.</b> A glass panel
    ///         with no <c>backdrop-filter</c> matrix — <c>blur()</c> and nothing else — reaches the image
    ///         pipeline when it is square, <c>UiColour</c> when it is rounded (for its box) and
    ///         <c>UiMask</c> when it is masked (for its ramp). The last two ran the identity through
    ///         <c>UiComposite.Filter</c>, whose clamp to the alpha times the white is not the identity
    ///         on a colour above the white: a rounded glass panel over an HDR world capped every
    ///         highlight behind it at 203 cd/m², where the square one next to it did not. Before #1418
    ///         the ceiling was one candela, re-lit by the double-lit tint.
    ///         <c>SoftwareUiRasterizer</c> skips an identity matrix and never clamped.
    ///     </para>
    ///     <para>
    ///         The oracle is a closed form for the rounded panel's middle — the translucent grey over
    ///         the scene, <c>0.125·w + 0.75·scene</c> — which the square panel on the image pipeline is
    ///         held to first, so the fixture is shown able to carry a colour above the white at all;
    ///         and the software executor for both, across the panel's middle.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(GlassArrangements))]
    public void AGlassPanelWithNoMatrixPassesASceneAboveTheWhite(string arrangement) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var (square, squared) = Frame(owned, Glass("square"), DiffuseWhite, "glass-square", Scene);
        var (glass, renderer) = Frame(owned, Glass(arrangement), DiffuseWhite, $"glass-{arrangement}", Scene);

        // The instrument: every panel is a backdrop with no matrix, the square one took the image
        // pipeline and the other the module its arrangement names.
        foreach (var counted in new[] { squared, renderer }) {
            Assert.Equal(1, counted.Backdropped);
            Assert.Equal(0, counted.Filtered);
            Assert.Equal(0, counted.SquareBackdrops);
        }

        Assert.Equal(0, squared.Masked);
        // Two for the masked panel: the backdrop quad and the group's own composite both take the ramp.
        Assert.Equal(arrangement == "masked" ? 2 : 0, renderer.Masked);

        var expected = new Vector3(
            (0.125f * DiffuseWhite) + (0.75f * Scene.R),
            (0.125f * DiffuseWhite) + (0.75f * Scene.G),
            (0.125f * DiffuseWhite) + (0.75f * Scene.B)
        );

        Assert.True(expected.X > DiffuseWhite * 2f, "the closed form is not above the white, so no ceiling there could show");
        AssertNear("the square panel on the image pipeline", At(square, Side / 2, Side / 2), expected);

        var geometry = Build(Glass(arrangement), DiffuseWhite, out var atlas);
        var software = ToPixels(SoftwareUiRasterizer.RenderLinear(geometry, atlas, Side, Side, Scene));

        if (arrangement == "rounded") {
            AssertNear("the rounded panel", At(glass, Side / 2, Side / 2), expected);
        } else {
            Assert.True(
                At(software, Side / 2, Side / 2).X > DiffuseWhite * 1.5f,
                $"the software executor's masked middle {At(software, Side / 2, Side / 2)} is not above the white"
            );
        }

        // Both against the independent executor across the panel's middle, away from the corners and
        // the rectangle's edge.
        for (var y = 44; y < 84; y++) {
            for (var x = 44; x < 84; x++) {
                var reference = At(software, x, y);

                AssertNear($"{arrangement} against the software executor at ({x}, {y})", At(glass, x, y), new(reference.X, reference.Y, reference.Z));
            }
        }

        static void AssertNear(string what, Vector4 actual, Vector3 expected) {
            var error = MathF.Max(
                MathF.Abs(actual.X - expected.X) / expected.X,
                MathF.Max(MathF.Abs(actual.Y - expected.Y) / expected.Y, MathF.Abs(actual.Z - expected.Z) / expected.Z)
            );

            Assert.True(error <= 0.02f, $"{what}: {actual} where {expected} was expected (relative error {error:0.###})");
        }
    }

    /// <summary>A glass panel — <c>backdrop-filter: blur(2px)</c>, no matrix — square, rounded by sixteen, or square with a mask.</summary>
    static DrawList Glass(string arrangement) {
        var radius = arrangement == "rounded" ? 16 : 0;

        var list = new DrawList();
        list.BeginFrame();

        var push = new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, Color4.White, radius, 0) {
            Backdrop = new UiBackdrop(2f, 1f)
        };

        if (arrangement == "masked") {
            push = push with {
                Offset = list.AddMasks([
                    new UiMask(
                        new Vector2(Side / 2f, Side / 2f),
                        new Vector2(40f, 40f),
                        Vector2.UnitX,
                        new Vector3(0.5f, 0.5f, 0.5f),
                        GradientStops.Default,
                        GradientShape.Linear,
                        Via: false
                    )
                ]),
                Length = 1
            };
        }

        list.Add(push);
        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 0.25f), radius, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        return list;
    }

    /// <summary>Whether a pixel is within two of an edge of <see cref="Group" />'s rectangles, where <c>hue</c> is ill-conditioned.</summary>
    static bool Edge(int x, int y) {
        static bool Near(int value) => Math.Abs(value - 24) <= 2 || Math.Abs(value - 40) <= 2 || Math.Abs(value - 88) <= 2 || Math.Abs(value - 104) <= 2;

        return Near(x) || Near(y);
    }

    /// <summary>Asserts every pixel of <paramref name="lit" /> is <paramref name="one" /> times the white, alpha unchanged.</summary>
    static void AssertScaled(string arrangement, string executor, Vector4[] one, Vector4[] lit, Func<int, int, bool>? skip = null) {
        var worst = 0f;
        var worstAt = (0, 0);
        var worstPair = (default(Vector4), default(Vector4));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (skip is not null && skip(x, y)) {
                    continue;
                }

                var a = one[(y * Side) + x];
                var b = lit[(y * Side) + x];

                // Relative in the colour, with a floor of a hundredth of the white so a near-black
                // pixel's rounding is not read as a ratio. Alpha is a coverage and does not scale.
                var error = MathF.Max(
                    MathF.Max(Relative(b.X, a.X * DiffuseWhite), Relative(b.Y, a.Y * DiffuseWhite)),
                    MathF.Max(Relative(b.Z, a.Z * DiffuseWhite), MathF.Abs(b.W - a.W) * 100f)
                );

                if (error > worst) {
                    worst = error;
                    worstAt = (x, y);
                    worstPair = (a, b);
                }
            }
        }

        Assert.True(
            worst <= 0.02f,
            $"{arrangement}: {executor} at a white of {DiffuseWhite} is not the white-one frame times the white — worst at "
            + $"{worstAt}: {worstPair.Item2} against {worstPair.Item1} × {DiffuseWhite} (relative error {worst:0.###})"
        );

        static float Relative(float actual, float expected) =>
            MathF.Abs(actual - expected) / MathF.Max(MathF.Abs(expected), DiffuseWhite * 0.01f);
    }

    /// <summary>The field, and one group over its middle carrying <paramref name="arrangement" />.</summary>
    static DrawList Group(string arrangement) {
        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        var push = new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0);

        push = arrangement switch {
            _ when arrangement.StartsWith("blend-", StringComparison.Ordinal) =>
                push with { Blend = Enum.Parse<UiBlendMode>(arrangement["blend-".Length..]) },
            "opacity" => push,
            "blend" => push with { Blend = UiBlendMode.Multiply },
            "filter" => push with { Filter = UiColorMatrix.Invert(1f) },
            "shadow" => push with { Shadow = new UiDropShadow(new Vector2(8f, 8f), 0f, new Color4(0.1f, 0.6f, 0.9f, 1f)) },
            "backdrop" => push with { Backdrop = new UiBackdrop(0f, 1f, UiColorMatrix.Invert(1f)) },
            "mask" => push with {
                Offset = list.AddMasks([
                    new UiMask(
                        new Vector2(Side / 2f, Side / 2f),
                        new Vector2(40f, 40f),
                        Vector2.UnitX,
                        new Vector3(0.5f, 0.5f, 0.5f),
                        GradientStops.Default,
                        GradientShape.Linear,
                        Via: false
                    )
                ]),
                Length = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(arrangement), arrangement, null)
        };

        list.Add(push);

        // Two overlapping rectangles, so the group is a real group and not one command the builder
        // folds into a vertex alpha. The backdrop's group paints translucently, so the inverted field
        // shows through it.
        var over = arrangement == "backdrop" ? new Color4(Paint.R, Paint.G, Paint.B, 0.5f) : Paint;
        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, arrangement == "backdrop" ? new Color4(0.5f, 0.5f, 0.5f, 0.25f) : Grey, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, over, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        return list;
    }

    static UiGeometry Build(DrawList list, float white, out GlyphAtlas atlas) {
        atlas = new GlyphAtlas(64, 64);
        var cache = new GlyphFieldCache(atlas);

        return new UiGeometryBuilder { Gamut = ColorGamut.Srgb, WhiteLevel = white }.Build(list, cache, Viewport);
    }

    /// <summary>One frame at one white level, through the Raven table into a float target.</summary>
    static (Vector4[] Pixels, UiRenderer Renderer) Frame(Fixture fixture, DrawList list, float white, string name, Color4? ground = null) {
        var under = ground ?? Clear;

        var device = fixture.Device;

        fixture.Graph.Reset();

        var geometry = Build(list, white, out var atlas);

        Assert.Equal(white, geometry.WhiteLevel);
        Assert.NotEmpty(geometry.Layers);

        var target = fixture.Owned(name, TextureUsage.ColourTarget | TextureUsage.CopySource, PixelFormat.Rgba32Float);

        var colour = fixture.Graph.ImportTexture(
            target.Texture,
            target.View,
            target.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var renderer = new UiRenderer(
            device,
            UiShaderLibrary.Load(device),
            new Rendering.RenderOutput([PixelFormat.Rgba32Float])
        );

        fixture.Owns(renderer.Dispose);

        fixture.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, under);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var readback = device.CreateBuffer(
            new(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "white readback")
        );

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "white")) {
            renderer.Upload(commands, geometry, atlas);
            renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(under));
            fixture.Graph.Execute(commands);
            commands.CopyTextureToBuffer(new(fixture.Graph.TextureOf(colour)), new(Side, Side, 1), readback, 0);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var floats = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(floats.AsSpan()));
        device.Destroy(readback);

        Assert.True(VulkanDiagnostics.ErrorCount == 0, string.Join(Environment.NewLine, VulkanDiagnostics.Messages));

        return (ToPixels(floats), renderer);
    }

    static Vector4[] ToPixels(float[] floats) {
        var pixels = new Vector4[floats.Length / 4];

        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = new(floats[index * 4], floats[(index * 4) + 1], floats[(index * 4) + 2], floats[(index * 4) + 3]);
        }

        return pixels;
    }

    static Vector4 At(Vector4[] frame, int x, int y) => frame[(y * Side) + x];

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
