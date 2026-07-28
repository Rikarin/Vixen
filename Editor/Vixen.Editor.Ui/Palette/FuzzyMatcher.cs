// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>Scores how well a typed fragment matches a candidate.</summary>
/// <remarks>
///     <para>
///         <b>Subsequence matching with position bonuses</b> — the shape every editor's palette
///         uses, because it is what makes <c>rl</c> find "Reset Layout" and <c>opj</c> find "Open
///         Project". A substring search would find neither, and a search that ignored order would
///         find everything.
///     </para>
///     <para>
///         ⚠ <b>The bonuses are what make it usable, not the matching.</b> Every candidate
///         containing the letters in order matches; the ranking is the product. A hit at the start
///         of a word beats one in the middle, a run of adjacent hits beats the same letters spread
///         out, and a shorter candidate beats a longer one that matched equally well — so <c>save</c>
///         puts "Save" above "Save All" above "Autosave Interval" rather than in whatever order the
///         registry happened to be in.
///     </para>
///     <para>
///         ⚠ <b>The scan is greedy and takes the first match for each query character.</b> Optimal
///         alignment is a dynamic program over the whole string, and for candidates that are two or
///         three words long the difference never shows — while the cost does, on every keystroke,
///         against every command, asset and setting in the project.
///     </para>
/// </remarks>
public static class FuzzyMatcher {
    /// <summary>What a candidate that does not match at all scores.</summary>
    public const int NoMatch = int.MinValue;

    /// <summary>Scores a candidate against a query.</summary>
    /// <param name="query">What was typed. Case and spaces are ignored.</param>
    /// <param name="candidate">What it might be.</param>
    /// <returns>A score, higher being better, or <see cref="NoMatch" />.</returns>
    /// <remarks>An empty query matches everything with a score of zero, which is what leaves the
    ///     palette showing its list in the order the registry gave it.</remarks>
    public static int Score(string? query, string? candidate) {
        if (string.IsNullOrEmpty(query)) {
            return 0;
        }

        if (string.IsNullOrEmpty(candidate)) {
            return NoMatch;
        }

        var score = 0;
        var previous = -2;
        var index = 0;

        foreach (var wanted in query) {
            if (char.IsWhiteSpace(wanted)) {
                continue;
            }

            var found = -1;

            for (var i = index; i < candidate.Length; i++) {
                if (char.ToUpperInvariant(candidate[i]) != char.ToUpperInvariant(wanted)) {
                    continue;
                }

                found = i;
                break;
            }

            if (found < 0) {
                return NoMatch;
            }

            score += Bonus(candidate, found, previous, wanted);

            previous = found;
            index = found + 1;
        }

        // ⚠ Length is a penalty rather than the tiebreak it looks like. Without it "Save" and
        // "Save Scene As…" score identically for `save` — every query character matched at a word
        // start in both — and the one the user meant is decided by dictionary order.
        return score - (candidate.Length / 4);
    }

    /// <summary>Whether a candidate matches at all.</summary>
    /// <param name="query">What was typed.</param>
    /// <param name="candidate">What it might be.</param>
    /// <returns>Whether it does.</returns>
    public static bool Matches(string? query, string? candidate) => Score(query, candidate) != NoMatch;

    static int Bonus(string candidate, int at, int previous, char wanted) {
        var score = 1;

        if (at == previous + 1) {
            // A run. Worth more than a word start, because `sav` matching three adjacent letters is
            // a stronger signal than three letters that each happen to begin a word.
            score += 8;
        }

        if (at == 0 || candidate[at - 1] is ' ' or '.' or '/' or '_' or '-') {
            score += 6;
        } else if (char.IsUpper(candidate[at]) && char.IsLower(candidate[at - 1])) {
            // A camel-case hump is a word start in an identifier, which is what an asset path and a
            // setting key are mostly made of.
            score += 5;
        }

        if (candidate[at] == wanted) {
            score += 1;
        }

        return score;
    }
}
