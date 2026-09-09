// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Which <c>display</c> keywords the bridge accepts, and what it does with the ones it does not.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because of the ONE way <c>display: table</c> could go wrong, and it
///         is not the way an unimplemented feature usually goes wrong.</b> Five audits of
///         <c>Rikarin/Vixen#258</c> reached the same conclusion — a table formatting context is a
///         fifth layout algorithm and none of it is written — and every one of them ended by warning
///         against the same shortcut: aliasing <c>table</c> onto <see cref="Display.Block" /> so the
///         keyword resolves. That would be worse than the silence, because a box that reads as a
///         table and lays out as a block is a finished-looking thing that lies, and nothing in this
///         tree would have contradicted it. Now something does.
///     </para>
///     <para>
///         ⚠ <b>The two halves check each other, which is what makes this an instrument rather than
///         a list.</b> The first is derived: every member of <see cref="Display" /> has to be
///         reachable, and the keyword count has to equal the enum's, so a ninth member cannot arrive
///         unnamed. The second is an explicit denial list, so a keyword that starts resolving reddens
///         it by name. A test that only did the first would pass on the day <c>table</c> became an
///         alias; a test that only did the second would pass on the day <c>display</c> stopped being
///         read at all, since an unread property and a dropped keyword both leave the default.
///     </para>
///     <para>
///         ⚠ <b><see cref="Display.Flex" /> is the default rather than <c>inline</c></b>, which is one
///         of the bridge's two deliberate departures from CSS: an element with no <c>display</c> at
///         all is far more often a container somebody forgot to declare. So "the declaration did
///         nothing" is not CSS's initial value — and it is read off an element with no declaration
///         rather than written as a constant, so that this file asks about the silence and never
///         about the default. What that buys, and the one thing it still cannot see, is on
///         <see cref="A_keyword_the_store_has_no_box_type_for_is_dropped_rather_than_aliased" />.
///     </para>
///     <para>
///         The declarations are written inline rather than through a stylesheet on purpose. ExCSS
///         validates while it parses, so a keyword it happens not to know would be dropped one layer
///         before the bridge and this file would be measuring ExCSS.
///     </para>
/// </remarks>
public class DisplayKeywordSurfaceTests {
    /// <summary>The keyword for each display this store has, which is the whole accepted surface.</summary>
    static readonly (string Keyword, Display Display)[] AcceptedPairs = [
        ("flex", Display.Flex),
        ("none", Display.None),
        ("block", Display.Block),
        ("grid", Display.Grid),
        ("inline", Display.Inline),
        ("inline-block", Display.InlineBlock),
        ("inline-flex", Display.InlineFlex),
        ("flow-root", Display.FlowRoot)
    ];

    /// <inheritdoc cref="AcceptedPairs" />
    public static TheoryData<string, Display> Accepted {
        get {
            var data = new TheoryData<string, Display>();

            foreach (var (keyword, display) in AcceptedPairs) {
                data.Add(keyword, display);
            }

            return data;
        }
    }

