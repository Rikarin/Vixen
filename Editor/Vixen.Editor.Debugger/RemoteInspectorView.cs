// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Debugger;

/// <summary>A running build's entities, its counters, and a way to write a value back.</summary>
/// <remarks>
///     <para>
///         The panel is <c>RemoteInspectorView.vxml</c>; this file is the accessibility modifier, the
///         record the counter pane keys on, and the two cells that let markup write an intrinsic
///         tag's own <c>Text</c>.
///     </para>
///     <para>
///         <b>Doc 13 calls this "how mobile and console debugging actually happens", and doc 20's E4
///         asks for an entity to be mutated live.</b> The entity tree is the far end's — the editor
///         has no idea what those entities are or which scene they came from — which is why the rows
///         carry a name and a component list rather than anything this process could inspect.
///     </para>
///     <para>
///         ⚠ <b>The write box takes <c>Component.Member</c> and a text value rather than drawing the
///         inspector.</b> Drawing a real inspector needs the far end's type descriptors, which is a
///         second protocol and a schema negotiation between two builds that may differ. What a text
///         field buys is that the exit criterion — an entity mutated live — is met today, over a
///         protocol both ends can implement in an afternoon.
///     </para>
///     <para>
///         ⚠ <b>Polled from the panel's tick rather than from a thread.</b> That is the transport's
///         own contract: nothing is delivered outside <c>Poll</c>, so the entity list is only ever
///         touched on the frame thread and the panel needs no lock. ⚠ <b>And it is why the signals
///         matter more here than in any other panel of the wave</b> — <c>Poll</c> runs every frame,
///         so the hand-written <c>Restate</c> relabelled a button, rewrote a sentence, rebuilt the
///         entity tree and re-ran a pool sixty times a second whether or not the far end had said
///         anything. An unchanged poll now writes no signal and runs no effect.
///     </para>
/// </remarks>
public sealed partial class RemoteInspectorView;

/// <summary>One live counter's row, as the <c>@for</c> keys it.</summary>
/// <param name="Name">What the far end calls it.</param>
/// <param name="Reading">Its value, as the pane prints it.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key, and there is no slot in it — which is the one place in this
///     wave where leaving the slot out is right.</b> A counter is keyed by name on the far end and
///     the pane is sorted by that name, so two equal rows are not merely unlikely, they are
///     unrepresentable: <c>IReadOnlyDictionary</c> cannot hold the same key twice.
/// </remarks>
internal readonly record struct CounterRow(string Name, string Reading);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its four-line escape. <c>remote-status</c>,
///     <c>counter-label</c> and <c>counter-value</c> are all written with
///     <c>element.Text = …</c> by the panel this replaces, and an interpolation would have added a
///     <c>text</c> child inside each of them.
/// </remarks>
internal sealed class RemoteStatus : UiElement {
    /// <inheritdoc />
    protected override string TagName => "remote-status";
}

/// <inheritdoc cref="RemoteStatus" />
internal sealed class CounterLabel : UiElement {
    /// <inheritdoc />
    protected override string TagName => "counter-label";
}

/// <inheritdoc cref="RemoteStatus" />
internal sealed class CounterValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "counter-value";
}
