// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>backdrop-filter</c>, from the stylesheet to the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This family has <i>two</i> ways of being vacuously green and the second is worse than
///         anything <c>filter</c> has.</b> The first is the one the colour filters have: the
///         consumption gate asks whether the draw list changed, and a <c>backdrop-filter</c> changes it
///         by opening a group — the <c>LayerPush</c>/<c>LayerPop</c> bracket appears whatever the
///         declaration says, so the gate would pass on a backdrop no executor reads. The second is
///         peculiar to this: <b>with nothing behind the element, every backdrop filter is the
///         identity.</b> Blurring a flat field returns it; greying a black one returns it. So a
///         fixture of panels on a plain background measures a working capture and no capture at all
///         as the same picture, and so does a comparison of the two executors, because the software
///         path would be reproducing the same nothing.
///     </para>
///     <para>
///         ⚠ <b>Which is why every fixture in this file puts <i>structured</i> content behind the
///         panel and reads a pixel that is only explicable by the filter having run.</b> The blur's is
///         a bar of light that does not reach the sample point until it is smeared there; the colour
///         functions' is a saturated field whose transformed value differs from every neighbouring
///         function's on the same input, which <see cref="No_two_of_the_functions_agree_on_this_colour" />
///         asserts once so that the rows below it can each be a single comparison.
///     </para>
///     <para>
///         ⚠ <b>And it is the file <c>UiCompositingTests</c> cannot be</b>, for
///         <c>FilterBlurTests</c>' reason word for word: both executors read the backdrop out of the
///         same <see cref="UiLayer" />, so a <c>backdrop-sepia</c> that came out as a
///         <c>backdrop-grayscale</c> would be identically wrong on both paths and the comparison would
///         pass. What the functions <i>are</i> is asserted here; only the agreement belongs over
///         there.
///     </para>
///     <para>
///         ⚠ <b>Linear and not sRGB</b>, exactly as <c>FilterColourTests</c> is and for the same
///         reason: the whole engine is linear from the parser down, so the levels here are not a
///         browser's. The assertions are therefore written against <see cref="UiColorMatrix" />'s own
///         arithmetic applied to the known backdrop colour, which is the same arithmetic the executor
///         runs and a different question from "is this the number Chrome prints".
///     </para>
/// </remarks>
public class BackdropFilterTests {
    /// <summary>The colour the glass panel sits over, as the draw list carries it.</summary>
    /// <remarks>
    ///     ⚠ <b>Saturated and deliberately lopsided.</b> A <c>backdrop-grayscale</c> over a grey is the
    ///     identity and a <c>backdrop-hue-rotate</c> over an equal-parts colour is very nearly one, so
    ///     a fixture painted in anything convenient would measure several of these as working while
    ///     doing nothing — which is this family's whole hazard restated at the level of one colour.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Written as the <i>linear</i> colour that <c>rgb(51, 102, 204)</c> resolves to, and the
    ///     conversion is spelt out rather than baked into three literals.</b> The engine is linear from
    ///     the parser down and the frame is <c>Rgba8UNorm</c> with no encode on the way out — see
    ///     <c>SoftwareUiRasterizer.Render</c> — so the number a matrix is applied to is not the number
    ///     in the stylesheet. Three literals here would be right until somebody read them as sRGB.
    /// </remarks>
    static readonly Color4 Wall = new(
        ColorSpace.SrgbToLinear(51f / 255f),
        ColorSpace.SrgbToLinear(102f / 255f),
        ColorSpace.SrgbToLinear(204f / 255f),
        1f
    );

