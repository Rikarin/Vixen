// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     <see cref="TaffyTrackListParser" /> against hand-written expectations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The parser needs an oracle that is not the grid algorithm, and this is it.</b> Every
///         other translation in <see cref="TaffyStyleMap" /> is a keyword or a number, so a mistake
///         in it is a compile error or a refusal. A track list is a nested grammar, and a mistake in
///         <i>it</i> is a fixture that lays out and produces the wrong numbers — indistinguishable,
///         from inside <c>TaffyGridConformanceTests</c>, from the algorithm being wrong. That is
///         precisely the failure the whole corpus was set up to be immune to, so the parser gets
///         judged on its own terms first.
///     </para>
///     <para>
///         Every string below is one that actually occurs in <c>grid.xml</c>, <c>blockgrid.xml</c> or
///         <c>gridflex.xml</c>. The grammar was derived from all 3 564 track-list occurrences across
///         the three files and <see cref="Every_track_list_in_the_corpus_parses" /> holds that
///         derivation: if a refreshed corpus grows a form the grammar has no arm for, that test says
///         so by name rather than the fixture quietly turning into a numeric failure.
///     </para>
/// </remarks>
public class TaffyTrackListParserTests {
    // ── Breadths ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("40px", "40px")]
    [InlineData("auto", "auto")]
    [InlineData("min-content", "min-content")]
    [InlineData("max-content", "max-content")]
    [InlineData("30%", "30%")]
    [InlineData("0.5fr", "0.5fr")]
    [InlineData("40px 40px 40px", "40px 40px 40px")]
    [InlineData("10% 20% 30%", "10% 20% 30%")]
    [InlineData("1fr max-content", "1fr max-content")]
    public void A_bare_breadth_is_one_track(string value, string expected) =>
        Assert.Equal(expected, Render(value));

    /// <summary>
    ///     ⚠ A bare <c>fr</c> is <c>minmax(auto, Nfr)</c>, which the round trip has to preserve.
    /// </summary>
    /// <remarks>
    ///     <see cref="GridTrackSize.Single" /> is what applies §7.2.3's rule, so this asserts the
    ///     parser reached it rather than building the pair itself and getting a zero floor.
    /// </remarks>
    [Fact]
    public void A_flexible_track_keeps_its_automatic_minimum() {
        var track = Single("1fr");

        Assert.Equal(GridSizingKind.Auto, track.Min.Kind);
        Assert.Equal(GridSizingFunction.Flex(1f), track.Max);
    }

    [Fact]
    public void A_fixed_track_is_its_own_minimum_and_maximum() {
        var track = Single("40px");

        Assert.Equal(GridSizingFunction.Points(40f), track.Min);
        Assert.Equal(GridSizingFunction.Points(40f), track.Max);
    }

    // ── minmax() and fit-content() ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("minmax(0px,1fr)")]
    [InlineData("minmax(75px,1fr)")]
    [InlineData("minmax(0px,max-content)")]
    [InlineData("minmax(auto,10px)")]
    [InlineData("minmax(max-content,10px)")]
    [InlineData("minmax(min-content,10px)")]
    public void A_minmax_keeps_both_halves(string value) =>
        Assert.Equal(value, Render(value));

    /// <summary>
    ///     ⚠ An inverted <c>minmax()</c> is stored as written, because repairing it is §12.4's job.
    /// </summary>
    [Fact]
    public void An_inverted_minmax_is_not_clamped_by_the_parser() {
        var track = Single("minmax(max-content,10px)");

        Assert.Equal(GridSizingFunction.MaxContent, track.Min);
        Assert.Equal(GridSizingFunction.Points(10f), track.Max);
    }

    [Fact]
    public void A_fit_content_length_is_a_maximum_over_an_automatic_minimum() {
        var track = Single("fit-content(30px)");

        Assert.Equal(GridSizingFunction.Auto, track.Min);
        Assert.Equal(new GridSizingFunction(GridSizingKind.FitContent, 30f), track.Max);
    }

    [Fact]
    public void A_fit_content_percentage_remembers_that_it_was_one() {
        var track = Single("fit-content(50%)");

        Assert.Equal(new GridSizingFunction(GridSizingKind.FitContent, 50f, IsFitContentPercent: true), track.Max);
    }

    // ── repeat() ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fixed_repeat_is_expanded() {
        var (tracks, kind, index, count) = TaffyTrackListParser.Parse("grid-template-columns", "repeat(3, 40px)");

