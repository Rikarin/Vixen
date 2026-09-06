// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The gate: a utility family may not emit a property nothing in the engine acts on.</summary>
/// <remarks>
///     <para>
///         <b>The requirement is <c>docs/plan/43</c> § Part 5 and exit criterion 2</b>, and its one
///         sentence is the whole of this file: <i>a family that emits a property nothing reads is a
///         named gap in the engine, not a smaller family table.</i> Before this existed, a family
///         could be added, emit valid CSS, resolve cleanly on a real element and do nothing at all,
///         and the only thing that noticed was a person doing a survey by hand. Eighteen properties
///         were in that state when the survey was written; the survey took a week and rots the moment
///         the next family lands.
///     </para>
///     <para>
///         ⚠ <b>"It resolves" is the exact conflation this gate exists to catch, and it is the easy
///         test to write by mistake.</b> Every inert family emits syntactically valid CSS and the
///         cascade computes a value for it, so <c>StyleOf(element, property) is not null</c> passes
///         for all of them. <see cref="Vixen.Ui.Styling.Utilities.Tests" />' own
///         <c>UtilityFixture.Computed</c> answers that question and is right to. This one answers a
///         different one.
///     </para>
///     <para>
///         ⚠ <b>And "interned" is not "acted on" either — the difference was measured at seven
///         properties.</b> <c>LayoutStyleBuilder</c> interns <c>word-spacing</c> and exposes an id
///         for it that nothing reads; <c>InheritedProperties</c>
///         interns another five purely so the cascade knows they inherit. A gate that looked for an
///         <c>Intern("…")</c> call would pass every one of those. See
///         <see cref="An_interned_property_no_consumer_acts_on_reads_as_inert" />, which holds this
///         method to that distinction using the two properties known to be on the wrong side of it.
///     </para>
///     <para>
///         <b>How "acted on" is established: by changing the property and watching the engine.</b>
///         The measurement is a differential. A scene is built twice — once plain, once with one
///         extra declaration on the probe element — and four observables are compared:
///         <c>layout</c> (every element's rectangle), <c>paint</c> (the whole draw list, its glyph,
///         path and box-style side buffers included), <c>cursor</c> (<see cref="UiDocument.CursorOf" />)
///         and <c>hit</c> (<see cref="UiDocument.HitTest" /> over a grid). A property that moves none
///         of the four, in any scene, at any value a utility can give it, is not acted on by anything.
///     </para>
///     <para>
///         ⚠ <b>No text search is involved anywhere in this file, and that is deliberate rather than
///         incidental.</b> A raw NUL byte in a <c>.cs</c> string literal makes the file <i>binary</i>
///         to <c>grep</c>, which skips it silently and exits 1 — indistinguishable from no match. That
///         produced two confident wrong "this is dead code" answers in this repository in one week,
///         `ShorthandExpansion` being the one <c>docs/plan/43</c> § Part 0 records. Everything here is
///         either the registry's own data or a frame the engine actually ran, so the failure mode does
///         not exist. The one file this reads as text is the plan document, and a false negative there
///         fails the build rather than passing it.
///     </para>
///     <para>
///         <b>What it cannot see, stated rather than left to be discovered.</b> The verdict is
///         per-property and per-<i>engine</i>: it says something acts on the property, not that
///         everything that ought to does. <c>border-left-width</c> passed this gate for as long as the
///         layout inset the content box and the draw list painted nothing — <c>docs/plan/43</c> F1 —
///         because the layout is something. Partial support is representable here only in that the
///         gate works one longhand at a time, so a family whose widths are read and whose colours are
///         not fails on the colours; the finer claim, that the <i>right</i> consumer read it, is what
///         <c>Editor/Vixen.Editor.Ui.Tests/UtilityFamilySupportTests</c> is for, and its per-edge and
///         per-corner <c>Fact</c>s are exactly that claim made by hand. Neither file subsumes the
///         other: this one guarantees nothing escapes classification, that one guarantees the
///         classification means what it says.
///     </para>
/// </remarks>
public class UtilityConsumptionGateTests {
    /// <summary>The gate. Every property any utility can emit is acted on, or allow-listed.</summary>
    /// <remarks>
    ///     One <c>Fact</c> rather than a theory over properties, because the failure worth reading is
    ///     the whole set at once — "these four properties emit and nothing reads them, and here is the
    ///     allow-list they are not on" — and a theory would report it as four unrelated red rows with
    ///     no way to see that a fifth had just been fixed.
    /// </remarks>
    [Fact]
    public void No_family_emits_a_property_nothing_in_the_engine_acts_on() {
        var allowed = InertAllowList.Load();
        var measured = UtilityConsumptionProbe.Take();

        var unexplained = measured.Inert
            .Where(entry => !allowed.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexplained.Count == 0,
            $"""
             {unexplained.Count} property(ies) are emitted by a utility family and nothing in the engine
             acts on them. Either teach a consumer to read them, or add a line to
             Core/Vixen.Ui.Styling.Utilities.Tests/InertProperties.txt naming the task that will:

             {string.Join("\n", unexplained.Select(entry => $"  {entry.Key,-32} emitted by {string.Join(", ", entry.Value.Take(4))}"))}
             """
        );
    }

