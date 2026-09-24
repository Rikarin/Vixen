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

    /// <summary>A second backdrop colour, for the stripe a transformed group's quad reaches and its surface does not.</summary>
    static readonly Color4 Stripe = new(0.15f, 0.55f, 0.85f, 1f);

    /// <summary>The grey the transformed fixtures' outer rectangle is painted, opaque.</summary>
    static readonly Color4 Grey = new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>A scaled blended group reads the backdrop under each pixel, not under its surface coordinate (#1379).</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This replaces <c>ATransformedBlendIsDeclinedAndCounted</c>, whose reason was false.</b>
    ///         A transformed group was declined because <c>UiBlend</c> read the backdrop at the
    ///         composite quad's texture coordinate — which a transformed quad carries untransformed —
    ///         and Raven was said to have no fragment-position input. It has had one since 289b50247,
    ///         and <c>UiBlend</c> now reads <c>SV_Position</c> for such a group.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The backdrop is not uniform, and without that this could not fail.</b> A group
    ///         scaled 1.5× about its centre reaches a stripe of <see cref="Stripe" /> that its
    ///         untransformed surface does not; at the probed pixel the surface coordinate lands back
    ///         over <see cref="Field" />. So reading the capture at the texture coordinate blends with
    ///         the field, and reading it at the pixel blends with the stripe — and against a uniform
    ///         field the two readings are the same picture. The pixel is also outside the group's
    ///         untransformed <see cref="UiLayer.Bounds" />, so a capture confined to them leaves it
    ///         holding the clear, which this catches too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AScaledBlendReadsTheBackdropUnderEachPixel() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = Scaled(units: Side);
        var (rendered, software, renderer) = Draw(owned, list, "blend-scaled");

        Assert.Equal(1, renderer.Composited);
        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        AssertScaledPixels(rendered, "device");
        AssertScaledPixels(software, "software");

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"a scaled blend: the executors disagree: {comparison}");
    }

    /// <summary>The same scaled group, laid out in half as many units and drawn at a density of two.</summary>
    /// <remarks>
    ///     ⚠ <b>The #1200 class of defect, asked of the fragment-position read.</b> <c>SV_Position</c>
    ///     is in framebuffer texels and the capture is too, so the reciprocal the host pushes must be
    ///     the capture's size in texels and not the surface's in document units — which are the same
    ///     number at a density of one and differ by exactly the density here. There is no software
    ///     executor at a density other than one, so this is the closed form alone.
    /// </remarks>
    [Fact]
    public void AScaledBlendAtDensityTwoReadsTheBackdropUnderEachPixel() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        const int units = Side / 2;

        var list = Scaled(units);
        var colour = owned.ColourTarget("ui-blend-density");
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(list, cache, new Rectangle(0, 0, units, units));

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass("ui-blend-density", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(units, units), 2f));
        });

        var rendered = owned.Render(
            colour,
            commands => {
                renderer.Upload(commands, geometry, cache.Atlas);
                renderer.Compose(commands, geometry, new Int2(units, units), 2f, new UiBackdropSource(Background));
            }
        );

        Keep(rendered, "blend-scaled-density-2.device");

        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        AssertScaledPixels(rendered, "device at density two");
    }

    /// <summary>A rotated blended group agrees with the software executor, which blends by reading its own destination.</summary>
    /// <remarks>
    ///     The general case beside the two closed forms: a rotation moves every pixel of the quad off
    ///     its surface coordinate, over a backdrop that changes under it, so any disagreement between
    ///     the window-position read and the software path's destination read shows up somewhere in the
    ///     group rather than at one probed pixel.
    /// </remarks>
    [Fact]
    public void ARotatedBlendAgreesWithTheSoftwarePath() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, Side / 2f, Side, Stripe, 0, 0));

        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 32, 32, 64, 64, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Transform = UiTransform.Rotation(30f, new Vector2(Side / 2f, Side / 2f))
            }
        );

        list.Add(new(DrawCommandKind.Rectangle, 32, 32, 64, 64, Grey, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (rendered, software, renderer) = Draw(owned, list, "blend-rotated");

        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var comparison = ImageComparer.Compare(rendered, software, new ImageTolerance(4, 0.001));

        Assert.True(comparison.Matches, $"a rotated blend: the executors disagree: {comparison}");
    }

    /// <summary>
    ///     The field, a stripe down its left quarter, and a multiplied group over the middle half scaled
    ///     1.5× about its centre — in <paramref name="units" /> document pixels across.
    /// </summary>
    static DrawList Scaled(int units) {
        var k = units / (float)Side;
        var list = new DrawList();

        list.BeginFrame();
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, units, units, Field, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 0, 0, 32 * k, units, Stripe, 0, 0));

        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 32 * k, 32 * k, 64 * k, 64 * k, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Transform = UiTransform.Scale(1.5f, 1.5f, new Vector2(64 * k, 64 * k))
            }
        );

        list.Add(new(DrawCommandKind.Rectangle, 32 * k, 32 * k, 64 * k, 64 * k, Grey, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40 * k, 40 * k, 48 * k, 48 * k, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        return list;
    }

    /// <summary>The two pixels of <see cref="Scaled" /> whose closed forms differ from every wrong reading.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>(20, 64)</b> is inside the scaled quad and over the stripe. Its surface coordinate is
    ///         <c>64 + (20.5 − 64) / 1.5 = 35</c>, which is the grey rectangle's and lies over the
    ///         field — so the right answer is the grey multiplied into the <i>stripe</i>; the
    ///         texture-coordinate reading is the grey multiplied by the field and then laid source-over
    ///         on the stripe it actually lands on (measured under sabotage: (89, 58, 99), exactly that).
    ///         A capture confined to the untransformed bounds (32..96) would read whatever that texel
    ///         last held, which is not a closed form, so it is named in the message and not asserted.
    ///     </para>
    ///     <para>
    ///         <b>(64, 64)</b> is the middle: <see cref="Paint" /> over the field, the reading every
    ///         untransformed fixture here already makes — the control that the group was composited at
    ///         all.
    ///     </para>
    /// </remarks>
    static void AssertScaledPixels(Bitmap picture, string executor) {
        var edge = ExpectedOver(UiBlendMode.Multiply, Grey, Stripe);
        var wrong = ExpectedOver(UiBlendMode.Multiply, Grey, Field, onto: Stripe);
        var clear = ExpectedOver(UiBlendMode.Multiply, Grey, Background, onto: Stripe);

        // The instrument: the right reading and the texture-coordinate one are far enough apart to tell.
        Assert.True(Distance(edge, wrong) >= 20, $"stripe {edge} and field {wrong} readings are too close to tell apart");

        var at = At(picture, 20, 64);

        Assert.True(
            Distance(at, edge) <= 3,
            $"{executor}: (20, 64) is {at}; over the stripe it should be {edge} (the field reading is {wrong}, the clear's {clear})"
        );

        var middle = At(picture, 64, 64);
        var expected = ExpectedOver(UiBlendMode.Multiply, Paint, Field);

        Assert.True(Distance(middle, expected) <= 3, $"{executor}: the middle is {middle}; it should be {expected}");
    }

    /// <summary><paramref name="paint" /> at <see cref="Opacity" />, blended by <paramref name="mode" /> onto an opaque <paramref name="under" />.</summary>
    static (int Red, int Green, int Blue) ExpectedOver(UiBlendMode mode, Color4 paint, Color4 under, Color4? onto = null) {
        var destination = onto ?? under;
        var source = new Color4(paint.R * Opacity, paint.G * Opacity, paint.B * Opacity, Opacity);
        var mixed = UiBlend.Apply(mode, source, under);
        var inverse = 1f - mixed.A;

        return (
            Code(mixed.R + (destination.R * inverse)),
            Code(mixed.G + (destination.G * inverse)),
            Code(mixed.B + (destination.B * inverse))
        );

        static int Code(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    static (int Red, int Green, int Blue) At(Bitmap bitmap, int x, int y) {
        var offset = bitmap.Offset(x, y);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>Saves a picture under <c>VIXEN_KEEP_PICTURES</c>, for a person to look at.</summary>
    static void Keep(Bitmap picture, string name) {
        if (Environment.GetEnvironmentVariable("VIXEN_KEEP_PICTURES") is { Length: > 0 } directory) {
            PngCodec.Save(Path.Combine(directory, $"{name}.png"), picture);
        }
    }

    /// <summary>A blended group's drop-shadow quad goes out source-over, and is counted as a decline.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The software path blends the shadow and this one does not, so the counter is the
    ///         only thing that says the two pictures differ on purpose.</b> <c>SoftwareUiRasterizer</c>
    ///         records the mode under <see cref="UiLayer.ShadowImage" /> as well as under the group's
    ///         own number; the device composites the shadow through the colour stage, which is what
    ///         turns the surface into a silhouette and which samples no backdrop. Until the renderer
    ///         recorded the mode on the shadow's number too, this frame read <c>Unblended</c> 0 — the
    ///         shadow quad carried no mode as far as the draw could see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two, not one, because the count is per draw across both halves of the frame.</b>
    ///         The shadow is submitted once by <see cref="UiRenderer.Record" /> and once more inside the
    ///         group's own blend capture, which replays everything its composite lands on — and the
    ///         shadow quad is painted before the composite. <see cref="UiRenderer.Blended" /> is one:
    ///         the composite itself is never inside its own capture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ABlendedGroupsShadowIsDeclinedAndCounted() {
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
                Shadow = new UiDropShadow(new Vector2(6f, 6f), 0f, new Color4(0.1f, 0.6f, 0.9f, 1f))
            }
        );

        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var (_, _, renderer) = Draw(owned, list, "blend-shadow");

        Assert.Equal(1, renderer.Shadowed);
        Assert.Equal(1, renderer.Blended);
        Assert.Equal(2, renderer.Unblended);
    }

    /// <summary>A top-level blend with nothing handed over beneath it is blended, and not counted as declined.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This pins a limitation, so that the documents stating it stay true.</b> It is the
    ///         world renderer's arrangement: <c>UiRenderFeature.Compose</c> passes no
    ///         <see cref="UiBackdropSource" />, because the scene is not drawn when the passes are
    ///         recorded. The group goes through <c>UiBlend</c> against the interface's own prefix over
    ///         transparent black, and a backdrop of alpha zero weights § 5.1 to nothing — so over the
    ///         bare target the composite is exactly source-over, and <see cref="UiRenderer.Unblended" />
    ///         is still zero, because the renderer cannot tell a scene beneath from a host that painted
    ///         nothing. <c>docs/guide/ui/compositing.md</c> says so; a change that starts counting or
    ///         declining this has to change that page too, and this is what will tell it.
    ///     </para>
    ///     <para>
    ///         The pixel half is the closed form: with no field under the group, the middle is
    ///         <see cref="Paint" /> at <see cref="Opacity" /> source-over the target's clear.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATopLevelBlendWithNothingBeneathIsBlendedAndNotCounted() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var list = new DrawList();
        list.BeginFrame();

        list.Add(new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0) { Blend = UiBlendMode.Multiply });
        list.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        list.EndFrame();

        var colour = owned.ColourTarget("ui-blend-nothing-beneath");
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(list, cache, Viewport);

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass("ui-blend-nothing-beneath", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var rendered = owned.Render(
            colour,
            commands => {
                renderer.Upload(commands, geometry, cache.Atlas);
                renderer.Compose(commands, geometry, new Int2(Side, Side));
            }
        );

        Assert.Equal(1, renderer.Blended);
        Assert.Equal(0, renderer.Unblended);

        var over = (
            Code((Paint.R * Opacity) + (Background.R * (1f - Opacity))),
            Code((Paint.G * Opacity) + (Background.G * (1f - Opacity))),
            Code((Paint.B * Opacity) + (Background.B * (1f - Opacity)))
        );

        var middle = Middle(rendered);

        Assert.True(Distance(middle, over) <= 3, $"a blend with nothing beneath: middle {middle}, source-over {over}");

        static int Code(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    /// <summary>A capture left over from last frame is not used for this frame's group at the same number.</summary>
    /// <remarks>
    ///     ⚠ <b>Surface numbers are reused by position from frame to frame, and a capture outlives the
    ///     group that made it.</b> Frame one blends a plain group and makes its capture; frame two puts
    ///     a filtered group at the same number, which the device declines. A renderer that asked only
    ///     "is there a capture for this number" would blend frame two against last frame's texels — so
    ///     what decides is this frame's own verdict, and this reads it.
    ///     <para>
    ///         ⚠ <b>Frame two was a rotated group until #1379 made rotation blendable</b>, and a filter
    ///         is declined twice over on an ordinary host: by <c>EnsureSurfaces</c>' verdict and again
    ///         by <c>SubmitDraw</c>, whose colour matrix takes the draw first. So the host here has no
    ///         colour or mask stage — the one arrangement in which the filter never reaches
    ///         <c>SubmitDraw</c>'s map and the per-frame verdict is the only thing standing between the
    ///         group and last frame's capture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AStaleCaptureIsNotUsedForTheNextFramesGroup() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var renderer = new UiRenderer(
            owned.Device,
            UiShaderLibrary.Load(owned.Device) with { Colour = default, Mask = default },
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        var plain = Group(UiBlendMode.Multiply);
        Frame(plain, "ui-blend-first");

        Assert.Equal(1, renderer.Blended);

        var filtered = new DrawList();
        filtered.BeginFrame();
        filtered.Add(new(DrawCommandKind.Rectangle, 0, 0, Side, Side, Field, 0, 0));

        filtered.Add(
            new DrawCommand(DrawCommandKind.LayerPush, 24, 24, 80, 80, new Color4(1f, 1f, 1f, Opacity), 0, 0) {
                Blend = UiBlendMode.Multiply,
                Filter = UiColorMatrix.Invert(1f)
            }
        );

        filtered.Add(new(DrawCommandKind.Rectangle, 24, 24, 80, 80, new Color4(0.5f, 0.5f, 0.5f, 1f), 0, 0));
        filtered.Add(new(DrawCommandKind.Rectangle, 40, 40, 48, 48, Paint, 0, 0));
        filtered.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        filtered.EndFrame();

        Frame(filtered, "ui-blend-second");

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
