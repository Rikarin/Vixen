// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vixen.Ui.Layout;

/// <summary>Where a track list's one automatic repetition sits, if it has one.</summary>
/// <param name="Kind">Which kind of automatic repetition, or <see cref="GridAutoRepeat.None" />.</param>
/// <param name="Index">Where the single written-out repetition begins, or −1.</param>
/// <param name="Count">How many tracks one repetition holds, or zero.</param>
public readonly record struct GridAutoRepeatSpan(GridAutoRepeat Kind, int Index, int Count) {
    /// <summary>A list with no automatic repetition.</summary>
    public static GridAutoRepeatSpan None => new(GridAutoRepeat.None, -1, 0);
}

/// <summary>Reads a CSS <c>&lt;track-list&gt;</c> into the tracks <see cref="LayoutTree" /> stores.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the inverse of <see cref="GridTrackSize.ToString" />, which is why it lives
///         here rather than in the styling assembly.</b> <c>Vixen.Ui.Styling</c> resolves declarations
///         without knowing what a length measures and cannot see a <see cref="GridTrackSize" /> at
///         all; <c>Vixen.Ui</c>, the bridge, can — but so can the layout conformance corpus, and only
///         one of those two can be true of a parser that both of them must agree with. A type that
///         already emits <c>minmax(0,1fr)</c> owns the reading of it too, and the alternative is two
///         grammars that drift.
///     </para>
///     <para>
///         ⚠ <b>That the corpus and the stylesheet share this grammar is the point, not a convenience.</b>
///         Taffy's 1 526 grid fixtures reach the store through <c>TaffyStyleMap</c> and never touch
///         CSS, so a grammar written only for the stylesheet would have no adversarial coverage at
///         all — and this one is attacked by <c>repeat(40000, 10px 10px)</c> and by a single 84 KB
///         attribute holding twenty-one thousand longhand tracks. Both callers parse with these
///         lines, so a fixture that would break the stylesheet breaks the corpus first.
///     </para>
///     <para>
///         ⚠ <b>It is a tokeniser and not a <c>Split</c>, because both of the separators a track list
///         uses also occur inside its functions.</b> <c>minmax(0px,1fr)</c> holds a comma and
///         <c>repeat(auto-fill, 1px 1px)</c> holds spaces, so splitting on either at the top level
///         cuts through a function argument — and the corpus contains
///         <c>40px repeat(1, 40px) repeat(auto-fill, 40px)</c>, which needs both separators read
///         correctly in one string. A depth counter over the parentheses is the whole of the fix.
///     </para>
///     <para>
///         ⚠ <b>A fixed <c>repeat(N, …)</c> is expanded here and an automatic one is not, and that
///         asymmetry is CSS Grid §7.2.3.2 rather than a shortcut.</b> <c>repeat(4, 10px)</c> is
///         defined as a textual expansion — it means exactly <c>10px 10px 10px 10px</c> and nothing
///         downstream can tell the two apart. <c>repeat(auto-fill, …)</c> is not: the number of
///         repetitions is computed from the container's own definite size, so it changes when the box
///         is resized without the declaration changing at all. Expanding it here would freeze one
///         frame's answer into the style.
///     </para>
///     <para>
///         Anything outside the grammar — named lines, <c>subgrid</c>, <c>masonry</c>, <c>calc()</c>,
///         <c>none</c> — is <i>refused</i> rather than skipped, and a refusal carries the token that
///         caused it. Silence is the one behaviour a track list must not have: a list that half-parses
///         becomes a one-column grid, which looks like a layout bug in a panel rather than a typo in a
///         stylesheet, and nothing anywhere says which.
///     </para>
/// </remarks>
public static class GridTrackList {
    /// <summary>Reads a track list.</summary>
    /// <param name="value">The declaration's value, verbatim.</param>
    /// <param name="tracks">Receives the tracks. Cleared first.</param>
    /// <param name="repeat">Receives where the automatic repetition sits, if there is one.</param>
    /// <param name="refusal">Receives the token that could not be read, when this returns false.</param>
    /// <returns>Whether the whole list was understood.</returns>
    /// <remarks>
    ///     ⚠ <b>The caller supplies the list, because the bridge parses once per restyled element and
    ///     a grid-heavy panel would otherwise allocate one per element per change.</b> A refusal
    ///     leaves <paramref name="tracks" /> holding whatever had been read before the bad token,
    ///     which no caller may use — the declaration is dropped whole, per CSS's rule for an invalid
    ///     value in an otherwise valid declaration.
    /// </remarks>
    public static bool TryParse(
        string value,
        List<GridTrackSize> tracks,
        out GridAutoRepeatSpan repeat,
        [NotNullWhen(false)] out string? refusal
    ) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(tracks);

