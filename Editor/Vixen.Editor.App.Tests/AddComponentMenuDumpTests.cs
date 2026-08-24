// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The ported Add Component picker, held to the rows and the pool it replaced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A committed test rather than a wave note.</b> Doc 36 § F7 wave 6 found that "byte
///         identical in N dumped states" was claimed by nine ledger rows and gated by three test
///         files: every other comparison had been run once, eyeballed and deleted. This is the
///         comparison, kept.
///     </para>
///     <para>
///         ⚠ <b>Two dumps, because a tree dump is blind.</b> <c>UiTest.Tree</c> sees tags, classes,
///         rectangles and text; a row's <c>Label</c> lives in a part the control owns and its
///         highlight is a bit of <c>ElementState</c>, and neither appears. <c>UiTest.Flags</c> is
///         the second half. Wave 7 proved the gap matters: <c>StandardFrameView</c> matched
///         byte-for-byte in six states while carrying a binding that could not work.
///     </para>
///     <para>
///         ⚠ <b>And the panel is driven through the control.</b> The query is written to
///         <c>Field.Value</c> and the arrows are real key presses, so what is exercised is the
///         <c>change:Value</c> binding and the capture-leg key handler rather than a model somebody
///         poked. Wave 7's dumps only ever wrote to the panel's model, which is the leg that cannot
///         fail.
///     </para>
/// </remarks>
public sealed class AddComponentMenuDumpTests {
    // ── The row, against the four lines the pool wrote ───────────────────────

    /// <summary>
    ///     A line that adds something: no arrow, and the quiet word on the right.
    /// </summary>
    [Fact]
    public void An_entry_row_is_the_one_the_pool_built() => SameRow(
        handWritten: row => {
            row.Index = 3;
            row.Label = "Audio Source";
            row.Arrow.SetStyle("display", "none");
            row.DetailPart.Text = "Audio";
        },
        ported: row => {
            row.Index = 3;
            row.Label = "Audio Source";
            row.Opening = RowArrow.None;
            row.Detail = "Audio";
        }
    );

    /// <summary>A category to go into: the right chevron, and the count of what is in it.</summary>
    [Fact]
    public void A_category_row_is_the_one_the_pool_built() => SameRow(
        handWritten: row => {
            row.Index = 0;
            row.Arrow.SetStyle("display", "flex");
            row.Arrow.Geometry = ControlIcons.ChevronRight;
            row.Label = "Rendering";
            row.RemoveClass("back");
            row.DetailPart.Text = "7";
        },
        ported: row => {
            row.Index = 0;
            row.Opening = RowArrow.Into;
            row.Label = "Rendering";
            row.Detail = "7";
        }
    );

    /// <summary>
    ///     The way back out: the same chevron turned round, and the class that greys it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The arrow is turned rather than swapped for another glyph, and it is turned in code
    ///     rather than by a rule — a transform is not something this layout applies. The port kept
    ///     that and moved it behind one property; this is what says the property writes both halves.
    /// </remarks>
    [Fact]
    public void The_way_back_out_is_the_one_the_pool_built() => SameRow(
        handWritten: row => {
            row.Index = 0;
            row.Arrow.SetStyle("display", "flex");
            row.Arrow.Geometry = ControlIcons.ChevronLeft;
            row.Label = "All categories";
            row.DetailPart.Text = "";
            row.AddClass("back");
        },
        ported: row => {
            row.Index = 0;
            row.Opening = RowArrow.Out;
            row.Label = "All categories";
            row.Detail = "";
            row.AddClass("back");
        }
    );

    /// <summary>And the highlight, which is one bit of a flag set the pool wrote by hand.</summary>
    /// <remarks>
    ///     ⚠ <b>The one state a tree dump cannot see at all.</b> <c>ElementState.Checked</c> paints
    ///     through <c>add-component-row:checked</c>, so a port that lost it draws a differently
    ///     coloured row and moves no element. This is the pair of dumps that would notice.
    /// </remarks>
    [Fact]
    public void The_highlight_is_the_bit_the_pool_set() {
        var handWritten = SameRow(
            handWritten: row => {
                row.Label = "Camera";
                row.Arrow.SetStyle("display", "none");
                row.State |= Vixen.Ui.Styling.ElementState.Checked;
            },
            ported: row => {
                row.Label = "Camera";
                row.Opening = RowArrow.None;
                row.Selected = true;
            }
        );

        Assert.Contains("State=Checked", handWritten.Flags, StringComparison.Ordinal);
        Assert.Contains("Label=\"Camera\"", handWritten.Flags, StringComparison.Ordinal);
    }

