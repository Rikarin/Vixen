// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using ExCSS;
using Xunit;
using StylesheetParser = ExCSS.StylesheetParser;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     A shorthand holding a <c>var()</c> has to reach the cascade as the same longhands the same
///     shorthand without one does.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asserted against ExCSS's own expansion rather than against a table written here.</b>
///         The bug was that a var-bearing shorthand behaved differently from a var-free one — so a
///         test that stated what the longhands ought to be would be stating the same opinion twice
///         and would agree with itself while disagreeing with the parser. Every case below parses
///         the concrete form, records what came out, and asks the var form for the same property
///         names.
///     </para>
///     <para>
///         The one that mattered: every stylesheet in this repository writes
///         <c>border-color: var(--border)</c>, the draw list reads <c>border-top-color</c>, and
///         nothing joined the two — so no control in the framework drew a border.
///     </para>
/// </remarks>
public class ShorthandExpansionTests {
    static readonly StylesheetParser Parser = new(true, true, true, true, true);

    [Theory]
    [InlineData("border-color", "#383c43", "var(--border)")]
    [InlineData("border-color", "#111 #222", "var(--a) var(--b)")]
    [InlineData("border-color", "#111 #222 #333 #444", "var(--a) var(--b) var(--c) var(--d)")]
    [InlineData("border-width", "1px", "var(--w)")]
    [InlineData("border-style", "solid", "var(--s)")]
    [InlineData("border-radius", "6px", "var(--corner)")]
    [InlineData("border-radius", "1px 2px", "var(--a) 2px")]
    [InlineData("margin", "4px", "var(--m)")]
    [InlineData("margin", "4px 8px", "4px var(--m)")]
    [InlineData("padding", "1px 2px 3px", "1px var(--p) 3px")]
    [InlineData("gap", "4px 12px", "4px var(--g)")]
    [InlineData("border", "1px solid #383c43", "1px solid var(--border)")]
    [InlineData("border-top", "2px dashed #111", "2px dashed var(--c)")]
    public void A_var_reaches_the_same_longhands_a_literal_does(string property, string literal, string deferred) {
        var expected = ExpandedBy(Parser, property, literal);
        Assert.NotEqual(new[] { property }, expected);

        List<KeyValuePair<string, string>> actual = [];
        Assert.True(ShorthandExpansion.TryExpand(property, deferred, actual));

        Assert.Equal(expected, actual.Select(pair => pair.Key).ToArray());
    }

    [Theory]
    [InlineData("border-color", "var(--border)", "border-top-color", "var(--border)")]
    [InlineData("border-color", "var(--a) var(--b)", "border-left-color", "var(--b)")]
    [InlineData("margin", "1px var(--m) 3px", "margin-right", "var(--m)")]
    [InlineData("gap", "var(--g)", "column-gap", "var(--g)")]
    [InlineData("border", "1px solid var(--c)", "border-bottom-color", "var(--c)")]
    [InlineData("border", "1px solid var(--c)", "border-bottom-width", "1px")]
    [InlineData("border-left", "var(--w) solid #111", "border-left-width", "var(--w)")]
    public void The_value_lands_on_the_side_it_names(string property, string value, string longhand, string expected) {
        List<KeyValuePair<string, string>> actual = [];
        Assert.True(ShorthandExpansion.TryExpand(property, value, actual));

        Assert.Equal(expected, actual.Single(pair => pair.Key == longhand).Value);
    }

    /// <summary>
    ///     ⚠ A corner's longhand takes two radii, and the shorthand's one value is both of them.
    /// </summary>
    [Fact]
    public void A_corner_gets_the_horizontal_and_vertical_radius_ExCSS_writes() {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.True(ShorthandExpansion.TryExpand("border-radius", "var(--corner)", expanded));

        Assert.All(expanded, pair => Assert.Equal("var(--corner) var(--corner)", pair.Value));
    }

    /// <summary>
    ///     A <c>var()</c> in the shorthand may only take a role nothing else can be holding.
    /// </summary>
    /// <remarks>
    ///     ⚠ The refusal is the point. A colour written into the width slot is a border whose
    ///     thickness changes with the theme, which is a far worse outcome than a border that is not
    ///     drawn — and the loader reports it rather than dropping it silently.
    /// </remarks>
    [Theory]
    [InlineData("border", "var(--everything)")]
    [InlineData("border", "1px var(--rest)")]
    [InlineData("border", "var(--a) var(--b) var(--c)")]
    [InlineData("border-top", "solid var(--rest)")]
    public void An_ambiguous_border_is_refused_rather_than_guessed(string property, string value) {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.False(ShorthandExpansion.TryExpand(property, value, expanded));
    }

