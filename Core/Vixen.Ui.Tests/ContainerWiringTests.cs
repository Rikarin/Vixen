// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That <c>@container</c> answers in a live document, which for a day it could not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The cascade half of container queries shipped with nothing calling
///         <c>ContainerScopes.Enter</c>, so every element of every real document sat at the root
///         scope where no query has an eligible container and all of them are false.</b>
///         <c>ContainerQueryTests</c> in the styling project proves the verdict is right <i>given a
///         box</i>; it drives the boxes by hand, and had to, because no layout fed them. These
///         prove the box arrives.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is a resolved value on an element <i>inside</i> a container of a
///         given size, and nearly every one is paired with the same rule in a differently-sized
///         box.</b> The defect this file is written against is the one this subsystem keeps
///         producing — a query that silently never matches — and it is invisible to any test that
///         checks a rule parsed, a scope exists, or a selector is spelled right. Most of them assert
///         on a <i>width</i> rather than on a property nothing reads, so a green run means the
///         declaration reached the layout tree and moved a box.
///     </para>
///     <para>
///         ⚠ <b>The sabotage they are written against is the one the cascade half's own five
///         missed:</b> relax the name test so <c>@container card (…)</c> falls back to an unnamed
///         container and every other test in this file stays green.
///         <see cref="A_named_query_does_not_fall_back_to_an_unnamed_container_in_a_live_document" />
///         is the one that goes red. The others cover the wiring's own ways of being silently
///         wrong: reading the border box instead of the content box, letting a container answer its
///         own query, entering the scope before the boxes are final, and never re-entering it when
///         one moves.
///     </para>
/// </remarks>
public class ContainerWiringTests {
    /// <summary>A body that is 10 px wide in a cramped container and 300 in a roomy one.</summary>
    /// <remarks>
    ///     ⚠ The unconditional <c>.body</c> rule is what makes a failure legible: without it an
    ///     unmatched query gives a box of nought, which is also what a dozen unrelated layout faults
    ///     give.
    /// </remarks>
    const string Responsive = """
        root { width: 1000px; height: 600px; flex-direction: column; }
        .panel { container-type: inline-size; height: 100px; }
        .wide { width: 500px; }
        .narrow { width: 200px; }
        .body { width: 10px; height: 10px; }
        @container (min-width: 400px) { .body { width: 300px; } }
        """;

    static UiDocument Document(string css = Responsive, float width = 1000f) {
        var document = new UiDocument(width, 600f);
        document.Load(css);

        return document;
    }

    /// <summary>⚠ The headline: one rule, two boxes, one instant, two answers.</summary>
    /// <remarks>
    ///     Either half alone is satisfied by an engine that answers every element the same way and
    ///     happens to be right about that one — which is precisely the state the day before this,
    ///     where the answer was always "no". The pair is the test.
    /// </remarks>
    [Fact]
    public void One_rule_answers_differently_in_two_containers_of_one_document() {
        using var document = Document();

        var roomy = document.Root.Add("div", classNames: ["panel", "wide"]);
        var cramped = document.Root.Add("div", classNames: ["panel", "narrow"]);

        var inRoomy = roomy.Add("div", classNames: "body");
        var inCramped = cramped.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(300f, inRoomy.Width, 0.001f);
        Assert.Equal(10f, inCramped.Width, 0.001f);
    }

    /// <summary>⚠ And an element with no query container above it matches nothing.</summary>
    /// <remarks>
    ///     CSS Containment 3 § 5.1: a query with no eligible container resolves to false. This is
    ///     also the state the whole document was in before the wiring, so it is the assertion that
    ///     would still have passed — kept because the two above it are what make it mean something.
    /// </remarks>
    [Fact]
    public void An_element_with_no_container_above_it_matches_nothing() {
        using var document = Document();
        var loose = document.Root.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(10f, loose.Width, 0.001f);
    }