    // ── The pool, which is what the port deletes ─────────────────────────────

    /// <summary>
    ///     ⚠ <b>A narrowed query leaves no rows behind, which the pool could not manage.</b> The
    ///     hand-written picker grew its row list to the high-water mark of every list it had ever
    ///     shown and parked the surplus under <c>display: none</c> — <b>still labelled with the
    ///     previous query's components</b>. This is the assertion that says the tree holds what is on
    ///     screen and nothing else, and it is written against the dump rather than against
    ///     <c>Rows.Count</c> so that a hidden element counts.
    /// </summary>
    [Fact]
    public void Narrowing_the_query_leaves_nothing_behind() {
        using var editor = Selected();

        var picker = Open(editor);

        picker.Field.Value = "a";
        editor.Settle();

        var wide = Dump(editor, picker);
        var many = picker.LineCount;

        Assert.True(many > 3, $"the wide query should match plenty, and matched {many}");

        picker.Field.Value = "Camera";
        editor.Settle();

        var narrow = Dump(editor, picker);

        Assert.True(picker.LineCount < many, "the narrow query should match fewer");

        // One row element per line, hidden ones included: this is the count the pool got wrong.
        Assert.Equal(
            picker.LineCount,
            narrow.Tree.Split('\n').Count(line => line.Contains("<add-component-row", StringComparison.Ordinal))
        );

        Assert.DoesNotContain("parked", narrow.Tree, StringComparison.Ordinal);

        // And no label from the wider list survives into the narrower one, which is the half a
        // count cannot see — a parked row keeps its text.
        foreach (var label in Labels(wide.Flags).Except(Labels(narrow.Flags), StringComparer.Ordinal)) {
            Assert.DoesNotContain(label, narrow.Flags, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠ <b>Typing is the <c>change:</c> leg, driven through the control.</b> A
    ///     <c>SearchBox</c>'s value is a control's state and not a signal, so the list follows it only
    ///     because <c>change:Value</c> writes one — a binding that read <c>Field.Value</c> directly
    ///     would compile, run once and never re-run. Wave 7 shipped exactly that defect and its
    ///     dumps did not see it, because they drove the panel from the model.
    /// </summary>
    [Fact]
    public void Typing_into_the_field_is_what_moves_the_list() {
        using var editor = Selected();

        var picker = Open(editor);
        var categories = Dump(editor, picker);

        // Categories to begin with, which is a list of headings with a chevron each.
        Assert.Contains("<icon", categories.Tree, StringComparison.Ordinal);

        picker.Field.Value = "Camera";
        editor.Settle();

        var found = Dump(editor, picker);

        Assert.NotEqual(categories.Tree, found.Tree);
        Assert.Contains("Label=\"Camera\"", found.Flags, StringComparison.Ordinal);

        // And back, because a binding that fired once would leave the results standing.
        picker.Field.Value = string.Empty;
        editor.Settle();

        Assert.Equal(categories.Tree, Dump(editor, picker).Tree);
    }

    /// <summary>
    ///     ⚠ <b>The arrows move the highlight and rebuild nothing.</b> <c>Selected</c> is a binding
    ///     inside each row's own region, so pressing Down changes no key — where putting the
    ///     highlight in the key would have destroyed and rebuilt two elements per press. The tree
    ///     dump is unchanged and the flags dump is not, which is exactly the pair of facts a single
    ///     dump could not state.
    /// </summary>
    [Fact]
    public void Arrowing_down_moves_the_highlight_and_rebuilds_no_row() {
        using var editor = Selected();

        var picker = Open(editor);

        editor.Settle();

        var before = Dump(editor, picker);
        var first = picker.Rows[0];

        Assert.Single(Checked(before.Flags));

        editor.Ui.PressKey(Vixen.Input.InputKey.Down);
        editor.Settle();

        var after = Dump(editor, picker);

        Assert.Equal(before.Tree, after.Tree);
        Assert.NotEqual(before.Flags, after.Flags);
        Assert.Single(Checked(after.Flags));

        // The same object, so the region survived and only the bit moved.
        Assert.Same(first, picker.Rows[0]);
        Assert.False(first.Selected);
        Assert.True(picker.Rows[1].Selected);
    }

    /// <summary>
    ///     <c>Part&lt;ScrollView&gt;("add-component-list")</c>, said in markup, and still a scroller.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tag is the whole reason this panel was blocked</b>, and it is load-bearing:
    ///     <c>add-component-list</c> is where <c>max-height: 300px</c> is declared, because "the cap
    ///     belongs on the thing that scrolls". A control created under its own <c>TagName</c> would
    ///     be a list with no cap and a popup as tall as the project.
    /// </remarks>
    [Fact]
    public void The_list_is_a_scroller_under_the_tag_the_stylesheet_names() {
        using var editor = Selected();

        var picker = Open(editor);

        Assert.Equal("add-component-list", picker.List.Tag);

        // Not a plain element wearing the name: a scroller builds its viewport in `OnCreated`.
        Assert.NotSame(picker.List, picker.List.Content);
        Assert.All(picker.Rows, row => Assert.Same(picker.List.Content, row.Parent));
    }

    // ── The comparison ───────────────────────────────────────────────────────

    /// <summary>Builds a row both ways in the same place and asserts the two dumps agree.</summary>
    /// <returns>What the ported form drew, for a caller that wants to say more about it.</returns>
    /// <remarks>
    ///     ⚠ <b>Under <c>add-component-menu</c>, because a row's width is its parent's.</b> The two
    ///     hosts are created and destroyed in the same position, which is what makes two dumps of
    ///     absolute rectangles comparable at all.
    /// </remarks>
    static (string Tree, string Flags) SameRow(
        Action<AddComponentRow> handWritten,
        Action<AddComponentRow> ported
    ) {
        using var editor = EditorSession.Start();

        var one = Row(editor, handWritten);
        var two = Row(editor, ported);

        Assert.Equal(one, two);
        Assert.NotEqual("", two.Tree);
        Assert.NotEqual("", two.Flags);

        return two;
    }

    static (string Tree, string Flags) Row(EditorSession editor, Action<AddComponentRow> arrange) {
        var host = editor.Document.Root.Add("add-component-menu");
        var row = host.Add<AddComponentRow>();

        row.Focusable = false;
        arrange(row);

        editor.Frames(2);

        var written = (editor.Ui.Tree(host), editor.Ui.Flags(host));

        host.Remove();
        editor.Frames(2);

        return written;
    }

    static (string Tree, string Flags) Dump(EditorSession editor, AddComponentMenu picker) =>
        (editor.Ui.Tree(picker), editor.Ui.Flags(picker));

    static IReadOnlyList<string> Checked(string flags) =>
        [.. flags.Split('\n').Where(line => line.Contains("State=Checked", StringComparison.Ordinal))];

    /// <summary>Every row label the flags dump named, which is what a parked row would keep.</summary>
    static IReadOnlyList<string> Labels(string flags) {
        List<string> found = [];

        foreach (var line in flags.Split('\n')) {
            if (line.IndexOf("Label=\"", StringComparison.Ordinal) is var at and >= 0) {
                found.Add(line[(at + 7)..].Split('"')[0]);
            }
        }

        return found;
    }

    // ── The editor around it ─────────────────────────────────────────────────

    static EditorSession Selected() {
        var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Open("inspector");
        editor.Frames(2);

        return editor;
    }

    static AddComponentMenu Open(EditorSession editor) {
        Descendants(editor.Panel("inspector"))
            .OfType<ButtonBase>()
            .First(button => button.Label == "Add Component")
            .Activate();

        editor.Settle();

        return Descendants(editor.Document.Root)
            .OfType<AddComponentMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component picker did not open");
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var deeper in Descendants(child)) {
                yield return deeper;
            }
        }
    }
}
