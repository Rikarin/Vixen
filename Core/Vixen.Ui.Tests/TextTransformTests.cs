// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>text-transform</c> as the element shapes, measures, wraps and hit-tests it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The caret is why this feature waited, and it is the half of it worth reading.</b> A
///         full Unicode case mapping changes the string's UTF-16 <i>length</i> — <c>straße</c>
///         uppercases to <c>STRASSE</c> — while <c>TextRun.Start</c>, <c>TextLine.Start</c>, every
///         caret offset and <c>TextField</c>'s selection are indices into the element's <i>own</i>
///         text. Shipping the four keywords without a map between the two puts the caret in the
///         wrong character of an editable field, silently, and only on the strings that expand.
///     </para>
///     <para>
///         So the round trip <c>CaretIndexAt(CaretOffset(i)) == i</c> is asserted the way
///         <c>text-indent</c>'s was, over a string where the two lengths differ — which is the only
///         arrangement in which a missing map is visible at all, and is why the fixture is
///         <c>straße</c> and not <c>hello</c>.
///     </para>
///     <para>
///         ⚠ <b>But the round trip alone does not catch a missing map, and finding that out is worth
///         more than the round trip.</b> It is satisfied by <i>any</i> pair of inverse functions, the
///         identity included — so with both directions of the map removed it stays green, which is
///         the predicate-that-cannot-be-false this repository keeps meeting. Measured by sabotage,
///         twice. With <i>both</i> directions removed, what goes red is
///         <see cref="The_caret_advances_once_per_character_the_author_wrote" />,
///         <see cref="The_lines_length_counts_what_was_written_and_the_runs_counts_what_is_drawn" />
///         and <see cref="A_wrapped_paragraph_reports_source_indices_on_every_line" /> — the three
///         that hold the map against something outside itself, the line's own width and the source
///         string's length. With <i>one</i> direction removed the two round trips go red as well. So
///         both kinds are needed and neither is redundant.
///     </para>
///     <para>
///         ⚠ <b>Open Sans rather than the Consortium fixture, and it is not a preference.</b>
///         <c>TestShapeLana</c> has no <c>ß</c> and no <c>ﬁ</c>, so every one of them shapes to
///         .notdef — and a case mapping asserted against a face that draws the character and its
///         replacement identically measures the same width whichever it applied. That is the shape
///         of green the <c>font-variant-numeric</c> sweep hit: the probe was fine and the font could
///         not witness the property.
///     </para>
/// </remarks>
public class TextTransformTests {
    const float Tolerance = 0.01f;
    static readonly FontFace Sans = Load("OpenSans-Regular.ttf", "OpenSans");