        tracks.Clear();
        repeat = GridAutoRepeatSpan.None;
        refusal = null;

        // ⚠ The grammar below signals a bad token by throwing, and this is the only place that is
        // visible. Converting every arm to a `Try` return would thread an `out` through six mutually
        // recursive helpers whose logic is exactly what Taffy's 1 526 grid fixtures certify; the
        // control flow is kept identical to the proven version on purpose, and the cost is paid only
        // on the malformed path, which is a stylesheet typo rather than a frame.
        try {
            repeat = Parse(value, tracks);
            return true;
        } catch (RefusedException refused) {
            refusal = refused.Message;
            return false;
        }
    }

    static GridAutoRepeatSpan Parse(string value, List<GridTrackSize> tracks) {
        var kind = GridAutoRepeat.None;
        var autoRepeatIndex = -1;
        var autoRepeatCount = 0;

        foreach (var item in Items(value)) {
            if (!item.StartsWith("repeat(", StringComparison.Ordinal)) {
                Emit(tracks, TrackSize(item));
                continue;
            }

            var (counter, repetition) = Repeat(item);

            switch (counter) {
                case "auto-fill" or "auto-fit": {
                    // CSS Grid §7.2.3.2 permits at most one automatic repetition per axis.
                    if (kind != GridAutoRepeat.None) {
                        throw new RefusedException("two automatic repetitions");
                    }

                    // ⚠ Either the whole repetition is written out or none of it is. Half a
                    // repetition is not a smaller grid, it is a different declaration — and it would
                    // leave the store with an AutoRepeatCount that runs off the end of its tracks.
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
                        throw new RefusedException($"repeat({counter}, …)");
                    }

                    for (var repetitionIndex = 0;
                        repetitionIndex < times && tracks.Count < LayoutLimits.MaximumGridTracks;
                        repetitionIndex++) {
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

        // ⚠ An empty result is NOT refused here, and the asymmetry is deliberate. `grid-auto-columns`
        // documents an empty list as meaning `auto`, and the corpus feeds whitespace-only values
        // through expecting exactly that — so refusing here would change the answer for fixtures
        // this grammar is supposed to leave alone. A stylesheet wants the opposite (an empty
        // `grid-template-columns` is a typo, not a template), so that judgement belongs to the
        // caller, which knows which of the two it is. See the bridge's own empty check.
        return new GridAutoRepeatSpan(kind, autoRepeatIndex, autoRepeatCount);
    }

    // ── Tokenising ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Splits a track list on the spaces that are not inside a function's parentheses.</summary>
    static List<string> Items(string value) {
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
                        throw new RefusedException(value);
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
            throw new RefusedException(value);
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
    static (string Counter, List<GridTrackSize> Repetition) Repeat(string item) {
        if (!item.EndsWith(')')) {
            throw new RefusedException(item);
        }

        var body = item["repeat(".Length..^1];
        var comma = body.IndexOf(',');

        if (comma < 0) {
            throw new RefusedException(item);
        }

        var written = Items(body[(comma + 1)..]);
        if (written.Count == 0) {
            throw new RefusedException(item);
        }

        var repetition = new List<GridTrackSize>(written.Count);
        foreach (var track in written) {
            repetition.Add(TrackSize(track));
        }

        return (body[..comma].Trim(), repetition);
    }

    // ── The grammar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One <c>&lt;track-size&gt;</c>: a breadth, a <c>minmax()</c> or a <c>fit-content()</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>minmax()</c> whose minimum exceeds its maximum is parsed faithfully and not
    ///     repaired here.</b> <c>minmax(auto,10px)</c> and <c>minmax(max-content,10px)</c> both occur
    ///     in the corpus, and §12.4 is explicit that the growth limit is raised to the base size when
    ///     it ends up below it — a step of the sizing algorithm operating on resolved numbers, not a
    ///     syntactic fixup. Clamping at parse time would resolve <c>max-content</c> against nothing.
    /// </remarks>
    static GridTrackSize TrackSize(string item) {
        if (item.StartsWith("minmax(", StringComparison.Ordinal)) {
            if (!item.EndsWith(')')) {
                throw new RefusedException(item);
            }

            var body = item["minmax(".Length..^1];
            var comma = body.IndexOf(',');

            if (comma < 0) {
                throw new RefusedException(item);
            }

            return GridTrackSize.MinMax(Breadth(body[..comma].Trim()), Breadth(body[(comma + 1)..].Trim()));
        }

        if (item.StartsWith("fit-content(", StringComparison.Ordinal)) {
            if (!item.EndsWith(')')) {
                throw new RefusedException(item);
            }

            var argument = item["fit-content(".Length..^1].Trim();

            // §7.2.2's argument is a <length-percentage> only — a keyword there is not fit-content.
            if (argument.EndsWith("px", StringComparison.Ordinal)) {
                return GridTrackSize.FitContent(Number(argument[..^2]), isPercent: false);
            }

            if (argument.EndsWith('%')) {
                return GridTrackSize.FitContent(Number(argument[..^1]), isPercent: true);
            }

            throw new RefusedException($"fit-content({argument})");
        }

        return GridTrackSize.Single(Breadth(item));
    }

    /// <summary>One <c>&lt;track-breadth&gt;</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A unitless <c>0</c> is a length, and leaving it out is not a theoretical gap.</b> CSS
    ///     Values §5 lets a zero length omit its unit, and <c>minmax(0, 1fr)</c> — written exactly
    ///     that way — is the single most common track in real stylesheets, because it is what
    ///     Tailwind's <c>grid-cols-*</c> expands to. Taffy's corpus never exercises it: its generator
    ///     emits <c>0px</c>, so all 1 526 fixtures pass with this arm missing and the first thing to
    ///     notice would have been a refused stylesheet. Only zero — a bare <c>1</c> is still not a
    ///     length, and reading it as one would make <c>grid-cols-3</c>'s old broken emission
    ///     (<c>grid-template-columns: 3</c>) parse into three points of nothing.
    /// </remarks>
    static GridSizingFunction Breadth(string token) =>
        token switch {
            "auto" => GridSizingFunction.Auto,
            "min-content" => GridSizingFunction.MinContent,
            "max-content" => GridSizingFunction.MaxContent,
            "0" => GridSizingFunction.Points(0f),
            _ when token.EndsWith("px", StringComparison.Ordinal) => GridSizingFunction.Points(Number(token[..^2])),

            // ⚠ Before the percentage arm, because `fr` is not a length and must never reach
            // StyleLength — see the remarks on GridSizingKind.
            _ when token.EndsWith("fr", StringComparison.Ordinal) => GridSizingFunction.Flex(Number(token[..^2])),
            _ when token.EndsWith('%') => GridSizingFunction.Percent(Number(token[..^1])),
            _ => throw new RefusedException(token)
        };

    /// <summary>Parses a number, refusing rather than throwing <see cref="FormatException" />.</summary>
    static float Number(string token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new RefusedException(token);

    /// <summary>Appends a track unless the budget is spent.</summary>
    static bool Emit(List<GridTrackSize> tracks, GridTrackSize track) {
        if (tracks.Count >= LayoutLimits.MaximumGridTracks) {
            return false;
        }

        tracks.Add(track);
        return true;
    }

    /// <summary>The token that stopped the parse, carried out to <see cref="TryParse" />.</summary>
    sealed class RefusedException(string token) : Exception(token);
}