        Assert.Equal("40px 40px 40px", Join(tracks));
        Assert.Equal((GridAutoRepeat.None, -1, 0), (kind, index, count));
    }

    /// <summary>One <c>repeat()</c> may hold several tracks, and they expand as a block.</summary>
    [Fact]
    public void A_multi_track_repeat_expands_as_a_block() =>
        Assert.Equal("10px 20px 10px 20px", Render("repeat(2, 10px 20px)"));

    /// <summary>
    ///     ⚠ The space inside <c>repeat()</c>'s comma and the one inside <c>minmax()</c>'s absence of
    ///     a comma are the whole reason this is a tokeniser.
    /// </summary>
    [Fact]
    public void A_repeat_may_nest_a_minmax() {
        var (tracks, kind, index, count) = TaffyTrackListParser.Parse(
            "grid-template-columns",
            "repeat(auto-fill, minmax(150px,1fr))"
        );

        Assert.Equal("minmax(150px,1fr)", Join(tracks));
        Assert.Equal((GridAutoRepeat.AutoFill, 0, 1), (kind, index, count));
    }

    /// <summary>
    ///     The hard one: a bare track, a fixed repetition and an automatic one in a single list.
    /// </summary>
    /// <remarks>
    ///     <c>grid_auto_fill_fixed_size</c> writes exactly this. The automatic part is <i>third</i>,
    ///     so <c>AutoRepeatIndex</c> has to count the two tracks the first two items emitted — an
    ///     implementation that reports the index of the <c>repeat()</c> <i>item</i> rather than of
    ///     the track it produced gets 1 here instead of 2 and is right on every one-item list.
    /// </remarks>
    [Fact]
    public void A_list_may_mix_bare_tracks_with_both_kinds_of_repeat() {
        var (tracks, kind, index, count) = TaffyTrackListParser.Parse(
            "grid-template-columns",
            "40px repeat(1, 40px) repeat(auto-fill, 40px)"
        );

        Assert.Equal("40px 40px 40px", Join(tracks));
        Assert.Equal((GridAutoRepeat.AutoFill, 2, 1), (kind, index, count));
    }

    [Fact]
    public void An_automatic_repeat_reports_its_kind() {
        Assert.Equal(GridAutoRepeat.AutoFit, TaffyTrackListParser.Parse("grid-template-columns", "repeat(auto-fit, 100px)").Kind);
        Assert.Equal(GridAutoRepeat.AutoFill, TaffyTrackListParser.Parse("grid-template-columns", "repeat(auto-fill, 40px)").Kind);
    }

    /// <summary>
    ///     ⚠ An automatic repetition is written out ONCE, not expanded.
    /// </summary>
    /// <remarks>
    ///     Its repetition count comes from the container's own size at layout time, so a parser that
    ///     expanded it would be storing one frame's answer in the style. See the remarks on
    ///     <see cref="LayoutTree.SetGridTemplateColumns(LayoutNodeId,ReadOnlySpan{GridTrackSize},GridAutoRepeat,int,int)" />.
    /// </remarks>
    [Fact]
    public void An_automatic_repeat_is_written_out_once() =>
        Assert.Single(TaffyTrackListParser.Parse("grid-template-columns", "repeat(auto-fill, 40px)").Tracks);

    // ── The budget ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The corpus attacks the expansion on purpose and the parser has to survive it.</b>
    /// </summary>
    /// <remarks>
    ///     <c>repeat(40000, 10px 10px)</c> asks for eighty thousand tracks from twenty-four
    ///     characters. Expansion stops at <see cref="LayoutLimits.MaximumGridTracks" />, which is the
    ///     same ceiling <c>LayoutTree.WriteTemplate</c> clamps to — so the parser never hands the
    ///     store a list the store would silently cut.
    /// </remarks>
    [Theory]
    [InlineData("repeat(9000, 0px)", 9000)]
    [InlineData("repeat(10000, 0px)", 10000)]
    [InlineData("repeat(10001, 0px)", 10001)]
    [InlineData("repeat(32768, fit-content(512px)) fit-content(100%)", 32769)]
    [InlineData("repeat(65535, 0px)", 65535)]
    [InlineData("repeat(65536, 0px)", 65535)]
    [InlineData("repeat(40000, 10px 10px)", 65535)]
    public void Expansion_stops_at_the_track_limit(string value, int expected) {
        Assert.Equal(65_535, LayoutLimits.MaximumGridTracks);
        Assert.Equal(expected, TaffyTrackListParser.Parse("grid-template-columns", value).Tracks.Count);
    }

    /// <summary>
    ///     An automatic repetition that does not fit whole is dropped whole.
    /// </summary>
    /// <remarks>
    ///     ⚠ Half a repetition is not a smaller grid, it is a different declaration — and an
    ///     <c>AutoRepeatCount</c> that runs past the end of the tracks it was handed is a buffer
    ///     overrun waiting for §7.2.3.2 to read it.
    ///     ⚠ <b>No corpus list reaches this any more, and that is the point of the change that
    ///     raised the ceiling.</b> The two that used to — <c>repeat(9990, 0px) repeat(auto-fill,
    ///     …20 tracks)</c> and its <c>auto-fit</c> twin — need 10 010 tracks, which the store now
    ///     allocates, and both of their fixtures went green when it did. The rule still has to hold
    ///     at whatever the ceiling is, so it is exercised against the ceiling rather than against a
    ///     number the corpus happens to write.
    /// </remarks>
    [Fact]
    public void An_automatic_repetition_that_does_not_fit_is_dropped_rather_than_truncated() {
        var fixedTracks = LayoutLimits.MaximumGridTracks - 5;
        var value = $"repeat({fixedTracks}, 0px) repeat(auto-fill, " + string.Join(' ', Enumerable.Repeat("0px", 10)) + ")";
        var (tracks, kind, index, count) = TaffyTrackListParser.Parse("grid-template-columns", value);

        Assert.Equal(fixedTracks, tracks.Count);
        Assert.Equal((GridAutoRepeat.None, -1, 0), (kind, index, count));
    }

    /// <summary>
    ///     An automatic repetition that DOES fit is written out whole, at the new ceiling.
    /// </summary>
    /// <remarks>
    ///     ⚠ The companion to the test above, and the one that pins what changed.
    ///     <c>grid_overlarge_fixed_tracks_plus_auto_fill_repetition_over_limit</c> is exactly this
    ///     list: at a ceiling of 10 000 the repetition was dropped, the item at line 10 010 landed in
    ///     an implicit <c>auto</c> track and took a size the explicit <c>0px</c> track it named would
    ///     never have had. Chrome puts it at x=0 with every other track, because every track in the
    ///     list is zero.
    /// </remarks>
    [Fact]
    public void An_automatic_repetition_that_fits_is_written_out_whole() {
        var value = "repeat(9990, 0px) repeat(auto-fill, " + string.Join(' ', Enumerable.Repeat("0px", 20)) + ")";
        var (tracks, kind, index, count) = TaffyTrackListParser.Parse("grid-template-columns", value);

        Assert.Equal(10_010, tracks.Count);
        Assert.Equal((GridAutoRepeat.AutoFill, 9990, 20), (kind, index, count));
    }

    // ── Refusals ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>Everything outside the grammar is refused, not guessed at.</b>
    /// </summary>
    /// <remarks>
    ///     A corpus refresh that grows named lines or <c>subgrid</c> has to be loud. The alternative
    ///     is a track list that parses into something plausible and lays out to the wrong numbers,
    ///     which reads as an algorithm bug and is the one mistake this corpus exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("none")]
    [InlineData("subgrid")]
    [InlineData("masonry")]
    [InlineData("[full-start] 40px [full-end]")]
    [InlineData("calc(100% - 40px)")]
    [InlineData("40px 40px)")]
    [InlineData("repeat(3 40px)")]
    [InlineData("repeat(auto-fill, 40px) repeat(auto-fit, 40px)")]
    [InlineData("minmax(40px)")]
    [InlineData("fit-content(auto)")]
    [InlineData("40")]
    [InlineData("40em")]
    public void Anything_outside_the_grammar_is_refused(string value) =>
        Assert.Throws<TaffyUnsupportedException>(() => TaffyTrackListParser.Parse("grid-template-columns", value));

    // ── The corpus itself ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every track list the three grid corpora actually contain parses.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the test that keeps the grammar honest across a corpus refresh. A new form does
    ///     not become a numeric failure buried among two thousand fixtures; it becomes this test,
    ///     naming the string.
    /// </remarks>
    [Fact]
    public void Every_track_list_in_the_corpus_parses() {
        string[] properties = ["grid-template-columns", "grid-template-rows", "grid-auto-columns", "grid-auto-rows"];
        string[] categories = ["grid", "blockgrid", "gridflex"];
        var refused = new List<string>();
        var seen = 0;

        foreach (var category in categories) {
            foreach (var fixture in TaffyCorpus.Load(category)) {
                Walk(fixture.Input);
            }
        }

        Assert.Empty(refused);
        Assert.Equal(3564, seen);

        void Walk(TaffyInput input) {
            foreach (var property in properties) {
                if (!input.Attributes.TryGetValue(property, out var value)) {
                    continue;
                }

                seen++;

                try {
                    TaffyTrackListParser.Parse(property, value);
                } catch (TaffyUnsupportedException unsupported) {
                    refused.Add($"{property}: {unsupported.Feature}");
                }
            }

            foreach (var child in input.Children) {
                Walk(child);
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses and prints, so that an expectation reads as the CSS it came from.
    /// </summary>
    /// <remarks>
    ///     <see cref="GridTrackSize.ToString" /> is the store's own spelling and round-trips every
    ///     form in the grammar, which makes a wrong expectation legible instead of a struct dump.
    /// </remarks>
    static string Render(string value) => Join(TaffyTrackListParser.Parse("grid-template-columns", value).Tracks);

    static string Join(List<GridTrackSize> tracks) => string.Join(' ', tracks);

    static GridTrackSize Single(string value) =>
        Assert.Single(TaffyTrackListParser.Parse("grid-template-columns", value).Tracks);
}
