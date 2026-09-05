// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>A refusal is a verdict plus a condition, and this is the half nothing checked.</summary>
/// <remarks>
///     <para>
///         <b>The finding, which is the most-repeated one in doc 43 and had no mechanism behind it.</b>
///         Six times in one month a refusal that was correct when it was written was false when it was
///         read, and in every case the words that expired were right there in the note. Transforms were
///         refused because "the renderer would have to composite a transformed subtree — the same
///         compositor <c>DrawListBuilder</c>'s opacity remark already owes", and the compositor had
///         landed a week earlier; <c>origin-*</c> was refused as unobservable "until <c>scale</c> and
///         <c>rotate</c> land", and they landed; <c>scale-x/y-*</c> cited <c>scale</c>'s refusal and
///         inherited its expiry; <c>on:click</c>'s note described a mapping <c>ControlMarkup</c> had
///         replaced three weeks before. Every one of those is a condition filed as a verdict.
///     </para>
///     <para>
///         ⚠ <b>The allow-list next door already solves this and cannot be copied, and the reason is
///         worth stating exactly.</b> <c>InertProperties.txt</c> expires on its condition:
///         <see cref="UtilityConsumptionGateTests.No_allow_list_entry_outlives_the_gap_it_names" />
///         measures whether anything reads the property and fails the exemption the moment something
///         does. That works because its condition is <i>measurable</i> — a property either moves one of
///         four channels or it does not. Most refusals are not like that. "There is no
///         <c>&lt;transform-function&gt;</c> parser" and "a <c>DrawCommand</c> has no blend channel"
///         cannot be measured by running a frame, because the thing they describe is a thing that does
///         not exist. The file says so itself, and this suite is the answer to that paragraph.
///     </para>
///     <para>
///         <b>Two clause kinds, and they are not equally strong. Prefer the first.</b>
///     </para>
///     <para>
///         <b><c>[expires-with &lt;root&gt;]</c> — exact.</b> The refusal stands only while the named
///         ledger root is itself refused. The cited root's <c>state</c> is a <i>computed</i> column, so
///         nobody has to predict anything and nobody can spell around it: whatever closes the cited
///         root changes its state, and the run that changes it is the run this fails on. This is
///         issue #288's literal subject — a refusal that cites another refusal inheriting its expiry
///         date — and for that shape the mechanism is airtight.
///     </para>
///     <para>
///         ⚠ <b><c>[expires-on &lt;Namespace.Type&gt;.&lt;Member&gt;]</c> — a tripwire, and it can be
///         walked around.</b> The refusal stands only while that member does not exist. The weakness is
///         plain and is written here rather than discovered later: whoever eventually builds the thing
///         picks the name, and if they pick a different one the clause stays green for ever. It is
///         here because some refusals have no ledger root to hang on — <c>mix-blend</c>'s surviving
///         half is "no blend channel on a <c>DrawCommand</c>", which is a fact about a struct — and a
///         tripwire on the most likely name is worth more than a sentence in a paragraph nobody
///         re-reads. It is not worth more than an <c>expires-with</c>, so where both are available the
///         root is the one to name.
///     </para>
///     <para>
///         ⚠ <b>That weakness stopped being hypothetical one day after it was written, and the worked
///         case is worth more than the warning.</b> <c>ring-offset-*</c> was refused partly because
///         <c>StyleValueParser</c> read no <c>calc()</c>, and its clause was
///         <c>[expires-on Vixen.Ui.Styling.StyleValueKind.Calculation]</c> — the most likely name for
///         the thing somebody would build. What was built instead <i>folds</i> the expression to an
///         ordinary length, on the argument that a <see cref="StyleValue" /> is one number and one
///         unit and a kind carrying a tree would allocate on every declaration in the cascade. The
///         premise expired, the symbol never arrived, and this suite stayed green — exactly as
///         predicted, by the paragraph above, in the same week. It was a person re-reading the note
///         who caught it, which is the thing the mechanism exists to stop being necessary.
///     </para>
///     <para>
///         <b>The lesson is about <i>what</i> an anchor names, not about the kind.</b> The clause now
///         reads <c>[expires-on Vixen.Ui.Styling.Utilities.UtilityComposition.RingOffsetWidth]</c> —
///         the <c>--tw-*</c> fragment that is genuinely missing rather than a plausible spelling of a
///         parser feature. A tripwire on a thing whose <i>name</i> is forced by an external
///         specification is far harder to walk around than one on a thing whose implementation is a
///         design decision, because the first has one spelling and the second has as many as there
///         are designs. ⚠ A fourth kind — <c>expires-when-parsed</c>, measured by parsing a value —
///         would have caught this exactly, and is deliberately not built: after the fold landed, no
///         row in the ledger would carry it, and a clause kind with no users is this repository's
///         commonest defect wearing a mechanism's clothes.
///     </para>
///     <para>
///         <b><c>[expires-when-read &lt;css-property&gt;]</c> — exact, and it is the other file #288
///         names.</b> The refusal stands only while nothing in the engine reads that property. Its
///         condition is the <i>same measurement</i> <c>InertProperties.txt</c> expires on — the probe
///         runs the frame and reports which properties moved a channel — so a ledger note resting on an
///         allow-list line is now one dependency edge out from that line's own expiry rather than
///         prose beside it. <c>border-s-*</c> is the worked case: its width is read, its logical colour
///         is not, and its note has cited <c>InertProperties.txt #21</c> in words since the row was
///         written. ⚠ A row carrying this clause may be <c>partial</c>, unlike the two kinds above —
///         see <see cref="RefusalExpiry.Gapped" />, where the difference is argued.
///     </para>
///     <para>
///         ⚠ <b>And the typo, which is how a check like this normally dies.</b> An anchor is a string
///         in a document, and the failure mode of "assert this symbol is absent" is that a misspelt
///         symbol is absent too — green for ever, for the wrong reason. This repository has shipped
///         that bug three times in other files this year, in a navmesh, in a security policy and in a
///         shader key. So an <c>expires-on</c> anchor is checked in two halves by
///         <see cref="Every_clause_is_anchored_on_something_that_is_really_there" />: the <i>type</i>
///         must resolve and the <i>member</i> must not exist. A typo in the type name is a red test.
///         A typo in the member name is the one this cannot catch, and naming a type that has to
///         resolve is what shrinks the target from any string at all to one identifier.
///     </para>
///     <para>
///         <b>What this prints on the day it does not run.</b> The question every instrument here owes
///         an answer to. If the notes lose their clauses — the column is rewritten, the ledger is
///         regenerated by something that drops it, the convention is forgotten — the derived set is
///         empty, the committed census is not, and
///         <see cref="The_census_is_exactly_the_clauses_the_ledger_declares" /> is red. It is an exact
///         set comparison in both directions rather than a floor, because a floor is the guard that has
///         twice been eaten by success in this suite's neighbours: "more than one distinct reason" and
///         then "the reason set is exactly <c>float</c>" both passed while measuring nothing. A count
///         that only has to be big enough is satisfied by the defect it exists to catch. This one is
///         satisfied by nothing except the census being right.
///     </para>
///     <para>
///         ⚠ <b>A malformed clause fails rather than being skipped</b>, which is the same point one
///         level down. A regex sweep reports a clause it could not parse as no clause at all, so the
///         opening bracket is counted separately with a pattern that cannot be fooled by the contents
///         and the two numbers must agree. Without that,
///         <c>[expires-witth border-spacing-*]</c> is a silent exemption.
///     </para>
/// </remarks>
public class RefusalExpiryTests {
    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating =>
        Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>The census names every clause the ledger declares, and no others.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that answers "what happens on the day nothing runs", so it is an
    ///     equality and not a floor.</b> Adding a clause fails until the census records it, which is
    ///     the review step; deleting one fails too, which is the half a floor cannot do and the half
    ///     that matters — a refusal quietly losing its condition is exactly the event this whole suite
    ///     exists to notice. The shape is borrowed from
    ///     <c>Vixen.Ui.Layout.Tests.Taffy.TaffyUnsupportedCensusTests</c>, which holds a derived list of
    ///     reasons against a committed one for the same reason.
    /// </remarks>
    [Fact]
    public void The_census_is_exactly_the_clauses_the_ledger_declares() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var declared = RefusalExpiry.Declared(rows);
        var path = RefusalExpiry.Locate();

