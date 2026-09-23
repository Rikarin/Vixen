// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling.Testing;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The scoped half of #531 for the controls' own sheets: whether each whole type-only selector
///     <c>ControlTheme.vcss</c> and <c>AdvancedTheme.vcss</c> declare matches anything a bare control
///     builds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The editor asks this question and could not answer it for these rows.</b>
///         <c>EditorCombinatorPairTests</c>' scoped census reads <c>-</c> for <c>radio box dot</c>,
///         <c>switch track knob</c>, <c>stepper icon-button</c> and the rest of the control theme's
///         descendant rules, because the editor never builds those controls bare — and its own
///         header says the controls' live sweep is where they are provable and "does not ask this
///         question yet". This asks it, of the same trees the pair sweep already grows, through the
///         same matcher (<c>UiTest.Get</c>, the cascade's own compiler), so a <c>-</c> there and a
///         verdict here are answers to one question from two sets of trees.
///     </para>
///     <para>
///         ⚠ <b>Bare controls only, and the seeded sweep is deliberately not asked.</b> A pairing
///         can refuse the elements a seed introduced, because a pairing names its child; a whole
///         selector can match THROUGH a seeded element at any compound, so the same refusal would
///         need a matcher that reports which element stood for each compound. What a bare control
///         builds is the control's alone, which is the standing the pair census has too.
///     </para>
///     <para>
///         ⚠ <b>The domain is the controls' sheets and nothing else.</b> Every editor selector would
///         read <c>-</c> here by construction — the editor's elements are not in this assembly — and
///         a census that is mostly rows this sweep cannot reach is the shape nobody reads. The
///         selector column is shared with the editor's through <see cref="TypeOnlySelectors" />, and
///         <c>CombinatorCensusDriftTests</c> in <c>Vixen.Ui.Styling.Tests</c> holds this file's
///         column against the sheets too, so a control-sheet edit is red where its author is.
///     </para>
///     <para>
///         ⚠ <b>A <c>-</c> row is UNJUDGED here, never dead</b> — a part built only with content, or a
///         control that only exists inside another one, is unreached by a bare sweep exactly as it is
///         by the editor's ladder. The loud direction is a verdict leaving <c>Bare</c>: the rule still
///         stands and no bare control builds anything under the scope it names any more.
///     </para>
/// </remarks>
public partial class LiveCombinatorPairTests {
    /// <summary>The scoped census: every type-only selector the controls' sheets declare, and whether a bare control matched it.</summary>
    const string ScopedFile = "Core/Vixen.Ui.Controls.Advanced.Tests/ControlScopedSelectors.txt";

    /// <summary>The verdict for a selector some bare control's tree matched.</summary>
    const string BareVerdict = "Bare";

    /// <summary>The verdict for a selector no bare control's tree matched.</summary>
    const string Unmatched = "-";

    /// <summary>The two sheets whose selectors a control sweep can answer for.</summary>
    static readonly string[] ControlSheets = [
        "Core/Vixen.Ui.Controls/ControlTheme.vcss",
        "Core/Vixen.Ui.Controls.Advanced/AdvancedTheme.vcss"
    ];

    /// <summary>
    ///     Two selectors a bare control's tree must match and their mirrors, which it must not —
    ///     named so that a scan matching nothing, or matching the wrong way round, cannot pass.
    /// </summary>
    /// <remarks>
    ///     A child chain and a descendant chain, each three compounds, each the far end of parts a
    ///     control builds with nothing done to it. The mirrors are asked of every tree alongside the
    ///     domain, because a mirror that was never asked is absent from the matched set whatever the
    ///     matcher would have said.
    /// </remarks>
    static readonly (string Selector, string Mirror)[] ScopedAnchors = [
        ("radio box dot", "dot box radio"),
        ("switch track knob", "knob track switch")
    ];

    /// <summary>The domain: every type-only selector the two control sheets declare.</summary>
    static IReadOnlyDictionary<string, string> ScopedDomain => scopedDomain ??= ReadScopedDomain();

    static IReadOnlyDictionary<string, string>? scopedDomain;

    /// <summary>Every selector some bare control's tree matched, filled by <see cref="Scope" /> as the sweep runs.</summary>
    static readonly HashSet<string> ScopedMatched = new(StringComparer.Ordinal);

    /// <summary>How many selector questions the sweep put to a matcher, counted where it answered.</summary>
    static int scopedAsked;

    static Dictionary<string, string> ReadScopedDomain() =>
        TypeOnlySelectors.Read(Root())
            .Where(static row => ControlSheets.Contains(row.Value, StringComparer.Ordinal))
            .ToDictionary(static row => row.Key, static row => row.Value, StringComparer.Ordinal);

