// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     The four claims a key/value list stands or falls on, each measured rather than read.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here resolves a real element through the cascade or reads a
///         rectangle the layout produced.</b> A test that checked the stylesheet's text would pass
///         against a sheet the engine never applied — which is the failure the utility generator's
///         own suite documents at length — and three of the four things below were only decidable by
///         measuring: whether the selector engine implements <c>:nth-child</c>, whether the layout
///         implements flexbox's automatic minimum size, and what this renderer does with text that
///         does not fit.
///     </para>
///     <para>
///         The fixture is <see cref="ControlFixture" />, for the real theme and a real font. Both
///         halves matter: a document with no theme has no stripe to resolve, and a list measured
///         without a font is a list of zero-width keys — so the automatic-minimum-size case, the one
///         that matters, cannot happen at all. The first draft of this file measured 0 against 8 and
///         proved nothing.
///     </para>
/// </remarks>
public class KeyValueListTests {
    /// <summary>The stripe alternates, and it comes from the cascade rather than from a class.</summary>
    [Fact]
    public void Rows_alternate() {
        using var harness = new Harness();
        var list = harness.List(5);

        Assert.Equal(
            [false, true, false, true, false],
            list.Rows.Select(harness.IsShaded)
        );
    }

    /// <summary>
    ///     ⚠ And it re-resolves when a row is taken out of the middle, which is the whole argument
    ///     for <c>:nth-child</c> over a class applied when the row was built. A class would leave the
    ///     rows after the hole shaded exactly as they were, so the list would show two shaded rows
    ///     against each other and nobody would know which mutation caused it.
    /// </summary>
    [Fact]
    public void Removing_a_row_from_the_middle_re_resolves_the_stripe() {
        using var harness = new Harness();
        var list = harness.List(5);

        list.Rows[2].Remove();
        harness.Update();

        Assert.Equal(
            [false, true, false, true],
            list.Children.Select(harness.IsShaded)
        );
    }

    /// <summary>
    ///     ⚠ <b>And <c>order</c> does not touch it, which is the one mutation that changes where a
    ///     row <i>is</i> without changing which child it is.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         CSS Flexbox §5.4 changes layout and paint order and explicitly leaves selector
    ///         matching alone, so a striped list whose rows have been reordered still alternates by
    ///         <i>document</i> position — the shading travels with the row rather than staying with
    ///         the slot. Every other test in this file mutates the child list, where the stripe
    ///         moving is the correct answer; this is the case where it must not move, and the two
    ///         are indistinguishable until something can reorder without reparenting.
    ///     </para>
    ///     <para>
    ///         The reordering is asserted as well as applied. A stylesheet that failed to reach the
    ///         rows at all would leave the stripe exactly as expected and pass this test for the
    ///         wrong reason, which is the same trap the utility inventory exists for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Reordering_the_rows_does_not_move_the_stripe() {
        using var harness = new Harness();
        var list = harness.List(4);

        // The last row is pulled to the front of the line. `key-value-row` is the row's own tag, so
        // this reaches exactly the elements the stripe selector counts.
        harness.Document.Load("key-value-row:last-child { order: -1; }");
        harness.Update();

        var rows = list.Rows.Take(4).ToList();

        // It really did move: laid out first, above every sibling.
        Assert.True(
            rows[3].AbsoluteTop < rows[0].AbsoluteTop,
            "the ordered row is laid out in front of the others"
        );

