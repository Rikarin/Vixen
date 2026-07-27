// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The measure function Yoga's intrinsic-size fixtures were generated against, ported so that
///     those fixtures mean here what they mean there.
/// </summary>
/// <remarks>
///     <para>
///         It is a stand-in for a text engine, not one: ten points per character, ten per line,
///         breaking on spaces. What matters is that it answers the three measure modes the way a
///         real text measurer does — its longest word under a tight bound, one line under none —
///         because that is the behaviour the fixtures are probing for.
///     </para>
///     <para>
///         Ported line for line rather than reimplemented, including the parts that look odd. A
///         word wider than the line counts as two lines and resets the run; a line that ends
///         exactly full is not counted twice. Those are not principles, they are what the fixture
///         numbers were produced with, and an approximation of them makes several fixtures disagree
///         for reasons that have nothing to do with flexbox — which is exactly what happened on the
///         first run of this suite.
///     </para>
/// </remarks>
static class ConformanceMeasure {
    const float WidthPerCharacter = 10f;
    const float HeightPerLine = 10f;

    public static LayoutSize IntrinsicSize(in MeasureRequest request) {
        var text = request.Context as string ?? string.Empty;

        var measuredWidth = request.WidthMode switch {
            MeasureMode.Exactly => request.AvailableWidth,
            MeasureMode.AtMost => MathF.Min(text.Length * WidthPerCharacter, request.AvailableWidth),
            _ => text.Length * WidthPerCharacter
        };

        if (request.HeightMode == MeasureMode.Exactly) {
            return new LayoutSize(measuredWidth, request.AvailableHeight);
        }

        // A row cannot wrap below its longest word, so that is the floor the height is computed
        // against. A column wraps to whatever width it was given.
        var isColumn = request.Tree.GetStyle(request.Node).FlexDirection == FlexDirection.Column;
        var widthForHeight = isColumn ? measuredWidth : MathF.Max(LongestWordWidth(text), measuredWidth);

        var height = CalculateHeight(text, widthForHeight);
        return new LayoutSize(
            measuredWidth,
            request.HeightMode == MeasureMode.AtMost ? MathF.Min(height, request.AvailableHeight) : height
        );
    }

    static float LongestWordWidth(string text) {
        var longest = 0;
        var current = 0;

        foreach (var character in text) {
            if (character == ' ') {
                longest = int.Max(current, longest);
                current = 0;
            } else {
                current++;
            }
        }

        return int.Max(current, longest) * WidthPerCharacter;
    }

    static float CalculateHeight(string text, float measuredWidth) {
        if (text.Length * WidthPerCharacter <= measuredWidth) {
            return HeightPerLine;
        }

        var lines = 1f;
        var currentLineLength = 0f;

        foreach (var word in text.Split(' ')) {
            var wordWidth = word.Length * WidthPerCharacter;

            if (wordWidth > measuredWidth) {
                if (currentLineLength > 0f) {
                    lines++;
                }

                lines++;
                currentLineLength = 0f;
            } else if (currentLineLength + wordWidth <= measuredWidth) {
                currentLineLength += wordWidth + WidthPerCharacter;
            } else {
                lines++;
                currentLineLength = wordWidth + WidthPerCharacter;
            }
        }

        return (currentLineLength == 0f ? lines - 1f : lines) * HeightPerLine;
    }
}