        if (Regenerating) {
            RefusalExpiry.WriteCensus(path, declared);
        }

        var census = RefusalExpiry.ReadCensus(path);

        foreach (var line in census) {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        Assert.Equal(census, declared.Select(clause => clause.Line).ToList());
    }

    /// <summary>Every clause that opened parsed, so a mistyped one is a failure and not an exemption.</summary>
    [Fact]
    public void A_clause_that_does_not_parse_is_a_failure_rather_than_a_row_the_sweep_skips() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());

        Assert.Equal(RefusalExpiry.Opened(rows), RefusalExpiry.Declared(rows).Count);
    }

    /// <summary>An anchor names a root the ledger has, or a type an assembly has.</summary>
    /// <remarks>
    ///     The anti-typo half. An <c>expires-on</c> whose type does not resolve would assert the absence
    ///     of a member of nothing, which is true for ever.
    /// </remarks>
    [Fact]
    public void Every_clause_is_anchored_on_something_that_is_really_there() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var roots = rows.Select(row => row.Root).ToHashSet(StringComparer.Ordinal);

        foreach (var clause in RefusalExpiry.Declared(rows)) {
            if (clause.Kind == ExpiryKind.With) {
                Assert.True(
                    roots.Contains(clause.Anchor),
                    $"{clause.Root} expires with '{clause.Anchor}', which is not a root in the ledger. "
                    + "A citation nothing resolves is a condition that can never come due."
                );

                continue;
            }

            if (clause.Kind == ExpiryKind.WhenRead) {
                // The typo guard for this kind. A property no family emits cannot be read by anything
                // — the probe only measures what is emitted — so a misspelt one would be a condition
                // that can never come due, which is the exact failure the `expires-on` half is split
                // in two to avoid.
                Assert.True(
                    UtilityConsumptionProbe.Take().Emitted.Contains(clause.Anchor),
                    $"{clause.Root} expires when '{clause.Anchor}' is read, and no utility family emits "
                    + "that property. Either it is misspelt, or the family that emitted it is gone — in "
                    + "which case the refusal resting on it wants re-reading anyway."
                );

                continue;
            }

            var (type, member) = RefusalExpiry.Resolve(clause.Anchor);

            Assert.True(
                type is not null,
                $"{clause.Root} expires on '{clause.Anchor}', and no loaded assembly has a type called "
                + $"'{clause.Anchor[..clause.Anchor.LastIndexOf('.')]}'. Either it is misspelt — in "
                + "which case the clause has been green for the wrong reason — or the type was renamed, "
                + "in which case the refusal wants re-reading anyway."
            );

            Assert.NotEqual(string.Empty, member);
        }
    }

    /// <summary>No refusal outlives the condition it named.</summary>
    /// <remarks>
    ///     ⚠ <b>Both directions.</b> A clause fails when what it waits on arrives, and it also fails
    ///     when the row carrying it stops being refused — because at that point the note says "refused
    ///     with X" about a root that is not refused, which is prose describing a state the tree left.
    ///     Leaving that half out is how <c>origin-*</c>'s page stayed authoritative for a year.
    /// </remarks>
    [Fact]
    public void No_refusal_outlives_the_condition_it_names() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var state = rows.ToDictionary(row => row.Root, row => row.State, StringComparer.Ordinal);

        foreach (var clause in RefusalExpiry.Declared(rows)) {
            var standing = clause.Kind == ExpiryKind.WhenRead ? RefusalExpiry.Gapped : RefusalExpiry.Refusing;

            Assert.True(
                standing.Contains(state[clause.Root]),
                $"{clause.Root} is '{state[clause.Root]}' and still declares an expiry clause. It is not "
                + "refused any more, so the clause and the sentence it formalises both want deleting."
            );

            if (clause.Kind == ExpiryKind.WhenRead) {
                var read = UtilityConsumptionProbe.Take().Consumers.TryGetValue(clause.Anchor, out var consumers);

                Assert.False(
                    read,
                    $"{clause.Root}'s gap rests on nothing reading '{clause.Anchor}', and "
                    + $"{string.Join(", ", consumers ?? [])} reads it now. Re-read {clause.Root}'s note "
                    + "and re-measure the row: the allow-list line it cites has expired, and this is the "
                    + "sentence one dependency edge out from it."
                );

                continue;
            }

            if (clause.Kind == ExpiryKind.With) {
                Assert.True(
                    RefusalExpiry.Refusing.Contains(state[clause.Anchor]),
                    $"{clause.Root} is refused on the strength of {clause.Anchor} being refused, and "
                    + $"{clause.Anchor} is '{state[clause.Anchor]}' now. Re-read {clause.Root}'s note: "
                    + "the premise it rests on has closed."
                );

                continue;
            }

            var (type, member) = RefusalExpiry.Resolve(clause.Anchor);

            Assert.False(
                type is not null && RefusalExpiry.Has(type, member),
                $"{clause.Root} is refused because '{clause.Anchor}' does not exist, and it does now. "
                + "Re-read the note: the thing it was waiting for has arrived."
            );
        }
    }
}
