// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That <c>cqw</c>, <c>cqi</c>, <c>cqb</c>, <c>cqmin</c> and <c>cqmax</c> measure a container.</summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is a resolved width or height on an element inside a container of
///         a known size</b>, which is <see cref="ContainerWiringTests" />'s discipline and for its
///         reason: the failure this feature can have is a unit that parses, reaches the layout tree
///         and measures the wrong box, and a test that checks the parse cannot see it.
///     </para>
///     <para>
///         ⚠ <b>Nearly every case is a pair, because the wrong answer here is a <i>plausible</i>
///         number rather than nothing.</b> CSS Containment 3 § 5.3 makes a container unit outside
///         every container resolve against the viewport, so an implementation that never found a
///         container would answer every one of these with a viewport fraction — a box of a sensible
///         size, in a sensible place, wrong. The paired cases differ only in the container, so a
///         viewport-resolving engine gives both halves the same number and the pair goes red.
///     </para>
///     <para>
///         ⚠ <b>There is no assertion here on a <c>@container</c> rule, deliberately.</b> A container
///         unit needs no query: <c>container-type: inline-size</c> on a panel and <c>width: 50cqi</c>
///         on its child is a complete stylesheet, and the scope chain those rules run on is never
///         built for a document that declares no container group — <see cref="UiDocument" />'s
///         <c>Recontain</c> gives up first. So these fixtures declare no group, which is exactly the
///         arrangement that would have resolved against the viewport had the units been read off the
///         chain.
///     </para>
/// </remarks>
public class ContainerUnitTests {
    const float Tolerance = 0.001f;

    static UiDocument Document(string css, float width = 1000f, float height = 600f) {
        var document = new UiDocument(width, height);
        document.Load(css);

        return document;
    }

