// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a declared transition actually runs, in a document, over frames.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about a value read <i>between</i> the two endpoints, and that
///         is the only shape that could have caught what it is guarding.</b>
///         <c>Vixen.Ui.Styling.Animator</c> has had three test files of its own since it was written
///         and every one of them passed; what none of them could see is that nothing in the
///         repository ever constructed one. A transition and its absence agree about where a property
///         starts and where it finishes and disagree only in the middle, so a test that checks the
///         destination passes against an engine with no animator at all — which is precisely what
///         <c>docs/plan/43</c> F10 found, and precisely how the three <c>transition-*</c> rows came to
///         sit in <c>UtilityFamilySupportTests.Supported</c> while nothing ran.
///     </para>
///     <para>
///         So these read the frame in flight: a width that is neither ten nor a hundred and ten, a
///         colour that is neither of the two the stylesheet names. Deleting any one of the four wires
///         — the animator on the engine, <c>Observe</c> in the updater, <c>Advance</c> in the tick,
///         <c>Apply</c> in the pass — puts the endpoint back and fails them.
///     </para>
///     <para>
///         The frames are driven by hand and the clock is an argument, which is
///         <c>UiDocument.Tick</c>'s whole design. Nothing here sleeps, nothing reads
///         <c>DateTime.Now</c>, and the same sequence of calls gives the same numbers on every machine
///         — see <see cref="A_transition_is_a_function_of_the_clock_and_not_of_the_frame_count" />,
///         which is the property that keeps a screenshot suite reproducible now that time can change
///         what a frame looks like.
///     </para>
/// </remarks>
public class TransitionTests {
    const string Css = """
        root  { width: 400px; height: 200px; }
        #box  { width: 10px; height: 20px; background-color: #000000;
                transition-property: width; transition-duration: 200ms;
                transition-timing-function: linear; }
        #box.wide { width: 110px; }
        """;

