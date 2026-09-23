// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary><c>LevelIndicator</c> and <c>Gauge</c>, drawn by the device with the shipped theme and the shipped shaders.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first picture of this control anywhere.</b> #666 rank 6 landed it with a
///         closed-form oracle in <c>LevelIndicatorTests</c> that runs on the software rasterizer with
///         the theme's corner radius switched off, so until this file nobody had seen the rail the
///         theme actually draws — six pixels high, rounded by three — nor any of its three levels in
///         either palette on a GPU.
///     </para>
///     <para>
///         ⚠ <b>Through <see cref="UiShaderLibrary.Load" /> and not the hand-written GLSL beside this
///         project</b>, because the question is what an application shows and that is the call
///         <c>UiApplication</c> and <c>EditorHost</c> both make.
///     </para>
///     <para>
///         <b>Asserted three ways, before the reference is consulted.</b> The lit share of every rail
///         is its reading's share of the rail — read off the device's pixels with the colours taken
///         from the same picture, so the oracle is about the control and not about the palette. The
///         three levels are three different fills, and the two readings the theme calls critical are
///         one fill. And the device agrees with the software rasterizer, which is the renderer the
///         unit suite's oracle already trusts, so the picture is not merely self-consistent.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class LevelIndicatorImageTests {
    const int Side = Fixture.Side;
    const int Left = 8;
    const int Rail = 112;

    /// <summary>How far apart, in the worst channel, two fills must be to count as two colours.</summary>
    /// <remarks>
    ///     Ten times the antialiasing noise measured on these rails, and well under the closest pair
    ///     either palette has: the dark one's warning and danger are 51 apart in this target, which
    ///     is linear — a first draft asking for 60 failed on exactly that pair.
    /// </remarks>
    const int Apart = 30;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>What the frame is cleared to: magenta, which the root paints over, so it shows only through a hole.</summary>
    static readonly Color4 Background = new(1f, 0f, 1f, 1f);

    /// <summary>The rails, top to bottom: an id, where the rail's top edge is, and the reading's share of the rail.</summary>
    static readonly (string Id, int Top, float Share)[] Rows = [
        ("ordinary", 10, 0.5f),
        ("warning", 26, 0.75f),
        ("critical", 42, 0.95f),
        ("segments", 58, 0.7f),
        ("full-battery", 74, 1f),
        ("flat-battery", 90, 0.1f),
        ("empty", 106, 0f)
    ];

    /// <summary>Every level, every drawing mode and the lone falling line, in one palette.</summary>
    /// <param name="dark">Whether the root carries <c>dark</c>, the theme's second palette.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryLevelIsDrawnInItsOwnColourAtItsOwnLength(bool dark) {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;
        using var ui = Document(dark);

        var name = dark ? "ui-level-indicator-dark" : "ui-level-indicator";
        var image = Draw(owned, ui, name);

        var track = Pixel(image, Left + Rail - 5, Middle("ordinary"));
        var ordinary = Pixel(image, Left + 4, Middle("ordinary"));
        var warning = Pixel(image, Left + 4, Middle("warning"));
        var critical = Pixel(image, Left + 4, Middle("critical"));

        // ⚠ The three levels are three fills. This is the half no CPU test in the tree could see: a
        // theme whose `.warning` rule resolved to nothing draws a warning in the accent, and every
        // `Level` assertion still passes. See `Apart` for the bound.
        Assert.True(Distance(ordinary, track) > Apart, $"the ordinary fill {ordinary} is not told apart from the track {track}");
        Assert.True(Distance(ordinary, warning) > Apart, $"a warning {warning} is drawn in the ordinary fill {ordinary}");
        Assert.True(Distance(ordinary, critical) > Apart, $"a critical reading {critical} is drawn in the ordinary fill {ordinary}");
        Assert.True(Distance(warning, critical) > Apart, $"warning {warning} and critical {critical} are one colour");

        // The lone falling line: full is ordinary, nearly empty is critical — the same two fills the
        // pair above used, because the level is a class and the theme has one rule per class.
        Assert.True(Distance(Pixel(image, Left + 4, Middle("full-battery")), ordinary) <= 3);
        Assert.True(Distance(Pixel(image, Left + 4, Middle("flat-battery")), critical) <= 3);

        // An empty rail is all track, end to end.
        Assert.True(Distance(Pixel(image, Left + 4, Middle("empty")), track) <= 3);

        // ⚠ The closed-form oracle: each rail's lit columns are its reading's share of the rail, give
        // or take the antialiased column at each rounded end.
        foreach (var (id, _, share) in Rows) {
            var fill = share > 0f ? Pixel(image, Left + 4, Middle(id)) : track;
            var lit = share > 0f ? Lit(image, Middle(id), fill, track) : 0;

            // Four blocks at seven-tenths is three, rounding: the segmented rail's lit share is 3/4
            // less the gaps taken out of the lit blocks, which is a pixel each.
            var expected = id == "segments" ? Rail * 0.75f : Rail * share;
            var slack = id == "segments" ? 5f : 2f;

            Assert.True(
                MathF.Abs(lit - expected) <= slack,
                $"'{id}' lights {lit} of {Rail} columns where its reading asks for {expected:F1}"
            );
        }

        // And the segmented rail is blocks: three separated runs of lit columns, not one.
        Assert.Equal(3, Runs(image, Middle("segments"), Pixel(image, Left + 4, Middle("segments")), track));

        GoldenImage.Verify(name, image, Tolerance.Edges);
    }

    /// <summary>
    ///     <c>Gauge</c>, the dial half of rank 6: the pair's three levels and the lone falling line,
    ///     four dials at the theme's own size and arc width.
    /// </summary>
    /// <param name="dark">Whether the root carries <c>dark</c>, the theme's second palette.</param>
    /// <remarks>
    ///     The share-of-area oracle is the unit suite's (<c>GaugeTests</c>), where the arc can be made
    ///     thick enough for the count to mean something; at five pixels round a 48-pixel dial the rim
    ///     is too large a share. What this adds is the device: its picture agrees with the software
    ///     rasterizer's, the levels are three fills, and each dial's fill is where the reading puts it
    ///     — the left-hand point of every ring is lit, the right-hand one only past two-thirds.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryLevelIsDrawnOnADial(bool dark) {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;
        using var ui = Dials(dark);

        var name = dark ? "ui-gauge-dark" : "ui-gauge";
        var image = Draw(owned, ui, name);

        // Each dial is 48 pixels at (x, y); its ring is five pixels wide, so two and a half in from
        // the rim is the middle of the ring. Left-hand point: 45° into a 270° sweep, lit at every
        // reading here past a sixth. Right-hand point: 225° in, lit only past five-sixths.
        (int R, int G, int B) West(int x, int y) => Pixel(image, x + 2, y + 24);
        (int R, int G, int B) East(int x, int y) => Pixel(image, x + 45, y + 24);

        var ordinary = West(8, 8);
        var warning = West(72, 8);
        var critical = West(8, 72);
        var track = East(8, 8);

        Assert.True(Distance(ordinary, track) > Apart, $"the ordinary fill {ordinary} is not told apart from the track {track}");
        Assert.True(Distance(ordinary, warning) > Apart, $"a warning {warning} is drawn in the ordinary fill {ordinary}");
        Assert.True(Distance(ordinary, critical) > Apart, $"a critical reading {critical} is drawn in the ordinary fill {ordinary}");
        Assert.True(Distance(warning, critical) > Apart, $"warning {warning} and critical {critical} are one colour");

        // Half and three-quarters stop short of the right-hand point; 0.95 reaches past it.
        Assert.True(Distance(East(72, 8), track) <= 3, "a reading of three-quarters lit the right-hand point of its dial");
        Assert.True(Distance(East(8, 72), critical) <= 3, "a reading of 0.95 did not reach the right-hand point of its dial");

        // The tank at a tenth with one falling line is critical, and lit only near its start: its
        // left-hand point, a sixth of the way round, is still track.
        Assert.True(Distance(West(72, 72), track) <= 3, "a reading of a tenth lit a sixth of its dial");
        // (5, 35) from the dial's corner is the middle of the ring at 148°, thirteen degrees into a
        // fill that ends at 162°.
        Assert.True(Distance(Pixel(image, 72 + 5, 72 + 35), critical) <= 3, "the flat tank is not drawn critical near its start");

        GoldenImage.Verify(name, image, Tolerance.Edges);
    }

    /// <summary>
    ///     Draws the document on the device through the shipped modules, and requires the software
    ///     rasterizer to agree with the picture before anybody looks at it.
    /// </summary>
    /// <remarks>
    ///     The agreement is the one claim a reference image cannot make, because a reference is made
    ///     by one renderer.
    /// </remarks>
    static Bitmap Draw(Fixture owned, UiTest ui, string name) {
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(ui.Document.Drawing, cache, Viewport);

        // Something has to have drawn: a device and a rasterizer that were both handed nothing agree.
        Assert.NotEmpty(geometry.Draws);

        var shaders = UiShaderLibrary.Load(owned.Device);
        owned.Owns(() => Destroy(owned, shaders));

        var renderer = new UiRenderer(owned.Device, shaders, new Rendering.RenderOutput([PixelFormat.Rgba8UNorm]));
        owned.Owns(renderer.Dispose);

        var colour = owned.ColourTarget(name);

        owned.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var image = owned.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));

        var software = SoftwareUiRasterizer.Render(geometry, cache.Atlas, Side, Side, Background);
        var agreement = ImageComparer.Compare(image, software, ImageTolerance.Slight);

        Assert.True(agreement.Matches, $"the device and the software rasterizer disagree about '{name}': {agreement}");

        return image;
    }

    /// <summary>A themed document of the fixture's size, in one palette, with the root painted.</summary>
    static UiTest Themed(bool dark, string css) {
        var ui = UiTest.Create(Side, Side);

        ControlTheme.Install(ui.Document);

        if (dark) {
            ui.Document.Root.AddClass("dark");
        }

        // The root painted the way a window's is, so the picture is the control on the surface it sits on.
        ui.Load($"root {{ width: {Side}px; height: {Side}px; background-color: var(--surface); }} " + css);
        return ui;
    }

    /// <summary>Four dials in a square: ordinary, warning, critical, and a flat tank with one falling line.</summary>
    static UiTest Dials(bool dark) {
        var ui = Themed(
            dark,
            "gauge { position: absolute; } "
            + "#ordinary { left: 8px; top: 8px; } #warning { left: 72px; top: 8px; } "
            + "#critical { left: 8px; top: 72px; } #tank { left: 72px; top: 72px; }"
        );

        foreach (var (id, value) in new[] { ("ordinary", 0.5f), ("warning", 0.75f), ("critical", 0.95f) }) {
            var dial = ui.Document.Create<Gauge>(null, ui.Document.Root, id);

            dial.Warning = 0.6f;
            dial.Critical = 0.85f;
            dial.Value = value;
        }

        var tank = ui.Document.Create<Gauge>(null, ui.Document.Root, "tank");

        tank.Critical = 0.15f;
        tank.Direction = LevelDirection.Falling;
        tank.Value = 0.1f;

        ui.Frame();
        return ui;
    }

    /// <summary>The seven rails, laid out absolutely so that every row is where <see cref="Rows" /> says.</summary>
    static UiTest Document(bool dark) {
        var css = $"level-indicator {{ position: absolute; left: {Left}px; width: {Rail}px; }} ";

        foreach (var (id, top, _) in Rows) {
            css += $"#{id} {{ top: {top}px; }} ";
        }

        var ui = Themed(dark, css);

        // A disk's pair of lines, at three readings: one under both, one past the warning, one past
        // the critical.
        Pair(Add(ui, "ordinary"), 0.5f);
        Pair(Add(ui, "warning"), 0.75f);
        Pair(Add(ui, "critical"), 0.95f);

        var segments = Add(ui, "segments");
        segments.Segments = 4;
        segments.Value = 0.7f;

        // A battery with one line, which says it falls (#1353): ordinary when full, critical when flat.
        Battery(Add(ui, "full-battery"), 1f);
        Battery(Add(ui, "flat-battery"), 0.1f);

        Add(ui, "empty").Value = 0f;

        ui.Frame();
        return ui;

        static void Pair(LevelIndicator meter, float value) {
            meter.Warning = 0.6f;
            meter.Critical = 0.85f;
            meter.Value = value;
        }

        static void Battery(LevelIndicator meter, float value) {
            meter.Critical = 0.15f;
            meter.Direction = LevelDirection.Falling;
            meter.Value = value;
        }
    }

    static LevelIndicator Add(UiTest ui, string id) => ui.Document.Create<LevelIndicator>(null, ui.Document.Root, id);

    /// <summary>The rail's middle row, which is clear of the rounded ends' antialiasing except at the tips.</summary>
    static int Middle(string id) {
        foreach (var (row, top, _) in Rows) {
            if (row == id) {
                return top + 3;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "not a row of this fixture");
    }

    static (int R, int G, int B) Pixel(in Bitmap image, int x, int y) {
        var at = image.Offset(x, y);
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    static int Distance((int R, int G, int B) a, (int R, int G, int B) b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    static bool IsFill(in Bitmap image, int x, int y, (int R, int G, int B) fill, (int R, int G, int B) track) {
        var here = Pixel(image, x, y);
        return Distance(here, fill) < Distance(here, track);
    }

    /// <summary>How many of the rail's columns on row <paramref name="y" /> are nearer the fill than the track.</summary>
    static int Lit(in Bitmap image, int y, (int R, int G, int B) fill, (int R, int G, int B) track) {
        var count = 0;

        for (var x = Left; x < Left + Rail; x++) {
            if (IsFill(image, x, y, fill, track)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>How many separated runs of lit columns row <paramref name="y" /> has.</summary>
    static int Runs(in Bitmap image, int y, (int R, int G, int B) fill, (int R, int G, int B) track) {
        var runs = 0;
        var inside = false;

        for (var x = Left; x < Left + Rail; x++) {
            var lit = IsFill(image, x, y, fill, track);

            if (lit && !inside) {
                runs++;
            }

            inside = lit;
        }

        return runs;
    }

    /// <summary>Destroys the eight modules a loaded table holds — see <c>UiRavenAgreementTests.Destroy</c>.</summary>
    static void Destroy(Fixture owned, UiShaders shaders) {
        owned.Device.Destroy(shaders.Vertex);
        owned.Device.Destroy(shaders.Box);
        owned.Device.Destroy(shaders.Text);
        owned.Device.Destroy(shaders.Solid);
        owned.Device.Destroy(shaders.Image);
        owned.Device.Destroy(shaders.Blur);
        owned.Device.Destroy(shaders.Colour);
        owned.Device.Destroy(shaders.Mask);
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the level indicator may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
