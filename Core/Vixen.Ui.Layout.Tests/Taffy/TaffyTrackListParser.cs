// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Parses one CSS <c>&lt;track-list&gt;</c> into the shape <see cref="LayoutTree" /> stores.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a tokeniser and not a <c>Split</c>, because both of the separators a track
///         list uses also occur inside its functions.</b> <c>minmax(0px,1fr)</c> holds a comma and
///         <c>repeat(auto-fill, 1px 1px)</c> holds spaces, so splitting on either at the top level
///         cuts through a function argument — and the corpus contains
///         <c>40px repeat(1, 40px) repeat(auto-fill, 40px)</c>, which needs both separators read
///         correctly in one string. A depth counter over the parentheses is the whole of the fix and
///         it is cheaper than the regular expression it replaces.
///     </para>
///     <para>
///         ⚠ <b>A fixed <c>repeat(N, …)</c> is expanded here and an automatic one is not, and that
///         asymmetry is CSS Grid §7.2.3.2 rather than a shortcut.</b> <c>repeat(4, 10px)</c> is
///         defined as a textual expansion — it means exactly <c>10px 10px 10px 10px</c> and nothing
///         downstream can tell the two apart. <c>repeat(auto-fill, …)</c> is not: the number of
///         repetitions is computed from the container's own definite size, so it changes when the
///         box is resized without the declaration changing at all. Expanding it here would freeze one
///         frame's answer into the style, which is why <c>SetGridTemplateColumns</c> takes one
///         repetition plus a position instead of a count.
///     </para>
///     <para>
///         ⚠ <b>The expansion is budgeted, because the corpus deliberately attacks it.</b>
///         <c>repeat(40000, 10px 10px)</c> asks for eighty thousand tracks and
///         <c>repeat(auto-fill, 1px …)</c> writes twenty-one thousand of them out longhand in a
///         single 84 KB attribute — these fixtures exist precisely to find an implementation that
///         allocates whatever it is told to. Expansion stops at
///         <see cref="LayoutLimits.MaximumGridTracks" />, which is the same ceiling
///         <c>LayoutTree.WriteTemplate</c> clamps to, so the parser and the store agree on the size
///         of the largest grid that exists.
///     </para>
///     <para>
///         Anything outside the grammar below — named lines, <c>subgrid</c>, <c>masonry</c>,
///         <c>calc()</c>, <c>none</c> — is refused with a <see cref="TaffyUnsupportedException" />
///         rather than skipped. A refreshed corpus that grows a new form has to be noticed; the one
///         thing it must not do is parse into something plausible and wrong.
///     </para>
/// </remarks>
static class TaffyTrackListParser {
    /// <summary>
    ///     Parses a track list.
    /// </summary>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">The track list, verbatim.</param>
    /// <returns>
    ///     The emitted tracks; which automatic repetition the list carries, if any; where in
    ///     <c>Tracks</c> its single written-out repetition begins; and how many tracks that
    ///     repetition holds. <c>AutoRepeatIndex</c> is −1 and <c>AutoRepeatCount</c> zero when the
    ///     list has no automatic repetition.
    /// </returns>
    public static (List<GridTrackSize> Tracks, GridAutoRepeat Kind, int AutoRepeatIndex, int AutoRepeatCount) Parse(
        string property,
        string value
    ) {
        var tracks = new List<GridTrackSize>();
        var kind = GridAutoRepeat.None;
        var autoRepeatIndex = -1;
        var autoRepeatCount = 0;

        foreach (var item in Items(property, value)) {
            if (!item.StartsWith("repeat(", StringComparison.Ordinal)) {
                Emit(tracks, TrackSize(property, item));
                continue;
            }

            var (counter, repetition) = Repeat(property, item);

            switch (counter) {
                case "auto-fill" or "auto-fit": {
                    // CSS Grid §7.2.3.2 permits at most one automatic repetition per axis, and the
                    // corpus honours that. A second one is a corpus that changed, not a list to
                    // guess at.
                    if (kind != GridAutoRepeat.None) {
                        throw new TaffyUnsupportedException($"{property}: two automatic repetitions");
                    }

                    // ⚠ Either the whole repetition is written out or none of it is. Half a
                    // repetition is not a smaller grid, it is a different declaration — and the two
                    // corpus lists that hit this (`repeat(9990, 0px) repeat(auto-fill, <20 tracks>)`
                    // and its auto-fit twin) would otherwise leave the store with an
                    // AutoRepeatCount that runs off the end of the tracks it was given.
                    if (tracks.Count + repetition.Count > LayoutLimits.MaximumGridTracks) {
                        break;
                    }

                    kind = counter == "auto-fill" ? GridAutoRepeat.AutoFill : GridAutoRepeat.AutoFit;
                    autoRepeatIndex = tracks.Count;
                    autoRepeatCount = repetition.Count;
                    tracks.AddRange(repetition);

                    break;
                }

                default: {
                    if (!int.TryParse(counter, NumberStyles.None, CultureInfo.InvariantCulture, out var times)) {
                        throw new TaffyUnsupportedException($"{property}: repeat({counter}, …)");
                    }

                    for (var repeat = 0; repeat < times && tracks.Count < LayoutLimits.MaximumGridTracks; repeat++) {
                        foreach (var track in repetition) {
                            if (!Emit(tracks, track)) {
                                break;
                            }
                        }
                    }

                    break;
                }
            }
        }

        return (tracks, kind, autoRepeatIndex, autoRepeatCount);
    }

