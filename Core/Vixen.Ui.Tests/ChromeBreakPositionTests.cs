// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Where Chrome breaks a paragraph of Open Sans at a real width, and where this engine does.
/// </summary>
/// <remarks>
///     <para>
///         <b>The oracle #257 says exists nowhere else: break positions at actual widths in an
///         actual font, against a real browser.</b> Parley's 2 048 Chrome-recorded rows carry a
///         seed rather than the text and were taken in faces this tree does not commit, which is
///         why every pass on that issue sized them as a font decision and a shaping-parity claim
///         before a break-position oracle. This file takes the route the same passes named as
///         cheaper and nobody took: a fixture of <i>our</i> strings in <i>our</i> face, served over
///         localhost to the desktop app's browser pane, read through <c>Range.getClientRects</c>.
///     </para>
///     <para>
///         ⚠ <b>The face is the one <c>Vixen.Ui.Tests</c> already embeds, and Chrome shapes it to
///         the same advances.</b> Open Sans Regular at 16px: <c>ab cd</c> is 40.297 in Chrome and
///         40.29 here, <c>ab cd  </c> 48.609 against 48.60. So a row that disagrees is a
///         <i>breaking</i> disagreement and not a shaping one, which is the property the earlier
///         sizing said could not be had without Roboto or Arimo.
///     </para>
///     <para>
///         <b>Recorded 2026-09-22 in Chrome 152.0.7977.76</b>, <c>white-space: pre-wrap</c>,
///         <c>line-height: 20px</c>, from the fixture in the commit that added this file. Each row
///         is the UTF-16 index every line starts at. <see cref="Recorded" /> holds the rows the
///         two agree on — breaks at spaces, after hyphens, around punctuation, a run of spaces
///         hung at a soft wrap, a number with its separators — as equalities, so a row that goes
///         red is a break moved without an oracle and the Chrome side of it is the oracle.
///         <see cref="Departures" /> holds the seven rows they do not agree on, with both answers,
///         and every one of them is the one delta <c>ChromiumBreakDeltas.txt</c> already records
///         for <c>/</c>: a ledger that was a measurement of two-character strings now has a
///         sighting in a laid-out paragraph.
///     </para>
///     <para>
///         ⚠ <b>What this does not cover</b>: scripts the face does not have — CJK, Thai, Arabic —
///         which is where the tailorings live and where <c>CssLineBreakTailoringTests</c> and the
///         Consortium's suite already judge the breaker; and any width at which a single advance's
///         rounding decides the answer, which is why the widths are 60, 100, 150 and 220 and not
///         the boundaries of any word.
///     </para>
/// </remarks>
public class ChromeBreakPositionTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>This engine's line starts for a paragraph in a box of the given width.</summary>
    /// <remarks>
    ///     ⚠ <b>The width is on the label and not on the root, and the first version of this file
    ///     had it the other way and disagreed with Chrome on a paragraph the raw wrapper agreed
    ///     on.</b> The root is a flex container, a flex item's <c>min-width</c> is <c>auto</c>, and
    ///     <c>Zeilenumbruch</c> is 113 points wide — so a label in a 60-point root is 113 wide and
    ///     wraps at 113, which is CSS and not a defect. Chrome's fixture was a block of the width;
    ///     so is this one.
    /// </remarks>
    static int[] Starts(string text, float width) {
        var document = new UiDocument(900f, 600f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 900px; height: 600px; align-items: flex-start; }
              label { width: {{width}}px; font-family: Test; font-size: 16px; line-height: 20px; }
              """
        );

        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block.Lines.Select(line => line.Start).ToArray();
    }

    /// <summary>Chrome's line starts, one row per paragraph and width.</summary>
    public static TheoryData<string, float, int[]> Recorded => new() {
        { "the quick brown fox jumps over the lazy dog", 60f, [0, 4, 10, 16, 20, 26, 31, 40] },
        { "the quick brown fox jumps over the lazy dog", 100f, [0, 10, 20, 31] },
        { "the quick brown fox jumps over the lazy dog", 150f, [0, 20, 40] },
        { "the quick brown fox jumps over the lazy dog", 220f, [0, 26] },
        { "well-known state-of-the-art hyphen-ated words wrap here", 60f, [0, 5, 11, 17, 24, 28, 35, 40, 46, 51] },
        { "well-known state-of-the-art hyphen-ated words wrap here", 100f, [0, 11, 24, 35, 46] },
        { "well-known state-of-the-art hyphen-ated words wrap here", 150f, [0, 17, 35, 51] },
        { "well-known state-of-the-art hyphen-ated words wrap here", 220f, [0, 28, 51] },
        { "Zeilenumbruch mit längeren Wörtern und Umlauten ähnlich", 60f, [0, 14, 18, 27, 35, 39, 48] },
        { "Zeilenumbruch mit längeren Wörtern und Umlauten ähnlich", 100f, [0, 14, 27, 39, 48] },
        { "Zeilenumbruch mit längeren Wörtern und Umlauten ähnlich", 150f, [0, 18, 35, 48] },
        { "Zeilenumbruch mit längeren Wörtern und Umlauten ähnlich", 220f, [0, 27, 48] },
        { "a/b c/d paths/like/this and (parenthesised) text, punctuation; more.", 220f, [0, 28, 50] },
        { "one   two  three    four   ", 60f, [0, 6, 11, 20] },
        { "one   two  three    four   ", 100f, [0, 11] },
        { "one   two  three    four   ", 150f, [0, 20] },
        { "one   two  three    four   ", 220f, [0] },
        { "numbers 1,000.50 and 3-4 and 12/25 and 5% off", 60f, [0, 8, 17, 25, 29, 35, 42] },
        { "numbers 1,000.50 and 3-4 and 12/25 and 5% off", 100f, [0, 8, 21, 29, 39] },
        { "numbers 1,000.50 and 3-4 and 12/25 and 5% off", 150f, [0, 17, 35] },
        { "numbers 1,000.50 and 3-4 and 12/25 and 5% off", 220f, [0, 25] },
    };

    [Theory]
    [MemberData(nameof(Recorded))]
    public void Lines_start_where_chrome_starts_them(string text, float width, int[] expected) =>
        Assert.Equal(expected, Starts(text, width));

    /// <summary>The rows where Chrome and this engine part, with both answers.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every departure here is one delta, and the ledger predicted it.</b>
    ///         <c>ChromiumBreakDeltas.txt</c> records <c>/</c> followed by a letter as
    ///         <c>chromium=none uax14=break</c>, eighty rows of it, and says in its header that the
    ///         Chromium side is a transcription of Parley's <c>break_overrides.rs</c> rather than
    ///         anything this engine applies. So <c>paths/like/this</c> is one unbreakable word in
    ///         Chrome and three in Vixen, and <c>https://example.org/path?</c> is one line in Chrome
    ///         at every width below its 194 points and breaks after each slash here. This is that
    ///         ledger's first sighting in a laid-out paragraph rather than in a two-character
    ///         string: the ledger is a measurement, and these are what it measures.
    ///     </para>
    ///     <para>
    ///         The Vixen column is what the test asserts and the Chrome column is what it asserts
    ///         it differs from, so a row cannot rot in either direction: applying the overrides
    ///         turns the first assertion red and the row moves up into <see cref="Recorded" />;
    ///         a Chrome that stopped disagreeing would need re-recording, which the second
    ///         assertion is there to demand.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, float, int[], int[]> Departures => new() {
        { "a/b c/d paths/like/this and (parenthesised) text, punctuation; more.", 60f, [0, 8, 24, 28, 44, 50, 63], [0, 8, 14, 24, 28, 44, 50, 63] },
        { "a/b c/d paths/like/this and (parenthesised) text, punctuation; more.", 100f, [0, 8, 24, 28, 44, 50, 63], [0, 8, 19, 28, 44, 50, 63] },
        { "a/b c/d paths/like/this and (parenthesised) text, punctuation; more.", 150f, [0, 8, 28, 44, 63], [0, 19, 28, 44, 63] },
        { "email me@example.com or visit https://example.org/path?q=1 today", 60f, [0, 6, 21, 30, 55, 59], [0, 6, 21, 30, 38, 50, 55, 59] },
        { "email me@example.com or visit https://example.org/path?q=1 today", 100f, [0, 6, 21, 30, 55], [0, 6, 21, 30, 38, 50, 59] },
        { "email me@example.com or visit https://example.org/path?q=1 today", 150f, [0, 6, 21, 30, 55], [0, 6, 21, 38, 55] },
        { "email me@example.com or visit https://example.org/path?q=1 today", 220f, [0, 24, 30, 55], [0, 24, 50] },
    };

    [Theory]
    [MemberData(nameof(Departures))]
    public void Lines_depart_from_chrome_only_where_the_ledger_says(string text, float width, int[] chrome, int[] vixen) {
        Assert.NotEqual(chrome, vixen);
        Assert.Equal(vixen, Starts(text, width));

        // The departure is the slash and nothing else: the first line Vixen starts that Chrome
        // does not starts after a `/`. Only the first, because once the two disagree about one
        // break every line after it fills differently, and a later Vixen-only start may well
        // follow a space.
        var first = vixen.Except(chrome).First();
        Assert.Equal('/', text[first - 1]);
    }
}
