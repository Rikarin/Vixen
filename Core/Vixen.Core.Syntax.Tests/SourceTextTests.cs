// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Core.Syntax.Tests;

public class TextSpanTests {
    [Fact]
    public void End_is_exclusive() {
        var span = new TextSpan(3, 4);

        Assert.Equal(7, span.End);
        Assert.False(span.IsEmpty);
        Assert.True(span.Contains(3));
        Assert.True(span.Contains(6));
        Assert.False(span.Contains(7));
    }

    [Fact]
    public void Touching_spans_do_not_overlap() {
        Assert.False(new TextSpan(0, 5).OverlapsWith(new TextSpan(5, 5)));
        Assert.True(new TextSpan(0, 6).OverlapsWith(new TextSpan(5, 5)));
    }

    [Fact]
    public void Containment_is_inclusive_of_identical_bounds() {
        var outer = new TextSpan(0, 10);

        Assert.True(outer.Contains(outer));
        Assert.True(outer.Contains(new TextSpan(2, 3)));
        Assert.False(outer.Contains(new TextSpan(8, 5)));
    }

    [Fact]
    public void FromBounds_rejects_a_reversed_or_negative_range() {
        Assert.Equal(new TextSpan(2, 3), TextSpan.FromBounds(2, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSpan.FromBounds(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSpan.FromBounds(5, 2));
    }

    [Fact]
    public void Ordering_is_by_start_then_length() {
        Assert.True(new TextSpan(0, 5) < new TextSpan(1, 1));
        Assert.True(new TextSpan(0, 1) < new TextSpan(0, 2));
        Assert.True(new TextSpan(2, 2) >= new TextSpan(2, 2));
    }
}

public class SourceTextTests {
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void All_three_line_break_forms_split_into_two_lines(string text) {
        var source = SourceText.From(text);

        Assert.Equal(2, source.LineCount);
        Assert.Equal("a", source.GetLineText(0));
        Assert.Equal("b", source.GetLineText(1));

        // CRLF counts as one break, so "b" starts at 3 there and 2 otherwise.
        Assert.Equal(new LinePosition(1, 0), source.GetLinePosition(text.Length - 1));
    }

    [Fact]
    public void A_trailing_newline_yields_a_final_empty_line() {
        var text = SourceText.From("a\n");

        Assert.Equal(2, text.LineCount);
        Assert.Equal("", text.GetLineText(1));
    }

    [Fact]
    public void Offsets_map_to_zero_based_line_and_character() {
        var text = SourceText.From("one\ntwo\nthree");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0));
        Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4));
        Assert.Equal(new LinePosition(1, 2), text.GetLinePosition(6));
        Assert.Equal(new LinePosition(2, 4), text.GetLinePosition(12));
    }

    [Fact]
    public void Offsets_are_clamped_so_end_of_file_resolves() {
        var text = SourceText.From("ab");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(-5));
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2));
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(999));
    }

    [Fact]
    public void Line_text_excludes_the_break() {
        var text = SourceText.From("one\r\ntwo");

        Assert.Equal("one", text.GetLineText(0));
        Assert.Equal("two", text.GetLineText(1));
    }

    [Fact]
    public void An_out_of_range_line_throws() {
        var text = SourceText.From("one");

        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLineStart(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLineStart(1));
    }

    [Fact]
    public void A_span_maps_to_a_line_position_span() {
        var span = SourceText.From("one\ntwo").GetLinePositionSpan(TextSpan.FromBounds(1, 5));

        Assert.Equal(new LinePosition(0, 1), span.Start);
        Assert.Equal(new LinePosition(1, 1), span.End);
    }

    [Fact]
    public void A_null_string_is_treated_as_empty() {
        var text = SourceText.From(null!);

        Assert.Equal(0, text.Length);
        Assert.Equal(1, text.LineCount);
    }
}
