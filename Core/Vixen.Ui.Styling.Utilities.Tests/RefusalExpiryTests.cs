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
///         <b>The lesson is about <i>what</i> an anchor names, not about the kind.</b> The clause read
///         reads <c>[expires-on Vixen.Ui.Styling.Utilities.UtilityComposition.RingOffsetWidth]</c> —
///         the <c>--tw-*</c> fragment that was genuinely missing rather than a plausible spelling of a
///         parser feature — and ⚠ <b>it FIRED, on 2026-09-06.</b> The member arrived under exactly
///         that name, the clause went red, and the refusal left the file in the commit that closed the
///         root, which is the only way a clause is meant to go. A tripwire on a thing whose
///         <i>name</i> is forced by an external
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
///         written. ⚠ A row carrying this clause may be <c>partial</c>, unlike <c>expires-with</c> —
///         see <see cref="RefusalExpiry.Gapped" />, where the difference is argued.
///     </para>
///     <para>
///         ⚠ <b><c>expires-on</c> may sit on a <c>partial</c> row too, and it took a rotted sentence
///         to find that out.</b> <c>hyphens</c>' <c>auto</c> keyword was refused on two things: no
///         Liang pattern set, and nothing carrying a language. #600 landed the language, and nothing
///         went red — the row is <c>partial</c> because <c>none</c> and <c>manual</c> shipped, so it
///         could carry no clause at all, and the stale half of the reason stood in three files.
///         ⚠ The lesson is the opposite of the intuition: a refusal buried in one keyword of a
///         half-landed root is <i>more</i> exposed than one on an <c>absent</c> row, because once the
///         other keywords ship the row's own state stops moving and nobody re-reads it.
///     </para>
///     <para>
///         ⚠ <b>The ledger is not where refusals live — it is only where they were checked.</b> #674
///         found four false ones in prose the same month: <c>Core/Vixen.Ui/README.md</c> said
///         <c>scale</c> and <c>rotate</c> were refused while <c>TransformReader</c> read both,
///         <c>NodeCanvas</c> said there is no <c>transform</c> property, the <c>touch-action</c> row
///         said no touch reaches <c>UiDocument</c> while <c>PlatformInput</c> routed them, and
///         <c>Vixen.Ui.Markup</c>'s README taught a <c>[Parameter]</c> attribute that has never
///         existed. So the sweep reads every <c>README.md</c> and every doc comment in the tree as
///         well — see <see cref="RefusalExpiry.DeclaredInProse" /> — and a prose refusal declares its
///         condition in exactly the same clause a note does.
///     </para>
///     <para>
///         ⚠ <b>Widening the sweep would not have caught those four and it is worth being exact about
///         why.</b> None of them declared a clause; a clause-reader catches only conditions somebody
///         wrote down. What the widening buys is that a prose refusal now <i>can</i> be written down —
///         <c>Core/Vixen.Ui</c>'s <c>box-shadow</c> paragraph is the first, resting its <c>inset</c>
///         half on <c>inset-shadow-*</c> — where before there was nowhere to put the condition but a
///         sentence, and a sentence is what the four were. Detecting an <i>undeclared</i> refusal is
///         the mechanism <see cref="RefusalExpiry" />'s own remarks measured at 106 false positives
///         across 42 rows and refused to build.
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
///         <see cref="The_census_is_exactly_the_clauses_the_ledger_and_the_prose_declare" /> is red.
///         It is an exact set comparison in both directions rather than a floor, because a floor is
///         the guard that has twice been eaten by success in this suite's neighbours: "more than one
///         distinct reason" and
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
    public void The_census_is_exactly_the_clauses_the_ledger_and_the_prose_declare() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var declared = RefusalExpiry.All(rows, RefusalExpiry.Root());
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

    /// <summary>A row that emits and is read by nothing declares the condition that ends it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The gate the census cannot be, and it exists because a clause was lost exactly
    ///         the way the census is blind to.</b> <c>5103da9b3</c> gave <c>select</c> a second
    ///         clause — <c>expires-on Vixen.Ui.UiDocument.Selection</c> — with the argument that its
    ///         first one, <c>expires-when-read user-select</c>, comes due on the cheap close the
    ///         note spends a paragraph declining, so the row's only tripwire would have certified
    ///         the wrong fix. Seventy-seven minutes later a conflict resolution in <c>295ffa867</c>
    ///         took the side of the <c>note</c> cell without it, and seventeen minutes after that
    ///         <c>b68f80aba</c> regenerated the census, which dropped its own copy to match.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So both halves of the guard went, in the one direction the equality above
    ///         cannot see.</b> That equality does catch a clause deleted on its own — sabotaging
    ///         this row reddens it verbatim. What it cannot catch is a deletion followed by a
    ///         regeneration, because the census is <i>derived</i> from the ledger: the run that
    ///         writes it back is the run that makes the two files agree about having lost the
    ///         clause, and an equality between a thing and its own shadow says nothing about
    ///         either. Here the two runs were seventeen minutes apart and the second had every
    ///         reason to regenerate, since a different row's refusal had genuinely expired in the
    ///         same merge. What can be checked instead is
    ///         a property of the row itself — a root that resolves, computes a value and is read by
    ///         nothing is a recorded debt, and the one thing that stops such a row becoming
    ///         permanent is a stated condition — so an <c>inert</c> row with no clause at all fails
    ///         here whether it never had one or quietly lost it.
    ///     </para>
    ///     <para>
    ///         <c>absent</c> is deliberately not on this rule: fifty-odd roots are simply not
    ///         emitted yet and most are work nobody has started, where a clause would be a
    ///         prediction rather than a refusal. <c>inert</c> is the state that means somebody
    ///         decided, wrote the family, and left the engine not reading it.
    ///     </para>
    ///     <para>
    ///         ⚠ And the set is asserted non-empty first, for this file's standing reason: a rule
    ///         applied to no rows passes vacuously, and today the set is exactly one row.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_inert_row_declares_a_condition() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var clauses = RefusalExpiry.All(rows, RefusalExpiry.Root());

        var inert = rows
            .Where(row => string.Equals(row.State, "inert", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            inert.Count > 0,
            "no ledger row reads `inert`, so this rule has no subjects. That is either a state the "
            + "table has left behind — in which case delete this and say so — or the ledger is not "
            + "being read, which is what every anti-vacuity assertion in this suite is about."
        );

        var bare = inert
            .Where(row => !clauses.Any(clause => !clause.Prose && string.Equals(clause.Root, row.Root, StringComparison.Ordinal)))
            .Select(row => row.Root)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            bare.Count == 0,
            $"""
             {bare.Count} `inert` row(s) declare no expiry clause at all:

               {string.Join("\n  ", bare)}

             An inert root emits a property and nothing reads it — a debt this repository has written
             down. Say in the row's `note` what ends it: `[expires-when-read <property>]` when a
             reader is what is missing, `[expires-on <Type>.<Member>]` when the blocker is a symbol
             that does not exist yet. ⚠ A row here may be one that HAD a clause: see this test's
             remarks for the merge that took one, which is why the rule is on the row and not on the
             census.
             """
        );
    }

    /// <summary>Every clause that opened parsed, so a mistyped one is a failure and not an exemption.</summary>
    /// <remarks>
    ///     ⚠ <b>Quotations are on the right-hand side, and leaving them off would have exempted the
    ///     half of the grammar that has no other guard at all.</b> A quotation resolves no anchor and
    ///     expires on no condition — there is nothing else in this file that ever looks at one — so
    ///     being counted here is the whole of what holds it to a shape. <c>[~expires-</c> does not
    ///     contain <c>[expires-</c>, so this is not automatic: <see cref="RefusalExpiry.Opened" />'s
    ///     pattern had to be widened to see the form at all.
    /// </remarks>
    [Fact]
    public void A_clause_that_does_not_parse_is_a_failure_rather_than_a_row_the_sweep_skips() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var root = RefusalExpiry.Root();

        Assert.Equal(
            RefusalExpiry.Opened(rows) + RefusalExpiry.OpenedInProse(root),
            RefusalExpiry.All(rows, root).Count + RefusalExpiry.AllQuoted(rows, root).Count
        );
    }

    /// <summary>A note that talks about a clause does not thereby declare a second one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect this pair is written against, which was found in the tree rather than
    ///         imagined</b> (#1325). The <c>select</c> row's note narrates the commit that gave it its
    ///         <c>expires-on</c> clause and the merge that took it away seventy-seven minutes later —
    ///         the ordinary way this repository records why a refusal is worded as it is, and the thing
    ///         the census header asks for when it says clauses live "next to the prose they formalise".
    ///         Written in the only spelling the grammar had, that sentence <i>declared a second real
    ///         clause</i>, off the same cell as the live one, and the census recorded the same line
    ///         twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cell below is the real one, not a reduction of it</b>, so the assertion is
    ///         about the shape the tree actually contains: a live pair of clauses at the end of the
    ///         note and a quotation of one of them in the middle of the prose.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_quotation_is_counted_as_clause_shaped_and_declares_nothing() {
        var rows = new[] {
            Row(
                "select",
                "`5103da9b3` added the `[~expires-on Vixen.Ui.UiDocument.Selection]` clause now at the "
                + "end of this cell, and `295ffa867` took the side of the note without it. "
                + "[expires-when-read user-select] [expires-on Vixen.Ui.UiDocument.Selection]"
            )
        };

        Assert.Equal(
            ["select\texpires-on\tVixen.Ui.UiDocument.Selection", "select\texpires-when-read\tuser-select"],
            RefusalExpiry.Declared(rows).Select(static clause => clause.Line)
        );

        Assert.Equal(
            ["select\texpires-on\tVixen.Ui.UiDocument.Selection"],
            RefusalExpiry.Quoted(rows).Select(static clause => clause.Line)
        );

        // ⚠ Three, not two. The quotation is clause-shaped and is counted with the other two, which is
        // what makes a mistyped one red rather than prose — see the balance test above.
        Assert.Equal(3, RefusalExpiry.Opened(rows));
    }

    /// <summary>A mistyped quotation is a failure and not a line the sweep reads as prose.</summary>
    /// <remarks>
    ///     The instrument's own check, one level over from the clause it was written for. A quotation
    ///     is held to nothing else, so a sweep that could not see this one would read
    ///     <c>[~expires-witth user-select]</c> as an English sentence — and a quotation that has
    ///     silently stopped being one is a tilde away from being a live declaration again.
    /// </remarks>
    [Fact]
    public void A_quotation_that_does_not_parse_unbalances_the_count_rather_than_vanishing() {
        var rows = new[] { Row("select", "narrating `[~expires-witth user-select]`, which is misspelt") };

        Assert.Equal(1, RefusalExpiry.Opened(rows));
        Assert.Empty(RefusalExpiry.Declared(rows));
        Assert.Empty(RefusalExpiry.Quoted(rows));
    }

    /// <summary>No root declares the same condition twice.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The question no other test in this file can ask, and the reason the duplicate
    ///         #1325 found sat green.</b>
    ///         <see cref="The_census_is_exactly_the_clauses_the_ledger_and_the_prose_declare" /> is a
    ///         list equality against a list the same sweep produced, so a repeated line is consistent
    ///         with itself; <see cref="No_refusal_outlives_the_condition_it_names" /> evaluates the
    ///         repeat twice and agrees with itself both times; and
    ///         <see cref="A_clause_that_does_not_parse_is_a_failure_rather_than_a_row_the_sweep_skips" />
    ///         balances, because two openings parse to two clauses. Every guard here is of the shape
    ///         "derive a set and hold it against a committed copy", and no guard of that shape can see
    ///         a set holding one member twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why a duplicate is worth failing on rather than de-duplicating.</b> It is not a
    ///         tidiness rule: the two rows are one condition, so the census overstates how many
    ///         refusals this repository has written down and understates how much rests on the one
    ///         anchor — which is the exact reading <c>RefusalExpiry.txt</c>'s header tells the next
    ///         person to take from it. Collapsing them silently would make the count right and leave
    ///         the note that produced it unread; failing sends the reader to the cell, where either a
    ///         quotation was meant or one of the two clauses is redundant.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_root_declares_the_same_condition_twice() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var duplicates = RefusalExpiry.Duplicates(RefusalExpiry.All(rows, RefusalExpiry.Root()));

        Assert.True(
            duplicates.Count == 0,
            $"""
             {duplicates.Count} expiry condition(s) are declared twice by the same root:

               {string.Join("\n  ", duplicates)}

             One condition declared twice is one condition, and the census counts it as two. If the
             second is a note TALKING ABOUT the clause rather than declaring it — which is what the
             `note` column is for — write it `[~expires-… …]`, the quotation form: it is read, held to
             being well formed, and declares nothing. Otherwise one of the two wants deleting.
             """
        );
    }

    /// <summary>The duplicate rule sees the shape #1325 found, in the cell it found it in.</summary>
    /// <remarks>
    ///     The rule above is a whole-tree sweep and today it has nothing to report, which is the state
    ///     an anti-vacuity assertion cannot be written for — there is no non-empty set to insist on.
    ///     So the predicate is exercised directly, on the cell that produced the pair.
    /// </remarks>
    [Fact]
    public void The_duplicate_rule_reports_the_pair_a_quoted_clause_used_to_produce() {
        var rows = new[] {
            Row(
                "select",
                "`5103da9b3` added [expires-on Vixen.Ui.UiDocument.Selection] because … "
                + "[expires-when-read user-select] [expires-on Vixen.Ui.UiDocument.Selection]"
            )
        };

        Assert.Equal(
            ["select\texpires-on\tVixen.Ui.UiDocument.Selection"],
            RefusalExpiry.Duplicates(RefusalExpiry.Declared(rows))
        );
    }

    /// <summary>A ledger row carrying nothing but the note under test.</summary>
    /// <param name="root">The root name.</param>
    /// <param name="note">The <c>note</c> cell.</param>
    /// <returns>The row.</returns>
    static ParityRow Row(string root, string note) {
        var cells = new string[ParityLedger.Columns];

        Array.Fill(cells, string.Empty);
        cells[1] = root;
        cells[12] = note;

        return new ParityRow { Cells = cells };
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

        foreach (var clause in RefusalExpiry.All(rows, RefusalExpiry.Root())) {
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
    ///     <para>
    ///         ⚠ <b>Both directions.</b> A clause fails when what it waits on arrives, and it also
    ///         fails when the row carrying it stops being refused — because at that point the note
    ///         says "refused with X" about a root that is not refused, which is prose describing a
    ///         state the tree left. Leaving that half out is how <c>origin-*</c>'s page stayed
    ///         authoritative for a year.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every clause is evaluated and they are reported together, which is not a tidying
    ///         of the message.</b> This asserted per clause until 2026-09-10, so the first expiry
    ///         threw and the rest of the sweep never ran — and <c>RefusalExpiry.txt</c>'s own header
    ///         says the pattern to look for is <i>rows sharing an anchor</i>, because "when that
    ///         premise closes, they all rot at once and the suite reports only the first". The
    ///         instrument had exactly the defect the census it reads on warns about. Three rows name
    ///         <c>UiVertex.W</c> directly (until #548 landed it and they moved to
    ///         <c>expires-on Vixen.Ui.TransformReader.perspective</c>) and four more reach it through <c>expires-with</c>, so the
    ///         day #548 lands this used to print one root's name; a reader would size a seven-row
    ///         decision as a one-row one, fix the row they were shown, and be told about the next
    ///         one on the next run. It was measured on that exact anchor rather than predicted
    ///         (#548's fifth pass), and it prints all of them now.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_refusal_outlives_the_condition_it_names() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var state = rows.ToDictionary(row => row.Root, row => row.State, StringComparer.Ordinal);
        var expired = new List<string>();

        // ⚠ The sweep is the WIDE one — the ledger plus every README and doc comment — because a
        // refusal in prose rots exactly as a note does, and four false ones were found in prose in
        // the same month the ledger held none.
        foreach (var clause in RefusalExpiry.All(rows, RefusalExpiry.Root())) {
            // ⚠ Only `expires-with` demands that the CARRIER still be refused outright, and the
            // asymmetry is argued on `RefusalExpiry.Gapped`. `expires-with` is prose about another
            // root's state, which a half-landed root has stopped making claims about. The other two
            // name a condition of this root's own — a property nothing reads, a symbol that does not
            // exist — and a `partial` root is where those live, because a refusal inside one keyword
            // of a root whose other keywords shipped is the one whose state column stops moving.
            var standing = clause.Kind == ExpiryKind.With ? RefusalExpiry.Refusing : RefusalExpiry.Gapped;

            // A prose refusal has no row, so there is no state to hold it to — only the condition
            // below. That is the whole of the difference between the two sources.
            if (!clause.Prose && !standing.Contains(state[clause.Root])) {
                expired.Add(
                    $"{clause.Root} is '{state[clause.Root]}' and still declares an expiry clause. It is "
                    + "not refused any more, so the clause and the sentence it formalises both want "
                    + "deleting."
                );

                continue;
            }

            if (clause.Kind == ExpiryKind.WhenRead) {
                if (UtilityConsumptionProbe.Take().Consumers.TryGetValue(clause.Anchor, out var consumers)) {
                    expired.Add(
                        $"{clause.Root}'s gap rests on nothing reading '{clause.Anchor}', and "
                        + $"{string.Join(", ", consumers)} reads it now. Re-read {clause.Root}'s note "
                        + "and re-measure the row: the allow-list line it cites has expired, and this is "
                        + "the sentence one dependency edge out from it."
                    );
                }

                continue;
            }

            if (clause.Kind == ExpiryKind.With) {
                if (!RefusalExpiry.Refusing.Contains(state[clause.Anchor])) {
                    expired.Add(
                        $"{clause.Root} is refused on the strength of {clause.Anchor} being refused, and "
                        + $"{clause.Anchor} is '{state[clause.Anchor]}' now. Re-read {clause.Root}'s note: "
                        + "the premise it rests on has closed."
                    );
                }

                continue;
            }

            var (type, member) = RefusalExpiry.Resolve(clause.Anchor);

            if (type is not null && RefusalExpiry.Has(type, member)) {
                expired.Add(
                    $"{clause.Root} is refused because '{clause.Anchor}' does not exist, and it does now. "
                    + "Re-read the note: the thing it was waiting for has arrived."
                );
            }
        }

        Assert.True(
            expired.Count == 0,
            $"""
             {expired.Count} refusal clause(s) have outlived the condition they name:

             {string.Join("\n\n", expired.Select(static line => "  " + line))}

             ⚠ Read the WHOLE list before deciding anything. Rows here share an anchor on purpose,
             so one arriving member can come due for several roots at once, and each of them is a
             separate decision about a separate refusal — fixing only the first name printed is how
             a shared premise closes while the notes resting on it stay as they were.
             """
        );
    }
}
