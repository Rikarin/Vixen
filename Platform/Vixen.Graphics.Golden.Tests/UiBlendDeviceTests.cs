// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary><c>mix-blend-mode</c> on the device: every mode, and every arrangement the backdrop replay has to get right.</summary>
/// <remarks>
///     <para>
///         <b>#783.</b> <c>UiRenderer</c> composites a blended group through <c>UiBlend</c>, which
///         samples the group's surface and a capture of what its composite lands on — the parent's
///         draws replayed up to the composite — and mixes them by CSS Compositing 1 § 5.1's and
///         § 5.3's arithmetic. Every fixture here is held to two things: a closed form computed off
///         <see cref="UiBlend.Apply" /> where one can be written, and <c>SoftwareUiRasterizer</c>,
///         which blends by reading its own destination and is therefore the independent executor.
///     </para>
///     <para>
///         ⚠ <b>Operands chosen so no mode is the identity on them</b>, which is the trap
///         <see cref="UiRenderer.Unblended" />'s remarks describe: <c>multiply</c> against white or
///         <c>screen</c> against black would pass a device that never blended. So the field and the
///         group are both mid-range and unequal in every channel, their saturations differ (a pair with
///         equal <c>Sat</c> makes <c>hue</c> and <c>color</c> the same picture), and every mode's
///         expected pixel is asserted to differ from plain source-over before the device is asked.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiBlendDeviceTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    static readonly Color4 Background = new(0.08f, 0.09f, 0.11f, 1f);

    /// <summary>What the group lands on: opaque, mid-range, saturation 0.5.</summary>
    static readonly Color4 Field = new(0.8f, 0.3f, 0.55f, 1f);

    /// <summary>The group's own paint: opaque, saturation 0.7, a different hue and luma from the field.</summary>
    static readonly Color4 Paint = new(0.2f, 0.9f, 0.4f, 1f);

    /// <summary>The group's opacity, so the composite's vertex alpha is part of what is blended.</summary>
    const float Opacity = 0.8f;

    public static TheoryData<UiBlendMode> Modes() {
        var data = new TheoryData<UiBlendMode>();

        foreach (var mode in Enum.GetValues<UiBlendMode>()) {
            if (mode != UiBlendMode.Normal) {
                data.Add(mode);
            }
        }

        return data;
    }

    /// <summary>Each of the fifteen modes lands on the pixel § 5.1 says, on the device and in software.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryModeIsTheClosedFormOnTheDevice(UiBlendMode mode) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var expected = Expected(mode);
        var plain = Expected(UiBlendMode.Normal);

        // The instrument first: on these operands the mode is not the identity, so a device that
        // composited source-over would miss by more than a rounding.
        Assert.True(
            Distance(expected, plain) >= 6,
            $"{mode} lands within {Distance(expected, plain)} codes of source-over on this fixture, so it "
            + "cannot tell a blend that ran from one that did not"
        );

        var (rendered, software, renderer) = Draw(owned, Group(mode), $"blend-{mode}");

        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var middle = Middle(rendered);

        Assert.True(
            Distance(middle, expected) <= 2,
            $"{mode}: the device drew {middle} where § 5.1 gives {expected} (source-over would be {plain})"
        );

        Assert.True(
            Distance(Middle(software), expected) <= 2,
            $"{mode}: the software renderer drew {Middle(software)} where § 5.1 gives {expected} — the oracle and its "
            + "executor disagree, so the fixture is wrong before the device is"
        );

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"{mode}: the two executors disagree: {comparison}");
    }

    /// <summary>A second blended sibling blends with the first one's <i>blended</i> composite.</summary>
    /// <remarks>
    ///     ⚠ <b>The replay has to draw a blended composite blended</b>, and that is the arrangement
    ///     most likely to be wrong: the second group's backdrop capture passes over the first group's
    ///     composite, and a capture that drew it source-over would hand the second one a backdrop the
    ///     frame never shows. Three blended draws, not two — the first composite once in the replay and
    ///     once in the frame — which is what <see cref="UiRenderer.Blended" />'s remarks say it counts.
    /// </remarks>
    [Fact]
    public void OverlappingBlendedSiblingsAgreeWithTheSoftwarePath() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        list.Add(new DrawCommand(DrawCommandKind.LayerPush, 16, 16, 64, 64, new Color4(1f, 1f, 1f, Opacity), 0, 0) { Blend = UiBlendMode.Multiply });
        list.Add(new(DrawCommandKind.Rectangle, 16, 16, 64, 64, Paint, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 24, 24, new Color4(0.9f, 0.6f, 0.1f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));

        list.Add(new DrawCommand(DrawCommandKind.LayerPush, 48, 48, 64, 64, new Color4(1f, 1f, 1f, Opacity), 0, 0) { Blend = UiBlendMode.Difference });
        list.Add(new(DrawCommandKind.Rectangle, 48, 48, 64, 64, new Color4(0.3f, 0.5f, 0.95f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 80, 80, 24, 24, new Color4(0.1f, 0.2f, 0.3f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (rendered, software, renderer) = Draw(owned, list, "blend-siblings");

        Assert.Equal(2, renderer.Composited);
        Assert.Equal(3, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"two blended siblings: the executors disagree: {comparison}");
    }

    /// <summary>A blended group inside a translucent one blends with its parent's surface, not the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The group is a backdrop root</b> — CSS Compositing 1 § 3's isolation: the inner group's
    ///     capture starts from transparent black and holds the parent's own draws before it, and the
    ///     frame behind the parent is not in it. A capture that cleared to the host's ground instead
    ///     would blend with the window background through a translucent panel, and the two executors
    ///     would disagree by exactly that.
    /// </remarks>
    [Fact]
    public void ANestedBlendMixesWithItsParentsSurface() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, new Color4(0.1f, 0.35f, 0.2f, 1f), 0, 0));

        list.Add(new(DrawCommandKind.LayerPush, 12, 12, 104, 104, new Color4(1f, 1f, 1f, 0.9f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 12, 12, 104, 60, Field, 0, 0));

        list.Add(new DrawCommand(DrawCommandKind.LayerPush, 32, 40, 64, 64, new Color4(1f, 1f, 1f, Opacity), 0, 0) { Blend = UiBlendMode.Screen });
        list.Add(new(DrawCommandKind.Rectangle, 32, 40, 64, 64, Paint, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 48, 20, 20, new Color4(0.6f, 0.1f, 0.7f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));

        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (rendered, software, renderer) = Draw(owned, list);

        Assert.Equal(2, renderer.Composited);
        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"a nested blend: the executors disagree: {comparison}");
    }

    /// <summary>A blend reads what its composite lands on — which includes the group's own filtered backdrop.</summary>
    /// <remarks>
    ///     ⚠ <b>The replay stops at <see cref="UiLayer.Composite" /> and not at <see cref="UiLayer.First" />,
    ///     and this is the one fixture that can tell.</b> A <c>backdrop-filter</c> paints its quad
    ///     behind the element and before the composite, so the composite lands on the <i>inverted</i>
    ///     field; <c>SoftwareUiRasterizer</c> reads its destination at that moment. A capture stopped at
    ///     the group's first draw — <c>backdrop-filter</c>'s own stop — would hand the blend the
    ///     uninverted field instead, and every other fixture here would still pass.
    /// </remarks>
    [Fact]
    public void ABlendMixesWithItsOwnFilteredBackdrop() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Backdrop = new UiBackdrop(0f, 1f, UiColorMatrix.Invert(1f))
            }
        );

        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (rendered, software, renderer) = Draw(owned, list);

        Assert.Equal(1, renderer.Backdropped);
        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"a blend over its own filtered backdrop: the executors disagree: {comparison}");
    }

    /// <summary>A transformed blended group is declined and counted, rather than blended against the wrong texels.</summary>
    /// <remarks>
    ///     <c>UiBlend</c> reads the backdrop at the composite quad's texture coordinate, which is the
    ///     target texel only while the quad is where the surface is; a rotated quad has moved and Raven
    ///     has no fragment-position input to recover the texel from. So the renderer makes no capture
    ///     for it, composites it source-over, and says so in <see cref="UiRenderer.Unblended" /> — which
    ///     is the whole of what this asserts, because the picture is then a known divergence.
    /// </remarks>
    [Fact]
    public void ATransformedBlendIsDeclinedAndCounted() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 32, 32, 64, 64, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Transform = UiTransform.Rotation(15f, new Vector2(Side / 2f, Side / 2f))
            }
        );

        list.Add(new(DrawCommandKind.Rectangle, 32, 32, 64, 64, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (_, _, renderer) = Draw(owned, list);

        Assert.Equal(1, renderer.Composited);
        Assert.Equal(0, renderer.Blended);
        Assert.Equal(1, renderer.Unblended);
    }

    /// <summary>A capture left over from last frame is not used for this frame's group at the same number.</summary>
    /// <remarks>
    ///     ⚠ <b>Surface numbers are reused by position from frame to frame, and a capture outlives the
    ///     group that made it.</b> Frame one blends a plain group and makes its capture; frame two puts
    ///     a rotated group at the same number, which the device declines. A renderer that asked only
    ///     "is there a capture for this number" would blend frame two against last frame's texels at
    ///     the unrotated place — so what decides is this frame's own verdict, and this reads it.
    /// </remarks>
    [Fact]
    public void AStaleCaptureIsNotUsedForTheNextFramesGroup() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        var plain = Group(UiBlendMode.Multiply);
        Frame(plain, "ui-blend-first");

        Assert.Equal(1, renderer.Blended);

        var rotated = new DrawList();
        rotated.BeginFrame();
        rotated.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        rotated.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Transform = UiTransform.Rotation(15f, new Vector2(Side / 2f, Side / 2f))
            }
        );

        rotated.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        rotated.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        rotated.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        rotated.EndFrame();

        Frame(rotated, "ui-blend-second");

        Assert.Equal(0, renderer.Blended);
        Assert.Equal(1, renderer.Unblended);

        void Frame(DrawList list, string name) {
            var colour = owned.ColourTarget(name);
            var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
            var geometry = new UiGeometryBuilder().Build(list, cache, Viewport);

            owned.Graph.AddPass(name, pass => {
                pass.ColourAttachment(colour, LoadAction.Clear, Background);
                pass.SideEffect();
                pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
            });

            owned.Render(
                colour,
                commands => {
                    renderer.Upload(commands, geometry, cache.Atlas);
                    renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Background));
                }
            );

            owned.Graph.Reset();
        }
    }

    /// <summary>The field, and one opaque-painted blended group at <see cref="Opacity" /> over its middle.</summary>
    static DrawList Group(UiBlendMode mode) {
        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        list.Add(new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0) { Blend = mode });

        // Two overlapping rectangles, so the group is a real group and not one command the builder
        // folds into a vertex alpha — the middle pixel is under the second, which is `Paint`.
        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        return list;
    }

    /// <summary>The middle pixel § 5.1 gives: <see cref="Paint" /> at <see cref="Opacity" /> blended onto <see cref="Field" />.</summary>
    static (int Red, int Green, int Blue) Expected(UiBlendMode mode) {
        var source = new Color4(Paint.R * Opacity, Paint.G * Opacity, Paint.B * Opacity, Opacity);
        var mixed = UiBlend.Apply(mode, source, Field);
        var inverse = 1f - mixed.A;

        return (
            Code(mixed.R + (Field.R * inverse)),
            Code(mixed.G + (Field.G * inverse)),
            Code(mixed.B + (Field.B * inverse))
        );

        static int Code(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    /// <summary>Draws a frame through the Raven table on the device, and through the software renderer.</summary>
    static (Bitmap Rendered, Bitmap Software, UiRenderer Renderer) Draw(Fixture owned, DrawList list, string? keep = null) {
        var colour = owned.ColourTarget("ui-blend");
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(list, cache, Viewport);

        Assert.NotEmpty(geometry.Layers);

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass("ui-blend", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var rendered = owned.Render(
            colour,
            commands => {
                renderer.Upload(commands, geometry, cache.Atlas);
                renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Background));
            }
        );

        var software = SoftwareUiRasterizer.Render(geometry, cache.Atlas, Side, Side, Background);

        // Only under `VIXEN_KEEP_PICTURES`, for a person to look at: the assertions are the test.
        if (keep is not null && Environment.GetEnvironmentVariable("VIXEN_KEEP_PICTURES") is { Length: > 0 } directory) {
            PngCodec.Save(Path.Combine(directory, $"{keep}.device.png"), rendered);
            PngCodec.Save(Path.Combine(directory, $"{keep}.software.png"), software);
        }

        return (rendered, software, renderer);
    }

    static (int Red, int Green, int Blue) Middle(Bitmap bitmap) {
        var offset = bitmap.Offset(Side / 2, Side / 2);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    static int Distance((int Red, int Green, int Blue) a, (int Red, int Green, int Blue) b) =>
        Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

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
