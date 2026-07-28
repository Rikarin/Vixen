// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>The stylesheet the editor's own chrome comes with.</summary>
/// <remarks>
///     <para>
///         A third user-agent sheet after <c>ControlTheme</c> and <c>AdvancedTheme</c>, on the same
///         terms and for the same reason: it draws what those two do not — the shell, the toolbar,
///         the status bar, the palette and the notification list — and it is written entirely
///         against their tokens, so recolouring the root recolours all three.
///     </para>
///     <para>
///         ⚠ <b><see cref="Install" /> loads this and neither of the others.</b> An editor needs all
///         three, in order, because this sheet reads <c>--surface</c>, <c>--border</c> and the rest,
///         and a custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>The editor's own tokens are declared here rather than added to the control set's.</b>
///         <c>--chrome</c> and <c>--chrome-sunken</c> are the tool-window greys an editor has and an
///         application does not, and putting them in <c>ControlTheme</c> would make every game that
///         ships a button carry the editor's palette.
///     </para>
/// </remarks>
public static class EditorTheme {
    /// <summary>Loads the sheet into a document.</summary>
    /// <param name="document">The document, which should already have the other two sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The sheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* ── Editor tokens ──────────────────────────────────────────────────────
           The greys a tool window has. Everything else the editor draws comes from
           the control set's tokens, which is what keeps one `root.dark` in charge
           of the whole application. */
        root {
            --chrome: #eceef1;
            --chrome-sunken: #e2e5ea;
            --chrome-text: #3c4350;
            --warning: #b7791f;
            --status-height: 24px;
        }

        root.dark {
            --chrome: #202329;
            --chrome-sunken: #17191d;
            --chrome-text: #aab2be;
            --warning: #e0b252;
        }

        /* ── Shell ──────────────────────────────────────────────────────────────
           A column: menu bar, toolbar, the docking host taking what is left, and a
           status bar. The host is the only one that grows. */
        editor-shell { flex-direction: column; flex-grow: 1; }

        editor-shell > menu-bar {
            flex-shrink: 0;
            padding: 2px 4px;
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
            background-color: var(--chrome);
        }

        toolbar {
            flex-direction: row;
            align-items: center;
            flex-shrink: 0;
            gap: 2px;
            padding: 3px 6px;
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
            background-color: var(--chrome);
        }

        toolbar separator { height: 16px; margin: 0px 4px; }

        editor-workspace { flex-direction: column; flex-grow: 1; flex-basis: 0px; }

        /* ── Status bar ─────────────────────────────────────────────────────────
           ⚠ `flex-shrink: 0` and a fixed height, because it is the one strip whose
           size must not follow its contents: a task title three words longer must
           not make the viewport above it jump. */
        status-bar {
            flex-direction: row;
            align-items: center;
            flex-shrink: 0;
            gap: 8px;
            height: var(--status-height);
            padding: 0px 8px;
            border-width: 1px 0px 0px 0px;
            border-color: var(--border);
            background-color: var(--chrome-sunken);
            color: var(--chrome-text);
            font-size: 0.9em;
        }

        status-message { flex-grow: 1; overflow: hidden; }
        status-bar progress-bar { width: 120px; }
        status-bar icon-button { padding: 0px 4px; }

        /* ── Background tasks ───────────────────────────────────────────────────
           Never a modal dialog — doc 11 is explicit — so this is a panel that
           happens to be over the status bar and takes no input from the rest of
           the window. */
        task-center { flex-direction: column; gap: 6px; min-width: 280px; padding: 8px; }
        task-center > text { color: var(--text-muted); }

        task-row { flex-direction: column; gap: 3px; padding: 4px 2px; }
        task-row > task-line { flex-direction: row; align-items: center; gap: 6px; }
        task-title { flex-grow: 1; }
        task-status { color: var(--text-muted); font-size: 0.9em; }
        task-row progress-bar { flex-grow: 1; }

        /* ── Notifications ──────────────────────────────────────────────────────*/
        notification-list { flex-direction: column; gap: 4px; min-width: 300px; padding: 8px; }
        notification-row { flex-direction: row; align-items: center; gap: 8px; padding: 4px 2px; }
        notification-row > text { flex-grow: 1; }
        notification-row.severity-warning { color: var(--warning); }
        notification-row.severity-error { color: var(--danger); }

        /* ── Command palette ────────────────────────────────────────────────────
           Absolutely positioned by the overlay, so the width is declared and the
           height is whatever ten rows come to. */
        command-palette {
            position: absolute;
            left: 0px;
            top: 0px;
            flex-direction: column;
            width: 560px;
            padding: 6px;
            gap: 4px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 8px;
            background-color: var(--surface-raised);
        }

        /* `.closed` is the control set's own rule and already hides it; nothing is
           needed here beyond not fighting it. */
        command-palette search-box { flex-shrink: 0; }
        palette-list { flex-direction: column; }
        palette-empty { padding: 8px 6px; color: var(--text-muted); }

        palette-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 5px 8px;
            border-width: 0px;
            border-radius: 4px;
            background-color: transparent;
            color: var(--text);
        }

        /* ⚠ `:checked` rather than `:hover` is what the highlight is, because the
           highlight is moved by the arrow keys while the pointer sits still. A
           hover rule as well would give two highlighted rows. */
        palette-row:checked { background-color: var(--accent); color: var(--accent-text); }
        palette-row.parked { display: none; }
        palette-row > text { flex-grow: 1; }
        palette-category { color: var(--text-muted); font-size: 0.85em; }
        palette-row:checked palette-category, palette-row:checked palette-detail { color: var(--accent-text); }
        palette-detail { color: var(--text-muted); font-size: 0.85em; }
        """;
}
