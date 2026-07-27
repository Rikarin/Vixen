// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>What the generated conformance cases call.</summary>
/// <remarks>
///     <para>
///         The suite is written in <i>code points</i> and the implementation works in UTF-16, so
///         somewhere the two have to be reconciled. Doing it here, once, keeps the generated files
///         free of anything that is a Vixen decision — every one of the 2 710 cases is the
///         Consortium's data and nothing else.
///     </para>
///     <para>
///         The failure message prints the expected and actual boundaries as code-point offsets
///         rather than UTF-16 indices, because that is what the case's own comment is written in and
///         a failure is read against the comment.
///     </para>
/// </remarks>
static class UnicodeConformance {
    /// <summary>Checks a grapheme cluster case.</summary>
    /// <param name="codePoints">The case's code points.</param>
    /// <param name="expected">The boundaries, as code point offsets.</param>
    public static void AssertGraphemes(int[] codePoints, int[] expected) {
        var (text, offsets) = Encode(codePoints);
        var boundaries = new List<int>();

        GraphemeBreaker.Collect(text, boundaries);

        Assert.Equal(expected, ToCodePointOffsets(boundaries, offsets));
    }

    /// <summary>Checks a word boundary case.</summary>
    /// <param name="codePoints">The case's code points.</param>
    /// <param name="expected">The boundaries, as code point offsets.</param>
    public static void AssertWords(int[] codePoints, int[] expected) {
        var (text, offsets) = Encode(codePoints);
        var boundaries = new List<int>();

        WordBreaker.Collect(text, boundaries);

        Assert.Equal(expected, ToCodePointOffsets(boundaries, offsets));
    }

    /// <summary>Encodes code points to UTF-16, remembering where each one started.</summary>
    static (string Text, Dictionary<int, int> Offsets) Encode(int[] codePoints) {
        var builder = new StringBuilder();
        var offsets = new Dictionary<int, int>();

        for (var i = 0; i < codePoints.Length; i++) {
            offsets[builder.Length] = i;
            builder.Append(char.ConvertFromUtf32(codePoints[i]));
        }

        offsets[builder.Length] = codePoints.Length;
        return (builder.ToString(), offsets);
    }

    static int[] ToCodePointOffsets(List<int> boundaries, Dictionary<int, int> offsets) {
        var result = new int[boundaries.Count];

        for (var i = 0; i < boundaries.Count; i++) {
            // A boundary that is not at a code point start would be a bug in the breaker rather
            // than a disagreement about where clusters end, so it is worth saying so distinctly.
            Assert.True(
                offsets.ContainsKey(boundaries[i]),
                $"boundary {boundaries[i]} falls inside a surrogate pair"
            );

            result[i] = offsets[boundaries[i]];
        }

        return result;
    }
}
