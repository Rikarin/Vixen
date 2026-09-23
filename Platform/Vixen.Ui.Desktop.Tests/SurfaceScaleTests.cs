// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>The window's DPI scale reaching the two numbers that are spent inside the triangles.</summary>
/// <remarks>
///     <para>
///         <b>Everything else a host hands the geometry builder is a colour fact</b> — the gamut and
///         the white level, both read off the swapchain in <c>Adopt</c>. <c>Tolerance</c> and
///         <c>Fringe</c> are the two that come from the <i>window</i>, and until #1329 no host set
///         either: both hosts left the 1× defaults whatever the display was, so a 2× surface flattened
///         curves to 0.4 device pixels of chord error and drew a two-device-pixel antialiasing band
///         where the design is one.
///     </para>
///     <para>
///         ⚠ <b>Asserted here rather than in the two hosts, because there is only one path.</b>
///         <c>UiApplication.Tessellate</c> and <c>EditorHost.Build</c> both reach the builder through
///         <c>UiWindowSurface.Tessellate</c> — which is the arrangement the key's own remarks describe
///         as what stops a fix reaching one renderer and not the other.
///     </para>
///     <para>
///         ⚠ <b>A headless window is what makes the second fixture possible at all.</b>
///         <c>FramebufferSize</c> is <c>ClientSize × DpiScale</c>, so <c>SetDpiScale</c> is a display
///         change that leaves <c>Extent</c> — the framebuffer divided by the scale — exactly where it
///         was. The draw-list version does not move either. So a rebuild after it can only have come
///         from the flattening part of <c>TryBuild</c>'s key, which is the half #905 landed and this
///         is the half that turns the knob.
///     </para>
/// </remarks>
public class SurfaceScaleTests {
    static (UiDocument Document, UiWindowSurface Surface, HeadlessWindow Window) Opened(float scale) {
        var platform = new HeadlessPlatform();
        var window = (HeadlessWindow) platform.CreateWindow(
            new WindowOptions { Title = "scale", Size = new Int2(200, 200) }
        );

        window.SetDpiScale(scale);

        var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            div { width: 40px; height: 20px; background-color: #345; }
            """);

        document.Root.Add("div");

        return (document, new UiWindowSurface(document.Surfaces[0], window), window);
    }

    static GlyphFieldCache Glyphs() => new(new GlyphAtlas(256, 256), resolution: 32);

    /// <summary>One frame, the way both hosts do it.</summary>
    static bool Frame(UiDocument document, UiWindowSurface surface, GlyphFieldCache glyphs) {
        document.Update();
        document.Draw();

        return surface.Tessellate(glyphs);
    }

    /// <summary>A 2× window gets half the chord error and half the fringe, in document pixels.</summary>
    [Fact]
    public void A_surface_hands_its_scale_to_the_flattening() {
        var (document, surface, _) = Opened(2f);

        Frame(document, surface, Glyphs());

        Assert.Equal(UiGeometryBuilder.ToleranceFor(2f), surface.Geometry.Tolerance);
        Assert.Equal(UiGeometryBuilder.FringeFor(2f), surface.Geometry.Fringe);

        // ⚠ Stated as the product rather than as 0.1 and 0.25: what has to hold is that the device
        // pixel — the one the user looks at — gets the same fifth and the same half it gets at 1×.
        Assert.Equal(0.2f, surface.Geometry.Tolerance * 2f, 1e-6f);
        Assert.Equal(0.5f, surface.Geometry.Fringe * 2f, 1e-6f);
    }

    /// <summary>And a 1× window is left exactly where the defaults are.</summary>
    /// <remarks>
    ///     The half that says the handover is a scaling rather than a constant: a host that assigned
    ///     the 2× numbers unconditionally would pass the fixture above and make every ordinary display
    ///     flatten twice as finely for nothing.
    /// </remarks>
    [Fact]
    public void An_ordinary_surface_keeps_the_numbers_it_always_had() {
        var (document, surface, _) = Opened(1f);

        Frame(document, surface, Glyphs());

        Assert.Equal(0.2f, surface.Geometry.Tolerance, 1e-6f);
        Assert.Equal(0.5f, surface.Geometry.Fringe, 1e-6f);
    }

    /// <summary>Dragging a window onto a 2× display rebuilds geometry whose extent did not move.</summary>
    [Fact]
    public void A_display_change_alone_tessellates_again() {
        var (document, surface, window) = Opened(1f);
        var glyphs = Glyphs();

        Assert.True(Frame(document, surface, glyphs), "the first frame has nothing to keep");

        var extent = surface.Extent;

        // The skip, so that the rebuild below is measured against a builder that was demonstrably
        // willing to skip — a builder that rebuilt every frame would satisfy the assertion after it
        // without the scale having reached anything.
        Assert.False(Frame(document, surface, glyphs), "an unchanged frame keeps its geometry");

        window.SetDpiScale(2f);

        Assert.Equal(extent, surface.Extent);
        Assert.True(Frame(document, surface, glyphs), "a 2× display is a different tessellation");
        Assert.Equal(UiGeometryBuilder.ToleranceFor(2f), surface.Geometry.Tolerance);

        // And it settles again: the setters are idempotent, so the frame after the move skips.
        Assert.False(Frame(document, surface, glyphs), "the scale stopped moving, so the geometry can");
    }
}
