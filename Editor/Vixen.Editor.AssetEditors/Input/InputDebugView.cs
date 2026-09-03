// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Input;

/// <summary>
///     The input debug panel: which devices there are, what is actuated right now, where the pointer
///     is, and what every action reads.
/// </summary>
/// <remarks>
///     <para>
///         [doc 11](../../../docs/plan/11-editor.md) § Input system's second editor surface — "the
///         action-map editor, plus an input-debug panel showing live device state". The first is
///         <c>InputActionsView</c> beside this file; this is the second.
///     </para>
///     <para>
///         ⚠ <b>The source line is the whole design, and it is there because an instrument that never
///         ran reports success.</b> The editor process owns no <c>InputService</c> — it routes
///         platform events straight into the interface's own document and never into an
///         <c>InputDeviceSet</c> — so a panel that simply listed "what is pressed" would draw an empty
///         list whether the player was pressing nothing or nothing in this process was reading a
///         device at all. Those are different sentences and this panel says which one it means before
///         it says anything else.
///     </para>
///     <para>
///         ⚠ <b>It reads a service rather than owning one, for the reason the agent debugger reads an
///         <c>AiSystem</c> rather than stepping agents.</b> A panel that built its own
///         <c>InputService</c> and fed it from the editor's <c>KeyEvent</c>s would be a *second*
///         opinion about what the player is holding down — right up until the two disagreed, which is
///         the frame somebody opened this panel to understand. A host that reads devices publishes
///         its service with <c>Services.Add(service)</c>, and this goes live with no further wiring.
///     </para>
///     <para>
///         ⚠ <b>Deltas and positions are in their own section rather than in "pressed now".</b>
///         <c>InputDeviceSet.Actuated</c> deliberately leaves them out — a rebinding screen that
///         offered <c>&lt;Mouse&gt;/position/x</c> because the pointer drifted towards the button
///         would be unusable — so a panel that only drew <c>Actuated</c> would report a mouse that
///         never moves.
///     </para>
/// </remarks>
public sealed partial class InputDebugView;

/// <summary>One line of one of the panel's four lists.</summary>
/// <param name="Slot">Where it is in that list.</param>
/// <param name="Active">Whether it is live — pressed, connected, performing — which is a different tag.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Value">And what it reads.</param>
/// <remarks>
///     ⚠ <b><see cref="Active" /> is in the key, and <see cref="Slot" /> is there because equal rows
///     are normal.</b> Two disconnected gamepads with the same name are two rows a <c>@for</c> cannot
///     reconcile without one, and the live/idle choice is a tag rather than a class — so a surviving
///     key would leave the highlight on whatever was pressed when the panel opened while every value
///     beside it kept updating.
/// </remarks>
internal readonly record struct InputFactRow(int Slot, bool Active, string Name, string Value);

/// <summary>
///     ⚠ The cells that exist only so that markup can set an intrinsic tag's own <c>Text</c> — the
///     panel ledger's shape 5.
/// </summary>
internal sealed class InputName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "input-name";
}

/// <inheritdoc cref="InputName" />
internal sealed class InputValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "input-value";
}

/// <inheritdoc cref="InputName" />
internal sealed class InputSource : UiElement {
    /// <inheritdoc />
    protected override string TagName => "input-source";
}
