// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A group blended with what is under it, rasterised and read back a pixel at a time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every scene here is built so that the blended answer and the unblended one differ in a
///         channel that is 0 in one and 255 in the other.</b> The tempting fixture — green multiplied
///         into red — is the one to avoid: it lands on black, which is also what a group that never
///         drew produces, so it would pass against a feature that does nothing at all. Yellow over
///         magenta lands on red, and the green channel alone says which of the two happened.
///     </para>
///     <para>
///         ⚠ <b>Pixels asserted against CSS Compositing 1 § 5.1's arithmetic rather than against a
///         committed picture</b>, on <c>GroupOpacityTests</c>' terms: the numbers are worked out by
///         hand from the stated colours, so this cannot be made to pass by re-accepting a screenshot.
///     </para>
///     <para>
///         ⚠ <b>The blend is done on the values the surface holds, which are linear.</b> The colours
///         below are chosen from the corners of the unit cube for that reason — a component is 0 or 1
///         in either encoding, so none of these numbers depends on a transfer function this engine
///         deliberately does not apply. See <see cref="UiBlend" />, which states the divergence from
///         a browser's sRGB compositing.
///     </para>
/// </remarks>
public class MixBlendModeTests {
    /// <summary>A magenta field with a yellow panel over it, under whatever style is named.</summary>
    /// <remarks>
    ///     The backdrop is painted by an element rather than by the page, so that the same scene works
    ///     inside a group: what a blend mixes with is whatever is in the buffer its composite lands
    ///     in, and that is exactly the property <see cref="Isolation_bounds_which_backdrop_a_descendant_blends_with" />
    ///     turns on.
    /// </remarks>
    static UiTest Over(string panelStyle) {
        var ui = UiTest.Create(40f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: 40px; height: 40px; }
            .back { position: absolute; left: 0; top: 0; width: 40px; height: 40px; background-color: #ff00ff; }
            .panel { position: absolute; left: 8px; top: 8px; width: 24px; height: 24px; background-color: #ffff00; {{panelStyle}} }
            """
        );

        ui.Create("div", null, "back", "back");
        ui.Create("div", null, "panel", "panel");
        ui.Frame();

        return ui;
    }

    /// <summary>What the middle of the panel came out as.</summary>
    static (int Red, int Green, int Blue) Panel(UiTest ui) {
        var bitmap = ui.Capture();
        var offset = bitmap.Offset(20, 20);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>With no declaration the panel covers the field, which is the instrument's own check.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what every other test here is measured against, and without it none of them is
    ///     evidence.</b> A blend that silently did nothing would leave the panel yellow; so would a
    ///     blend that worked. The difference is only legible because this test pins what "nothing
    ///     happened" looks like in the same three channels.
    /// </remarks>
    [Fact]
    public void An_unblended_panel_covers_the_field_it_is_over() {
        using var ui = Over(string.Empty);

        Assert.Equal((255, 255, 0), Panel(ui));
    }

    /// <summary>Yellow multiplied into magenta is red, so the green channel goes out.</summary>
    /// <remarks>
    ///     <c>B(Cb, Cs) = Cb × Cs</c> per channel: <c>(1,0,1) × (1,1,0)</c> is <c>(1,0,0)</c>. The
    ///     panel is opaque, so § 5.1's weighting by the backdrop's alpha leaves the product alone and
    ///     the source-over that follows writes it whole.
    /// </remarks>
    [Fact]
    public void Multiply_takes_the_backdrop_into_the_group() {
        using var ui = Over("mix-blend-mode: multiply;");

        Assert.Equal((255, 0, 0), Panel(ui));
    }

    /// <summary>Yellow screened over magenta is white, so the blue channel comes in.</summary>
    /// <remarks>
    ///     The complement of the test above and not a second copy of it: <c>multiply</c> can only
    ///     remove a channel, so a "blend" that simply intersected coverage would pass it. Screen has
    ///     to <i>add</i> the blue that neither operand has on its own.
    /// </remarks>
    [Fact]
    public void Screen_lightens_towards_white() {
        using var ui = Over("mix-blend-mode: screen;");

        Assert.Equal((255, 255, 255), Panel(ui));
    }

    /// <summary>The absolute difference, which moves a channel in each direction at once.</summary>
    [Fact]
    public void Difference_is_the_distance_between_the_two() {
        using var ui = Over("mix-blend-mode: difference;");

        Assert.Equal((0, 255, 255), Panel(ui));
    }

    /// <summary>An unknown keyword is <c>normal</c>, rather than a mode picked by index.</summary>
    /// <remarks>
    ///     ⚠ The reader scans a table of interned ids, so a keyword nothing interned compares equal to
    ///     nothing and falls through. Written down because the tempting implementation — parsing the
    ///     word into an index — turns a typo into a silently different picture.
    /// </remarks>
    [Fact]
    public void An_unrecognised_mode_composites_normally() {
        using var ui = Over("mix-blend-mode: plaid;");

        Assert.Equal((255, 255, 0), Panel(ui));
    }

    /// <summary>A bordered element blends its finished result, not each command it painted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The test that tells a group-wide blend from a per-command one, on the commonest
    ///         element in any interface.</b> A red panel with a blue border over a cyan field,
    ///         multiplied. CSS Compositing 1 § 5.1 blends the element's <i>rendered result</i>: the
    ///         blue border covers the red background source-over first, and blue multiplied into cyan
    ///         is blue. A blend carried on each <see cref="DrawCommand" /> would multiply the red
    ///         background into the cyan field — which is black — and then multiply the blue border
    ///         into <i>that</i>, leaving the border band black as well.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two pixels, because either one alone is ambiguous.</b> The band is blue whether the
    ///         blend is group-wide or absent altogether, so it cannot say the feature ran; the middle
    ///         is black whether the blend is group-wide or per-command, so it cannot say which kind it
    ///         was. Together they pin both axes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bordered_element_blends_its_result_rather_than_each_command() {
        using var ui = UiTest.Create(40f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            """
            root { width: 40px; height: 40px; }
            .back { position: absolute; left: 0; top: 0; width: 40px; height: 40px; background-color: #00ffff; }
            .panel {
                position: absolute; left: 0; top: 0; width: 40px; height: 40px;
                background-color: #ff0000;
                border-top: 6px solid #0000ff;
                border-right: 6px solid #0000ff;
                border-bottom: 6px solid #0000ff;
                border-left: 6px solid #0000ff;
                mix-blend-mode: multiply;
            }
            """
        );

        ui.Create("div", null, "back", "back");
        ui.Create("div", null, "panel", "panel");
        ui.Frame();

        var bitmap = ui.Capture();

        // The middle, which is the plain background: red multiplied into cyan is black. This is the
        // half that says the blend ran at all.
        var middle = bitmap.Offset(20, 20);

        Assert.Equal(0, bitmap.Pixels[middle]);
        Assert.Equal(0, bitmap.Pixels[middle + 1]);
        Assert.Equal(0, bitmap.Pixels[middle + 2]);

        // Well inside the top border band, away from both of its edges so no coverage ramp is being
        // read as a colour. This is the half that says the blend was group-wide.
        var band = bitmap.Offset(20, 3);

        Assert.Equal(0, bitmap.Pixels[band]);
        Assert.Equal(255, bitmap.Pixels[band + 2]);
    }

    /// <summary><c>isolation: isolate</c> stops a descendant reaching the picture outside the group.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole of <c>isolation</c>, and it is observable only through somebody else's
    ///         <c>mix-blend-mode</c>.</b> The child multiplies. Outside an isolated ancestor its
    ///         backdrop is the magenta field, so it lands on red. Inside one its backdrop is the
    ///         ancestor's own surface, which is transparent black where the ancestor painted nothing —
    ///         and § 5.1 weights the blend by the backdrop's alpha, so a blend against nothing is
    ///         <c>normal</c> and the child stays yellow.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted in one test because neither is evidence alone: the isolated
    ///         answer is also what an engine that ignored <c>mix-blend-mode</c> entirely would produce.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Isolation_bounds_which_backdrop_a_descendant_blends_with() {
        Assert.Equal((255, 0, 0), Nested(string.Empty));
        Assert.Equal((255, 255, 0), Nested("isolation: isolate;"));

        static (int, int, int) Nested(string wrapperStyle) {
            using var ui = UiTest.Create(40f, 40f);
            ui.Document.Compositing = true;

            ui.Load(
                $$"""
                root { width: 40px; height: 40px; }
                .back { position: absolute; left: 0; top: 0; width: 40px; height: 40px; background-color: #ff00ff; }
                .wrap { position: absolute; left: 8px; top: 8px; width: 24px; height: 24px; {{wrapperStyle}} }
                .kid {
                    position: absolute; left: 0; top: 0; width: 24px; height: 24px;
                    background-color: #ffff00; mix-blend-mode: multiply;
                }
                """
            );

            ui.Create("div", null, "back", "back");

            var wrap = ui.Create("div", null, "wrap", "wrap");
            ui.Create("div", wrap, "kid", "kid");
            ui.Frame();

            var bitmap = ui.Capture();
            var offset = bitmap.Offset(20, 20);

            return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
        }
    }

    /// <summary>A blended element opens a group even when it paints one rectangle.</summary>
    /// <remarks>
    ///     ⚠ <b>What a picture cannot answer, and the case the peephole gets wrong.</b>
    ///     <c>DrawList.Collapse</c> throws the layer bracket away when a group holds one fadeable
    ///     command — right for opacity, and fatal here, because the bracket is the only thing carrying
    ///     the mode. A collapsed blended panel is a picture with no blend in it, which is exactly what
    ///     the multiply test above would report; this says out loud that the layer survived and what
    ///     it is carrying.
    /// </remarks>
    [Fact]
    public void A_blended_group_survives_the_single_command_collapse() {
        using var ui = Over("mix-blend-mode: multiply;");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(UiBlendMode.Multiply, layer.Blend);
    }

    /// <summary>Nothing opens a group for a mode that changes nothing.</summary>
    /// <remarks>
    ///     The converse of the test above, and it is what stops the feature costing a viewport-sized
    ///     surface on every element that names <c>mix-blend-mode: normal</c> — which is what a family
    ///     with a <c>normal</c> class emits on the elements that turn it back off.
    /// </remarks>
    [Fact]
    public void A_normal_mode_opens_no_group() {
        using var ui = Over("mix-blend-mode: normal;");

        Assert.Empty(ui.Geometry.Layers);
    }

    /// <summary>§ 5.1's weighting: a group over nothing is composited normally, whatever the mode.</summary>
    /// <remarks>
    ///     ⚠ Read directly rather than through a scene, because a transparent backdrop is also the one
    ///     place the un-premultiply would divide by zero — so a fixture that never reached it would
    ///     leave the guard untested and the arithmetic unstated.
    /// </remarks>
    [Fact]
    public void A_transparent_backdrop_leaves_every_mode_at_normal() {
        var source = new Color4(0.25f, 0.5f, 0.75f, 1f);

        foreach (var mode in Enum.GetValues<UiBlendMode>()) {
            Assert.Equal(source, UiBlend.Apply(mode, source, Color4.Transparent));
        }
    }

    /// <summary>The four non-separable modes move what they name and leave the rest alone.</summary>
    /// <remarks>
    ///     ⚠ <b><c>luminosity</c> is the one to assert, because it is the mode whose whole promise is
    ///     that it does <i>not</i> change the other two axes.</b> A per-channel implementation of it —
    ///     the mistake — would tint the result towards the source's hue as well, which shows as the
    ///     red and blue channels no longer being equal to each other over a grey backdrop.
    /// </remarks>
    [Fact]
    public void Luminosity_takes_the_source_brightness_onto_the_backdrop_colour() {
        var backdrop = new Color4(0.8f, 0.2f, 0.2f, 1f);
        var source = new Color4(0.5f, 0.5f, 0.5f, 1f);

        var result = UiBlend.Apply(UiBlendMode.Luminosity, source, backdrop);

        // Rec. 601's luma of the source is 0.5 exactly, since the source is grey.
        Assert.Equal(0.5f, (0.3f * result.R) + (0.59f * result.G) + (0.11f * result.B), 3);

        // And the backdrop's hue survives: red is still the dominant channel and the other two are
        // still equal, which a per-channel `Lum` would not preserve.
        Assert.True(result.R > result.G, $"the backdrop's hue was lost: {result.R} vs {result.G}");
        Assert.Equal(result.G, result.B, 5);
    }
}