    /// <summary>A glass panel over a flat saturated wall.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The panel's own paint is a one-pixel border and nothing else, so the pixel read in
    ///         the middle is the filtered backdrop and only that.</b> A translucent background — which
    ///         is what a real glass panel has — would blend a known but arbitrary amount of white into
    ///         every measurement and turn each assertion below into an argument about the blend rather
    ///         than about the filter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It has to paint <i>something</i>.</b> A group that draws nothing is discarded by
    ///         <c>DrawListBuilder</c> before it becomes a layer — see the remark there, which states
    ///         it as a divergence — so a panel with no background and no border would take its
    ///         backdrop with it and every test in this file would be asserting against an empty
    ///         geometry.
    ///     </para>
    /// </remarks>
    static UiTest Glass(string declaration) {
        var ui = UiTest.Create(60f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: 60px; height: 40px; background-color: #000000; }
            .wall { position: absolute; left: 0px; top: 0px; width: 60px; height: 40px;
                    background-color: rgb(51, 102, 204); }
            .glass { position: absolute; left: 20px; top: 10px; width: 20px; height: 20px;
                     border: 1px solid #000000; {{declaration}} }
            """
        );

        ui.Create("div", null, "wall", "wall");
        ui.Create("div", null, "glass", "glass");
        ui.Frame();

        return ui;
    }

    /// <summary>A glass panel over a single bar of light, on black.</summary>
    /// <remarks>
    ///     ⚠ <b>One bar and not a field, which is the whole of what separates a blur from a copy.</b>
    ///     A capture that was taken and never convolved reproduces the picture exactly, so a fixture
    ///     whose backdrop is uniform cannot tell the two apart. A bar has a place the light is and a
    ///     place it is not, and a blur is the only thing that moves light from the first to the second.
    /// </remarks>
    static UiTest Bar(string declaration) {
        var ui = UiTest.Create(60f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            $$"""
            root { width: 60px; height: 40px; background-color: #000000; }
            .bar { position: absolute; left: 28px; top: 0px; width: 4px; height: 40px;
                   background-color: #ffffff; }
            .glass { position: absolute; left: 20px; top: 10px; width: 20px; height: 20px;
                     border: 1px solid #000000; {{declaration}} }
            """
        );

        ui.Create("div", null, "bar", "bar");
        ui.Create("div", null, "glass", "glass");
        ui.Frame();

        return ui;
    }

    /// <summary>One pixel of the rendered frame, as three channels.</summary>
    static (int R, int G, int B) At(UiTest ui, int x, int y) {
        var bitmap = ui.Capture();
        var offset = bitmap.Offset(x, y);

        return (bitmap.Pixels[offset], bitmap.Pixels[offset + 1], bitmap.Pixels[offset + 2]);
    }

    /// <summary>What a matrix makes of <see cref="Wall" />, as the eight bits the frame stores.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="UiColorMatrix" /> rather than against numbers written here, and the
    ///     claim that makes is narrower than it looks and is the right one.</b> What is under test is
    ///     the <i>wiring</i> — that <c>backdrop-sepia</c> reaches <c>UiColorMatrix.Sepia</c> and lands
    ///     on the backdrop rather than on the element — and the arithmetic of each matrix is
    ///     <c>FilterColourTests</c>' subject, asserted there against relations that hold in any colour
    ///     space. Writing levels here would restate that badly and would have to be recomputed the
    ///     first time anyone touched the conversion.
    /// </remarks>
    static (int R, int G, int B) Expected(UiColorMatrix matrix) {
        var result = matrix.Apply(Wall);

        return (Level(result.R), Level(result.G), Level(result.B));
    }

    static int Level(float value) => (int) Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    /// <summary>How far a channel may be from the arithmetic. One level of the 8-bit store.</summary>
    /// <remarks>
    ///     ⚠ Not zero, and not because the filter is approximate. The frame is <c>Rgba8UNorm</c> and
    ///     the wall's own colour was written as <c>rgb(51, 102, 204)</c>, so it makes one round trip
    ///     through eight bits before the matrix ever sees it — and the matrix's answer makes a second.
    ///     One level is those two roundings and there is nothing else in the path.
    /// </remarks>
    const int Tolerance = 1;

    static void Same((int R, int G, int B) observed, (int R, int G, int B) expected, string what) {
        Assert.True(
            Math.Abs(observed.R - expected.R) <= Tolerance
            && Math.Abs(observed.G - expected.G) <= Tolerance
            && Math.Abs(observed.B - expected.B) <= Tolerance,
            $"{what}: the backdrop came out {observed} where {expected} was wanted. Equal to the "
            + "unfiltered wall means the capture ran and the function did not; equal to a *neighbour's* "
            + "answer means the family is wired to the wrong `UiColorMatrix` factory."
        );
    }

    /// <summary>A backdrop filter opens a group on an element that is fully opaque.</summary>
    /// <remarks>
    ///     ⚠ <b>The fourth reason a group exists, and the only one whose surface holds something the
    ///     element did not draw.</b> Filter Effects 2 § 2 makes any <c>backdrop-filter</c> other than
    ///     <c>none</c> a backdrop root and a stacking context, and here it is not merely spec
    ///     compliance: the capture is a replay of the draw list up to this group's first draw, so
    ///     without the bracket there is no "up to" to speak of.
    /// </remarks>
    [Fact]
    public void A_backdrop_filter_opens_a_group_on_an_element_that_is_fully_opaque() {
        using var ui = Glass("backdrop-filter: blur(3px);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(1f, layer.Alpha, 3);
        Assert.Equal(0f, layer.Blur);
        Assert.Null(layer.Filter);

        var backdrop = Assert.NotNull(layer.Backdrop);

        Assert.Equal(3f, backdrop.Blur, 3);
        Assert.Equal(1f, backdrop.Alpha, 3);
        Assert.Null(backdrop.Matrix);
    }

    /// <summary>The group's own blur and its backdrop's are different fields and different pictures.</summary>
    /// <remarks>
    ///     ⚠ <b>The one mistake that would make every other test in this file pass while the feature
    ///     was wrong.</b> A reader that folded <c>backdrop-filter</c> into <c>UiLayer.Blur</c> would
    ///     blur the panel instead of the scene under it — a plausible picture, and the wrong one. An
    ///     element carrying both is what says the two are separate all the way down.
    /// </remarks>
    [Fact]
    public void A_filter_and_a_backdrop_filter_on_one_element_are_two_independent_things() {
        using var ui = Glass("filter: grayscale(1); backdrop-filter: blur(3px);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(0f, layer.Blur);
        Assert.NotNull(layer.Filter);

        var backdrop = Assert.NotNull(layer.Backdrop);

        Assert.Equal(3f, backdrop.Blur, 3);
        Assert.Null(backdrop.Matrix);
    }

    /// <summary>The filtered backdrop is clipped to the border box and not to the group's ink.</summary>
    /// <remarks>
    ///     ⚠ <b>Two rectangles that are the same on a bare panel and are not the same in general,
    ///     which is why the fixture here has a child hanging out of its parent.</b>
    ///     <c>UiLayer.Bounds</c> is what the subtree <i>drew</i> — CSS Compositing 1 § 3 isolates a
    ///     group without bounding it, so an overflowing child grows it. CSS clips a backdrop filter to
    ///     the element's own border box. Reading the bounds for the backdrop would put a rectangle of
    ///     blurred scene outside the panel that asked for it, in the shape of whatever happened to
    ///     stick out.
    /// </remarks>
    [Fact]
    public void The_backdrop_is_bounded_by_the_border_box_and_not_by_the_groups_ink() {
        var ui = UiTest.Create(60f, 40f);
        ui.Document.Compositing = true;

        ui.Load(
            """
            root { width: 60px; height: 40px; background-color: #000000; }
            .glass { position: absolute; left: 20px; top: 10px; width: 20px; height: 10px;
                     border: 1px solid #000000; backdrop-filter: invert(1); }
            .spill { position: absolute; left: 0px; top: 12px; width: 16px; height: 16px;
                     background-color: #ffffff; }
            """
        );

        var glass = ui.Create("div", null, "glass", "glass");
        ui.Create("div", glass, "spill", "spill");
        ui.Frame();

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.NotNull(layer.Backdrop);
        // ⚠ 22 by 12 and not 20 by 10: a `LayerPush` carries the *border* box, which is the
        // rectangle CSS clips a backdrop filter to and includes the one-pixel border on each side.
        Assert.Equal(new Rectangle(20f, 10f, 22f, 12f), layer.BackdropBounds);

        Assert.True(
            layer.Bounds.Height > layer.BackdropBounds.Height,
            $"the child did not overflow, so this fixture proves nothing: {layer.Bounds}"
        );

        ui.Dispose();
    }

    /// <summary>A blurred backdrop moves light to where the backdrop had none.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement that says a capture was taken <i>and convolved</i>, and the only shape
    ///     of measurement that can.</b> The bar of light is eight pixels from the sample point, so
    ///     nothing but a Gaussian puts anything there — a capture that ran and was not blurred
    ///     reproduces black, and no capture at all reproduces black. The pixel is read at a row inside
    ///     the panel and compared with the same column above it, where the panel is not and the
    ///     picture must be untouched.
    /// </remarks>
    [Fact]
    public void A_blurred_backdrop_puts_light_where_the_backdrop_had_none() {
        using var ui = Bar("backdrop-filter: blur(4px);");

        // The bar is 28..32. Inside the panel (rows 11..29) the light should have spread; above it
        // (row 4) it must not have.
        var spread = At(ui, 24, 20);
        var sharp = At(ui, 24, 4);

        Assert.Equal(0, sharp.R);

        Assert.True(
            spread.R > 0,
            "the backdrop was not blurred: no light reached four pixels outside the bar, which is what "
            + "a capture that never ran and a capture that was never convolved both look like"
        );
    }

    /// <summary>And takes it away from where the backdrop had it.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half, and a blur that only spread outwards would pass the test above.</b> A
    ///     normalised kernel conserves light, so the bar's own middle has to give up exactly what the
    ///     dark around it gained. A reader that added the halo without dimming the source would be a
    ///     brighter picture rather than a blurred one — which reads as an opacity bug.
    /// </remarks>
    [Fact]
    public void A_blurred_backdrop_takes_light_from_where_the_backdrop_had_it() {
        using var ui = Bar("backdrop-filter: blur(4px);");

        var inside = At(ui, 30, 20);
        var above = At(ui, 30, 4);

        Assert.Equal(255, above.R);
        Assert.InRange(inside.R, 1, 200);
    }

    /// <summary>The blur stops at the border box, so the picture beside the panel is untouched.</summary>
    /// <remarks>
    ///     ⚠ <b>A Gaussian spreads and a backdrop filter must not.</b> CSS clips the filtered backdrop
    ///     to the element's border box — the panel is a window onto a blurred version of the scene, not
    ///     a smudge applied to it — so the row just outside the panel has to hold the bar's own hard
    ///     edge. An executor that blurred into the frame instead of into a surface of its own would
    ///     fail here and pass everything above.
    /// </remarks>
    [Fact]
    public void The_blur_does_not_escape_the_border_box() {
        using var ui = Bar("backdrop-filter: blur(4px);");

        // One row below the panel, which ends at y = 30.
        Assert.Equal(0, At(ui, 24, 32).R);
        Assert.Equal(255, At(ui, 30, 32).R);
    }

    /// <summary>Each colour function transforms the backdrop and not the element.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Seven rows, one comparison each, and what makes one comparison enough is
    ///         <see cref="No_two_of_the_functions_agree_on_this_colour" /> next door.</b> The failure
    ///         this family invites is a root wired to the neighbouring factory — <c>backdrop-sepia</c>
    ///         reaching <c>UiColorMatrix.Grayscale</c> — which produces a plausible washed-out colour
    ///         that only a comparison against the <i>right</i> answer can reject. That comparison is
    ///         only worth anything if the wrong answers are different numbers, which is what the other
    ///         test establishes for this particular wall.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The amounts are not round, deliberately.</b> <c>brightness(1)</c>,
    ///         <c>grayscale(0)</c> and the rest are each some function's identity, and a fixture built
    ///         out of identities would pass with the whole family reading a fixed matrix.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("brightness(0.6)")]
    [InlineData("contrast(1.4)")]
    [InlineData("grayscale(1)")]
    [InlineData("invert(1)")]
    [InlineData("saturate(1.8)")]
    [InlineData("sepia(1)")]
    [InlineData("hue-rotate(90deg)")]
    public void A_colour_function_transforms_the_picture_behind_the_element(string function) {
        using var ui = Glass($"backdrop-filter: {function};");

        Same(At(ui, 30, 20), Expected(Matrix(function)), function);

        // ⚠ And the wall beside the panel is the wall, which is what says the transform was applied
        // to the *capture* rather than to the frame. An executor that filtered the destination in
        // place would pass the line above and fail this one.
        Same(At(ui, 8, 20), Expected(UiColorMatrix.Identity), function + " outside the panel");
    }

    /// <summary>The seven functions give seven different answers on this wall.</summary>
    /// <remarks>
    ///     ⚠ <b>The premise the theory above rests on, made a fact.</b> A comparison against the
    ///     expected matrix only rejects a mis-wired root if the other roots' matrices would have given
    ///     something else — and over the wrong colour several of them coincide: everything is the
    ///     identity over black, <c>grayscale</c> and <c>saturate(0)</c> agree everywhere, and
    ///     <c>hue-rotate</c> is nearly the identity over a grey. Checked here rather than reasoned
    ///     about, because the wall is a literal somebody may one day make prettier.
    /// </remarks>
    [Fact]
    public void No_two_of_the_functions_agree_on_this_colour() {
        string[] functions = [
            "brightness(0.6)",
            "contrast(1.4)",
            "grayscale(1)",
            "invert(1)",
            "saturate(1.8)",
            "sepia(1)",
            "hue-rotate(90deg)"
        ];

        var seen = new Dictionary<(int, int, int), string>();

        foreach (var function in functions) {
            var value = Expected(Matrix(function));

            Assert.False(
                seen.TryGetValue(value, out var other),
                $"{function} and {other} both take the wall to {value}, so a root wired to the wrong "
                + "one of them would pass the theory next door. Pick a different wall."
            );

            // And none of them may be the identity either, or the row would pass on a backdrop that
            // was captured and never transformed.
            Assert.NotEqual(Expected(UiColorMatrix.Identity), value);

            seen[value] = function;
        }
    }

    /// <summary><c>opacity()</c> fades the backdrop, and it is the one function that is not a matrix.</summary>
    /// <remarks>
    ///     ⚠ <b>It rides the backdrop quad's own vertex alpha, because <see cref="UiColorMatrix" /> is
    ///     three rows and has no alpha row at all</b> — the same place a <c>drop-shadow</c>'s colour
    ///     alpha rides. So the picture is the wall at half strength over what is already there, which
    ///     is the wall: half of a thing composited over itself is the thing. What is observable is that
    ///     the <i>value</i> reached <c>UiBackdrop.Alpha</c> rather than the matrix, which is what this
    ///     asserts — a reader that put it in the matrix would have to have invented a fourth row.
    /// </remarks>
    [Fact]
    public void Opacity_lands_on_the_quads_alpha_rather_than_on_the_matrix() {
        using var ui = Glass("backdrop-filter: opacity(0.5);");

        var backdrop = Assert.NotNull(Assert.Single(ui.Geometry.Layers).Backdrop);

        Assert.Equal(0.5f, backdrop.Alpha, 3);
        Assert.Null(backdrop.Matrix);
        Assert.Equal(0f, backdrop.Blur);
    }

    /// <summary>Two functions compose into one backdrop, in the order CSS gives.</summary>
    /// <remarks>
    ///     ⚠ <b>A blur and a matrix are different fields and have to survive being written together</b>
    ///     — which is the shape every generated declaration has, since
    ///     <c>UtilityComposition.BackdropFilter</c> names all nine functions whatever the element wrote.
    /// </remarks>
    [Fact]
    public void A_blur_and_a_matrix_compose_into_one_backdrop() {
        using var ui = Glass("backdrop-filter: blur(2px) grayscale(1) opacity(0.25);");

        var backdrop = Assert.NotNull(Assert.Single(ui.Geometry.Layers).Backdrop);

        Assert.Equal(2f, backdrop.Blur, 3);
        Assert.Equal(0.25f, backdrop.Alpha, 3);
        Assert.NotNull(backdrop.Matrix);
    }

    /// <summary>An all-identity backdrop buys no surface, which every generated declaration is.</summary>
    /// <remarks>
    ///     ⚠ <b>The departure from CSS this shares with <c>filter</c>, and it costs more here.</b>
    ///     Filter Effects 2 makes any <c>backdrop-filter</c> other than <c>none</c> a backdrop root, so
    ///     a browser isolates for <c>blur(0)</c>. A group here costs a viewport-sized surface and a
    ///     pass, and a backdrop group costs a <i>second</i> surface and a second pass — and
    ///     <c>backdrop-blur-0</c> is a class somebody writes to turn the effect off.
    /// </remarks>
    [Theory]
    [InlineData("backdrop-filter: none;")]
    [InlineData("backdrop-filter: blur(0px);")]
    [InlineData("backdrop-filter: brightness(1);")]
    [InlineData("backdrop-filter: opacity(1);")]
    [InlineData("backdrop-filter: blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0);")]
    public void An_identity_backdrop_opens_no_group(string declaration) {
        using var ui = Glass(declaration);

        Assert.Empty(ui.Geometry.Layers);
        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
    }

    /// <summary>A backdrop list carrying a function this cannot run is refused whole.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rule the whole filter path keeps, and <c>drop-shadow()</c> is the row worth
    ///         having.</b> It is legal CSS inside a <c>backdrop-filter</c> and it is meaningless here:
    ///         a shadow of the backdrop is a silhouette composited under a picture that is already
    ///         behind everything. The plausible mistake is drawing it as the element's own shadow,
    ///         which would put a dark rectangle under every glass panel — so the declaration is refused
    ///         whole rather than partly applied, exactly as an unreadable <c>filter</c> is.
    ///     </para>
    ///     <para>
    ///         ⚠ The last two rows are arguments rather than functions, and a reader that clamped
    ///         instead of refusing would pass every other row.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("backdrop-filter: drop-shadow(2px 2px 4px black);")]
    [InlineData("backdrop-filter: blur(4px) drop-shadow(2px 2px 4px black);")]
    [InlineData("backdrop-filter: blur(4px) url(#thing);")]
    [InlineData("backdrop-filter: blur(nonsense);")]
    [InlineData("backdrop-filter: brightness(-1);")]
    [InlineData("backdrop-filter: hue-rotate(90);")]
    public void A_backdrop_carrying_a_function_the_engine_cannot_run_is_refused_whole(string declaration) {
        using var ui = Glass(declaration);

        Assert.Empty(ui.Geometry.Layers);
    }

    /// <summary><c>opacity()</c> is accepted by <c>backdrop-filter</c> and refused by <c>filter</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The asymmetry between the two properties, asserted from both sides in one place.</b>
    ///     Tailwind has a <c>backdrop-opacity-*</c> root and no <c>filter</c> spelling of it, and
    ///     <c>UtilityComposition.Filter</c> emits no <c>opacity()</c> — so accepting it there would be
    ///     a code path nothing generates, on a field <c>ElementFilter</c> would have to grow to hold.
    ///     The mirror row is in <c>FilterBlurTests</c>, which asserts the refusal.
    /// </remarks>
    [Fact]
    public void Opacity_is_a_backdrop_function_and_not_a_filter_one() {
        using var accepted = Glass("backdrop-filter: opacity(0.5);");
        using var refused = Glass("filter: opacity(0.5);");

        Assert.NotNull(Assert.Single(accepted.Geometry.Layers).Backdrop);
        Assert.Empty(refused.Geometry.Layers);
    }

    /// <summary>The backdrop's quad sits under the group's own, which is what "behind" means.</summary>
    /// <remarks>
    ///     ⚠ <b>Paint order is the whole of it: nothing in either executor knows a backdrop from a
    ///     nested group's composite.</b> Both are premultiplied surfaces sampled by a quad, so a
    ///     backdrop emitted after the composite would draw the blurred scene <i>over</i> the panel —
    ///     which is not a subtle error, and is exactly what a reader appending the quad would get.
    /// </remarks>
    [Fact]
    public void The_backdrops_quad_is_drawn_before_the_groups_own() {
        using var ui = Glass("backdrop-filter: invert(1);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.Equal(layer.First + layer.Count, layer.BackdropDraw);
        Assert.Equal(layer.BackdropDraw + 1, layer.Composite);

        Assert.Equal(layer.BackdropImage, ui.Geometry.Draws[layer.BackdropDraw].Image);
        Assert.Equal(layer.Image, ui.Geometry.Draws[layer.Composite].Image);
        Assert.NotEqual(layer.BackdropImage, layer.Image);
    }

    /// <summary>The three quads of one group are backdrop, then shadow, then composite.</summary>
    /// <remarks>
    ///     ⚠ <b>The order CSS paints them in, and each pair of it is a different picture the other way
    ///     round.</b> The backdrop under the shadow, because an element's own <c>drop-shadow</c> falls
    ///     on top of the glass rather than under it; both under the composite, because the element is
    ///     drawn over both. The arithmetic lives on <c>UiLayer</c> so that neither executor rederives
    ///     it, and this is what says the three cases of it agree.
    /// </remarks>
    [Fact]
    public void A_group_with_both_puts_the_backdrop_under_the_shadow() {
        using var ui = Glass("backdrop-filter: invert(1); filter: drop-shadow(3px 3px 0 #ff0000);");

        var layer = Assert.Single(ui.Geometry.Layers);

        Assert.NotNull(layer.Backdrop);
        Assert.NotNull(layer.Shadow);

        Assert.Equal(layer.First + layer.Count, layer.BackdropDraw);
        Assert.Equal(layer.BackdropDraw + 1, layer.ShadowDraw);
        Assert.Equal(layer.ShadowDraw + 1, layer.Composite);
    }

    /// <summary>Which matrix a function name stands for, for the theory above.</summary>
    static UiColorMatrix Matrix(string function) =>
        function switch {
            "brightness(0.6)" => UiColorMatrix.Brightness(0.6f),
            "contrast(1.4)" => UiColorMatrix.Contrast(1.4f),
            "grayscale(1)" => UiColorMatrix.Grayscale(1f),
            "invert(1)" => UiColorMatrix.Invert(1f),
            "saturate(1.8)" => UiColorMatrix.Saturate(1.8f),
            "sepia(1)" => UiColorMatrix.Sepia(1f),
            _ => UiColorMatrix.HueRotate(90f)
        };
}