        // And the stripe still alternates by document position, so the fourth row — an even child —
        // keeps its shading at the top of the list rather than picking up the first slot's.
        Assert.Equal([false, true, false, true], rows.Select(harness.IsShaded));
    }

    /// <summary>And when one is put back into the middle.</summary>
    [Fact]
    public void Inserting_a_row_into_the_middle_re_resolves_the_stripe() {
        using var harness = new Harness();
        var list = harness.List(4);

        var inserted = list.AddRow("inserted", "x");
        harness.Document.Move(inserted, 1);
        harness.Update();

        Assert.Equal(
            [false, true, false, true, false],
            list.Children.Select(harness.IsShaded)
        );
    }

    /// <summary>
    ///     A parked row is still an element, so it still counts — which is why <c>Trim</c> parks at
    ///     the end and the live rows keep the parity they had.
    /// </summary>
    [Fact]
    public void Trimming_does_not_disturb_the_rows_that_are_left() {
        using var harness = new Harness();
        var list = harness.List(6);

        list.Trim(3);
        harness.Update();

        Assert.Equal(3, list.Count);
        Assert.Equal([false, true, false], list.Rows.Take(3).Select(harness.IsShaded));

        // And the pool is still there, hidden rather than gone.
        Assert.Equal(6, list.Rows.Count);
        Assert.Equal(0f, list.Rows[4].Bounds.Height);
    }

    /// <summary>A row asked for again is the same element, which is the point of the pool.</summary>
    [Fact]
    public void A_trimmed_row_is_reused_rather_than_rebuilt() {
        using var harness = new Harness();
        var list = harness.List(4);

        var fourth = list.Rows[3];
        list.Trim(2);

        var reused = list.Row(3);
        harness.Update();

        Assert.Same(fourth, reused);
        Assert.True(reused.Bounds.Height > 0f);
    }

    /// <summary>
    ///     ⚠ <b>The halves stay equal under a key that does not fit.</b> The failing case is not a
    ///     long key — it is a long key with no space in it, because a flex item's automatic minimum
    ///     size is its <i>min-content</i> width and a sentence's min-content is its longest word.
    ///     Without <c>min-width: 0</c> the key measured 388 pixels inside a 200-pixel row and drew
    ///     straight over the value.
    /// </summary>
    [Fact]
    public void A_long_key_does_not_take_the_value_s_half() {
        using var harness = new Harness(width: 200f);
        var list = harness.List(0);

        var row = list.AddRow("AVeryLongUnbreakableKeyNameNobodyWouldChoose", "42");
        harness.Update();

        Assert.Equal(row.KeyPart.Bounds.Width, row.ValuePart.Bounds.Width, 0.5f);
        Assert.True(row.KeyPart.Bounds.Width > 0f, "the key column collapsed");

        // And the value starts where the key ends rather than under it.
        Assert.True(
            row.ValuePart.Bounds.X >= row.KeyPart.Bounds.X + row.KeyPart.Bounds.Width - 0.5f,
            $"the key overhangs its column: key={row.KeyPart.Bounds}, value={row.ValuePart.Bounds}"
        );
    }

    /// <summary>
    ///     ⚠ <b>Which is a claim about the theme, so it is worth failing without it.</b> The same
    ///     row with the automatic minimum left in place is what the assertion above would see, and it
    ///     is 388 against 100 — the number in the sheet's own remarks, checked rather than quoted.
    /// </summary>
    [Fact]
    public void The_automatic_minimum_is_what_the_theme_is_turning_off() {
        using var harness = new Harness(width: 200f);

        // The same two columns the theme writes, minus the one declaration under test. Built out of
        // bare tags rather than by unsetting the rule, because "min-width: auto" is a value the
        // bridge may or may not read back to nothing — and a negative control that depended on that
        // would be testing the parser instead of the layout.
        harness.Document.Load(
            """
            loose-row { flex-direction: row; }
            loose-key, loose-value { flex-grow: 1; flex-shrink: 1; flex-basis: 0px; white-space: nowrap; }
            """
        );

        var row = harness.Document.Root.Add("loose-row");
        var key = row.Add("loose-key");
        var value = row.Add("loose-value");

        key.Text = "AVeryLongUnbreakableKeyNameNobodyWouldChoose";
        value.Text = "42";

        harness.Update();

        Assert.True(
            key.Bounds.Width > value.Bounds.Width * 2f,
            $"the automatic minimum did not bite, so the test above proves nothing: key={key.Bounds}, value={value.Bounds}"
        );
    }

    /// <summary>
    ///     ⚠ <b>A key too long for its column is clipped, and this theme asks for no ellipsis.</b>
    ///     What the theme has to prevent is the other outcome: text that <i>wraps</i>, which turns
    ///     one row of a uniform list into seven lines of one. <c>overflow: hidden</c> cutting the
    ///     glyphs at the column's edge is the defined behaviour here.
    ///     <para>
    ///         ⚠ <b>The reason the assertion below is <c>Null</c> changed under it, and the wording
    ///         used to say the engine had no <c>text-overflow</c> at all.</b> It has one now — doc
    ///         43's F5 — so this row is clipped because <c>KeyValueList</c>'s theme does not set the
    ///         property, which is a choice, and not because setting it would do nothing, which was a
    ///         gap. Adding <c>text-overflow: ellipsis</c> to the key part would now give this list
    ///         ellipsised keys; whether it should is a design question this test does not answer.
    ///     </para>
    /// </summary>
    [Fact]
    public void A_long_key_is_clipped_rather_than_wrapped() {
        using var harness = new Harness(width: 200f);
        var list = harness.List(0);

        var short_ = list.AddRow("a", "1");
        var long_ = list.AddRow("a key long enough to wrap several times over if it were allowed to", "2");

        harness.Update();

        Assert.Equal(short_.Bounds.Height, long_.Bounds.Height, 0.5f);
        Assert.Null(harness.StyleOf(long_.KeyPart, "text-overflow"));
        Assert.Equal("nowrap", harness.StyleOf(long_.KeyPart, "white-space"));
        Assert.Equal("hidden", harness.StyleOf(long_.KeyPart, "overflow"));
    }

    /// <summary>
    ///     ⚠ <b>And the clip is real, which the three declarations above do not establish.</b>
    /// </summary>
    /// <remarks>
    ///     The version of this test that asserted <c>overflow: hidden</c> resolves passed against a
    ///     renderer that emitted an element's own text <i>before</i> its own clip — so every long key
    ///     drew straight across the value column while every rectangle in the tree stayed the right
    ///     size. The picture found it; this is the assertion that keeps it found. Counted in pixels
    ///     inside the value's rectangle, with the value itself left empty, so anything at all in
    ///     there came from the key.
    /// </remarks>
    [Fact]
    public void A_long_key_does_not_draw_into_the_value_column() {
        using var ui = ControlHarness.Open(200f, 60f, "root { background-color: #000000; flex-direction: column; }");

        var list = ui.Document.Root.Add<KeyValueList>();
        ui.Frame();

        var row = list.AddRow("AVeryLongUnbreakableKeyNameNobodyWouldChoose");
        ui.Frame();

        // ⚠ The value's *column*, taken as its horizontal span over the row's full height. An empty
        // value slot has no text and no children and therefore no height at all, so sampling its own
        // rectangle scans nothing and reports zero however badly the key overdraws — which is the
        // first version of this test, and it passed with the renderer bug reinstated.
        var slot = row.ValuePart.Bounds;
        var line = row.Bounds;

        Assert.Equal(0L, ui.InkIn((int) slot.X, (int) line.Y, (int) slot.Width, (int) line.Height));

        // And the key half is not blank, so the zero above is a clip rather than a row that drew
        // nothing at all.
        var key = row.KeyPart.Bounds;
        Assert.True(ui.InkIn((int) key.X, (int) line.Y, (int) key.Width, (int) line.Height) > 0L);
    }

    /// <summary>
    ///     The value half takes an input control, which is the whole reason it is an element. A
    ///     settings panel is the case this exists for, and a slot that only took strings would not
    ///     survive one.
    /// </summary>
    [Fact]
    public void The_value_half_holds_an_editor() {
        using var harness = new Harness(width: 300f);
        var list = harness.List(0);

        var text = list.AddRow("Name").Content<TextBox>();
        var toggle = list.AddRow("Enabled").Content<Switch>();
        var slider = list.AddRow("Volume").Content<Slider>();

        harness.Update();

        // The field fills its half; the switch is the size a switch is. Both are laid out, which is
        // the assertion — a slot with text in it would have laid out neither.
        Assert.Equal(list.Rows[0].ValuePart.Bounds.Width, text.Bounds.Width, 0.5f);
        Assert.True(toggle.Bounds.Width > 0f && toggle.Bounds.Width < list.Rows[1].ValuePart.Bounds.Width);
        Assert.Equal(list.Rows[2].ValuePart.Bounds.Width, slider.Bounds.Width, 0.5f);
    }

    /// <summary>
    ///     ⚠ And the two ways of filling the slot undo each other, because the framework will not
    ///     have both: an element with text measures itself, and the layout treats a node that
    ///     measures itself as a leaf and never lays out its children. A pooled row that switched from
    ///     an editor to a string without this would draw the string over an editor placed nowhere.
    /// </summary>
    [Fact]
    public void Text_and_an_editor_replace_each_other() {
        using var harness = new Harness(width: 300f);
        var list = harness.List(0);

        var row = list.AddRow("Name", "Crate");
        var field = row.Content<TextBox>();
        harness.Update();

        Assert.Null(row.ValuePart.Text);
        Assert.True(field.Bounds.Width > 0f);

        row.Value = "Crate";
        harness.Update();

        Assert.Empty(row.ValuePart.Children);
        Assert.Equal("Crate", row.ValuePart.Text);
    }

    /// <summary>A heading wins over the stripe whichever side of the alternation it lands on.</summary>
    [Fact]
    public void A_heading_is_shaded_on_both_parities() {
        using var harness = new Harness();
        var list = harness.List(0);

        var first = list.AddRow("Managed heap");
        first.IsHeading = true;

        list.AddRow("Gen 0", "1 KiB");

        var third = list.AddRow("Native");
        third.IsHeading = true;

        harness.Update();

        Assert.True(harness.IsShaded(first));
        Assert.True(harness.IsShaded(third));

        // And it carries a rule the plain rows do not, so it reads as a heading rather than as a row
        // that happened to fall on an even index.
        Assert.NotNull(harness.LengthOf(first, "border-bottom-width"));
        Assert.True(harness.LengthOf(first, "border-bottom-width") > 0f);
    }

    /// <summary>
    ///     The whole thing from a <c>.vxml</c>: the tags resolve to the controls, the loop's
    ///     <c>key</c> and the row's <c>Key</c> coexist, and a control written inside a row lands in
    ///     its value half.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two <c>key</c>s are the case worth having a fixture for.</b> The binder classifies
    ///     the attribute name with <c>StringComparison.Ordinal</c>, so lowercase <c>key</c> is the
    ///     loop's identity and <c>Key</c> is a property assignment — a control whose natural property
    ///     name happened to be the one reserved word of the loop syntax would be unusable in the one
    ///     place it is most wanted, and nothing but compiling the markup says which way it went.
    /// </remarks>
    [Fact]
    public void The_list_is_reachable_from_markup() {
        using var harness = new Harness();

        var sheet = new FactSheet {
            Rows = [new("Draw calls", "1 204"), new("Triangles", "98 331")]
        };

        BuildContext.BuildInto(sheet, harness.Document, harness.Document.Root);
        harness.Update();

        var list = Assert.IsType<KeyValueList>(sheet.Root.Children[0]);
        Assert.Equal("key-value-list", list.Tag);

        var rows = list.Children.Cast<KeyValueRow>().ToArray();
        Assert.Equal(3, rows.Length);

        Assert.Equal("Draw calls", rows[0].KeyPart.Text);
        Assert.Equal("1 204", rows[0].ValuePart.Text);
        Assert.Equal("Triangles", rows[1].KeyPart.Text);

        // The slider written between the row's tags is in the row's value half, which is
        // `ContentHost` doing its job — markup cannot name a part.
        Assert.Equal("Volume", rows[2].KeyPart.Text);
        var slider = Assert.IsType<Slider>(Assert.Single(rows[2].ValuePart.Children));
        Assert.Equal(0.5f, slider.Value, 0.001f);

        // And the stripe reached elements the markup made, not just ones a test made.
        Assert.Equal([false, true, false], rows.Select(harness.IsShaded));
    }

    /// <summary>
    ///     ⚠ A list nobody sized is still a list, which is not free: <c>min-width: 0</c> on both
    ///     columns means the container's own intrinsic width is zero, so in a parent that does not
    ///     stretch it — a row, which is CSS's initial — it collapses to its padding and its gap. The
    ///     floor in the theme is what stops that, and this is the case that has no column above it.
    /// </summary>
    [Fact]
    public void A_list_in_a_row_does_not_collapse() {
        using var ui = ControlHarness.Open(400f, 200f);

        var list = ui.Document.Root.Add<KeyValueList>();
        ui.Frame();

        var row = list.AddRow("Draw calls", "1 204");
        ui.Frame();

        Assert.True(list.Bounds.Width >= 160f, $"the list collapsed to {list.Bounds.Width}px");
        Assert.True(row.KeyPart.Bounds.Width > 0f, "the key column has no width");
        Assert.Equal(row.KeyPart.Bounds.Width, row.ValuePart.Bounds.Width, 0.5f);
    }

    /// <summary>
    ///     The list, drawn — the one question every assertion above is unable to answer.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Rectangles are not a picture.</b> Everything else here reads a resolved colour or a
    ///     measured box, and every one of them would pass against a list whose stripe is the same
    ///     shade as its surface, whose key text is drawn over its value's, or whose slider is a
    ///     hairline. The alternation, the shared column edge and the editor in the value half are
    ///     things that are either visibly right or visibly wrong, and this is where somebody looks.
    ///     <para>
    ///         The fixture is deliberately narrow and holds one over-long key, because the picture
    ///         worth committing is the one with the failure case in it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_list_looks_right() {
        // ⚠ On `--surface`, because an unstriped row has no background of its own — the zebra is
        // *one* shade painted on alternate rows, and against the harness's bare root the other half
        // is whatever is behind the document. A picture taken without this is a picture of dark text
        // on black for every second row, which is a fixture with no panel in it rather than a
        // control with a bug in it, and the two look identical.
        using var ui = ControlHarness.Open(280f, 180f, "root { background-color: var(--surface); flex-direction: column; }");

        var list = ui.Document.Root.Add<KeyValueList>();
        ui.Frame();

        var heading = list.AddRow("Rasteriser");
        heading.IsHeading = true;

        list.AddRow("Draw calls", "1 204");
        list.AddRow("AVeryLongUnbreakableKeyName", "98 331");
        list.AddRow("Culled", "12");

        var volume = list.AddRow("Volume").Content<Slider>();
        volume.Minimum = 0f;
        volume.Maximum = 1f;
        volume.Value = 0.5f;

        ui.Frame();
        ui.Screenshot("key-value-list");
    }

    /// <summary>A document with the theme, a font, and a way to ask what the cascade decided.</summary>
    /// <remarks>
    ///     ⚠ <b>The readings go through <see cref="UiDocument" /> rather than through
    ///     <c>UiTest</c>.</b> Both answer the same question — <c>UiTest.ColorOf</c> is a wrapper over
    ///     this — but a harness holding two objects that each dispose the document is a harness whose
    ///     teardown aborts the process, and it aborts before any test result is written, so the suite
    ///     hangs rather than fails. Reading through the document is one owner and the same answer.
    /// </remarks>
    sealed class Harness : IDisposable {
        readonly ControlFixture fixture;

        public Harness(float width = 400f, float height = 600f) {
            fixture = new(width, height);

            // ⚠ A column, because the root's own direction is CSS's initial — a row — and a list
            // dropped into a row is sized by its content rather than stretched across the panel.
            // Every measurement below is about how a fixed width is divided, so it has to have one.
            fixture.Document.Load($"root {{ width: {width}px; height: {height}px; flex-direction: column; }}");

            Update();
        }

        public UiDocument Document => fixture.Document;

        public void Update() => fixture.Update();

        /// <summary>What the cascade resolved a property to on an element, as text.</summary>
        /// <remarks>
        ///     <c>Lookup</c> rather than <c>Intern</c>, for the reason <c>UiTest.StyleOf</c> gives:
        ///     asking about a property no stylesheet mentions must not add it to the table.
        /// </remarks>
        public string? StyleOf(UiElement element, string property) {
            var id = Document.Styles.Properties.Lookup(property);

            return id == NameTable.None || !element.Style.TryGet(id, out var value)
                ? null
                : Document.Styles.Values.NameOf(value);
        }

        public float? LengthOf(UiElement element, string property) {
            var id = Document.Styles.Properties.Lookup(property);
            return id == NameTable.None ? null : Document.LengthOf(element.Style, id);
        }

        /// <summary>A list with that many plain rows in it, laid out.</summary>
        public KeyValueList List(int count) {
            var list = Document.Root.Add<KeyValueList>();

            for (var index = 0; index < count; index++) {
                list.AddRow("key " + index, "value " + index);
            }

            Update();
            return list;
        }

        /// <summary>Whether the cascade gave a row the stripe's background.</summary>
        /// <remarks>
        ///     Against <c>--surface-sunken</c> resolved from the theme rather than against a literal,
        ///     so the assertion follows a retheme instead of pinning the palette.
        /// </remarks>
        public bool IsShaded(UiElement row) {
            var id = Document.Styles.Properties.Lookup("background-color");

            return id != NameTable.None
                && Document.ColorOf(row.Style, id) is { } colour
                && colour != default(Color4);
        }

        public void Dispose() => fixture.Dispose();
    }
}
