// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>doc 11 § Input system's second editor surface: the input debug panel.</summary>
/// <remarks>
///     <para>
///         <b>The action-map editor was built and this was not.</b> <c>InputActionsView</c> edits the
///         <c>.vxinput</c> and is registered in <see cref="StandardEditors" />; nothing anywhere showed
///         what a device is actually doing. <c>Core/Vixen.Input/README.md</c> said the reason had
///         expired — "two panels nobody has written rather than two panels with nowhere to go" — and
///         this is the second of them.
///     </para>
///     <para>
///         ⚠ <b>Here, beside the action-map editor, for <c>AgentDebuggerPanel</c>'s reason.</b> A
///         debug panel has no document and no extension, so the factory registry has no seat for it;
///         a module that already owns a <see cref="PluginContext" /> does, and
///         <see cref="PluginContext.AddPanel(PanelDescriptor)" /> takes it out again on unload.
///     </para>
///     <para>
///         ⚠ <b>A host with no <see cref="InputService" /> gets a first line that says so.</b> The
///         shipping editor publishes one since
///         <see href="https://github.com/Rikarin/Vixen/issues/470">#470</see> — a play session's,
///         fed only by a focused scene pane, published once because it outlives every session — so
///         this panel is live there. A host that publishes none still gets the honest sentence, and it
///         is worth stating that the honest failure was the hard part: four empty lists are what a
///         panel draws when nobody is pressing anything <i>and</i> what it draws when nothing in the
///         process reads a device, and the second is the one that would have been mistaken for the
///         first for years.
///     </para>
/// </remarks>
public sealed partial class AssetEditorsModule {
    /// <summary>What the input debug panel is registered as.</summary>
    public const string InputDebugPanelId = "input-debug";

    InputDebugView? inputDebug;

    PluginContext? inputContext;

    /// <summary>Registers the panel and keeps it fed.</summary>
    /// <param name="context">The module's context.</param>
    void InputDebugPanel(PluginContext context) {
        // ⚠ Before AddPanel: a panel restored from a saved layout is built during registration, so a
        // factory that ran before this was assigned would show a panel that never finds the service.
        inputContext = context;

        context.AddPanel(
            new PanelDescriptor(
                InputDebugPanelId,
                AssetEditorStrings.PanelInputDebug,
                panel => {
                    inputDebug = panel.Add<InputDebugView>();

                    Reading();
                }
            ) {
                Closed = () => inputDebug = null
            }
        );

        // Only while the panel is open. Polling a device set is cheap, but `Actuated` walks every
        // control on every gamepad and the answer is of interest to nobody who is not looking.
        context.OnUpdate(_ => Reading());
    }

    /// <summary>Re-reads the published service, if this editor has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Looked up every frame rather than captured once.</b> A host may publish its service
    ///     after the panel was opened — a play session is the obvious case — and a panel that resolved
    ///     the service in its factory would stay dark for the rest of the editor's life with no way
    ///     for anybody to tell that it had simply asked too early.
    /// </remarks>
    void Reading() {
        if (inputDebug is null) {
            return;
        }

        inputDebug.Show(
            inputContext is not null && inputContext.Services.TryGet<InputService>(out var service) ? service : null
        );
    }
}