    /// <summary>The elliptical form is a different shape, and is refused for being one.</summary>
    [Fact]
    public void An_elliptical_radius_is_refused() {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.False(ShorthandExpansion.TryExpand("border-radius", "var(--a) / var(--b)", expanded));
    }

    /// <summary>
    ///     ⚠ A <c>var()</c> with a multi-word fallback is one component, not three.
    /// </summary>
    [Fact]
    public void A_fallback_holding_spaces_does_not_split_the_shorthand() {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.True(ShorthandExpansion.TryExpand("padding", "var(--pad, 4px 8px)", expanded));

        Assert.Equal(4, expanded.Count);
        Assert.All(expanded, pair => Assert.Equal("var(--pad, 4px 8px)", pair.Value));
    }

    [Theory]
    [InlineData("inset")]
    [InlineData("flex")]
    [InlineData("color")]
    [InlineData("background-color")]
    [InlineData("transition")]
    [InlineData("grid-area")]
    [InlineData("place-self")]
    [InlineData("place-items")]
    [InlineData("place-content")]
    public void What_this_deliberately_leaves_alone(string property) => Assert.False(ShorthandExpansion.IsShorthand(property));

    // ── The two ExCSS has never heard of ────────────────────────────────────────────────────────

    /// <summary>ExCSS really does hand these two back whole, which is the premise of the rest.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted rather than assumed, for the same reason the tests above are.</b> Everything
    ///     below only makes sense if the parser leaves <c>grid-row</c> alone; if a later ExCSS learns
    ///     the property, this file would go on expanding it a second time and this is the row that
    ///     would say so. Note that it holds for the literal form and not only the <c>var()</c> one —
    ///     that difference is exactly what <see cref="ShorthandExpansion.NeedsExpanding" /> is for.
    /// </remarks>
    [Theory]
    [InlineData("grid-column", "1 / 3")]
    [InlineData("grid-row", "span 2 / span 2")]
    public void ExCSS_leaves_a_placement_shorthand_whole(string property, string literal) =>
        Assert.Equal([property], ExpandedBy(Parser, property, literal));

    /// <summary>So a literal one needs expanding here, where a literal <c>margin</c> does not.</summary>
    [Theory]
    [InlineData("grid-row", "1 / -1", true)]
    [InlineData("grid-column", "span 2", true)]
    [InlineData("grid-column", "var(--place)", true)]
    [InlineData("margin", "4px", false)]
    [InlineData("border-color", "#111", false)]
    [InlineData("margin", "var(--m)", true)]
    [InlineData("inset", "0", false)]
    [InlineData("grid-template-columns", "1fr 1fr", false)]
    public void Whether_it_needs_expanding_is_the_property_and_the_value(string property, string value, bool expected) =>
        Assert.Equal(expected, ShorthandExpansion.NeedsExpanding(property, value));

    /// <summary>The two edges, and an omitted one written out as <c>auto</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><c>auto</c> is emitted rather than left off, and that is the half that fixes the
    ///     bug.</b> A shorthand resets every longhand it covers; if the end edge were simply omitted,
    ///     a <c>grid-column-end: 4</c> from an earlier rule would survive a later
    ///     <c>grid-column: 1</c> — which is the silent precedence failure this expansion exists to
    ///     remove, reintroduced one edge at a time.
    /// </remarks>
    [Theory]
    [InlineData("grid-column", "1 / 3", "1", "3")]
    [InlineData("grid-row", "1 / -1", "1", "-1")]
    [InlineData("grid-row", "span 2 / span 2", "span 2", "span 2")]
    [InlineData("grid-column", "span 2", "span 2", "auto")]
    [InlineData("grid-row", "auto", "auto", "auto")]
    [InlineData("grid-column", "  2  /  4  ", "2", "4")]
    [InlineData("grid-row", "var(--a) / var(--b)", "var(--a)", "var(--b)")]
    public void A_placement_divides_into_a_start_and_an_end(string property, string value, string start, string end) {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.True(ShorthandExpansion.TryExpand(property, value, expanded));

        Assert.Equal([$"{property}-start", $"{property}-end"], expanded.Select(pair => pair.Key).ToArray());
        Assert.Equal([start, end], expanded.Select(pair => pair.Value).ToArray());
    }

