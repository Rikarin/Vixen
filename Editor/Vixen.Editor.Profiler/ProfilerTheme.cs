// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Profiler;

/// <summary>The stylesheet the diagnostics panels' own elements come with.</summary>
/// <remarks>
///     <para>
///         A sheet after <c>ControlTheme</c>, <c>AdvancedTheme</c> and the editor's, on the terms
///         <c>InspectorTheme</c> is: everything below is written against tokens those declare, and a
///         custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>The eight flame colours are the only place this sheet invents a palette</b>, and they
///         are here rather than as computed colours in the view for two reasons. A theme has to be
///         able to choose its own eight — the dark set below is unreadable on a light background —
///         and a colour a stylesheet owns is one a game team can override without a fork.
///     </para>
///     <para>
///         ⚠ <b>They are hues of one lightness rather than eight arbitrary colours.</b> A chart whose
///         bars vary in brightness reads as though the bright ones matter, which is exactly the
///         wrong signal: colour here means "a different scope" and nothing else, so only the hue
///         moves.
///     </para>
/// </remarks>
public static class ProfilerTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var sheet = document.Load(Css, StyleOrigin.UserAgent);

        document.Load(Utilities, StyleOrigin.UserAgent);

        return sheet;
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A sheet of its own rather than a share of the editor's, and that is shape C
    ///         working rather than a duplication of it.</b> What
    ///         <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares is the
    ///         <i>tokens</i>; the scan and the output stay this project's, so the build stays
    ///         incremental and this assembly does not have to be rebuilt because a panel somewhere
    ///         else started using <c>gap-3</c>. Everything here is inside <c>@layer utilities</c>,
    ///         where document order decides nothing, so a dozen assemblies loading a dozen of these
    ///         behaves as one sheet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Loaded at the same origin as the sheet above, which is what keeps the layer
    ///         meaningful.</b> Origin is the cascade's first question and the layer only its second,
    ///         so a utility sheet loaded as <c>Author</c> here would beat every hand-written rule in
    ///         <c>Sheet</c> on origin alone — the inversion <c>EditorTheme.Install</c> spells out at
    ///         length. It is loaded second so that a layering regression cannot hide behind source
    ///         order.
    ///     </para>
    /// </remarks>
    public static string Utilities => VixenUtilityStyles.Utilities;

    const string Sheet = """
        /* ── Shared ─────────────────────────────────────────────────────────────
           Every panel here is a strip, a body and sometimes a detail line. The
           strips are the console's, deliberately: three diagnostics panels whose
           toolbars were three different heights would look like three plugins. */
        profiler-view, memory-view, statistics-view, gpu-timeline, frame-debugger,
        remote-inspector, device-manager {
            flex-direction: column;
            flex-grow: 1;
            flex-basis: 0px;
            min-height: 0px;
            gap: 0px;
        }

        profiler-toolbar, memory-toolbar, statistics-toolbar, debugger-toolbar, remote-toolbar {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 4px 6px;
            flex-shrink: 0;
            border-bottom-width: 1px;
            border-color: var(--border);
        }

        profiler-toolbar select { width: 150px; flex-shrink: 0; }

        profiler-status, gpu-status, memory-status {
            padding: 3px 8px;
            color: var(--text-muted);
            font-size: 0.85em;
            flex-shrink: 0;
        }

        .parked { display: none; }

        /* ── The flame chart ────────────────────────────────────────────────────
           Relative, because every bar is absolutely positioned against it — the
           chart's own height is set from code once the deepest row is known. */
        profiler-body { flex-grow: 1; flex-basis: 0px; min-height: 0px; }
        profiler-body > scroll-view { flex-grow: 1; flex-basis: 0px; min-height: 0px; }

        flame-chart {
            position: relative;
            width: 100%;
            min-height: 18px;
        }

        flame-bar {
            position: absolute;
            border-radius: 2px;
            padding: 0px 4px;
            overflow: hidden;
            font-size: 0.8em;
            color: var(--surface-sunken);
            align-items: center;
        }

        flame-bar:hover { border-width: 1px; border-color: var(--text); }

        /* The selected bar keeps its hue and gains an outline, rather than turning
           the accent colour: losing the hue is losing the one thing that says which
           scope you are looking at across a zoom. */
        flame-bar:checked { border-width: 1px; border-color: var(--accent-text); }

        flame-hue-0 { background-color: #6ea8fe; }
        flame-hue-1 { background-color: #79d99a; }
        flame-hue-2 { background-color: #e6c15c; }
        flame-hue-3 { background-color: #e08a6b; }
        flame-hue-4 { background-color: #b191e0; }
        flame-hue-5 { background-color: #5fc9c1; }
        flame-hue-6 { background-color: #d98ab5; }
        flame-hue-7 { background-color: #9aae7a; }

        profiler-detail {
            padding: 4px 8px;
            color: var(--text-muted);
            font-size: 0.85em;
            flex-shrink: 0;
            border-top-width: 1px;
            border-color: var(--border);
        }

        /* A third of the panel, so the chart above it stays the larger half —
           the table is what you read second. */
        profiler-view > data-grid { height: 34%; min-height: 80px; flex-shrink: 0; }

        /* ── The GPU timeline ───────────────────────────────────────────────── */
        gpu-lanes { position: relative; width: 100%; padding: 4px 0px; }

        gpu-bar {
            position: absolute;
            border-radius: 2px;
            padding: 0px 4px;
            overflow: hidden;
            font-size: 0.8em;
            color: var(--surface-sunken);
            align-items: center;
        }

        /* ── Memory ─────────────────────────────────────────────────────────── */
        memory-view > scroll-view { flex-grow: 1; flex-basis: 0px; min-height: 0px; }

        memory-line {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 2px 8px;
            min-height: 20px;
        }

        memory-line.memory-heading {
            margin-top: 8px;
            color: var(--text);
            border-bottom-width: 1px;
            border-color: var(--border);
        }

        memory-line.memory-row { color: var(--text-muted); }
        memory-label { width: 200px; flex-shrink: 0; overflow: hidden; }

        /* Right-aligned and fixed, because a column of magnitudes is read by
           comparing digit positions and a ragged one cannot be. */
        memory-value { width: 110px; flex-shrink: 0; text-align: right; color: var(--text); }
        memory-detail { flex-grow: 1; font-size: 0.85em; overflow: hidden; }

        /* ── Statistics ─────────────────────────────────────────────────────── */
        statistics-body { flex-direction: column; padding: 4px 0px; }

        statistics-warnings {
            flex-direction: column;
            gap: 2px;
            padding: 6px 8px;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-bottom-width: 1px;
            border-color: var(--border);
        }

        statistic-warning { color: var(--warning); font-size: 0.85em; }

        statistic-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 3px 8px;
            min-height: 24px;
        }

        statistic-label { width: 170px; flex-shrink: 0; color: var(--text-muted); }
        statistic-value { width: 170px; flex-shrink: 0; text-align: right; }

        /*
         * ⚠ The budget bar is a `ProgressBar` and was a `statistic-track` holding a
         * `statistic-fill` whose width was written per row with `SetStyle`. Markup cannot write an
         * inline declaration — `class` and `binding-path` are the only universal attributes, and a
         * `style="…"` lands in the selector engine's attribute arena rather than in the cascade — so
         * the port had to reach for the control that was always the right one. `min-width` is
         * overridden because the control library's own rule floors a progress bar at 80px for a
         * dialog, and this is a column.
         */
        statistic-row progress-bar {
            width: 120px;
            min-width: 0px;
            flex-shrink: 0;
            --track-color: var(--surface-sunken);
            --fill-color: var(--accent);
        }

        statistic-row.near progress-bar { --fill-color: var(--warning); }
        statistic-row.over progress-bar { --fill-color: var(--danger); }
        statistic-row.near statistic-value { color: var(--warning); }
        statistic-row.over statistic-value { color: var(--danger); }
        statistic-detail { flex-grow: 1; color: var(--text-muted); font-size: 0.85em; overflow: hidden; }
        """;
}