    static FontFace Load(string file, string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{file}")
            ?? throw new InvalidOperationException($"{file} is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    static UiDocument Documented(string label) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Sans);
        document.Load($"root {{ width: 400px; height: 300px; align-items: flex-start; }} label {{ {label} }}");

        return document;
    }

    static UiElement Labelled(UiDocument document, string text) {
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return element;
    }

    static string Drawn(UiElement element) {
        var block = element.Block()!;
        var text = string.Empty;

        foreach (var line in block.Lines) {
            foreach (var run in line.Runs) {
                text += run.Shaped.Text;
            }
        }

        return text;
    }

    // ── The keywords, one at a time ─────────────────────────────────────────────────────────
    // ⚠ Every keyword by hand rather than one for the family. The consumption gate scores a
    // property, so a family whose `uppercase` works and whose `capitalize` silently does not is a
    // green row — and `capitalize` is the one of the four that takes a different path through the
    // code, because it is the only one that asks where the words are.

    [Fact]
    public void Nothing_declared_leaves_the_characters_alone() {
        using var document = Documented("font-size: 16px;");

        Assert.Equal("Ag jq Wm", Drawn(Labelled(document, "Ag jq Wm")));
    }

    [Fact]
    public void Uppercase_reaches_the_shaper() {
        using var document = Documented("font-size: 16px; text-transform: uppercase;");

        Assert.Equal("AG JQ WM", Drawn(Labelled(document, "Ag jq Wm")));
    }

    [Fact]
    public void Lowercase_reaches_the_shaper() {
        using var document = Documented("font-size: 16px; text-transform: lowercase;");

        Assert.Equal("ag jq wm", Drawn(Labelled(document, "Ag jq Wm")));
    }

    [Fact]
    public void Capitalize_reaches_the_shaper() {
        using var document = Documented("font-size: 16px; text-transform: capitalize;");

        Assert.Equal("Ag Jq Wm", Drawn(Labelled(document, "Ag jq wm")));
    }

    [Fact]
    public void None_is_the_opt_out_and_not_merely_the_absence_of_a_declaration() {
        using var document = Documented("font-size: 16px; text-transform: none;");

        Assert.Equal("Ag jq Wm", Drawn(Labelled(document, "Ag jq Wm")));
    }

    /// <summary>
    ///     ⚠ <b>A shaping-time transform changes the measured width</b>, which is why it happens in
    ///     <c>Block</c> and not at paint: a capital is wider than its lowercase in every text face,
    ///     so a paragraph transformed after measuring wraps at the wrong characters.
    /// </summary>
    [Fact]
    public void The_transform_changes_what_the_element_measures() {
        using var plain = Documented("font-size: 16px;");
        using var loud = Documented("font-size: 16px; text-transform: uppercase;");

        var before = Labelled(plain, "ag jq wm il").Block()!.Width;
        var after = Labelled(loud, "ag jq wm il").Block()!.Width;

        Assert.True(after > before + 1f, $"uppercase is wider than lowercase: {after} vs {before}");
    }

    // ── The index map ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>Seven glyphs where the author wrote six characters.</b> This is the fixture the whole
    ///     file rests on: if the expansion ever stops arriving, every caret assertion below becomes
    ///     a test of the identity map and proves nothing.
    /// </summary>
    [Fact]
    public void A_case_mapping_that_expands_reaches_the_shaper_as_the_longer_string() {
        using var document = Documented("font-size: 16px; text-transform: uppercase;");

        Assert.Equal("STRASSE", Drawn(Labelled(document, "straße")));
    }

    /// <summary>The deliverable of doc 43's `text-transform` split, asserted as a round trip.</summary>
    [Fact]
    public void Every_source_index_round_trips_through_an_expanded_line() {
        using var document = Documented("font-size: 16px; text-transform: uppercase;");
        var line = Labelled(document, "straße").Block()!.Lines[0];

        for (var index = 0; index <= "straße".Length; index++) {
            var x = line.CaretOffset(index);
            Assert.Equal(index, line.CaretIndexAt(x));
        }
    }

    /// <summary>
    ///     ⚠ <b>And the caret walks forwards.</b> A round trip is satisfied by a map that is wrong
    ///     the same way in both directions — the failure a single inverse pair cannot see — so the
    ///     offsets are also asserted to be strictly increasing, which a constant or a collapsed map
    ///     is not.
    /// </summary>
    [Fact]
    public void The_caret_advances_once_per_character_the_author_wrote() {
        using var document = Documented("font-size: 16px; text-transform: uppercase;");
        var line = Labelled(document, "straße").Block()!.Lines[0];

        var previous = float.NegativeInfinity;

        for (var index = 0; index <= "straße".Length; index++) {
            var x = line.CaretOffset(index);
            Assert.True(x > previous, $"the caret moved backwards at {index}: {x} after {previous}");
            previous = x;
        }

        Assert.Equal(line.Width, previous, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The line reports the element's own indices and the run reports the shaped text's</b>,
    ///     and on an expanded string they are different numbers. A consumer handed the run's is a
    ///     selection that highlights one character too many.
    /// </summary>
    [Fact]
    public void The_lines_length_counts_what_was_written_and_the_runs_counts_what_is_drawn() {
        using var document = Documented("font-size: 16px; text-transform: uppercase;");
        var line = Labelled(document, "straße").Block()!.Lines[0];

        Assert.Equal(0, line.Start);
        Assert.Equal(6, line.Length);
        Assert.Equal(7, line.Runs[0].Shaped.Text.Length);
    }

    /// <summary>
    ///     A wrapped paragraph is where the two index spaces drift furthest apart: every line after
    ///     the expansion starts at a different number in each.
    /// </summary>
    [Fact]
    public void A_wrapped_paragraph_reports_source_indices_on_every_line() {
        using var document = Documented("width: 60px; font-size: 16px; text-transform: uppercase;");
        var source = "straße straße";
        var block = Labelled(document, source).Block()!;

        Assert.True(block.Lines.Length > 1, "the fixture has to wrap for this to say anything");

        var last = block.Lines[^1];
        Assert.Equal(source.Length, last.Start + last.Length);

        // Every line's own start is a character boundary of the *source*, so slicing the element's
        // text by it is well formed — which is what `TextField` does to draw a selection.
        foreach (var line in block.Lines) {
            Assert.InRange(line.Start, 0, source.Length);
            Assert.InRange(line.Start + line.Length, line.Start, source.Length);
        }
    }

    [Fact]
    public void A_caret_index_on_a_later_line_still_round_trips() {
        using var document = Documented("width: 60px; font-size: 16px; text-transform: uppercase;");
        var source = "straße straße";
        var block = Labelled(document, source).Block()!;

        for (var index = 0; index <= source.Length; index++) {
            var (x, y) = block.CaretAt(index);
            var landed = block.CaretIndexAt(x, y);

            // A break consumes its space, so the index of that space has two readings and lands on
            // whichever line the y chose. Everything else is exact.
            Assert.True(
                landed == index || source[Math.Min(index, source.Length - 1)] == ' ',
                $"index {index} came back as {landed}"
            );
        }
    }

    // ── The rest of the pipeline ────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The block cache does not key on the element's text alone.</b> Changing the transform
    ///     leaves <c>Text</c> the same instance, so a cache that compared only that would draw the
    ///     old case until something else invalidated it.
    /// </summary>
    [Fact]
    public void Changing_the_transform_rebuilds_the_block() {
        using var document = Documented("font-size: 16px;");
        var element = Labelled(document, "Ag jq Wm");

        Assert.Equal("Ag jq Wm", Drawn(element));

        document.Load(
            "root { width: 400px; height: 300px; align-items: flex-start; }"
            + " label { font-size: 16px; text-transform: uppercase; }"
        );

        document.Update();
        Assert.Equal("AG JQ WM", Drawn(element));
    }

    /// <summary>
    ///     ⚠ <b>The ellipsis cuts the drawn string, not the written one.</b> Cutting by a source
    ///     index into the transformed text takes the prefix a character short for every expansion
    ///     before the cut — a letter eaten by the marker that had room to be drawn.
    /// </summary>
    [Fact]
    public void An_ellipsis_on_transformed_text_keeps_the_glyphs_that_fit() {
        using var document = Documented(
            "width: 60px; font-size: 16px; white-space: nowrap; overflow: hidden;"
            + " text-overflow: ellipsis; text-transform: uppercase;"
        );

        var element = Labelled(document, "straße straße");
        var drawn = element.Ellipsized(60f)!;
        var line = drawn.Lines[0];

        var text = string.Empty;

        foreach (var run in line.Runs) {
            text += run.Shaped.Text;
        }

        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.StartsWith("STRAS", text, StringComparison.Ordinal);
        Assert.True(line.Width <= 60f + Tolerance, $"the truncated line still fits: {line.Width}");
    }

    /// <summary>
    ///     <c>text-transform</c> inherits, which is what makes it work on the text child a markup
    ///     interpolation emits rather than only where it is written.
    /// </summary>
    [Fact]
    public void It_inherits_to_the_child_that_holds_the_text() {
        using var document = Documented("font-size: 16px;");
        document.Load(
            "root { width: 400px; height: 300px; align-items: flex-start; text-transform: uppercase; }"
            + " label { font-size: 16px; }"
        );

        Assert.Equal("AG JQ WM", Drawn(Labelled(document, "Ag jq Wm")));
    }
}
