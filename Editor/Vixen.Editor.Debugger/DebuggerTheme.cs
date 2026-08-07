// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Debugger;

/// <summary>The stylesheet the debugger's own elements come with.</summary>
/// <remarks>
///     ⚠ <b>It assumes <c>ProfilerTheme</c> is loaded and deliberately does not repeat it.</b> The
///     two assemblies' panels share a strip, a status line and the <c>parked</c> rule, and two
///     copies of those rules would be two places to change when the strip's height moves. Loading
///     this without the profiler's leaves the toolbars unstyled, which is why the editor loads both.
/// </remarks>
public static class DebuggerTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        debugger-status, remote-status {
            padding: 3px 8px;
            color: var(--text-muted);
            font-size: 0.85em;
            flex-shrink: 0;
        }

        /* ── The frame debugger ─────────────────────────────────────────────────
           Tree on the left, state on the right. A state pane under the tree would
           put the two things somebody is comparing a scroll apart. */
        debugger-body, remote-body {
            flex-direction: row;
            flex-grow: 1;
            flex-basis: 0px;
            min-height: 0px;
            gap: 0px;
        }

        debugger-body > tree-view, remote-body > tree-view {
            flex-grow: 1;
            flex-basis: 0px;
            min-width: 0px;
        }

        /* ⚠ A class rather than a tag, because the state pane is a `KeyValueList` now and its tag is
           the control's. What is left here is the pane's place in the debugger's layout — how wide
           it is and which edge it is against — and the six rules that used to draw a row are gone:
           two columns, a heading class and a pooling loop were the control's job and never this
           file's. Its own `min-width: 180px` is what keeps the halves readable in a narrow dock;
           below that the key column clips, which is the control's answer and not a new one. */
        .debugger-state, remote-counters {
            width: 44%;
            min-width: 180px;
            flex-shrink: 0;
            flex-direction: column;
            overflow: hidden;
            border-left-width: 1px;
            border-color: var(--border);
        }

        /* The one thing the shared row does not say: a group heading in a capture has air above it,
           because the groups are what somebody scans the pane for. */
        .debugger-state key-value-row.heading { margin-top: 6px; }

        /* ── The remote inspector ───────────────────────────────────────────── */
        remote-counter {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 2px 8px;
            min-height: 20px;
        }

        counter-label { flex-grow: 1; color: var(--text-muted); overflow: hidden; }
        counter-value { width: 90px; flex-shrink: 0; text-align: right; }

        remote-write {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 4px 6px;
            flex-shrink: 0;
            border-top-width: 1px;
            border-color: var(--border);
        }

        remote-write > text-box { flex-grow: 1; min-width: 60px; }

        /* ── The device manager ─────────────────────────────────────────────── */
        device-manager > data-grid { flex-grow: 1; flex-basis: 0px; min-height: 0px; }
        """;
}
