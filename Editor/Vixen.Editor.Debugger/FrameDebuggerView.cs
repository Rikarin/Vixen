// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Debugger;

/// <summary>A frame's command stream, steppable, with the state at each call beside it.</summary>
/// <remarks>
///     <para>
///         The panel is <c>FrameDebuggerView.vxml</c>; this file is the accessibility modifier and
///         the record its state pane is made of. The emitter's partial carries no modifier, and
///         <c>Vixen.Editor.Diagnostics</c> and <c>Vixen.Editor.App.Tests</c> both hold this type —
///         so the declaration that says <c>public</c> has to be here. Same arrangement as
///         <c>GpuTimelineView</c>, <c>MemoryView</c> and <c>StatisticsView</c>.
///     </para>
///     <para>
///         <b>Doc 20's E4 exit criterion: "a draw call can be stepped and its render target
///         inspected".</b> The first half is here in full — the tree, the two step buttons, and a
///         state pane replayed to whichever call is selected. The second half is honest about what it
///         has: a capture taken from a recording backend holds the state and not the pixels, and the
///         target pane says which attachments the draw wrote rather than showing an image that would
///         be a fabrication.
///     </para>
///     <para>
///         ⚠ <b>Stepping moves between draws, not between commands.</b> A frame is a few thousand
///         calls and forty of them per draw are binds; a step that advanced one command would take
///         forty presses to reach the next thing that put a pixel anywhere.
///         <see cref="FrameCapture.Work" /> is the index that makes it one press.
///     </para>
///     <para>
///         ⚠ <b>The tree is rebuilt on capture rather than every frame.</b> A capture does not change
///         while it is on screen — that is the whole point of capturing one — so the only work per
///         frame here is none.
///     </para>
/// </remarks>
public sealed partial class FrameDebuggerView;

/// <summary>One line of the state pane: a heading naming a group, or a fact inside one.</summary>
/// <param name="Slot">Its position in the list, which is what keeps two identical lines apart.</param>
/// <param name="Key">The name on the left, or the group's name when this is a heading.</param>
/// <param name="Value">What it is worth, or <see langword="null" /> for a heading.</param>
/// <param name="IsHeading">Whether this line names the group below it.</param>
/// <remarks>
///     ⚠ <b>A record struct because the <c>@for</c> keys on it, and the key rule wants a value.</b>
///     Nothing here is signal-backed and nothing mutates: a line whose text has changed is a
///     different line, and replacing its region is the right reconciliation. <paramref name="Slot" />
///     is what makes the key unique — two descriptor slots holding the same handle produce the same
///     three strings, and two equal keys in one loop is not something <c>BuildContext.For</c> can be
///     asked to reconcile.
/// </remarks>
internal readonly record struct StatePaneRow(int Slot, string Key, string? Value, bool IsHeading);
