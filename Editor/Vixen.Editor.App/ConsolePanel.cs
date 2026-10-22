// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>The console panel's model: what it shows, which outlives the panel, and the view while it is open.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2's rule for a panel's state, applied to the console (#1294).</b> The view
///         and its model were two fields on <c>EditorApplication</c>, and the panel's factory and the
///         three verbs that reach into it — Clear Console, Clear on Play, and play mode's own clear —
///         read them there. They are this model's now; the application builds it over the log and
///         registers the panel it describes.
///     </para>
///     <para>
///         ⚠ <b>One <see cref="ConsoleModel" />, made on first sight and kept, rather than one per
///         panel.</b> A panel's factory runs again every time it is reopened, and a fresh model starts
///         at the sink's current end, so closing and reopening the console would empty it. The
///         buffer, the filters and the collapse state are all the user's and survive.
///     </para>
///     <para>
///         ⚠ <b>The view is forgotten when the panel closes, which the old field's summary claimed
///         and nothing did.</b> It said "while it is open, or <see langword="null" />", and the
///         registration had no <c>Closed</c>: View ▸ Clear Console and Play ▸ Clear Console stayed
///         enabled over a panel that had gone, and the second showed a tick for a toggle nobody could
///         see.
///     </para>
/// </remarks>
sealed class ConsolePanel {
    /// <summary>The panel's id, which a saved arrangement carries it back by.</summary>
    public const string PanelId = "console";

    readonly RingBufferSink sink;

    /// <summary>What it is showing, which outlives the panel. Made the first time the panel opens.</summary>
    ConsoleModel? model;

    /// <summary>Builds the model over the ring the editor's log writes into.</summary>
    /// <param name="sink">The ring.</param>
    public ConsolePanel(RingBufferSink sink) => this.sink = sink;

    /// <summary>The console's view while the panel is open, or <see langword="null" />.</summary>
    public ConsoleView? View { get; private set; }

    /// <summary>Whether the open console throws itself away when play mode is entered.</summary>
    /// <remarks>The view owns the preference — its toggle writes it — and the application owns the moment.</remarks>
    public bool ClearsOnPlay => View is { ClearsOnPlay: true };

    /// <summary>Describes the panel.</summary>
    /// <param name="shell">The shell, whose context a press in the panel claims.</param>
    /// <param name="context">The context id a press in the panel claims.</param>
    /// <param name="activated">What activating a row does — the application's source opener.</param>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor Describe(EditorShell shell, string context, Action<LogRecord> activated) =>
        new(PanelId, EditorStrings.PanelConsole, panel => {
            panel.WhenPressedIn(() => shell.Context = context);

            var view = panel.Add<ConsoleView>();

            model ??= new ConsoleModel(sink);
            view.Show(model);

            view.Activated += (_, record) => activated(record);

            View = view;
        }) {
            Closed = () => View = null
        };

    /// <summary>Takes whatever has been logged into the open view.</summary>
    /// <remarks>
    ///     ⚠ <b>Pulled once a frame rather than subscribed to, and the reason is threading:</b> the sink
    ///     is written from the pool by a content import and by anything else the editor runs in the
    ///     background, so a subscription would rebuild the panel's rows off the frame thread.
    /// </remarks>
    public void Tick() => View?.Tick();

    /// <summary>Empties the open console, which empties the ring behind it.</summary>
    public void Clear() => View?.Clear();

    /// <summary>Flips the open console's Clear on Play.</summary>
    public void ToggleClearsOnPlay() {
        if (View is { } view) {
            view.ClearsOnPlay = !view.ClearsOnPlay;
        }
    }
}