    /// <summary>What it will not divide, and why each one would be a wrong answer rather than none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A bare <c>var()</c> is refused because the slash may be inside it.</b>
    ///         <c>--place: 1 / 3</c> is a start edge and an end edge, and calling the whole reference
    ///         a start edge would turn a declaration the bridge reads today into one it refuses. Left
    ///         whole, it reaches the bridge's own shorthand reading after substitution.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two slashes are <c>grid-area</c> under the wrong name</b>, and taking the first
    ///         two of its four edges puts the item in a real but wrong cell — the same judgement, and
    ///         for the same stated reason, that <c>GridPlacement.TryParseShorthand</c> makes.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("grid-column", "var(--place)")]
    [InlineData("grid-row", "var(--a, 1 / 2)")]
    [InlineData("grid-column", "1 / 2 / 3")]
    [InlineData("grid-row", "1 /")]
    [InlineData("grid-row", "/ 3")]
    [InlineData("grid-column", "   ")]
    public void An_undividable_placement_is_refused_rather_than_guessed(string property, string value) {
        List<KeyValuePair<string, string>> expanded = [];
        Assert.False(ShorthandExpansion.TryExpand(property, value, expanded));
    }

    /// <summary>End to end: the later declaration wins, and it wins in both directions.</summary>
    /// <remarks>
    ///     ⚠ <b>The shorthand-last row is the assertion; the longhand-last row is the control.</b>
    ///     Before the expansion, the layout bridge applied the shorthand and then overwrote each half
    ///     from the longhands in an order fixed in code — so the longhand-last case was already right
    ///     and a test that checked only that direction would have passed against the bug. Asserted on
    ///     the cascade rather than on geometry here because that is where the ordering now happens;
    ///     <c>Vixen.Ui.Tests.GridFromCssTests</c> measures the boxes.
    /// </remarks>
    [Theory]
    [InlineData("grid-row-start: 3; grid-row: 1 / -1;", "grid-row-start", "1")]
    [InlineData("grid-row-start: 3; grid-row: 1 / -1;", "grid-row-end", "-1")]
    [InlineData("grid-row: 1 / -1; grid-row-start: 3;", "grid-row-start", "3")]
    [InlineData("grid-row: 1 / -1; grid-row-start: 3;", "grid-row-end", "-1")]
    [InlineData("grid-column-end: 4; grid-column: 1;", "grid-column-end", "auto")]
    public void The_placement_written_last_wins(string declarations, string property, string expected) {
        var fixture = new CascadeFixture();
        fixture.Load($"item {{ {declarations} }}");

        Assert.Equal(expected, fixture.Value(fixture.Tree.CreateElement("item"), property));
    }

    /// <summary>End to end: the sheet the controls ship with now reaches the property the paint reads.</summary>
    [Fact]
    public void A_bordered_control_resolves_a_border_top_color() {
        var fixture = new CascadeFixture();

        fixture.Load("""
            root { --border: #d8dbe0; }
            button { border-width: 1px; border-color: var(--border); }
            """);

        var root = fixture.Tree.CreateElement("root");
        var rootStyle = fixture.Engine.Resolver.Resolve(fixture.Tree, root);

        var button = fixture.Tree.CreateElement("button", parent: root);

        Assert.Equal("#d8dbe0", fixture.Value(button, "border-top-color", rootStyle));
        Assert.Equal("#d8dbe0", fixture.Value(button, "border-left-color", rootStyle));
    }

    /// <summary>
    ///     ⚠ Expanded at load, which is what keeps the cascade in charge of which one wins.
    /// </summary>
    /// <remarks>
    ///     Expanding after substitution instead would let the longhand below win on every element,
    ///     because by then the cascade has already chosen a winner for each property separately and
    ///     <c>border-color</c> and <c>border-top-color</c> are two of them.
    /// </remarks>
    [Fact]
    public void A_shorthand_from_a_stronger_rule_beats_a_longhand_from_a_weaker_one() {
        var fixture = new CascadeFixture();

        fixture.Load("""
            root { --border: shorthand; }
            button { border-top-color: longhand; }
            button.primary { border-color: var(--border); }
            """);

        var root = fixture.Tree.CreateElement("root");
        var rootStyle = fixture.Engine.Resolver.Resolve(fixture.Tree, root);

        var button = fixture.Tree.CreateElement("button", parent: root, classNames: ["primary"]);

        Assert.Equal("shorthand", fixture.Value(button, "border-top-color", rootStyle));
    }

    /// <summary>Every longhand a concrete shorthand produces, in ExCSS's own order.</summary>
    static string[] ExpandedBy(StylesheetParser parser, string property, string value) {
        var sheet = parser.Parse($"x {{ {property}: {value}; }}");
        var rule = (IStyleRule) sheet.Children.First();

        return [.. rule.Style.Select(declaration => declaration.Name)];
    }
}