    /// <summary>An allow-list entry whose property is now acted on has expired and must go.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the expiry, and it is the whole reason this list is not
    ///         <c>docs/DocsExempt.txt</c>.</b> That file's own instruction is "do not add a line to
    ///         DocsExempt.txt to make a build pass", and it still rotted — because nothing in the build
    ///         ever notices when an exempted type <i>gains</i> a page, so a line outlives its reason
    ///         silently and for ever. Here the line dies the moment the gap closes: the run that makes
    ///         a consumer read the property is the run that fails on its exemption.
    ///     </para>
    ///     <para>
    ///         <b>A date was considered as the expiry and rejected.</b> A date that passes fails the
    ///         build for everybody on a day nobody chose, and the cheapest way out is to bump the
    ///         date — which is the abuse pattern, made mandatory. An expiry that fires on the
    ///         <i>condition</i> rather than on the calendar cannot be bumped, because the only way to
    ///         silence it is to have fixed the thing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_allow_list_entry_outlives_the_gap_it_names() {
        var allowed = InertAllowList.Load();
        var measured = UtilityConsumptionProbe.Take();

        var stale = allowed.Keys
            .Where(property => measured.Consumers.ContainsKey(property))
            .OrderBy(property => property, StringComparer.Ordinal)
            .Select(property => $"  {property,-32} is now read by {string.Join(", ", measured.Consumers[property])}")
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"""
             {stale.Count} allow-list entry(ies) in InertProperties.txt name a property the engine now
             acts on. The gap closed; delete the line in the same commit that closed it:

             {string.Join("\n", stale)}
             """
        );
    }

    /// <summary>An allow-list entry for a property nothing emits any more is dead and must go.</summary>
    /// <remarks>
    ///     The other direction of rot, and the cheaper one: a family removed or renamed leaves its
    ///     exemption behind, and a list with dead lines in it stops being a readable statement of what
    ///     the engine owes. Checked against the same measurement rather than against a second list.
    /// </remarks>
    [Fact]
    public void No_allow_list_entry_names_a_property_no_family_emits() {
        var allowed = InertAllowList.Load();
        var measured = UtilityConsumptionProbe.Take();

        var dead = allowed.Keys
            .Where(property => !measured.Emitted.Contains(property))
            .OrderBy(property => property, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dead.Count == 0,
            $"""
             {dead.Count} allow-list entry(ies) in InertProperties.txt name a property no utility family
             emits. Delete them:

             {string.Join("\n", dead.Select(property => $"  {property}"))}
             """
        );
    }

    /// <summary>Every composed fragment is assembled, and the property it assembles into is explained.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The clause that stops "composed" being a hiding place.</b> A fragment escapes the
    ///         differential — it is half a declaration, and half a declaration moves nothing — so
    ///         something has to hold the other half accountable, or registering a
    ///         <see cref="UtilityComposition" /> fragment would become the cheapest way to make a
    ///         property stop being counted. Two things do. A fragment that no assembler references
    ///         reaches no property, is never classified as composed at all, and falls through to
    ///         <c>Inert</c> like anything else. And a fragment that <i>is</i> assembled inherits the
    ///         obligation of the property it assembles into, which this checks: that property is
    ///         judged by the same frame-running differential as everything else, and is either acted
    ///         on or carries its own line.
    ///     </para>
    ///     <para>
    ///         <b>The debt moves rather than disappearing, and lands where it is actually owed.</b>
    ///         Seven gradient fragments and one <c>background-image</c> is one line naming <c>#43</c>,
    ///         not eight — and it is the right one, because what the engine owes is a draw list that
    ///         paints a gradient, not seven custom properties.
    ///     </para>
    ///     <para>
    ///         An allow-list line naming a fragment is refused for the same reason. It would be a debt
    ///         recorded against a thing that cannot be paid: no consumer will ever read
    ///         <c>--tw-gradient-from</c>, so the entry could never expire, which is precisely the rot
    ///         <c>InertProperties.txt</c> was designed not to have.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_composed_fragment_escapes_the_obligation_of_what_it_assembles_into() {
        var allowed = InertAllowList.Load();
        var measured = UtilityConsumptionProbe.Take();
        var problems = new List<string>();

        // Every registered fragment is emitted by some family and reaches some property. One that is
        // neither is a table entry nothing uses, which is the other direction of rot.
        foreach (var fragment in UtilityComposition.Fragments) {
            if (!measured.Emitted.Contains(fragment)) {
                problems.Add($"  {fragment,-32} is registered as a fragment and no family emits it.");
            } else if (!measured.Composed.ContainsKey(fragment)) {
                problems.Add($"  {fragment,-32} is emitted and nothing assembles it — no var() reaches it.");
            }
        }

        // And what it assembles into is on the hook, exactly as if the fragment were not there.
        foreach (var (fragment, into) in measured.Composed) {
            foreach (var property in into.Where(property =>
                         !measured.Consumers.ContainsKey(property) && !allowed.ContainsKey(property))) {
                problems.Add(
                    $"  {fragment,-32} composes into {property}, which nothing acts on and which has no line."
                );
            }
        }

        foreach (var property in allowed.Keys.Where(UtilityComposition.IsFragment)) {
            problems.Add(
                $"  {property,-32} is a fragment and must not be allow-listed — put the line on what it composes into."
            );
        }

        Assert.True(
            problems.Count == 0,
            $"""
             {problems.Count} problem(s) with the composed families. A `--tw-*` fragment is exempt from
             the differential because it is half a declaration, and the price of that exemption is that
             the other half is accounted for:

             {string.Join("\n", problems.OrderBy(problem => problem, StringComparer.Ordinal))}
             """
        );
    }

    /// <summary>Every allow-list entry names a task, and the task is one the plan document knows.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The task number is what makes this an allow-list rather than a shrug</b>, and
    ///         checking it against <c>docs/plan/43</c> is what stops the number being invented at the
    ///         keyboard. <c>#99</c> silences the gate exactly as well as <c>#27</c> does if nobody ever
    ///         looks; requiring the number to appear in the document means the cheapest way to add a
    ///         line is to first write down, in the plan, what would close it — which is the thing that
    ///         was missing when eighteen properties accumulated with no record at all.
    ///     </para>
    ///     <para>
    ///         The document is read as text, which is the one place in this file that happens. A NUL
    ///         byte or a missing file makes the read fail or the match fail, and both fail the build —
    ///         the safe direction, unlike a <c>grep</c> for callers, where the same accident reads as
    ///         "nothing calls this".
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_allow_list_entry_names_a_task_the_plan_document_knows() {
        var plan = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "43-web-styling-parity.md"));

        Assert.Contains("web-styling-parity", plan, StringComparison.Ordinal);

        var problems = new List<string>();

        foreach (var (property, entry) in InertAllowList.Load()) {
            if (entry.Task is not { } task) {
                problems.Add(
                    $"  {property,-32} has no #task. Every line must name the task that will close it."
                );

                continue;
            }

            if (!plan.Contains($"#{task}", StringComparison.Ordinal)) {
                problems.Add(
                    $"  {property,-32} names #{task}, which docs/plan/43 has never heard of."
                );
            }

            if (entry.Reason.Length == 0) {
                problems.Add($"  {property,-32} names #{task} and says nothing about why.");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"""
             InertProperties.txt is malformed. Each line is `property  #task  reason`:

             {string.Join("\n", problems)}
             """
        );
    }

    /// <summary>The method distinguishes interned from acted on, proved on the two known cases.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The control experiment, and without it the gate is unfalsifiable.</b> The row is
    ///         a standard CSS property the cascade carries — it resolves, it is in the document's
    ///         table — that no utility family emits and nothing in the engine reads. The gate never
    ///         meets it in normal operation; it is here precisely because it is the case a cheaper
    ///         method would get wrong.
    ///     </para>
    ///     <para>
    ///         The first assertion is what makes the second mean something: the property really is in
    ///         the document's table, so "no channel moved" cannot be explained away as the cascade
    ///         having dropped the declaration. If it ever starts being read, this test fails and the
    ///         remark above needs rewriting rather than the gate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two properties have now been removed from this list by being implemented, and
    ///         both times that was this test doing its job rather than a hole in it.</b>
    ///         <c>text-indent</c> went the day <c>LineWrapper</c> learned a first-line width, and
    ///         <c>word-spacing</c> the day <c>TextRun</c> learned which characters CSS separates words
    ///         with. Each had sat here for exactly as long as <c>docs/plan/43</c> § Part 0's finding
    ///         about it was true, and each started failing on the commit that made it false. That is
    ///         what a control experiment is supposed to do when the thing it controls for stops being
    ///         the case.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the replacement has to be found rather than the row simply deleted.</b> A
    ///         <c>[Theory]</c> whose last <c>[InlineData]</c> is removed does not fail; it reports no
    ///         tests, and the gate above it goes on passing with nothing left to say its method can
    ///         tell "unread" from "not measured". <c>tab-size</c> is the third occupant on those
    ///         terms: CSS Text 3 § 6.1, parsed and cascaded here, read by nothing, emitted by no
    ///         family.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("tab-size", "8")]
    public void An_interned_property_no_consumer_acts_on_reads_as_inert(string property, string value) {
        Assert.Contains(property, UtilityConsumptionProbe.Resolved($"{property}: {value}"), StringComparer.Ordinal);
        Assert.Empty(UtilityConsumptionProbe.Channels(property, value));
    }

    /// <summary>The method sees a property that <i>is</i> acted on, proved on one of each channel.</summary>
    /// <remarks>
    ///     The other half of the control. A differential that reported "nothing changed" for everything
    ///     — a broken frame loop, a scene that never laid out — would pass every assertion above and
    ///     fail the build for the whole family table, so the four observables each need a case that
    ///     moves them and nothing else.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The three <c>translate</c> rows are a second case on channels that already have one,
    ///     and they are here because this gate's verdict is a <i>union</i>.</b> A property counts as
    ///     read if any one observable moves, which is the right rule for "is this a promise or a
    ///     feature" and is blind to the failure a transform actually has: painted in the new place and
    ///     clickable in the old one moves <c>paint</c>, passes, and is a broken interface. Naming the
    ///     channels a translation must move pins all three here, so a regression that quietly drops
    ///     the hit test fails the gate rather than only the two files that assert it directly —
    ///     <c>Vixen.Ui.Tests.TransformTests</c> and the editor's family table.
    /// </remarks>
    [Theory]
    [InlineData("width", "9px", "layout")]
    [InlineData("background-color", "#123456", "paint")]
    [InlineData("cursor", "pointer", "cursor")]
    [InlineData("pointer-events", "none", "hit")]
    [InlineData("translate", "9px 0px", "hit")]
    [InlineData("translate", "9px 0px", "paint")]
    [InlineData("translate", "9px 0px", "layout")]

    // ⚠ <b>`rotate` and `scale` are pinned on both channels for the translation's reason, and the
    // `hit` row is the one that has to be here.</b> Every transform opens a composited group, so
    // every transform moves `paint` — a gate row asking only for that would pass a matrix no executor
    // reads, a `rotate` that came out a `scale`, and one that reached the draw list and never reached
    // the pointer. The last of those is the interface you can see and cannot click, and unlike
    // `translate` it is not unstateable here: a translation lands in `AbsoluteLeft` and both consumers
    // read the result, where these two are one matrix applied in two places.
    //
    // ⚠ No `layout` row, and that is an assertion rather than an omission. CSS Transforms 1 §3
    // forbids a transform from affecting layout, so a `layout` channel moving for either of these
    // would be the bug — see `TransformTests.A_transformed_parent_carries_its_subtree_and_leaves_its_siblings`,
    // which is where that is asserted positively. The translation's `layout` row is honest because a
    // translation lands in the accumulated position, which this gate counts as layout.
    [InlineData("rotate", "30deg", "hit")]
    [InlineData("rotate", "30deg", "paint")]
    [InlineData("scale", "150%", "hit")]
    [InlineData("scale", "150%", "paint")]
    public void A_property_the_engine_acts_on_moves_the_channel_it_should(string property, string value, string channel) =>
        Assert.Contains(channel, UtilityConsumptionProbe.Channels(property, value), StringComparer.Ordinal);

    /// <summary>The two transition scenes can still see a running animation.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A control over the <i>scenes</i>, not over the engine, and the file needs one
    ///         because the scenes have been the blind spot three times.</b> <c>grid-template-columns</c>
    ///         measured inert because no scene had a grid container, <c>vertical-align</c> because none
    ///         had a line box, and <c>transition-property</c> because the one scene with transitions
    ///         switched on already declared the family's only emitted value. Each time the gate was
    ///         green while the property it was reporting on had a reader.
    ///     </para>
    ///     <para>
    ///         The rows above will keep this honest for as long as those three properties stay on the
    ///         family surface — but the surface is not fixed, and a family removed or renamed would
    ///         take the gate's only exposure to the <c>animated</c> and <c>primed</c> scenes with it,
    ///         leaving two arrangements nothing ever exercises. This asks them directly, with values
    ///         chosen to differ from what each scene declares: <c>700ms</c> against the scene's
    ///         <c>200ms</c>, <c>ease-in-out</c> against its <c>linear</c>, and <c>all</c> against
    ///         <c>primed</c>'s <c>color</c>.
    ///     </para>
    ///     <para>
    ///         <c>paint</c> rather than <c>layout</c>, and that is a real property of the animator
    ///         rather than a weak assertion: a transition needs the value it is coming *from*, which
    ///         it reads out of the previous computed style — and a cascade with no computed-value stage
    ///         has nothing to offer for a property the element did not previously declare. The probe's
    ///         mutation adds a <c>margin-left</c> that was not there before, so only its
    ///         <c>background-color</c> change has both ends and only the paint channel moves.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("transition-property", "all")]
    [InlineData("transition-duration", "700ms")]
    [InlineData("transition-timing-function", "ease-in-out")]
    public void The_transition_scenes_can_observe_a_running_animation(string property, string value) =>
        Assert.Contains("paint", UtilityConsumptionProbe.Channels(property, value), StringComparer.Ordinal);

    /// <summary>The <c>discrete</c> scene can see a transition that has no midpoint to run through.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sixth of these controls, and it is the same finding as the three above it: the
    ///         scenes were the blind spot, not the engine.</b> <c>Animator</c> has interned
    ///         <c>transition-behavior</c>, read it off both the shorthand and the longhand and gated
    ///         <c>Observe</c>'s <c>Start</c> on it since #861 — and it measured inert anyway, because
    ///         every scene's mutation moves <c>background-color</c> and <c>margin-left</c>, both of
    ///         which interpolate. A property that decides what happens to a pair with no midpoint
    ///         needs a pair with no midpoint in front of it.
    ///     </para>
    ///     <para>
    ///         <c>allow-discrete</c> and not <c>normal</c>, because <c>normal</c> is CSS's initial
    ///         value: injecting it reproduces the baseline exactly and would assert that writing the
    ///         default changes nothing, which is true of every default and evidence of nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_discrete_scene_can_observe_a_transition_with_no_midpoint() =>
        Assert.Contains(
            "paint",
            UtilityConsumptionProbe.Channels("transition-behavior", "allow-discrete"),
            StringComparer.Ordinal
        );

    /// <summary>The <c>clipped</c> scene can see an ellipsis, which is the fifth of these controls.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written before the <c>truncate</c> family was widened to emit the property, and
    ///         that order is the point.</b> F5's whole finding is that the class name promises an
    ///         ellipsis the engine cannot draw; emitting <c>text-overflow</c> first would have moved
    ///         the promise from the class to the gate without moving it any closer to the screen. So
    ///         the scene and the reader landed first, this test proved the arrangement observes them,
    ///         and only then did the family change.
    ///     </para>
    ///     <para>
    ///         <c>paint</c> and not <c>layout</c>, and the distinction is exactly the design: the line
    ///         is truncated when it is <i>drawn</i>, not when it is measured, because
    ///         <c>white-space: nowrap</c> hands the wrap pass an infinite width and the box only
    ///         learns its real one after its parent has shrunk it. A <c>layout</c> assertion here
    ///         would be asserting that the ellipsis changed the element's size, which would mean
    ///         truncation had leaked into measurement and a truncated box had stopped reporting the
    ///         width that made it get truncated.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_clipped_scene_can_observe_an_ellipsis() =>
        Assert.Contains("paint", UtilityConsumptionProbe.Channels("text-overflow", "ellipsis"), StringComparer.Ordinal);

    /// <summary>The scrollport scene can observe a scrollbar gutter.</summary>
    /// <remarks>
    ///     ⚠ <b>Named after the scene rather than the property, like the ellipsis above it, because
    ///     the thing that can break is the scene.</b> <c>scrollbar-width</c> is read on exactly one
    ///     condition — <see cref="Overflow.Scroll" /> on the axis — and until <c>scrollport</c> was
    ///     added no scene put that on <c>#probe</c>. The <c>scrolled</c> scene looks as though it
    ///     would: its probe is a <c>ScrollView</c>. But <c>ControlTheme.vcss</c> gives a
    ///     <c>scroll-view</c> <c>overflow: hidden</c> and lays an absolutely positioned bar over the
    ///     top, so the store sees a box that clips, and a gutter with 180 corpus fixtures behind it
    ///     measured inert.
    /// </remarks>
    [Fact]
    public void The_scrollport_scene_can_observe_a_scrollbar_gutter() =>
        Assert.Contains("layout", UtilityConsumptionProbe.Channels("scrollbar-width", "10px"), StringComparer.Ordinal);

    /// <summary>What the run measured, printed whether it passed or not.</summary>
    /// <remarks>
    ///     ⚠ <b>The allow-list is only a deterrent if somebody sees it.</b> A silent pass is how a list
    ///     of eighteen becomes a list of thirty without anyone deciding to let it: nothing in the build
    ///     output ever says how much is owed. This writes the whole ledger — how many properties the
    ///     families emit, how many are read, by what, and every outstanding line with its task — on
    ///     every run.
    /// </remarks>
    [Fact]
    public void The_parity_ledger_is_printed_for_whoever_is_reading_the_log() {
        var measured = UtilityConsumptionProbe.Take();
        var allowed = InertAllowList.Load();
        var ledger = new StringBuilder();

        ledger.Append("utility parity: ")
            .Append(measured.Emitted.Count)
            .Append(" properties emitted, ")
            .Append(measured.Consumers.Count)
            .Append(" acted on, ")
            .Append(measured.Inert.Count)
            .Append(" inert, ")
            .Append(measured.Composed.Count)
            .AppendLine(" composed.");

        foreach (var (fragment, into) in measured.Composed.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            ledger.Append("  ")
                .Append(fragment.PadRight(32))
                .Append("composed into ")
                .AppendLine(string.Join(", ", into));
        }

        foreach (var (property, utilities) in measured.Inert.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            var entry = allowed.GetValueOrDefault(property);

            ledger.Append("  ")
                .Append(property.PadRight(32))
                .Append(entry is null ? "UNEXPLAINED" : $"#{entry.Task} {entry.Reason}")
                .Append("  ← ")
                .AppendLine(string.Join(", ", utilities.Take(4)));
        }

        Assert.NotEmpty(ledger.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(ledger.ToString());
    }
}
