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
///         nothing" reads as <c>Flex</c> below, not as CSS's initial value.
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
    ///     ⚠ <b>The assertion is the DEFAULT rather than "not table", and that is deliberate.</b>
    ///     Vixen has no <c>Display.Table</c> to be wrong about, so the only observable form the lie
    ///     could take is the element coming back as some other box type — <c>block</c> for the
    ///     table roots, <c>block</c> or <c>flex</c> for the internal ones. Asserting that the
    ///     declaration changed nothing at all is the one predicate that catches every version of it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refused))]
    public void A_keyword_the_store_has_no_box_type_for_is_dropped_rather_than_aliased(string keyword) {
        var style = new BridgeFixture().BuildInline(("display", keyword));

        Assert.Equal(Display.Flex, style.Display);
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
    /// </remarks>
    [Fact]
    public void The_last_declaration_wins_before_the_bridge_so_an_unread_keyword_leaves_the_default() {
        var style = new BridgeFixture().BuildInline(("display", "grid"), ("display", "table"));

        Assert.Equal(Display.Flex, style.Display);
    }
}