    /// <summary>⚠ The headline: one rule, two containers, two answers, and no query anywhere.</summary>
    [Fact]
    public void One_container_unit_answers_differently_in_two_containers_of_one_document() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; height: 100px; }
            .wide { width: 500px; }
            .narrow { width: 200px; }
            .body { width: 50cqi; height: 10px; }
            """
        );

        var roomy = document.Root.Add("div", classNames: ["panel", "wide"]);
        var cramped = document.Root.Add("div", classNames: ["panel", "narrow"]);

        var inRoomy = roomy.Add("div", classNames: "body");
        var inCramped = cramped.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(250f, inRoomy.Width, Tolerance);
        Assert.Equal(100f, inCramped.Width, Tolerance);
    }

    /// <summary>⚠ And with no container above it, a container unit is a viewport unit.</summary>
    /// <remarks>
    ///     CSS Containment 3 § 5.3, and it is the half that is easy to get wrong in the safe-looking
    ///     direction: answering zero would make an element with no container vanish, which reads as
    ///     a layout bug rather than as a unit that was never resolved. The <c>10vw</c> sibling is
    ///     what pins it to the viewport rather than to any number that happens to be 100.
    /// </remarks>
    [Fact]
    public void Outside_every_container_a_container_unit_is_a_viewport_unit() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .container-unit { width: 10cqw; height: 10px; }
            .viewport-unit { width: 10vw; height: 10px; }
            """
        );

        var container = document.Root.Add("div", classNames: "container-unit");
        var viewport = document.Root.Add("div", classNames: "viewport-unit");

        document.Update();

        Assert.Equal(100f, container.Width, Tolerance);
        Assert.Equal(viewport.Width, container.Width, Tolerance);
    }

    /// <summary>⚠ An <c>inline-size</c> container answers <c>cqi</c> and not <c>cqb</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The axis a container did not claim falls back to the viewport, not to that
    ///     container's height.</b> An <c>inline-size</c> container's height is still its content's —
    ///     that is the whole of what the keyword means — so reading it for <c>cqb</c> would be
    ///     reading a number the containment did not make well-defined, which is the same refusal
    ///     <c>ContainerQuery</c> makes for <c>(min-height:)</c> one layer up. The pair is what makes
    ///     it a test: the panel is 100 px tall and the viewport 600, so an implementation that
    ///     collapsed the two axes into one box gives 50 where this asks for 300.
    /// </remarks>
    [Fact]
    public void An_inline_size_container_leaves_the_block_axis_to_the_viewport() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 500px; height: 100px; }
            .body { width: 50cqi; height: 50cqb; }
            """
        );

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(250f, body.Width, Tolerance);
        Assert.Equal(300f, body.Height, Tolerance);
    }

    /// <summary>A <c>size</c> container answers both axes, from its own box.</summary>
    [Fact]
    public void A_size_container_answers_the_block_axis_from_its_own_box() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: size; width: 400px; height: 200px; }
            .body { width: 50cqi; height: 50cqb; }
            """
        );

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(200f, body.Width, Tolerance);
        Assert.Equal(100f, body.Height, Tolerance);
    }

    /// <summary>⚠ An inner <c>inline-size</c> container does not shadow an outer <c>size</c> one.</summary>
    /// <remarks>
    ///     ⚠ <b>The two axes can come from two different ancestors, and this is the case that says
    ///     so.</b> The nearest container on the inline axis is the inner panel; the nearest on the
    ///     block axis is the outer one, because the inner never claimed that axis. An implementation
    ///     carrying one box down the walk answers <c>cqb</c> from the inner panel's height — 60 here
    ///     rather than 100 — or from the viewport, 300. Three distinguishable numbers, which is why
    ///     the fixture is built with three distinguishable heights.
    /// </remarks>
    [Fact]
    public void The_two_axes_may_come_from_two_different_containers() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .outer { container-type: size; width: 800px; height: 200px; }
            .inner { container-type: inline-size; width: 400px; height: 120px; }
            .body { width: 50cqi; height: 50cqb; }
            """
        );

        var body = document.Root
            .Add("div", classNames: "outer")
            .Add("div", classNames: "inner")
            .Add("div", classNames: "body");

        document.Update();

        Assert.Equal(200f, body.Width, Tolerance);
        Assert.Equal(100f, body.Height, Tolerance);
    }

    /// <summary><c>cqmin</c> and <c>cqmax</c> take the smaller and larger of the two axes.</summary>
    [Fact]
    public void The_min_and_max_units_take_the_two_axes_of_the_container() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: size; width: 400px; height: 200px; }
            .smaller { width: 50cqmin; height: 10px; }
            .larger { width: 50cqmax; height: 10px; }
            """
        );

        var panel = document.Root.Add("div", classNames: "panel");
        var smaller = panel.Add("div", classNames: "smaller");
        var larger = panel.Add("div", classNames: "larger");

        document.Update();

        Assert.Equal(100f, smaller.Width, Tolerance);
        Assert.Equal(200f, larger.Width, Tolerance);
    }

    /// <summary>⚠ A container does not answer its own container units.</summary>
    /// <remarks>
    ///     CSS Containment 3: a query container contains its <i>descendants</i>, so a <c>50cqi</c> on
    ///     the element that declares <c>container-type</c> measures whatever contains that element —
    ///     here nothing, so the viewport. The alternative is a width defined in terms of itself, and
    ///     an implementation that let it happen would not throw: it would settle on whatever the
    ///     previous pass's box was, differently on every document.
    /// </remarks>
    [Fact]
    public void A_container_resolves_its_own_container_units_against_what_contains_it() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 50cqi; height: 100px; }
            """
        );

        var panel = document.Root.Add("div", classNames: "panel");

        document.Update();

        Assert.Equal(500f, panel.Width, Tolerance);
    }

    /// <summary>⚠ And a unit still converges in a document whose sheet DOES declare a query group.</summary>
    /// <remarks>
    ///     ⚠ <b>The branch every other fixture here avoids, and it was untested until this was
    ///     written.</b> Resolving a container unit takes a second settle pass, because styles are
    ///     built before layout runs. <c>WithContainerOf</c> asks for that pass only when
    ///     <c>Recontain</c> will not — an unconditional invalidate costs every document with a query
    ///     container a pass it does not need, which <see cref="ContainerWiringTests" /> measures — so
    ///     with a <c>@container</c> rule in the sheet the unit is relying on somebody else's
    ///     invalidation entirely. Every other fixture in this file declares no group and therefore
    ///     exercises the opposite branch.
    ///     <para>
    ///         The two assertions are the two mechanisms in one document: the query sets the body's
    ///         height and the unit sets its width, from the same box on the same pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_container_unit_converges_in_a_document_that_also_declares_a_query() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 500px; height: 100px; }
            .body { width: 50cqi; height: 10px; }
            @container (min-width: 400px) { .body { height: 33px; } }
            """
        );

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(250f, body.Width, Tolerance);
        Assert.Equal(33f, body.Height, Tolerance);
        Assert.True(document.Settled, "the document did not reach a fixed point");
    }

    /// <summary>⚠ The content box, not the border box — the same padding away as every query.</summary>
    /// <remarks>
    ///     <c>BoxOf</c> already subtracts padding and border for <c>@container</c>, and CSS
    ///     Containment 3 § 5.2 makes the units read the same box. Written because it is the one
    ///     number here that an implementation reading <c>element.Width</c> straight off gets wrong by
    ///     a plausible amount: 250 rather than 200.
    ///     <para>
    ///         ⚠ <c>box-sizing: border-box</c>, for the reason
    ///         <see cref="ContainerWiringTests.A_query_asks_about_the_content_box_and_not_the_border_box" />
    ///         gives and which cost this fixture a wrong expectation first: Vixen's default is
    ///         <c>content-box</c>, where <c>width: 500px</c> <i>is</i> the content and the padding is
    ///         outside it — so the two readings agree at 500 and nothing here could tell them apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_container_unit_measures_the_content_box() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel {
                container-type: inline-size;
                box-sizing: border-box;
                width: 500px;
                height: 100px;
                padding: 50px;
            }
            .body { width: 50cqi; height: 10px; }
            """
        );

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(200f, body.Width, Tolerance);
    }

    /// <summary>⚠ And a <c>translate</c> distance measures it too, which the sizing half cannot show.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second reader, seeded from a different context, and it is the one a container
    ///         unit can reach without anybody noticing.</b> Every assertion above resolves through
    ///         <c>UiDocument.Apply</c>, which is the walk <c>WithContainerOf</c> runs on;
    ///         <c>translate</c>, <c>transform</c> and <c>position: sticky</c> resolve in
    ///         <c>UiDocument.Accumulate</c>, which used to thread the bare surface context through the
    ///         whole tree. So the unit parsed, the reader accepted it, and the answer was a viewport
    ///         fraction — the exact failure this file's own header calls the plausible one, arriving
    ///         through the one path the header's fixtures cannot see.
    ///     </para>
    ///     <para>
    ///         The pair is what makes it an assertion rather than a coincidence: the same declaration
    ///         on a sibling with no container above it is 500, so an engine that resolved both against
    ///         the viewport gives 500 twice and an engine that resolved both against the container
    ///         gives 100 twice.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_translation_in_container_units_measures_the_container() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 200px; height: 100px; }
            .body { width: 10px; height: 10px; translate: 50cqi; }
            """
        );

        var inside = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");
        var outside = document.Root.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(100f, inside.AbsoluteLeft, Tolerance);
        Assert.Equal(500f, outside.AbsoluteLeft, Tolerance);
    }

    /// <summary>⚠ And a <c>transform</c> function's distance, which is the same context one reader on.</summary>
    /// <remarks>
    ///     <c>translate</c> lands in the accumulated position and <c>transform</c> lands in a matrix,
    ///     so they are two readers with two outputs sharing one <see cref="LengthContext" />. Asserted
    ///     separately because a fix that reached only the position would leave the matrix answering the
    ///     viewport, and nothing about the number 500 looks wrong in a matrix.
    /// </remarks>
    [Fact]
    public void A_transform_distance_in_container_units_measures_the_container() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 200px; height: 100px; }
            .body { width: 10px; height: 10px; transform: translateX(50cqi); }
            """
        );

        var inside = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");
        var outside = document.Root.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(100f, Assert.NotNull(inside.Transform).Dx, Tolerance);
        Assert.Equal(500f, Assert.NotNull(outside.Transform).Dx, Tolerance);
    }

    /// <summary>⚠ And a shadow's offset, which is a third seeding of the same context in the draw list.</summary>
    /// <remarks>
    ///     <c>DrawListBuilder</c> resolves lengths on a walk of its own, so it is a third place a
    ///     container unit can silently become a viewport unit — and the number it produces is an
    ///     offset in points, where nothing is out of range and nothing is logged. It built its context
    ///     from <c>UiDocument.Viewport</c> until #1345 and reads <c>UiElement.AppliedLengths</c> now;
    ///     <c>filter: drop-shadow()</c> and <c>filter: blur()</c> read the same value.
    /// </remarks>
    [Fact]
    public void A_shadow_offset_in_container_units_measures_the_container() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 200px; height: 100px; }
            .card {
                width: 50px;
                height: 20px;
                background-color: #ffffff;
                box-shadow: 0px 10cqi 0px #000000;
            }
            """
        );

        var card = document.Root.Add("div", classNames: "panel").Add("div", classNames: "card");

        document.Update();
        document.Draw();

        var shadow = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Shadow
        );

        // Ten hundredths of the 200px container is 20. Ten hundredths of the 1000px viewport is 100.
        Assert.Equal(card.AbsoluteTop + 20f, shadow.Y, Tolerance);
    }

    /// <summary>⚠ And <c>filter: blur()</c>'s radius, the one draw-list reader a8c0c6ab6 did not reach.</summary>
    /// <remarks>
    ///     The two shadow readers were given the element's recorded container; the blur reader one
    ///     screen further down the same file built its context from the viewport and the font alone,
    ///     so <c>blur(5cqi)</c> inside a 200px container was a fifty-point blur rather than a
    ///     ten-point one — a picture that is merely very soft, which is nothing anyone reports. Found
    ///     while fixing #1345, which moved all three onto the position walk's context.
    /// </remarks>
    [Fact]
    public void A_filter_blur_in_container_units_measures_the_container() {
        using var document = Document(
            """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 200px; height: 100px; }
            .card { width: 50px; height: 20px; background-color: #ffffff; filter: blur(5cqi); }
            """
        );

        document.Root.Add("div", classNames: "panel").Add("div", classNames: "card");

        document.Update();
        document.Draw();

        var layer = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.LayerPush && command.Blur > 0f
        );

        // Five hundredths of the 200px container is 10. Of the 1000px viewport, 50.
        Assert.Equal(10f, layer.Blur, Tolerance);
    }

    /// <summary>⚠ And the parent's <c>perspective</c> measures the parent's container, not the child's.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>perspective</c> is the one declaration <c>TransformReader</c> reads off an element
    ///         other than the one it is measuring, so it is the one place a per-element container
    ///         context has to be re-based rather than accepted — the same argument
    ///         <see cref="TransformTests.A_perspective_in_em_is_measured_in_the_parents_font" /> makes
    ///         for the font, one unit along. The stage below is <i>itself</i> the query container, so
    ///         the context its card resolves in carries the stage's own 200px box while the stage's
    ///         own <c>50cqw</c> is under no container at all and is half the viewport.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is what makes <c>UiElement.WithAppliedContainer</c> reset on the
    ///         no-container branch instead of passing its argument through.</b> Every other caller
    ///         hands it a context with no container in it, where a pass-through and a reset are the
    ///         same thing; this one hands it the child's, and a pass-through would read the stage's
    ///         <c>perspective: 50cqw</c> as 100 rather than 320 — a projection three times as strong
    ///         as authored, which is a plausible picture of a card tipped further away.
    ///     </para>
    ///     <para>
    ///         The numbers are <c>A_perspective_in_em_is_measured_in_the_parents_font</c>'s, because
    ///         the geometry is deliberately identical and only the way the 320 is spelled differs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_parent_perspective_in_container_units_measures_the_parents_container() {
        using var document = Document(
            """
            root { width: 640px; height: 300px; }
            .stage { position: absolute; left: 20px; top: 20px; width: 200px; height: 200px;
                     container-type: inline-size; perspective: 50cqw; }
            .card { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px;
                    background-color: #111111; transform: rotateX(60deg); }
            """,
            640f,
            300f
        );

        document.Root.Add("div", classNames: "stage").Add("div", classNames: "card");
        document.Update();

        var card = Assert.IsType<UiTransform>(document.Root.Children[0].Children[0].Transform);
        var at = card.Apply(new Vector2(70f, 120f));

        Assert.Equal(62.1756f, at.X, 0.01f);
        Assert.Equal(91.0878f, at.Y, 0.01f);
    }
}
