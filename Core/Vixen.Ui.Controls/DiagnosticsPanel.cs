// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>A panel that shows what a running document is doing, for whoever is building it.</summary>
/// <remarks>
///     <para>
///         Doc 13 calls a UI-debug view "the single most valuable tool for anyone building a UI in
///         this framework". <see cref="UiDiagnostics" /> is the half that reads a document; this is
///         the half that shows it, and the two are separate types because the aggregator is
///         <c>Vixen.Ui</c>'s and a control is not.
///     </para>
///     <para>
///         ⚠ <b>A control rather than an <c>IDiagnosticOverlay</c>, and the seam is the whole reason
///         why.</b> The other overlays report a frame the interface knows nothing about, so they live
///         where the frame does. Every number here is <em>about</em> a <see cref="UiDocument" /> —
///         and the one host whose whole job is drawing one, <c>UiApplication</c>, may not reference
///         <c>Vixen.Engine</c> at all, while the one production holder of a <c>DiagnosticOverlays</c>
///         cannot see <c>Vixen.Ui</c>. An overlay written for this would be registered nowhere, which
///         is this repository's commonest defect wearing a diagnostics badge. A control works in
///         <c>UiApplication</c>, in the editor and in any game that draws a document, and needs no
///         reference in either direction.
///     </para>
///     <para>
///         ⚠ <b>It reports the frame before this one, deliberately, and that is not a rounding
///         error.</b> A panel drawn into the document it describes <em>is</em> part of that document:
///         it has elements, it is styled, it is laid out, and so it moves <c>StylesApplied</c>,
///         <c>LayoutNodes</c> and the settling counters it is reporting. Call <see cref="Refresh" />
///         at the top of the frame, before the document restyles, and the numbers shown are the ones
///         the previous pass finished with — a frame old and self-consistent, which is the trade
///         <c>FrameStatsOverlay</c> already documents. Refreshing in the middle of a pass reports a
///         document half way through changing, including this panel's own churn.
///     </para>
///     <para>
///         ⚠ <b><see cref="Subject" /> is what makes it usable at all in the honest arrangement.</b>
///         A panel that could only describe its own document has no way to describe one without
///         perturbing it; pointing this at another document — a second <see cref="UiDocument" /> on
///         its own surface — makes the reading exact rather than merely consistent. The default is
///         the panel's own document, because that is the arrangement somebody debugging reaches for
///         first.
///     </para>
///     <para>
///         The rows are a <see cref="KeyValueList" />, which pools: a panel refreshed sixty times a
///         second rewrites text rather than building and discarding a few dozen elements to say what
///         it said last frame.
///     </para>
/// </remarks>
public sealed class DiagnosticsPanel : Control {
    KeyValueList list = null!;

    /// <inheritdoc />
    protected override string TagName => "diagnostics-panel";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The document this describes. Its own, unless another one is named.</summary>
    /// <remarks>
    ///     ⚠ Read at <see cref="Refresh" /> rather than held resolved, so a panel moved between
    ///     documents describes the one it is in now — and so that setting this needs no invalidation
    ///     of its own.
    /// </remarks>
    public UiDocument? Subject { get; set; }

    /// <summary>Where the element description comes from, in the subject's coordinates.</summary>
    /// <remarks>
    ///     ⚠ <b>A point rather than an element, because the question doc 13 asks is "what is under
    ///     the pointer".</b> Left unset, the element rows are absent rather than stale — a panel that
    ///     kept describing the last element the pointer crossed is a panel that lies about a document
    ///     whose layout has since moved.
    /// </remarks>
    public Vector2? Probe { get; set; }

    /// <summary>How many rows the last <see cref="Refresh" /> wrote.</summary>
    /// <remarks>
    ///     The instrument for this panel's own tests, and the reason they need no rendering: "it
    ///     refreshed" and "it wrote nothing" are otherwise the same picture from outside.
    /// </remarks>
    public int RowCount { get; private set; }

