// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     <c>overflow-wrap</c>, <c>word-break</c>, <c>text-wrap</c> and <c>text-indent</c>, as the
///     pixels the software rasteriser produced.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests the consumption gate cannot be, and the argument is
///         <c>TextDecorationPixelTests</c>' one word for word.</b> That gate's verdict is "the draw
///         list changed", and re-wrapping a paragraph changes it whatever the new lines are. It would
///         pass on a break placed in the middle of a surrogate pair, on a word broken when it did not
///         need to be, and on a line that still runs off the right-hand edge after being told not to.
///     </para>
///     <para>
///         ⚠ <b>Every relation here is chosen to fail for the <i>neighbouring</i> case rather than
///         only for no declaration at all.</b> Breaking must both add a row band and pull the right
///         edge back inside the box — either alone is satisfied by something that is not wrapping.
///         And each opt-out is asserted against an <i>inherited</i> declaration rather than against
///         nothing, because that is the only situation in which it can do anything: <c>wrap-normal</c>
///         and <c>break-normal</c> emit CSS's initial value, so on a bare element they are correctly
///         indistinguishable from silence, and a test that wrote one on its own would be asserting
///         that the default is the default.
///     </para>
///     <para>
///         ⚠ <b>The word has no break opportunity in it, and that is what the feature is about.</b>
///         UAX#14 offers nothing between a run of Latin letters' first character and its last, so
///         <c>LineWrapper</c> reaches its "nothing fits" branch — the one and only place
///         <c>TextWrapMode.Anywhere</c> and <c>TextWrapMode.BreakWord</c> are consulted, and the one
///         place they part company. A word with a hyphen or a space in it would
///         wrap identically with the property and without it, and every assertion below would hold
///         against an engine that had never heard of <c>overflow-wrap</c>.
///     </para>
///     <para>
///         ⚠ <b>And <c>word-break</c> needs the fixture the other way round, which is why the two
///         families cannot share one.</b> <c>break-all</c> is only distinguishable from
///         <c>overflow-wrap</c> on text that has an ordinary opportunity <i>as well as</i> a long
///         word — see <see cref="Break_all_fills_the_line_that_overflow_wrap_leaves_ragged" /> — and
///         <c>keep-all</c> is not distinguishable in Latin at all, because LB28 already forbids the
///         break it suppresses. It is measured on ideographs, and on the one thing about them a font
///         with no CJK coverage still gets right: their advances.
///     </para>
/// </remarks>
public class TextWrappingPixelTests {
    /// <summary>One word, no space, no hyphen, no break opportunity anywhere inside it.</summary>
    const string Unbroken = "Wmilqjagwmilqjag";

    /// <summary>Four words, so that ordinary wrapping has somewhere to happen.</summary>
    const string Words = "Ag jq Wm il";

    const int BoxLeft = 20;
    const int BoxWidth = 120;

    static readonly FontFace Font = LoadFont();