    // ── Tokenising ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Splits a track list on the spaces that are not inside a function's parentheses.</summary>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">The list, or one <c>repeat()</c>'s body.</param>
    /// <returns>Its items, in order.</returns>
    static List<string> Items(string property, string value) {
        var items = new List<string>();
        var depth = 0;
        var start = -1;

        for (var index = 0; index < value.Length; index++) {
            switch (value[index]) {
                case '(':
                    depth++;
                    break;

                case ')':
                    if (--depth < 0) {
                        throw new TaffyUnsupportedException($"{property}: {value}");
                    }

                    break;

                case ' ' when depth == 0:
                    if (start >= 0) {
                        items.Add(value[start..index]);
                        start = -1;
                    }

                    continue;
            }

            if (start < 0) {
                start = index;
            }
        }

        if (depth != 0) {
            throw new TaffyUnsupportedException($"{property}: {value}");
        }

        if (start >= 0) {
            items.Add(value[start..]);
        }

        return items;
    }

    /// <summary>Takes a <c>repeat()</c> apart into its counter and one repetition's tracks.</summary>
    /// <remarks>
    ///     ⚠ The first comma in the body is always <c>repeat()</c>'s own, and no depth counting is
    ///     needed to find it: the counter is an integer or a keyword, so it can hold neither a comma
    ///     nor a parenthesis, and every comma a nested <c>minmax()</c> contributes is therefore
    ///     further right than this one.
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="item">The whole <c>repeat(…)</c>.</param>
    /// <returns>The counter, verbatim, and the tracks of one repetition.</returns>
    static (string Counter, List<GridTrackSize> Repetition) Repeat(string property, string item) {
        if (!item.EndsWith(')')) {
            throw new TaffyUnsupportedException($"{property}: {item}");
        }

        var body = item["repeat(".Length..^1];
        var comma = body.IndexOf(',');

        if (comma < 0) {
            throw new TaffyUnsupportedException($"{property}: {item}");
        }

        var written = Items(property, body[(comma + 1)..]);
        if (written.Count == 0) {
            throw new TaffyUnsupportedException($"{property}: {item}");
        }

        var repetition = new List<GridTrackSize>(written.Count);
        foreach (var track in written) {
            repetition.Add(TrackSize(property, track));
        }

        return (body[..comma], repetition);
    }