    /// <summary>Asks one built control's document about every selector in the domain, and the anchors' mirrors.</summary>
    /// <remarks>
    ///     ⚠ Through <see cref="UiTest.Get" />, which is the cascade's own compiler and matcher, for the
    ///     editor partial's reason: a second matcher written for the test would agree on <c>a &gt; b</c>
    ///     and disagree on <c>a b c</c>, silently. The harness is adopted and never disposed — the
    ///     fixture owns the document and disposes it.
    /// </remarks>
    static void Scope(UiDocument document) {
        var test = UiTest.Adopt(document);

        foreach (var selector in ScopedDomain.Keys.Concat(ScopedAnchors.Select(static anchor => anchor.Mirror))) {
            if (test.Get(selector).Count > 0) {
                ScopedMatched.Add(selector);
            }

            scopedAsked++;
        }
    }

    /// <summary>The premise: the domain is the real one, every tree was asked all of it, and the anchors hold.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is taken where the matcher answers</b>, which is the instrument defect the
    ///     editor's scoped sweep had until a later pass: comparing the domain's size with a number
    ///     assigned from the domain's size is a predicate whose only false case is the sweep never
    ///     running. Here a <c>continue</c> added inside the loop is a count that stops matching.
    /// </remarks>
    [Fact]
    public void The_scoped_control_scan_actually_ran() {
        _ = Observed;

        Assert.True(ScopedDomain.Count >= 30, $"the control sheets declare only {ScopedDomain.Count} type-only selectors, which is not the real table.");

        Assert.Equal((ScopedDomain.Count + ScopedAnchors.Length) * Built, scopedAsked);

        foreach (var (selector, mirror) in ScopedAnchors) {
            Assert.Contains(selector, ScopedDomain.Keys);
            Assert.Contains(selector, ScopedMatched);
            Assert.DoesNotContain(mirror, ScopedMatched);
        }
    }

    /// <summary>The scoped census is exactly what is committed, verdict for verdict.</summary>
    /// <remarks>
    ///     Exact in both directions and regenerable, like the pair census beside it. ⚠ A verdict moving
    ///     from <c>Bare</c> to <c>-</c> is the loud one and is listed first; a selector arriving or
    ///     leaving a sheet, or a <c>-</c> becoming <c>Bare</c>, is regenerated after reading.
    /// </remarks>
    [Fact]
    public void Every_type_only_control_selector_is_in_the_scoped_census_with_its_verdict() {
        _ = Observed;

        var path = Path.Combine(Root(), ScopedFile);

        var measured = ScopedDomain.Keys
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                static selector => selector,
                static selector => ScopedMatched.Contains(selector) ? BareVerdict : Unmatched,
                StringComparer.Ordinal
            );

        if (Regenerating) {
            Write(path, measured.Select(static row => $"{row.Key}\t{row.Value}"));
        }

        var census = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in Rows(path, ScopedFile)) {
            var columns = row.Split('\t');

            Assert.True(
                columns.Length == 2 && columns[1] is BareVerdict or Unmatched,
                $"{ScopedFile} is malformed at '{row}'. Each row is `selector<TAB>Bare-or-dash`."
            );

            Assert.True(census.TryAdd(columns[0].Trim(), columns[1].Trim()), $"'{columns[0]}' is listed twice in {ScopedFile}.");
        }

        var lost = measured.Where(row => census.GetValueOrDefault(row.Key) == BareVerdict && row.Value == Unmatched)
            .Select(static row => row.Key)
            .ToList();

        var gained = measured.Where(row => census.GetValueOrDefault(row.Key) == Unmatched && row.Value == BareVerdict)
            .Select(static row => row.Key)
            .ToList();

        var arrived = measured.Keys.Where(selector => !census.ContainsKey(selector)).ToList();
        var departed = census.Keys.Where(selector => !measured.ContainsKey(selector)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            lost.Count == 0 && gained.Count == 0 && arrived.Count == 0 && departed.Count == 0,
            $"""
             The controls' scoped census is out of date.

             ⚠ Matched by a bare control before and by none now — the sheet still declares each of
             these and no control builds anything under the scope it names any more:
             {Lines(lost)}

             Matched now and recorded as unmatched — regenerate once you have read them:
             {Lines(gained)}

             Declared by a control sheet and not in {ScopedFile}:
             {Lines(arrived)}

             In {ScopedFile} and declared by no control sheet any more:
             {Lines(departed)}

             Re-run with VIXEN_REGENERATE=1 to write this back, after reading the first list.
             """
        );
    }
}
