// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A reader for the numbers the document already publishes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here points the panel at a <em>second</em> document, and that is the
///         point rather than a convenience.</b> A panel drawn into the document it describes is part
///         of it — writing a row adds elements, which moves <c>LayoutNodes</c> and the styling
///         counters the row is reporting — so an assertion of equality against its own subject can
///         only be true up to its own churn. Against a second document the reading is exact, which
///         is what lets these be equalities rather than ranges.
///     </para>
///     <para>
///         And an equality against the aggregator rather than against a written-down number:
///         `UiDiagnostics` is where the truth is, so a fixture that hard-coded "12 layout nodes"
///         would be asserting the version of the control theme it was written against.
///     </para>
/// </remarks>
public class DiagnosticsPanelTests {
    /// <summary>The rows carry the subject's counters, not the panel's own document's.</summary>
    /// <remarks>
    ///     The two documents are deliberately different sizes of tree — the subject has a handful of
    ///     boxes added to it and the panel's host does not — so reading the wrong one is a different
    ///     number rather than a coincidence.
    /// </remarks>
    [Fact]
    public void The_rows_report_the_subject_document() {
        using var fixture = new ControlFixture();
        using var subject = new UiDocument(400f, 300f);

        for (var i = 0; i < 6; i++) {
            subject.Root.Add<UiElement>();
        }

        subject.Update();

        var panel = fixture.Add<DiagnosticsPanel>();
        panel.Subject = subject;
        panel.Refresh();

        Assert.Equal(
            subject.Diagnostics.LayoutNodes.ToString(CultureInfo.InvariantCulture),
            Value(panel, "Layout nodes")
        );

        Assert.NotEqual(
            fixture.Document.Diagnostics.LayoutNodes.ToString(CultureInfo.InvariantCulture),
            Value(panel, "Layout nodes")
        );
    }

    /// <summary>A cold pass says so in words, and an incremental one says the other word.</summary>
    /// <remarks>
    ///     ⚠ <b>The row no total can show.</b> "One element moved and the whole document
    ///     re-cascaded" is a defect rather than a cost, and it is invisible in
    ///     <c>StylesResolved</c> — a cold pass and a busy incremental one are both a large number.
    ///     The assertion is the pair, because "cold" alone is also what a panel that had stopped
    ///     reading the flag would print for ever.
    /// </remarks>
    [Fact]
    public void The_pass_kind_is_reported_and_changes() {
        using var fixture = new ControlFixture();
        using var subject = new UiDocument(400f, 300f);

        var box = subject.Root.Add<UiElement>();

        subject.Load("root { color: red; }");
        subject.Update();

        var panel = fixture.Add<DiagnosticsPanel>();
        panel.Subject = subject;
        panel.Refresh();

        Assert.Equal("cold", Value(panel, "Last pass"));

        box.AddClass("warm");
        subject.Update();
        panel.Refresh();

        Assert.Equal("incremental", Value(panel, "Last pass"));
    }

    /// <summary>A probe adds the element under it and its four boxes; clearing it takes them away.</summary>
    /// <remarks>
    ///     ⚠ <b>The removal half is the one worth the test.</b> A pooled list that never shrinks
    ///     leaves the last element the pointer crossed on screen for ever, which is a panel that
    ///     lies about a document whose layout has since moved — and it looks exactly like a panel
    ///     that is working.
    /// </remarks>
    [Fact]
    public void A_probe_describes_the_element_under_it_and_is_forgotten_when_it_is_cleared() {
        using var fixture = new ControlFixture();
        using var subject = new UiDocument(400f, 300f);

        subject.Load("root { width: 400px; height: 300px; } panel { width: 100px; height: 50px; }");

        var box = subject.Root.Add<UiElement>("panel");

        subject.Update();

        var panel = fixture.Add<DiagnosticsPanel>();
        panel.Subject = subject;
        panel.Refresh();

        var plain = panel.Rows.Count;

        Assert.Null(Find(panel, "Border box"));

        panel.Probe = new Vector2(10f, 10f);
        panel.Refresh();

        Assert.True(panel.Rows.Count > plain);
        Assert.Equal(box.Tag, Value(panel, "Under the pointer"));

        Assert.Equal(
            $"{box.AbsoluteLeft:0.#}, {box.AbsoluteTop:0.#} · {box.Width:0.#} × {box.Height:0.#}",
            Value(panel, "Border box")
        );

        panel.Probe = null;
        panel.Refresh();

        Assert.Equal(plain, panel.Rows.Count);
        Assert.Null(Find(panel, "Border box"));
    }

    /// <summary>
    ///     A build that records no regions says so, rather than showing a zero that means two things.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own honesty row.</b> "Nothing was invalidated" and "nobody was
    ///     recording" are the same empty span, and a panel that printed <c>0</c> for both would
    ///     report success on the day it did not run — which is the failure mode
    ///     <c>UiDiagnostics.RecordsRegions</c> exists to make impossible. This asserts whichever half
    ///     the compilation is in, so it is a real assertion in both.
    /// </remarks>
    [Fact]
    public void The_region_row_distinguishes_nothing_recorded_from_nothing_happening() {
        using var fixture = new ControlFixture();
        using var subject = new UiDocument(400f, 300f);

        subject.Update();

        var panel = fixture.Add<DiagnosticsPanel>();
        panel.Subject = subject;
        panel.Refresh();

        // Through a local for the reason the panel itself does it: a constant `if` leaves the other
        // arm unreachable, and both arms are live across the configurations this file is built in.
        var records = UiDiagnostics.RecordsRegions;

        if (records) {
            Assert.NotNull(Find(panel, "Regions recorded"));
            Assert.DoesNotContain("not recorded", Value(panel, "Dirty regions") ?? string.Empty);
        } else {
            Assert.Null(Find(panel, "Regions recorded"));
            Assert.Equal("not recorded in this build", Value(panel, "Dirty regions"));
        }
    }

    static string? Value(DiagnosticsPanel panel, string key) => Find(panel, key)?.Value;

    /// <summary>The row with that key, among the rows the list is SHOWING.</summary>
    /// <remarks>
    ///     ⚠ <b>`Rows.Count` and not `RowCount`, and the difference is a whole sabotage.</b> The
    ///     panel's own count is what the last refresh wrote; the list's is what is on screen, which
    ///     includes any row a refresh wrote and a later one failed to retire. Searching the panel's
    ///     count made this file green with `KeyValueList.Trim` deleted — the stale row was still
    ///     shown and the instrument could not see past the number the defect had already moved.
    /// </remarks>
    static KeyValueRow? Find(DiagnosticsPanel panel, string key) {
        for (var i = 0; i < panel.Rows.Count; i++) {
            if (panel.Rows.Rows[i].Key == key) {
                return panel.Rows.Rows[i];
            }
        }

        return null;
    }
}
