// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>ab cd</c> in a box narrower than a letter is four line boxes under either <c>anywhere</c>
///     keyword, as Chrome 153 draws it (#249).
/// </summary>
/// <remarks>
///     ⚠ <b>The break that fell before the space made the space a line of its own</b> — five lines,
///     the third holding nothing drawn. <c>Oracle/narrow-anywhere.html</c> is the reading: four, for
///     both keywords under <c>normal</c>, <c>pre-line</c> and <c>pre-wrap</c>. The wrapper's own half
///     is <c>Vixen.Ui.Text.Tests.AnywhereSpaceLineTests</c>; this is the same case through a laid-out
///     label with the face Chrome was given, so the count is of line boxes and not of ranges.
/// </remarks>
public class AnywhereSpaceLineTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    [Theory]
    [InlineData("overflow-wrap: anywhere; white-space: normal;")]
    [InlineData("overflow-wrap: anywhere; white-space: pre-line;")]
    [InlineData("overflow-wrap: anywhere; white-space: pre-wrap;")]
    [InlineData("line-break: anywhere; white-space: normal;")]
    [InlineData("line-break: anywhere; white-space: pre-line;")]
    [InlineData("line-break: anywhere; white-space: pre-wrap;")]
    public void Ab_cd_in_a_box_narrower_than_a_letter_is_four_lines(string declarations) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root  { width: 800px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; line-height: 20px; width: 1px; min-width: 0px; {{declarations}} }
              """
        );

        var label = document.Root.Add("label");
        label.Text = "ab cd";
        document.Update();

        var lines = label.Block()!.Lines;

        Assert.True(lines.Length == 4, $"{lines.Length} lines");
        Assert.Equal(80f, label.Bounds.Height, 0.5f);
    }
}
