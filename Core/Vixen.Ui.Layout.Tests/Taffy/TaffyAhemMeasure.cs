// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>The text content of one <c>&lt;text&gt;</c> leaf, and the axis its inline direction runs along.</summary>
sealed record TaffyText(string Content, bool IsVertical);

/// <summary>
///     The measure function Taffy's <c>&lt;text&gt;</c> fixtures were generated against.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>"The Ahem stub" is a misnomer worth correcting: no font is involved, on either side.</b>
///         Ahem is a test font whose every glyph is exactly one em square, which is what let Taffy's
///         authors replace text measurement with arithmetic — the HTML fixtures are laid out by Chrome
///         using the real Ahem font at 10 px, and the Rust harness then reproduces those same numbers
///         with ten points per character and ten per line. So this is not an approximation standing in
///         for a text engine; it is the exact model the expected numbers were produced with, and
///         Vixen needing no font here is a property of the corpus rather than a compromise.
///     </para>
///     <para>
///         Ported line for line from <c>AhemTextMeasureData::measure</c>, including the parts that
///         look wrong. Words are separated by <b>zero-width space</b>, not by U+0020 — a run of ASCII
///         spaces is measured as characters like any other glyph, because in Ahem it is one. And the
///         line-breaking loop counts a word that does not fit onto the current line by starting a new
///         one <i>without</i> first checking whether it fits the new line either. Those are not
///         principles; they are what the numbers were produced with.
///     </para>
///     <para>
///         ⚠ Taffy asks in terms of <c>known_dimensions</c> plus an <c>AvailableSpace</c> of
///         min-content / max-content / definite, where Vixen asks in terms of a
///         <see cref="MeasureMode" /> per axis. The mapping is exact rather than approximate:
///         <see cref="MeasureMode.Exactly" /> is a known dimension, <see cref="MeasureMode.Undefined" />
///         is max-content, and <see cref="MeasureMode.AtMost" /> is definite — including
///         <c>AtMost 0</c>, which falls through Taffy's own <c>min(…).max(min_line_length)</c> and so
///         lands on exactly the min-content answer. That is the same equivalence the layout README
///         relies on for the missing min-content callback.
///     </para>
/// </remarks>
static class TaffyAhemMeasure {
    const char ZeroWidthSpace = '​';
    const float CharacterWidth = 10f;
    const float LineHeight = 10f;

    public static LayoutSize Measure(in MeasureRequest request) {
        if (request is { WidthMode: MeasureMode.Exactly, HeightMode: MeasureMode.Exactly }) {
            return new LayoutSize(request.AvailableWidth, request.AvailableHeight);
        }

        if (request.Context is not TaffyText text) {
            return new LayoutSize(
                request.WidthMode == MeasureMode.Exactly ? request.AvailableWidth : 0f,
                request.HeightMode == MeasureMode.Exactly ? request.AvailableHeight : 0f
            );
        }

        // Taffy works in flow-relative terms; `vertical-lr` swaps which physical axis is inline.
        var (inlineMode, inlineAvailable) = text.IsVertical
            ? (request.HeightMode, request.AvailableHeight)
            : (request.WidthMode, request.AvailableWidth);

        var (blockMode, blockAvailable) = text.IsVertical
            ? (request.WidthMode, request.AvailableWidth)
            : (request.HeightMode, request.AvailableHeight);

        var words = text.Content.Split(ZeroWidthSpace);
        var longestWord = words.Max(word => word.Length);
        var allWords = words.Sum(word => word.Length);

        var inlineSize = inlineMode switch {
            MeasureMode.Exactly => inlineAvailable,
            MeasureMode.AtMost => MathF.Max(MathF.Min(inlineAvailable, allWords * CharacterWidth), longestWord * CharacterWidth),
            _ => MathF.Max(allWords * CharacterWidth, longestWord * CharacterWidth)
        };

        var blockSize = blockMode == MeasureMode.Exactly ? blockAvailable : LineCount(words, inlineSize) * LineHeight;

        return text.IsVertical ? new LayoutSize(blockSize, inlineSize) : new LayoutSize(inlineSize, blockSize);
    }

    static float LineCount(string[] words, float inlineSize) {
        var charactersPerLine = (int)MathF.Floor(inlineSize / CharacterWidth);
        var lines = 1;
        var current = 0;

        foreach (var word in words) {
            if (current + word.Length > charactersPerLine) {
                if (current > 0) {
                    lines++;
                }

                current = word.Length;
            } else {
                current += word.Length;
            }
        }

        return lines;
    }
}
