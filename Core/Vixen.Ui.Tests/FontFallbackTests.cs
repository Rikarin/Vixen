// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Per-character fallback, and the multi-run line it needs.</summary>
/// <remarks>
///     <para>
///         <b>The fixture is two fonts with almost no overlap</b>, which is what makes these tests
///         possible at all: <c>TestShapeLana</c> has Latin and Tai Tham and no Kannada,
///         <c>NotoSerifKannada</c> has Kannada and no Latin, and the only character both draw is the
///         space. A string mixing them therefore has exactly one correct split, and an implementation
///         that ignored coverage would draw half of it as <c>.notdef</c> — which is visible here as a
///         run count rather than as a screenshot nobody looks at.
///     </para>
///     <para>
///         ⚠ The fonts' coverage is asserted rather than assumed. A subsetted replacement would
///         otherwise turn every test below green by making the split unnecessary.
///     </para>
///     <para>
///         Verified by sabotage, nine of nine landing: covering per code point instead of per cluster
///         fails 1, not merging adjacent clusters on the same face fails 9, putting
///         <c>Default</c> behind the fallbacks fails 3, dropping <c>FontRegistry.Revision</c> from the
///         line cache fails 1, emitting one draw command for a mixed line fails 1, forgetting a run's
///         pen in the caret arithmetic fails 1, taking the tallest run's height instead of both sides
///         separately fails 1, taking the <i>last</i> covering face rather than the first fails 1, and
///         reading a surrogate pair as two characters fails 1.
///     </para>
///     <para>
///         ⚠ <b>Two of those needed the tests changed to see them.</b> The last one had no fixture at
///         all until Zycon was linked in — with only Latin and Kannada faces, both readings of a
///         surrogate pair send the character to the head of the chain and agree. And the
///         default-versus-fallback ordering test was passing against "AB", where the fallback has no
///         Latin and coverage decides the answer whatever the order was; it needed a character
///         <i>both</i> faces have, which is the space.
///     </para>
/// </remarks>
public class FontFallbackTests {
    const string Latin = "AB";

    /// <summary>KA and the vowel sign AA — two code points, two glyphs, one Kannada syllable.</summary>
    const string Kannada = "ಕಾ";

    /// <summary>U+1F98E LIZARD, the astral character Zycon draws and the other two do not.</summary>
    const string Astral = "🦎";

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");
    static readonly FontFace Serif = LoadFont("NotoSerifKannada-Regular.ttf", "kannada");
    static readonly FontFace Emoji = LoadFont("Zycon.ttf", "zycon");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>A document whose declared family is Latin-only and whose fallback is Kannada-only.</summary>
    static UiDocument Documented() {
        var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Test", Lana);
        document.Fonts.AddFallback(Serif);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; } label { font-family: Test; }");