    /// <summary>Where the glyphs landed in one frame.</summary>
    /// <param name="Bands">How many separated groups of rows hold a glyph pixel. One band is one line.</param>
    /// <param name="Right">The rightmost column holding one, or -1.</param>
    /// <param name="Count">How many glyph pixels there were, which says the text was drawn at all.</param>
    readonly record struct Marks(int Bands, int Right, int Count) {
        public bool Any => Count > 0;
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>White text on black in a box narrower than the word, under two nested declarations.</summary>
    /// <param name="text">What to draw.</param>
    /// <param name="outer">Declarations on the box, which the text element inherits.</param>
    /// <param name="inner">Declarations on the text element itself.</param>
    /// <remarks>
    ///     ⚠ <b>Two elements rather than one, because half of what is under test is the
    ///     inheritance.</b> <c>overflow-wrap</c> and <c>text-wrap</c> are both in
    ///     <c>InheritedProperties</c>, and the class is written on a row whose text is in a child
    ///     essentially always — a <c>.vxml</c> interpolation emits its text as a child element. A
    ///     one-element fixture would pass with the inheritance removed.
    ///     <para>
    ///         The box does not clip. An overflowing line has to stay visible for its right edge to
    ///         be measurable at all, and <c>overflow: hidden</c> here would make "it wrapped" and "it
    ///         was cut off" the same picture.
    ///     </para>
    /// </remarks>
    static Marks Render(string text, string outer, string inner) {
        using var ui = UiTest.Create(320f, 200f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root  { width: 320px; height: 200px; background-color: #000000; }
            .box  { position: absolute; left: {{BoxLeft}}px; top: 16px; width: {{BoxWidth}}px;
                    font-family: Test; font-size: 28px; color: #ffffff; {{outer}} }
            .text { {{inner}} }
            """
        );

        var box = ui.Create("div", null, "box", "box");
        ui.Create("span", box, "text", "text").Text = text;
        ui.Frame();

        return Scan(ui.Capture());
    }

    /// <summary>Counts the row bands the glyphs occupy and finds their right-hand edge.</summary>
    /// <remarks>
    ///     A band is a maximal run of consecutive rows holding a lit pixel, so two lines with a gap
    ///     between them count as two and one taller line counts as one. That is the measurement that
    ///     tells wrapping from a larger font, and it is why the row set is walked rather than only
    ///     its extent — an assertion on the extent alone would pass for text that simply got taller.
    /// </remarks>
    static Marks Scan(Bitmap image) {
        var rows = new HashSet<int>();
        var right = -1;
        var count = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                // Anything appreciably lighter than the black ground is a glyph: the text is the only
                // thing drawn, and it is antialiased, so almost no pixel is pure white.
                if (image.Pixels[image.Offset(x, y)] < 24) {
                    continue;
                }

                rows.Add(y);
                right = Math.Max(right, x);
                count++;
            }
        }

        var bands = 0;

        foreach (var y in rows.Order()) {
            if (!rows.Contains(y - 1)) {
                bands++;
            }
        }

        return new Marks(bands, right, count);
    }

    /// <summary>The font draws the letters this file measures.</summary>
    /// <remarks>
    ///     ⚠ The guard <c>TextDecorationPixelTests</c> keeps for the same reason. A shared test font
    ///     that lacked these characters would shape them to <c>.notdef</c> — a visible box, which is
    ///     still a lit pixel — and every relation below would hold against a row of tofu. A
    ///     <c>Fact</c> rather than an assertion inside <see cref="Render" />, so that its failure says
    ///     what is wrong instead of failing six tests obscurely.
    /// </remarks>
    [Fact]
    public void The_font_draws_the_letters_this_file_measures() {
        foreach (var letter in (Unbroken + Words).Distinct().Where(c => c != ' ')) {
            Assert.True(Font.Supports(letter), $"the test font has no glyph for '{letter}'");
        }
    }

    /// <summary>Left alone, an unbreakable word runs off the end of its box.</summary>
    /// <remarks>
    ///     The baseline every other test here is a departure from, and it is also the CSS default
    ///     being asserted rather than assumed: <c>overflow-wrap: normal</c> means "let it overflow",
    ///     and if the word already fitted or already wrapped there would be nothing for the property
    ///     to change and the whole file would be measuring noise.
    /// </remarks>
    [Fact]
    public void An_unbreakable_word_overflows_its_box_by_default() {
        var marks = Render(Unbroken, string.Empty, string.Empty);

        Assert.True(marks.Any, "the word was not drawn at all");
        Assert.Equal(1, marks.Bands);
        Assert.True(
            marks.Right > BoxLeft + BoxWidth,
            $"the word ends at x={marks.Right}, inside a box that ends at {BoxLeft + BoxWidth} — "
            + "it fits, so nothing below is measuring wrapping"
        );
    }

    /// <summary>Both spellings break the word inside the box, and both do the same thing.</summary>
    /// <remarks>
    ///     ⚠ <b>Two assertions and not one.</b> More row bands alone would pass for text that grew;
    ///     a right edge inside the box alone would pass for text that was clipped or that vanished.
    ///     Together they are wrapping.
    /// </remarks>
    [Theory]
    [InlineData("overflow-wrap: break-word")]
    [InlineData("overflow-wrap: anywhere")]
    public void Breaking_pulls_the_word_inside_the_box_and_onto_a_second_line(string declaration) {
        var marks = Render(Unbroken, declaration, string.Empty);

        Assert.True(marks.Any, "the word was not drawn at all");
        Assert.True(marks.Bands >= 2, $"the word stayed on {marks.Bands} line(s)");
        Assert.True(
            marks.Right <= BoxLeft + BoxWidth,
            $"the word still ends at x={marks.Right}, past the box's {BoxLeft + BoxWidth}"
        );
    }

    /// <summary><c>anywhere</c> and <c>break-word</c> draw the same picture and size differently.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to assert that the two keywords were <i>one behaviour</i>, as a stated
    ///         deviation with a note saying it would fail the day the layout grew an
    ///         intrinsic-minimum stage. #682 is that day, and the note was accurate.</b> CSS Sizing
    ///         §5.2 separates them only by their min-content contribution — <c>anywhere</c>'s
    ///         intrinsic minimum shrinks to one grapheme, <c>break-word</c>'s stays the longest
    ///         unbreakable run — and CSS Text §5.3 has both break an overflowing word at line-layout
    ///         time regardless. So the first half of this is unchanged and is the half that says the
    ///         difference is <em>only</em> in the sizing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The oracle for the second half is closed form rather than a threshold.</b>
    ///         "Smaller than the word" is satisfied by a box that measured nothing, by a break in the
    ///         middle of a surrogate pair, and by an off-by-one anywhere in <c>Squeeze</c>. The
    ///         widest single grapheme of the same text in the same face is the number CSS names, and
    ///         nothing but the intended break produces it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>break-word</c> is pinned against <c>normal</c> rather than merely against
    ///         <c>anywhere</c>.</b> Its intrinsic minimum is specified to be <i>unchanged</i> by the
    ///         keyword, so the assertion that says so is equality with the keyword absent — a bound
    ///         of "bigger than one grapheme" would be met by any regression that shrank it a little.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_breaking_keywords_differ_in_the_intrinsic_minimum_and_nowhere_else() {
        Assert.Equal(
            Render(Unbroken, "overflow-wrap: break-word", string.Empty),
            Render(Unbroken, "overflow-wrap: anywhere", string.Empty)
        );

        var normal = MinContentWidth(Unbroken, "overflow-wrap: normal");
        var breakWord = MinContentWidth(Unbroken, "overflow-wrap: break-word");
        var anywhere = MinContentWidth(Unbroken, "overflow-wrap: anywhere");

        // The floor, and without it every relation below is met by a box that measured nothing.
        Assert.True(normal > 0f, "the unbroken word measured nothing at all");

        Assert.Equal(normal, breakWord);

        var widest = WidestGrapheme(Unbroken);

        Assert.Equal(widest, anywhere);
        Assert.True(anywhere < breakWord, $"one grapheme measured {anywhere} and the whole word {breakWord}");
    }

    /// <summary><c>word-break: break-all</c> moves the intrinsic minimum too, and by itself.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of #682 that was true in practice and asserted nowhere.</b> The intrinsic
    ///         stage does not read <c>word-break</c> as a separate act — it reads it the only way it
    ///         reads anything, by asking for the text in no room and letting
    ///         <c>LineBreaker.Collect</c>'s tailoring decide where a line may end. <c>break-all</c>
    ///         offers an opportunity between every pair of typographic character units, so the probe
    ///         comes back one grapheme wide without <c>LineWrapper</c>'s "nothing fits" branch being
    ///         reached at all. That is a different mechanism from <c>overflow-wrap: anywhere</c>
    ///         arriving at the same number, and a regression in the tailoring's reach into the
    ///         intrinsic path would have moved nothing any test looked at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured against the same closed form the keyword pair is</b> — the widest single
    ///         grapheme of the same text in the same face, not "smaller than the word". <c>break-all</c>
    ///         breaking one character early, or in the middle of a surrogate pair, is smaller than the
    ///         word as well.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_intrinsic_minimum_reads_word_break_and_not_only_overflow_wrap() {
        var normal = MinContentWidth(Unbroken, "word-break: normal");
        var breakAll = MinContentWidth(Unbroken, "word-break: break-all");

        // Without it, everything below is met by a box that measured nothing at all.
        Assert.True(normal > 0f, "the unbroken word measured nothing at all");

        // `word-break` alone, with `overflow-wrap` left at its initial value the whole way — so the
        // difference cannot be the other property's.
        Assert.Equal(MinContentWidth(Unbroken, string.Empty), normal);
        Assert.Equal(WidestGrapheme(Unbroken), breakAll);
        Assert.True(breakAll < normal, $"one grapheme measured {breakAll} and the whole word {normal}");
    }

    /// <summary>The widest single grapheme of a text, measured in the same face at the same size.</summary>
    /// <remarks>
    ///     What CSS Sizing §5.2 names as the min-content contribution of a box that may break inside
    ///     a word, expressed as a measurement of this engine rather than as a number written down —
    ///     so it moves with the font, the size and the shaper instead of pinning a threshold.
    /// </remarks>
    static float WidestGrapheme(string text) {
        var widest = 0f;

        foreach (var grapheme in text) {
            widest = MathF.Max(widest, MinContentWidth(grapheme.ToString(), "overflow-wrap: normal"));
        }

        return widest;
    }

    /// <summary>What the layout's intrinsic probe is handed: the text measured in no room at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Width zero and <c>MeasureMode.AtMost</c> is literally how <c>LayoutTree</c> asks for
    ///     a min-content size</b> — see <c>ComputeMinContentSizeUncached</c>'s leaf branch, which
    ///     passes <c>0f</c> on the inline axis — and <c>TextLayout.Measure</c> turns that straight
    ///     into this call. So this is the stage under test rather than a proxy for it.
    /// </remarks>
    static float MinContentWidth(string text, string declaration) {
        using var ui = UiTest.Create(320f, 200f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root { width: 320px; height: 200px; }
            .box { width: {{BoxWidth}}px; font-family: Test; font-size: 28px; {{declaration}} }
            """
        );

        var box = ui.Create("div", null, "box", "box");
        box.Text = text;
        ui.Frame();

        return box.Block(0f)?.Width ?? -1f;
    }

    /// <summary><c>normal</c> on the text escapes a breaking rule on its container.</summary>
    /// <remarks>
    ///     ⚠ <b>What <c>wrap-normal</c> and <c>break-normal</c> are for, and the only arrangement in
    ///     which either does anything at all.</b> Both emit CSS's initial value, so written on a bare
    ///     element they are correctly indistinguishable from writing nothing — which is exactly how
    ///     the consumption gate measures them, and would be the whole story if the property did not
    ///     inherit. It does, so this is a real opt-out and not a no-op with a name: the same argument
    ///     <c>text-clip</c> earns its place with.
    /// </remarks>
    [Fact]
    public void Normal_on_the_text_escapes_a_breaking_container() {
        var inherited = Render(Unbroken, "overflow-wrap: break-word", string.Empty);
        var escaped = Render(Unbroken, "overflow-wrap: break-word", "overflow-wrap: normal");

        Assert.True(inherited.Bands >= 2, "the container's rule did not reach the text");
        Assert.Equal(1, escaped.Bands);
        Assert.True(
            escaped.Right > BoxLeft + BoxWidth,
            $"the text ends at x={escaped.Right} and did not escape back to overflowing"
        );
    }

    /// <summary><c>text-wrap: nowrap</c> keeps wrappable text on one line.</summary>
    /// <remarks>
    ///     CSS Text 4 § 4 makes this the half of <c>white-space</c> that decides wrapping, and
    ///     <c>UiDocument.WrapsOf</c> reads it beside <c>white-space</c>. Asserted on text that has
    ///     break opportunities in it and does wrap without the declaration, or the test would hold
    ///     against an engine that ignored the property.
    /// </remarks>
    [Fact]
    public void Nowrap_keeps_wrappable_text_on_one_line() {
        var wrapped = Render(Words, string.Empty, string.Empty);
        var held = Render(Words, "text-wrap: nowrap", string.Empty);

        Assert.True(wrapped.Bands >= 2, $"the text did not wrap to begin with ({wrapped.Bands} band)");
        Assert.Equal(1, held.Bands);
        Assert.True(
            held.Right > BoxLeft + BoxWidth,
            $"the line ends at x={held.Right}, so it was not held past the box's {BoxLeft + BoxWidth}"
        );
    }

    /// <summary><c>text-wrap: wrap</c> on the text escapes a <c>nowrap</c> on its container.</summary>
    /// <remarks>
    ///     The opt-out half, and the reason <c>text-wrap</c> is registered as a class rather than only
    ///     <c>text-nowrap</c>. Same shape as <see cref="Normal_on_the_text_escapes_a_breaking_container" />
    ///     and for the same reason: the property inherits.
    /// </remarks>
    [Fact]
    public void Wrap_on_the_text_escapes_a_nowrap_container() {
        var held = Render(Words, "text-wrap: nowrap", string.Empty);
        var escaped = Render(Words, "text-wrap: nowrap", "text-wrap: wrap");

        Assert.Equal(1, held.Bands);
        Assert.True(escaped.Bands >= 2, $"the text stayed on {escaped.Bands} line(s)");
    }

    /// <summary>⚠ <c>balance</c> and <c>pretty</c> draw different pictures from the default.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test is the inversion of
    ///         <c>The_better_break_keywords_are_indistinguishable_from_the_default</c>, which held
    ///         this file's whole refusal and was named as the thing that had to start failing before
    ///         either class was worth registering.</b> It said: <c>LineWrapper</c> is greedy
    ///         first-fit by an argued decision, so both values reach <c>WrapsOf</c>, fall through to
    ///         "wraps", and produce exactly the lines <c>text-wrap: wrap</c> produces — two classes
    ///         that would resolve, compute, and differ from the default in name only, invisible to
    ///         the consumption gate because the property <i>is</i> read.
    ///     </para>
    ///     <para>
    ///         Every clause of that was true and the greedy decision is untouched: <c>auto</c> is
    ///         still first-fit and still costs one pass. What the two keywords now get is a
    ///         <i>second</i> pass over the greedy answer — <c>balance</c> bisects for the narrowest
    ///         width that still costs no line, <c>pretty</c> refuses a last line holding one word —
    ///         so the picture moves, which is what this asserts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted as "not the default" rather than as a pixel count</b>, because what each
    ///         keyword does to <i>this</i> paragraph in <i>this</i> face is
    ///         <c>Vixen.Ui.Text.Tests.LineWrapTests</c>'s question and it answers it in closed form
    ///         against a stub advance array. What this file is for is the crossing: that a class on
    ///         an element reaches the wrapper at all.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("balance")]
    [InlineData("pretty")]
    public void The_better_break_keywords_change_where_the_lines_break(string keyword) {
        var greedy = Render(Words, string.Empty, string.Empty);
        var chosen = Render(Words, $"text-wrap: {keyword}", string.Empty);

        // Both keywords wrap — neither may quietly turn wrapping off on its way through `WrapsOf`.
        Assert.True(chosen.Bands >= 2, $"the text stayed on {chosen.Bands} line(s)");
        Assert.NotEqual(greedy, chosen);
    }

    /// <summary>Twelve Han ideographs, which UAX#14 lets a line end between any two of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>None of the shared test fonts covers a CJK code point, so these draw as
    ///         <c>.notdef</c> boxes — and that is legitimate <i>here</i> and would not be for most of
    ///         this file.</b> <c>word-break: keep-all</c> is a rule about where a line may end, not
    ///         about which glyph is drawn: <c>LineBreaker</c> classifies the characters, and the
    ///         wrapper needs an advance, which <c>.notdef</c> has — 1214 of this face's 2048 units.
    ///         So the picture is a row of identical boxes whose <i>arrangement</i> is exactly what is
    ///         under test, and every relation below is about that arrangement.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What would not survive the substitution is anything about the glyphs</b> — a
    ///         cursive join, a ligature, a mark's placement — which is why
    ///         <see cref="The_font_draws_the_letters_this_file_measures" /> guards the Latin fixtures
    ///         and deliberately does not guard this one. The alternative is a CJK face in the
    ///         repository for one property, and the measurement does not need it.
    ///     </para>
    ///     <para>
    ///         Twelve rather than six: at this size six fit on one line, and a fixture that does not
    ///         wrap without the declaration measures nothing with it.
    ///     </para>
    /// </remarks>
    const string Ideographs = "\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587";

    /// <summary><c>word-break: break-all</c> breaks a word that has no opportunity in it.</summary>
    /// <remarks>
    ///     The same two assertions <see cref="Breaking_pulls_the_word_inside_the_box_and_onto_a_second_line" />
    ///     makes, because a picture that only grew or only got clipped satisfies either one alone.
    ///     What this shares with that test is the outcome and not the mechanism —
    ///     <see cref="Break_all_fills_the_line_that_overflow_wrap_leaves_ragged" /> is where the two
    ///     part company.
    /// </remarks>
    [Fact]
    public void Break_all_pulls_an_unbreakable_word_inside_the_box() {
        var marks = Render(Unbroken, "word-break: break-all", string.Empty);

        Assert.True(marks.Any, "the word was not drawn at all");
        Assert.True(marks.Bands >= 2, $"the word stayed on {marks.Bands} line(s)");
        Assert.True(
            marks.Right <= BoxLeft + BoxWidth,
            $"the word still ends at x={marks.Right}, past the box's {BoxLeft + BoxWidth}"
        );
    }

    /// <summary>
    ///     ⚠ <b><c>break-all</c> and <c>overflow-wrap: anywhere</c> are two different pictures, and
    ///     this is the test that would have caught registering one as the other.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both keep the long word inside the box, so a fixture holding only the long word cannot
    ///         tell them apart — which is exactly why the text here has a short word in front of it.
    ///         <c>anywhere</c> is consulted only in the branch where nothing fits, which is reached
    ///         after <c>"Ag "</c> has already been sent to a line of its own, so the first line keeps
    ///         its ragged edge. <c>break-all</c> put an opportunity after every letter, so greedy
    ///         first-fit packs the first line out to the box's edge and everything after it shifts.
    ///     </para>
    ///     <para>
    ///         The right-hand edge is the evidence: the packed paragraph reaches further right than
    ///         the rescued one, on the same text, in the same box, at the same size.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Break_all_fills_the_line_that_overflow_wrap_leaves_ragged() {
        const string Mixed = "Ag " + Unbroken;

        var plain = Render(Mixed, string.Empty, string.Empty);
        var rescued = Render(Mixed, "overflow-wrap: anywhere", string.Empty);
        var packed = Render(Mixed, "word-break: break-all", string.Empty);

        Assert.True(
            plain.Right > BoxLeft + BoxWidth,
            "the text already fitted, so neither declaration has anything to do"
        );

        Assert.True(rescued.Right <= BoxLeft + BoxWidth, $"anywhere left the text at x={rescued.Right}");
        Assert.True(packed.Right <= BoxLeft + BoxWidth, $"break-all left the text at x={packed.Right}");

        Assert.NotEqual(rescued, packed);
        Assert.True(
            packed.Right > rescued.Right,
            $"break-all reached x={packed.Right} and anywhere x={rescued.Right} — the first line was "
            + "not packed, so break-all is behaving as overflow-wrap"
        );
    }

    /// <summary><c>word-break: keep-all</c> stops a run of ideographs breaking inside itself.</summary>
    /// <remarks>
    ///     ⚠ <b>The one keyword in this file that no Latin fixture could measure, and the reason the
    ///     pair is registered together.</b> UAX#14 offers a break between any two ideographs and
    ///     between any two Hangul syllables, and offers none at all between two Latin letters — LB28
    ///     forbids it — so <c>keep-all</c> written over English is correctly indistinguishable from
    ///     writing nothing. A test in Latin would have passed against an engine that never read the
    ///     property.
    /// </remarks>
    [Fact]
    public void Keep_all_holds_an_ideographic_run_on_one_line() {
        var wrapped = Render(Ideographs, string.Empty, string.Empty);
        var held = Render(Ideographs, "word-break: keep-all", string.Empty);

        Assert.True(wrapped.Bands >= 2, $"the run did not wrap to begin with ({wrapped.Bands} band)");
        Assert.True(wrapped.Right <= BoxLeft + BoxWidth, $"and it already overflowed, at x={wrapped.Right}");

        Assert.Equal(1, held.Bands);
        Assert.True(
            held.Right > BoxLeft + BoxWidth,
            $"the run ends at x={held.Right}, so it was not held past the box's {BoxLeft + BoxWidth}"
        );
    }

    /// <summary><c>text-indent</c> moves the first line's glyphs and no others'.</summary>
    /// <remarks>
    ///     ⚠ <b>A pixel test rather than a draw-list one, because the draw list is satisfied by the
    ///     wrong half of the feature.</b> An indent is two facts — the first line starts further in,
    ///     <i>and</i> it wraps earlier — and a command list changes under either alone. What is
    ///     visible in the picture is that the left-hand edge of the first band moved while the edge of
    ///     the second did not, which is the pair.
    /// </remarks>
    [Fact]
    public void An_indent_moves_the_first_line_and_leaves_the_second_where_it_was() {
        var plain = Bands(Indented(Words, "text-indent: 0px"));
        var pushed = Bands(Indented(Words, "text-indent: 24px"));

        Assert.True(plain.Count >= 2, $"the text did not wrap to begin with ({plain.Count} band)");
        Assert.Equal(plain.Count, pushed.Count);

        Assert.Equal(plain[0] + 24, pushed[0]);
        Assert.Equal(plain[1], pushed[1]);
    }

    /// <summary>And a negative one hangs the first line out to the left of the second.</summary>
    [Fact]
    public void A_hanging_indent_pulls_the_first_line_left_of_the_rest() {
        var hung = Bands(Indented(Words, "text-indent: -12px"));

        Assert.True(hung.Count >= 2, "the text did not wrap, so there is nothing to hang out from");
        Assert.Equal(hung[1] - 12, hung[0]);
    }

    /// <summary>The same box as <see cref="Render" />, captured rather than scanned.</summary>
    /// <remarks>
    ///     <c>text-indent</c> is computed and inherited — see <c>UiDocument.ResolveText</c> — so it is
    ///     written on the box and read on the span inside it, which is the arrangement every other
    ///     test in this file uses and the one a <c>.vxml</c> interpolation forces.
    /// </remarks>
    static Bitmap Indented(string text, string outer) {
        using var ui = UiTest.Create(320f, 200f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root  { width: 320px; height: 200px; background-color: #000000; }
            .box  { position: absolute; left: {{BoxLeft}}px; top: 16px; width: {{BoxWidth}}px;
                    font-family: Test; font-size: 28px; color: #ffffff; {{outer}} }
            """
        );

        var box = ui.Create("div", null, "box", "box");
        ui.Create("span", box, "text", "text").Text = text;
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>The leftmost lit column of each row band, top to bottom.</summary>
    /// <remarks>
    ///     ⚠ <b>Per band rather than over the whole image, which is the only measurement that can tell
    ///     an indent from a margin.</b> <see cref="Scan" />'s single <c>Right</c> is a fact about the
    ///     picture as a whole and moves for both; a list of per-line left edges says <i>which</i> line
    ///     moved, and the claim is that exactly one did.
    /// </remarks>
    static List<int> Bands(Bitmap image) {
        var edges = new List<int>();
        var current = -1;
        var lit = false;

        for (var y = 0; y < image.Height; y++) {
            var left = -1;

            for (var x = 0; x < image.Width && left < 0; x++) {
                if (image.Pixels[image.Offset(x, y)] >= 24) {
                    left = x;
                }
            }

            if (left < 0) {
                if (lit) {
                    edges.Add(current);
                }

                lit = false;
                current = -1;
                continue;
            }

            lit = true;
            current = current < 0 ? left : Math.Min(current, left);
        }

        if (lit) {
            edges.Add(current);
        }

        return edges;
    }

    /// <summary><c>break-normal</c> on the text escapes either <c>word-break</c> on its container.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that was missing until <c>word-break</c> gained a reader, and the reason
    ///     Tailwind's <c>break-normal</c> is two declarations rather than one.</b> <c>word-break</c>
    ///     inherits, so a <c>break-all</c> on a row reaches the span its text is in — and before this
    ///     the class emitted the <c>overflow-wrap</c> half alone, which cannot undo it. Asserted
    ///     against an inherited declaration in both directions, because on a bare element the value is
    ///     CSS's initial one and is correctly indistinguishable from silence.
    /// </remarks>
    [Theory]
    [InlineData(Unbroken, "word-break: break-all")]
    [InlineData("\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587", "word-break: keep-all")]
    public void Normal_on_the_text_escapes_a_word_break_container(string text, string container) {
        var inherited = Render(text, container, string.Empty);
        var escaped = Render(text, container, "word-break: normal");
        var never = Render(text, string.Empty, string.Empty);

        Assert.NotEqual(never, inherited);
        Assert.Equal(never, escaped);
    }
}