    // ── The grammar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One <c>&lt;track-size&gt;</c>: a breadth, a <c>minmax()</c> or a <c>fit-content()</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>minmax()</c> whose minimum exceeds its maximum is parsed faithfully and not
    ///     repaired here.</b> <c>minmax(auto,10px)</c> and <c>minmax(max-content,10px)</c> both occur
    ///     in the corpus, and §12.4 is explicit that the growth limit is raised to the base size when
    ///     it ends up below it — which is a step of the sizing algorithm operating on resolved
    ///     numbers, not a syntactic fixup. Clamping at parse time would resolve
    ///     <c>max-content</c> against nothing.
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="item">The token.</param>
    /// <returns>The track.</returns>
    static GridTrackSize TrackSize(string property, string item) {
        if (item.StartsWith("minmax(", StringComparison.Ordinal)) {
            if (!item.EndsWith(')')) {
                throw new TaffyUnsupportedException($"{property}: {item}");
            }

            var body = item["minmax(".Length..^1];
            var comma = body.IndexOf(',');

            if (comma < 0) {
                throw new TaffyUnsupportedException($"{property}: {item}");
            }

            return GridTrackSize.MinMax(
                Breadth(property, body[..comma].Trim()),
                Breadth(property, body[(comma + 1)..].Trim())
            );
        }

        if (item.StartsWith("fit-content(", StringComparison.Ordinal)) {
            if (!item.EndsWith(')')) {
                throw new TaffyUnsupportedException($"{property}: {item}");
            }

            var argument = item["fit-content(".Length..^1].Trim();

            // §7.2.2's argument is a <length-percentage> only — a keyword there is not fit-content.
            if (argument.EndsWith("px", StringComparison.Ordinal)) {
                return GridTrackSize.FitContent(Number(property, argument[..^2]), isPercent: false);
            }

            if (argument.EndsWith('%')) {
                return GridTrackSize.FitContent(Number(property, argument[..^1]), isPercent: true);
            }

            throw new TaffyUnsupportedException($"{property}: fit-content({argument})");
        }

        return GridTrackSize.Single(Breadth(property, item));
    }

    /// <summary>One <c>&lt;track-breadth&gt;</c>.</summary>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="token">The token.</param>
    /// <returns>The sizing function.</returns>
    static GridSizingFunction Breadth(string property, string token) =>
        token switch {
            "auto" => GridSizingFunction.Auto,
            "min-content" => GridSizingFunction.MinContent,
            "max-content" => GridSizingFunction.MaxContent,
            _ when token.EndsWith("px", StringComparison.Ordinal) => GridSizingFunction.Points(Number(property, token[..^2])),

            // ⚠ Before the percentage arm, because `fr` is not a length and must never reach
            // StyleLength — see the remarks on GridSizingKind.
            _ when token.EndsWith("fr", StringComparison.Ordinal) => GridSizingFunction.Flex(Number(property, token[..^2])),
            _ when token.EndsWith('%') => GridSizingFunction.Percent(Number(property, token[..^1])),
            _ => throw new TaffyUnsupportedException($"{property}: {token}")
        };

    /// <summary>Parses a number, refusing rather than throwing <see cref="FormatException" />.</summary>
    /// <remarks>
    ///     A <c>calc()</c> or a named line reaches here as a token this grammar has no arm for, and a
    ///     refusal is the honest answer for both — the corpus is data, so an unparseable value is a
    ///     corpus that grew a feature rather than a bug in the test harness.
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="token">The digits.</param>
    /// <returns>The number.</returns>
    static float Number(string property, string token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new TaffyUnsupportedException($"{property}: {token}");

    /// <summary>Appends a track unless the budget is spent.</summary>
    /// <param name="tracks">The list being built.</param>
    /// <param name="track">The track.</param>
    /// <returns>Whether there was room for it.</returns>
    static bool Emit(List<GridTrackSize> tracks, GridTrackSize track) {
        if (tracks.Count >= LayoutLimits.MaximumGridTracks) {
            return false;
        }

        tracks.Add(track);
        return true;
    }
}
