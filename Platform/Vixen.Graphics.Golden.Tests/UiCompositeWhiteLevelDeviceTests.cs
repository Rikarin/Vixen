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

    /// <summary>Asserts every pixel of <paramref name="lit" /> is <paramref name="one" /> times the white, alpha unchanged.</summary>
    static void AssertScaled(string arrangement, string executor, Vector4[] one, Vector4[] lit) {
        var worst = 0f;
        var worstAt = (0, 0);
        var worstPair = (default(Vector4), default(Vector4));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
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
    static (Vector4[] Pixels, UiRenderer Renderer) Frame(Fixture fixture, DrawList list, float white, string name) {
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
            pass.ColourAttachment(colour, LoadAction.Clear, Clear);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var readback = device.CreateBuffer(
            new(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "white readback")
        );

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "white")) {
            renderer.Upload(commands, geometry, atlas);
            renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Clear));
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