    /// <summary>The rows, as they are shown.</summary>
    public KeyValueList Rows => list;

    /// <summary>Reads the subject and rewrites the rows. Call it at the top of the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Pull rather than push, and there is no timer behind it.</b> A panel that subscribed
    ///     to the document would be touching the reactive graph to answer a question about it, and
    ///     that graph is single-threaded by contract — see <see cref="UiDiagnostics" />' own rule
    ///     that it reads and never samples. The host decides when a frame begins; this cannot.
    /// </remarks>
    public void Refresh() {
        var diagnostics = (Subject ?? Document).Diagnostics;
        var row = 0;

        Write(ref row, "Layout nodes", diagnostics.LayoutNodes);
        Write(ref row, "Styles resolved", diagnostics.StylesResolved);
        Write(ref row, "Styles applied", diagnostics.StylesApplied);
        Write(ref row, "Container scopes", diagnostics.ContainerScopesEntered);
        Write(ref row, "Style compactions", diagnostics.StyleCompactions);
        Write(ref row, "Settling passes", diagnostics.SettlingPasses);
        Write(ref row, "Settled", diagnostics.Settled ? "yes" : "no");

        // ⚠ The row worth reading first and the one no other number shows: one element moved and the
        // whole document re-cascaded is a defect, not a cost, and it is invisible in a total.
        Write(ref row, "Last pass", diagnostics.LastPassWasCold ? "cold" : "incremental");

        // ⚠ The pair, never one of them. A rebuild count alone is the same on an idle laptop and a
        // loaded one; what says an interface is wasteful is redrawing many times to produce one
        // picture, which is the gap between these two.
        Write(ref row, "Draw lists built", diagnostics.DrawListsBuilt);
        Write(ref row, "Draw lists changed", diagnostics.DrawListsChanged);

        // ⚠ "Nothing was invalidated" and "nobody was recording" are the same empty span, and a panel
        // that showed a zero for both would report success on the day it did not run. Which is why
        // `UiDiagnostics.RecordsRegions` exists, and why this says so in words instead.
        // ⚠ Through a local, because `RecordsRegions` is a `const` and a constant `if` makes the
        // other arm unreachable code — which this tree compiles as an error. The arm is not dead: it
        // is the one a Release build without `VIXEN_UI_DIAGNOSTICS` takes.
        var records = UiDiagnostics.RecordsRegions;

        if (records) {
            Write(ref row, "Dirty regions", diagnostics.DirtyRegions.Length);
            Write(ref row, "Regions recorded", diagnostics.RegionsRecorded);
        } else {
            Write(ref row, "Dirty regions", "not recorded in this build");
        }

        Describe(ref row, diagnostics);

        list.Trim(row);
        RowCount = row;
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        list = Part<KeyValueList>();
    }

    void Describe(ref int row, in UiDiagnostics diagnostics) {
        if (Probe is not { } probe || !diagnostics.TryDescribe(probe.X, probe.Y, out var element, out var box)) {
            return;
        }

        Write(ref row, "Under the pointer", element is null ? "nothing" : element.Tag);

        // The four CSS boxes as the aggregator hands them over, rather than as twelve edges. An
        // overlay that draws them draws four nested outlines, and turning edges into those is the
        // arithmetic that is easy to get wrong once per reader.
        Write(ref row, "Margin box", box.Margin);
        Write(ref row, "Border box", box.Border);
        Write(ref row, "Padding box", box.Padding);
        Write(ref row, "Content box", box.Content);
    }

    void Write(ref int row, string key, Rectangle value) =>
        Write(
            ref row,
            key,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{value.X:0.#}, {value.Y:0.#} · {value.Width:0.#} × {value.Height:0.#}"
            )
        );

    void Write(ref int row, string key, int value) =>
        Write(ref row, key, value.ToString(CultureInfo.InvariantCulture));

    void Write(ref int row, string key, string value) {
        var line = list.Row(row++);

        line.Key = key;
        line.Value = value;
    }
}
