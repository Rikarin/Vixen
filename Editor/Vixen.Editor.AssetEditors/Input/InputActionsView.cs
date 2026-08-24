// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Input;

/// <summary>What a row of the action tree is.</summary>
/// <param name="Map">The map it belongs to, by name.</param>
/// <param name="Action">The action, by name, or empty for the map's own row.</param>
/// <param name="Binding">Which binding, or −1 for the action's own row.</param>
/// <param name="Scheme">The control scheme, by name, or empty.</param>
/// <remarks>
///     ⚠ <b>Its members are nullable and every reader has to say so.</b> <c>default(InputRow)</c> is
///     what a tree with nothing selected produces — <c>TreeNode.Tag</c> is null then — and a struct's
///     <c>default</c> runs no constructor and no property initializer, so a <c>string</c> member is
///     genuinely null however the declaration is written. Coalescing in an accessor <i>looks</i> like
///     it fixes this and does not; the readers use <c>is { Length: &gt; 0 }</c>, which is correct for
///     both null and empty and is the only shape that stays correct.
/// </remarks>
public readonly record struct InputRow(string? Map, string? Action = null, int Binding = -1, string? Scheme = null);

/// <summary>An action asset, open for editing: maps, actions, bindings and control schemes.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>InputActionsView.vxml</c>; this file exists only to make it public and
///         sealed.</b> The markup compiler emits a partial class with no accessibility modifier,
///         which is <c>internal</c> — deliberately, so that a component is not public API by
///         accident — and this one is constructed from another assembly and read by two test suites.
///     </para>
///     <para>
///         Doc 11's row is "action-map editor", and what that means concretely is the four things a
///         person does to one of these files: name an action and say what shape its value is, hang
///         bindings off it, group those bindings into control schemes, and — the one that cannot be
///         done by typing — press the key you want.
///     </para>
///     <para>
///         ⚠ <b>Listening is a mode rather than a modal, which is <c>KeyBindingsView</c>'s argument
///         restated.</b> A dialog that swallows keystrokes to record them cannot be driven by the
///         automation harness. So the button latches, the panel takes keys on the capture leg ahead
///         of the command dispatcher, and Escape is the one control it will not record because
///         Escape is how listening ends. That capture-leg handler is
///         <c>&lt;self on:keydown.capture.handled /&gt;</c> in the markup, and this panel is one of
///         the two in the tree that should carry <c>handled</c> at all — see the file header there.
///     </para>
///     <para>
///         ⚠ <b>It records a <i>control</i>, not a chord.</b> The editor's own keymap binds
///         <c>Ctrl+S</c>; a game's action binds <c>&lt;Keyboard&gt;/s</c>, and the modifier is a
///         composite with a <c>modifier</c> part. Writing a chord into a binding path would produce a
///         path <c>InputControlPath.TryParse</c> refuses, which is a binding that silently never
///         fires.
///     </para>
/// </remarks>
public sealed partial class InputActionsView;

/// <summary>Opens an input action asset.</summary>
public sealed class InputActionsEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Input Actions";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [InputActionsDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new InputActionsDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<InputActionsView>();
        view.Show((InputActionsDocument) document);

        return view;
    }
}
