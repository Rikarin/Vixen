// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     Editing a snapshot and asking what changed — the foundation an incremental reparse and a
///     hot reload sit on.
/// </summary>
public class TextChangeTests {
    [Fact]
    public void An_edit_replaces_the_span() {
        var text = SourceText.From("hello world");

        Assert.Equal("hello there", text.WithChanges(new TextChange(new(6, 5), "there")).ToString());
    }

    [Fact]
    public void An_insertion_leaves_the_rest_alone() =>
        Assert.Equal("hello brave world", SourceText.From("hello world").WithChanges(TextChange.Insert(6, "brave ")).ToString());

    [Fact]
    public void A_deletion_closes_the_gap() =>
        Assert.Equal("hello", SourceText.From("hello world").WithChanges(TextChange.Delete(new(5, 6))).ToString());

    /// <summary>
    ///     Every span is against the original text, so a caller describing several edits does not
    ///     have to adjust for its own earlier ones.
    /// </summary>
    [Fact]
    public void Several_edits_are_all_in_the_old_texts_coordinates() {
        var text = SourceText.From("one two three");

        var edited = text.WithChanges(
            new TextChange(new(0, 3), "1"),
            new TextChange(new(8, 5), "3")
        );

        Assert.Equal("1 two 3", edited.ToString());
    }

    [Fact]
    public void No_edits_returns_the_same_snapshot() {
        var text = SourceText.From("unchanged");

        Assert.Same(text, text.WithChanges());
    }

    [Fact]
    public void The_line_index_is_rebuilt_for_the_edited_text() {
        var edited = SourceText.From("one\ntwo").WithChanges(TextChange.Insert(3, "\nmiddle"));

        Assert.Equal("one\nmiddle\ntwo", edited.ToString());
        Assert.Equal(3, edited.LineCount);
        Assert.Equal("middle", edited.GetLineText(1));
    }

    // --- Validation ----------------------------------------------------------

    /// <summary>
    ///     Overlapping edits have no single well-defined result, so they are rejected rather than
    ///     resolved by an arbitrary rule the caller cannot see.
    /// </summary>
    [Fact]
    public void Overlapping_or_unsorted_edits_are_rejected() {
        var text = SourceText.From("hello world");

        Assert.Throws<ArgumentException>(
            () => text.WithChanges(new TextChange(new(0, 6), "x"), new TextChange(new(3, 4), "y"))
        );

        Assert.Throws<ArgumentException>(
            () => text.WithChanges(new TextChange(new(6, 5), "b"), new TextChange(new(0, 5), "a"))
        );
    }

    [Fact]
    public void An_edit_outside_the_text_is_rejected() {
        var text = SourceText.From("short");

        Assert.Throws<ArgumentOutOfRangeException>(() => text.WithChanges(new TextChange(new(0, 99), "x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.WithChanges(TextChange.Insert(99, "x")));
    }

    [Fact]
    public void Two_edits_that_merely_touch_are_allowed() {
        var edited = SourceText.From("abcd").WithChanges(
            new TextChange(new(0, 2), "X"),
            new TextChange(new(2, 2), "Y")
        );

        Assert.Equal("XY", edited.ToString());
    }

    // --- Reporting what changed ----------------------------------------------

    [Fact]
    public void An_edited_text_reports_exactly_where_it_differs() {
        var original = SourceText.From("hello world");
        var edited = original.WithChanges(new TextChange(new(6, 5), "there"));

        var range = Assert.Single(edited.GetChangeRanges(original));
        Assert.Equal(new TextSpan(6, 5), range.Span);
        Assert.Equal(5, range.NewLength);
        Assert.Equal(0, range.Delta);
    }

    [Fact]
    public void A_growing_edit_reports_a_positive_delta() {
        var original = SourceText.From("a");
        var edited = original.WithChanges(new TextChange(new(0, 1), "abcd"));

        Assert.Equal(3, Assert.Single(edited.GetChangeRanges(original)).Delta);
    }

    [Fact]
    public void Comparing_a_text_with_itself_reports_nothing() {
        var text = SourceText.From("same");

        Assert.Empty(text.GetChangeRanges(text));
    }

    /// <summary>
    ///     Conservative on purpose. Being silently wrong about which region changed would let a
    ///     reparser trust a subtree the edit had actually invalidated.
    /// </summary>
    [Fact]
    public void An_unrelated_text_reports_the_whole_document_as_changed() {
        var unrelated = SourceText.From("some other document");
        var edited = SourceText.From("hello").WithChanges(TextChange.Insert(5, "!"));

        var range = Assert.Single(edited.GetChangeRanges(unrelated));
        Assert.Equal(new TextSpan(0, unrelated.Length), range.Span);
        Assert.Equal(edited.Length, range.NewLength);
    }

    /// <summary>
    ///     Only the immediate predecessor is tracked. Two edits back falls to the conservative
    ///     answer, which is correct rather than merely cheap.
    /// </summary>
    [Fact]
    public void A_grandparent_text_falls_back_to_the_whole_document() {
        var original = SourceText.From("aaaa");
        var once = original.WithChanges(TextChange.Insert(0, "b"));
        var twice = once.WithChanges(TextChange.Insert(0, "c"));

        // The immediate predecessor gives the exact edit: an insertion's span is empty.
        var exact = Assert.Single(twice.GetChangeRanges(once));
        Assert.Equal(new TextSpan(0, 0), exact.Span);
        Assert.Equal(1, exact.NewLength);

        // Two edits back is not tracked, so the whole document is reported.
        Assert.Equal(new TextSpan(0, original.Length), Assert.Single(twice.GetChangeRanges(original)).Span);
    }

    [Fact]
    public void A_change_describes_itself_readably() {
        Assert.Equal("[0..3) -> \"x\"", new TextChange(new(0, 3), "x").ToString());
        Assert.Equal("[0..3) -> (deleted)", TextChange.Delete(new(0, 3)).ToString());
        Assert.Equal("[0..3) -> 5", new TextChangeRange(new(0, 3), 5).ToString());
    }
}