    /// <summary>Builds the document, settles it, and returns the element about to move.</summary>
    static UiElement Settled(UiDocument document, string css = Css) {
        document.Load(css);

        var box = document.Create("div", document.Root, "box");

        document.Tick(TimeSpan.Zero);
        document.Update();

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    /// <summary>
    ///     ⚠ <b>The proof, and it is the mid-flight width that is the proof.</b>
    /// </summary>
    /// <remarks>
    ///     Half way through a two-hundred-millisecond linear transition from ten pixels to a hundred
    ///     and ten, the box is sixty pixels wide. The bounds are loose on purpose — what is under test
    ///     is that the value is *travelling*, not the exact curve, which
    ///     <c>Vixen.Ui.Styling.Tests</c> already pins against the specification — but they exclude
    ///     both endpoints, which is the whole assertion.
    /// </remarks>
    [Fact]
    public void A_declared_transition_interpolates_across_ticks() {
        using var document = new UiDocument(400f, 200f);
        var box = Settled(document);

        Assert.Equal(10f, box.Width);

        // The class change is seen by the pass that follows it, and that pass is what starts the
        // transition — so the frame it lands on is still at the old value and time zero.
        box.AddClass("wide");
        Frame(document, 0.0);
        Assert.Equal(10f, box.Width);

        // A quarter through.
        Frame(document, 0.05);
        Assert.InRange(box.Width, 20f, 45f);

        // Half.
        Frame(document, 0.10);
        Assert.InRange(box.Width, 50f, 70f);

        // Three quarters, and strictly wider than half was.
        Frame(document, 0.15);
        Assert.InRange(box.Width, 75f, 100f);

        // Arrived, and not overshooting.
        Frame(document, 0.20);
        Assert.Equal(110f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>A transition that starts after the first frame is stamped with <i>then</i>, not with
    ///     zero.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The test above starts its fade at <c>t = 0</c>, which is where a clock that is never
    ///         advanced also is — so it holds against an engine that stamps every transition with
    ///         zero, and a sabotage that pins <c>StyleUpdater.Now</c> to <c>0f</c> passes it. That is
    ///         not a theoretical hole: an interface where nothing ever animates until the user has
    ///         been looking at it for a while is one where <i>every</i> transition begins already
    ///         finished, because the elapsed time on its first frame is however long the process has
    ///         been up.
    ///     </para>
    ///     <para>
    ///         So this one lets ten seconds pass before touching anything, and then asks the same
    ///         question. The stamp is the only thing that differs between it and the test above.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_transition_started_late_in_the_session_still_takes_its_full_duration() {
        using var document = new UiDocument(400f, 200f);
        var box = Settled(document);

        for (var frame = 1; frame <= 4; frame++) {
            Frame(document, 10.0 + frame * 0.016);
        }

        box.AddClass("wide");
        Frame(document, 10.1);
        Assert.Equal(10f, box.Width);

        // Half a duration after the change, not half a duration after the epoch.
        Frame(document, 10.2);
        Assert.InRange(box.Width, 50f, 70f);

        Frame(document, 10.3);
        Assert.Equal(110f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>The sabotage guard: the same document with the declaration removed must jump.</b>
    /// </summary>
    /// <remarks>
    ///     Without it the test above is satisfied by an engine that is simply slow to lay out, or by
    ///     a width that happens to be animated by something else. With it, the pair says the
    ///     difference is the three <c>transition-*</c> declarations and nothing else in the frame.
    /// </remarks>
    [Fact]
    public void The_same_change_without_a_transition_declaration_jumps() {
        using var document = new UiDocument(400f, 200f);

        var box = Settled(
            document,
            """
            root  { width: 400px; height: 200px; }
            #box  { width: 10px; height: 20px; }
            #box.wide { width: 110px; }
            """
        );

        box.AddClass("wide");
        Frame(document, 0.0);

        Assert.Equal(110f, box.Width);

        Frame(document, 0.05);
        Assert.Equal(110f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>A frame with nothing running must not be dirtied, or every document repaints for
    ///     ever.</b>
    /// </summary>
    /// <remarks>
    ///     <c>Tick</c> marks the document dirty while the animator has work, which is what makes a
    ///     transition advance without anything else changing. The failure mode of getting that
    ///     backwards is not a wrong picture but a permanent one: a UI that lays out and redraws sixty
    ///     times a second because it once faded something. So the settled document is ticked twice
    ///     more and asked whether either pass did any work.
    /// </remarks>
    [Fact]
    public void An_idle_document_does_no_work_on_a_tick() {
        using var document = new UiDocument(400f, 200f);
        var box = Settled(document);

        Frame(document, 0.1);
        Assert.False(document.Update());

        // And once the fade has finished, it goes quiet again rather than staying awake.
        box.AddClass("wide");

        for (var frame = 0; frame <= 20; frame++) {
            Frame(document, frame * 0.016);
        }

        Assert.Equal(110f, box.Width);

        document.Tick(TimeSpan.FromSeconds(1));
        Assert.False(document.Update());
    }

    /// <summary>
    ///     ⚠ <b>Colour too, because the interpolation and the round trip through CSS text are
    ///     different risks.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The animator hands its result back by writing the value as CSS and interning it, so a
    ///         property whose midpoint has no spelling — or whose spelling the draw list cannot read
    ///         back — arrives as a value that parses to nothing and paints as transparent. A length
    ///         survives that trivially; a colour is the one that exercises it, and it is also the
    ///         property a real interface actually transitions.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two endpoints are read out of the document rather than written down here, and
    ///         the first draft of this test was wrong for exactly that reason.</b> Comparing against
    ///         the literals <c>#000000</c> and <c>#ffffff</c> passes whatever happens: ExCSS
    ///         normalises a hex colour while parsing, so neither string is ever what the cascade
    ///         interns and both assertions hold against an engine with no animator at all. Asking the
    ///         document what it stored at each end makes the comparison one the sabotage can fail.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_colour_transition_paints_a_colour_that_is_neither_endpoint() {
        using var document = new UiDocument(400f, 200f);

        var box = Settled(
            document,
            """
            root  { width: 400px; height: 200px; }
            #box  { width: 50px; height: 20px; background-color: #000000;
                    transition-property: background-color; transition-duration: 200ms;
                    transition-timing-function: linear; }
            #box.lit { background-color: #ffffff; }
            """
        );

        var dark = Painted(document, box);

        box.AddClass("lit");
        Frame(document, 0.0);
        Frame(document, 0.10);

        var midway = Painted(document, box);

        Frame(document, 0.30);
        var light = Painted(document, box);

        // The endpoints really are two different colours, so the comparison below means something.
        Assert.NotEqual(dark, light);

        Assert.NotEqual(dark, midway);
        Assert.NotEqual(light, midway);
    }

    /// <summary>
    ///     ⚠ <b>A fading inherited value does not reach the children, and this pins the gap rather
    ///     than hiding it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The animator is a tier laid over the <i>finished</i> cascade: <c>StyleUpdater</c>
    ///         inherits from the parent's cascaded style, and <c>UiDocument.Apply</c> overlays the
    ///         in-flight values afterwards, per element. So a panel fading its <c>color</c> travels
    ///         while its descendants are handed the destination on the first frame — and a descendant
    ///         cannot start a fade of its own, because <c>transition-*</c> are not inherited
    ///         properties and it has no spec.
    ///     </para>
    ///     <para>
    ///         Measured, not argued: halfway through a black-to-white fade the panel reads
    ///         <c>rgba(99, 99, 99, 1)</c> — Oklab's midpoint — and the child reads
    ///         <c>rgb(255, 255, 255)</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A guard, so read the failure the right way round.</b> If this test goes red, the
    ///         likely cause is that somebody made the overlay participate in inheritance, which is the
    ///         fix — and the right response is to rewrite this test, not the code. It is here because
    ///         a limitation nothing asserts is a limitation that gets rediscovered as a bug, and
    ///         because fixing it means changing the order of the pass rather than the animator, which
    ///         deserves to argue with something.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inherited_value_reaches_the_children_at_its_destination_rather_than_mid_fade() {
        using var document = new UiDocument(400f, 200f);

        document.Load(
            """
            root   { width: 400px; height: 200px; }
            #panel { color: #000000; transition-property: color; transition-duration: 200ms;
                     transition-timing-function: linear; }
            #panel.lit { color: #ffffff; }
            #kid   { width: 10px; height: 10px; }
            """
        );

        var panel = document.Create("div", document.Root, "panel");
        var kid = document.Create("div", panel, "kid");

        document.Tick(TimeSpan.Zero);
        document.Update();

        panel.AddClass("lit");
        Frame(document, 0.0);
        Frame(document, 0.10);

        var colour = document.Styles.Properties.Lookup("color");

        Assert.True(panel.Style.TryGet(colour, out var travelling));
        Assert.True(kid.Style.TryGet(colour, out var arrived));

        // The panel is somewhere in between…
        Assert.NotEqual(
            document.Styles.Values.NameOf(arrived),
            document.Styles.Values.NameOf(travelling)
        );

        // …and the child is already at the end, which is the gap.
        Assert.Equal("rgb(255, 255, 255)", document.Styles.Values.NameOf(arrived));
    }

    /// <summary>The <c>background-color</c> the element is currently showing, as the cascade holds it.</summary>
    static string Painted(UiDocument document, UiElement element) {
        var background = document.Styles.Properties.Lookup("background-color");

        Assert.True(element.Style.TryGet(background, out var value));
        return document.Styles.Values.NameOf(value);
    }

    /// <summary>
    ///     ⚠ <b>Determinism, and it is a rule about this whole suite rather than about this test.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Time can now change what a frame looks like, which is a new hazard for every test that
    ///         compares one — a screenshot baseline captured mid-fade would be a baseline that depends
    ///         on how many frames the harness happened to run. The property that makes it safe is that
    ///         the animator is a pure function of the clock: it is <i>told</i> the time rather than
    ///         reading one, and it interpolates from a stamp rather than accumulating a delta.
    ///     </para>
    ///     <para>
    ///         So two documents driven to the same instant agree, however many ticks it took to get
    ///         there. A harness that renders one frame per event and a harness that renders sixty per
    ///         second produce the same picture for the same timestamp, and a suite that never advances
    ///         its clock never sees a transition at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_transition_is_a_function_of_the_clock_and_not_of_the_frame_count() {
        using var sparse = new UiDocument(400f, 200f);
        using var dense = new UiDocument(400f, 200f);

        var slow = Settled(sparse);
        var fast = Settled(dense);

        slow.AddClass("wide");
        fast.AddClass("wide");

        Frame(sparse, 0.0);
        Frame(dense, 0.0);

        // One long step against ten short ones, arriving at the same instant.
        Frame(sparse, 0.10);

        for (var frame = 1; frame <= 10; frame++) {
            Frame(dense, frame * 0.01);
        }

        Assert.Equal(slow.Width, fast.Width);
    }
}
