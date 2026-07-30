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

        debugger-state, remote-counters {
            width: 44%;
            min-width: 180px;
            flex-shrink: 0;
            flex-direction: column;
            overflow: hidden;
            border-left-width: 1px;
            border-color: var(--border);
        }

        state-line {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 2px 8px;
            min-height: 20px;
        }

        state-line.state-heading {
            margin-top: 6px;
            color: var(--text);
            border-bottom-width: 1px;
            border-color: var(--border);
        }

        state-line.state-row { color: var(--text-muted); }
        state-label { width: 45%; min-width: 90px; flex-shrink: 0; overflow: hidden; }

        /* Monospaced would be better and there is no such token; what is here is the
           next best thing — a fixed column, so two handles line up digit for digit. */
        state-value { flex-grow: 1; color: var(--text); overflow: hidden; }

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
