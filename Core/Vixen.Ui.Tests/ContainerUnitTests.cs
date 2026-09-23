// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
}
