// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui.Composition;
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

    /// <summary>⚠ A binding that died is a row, and a document with none says zero rather than nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The reader for <c>UiDiagnostics.BrokenBindings</c>, which is the whole point of
    ///         counting it.</b> <a href="https://github.com/Rikarin/Vixen/issues/1109">#1109</a>'s
    ///         symptom is a panel that renders once and freezes, and the freeze is invisible in every
    ///         other row on this list — the counters all describe a document that is settled, because
    ///         it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both readings, and the zero is the load-bearing one.</b> A row that appeared only
    ///         when something broke would be a row nobody knows to look for and a panel nobody can
    ///         tell apart from one that has stopped reading the number. The second half is what the
    ///         first is measured against.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_broken_binding_is_reported_and_a_healthy_document_reports_zero() {
        using var fixture = new ControlFixture();
        using var subject = new UiDocument(400f, 300f);

        subject.Update();

        var panel = fixture.Add<DiagnosticsPanel>();
        panel.Subject = subject;
        panel.Refresh();

        Assert.Equal("0", Value(panel, "Broken bindings"));
        Assert.Null(Find(panel, "Last broken binding"));

        // ⚠ A real binding, built through `BuildContext` and run by the scheduler, rather than the
        // recorder poked by hand. A fixture that supplied its own input would be asserting that the
        // panel can print a number somebody handed it, which was never in doubt.
        BuildContext.Build<Freezes>(subject, subject.Root);
        subject.Effects.Flush();
        panel.Refresh();

        Assert.Equal("1", Value(panel, "Broken bindings"));

        var reported = Value(panel, "Last broken binding");

        Assert.NotNull(reported);
        Assert.Contains("DiagnosticsPanelTests.cs:", reported, StringComparison.Ordinal);
        Assert.Contains("<row>", reported, StringComparison.Ordinal);
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
    /// <summary>⚠ <c>Help</c>'s binding names the caller's file too, and it is tested here.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The third of the three helpers that construct an effect on the author's behalf</b>
    ///         — <c>Text</c> and <c>Use</c> are covered in <c>Vixen.Ui.Tests</c>. This one cannot be:
    ///         <c>Described</c> needs a description implementation, which <c>Vixen.Ui.Controls</c>
    ///         registers from a module initializer, so in an assembly referencing only
    ///         <c>Vixen.Ui</c> the call throws before it reaches a binding at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Forwarding is per-method and there is no compiler check on it.</b> A helper that
    ///         calls <c>Bind</c> without passing its own <c>[CallerFilePath]</c> compiles, runs, and
    ///         reports <c>BuildContext.cs</c> — silently, and only when something breaks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_help_binding_records_the_callers_file() {
        using var document = new UiDocument(200f, 200f);

        // Touches the assembly, which is what registers the description implementation.
        ControlTheme.Install(document);

        BuildContext.BuildInto(new Describes(), document, document.Root);
        document.Effects.Flush();

        var record = document.Diagnostics.LastBrokenBinding;

        Assert.NotNull(record);
        Assert.StartsWith("DiagnosticsPanelTests.cs:", record, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildContext.cs", record, StringComparison.Ordinal);
    }

    static KeyValueRow? Find(DiagnosticsPanel panel, string key) {
        for (var i = 0; i < panel.Rows.Count; i++) {
            if (panel.Rows.Rows[i].Key == key) {
                return panel.Rows.Rows[i];
            }
        }

        return null;
    }

    /// <summary>A component whose description binding throws.</summary>
    sealed class Describes : Component {
        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(null, "row");

            ctx.Help(row, Boom);
        }

        static object? Boom() => throw new InvalidOperationException("this binding is meant to throw.");
    }

    /// <summary>A row that binds its own text, which is the natural spelling and the trap.</summary>
    sealed class Freezes : Component {
        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(null, "row");

            ctx.Element(row, "slider");
            ctx.Bind(() => row.Text = "1.0");
        }
    }
}