    /// <summary>⚠ A container does not answer its own query, CSS Containment 3 § 5.1.</summary>
    /// <remarks>
    ///     The failure mode is a query that matches slightly <i>too</i> often, which every test of
    ///     the ordinary case passes through — so it gets its own, in the live document as well as in
    ///     the cascade, because the two slots on <c>StyleTree</c> that keep it true are written by
    ///     the walk in <c>Containers.cs</c> and a walk that wrote one of them would be caught by
    ///     nothing else here.
    /// </remarks>
    [Fact]
    public void A_container_does_not_answer_its_own_query_in_a_live_document() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 500px; }
            .body { height: 10px; }
            @container (min-width: 400px) { .body { height: 40px; } }
            """);

        var panel = document.Root.Add("div", classNames: ["panel", "body"]);
        var child = panel.Add("div", classNames: "body");

        document.Update();

        // ⚠ The panel carries `.body` too, so it is the *same rule* on the *same class* in the two
        // places — which is what makes the pair mean "inside" rather than "some other declaration
        // won". The panel gets the unconditional 10 because there is no container above it; its
        // child gets 40 because there is.
        Assert.Equal(10f, panel.Height, 0.001f);
        Assert.Equal(40f, child.Height, 0.001f);
    }

    /// <summary>⚠ The sabotage the cascade half's own five missed, now in a live document.</summary>
    /// <remarks>
    ///     Relaxing the name test so a named query falls back to whatever container is nearest left
    ///     every cascade test green, and it is the worst of the set because it is right until
    ///     somebody adds a wrapper. The live version is not redundant with the cascade's: the name
    ///     now comes out of a <c>container-name</c> declaration rather than out of a test helper's
    ///     argument, so a wiring that read the property and threw the string away — or lower-cased
    ///     it, or read the shorthand's half — would pass everything else here.
    /// </remarks>
    [Fact]
    public void A_named_query_does_not_fall_back_to_an_unnamed_container_in_a_live_document() {
        const string css = """
            root { width: 1000px; height: 600px; flex-direction: column; }
            .anonymous { container-type: inline-size; width: 900px; height: 100px; }
            .card { container-type: inline-size; container-name: card; width: 900px; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container card (min-width: 400px) { .body { width: 300px; } }
            """;

        using var document = Document(css);

        var anonymous = document.Root.Add("div", classNames: "anonymous");
        var inAnonymous = anonymous.Add("div", classNames: "body");

        var named = document.Root.Add("div", classNames: "card");
        var inNamed = named.Add("div", classNames: "body");

        document.Update();

        // Wide enough, and not called `card`, so it is not the box the query is about.
        Assert.Equal(10f, inAnonymous.Width, 0.001f);

        // The same width with the name it asked for does match, so the 10 above is the name and not
        // the width.
        Assert.Equal(300f, inNamed.Width, 0.001f);
    }

    /// <summary>⚠ A named query asks the container with that name, not the nearest one.</summary>
    [Fact]
    public void A_named_query_reaches_past_a_nearer_container() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .outer { container-type: inline-size; container-name: outer; width: 900px; height: 200px; }
            .inner { container-type: inline-size; container-name: inner; width: 100px; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container outer (min-width: 400px) { .body { width: 50px; } }
            """);

        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");
        var leaf = inner.Add("div", classNames: "body");

        document.Update();

        // 50, not 10: the walk skipped the 100 px box it is directly inside and read `outer`'s 900.
        Assert.Equal(50f, leaf.Width, 0.001f);
    }

    /// <summary>⚠ The test that fails the day a query stops matching: resize, and the verdict flips.</summary>
    /// <remarks>
    ///     <b>The whole feature, in the arrangement it exists for.</b> A panel in a dock has no width
    ///     of its own — it takes <c>SizingMode.StretchFit</c> from its parent — so the container's
    ///     box is the window's, and this is a window being dragged narrower. Both directions are
    ///     asserted, because a wiring that entered a scope once and never re-entered it would pass
    ///     the first two assertions and fail only the third.
    /// </remarks>
    [Fact]
    public void Resizing_the_window_flips_a_container_query_and_flips_it_back() {
        using var document = Document("""
            root { width: 100%; height: 100%; flex-direction: column; }
            .panel { container-type: inline-size; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 300px; } }
            """);

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();
        Assert.Equal(300f, body.Width, 0.001f);

        document.Resize(320f, 600f);
        document.Update();
        Assert.Equal(10f, body.Width, 0.001f);

        document.Resize(1000f, 600f);
        document.Update();
        Assert.Equal(300f, body.Width, 0.001f);
    }

    /// <summary>⚠ A stretch-fit container reaches its verdict in exactly one extra settle pass.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The convergence question, answered as a number rather than as an argument.</b> The
    ///         verdict depends on a measured box, so style decides layout and layout decides style —
    ///         and the reason that is not a cycle here is that <c>width: auto</c> on a normal-flow
    ///         block takes <c>SizingMode.StretchFit</c> and is sized by its parent with no child
    ///         consulted. So the second pass measures the same box the first one did, interns the
    ///         same scope, moves nothing, and stops.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One and not two, and not nought.</b> Nought would mean the query was answered
    ///         before any box existed, which is the wiring being in the wrong place; two would mean
    ///         the container's own size moved in response to its descendants' styles, which for this
    ///         arrangement it must not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stretch_fit_container_settles_in_one_extra_pass() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 300px; } }
            """);

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(300f, body.Width, 0.001f);
        Assert.True(document.Settled, "the container query did not reach a fixed point");
        Assert.Equal(1, document.SettlingPasses);
    }

    /// <summary>⚠ A container inside a container resolves, and costs one pass per level of nesting.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The case that decides where the walk lives.</b> The outer query sets the inner
    ///         container's <i>width</i>, so the inner container's box is not known until the cascade
    ///         has run with the outer verdict in hand — which is a second layout, and therefore a
    ///         second scope assignment. A walk called once per <see cref="UiDocument.Update" /> rather
    ///         than at the end of every <c>Arrange</c> would enter the inner chain off the width it
    ///         had before the outer query fired and show the leaf a frame late; every other test in
    ///         this file passes under that arrangement, because every other one is one level deep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the pass count is the cost, stated rather than discovered.</b> Two levels of
    ///         nesting take two extra passes, so <c>SettlePasses</c> of three is a depth limit of
    ///         three as well as a handler limit — a fourth level of size-dependent nesting would
    ///         report <see cref="UiDocument.Settled" /> false rather than hang. Nothing in the editor
    ///         is two deep today, which is why this is a documented ceiling and not a defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_container_inside_a_container_resolves_and_costs_a_pass_per_level() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .outer { container-type: inline-size; container-name: outer; width: 900px; height: 300px; }
            .inner { container-type: inline-size; container-name: inner; width: 100px; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container outer (min-width: 400px) { .inner { width: 500px; } }
            @container inner (min-width: 400px) { .body { width: 77px; } }
            """);

        var inner = document.Root.Add("div", classNames: "outer").Add("div", classNames: "inner");
        var body = inner.Add("div", classNames: "body");

        document.Update();

        // The outer query widened the inner container past its own threshold, and the leaf saw it.
        Assert.Equal(500f, inner.Width, 0.001f);
        Assert.Equal(77f, body.Width, 0.001f);

        Assert.True(document.Settled, "two levels of container did not reach a fixed point");
        Assert.Equal(2, document.SettlingPasses);
    }

    /// <summary>⚠ And a frame in which nothing moved interns no chain and needs no extra pass.</summary>
    /// <remarks>
    ///     <b>The other half of the bound, and the one that decides whether the feature is
    ///     affordable.</b> Scopes are interned by value and nothing evicts them below
    ///     <see cref="UiDocument.ContainerScopeCeiling" />, so a document that interned a chain per
    ///     frame while standing still would grow a table for the length of the session and pay a
    ///     cold cascade every time. Interning by value is exactly what makes this nought: the same
    ///     box hashes to the same chain.
    /// </remarks>
    [Fact]
    public void A_settled_frame_interns_nothing_and_costs_no_extra_pass() {
        using var document = Document();

        document.Root.Add("div", classNames: ["panel", "wide"]).Add("div", classNames: "body");
        document.Update();

        // Something has to happen for `Update` to run at all, and a position change is the cheapest
        // thing that is not a restyle.
        document.Invalidate();
        document.Update();

        Assert.Equal(0, document.ContainerScopesEntered);
        Assert.Equal(0, document.SettlingPasses);
        Assert.True(document.Settled);
    }

    /// <summary>⚠ The ceiling fires, and the document still answers on the frame it fires on.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The eviction policy had never once run.</b>
    ///         <see cref="UiDocument.ContainerScopeCeiling" /> is four thousand and ninety-six, and
    ///         reaching it takes about a minute of continuous dragging — so every test in this file
    ///         and every document in this repository took the branch above it, and the branch that
    ///         resets the table was code nothing had executed. That is the shape this repository
    ///         calls a finished thing nothing calls: it is not a claim about whether the number is
    ///         right, it is that <c>Reset</c> is documented as safe in exactly one order and nothing
    ///         had ever checked that the caller uses that order.
    ///     </para>
    ///     <para>
    ///         A drag is one chain per pixel per frame because the scopes are interned <i>by
    ///         value</i>, so a window widened a pixel at a time is the cheapest way to reach it —
    ///         and it is also the thing the policy exists for rather than a contrivance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is the table's size and then a verdict, in that order and both.</b>
    ///         The size alone would pass against a <c>Reset</c> that forgot to re-assign — every
    ///         element would be left pointing at a scope that no longer exists, which
    ///         <c>VerdictsOf</c> answers conservatively rather than throwing for, so the failure
    ///         would be a silently unstyled document and not a crash. The verdict alone would pass
    ///         against no ceiling at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_scope_table_is_rebuilt_at_the_ceiling_and_the_document_still_answers() {
        using var document = Document(
            """
            root { width: 100%; height: 100%; flex-direction: column; }
            .panel { container-type: inline-size; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 300px; } }
            """,
            width: 500f
        );

        var body = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");

        document.Update();
        Assert.Equal(300f, body.Width, 0.001f);

        // One pixel a frame, which is a drag, and the frame the table shrinks on is the one the
        // policy is about.
        var peak = 0;
        var passes = -1;
        var settled = false;

        for (var width = 501; width <= 500 + UiDocument.ContainerScopeCeiling + 4; width++) {
            var held = document.Styles.ContainerScopes.Count;

            document.Resize(width, 600f);
            document.Update();

            if (document.Styles.ContainerScopes.Count >= held) {
                continue;
            }

            peak = held;
            passes = document.SettlingPasses;
            settled = document.Settled;

            break;
        }

        // ⚠ The table never carries more than the ceiling <i>across a frame boundary</i>, which is
        // one less than the number the branch tests: the settle loop arranges twice, so the chain
        // that trips it is interned and the rebuild fires inside the same `Update`.
        Assert.Equal(UiDocument.ContainerScopeCeiling, peak);

        // ⚠ <b>Two extra passes, and this is the assertion that says "reset, re-assign, re-cascade"
        // rather than "reset and hope".</b> One for the drag, which moved a box and so moved a
        // scope, and one for the rebuild. Dropping the re-assign from the branch leaves the
        // document *correct* — the settle loop's next pass re-assigns everything anyway — so the
        // width below stays right and only this number moves, from two to three. A version of this
        // test that asserted the width alone passed that sabotage, which is how it came to be
        // written twice.
        Assert.Equal(2, passes);
        Assert.True(settled, "the frame the table was rebuilt on did not reach a fixed point");

        // And the document still answers, before and after the rebuild's cold cascade.
        Assert.Equal(300f, body.Width, 0.001f);

        document.Resize(320f, 600f);
        document.Update();
        Assert.Equal(10f, body.Width, 0.001f);
    }

    /// <summary>⚠ A document with no <c>@container</c> in it does not walk, and does not intern.</summary>
    /// <remarks>
    ///     The <c>if</c> that keeps the feature free for every sheet in this repository. It is worth
    ///     a test because it is a way for the wiring to be silently absent: widen it by one and the
    ///     walk never runs for anybody.
    /// </remarks>
    [Fact]
    public void A_document_with_no_container_rule_enters_no_scope_even_for_a_declared_container() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; width: 500px; height: 100px; }
            """);

        document.Root.Add("div", classNames: "panel").Add("div");
        document.Update();

        Assert.Equal(0, document.ContainerScopesEntered);

        // And a sheet that adds one makes the same document start walking, so the branch above is a
        // shortcut and not a switch that has to be thrown.
        document.Load("@container (min-width: 400px) { div { height: 40px; } }");
        document.Update();

        Assert.Equal(1, document.ContainerScopesEntered);
    }

    /// <summary>⚠ The query is about the content box, so padding moves the threshold.</summary>
    /// <remarks>
    ///     CSS Containment 3 § 5.2. Reading <c>GetWidth</c> and stopping there is the obvious wiring
    ///     and is wrong by exactly one padding — which reads as the author having mis-picked their
    ///     breakpoint rather than as a bug here, so it would survive. Two panels of the same border
    ///     box, one of which has padding enough to fall under the threshold.
    ///     <para>
    ///         ⚠ <c>box-sizing: border-box</c> on both, deliberately, because Vixen's default is
    ///         <c>content-box</c> — where <c>width: 420px</c> <i>is</i> the content and padding adds
    ///         to the outside, so the two spellings agree and the test could not tell them apart. It
    ///         is the border-box case that has two different numbers to pick from, and it is also the
    ///         case every real theme is written in.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_query_asks_about_the_content_box_and_not_the_border_box() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { container-type: inline-size; box-sizing: border-box; width: 420px; height: 100px; }
            .padded { padding-left: 20px; padding-right: 20px; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 50px; } }
            """);

        var bare = document.Root.Add("div", classNames: "panel").Add("div", classNames: "body");
        var padded = document.Root.Add("div", classNames: ["panel", "padded"]).Add("div", classNames: "body");

        document.Update();

        // 420 of content clears 400.
        Assert.Equal(50f, bare.Width, 0.001f);

        // 420 of border box is 380 of content, which does not.
        Assert.Equal(10f, padded.Width, 0.001f);
    }

    /// <summary>⚠ <c>inline-size</c> refuses a block-axis query and <c>size</c> answers it.</summary>
    /// <remarks>
    ///     The containment is the point of the keyword rather than a label on it: an
    ///     <c>inline-size</c> container's height is still its content's, so a height read off it is
    ///     a number the containment did not make well-defined. The pair is what proves the refusal is
    ///     the keyword and not the height.
    /// </remarks>
    [Fact]
    public void An_inline_size_container_refuses_a_height_query_that_a_size_container_answers() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .inline { container-type: inline-size; width: 500px; height: 300px; }
            .both { container-type: size; width: 500px; height: 300px; }
            .body { width: 10px; height: 10px; }
            @container (min-height: 200px) { .body { width: 60px; } }
            """);

        var inInline = document.Root.Add("div", classNames: "inline").Add("div", classNames: "body");
        var inBoth = document.Root.Add("div", classNames: "both").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(10f, inInline.Width, 0.001f);
        Assert.Equal(60f, inBoth.Width, 0.001f);
    }

    /// <summary>⚠ The <c>container</c> shorthand is read, and a name on its own is not a container.</summary>
    /// <remarks>
    ///     ExCSS expands no shorthand, so <c>container: card / inline-size</c> arrives as one
    ///     declaration under a property nobody would otherwise read — the exact spelling the
    ///     specification's own examples use, and therefore the one most likely to be written and
    ///     silently ignored. The second half is the trap in the shorthand: <c>container: card</c>
    ///     with no slash sets <c>container-type: normal</c>, so naming a box does not make it
    ///     answerable.
    /// </remarks>
    [Fact]
    public void The_container_shorthand_makes_a_container_and_a_bare_name_does_not() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .short { container: card / inline-size; width: 900px; height: 100px; }
            .bare { container: card; width: 900px; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container card (min-width: 400px) { .body { width: 70px; } }
            """);

        var inShorthand = document.Root.Add("div", classNames: "short").Add("div", classNames: "body");
        var inBare = document.Root.Add("div", classNames: "bare").Add("div", classNames: "body");

        document.Update();

        Assert.Equal(70f, inShorthand.Width, 0.001f);
        Assert.Equal(10f, inBare.Width, 0.001f);
    }

    /// <summary>⚠ An element that stops being a container stops being asked.</summary>
    /// <remarks>
    ///     <b>The stale-slot case, and the reason the walk writes both of <c>StyleTree</c>'s two
    ///     container slots for every element rather than only for the containers.</b> A walk that
    ///     wrote a provided scope and never cleared one would leave a box that no longer declares
    ///     <c>container-type</c> still handing its old chain down — a container that was removed by a
    ///     class and went on answering, which is the failure mode that hides.
    /// </remarks>
    [Fact]
    public void Removing_a_container_type_stops_the_query_matching() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            .panel { width: 900px; height: 100px; }
            .contains { container-type: inline-size; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 80px; } }
            """);

        var panel = document.Root.Add("div", classNames: ["panel", "contains"]);
        var body = panel.Add("div", classNames: "body");

        document.Update();
        Assert.Equal(80f, body.Width, 0.001f);

        panel.RemoveClass("contains");
        document.Update();

        Assert.Equal(10f, body.Width, 0.001f);
    }

    /// <summary>⚠ A second window starts its own chain rather than inheriting the main one's.</summary>
    /// <remarks>
    ///     A surface root is a child of an element of the document's tree — that is what keeps one
    ///     theme across a torn-off panel — but it is not <i>inside</i> that element's box in any
    ///     sense a size query can be about. Inheriting the chain would have a floating inspector
    ///     answering <c>@container</c> off the dock it was pulled out of, which is a wrong answer
    ///     that looks plausible: the palette would simply lay itself out as though it were still
    ///     docked.
    /// </remarks>
    [Fact]
    public void A_torn_off_window_does_not_inherit_the_main_windows_container() {
        using var document = Document("""
            root { width: 1000px; height: 600px; flex-direction: column; }
            ui-surface { flex-direction: column; }
            .panel { container-type: inline-size; width: 900px; height: 400px; }
            .body { width: 10px; height: 10px; }
            @container (min-width: 400px) { .body { width: 90px; } }
            """);

        var panel = document.Root.Add("div", classNames: "panel");
        var docked = panel.Add("div", classNames: "body");

        // Created under the wide panel, and so inside it for inheritance and outside it for a query.
        var torn = document.CreateSurface(300f, 200f, owner: panel).Root.Add("div", classNames: "body");

        document.Update();

        Assert.Equal(90f, docked.Width, 0.001f);
        Assert.Equal(10f, torn.Width, 0.001f);
    }

    /// <summary>A container sized by its contents oscillates, and the log names <i>it</i>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The failure doc 43 § D3 predicted, and until now the only report of it was a
    ///         document-level boolean.</b> <c>.seesaw</c> is a flex item with no width, so its inline
    ///         size is its content's; the query fires when it is narrow and widens the content, the
    ///         wider content widens the container past the threshold, and the next pass takes the
    ///         width away again. The loop does not hang — it exhausts <see cref="UiDocument.SettlePasses" />
    ///         and reports <see cref="UiDocument.Settled" /> false — but "this document did not
    ///         settle" is not a thing anybody can go and fix, and a real interface has dozens of
    ///         containers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted, and the boolean alone is the weaker one.</b> It was
    ///         already true before this walk recorded anything, so a test asserting only
    ///         <c>Settled == false</c> passes against a document that says nothing at all. The
    ///         message has to name the container, which is why the fixture gives it a
    ///         <c>container-name</c>: that is the string an author can search their stylesheet for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the diagnostic half of the owed containment coercion and not the
    ///         coercion.</b> The box still oscillates; what changed is that it is named. Forcing
    ///         <c>SizingMode.StretchFit</c> on a contained node is still owed under A16.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_container_sized_by_its_contents_never_settles_and_the_log_names_it() {
        var sink = new RingBufferSink(64);

        using var document = new UiDocument(1000f, 600f, logger: sink.CreateLogger("Vixen.Ui.Styling"));

        document.Load("""
            root { width: 1000px; height: 600px; flex-direction: row; }
            .seesaw { container-type: inline-size; container-name: seesaw; height: 100px; }
            .body { width: 10px; height: 10px; }
            @container seesaw (max-width: 100px) { .body { width: 900px; } }
            """);

        document.Root.Add("div", classNames: "seesaw").Add("div", classNames: "body");
        document.Update();

        Assert.False(document.Settled, "the fixture converged, so it is not measuring an oscillation");

        var warning = Assert.Single(
            sink.Snapshot(),
            record => record.Level >= LogLevel.Warning && record.EventId.Id == 7007
        );

        Assert.Contains("seesaw", warning.Message, StringComparison.Ordinal);
        Assert.Contains("never settled", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>And a document that settles says nothing, so the channel stays worth reading.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the sabotage.</b> Every container in a fresh document moves on its
    ///     first pass — a chain entered from the root scope has moved by definition — so a reporter
    ///     wired anywhere but the branch that gives up would write a line for every container in
    ///     every document, on the frame it was built. That version passes the test above.
    /// </remarks>
    [Fact]
    public void A_container_that_settles_writes_nothing() {
        var sink = new RingBufferSink(64);

        using var document = new UiDocument(1000f, 600f, logger: sink.CreateLogger("Vixen.Ui.Styling"));

        document.Load(Responsive);
        document.Root.Add("div", classNames: ["panel", "wide"]).Add("div", classNames: "body");
        document.Update();

        Assert.True(document.Settled);
        Assert.DoesNotContain(sink.Snapshot(), record => record.EventId.Id == 7007);
    }
}
