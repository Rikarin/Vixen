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
///         properties.</b> <c>LayoutStyleBuilder</c> interns <c>word-spacing</c> and
///         <c>text-indent</c> and exposes ids for them that nothing reads; <c>InheritedProperties</c>
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
    ///         ⚠ <b>The control experiment, and without it the gate is unfalsifiable.</b>
    ///         <c>word-spacing</c> and <c>text-indent</c> are interned by <c>LayoutStyleBuilder</c> —
    ///         they have ids, the cascade carries them, they are in the name table — and
    ///         <c>docs/plan/43</c> § Part 0 records that nothing in the repository reads either. They
    ///         are also the two properties no utility family emits, so the gate never meets them in
    ///         normal operation; they are here precisely because they are the case a cheaper method
    ///         would get wrong.
    ///     </para>
    ///     <para>
    ///         The first assertion is what makes the second mean something: the property really is in
    ///         the document's table, so "no channel moved" cannot be explained away as the cascade
    ///         having dropped the declaration. If either of these ever starts being read, this test
    ///         fails and the remark above needs rewriting rather than the gate.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("word-spacing", "6px")]
    [InlineData("text-indent", "12px")]
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
    [Theory]
    [InlineData("width", "9px", "layout")]
    [InlineData("background-color", "#123456", "paint")]
    [InlineData("cursor", "pointer", "cursor")]
    [InlineData("pointer-events", "none", "hit")]
    public void A_property_the_engine_acts_on_moves_the_channel_it_should(string property, string value, string channel) =>
        Assert.Contains(channel, UtilityConsumptionProbe.Channels(property, value), StringComparer.Ordinal);

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