    /// <summary>
    ///     Every CSS <c>display</c> value this store has no box type for, table-internal and
    ///     otherwise.
    /// </summary>
    public static TheoryData<string> Refused =>
        [
            "table",
            "inline-table",
            "table-row-group",
            "table-header-group",
            "table-footer-group",
            "table-row",
            "table-cell",
            "table-column-group",
            "table-column",
            "table-caption",
            "list-item",
            "contents",
            "ruby",
            "inline-grid"
        ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public void An_accepted_keyword_reaches_the_layout_style(string keyword, Display display) {
        var style = new BridgeFixture().BuildInline(("display", keyword));

        Assert.Equal(display, style.Display);
    }

    /// <summary>
    ///     The accepted keywords are exactly as many as the store has box types, so a ninth cannot
    ///     arrive without being named here.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the enum rather than counted by hand</b>, which is the half that turns
    ///     the list above from documentation into a gate. Adding <c>Display.Table</c> reddens this
    ///     line whether or not a keyword was wired to it — and a member added with no keyword is the
    ///     other half of the same defect, a box type nothing can ask for.
    /// </remarks>
    [Fact]
    public void The_accepted_keywords_cover_the_display_enum_exactly() {
        var members = Enum.GetValues<Display>();
        var named = AcceptedPairs.Select(pair => pair.Display).ToHashSet();

        Assert.Equal(members.Length, AcceptedPairs.Length);
        Assert.Equal(members.ToHashSet(), named);
    }

    /// <summary>
    ///     A <c>display</c> this store has no box type for is dropped, and is not quietly aliased onto
    ///     one it does have.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion is the DEFAULT rather than "not table", and that is deliberate.</b>
    ///         Vixen has no <c>Display.Table</c> to be wrong about, so the only observable form the lie
    ///         could take is the element coming back as some other box type — <c>block</c> for the
    ///         table roots, <c>block</c> or <c>flex</c> for the internal ones. Asserting that the
    ///         declaration changed nothing at all is the one predicate that catches every version of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default is MEASURED here rather than typed, and the difference is not
    ///         tidiness.</b> Written as the literal <see cref="Display.Flex" />, this theory reddens
    ///         when the bridge's default moves — a question that is <i>not</i> what it is asking — and
    ///         the obvious repair is to retype the new default. Do that with <see cref="Display.Block" />
    ///         and the predicate quietly stops being able to fail in the way it exists to fail:
    ///         <c>block</c> is precisely the box type six audits warned <c>table</c> would be aliased
    ///         onto, so "dropped" and "aliased" would be the same answer. Reading the no-declaration
    ///         element instead keeps the two questions apart —
    ///         <c>LayoutStyleBridgeTests.The_display_this_method_leaves_alone_is_what_a_ceiling_two_
    ///         projects_away_stands_on</c> owns the default, and this owns the silence.
    ///     </para>
    ///     <para>
    ///         ⚠ And the limit that leaves, written down because nothing else in the tree can see it:
    ///         while <c>LayoutStyleBuilder.CreateCssInitial</c>'s display is <c>flex</c>, an aliased
    ///         <c>table</c> is observable here. If that default ever becomes <c>block</c> —
    ///         <c>Rikarin/Vixen#265</c> and <c>#682</c>, closed on keeping <c>flex</c> — a
    ///         <c>table</c> aliased onto <c>block</c> and a <c>table</c> dropped produce the identical
    ///         style and NO assertion at this seam can separate them. The discriminator would have to
    ///         move to the builder's keyword table itself.
    ///         <see cref="The_silence_is_only_observable_while_the_default_is_not_the_alias" /> is the
    ///         tripwire for that day.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refused))]
    public void A_keyword_the_store_has_no_box_type_for_is_dropped_rather_than_aliased(string keyword) {
        var declared = new BridgeFixture().BuildInline(("display", keyword));
        var undeclared = new BridgeFixture().BuildInline();

        Assert.Equal(undeclared.Display, declared.Display);
    }

    /// <summary>
    ///     The theory above can only tell a dropped keyword from an aliased one while the bridge's
    ///     default is not the box type an alias would choose.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An assumption a test rests on, asserted rather than assumed — this file's whole
    ///     argument against aliasing <c>display: table</c> onto <see cref="Display.Block" /> is
    ///     observable only because a box that did that would differ from a box with no
    ///     <c>display</c> at all.</b> The day <c>CreateCssInitial</c>'s display becomes <c>block</c>,
    ///     that stops being true and the denial theory silently loses its teeth without changing a
    ///     line. This fails on that day instead, and what it is asking for is a replacement
    ///     discriminator — the builder's own keyword table, which is where "is this keyword read at
    ///     all" actually lives — not a looser assertion.
    /// </remarks>
    [Fact]
    public void The_silence_is_only_observable_while_the_default_is_not_the_alias() {
        var undeclared = new BridgeFixture().BuildInline();

        Assert.NotEqual(Display.Block, undeclared.Display);
        Assert.NotEqual(Display.FlowRoot, undeclared.Display);
    }

    /// <summary>
    ///     The last declaration wins before the bridge ever sees it, so an unread keyword leaves the
    ///     default and not the value that lost.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This case was written to assert the opposite, went red, and the test was the
    ///         defect rather than the code.</b> The intended assertion was that a <c>display</c> the
    ///         bridge cannot read does not overwrite one it could — <c>display: grid</c> then
    ///         <c>display: table</c> coming out as <see cref="Display.Grid" />. It comes out as
    ///         <see cref="Display.Flex" />, and that is right: the CASCADE collapses the two
    ///         declarations before the bridge sees either, and <c>table</c> is the later one, so
    ///         <c>grid</c> is lost exactly as it would be in a browser. What a browser does next is
    ///         lay out a table; what this does is drop the only declaration left.
    ///     </para>
    ///     <para>
    ///         So "the keyword the bridge could not read" and "the value the element would otherwise
    ///         have had" cannot both exist at the bridge — one declaration per property arrives — and
    ///         the silence is total rather than partial. That is why the theory above asserts the
    ///         DEFAULT rather than a previous value, and it is the sharpest form of the argument
    ///         against aliasing: there is no half-measure to reach for, because there is no residue
    ///         of the author's intent left by the time layout is asked.
    ///     </para>
    ///     <para>
    ///         ⚠ The default is measured rather than typed here for the same reason as in the theory
    ///         above, and measuring it separates the two failures: aliasing <c>table</c> onto
    ///         <see cref="Display.Block" /> reddens this and moving the bridge's default does not,
    ///         where the literal reddened on both and named neither.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_last_declaration_wins_before_the_bridge_so_an_unread_keyword_leaves_the_default() {
        var style = new BridgeFixture().BuildInline(("display", "grid"), ("display", "table"));
        var undeclared = new BridgeFixture().BuildInline();

        Assert.Equal(undeclared.Display, style.Display);
    }
}