        return document;
    }

    static UiElement Labelled(UiDocument document, string text) {
        var label = document.Root.Add("label");
        label.Text = text;
        document.Update();

        return label;
    }

    [Fact]
    public void The_two_faces_have_the_disjoint_coverage_every_test_here_assumes() {
        Assert.True(Lana.Supports('A'));
        Assert.False(Serif.Supports('A'));

        Assert.True(Serif.Supports('ಕ'));
        Assert.False(Lana.Supports('ಕ'));

        // The one they share, which is what makes the merging test below mean something.
        Assert.True(Lana.Supports(' '));
        Assert.True(Serif.Supports(' '));

        // And the astral character exactly one of the three has.
        var lizard = char.ConvertToUtf32(Astral, 0);

        Assert.True(Emoji.Supports(lizard));
        Assert.False(Lana.Supports(lizard));
        Assert.False(Serif.Supports(lizard));
    }

    [Fact]
    public void An_astral_character_is_one_code_point_and_finds_the_face_that_has_it() {
        var document = new UiDocument(400f, 200f);
        using var owned = document;

        document.Fonts.Register("Test", Lana);
        document.Fonts.AddFallback(Emoji);
        document.Load("root { width: 400px; height: 200px; } label { font-family: Test; }");

        var line = Labelled(document, "A" + Astral + "A").Block()!.Lines[0];

        // ⚠ **The surrogate pair is the test.** Reading the string a `char` at a time asks the font
        // about U+D83E and U+DD8E, and no font has either — so the lizard would be "covered by
        // nothing", stay with the head of the chain, and draw a tofu from a font that has the real
        // glyph two places further along. It looks exactly like a missing font.
        Assert.Equal([Lana, Emoji, Lana], line.Runs.Select(run => run.Font));
        Assert.Equal(Astral, line.Runs[1].Shaped.Text);
    }

    [Fact]
    public void A_string_one_face_covers_is_one_run() {
        using var document = Documented();

        var line = Labelled(document, Latin).Block()!.Lines[0];

        Assert.Same(Lana, Assert.Single(line.Runs).Font);
        Assert.Equal(0, line.Runs[0].Start);
    }

    [Fact]
    public void A_character_the_declared_face_cannot_draw_goes_to_the_fallback() {
        using var document = Documented();

        var line = Labelled(document, Latin + Kannada).Block()!.Lines[0];

        Assert.Equal(2, line.Runs.Length);
        Assert.Same(Lana, line.Runs[0].Font);
        Assert.Same(Serif, line.Runs[1].Font);

        // The runs tile the text, which is the property everything downstream depends on: a caret
        // index has to land in exactly one of them, and a gap or an overlap is a caret that jumps.
        Assert.Equal(0, line.Runs[0].Start);
        Assert.Equal(Latin.Length, line.Runs[0].Shaped.Text.Length);
        Assert.Equal(Latin.Length, line.Runs[1].Start);
        Assert.Equal(Kannada.Length, line.Runs[1].Shaped.Text.Length);
    }

    [Fact]
    public void It_switches_back_again_rather_than_staying_where_it_landed() {
        using var document = Documented();

        var line = Labelled(document, Kannada + Latin + Kannada).Block()!.Lines[0];

        Assert.Equal([Serif, Lana, Serif], line.Runs.Select(run => run.Font));
    }

    [Fact]
    public void A_character_both_faces_have_goes_to_the_earlier_one_and_merges() {
        using var document = Documented();

        // ⚠ The space is the test, and the Kannada on the end is what makes it one: without it the
        // whole string is covered by the first face and never reaches the per-cluster walk at all.
        // Both fonts draw a space, so it goes to the earlier face and then merges with the Latin
        // before it — three runs here would unjoin "AB" from "CD", which is a kerning pair and a
        // shaping context lost to a font decision that changed nothing visible.
        var line = Labelled(document, "AB CD" + Kannada).Block()!.Lines[0];

        Assert.Equal(2, line.Runs.Length);
        Assert.Same(Lana, line.Runs[0].Font);
        Assert.Equal("AB CD", line.Runs[0].Shaped.Text);
    }

    [Fact]
    public void A_cluster_no_face_covers_whole_stays_with_the_first_and_is_not_split() {
        using var document = Documented();

        // 'A' is Lana's and the Kannada vowel sign is the serif's, and they are one grapheme cluster
        // — a spacing mark attaches to what precedes it. Neither face covers the pair, so the whole
        // cluster goes to the head of the chain and draws a tofu for the mark.
        //
        // ⚠ **A per-code-point implementation splits this into two runs and looks more correct**:
        // each half lands in a font that has it. What it actually does is shape a combining mark
        // alone, in a different font, at a pen position derived from a different em — an accent
        // floating somewhere near a letter. One visible tofu is the better failure.
        var line = Labelled(document, "Aಾ").Block()!.Lines[0];

        Assert.Same(Lana, Assert.Single(line.Runs).Font);
    }

    [Fact]
    public void A_line_is_as_wide_as_its_runs_laid_end_to_end() {
        using var document = Documented();

        var mixed = Labelled(document, Latin + Kannada).Block()!.Lines[0];

        Assert.Equal(2, mixed.Runs.Length);
        Assert.Equal(mixed.Runs[0].Width + mixed.Runs[1].Width, mixed.Width, 0.01f);

        // And each run knows where it starts, which is what the draw path uses instead of re-adding
        // the widths itself.
        Assert.Equal(0f, mixed.PenOf(0));
        Assert.Equal(mixed.Runs[0].Width, mixed.PenOf(1), 0.01f);
    }

    [Fact]
    public void The_line_takes_the_deepest_ascender_and_the_deepest_descender() {
        using var document = Documented();

        var mixed = Labelled(document, Latin + Kannada).Block()!.Lines[0];
        var runs = mixed.Runs;

        // ⚠ Both sides separately, rather than the taller run's height whole. The runs share a
        // baseline, so the line needs room for the tallest ascender *and* the deepest descender even
        // when those come from different faces — taking one run's height crops the other.
        Assert.Equal(MathF.Max(runs[0].Baseline, runs[1].Baseline), mixed.Baseline, 0.01f);

        Assert.Equal(
            MathF.Max(runs[0].Height - runs[0].Baseline, runs[1].Height - runs[1].Baseline),
            mixed.Height - mixed.Baseline,
            0.01f
        );

        // Which for these two faces is a line taller than either run, since neither is deepest at
        // both ends. If that stops being true the assertions above still hold and this one says so.
        Assert.True(mixed.Height >= MathF.Max(runs[0].Height, runs[1].Height));
    }

    [Fact]
    public void Each_run_becomes_its_own_draw_command() {
        using var document = Documented();

        var label = Labelled(document, Latin + Kannada);
        document.Draw();

        var commands = document.Drawing.Commands
            .Where(static command => command.Kind == DrawCommandKind.Text)
            .ToList();

        // ⚠ The whole reason the line had to stop being one run: a draw command names one font, so a
        // mixed line is two commands. One command with two fonts' glyphs in it would draw the second
        // half from the first font's atlas, which is not a missing glyph but a wrong one.
        Assert.Equal(2, commands.Count);
        Assert.NotEqual(commands[0].Font, commands[1].Font);

        var line = label.Block()!.Lines[0];
        Assert.Equal(line.Runs[0].Width, commands[1].X - commands[0].X, 0.01f);

        // A shared baseline, which is the other half of being one line rather than two.
        Assert.Equal(commands[0].Y, commands[1].Y, 0.01f);
    }

    [Fact]
    public void A_caret_crosses_a_run_boundary_without_jumping() {
        using var document = Documented();

        var line = Labelled(document, Latin + Kannada).Block()!.Lines[0];

        Assert.Equal(0f, line.CaretOffset(0), 0.01f);
        Assert.Equal(line.Runs[0].Width, line.CaretOffset(Latin.Length), 0.01f);
        Assert.Equal(line.Width, line.CaretOffset(Latin.Length + Kannada.Length), 0.01f);

        // Monotonic across the join, which is what "without jumping" means and what a per-run caret
        // that forgot the pen would break — every index in the second run would come back as a
        // distance from the start of that run.
        var previous = -1f;
        for (var index = 0; index <= Latin.Length + Kannada.Length; index++) {
            var offset = line.CaretOffset(index);
            Assert.True(offset >= previous, $"caret {index} went backwards: {offset} after {previous}");
            previous = offset;
        }
    }

    [Fact]
    public void Hit_testing_finds_the_run_a_point_is_in() {
        using var document = Documented();

        var line = Labelled(document, Latin + Kannada).Block()!.Lines[0];
        var join = line.Runs[0].Width;

        Assert.Equal(0, line.CaretIndexAt(-10f));
        Assert.True(line.CaretIndexAt(join + 1f) >= Latin.Length, "a point past the join is in the second run");
        Assert.Equal(Latin.Length + Kannada.Length, line.CaretIndexAt(line.Width + 10f));
    }

    [Fact]
    public void A_wrapped_block_stacks_lines_of_different_heights() {
        var document = new UiDocument(400f, 300f);
        using var owned = document;

        document.Fonts.Register("Test", Lana);
        document.Fonts.AddFallback(Serif);
        document.Load(
            "root { width: 400px; height: 300px; align-items: flex-start; } label { font-family: Test; width: 60px; }"
        );

        var label = document.Root.Add("label");
        label.Text = "AAAA " + Kannada + Kannada;
        document.Update();

        var block = label.Block()!;

        Assert.True(block.Lines.Length > 1, $"expected wrapping, got {block.Lines.Length} line(s)");

        // ⚠ **The lines are in different faces and therefore different heights**, which is what makes
        // this the only test that can see a block stacking its lines by *each* line's height rather
        // than by the first one's. With one font every line is the same height and the two are the
        // same number — so a paragraph that overlapped itself the moment a fallback appeared would
        // pass every other test here.
        Assert.NotEqual(block.Lines[0].Height, block.Lines[1].Height);
        Assert.Equal(block.Lines[0].Height, block.TopOf(1), 0.01f);
        Assert.Equal(block.Lines[0].Height + block.Lines[1].Height, block.Height, 0.01f);
    }

    [Fact]
    public void Registering_a_face_rebuilds_a_line_that_was_already_built() {
        var document = new UiDocument(400f, 200f);
        using var owned = document;

        document.Fonts.Register("Test", Lana);
        document.Load("root { width: 400px; height: 200px; } label { font-family: Test; }");

        var label = Labelled(document, Latin + Kannada);
        Assert.Same(Lana, Assert.Single(label.Block()!.Lines[0].Runs).Font);

        // ⚠ **Nothing about the element changed**, and this is the case the line cache would get
        // wrong. Its text, its font size and its declaration are all what they were; what changed is
        // what the registry can answer — so the cache compares `FontRegistry.Revision` as well, and
        // a font registered after the first frame is a font that appears.
        document.Fonts.AddFallback(Serif);

        Assert.Equal(2, label.Block()!.Lines[0].Runs.Length);
        Assert.Same(Serif, label.Block()!.Lines[0].Runs[1].Font);
    }

    [Fact]
    public void A_declaration_that_names_nothing_registered_still_prefers_the_default() {
        using var document = Documented();

        // ⚠ **A space, and the test is nothing without it.** Both faces draw one, so it is the only
        // character here whose font is decided by chain order rather than by coverage — against
        // "AB" alone, a chain with the fallback in front still ends up drawing every letter in the
        // declared face, because the fallback has no Latin, and the test passes while defending
        // nothing. It cost a sabotage to notice.
        var label = document.Root.Add("div");
        label.Text = "A B";
        document.Update();

        // The default stands in for the *primary*, so it goes in front of the fallbacks rather than
        // behind them. Behind them, an element with no `font-family` would take its spaces — and
        // every character the fallback happens to have — from whichever face was registered as a
        // last resort for some other script.
        Assert.Same(Lana, Assert.Single(label.Block()!.Lines[0].Runs).Font);
    }
}
