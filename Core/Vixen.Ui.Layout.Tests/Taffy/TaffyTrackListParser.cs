// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Reads one CSS <c>&lt;track-list&gt;</c> for the corpus, in the corpus's own failure vocabulary.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The grammar itself is no longer here.</b> It moved to
///         <see cref="GridTrackList" /> in the layout assembly so that the stylesheet bridge and this
///         corpus read a track list with the same lines. That matters more than it looks: every one
///         of the corpus's 1 526 passing grid fixtures arrives through <c>TaffyStyleMap</c> and never
///         touches CSS, so a second grammar written for the stylesheet would have had no adversarial
///         coverage at all — no <c>repeat(40000, 10px 10px)</c>, no 84 KB attribute of longhand
///         tracks, none of the <c>0fr</c> families. Sharing one grammar points all of that at the
///         stylesheet path too.
///     </para>
///     <para>
///         What is left here is the adapter: the corpus signals "this fixture uses something the
///         store has no field for" by throwing <see cref="TaffyUnsupportedException" />, which is
///         counted as a skip rather than a failure, and <see cref="GridTrackList.TryParse" /> reports
///         the same condition as a returned refusal because a stylesheet must not throw out of a
///         frame. Translating between the two is this file's whole job.
///     </para>
/// </remarks>
static class TaffyTrackListParser {
    /// <summary>Parses a track list, or refuses the fixture.</summary>
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

        if (!GridTrackList.TryParse(value, tracks, out var repeat, out var refusal)) {
            throw new TaffyUnsupportedException($"{property}: {refusal}");
        }

        return (tracks, repeat.Kind, repeat.Index, repeat.Count);
    }
}
