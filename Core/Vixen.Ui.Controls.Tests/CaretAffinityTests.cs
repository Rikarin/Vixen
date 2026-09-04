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
    const string Latin = "AB";

    /// <summary>ALEF then TEH. Two letters, two glyphs, and no joining between them.</summary>
    const string Arabic = "ات";

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

        var block = FieldProbe.Block(field);

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

    [Fact]
    public void A_click_at_the_start_of_a_wrapped_row_leaves_the_caret_on_that_row() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = FieldProbe.Block(field);

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

        var block = FieldProbe.Block(field);

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
        Assert.Equal(block.TopOf(row + 1), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Up_from_a_clicked_continuation_row_goes_one_row_and_not_two() {
        var (fixture, field, row, boundary) = Wrapped(notBefore: 2);
        using var owned = fixture;

        var block = FieldProbe.Block(field);

        field.MoveCaret(boundary, CaretAffinity.Downstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Up);

        // The other half of the same mistake, and the more visible one: reading the row from the
        // index puts the caret's origin a row too high, so Up skips a line.
        Assert.Equal(block.TopOf(row - 1), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Right_off_the_end_of_a_wrapped_row_arrives_on_the_row_below() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = FieldProbe.Block(field);

        // Starting one grapheme back, strictly inside the row above, where the two readings of the
        // index are the same pixel and the fixture cannot be accused of pre-loading the answer.
        field.MoveCaret(boundary - 1, CaretAffinity.Upstream);
        fixture.Document.Focus(field);

        Assert.Equal(block.TopOf(row - 1), block.CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);

        fixture.Type(InputKey.Right);

        // ⚠ **The row is the assertion and the index cannot be.** Right lands on the one number that
        // ends the row above and begins the row below; a step that only said *where* left the caret
        // on the row it came from, so the reader saw it stall at the right margin for a keypress and
        // then reappear a character into the next row.
        Assert.Equal(boundary, field.CaretIndex);
        Assert.Equal(block.TopOf(row), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Left_back_onto_a_wrap_stays_on_the_row_it_walked_back_across() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = FieldProbe.Block(field);

        field.MoveCaret(boundary + 1, CaretAffinity.Upstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Left);

        // The mirror of the one above: the character Left crossed is on the lower row, so that is
        // where the caret belongs. Both directions used to answer with the row above.
        Assert.Equal(boundary, field.CaretIndex);
        Assert.Equal(block.TopOf(row), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    [Fact]
    public void Home_on_a_wrapped_row_goes_to_the_head_of_that_row_and_not_the_tail_of_the_one_above() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = FieldProbe.Block(field);

        field.MoveCaret(boundary + 1, CaretAffinity.Upstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Home);

        // ⚠ Home lands on the same index either way, and the two readings are a whole row apart —
        // which is why an index-only Home appeared to move the caret *up* a line.
        Assert.Equal(boundary, field.CaretIndex);
        Assert.Equal(block.TopOf(row), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
        Assert.Equal(block.TopOf(row - 1), block.CaretAt(boundary, CaretAffinity.Upstream).Y, 0.01f);
    }

    [Fact]
    public void End_on_the_row_above_a_wrap_stays_on_that_row() {
        var (fixture, field, row, boundary) = Wrapped();
        using var owned = fixture;

        var block = FieldProbe.Block(field);

        field.MoveCaret(boundary - 1, CaretAffinity.Upstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.End);

        // The half that was already right, pinned so that giving Home its answer cannot quietly
        // take End's away: the tail of a wrapped row is the upstream reading of the same number.
        Assert.Equal(boundary, field.CaretIndex);
        Assert.Equal(block.TopOf(row - 1), FieldProbe.Block(field).CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }

    /// <summary>A one-line field holding a Latin run and an Arabic one, which face opposite ways.</summary>
    /// <remarks>
    ///     ⚠ The fallback face is registered on the fixture's own document rather than in
    ///     <see cref="ControlFixture" />, because every other test in this project wants a line with
    ///     one direction in it and a second face registered globally would change what they measure.
    /// </remarks>
    static (ControlFixture Fixture, TextBox Field) Bidi() {
        var fixture = new ControlFixture(css: "textbox { width: 400px; }");
        fixture.Document.Fonts.AddFallback(FieldProbe.Aran);

        var field = fixture.Add<TextBox>();
        field.Value = Latin + Arabic;
        fixture.Update();

        var line = FieldProbe.Block(field).Lines[0];

        // ⚠ Asserted before anything is measured, for the reason `Vixen.Ui.Tests` gives: a fixture
        // that produced one run, or two runs at the same level, would pass every assertion below by
        // making the two affinities the same number.
        Assert.Equal(2, line.Runs.Length);
        Assert.NotEqual(line.Runs[0].Level % 2, line.Runs[1].Level % 2);

        return (fixture, field);
    }

    [Fact]
    public void Left_across_a_direction_boundary_lands_beside_the_letter_it_just_crossed() {
        var (fixture, field) = Bidi();
        using var owned = fixture;

        var line = FieldProbe.Block(field).Lines[0];

        // Between the two Arabic letters, which is inside one run and therefore unambiguous.
        field.MoveCaret(Latin.Length + 1, CaretAffinity.Downstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Left);

        Assert.Equal(Latin.Length, field.CaretIndex);

        // ⚠ **The two readings of this one index are a whole run apart, so the assertion has to name
        // a place.** Left crossed the first Arabic letter, so the caret leads it — at the far end of
        // the Arabic run. Read upstream instead and the caret is drawn back where the Latin ends,
        // which is the jump this rule is written against.
        var landed = FieldProbe.Block(field).Lines[0].CaretOffset(field.CaretIndex, field.CaretAffinity);

        Assert.Equal(line.CaretOffset(Latin.Length, CaretAffinity.Downstream), landed, 0.01f);

        Assert.True(
            landed - line.CaretOffset(Latin.Length, CaretAffinity.Upstream) > 1f,
            "the two readings are a whole run apart, so this is a claim about the side and not a rounding"
        );
    }

    [Fact]
    public void Right_across_a_direction_boundary_stays_beside_the_letter_it_just_crossed() {
        var (fixture, field) = Bidi();
        using var owned = fixture;

        var line = FieldProbe.Block(field).Lines[0];

        field.MoveCaret(Latin.Length - 1, CaretAffinity.Upstream);
        fixture.Document.Focus(field);

        fixture.Type(InputKey.Right);

        Assert.Equal(Latin.Length, field.CaretIndex);

        // The half the old code got right by accident, pinned: Right crossed the last Latin letter,
        // so the caret trails it and stays at the end of the Latin run. Giving Left its answer must
        // not be done by giving both directions the same one.
        Assert.Equal(
            line.CaretOffset(Latin.Length, CaretAffinity.Upstream),
            FieldProbe.Block(field).Lines[0].CaretOffset(field.CaretIndex, field.CaretAffinity),
            0.01f
        );
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

        var block = FieldProbe.Block(field);

        Assert.Equal(block.CaretAt(boundary).Y, block.CaretAt(field.CaretIndex, field.CaretAffinity).Y, 0.01f);
    }
}
