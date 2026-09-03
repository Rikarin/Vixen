// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a field does with the side of the caret, rather than only its index.</summary>
/// <remarks>
///     <para>
///         <b>The soft wrap is the fixture, and it has to be a soft one.</b> A hard newline consumes
///         itself, so the next line starts <i>after</i> the boundary index and there is only one
///         place the caret can be — the affinity is a genuine no-op there. Only a line broken for
///         width leaves the next line starting <i>at</i> the index that ended the last one, which is
///         the one number that names two rows.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is on the caret's drawn <c>y</c> or on a round trip, never on
///         <c>CaretIndex</c> alone.</b> That is the whole point: the index is the thing that cannot
///         tell the two rows apart, so a test that read it would pass against the bug.
///     </para>
/// </remarks>
public class CaretAffinityTests {
    /// <summary>A wrapped area, and a break in it at or below <paramref name="notBefore" />.</summary>
    /// <param name="notBefore">
    ///     ⚠ The lowest row the returned break may head. It exists because the <c>Up</c> test needs a
    ///     row with <i>two</i> rows above it: from row 1 the wrong answer is row −1, which
    ///     <see cref="TextField" /> clamps to the start of the text — landing on row 0, which is also
    ///     the right answer. A sabotage proved that: it reddened the <c>Down</c> test and left the
    ///     <c>Up</c> one green, because the clamp was covering for the bug.
    /// </param>
    static (ControlFixture Fixture, TextArea Field, int Row, int Boundary) Wrapped(int notBefore = 1) {
        var fixture = new ControlFixture(css: "textarea { width: 60px; height: 200px; }");

        var field = fixture.Add<TextArea>();
        field.Value = "aa bb cc dd ee ff gg hh ii jj kk ll";
        fixture.Update();

        var block = Block(field);

        Assert.True(block.Lines.Length > notBefore + 1, "the fixture has to wrap far enough or it tests nothing");

        // A break the next line actually starts at. A wrap that ate its space is not one of these,
        // and asserting against it would be asserting that nothing happens.
        var found = Enumerable.Range(notBefore, block.Lines.Length - 1 - notBefore)
            .Where(line => block.Lines[line].Start == block.Lines[line - 1].Start + block.Lines[line - 1].Length)
            .Select(line => (Row: line, Boundary: block.Lines[line].Start))
            .ToList();

        Assert.True(found.Count > 0, $"no break at or below row {notBefore} leaves its line starting at the boundary");

        return (fixture, field, found[0].Row, found[0].Boundary);
    }

    /// <summary>The laid-out text of the field's own text part.</summary>
    /// <remarks>
    ///     Walked rather than reached for: <c>TextField.text</c> is private, and the block is on the
    ///     part rather than on the field, which is a control's normal shape.
    /// </remarks>
    static TextLayout Block(TextField field) {
        foreach (var child in Walk(field)) {
            if (child.Block() is { } block) {
                return block;
            }
        }

        throw new InvalidOperationException("the field laid out no text");
    }

    static IEnumerable<UiElement> Walk(UiElement from) {
        foreach (var child in from.Children) {
            yield return child;

            foreach (var deeper in Walk(child)) {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void A_click_at_the_start_of_a_wrapped_row_leaves_the_caret_on_that_row() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = Block(field);

        // ⚠ **The caret must be drawn on the row that was clicked.** The index at the start of a
        // continuation row also ends the row above, so a field that kept only the index draws the
        // caret one row up — a caret that jumps when you click at the start of a wrapped line.
        field.MoveCaret(boundary, CaretAffinity.Downstream);

        Assert.Equal(block.TopOf(row), block.CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);

        // And the other reading of the same number is the row above, which is what makes this a
        // claim about the affinity rather than about the index.
        field.MoveCaret(boundary, CaretAffinity.Upstream);

        Assert.Equal(block.TopOf(row - 1), block.CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Down_from_a_clicked_continuation_row_leaves_the_row_it_was_clicked_on() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = Block(field);

        Assert.True(row + 1 < block.Lines.Length, "the fixture needs a row below the one clicked");

        // ⚠ **The caret has to start DOWNSTREAM for this to test anything, and that is the whole
        // finding.** An earlier version of this test started upstream and a sabotage that reverted
        // `Vertically` to reading the row from the index alone stayed green — because the index-only
        // reading *is* the upstream one, so the two agreed and the assertion could not fail. Starting
        // where a click at the head of a wrapped row puts the caret is what separates them.
        field.MoveCaret(boundary, CaretAffinity.Downstream);
        fixture.Document.Focus(field);

        var before = block.CaretAt(field.CaretIndex, field.CaretAffinity);

        Assert.Equal(block.TopOf(row), before.Y, 0.01f);

        fixture.Type(InputKey.Down);

        // Down goes to the row below the one the caret was on. Read from the index instead, the row
        // it is "on" is the one above, so Down lands on the row it already occupied — a Down key that
        // visibly does nothing, which is the defect this is written against.
        Assert.Equal(block.TopOf(row + 1), Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Up_from_a_clicked_continuation_row_goes_one_row_and_not_two() {
        var (fixture, field, row, boundary) = Wrapped(notBefore: 2);
        using var owned = fixture;

        var block = Block(field);

        field.MoveCaret(boundary, CaretAffinity.Downstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Up);

        // The other half of the same mistake, and the more visible one: reading the row from the
        // index puts the caret's origin a row too high, so Up skips a line.
        Assert.Equal(block.TopOf(row - 1), Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void The_resting_affinity_is_what_every_index_only_caret_answer_already_meant() {
        var (fixture, field, _, boundary) = Wrapped();
        using var owned = fixture;

        // ⚠ The compatibility claim the whole change rests on: a field that has never been clicked
        // draws its caret exactly where it did before the affinity existed, and `MoveCaret(index)`
        // without one keeps that. Defaulting downstream would move every caret on a boundary the
        // first time its field was shown.
        Assert.Equal(CaretAffinity.Upstream, new TextArea().CaretAffinity);

        field.MoveCaret(boundary);
        Assert.Equal(CaretAffinity.Upstream, field.CaretAffinity);

        var block = Block(field);

        Assert.Equal(block.CaretAt(boundary).Y, block.CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }
}
