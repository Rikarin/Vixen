// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>One thing on the toolbar, in the model rather than on screen.</summary>
/// <remarks>
///     ⚠ <b>A toolbar is described by command ids, exactly as a menu is.</b> No labels, no icons and
///     no enablement live here — all three come from the registry when the strip is built, so a
///     command renamed or disabled is right on the bar without anything being told.
/// </remarks>
public abstract record ToolbarEntry;

/// <summary>A button that runs a command.</summary>
/// <param name="CommandId">Which one.</param>
public sealed record ToolbarButton(string CommandId) : ToolbarEntry;

/// <summary>A rule between two groups of buttons.</summary>
public sealed record ToolbarSeparator : ToolbarEntry;

/// <summary>Several commands drawn as one segmented control, of which exactly one is chosen.</summary>
/// <param name="CommandIds">Its members, in order.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Translate, Rotate and Scale are a choice and have to look like one.</b> Three
///         adjacent buttons that happen to be next to each other say nothing about being mutually
///         exclusive; a segmented control says it before anything is clicked, and says it to a
///         screen reader as well — one <c>radiogroup</c> announcing "two of three" rather than three
///         independent pressed-or-not buttons.
///     </para>
///     <para>
///         ⚠ <b>A claim about the commands, and the presenter now acts on it.</b> This is drawn as a
///         <see cref="SegmentedControl" />, which has exclusivity and wrapping arrows of its own —
///         so a set of commands that are <i>not</i> alternatives must not be described with this.
///         <see cref="ToolbarBox" /> is what a merely-adjacent set is; the transport is the one in
///         the tree, and its own comment already said it was a different claim.
///     </para>
///     <para>
///         ⚠ <b>What makes the members exclusive is still the commands, not this.</b> Each one's
///         <c>Checked</c> predicate reads the same state, and the control shows whichever says yes.
///         ⚠ <c>EditorCommand.RadioGroup</c> is <i>not</i> what marks a group — this record's
///         remarks used to say it was, and the three gizmo commands have never set it.
///     </para>
/// </remarks>
public sealed record ToolbarGroup(params string[] CommandIds) : ToolbarEntry;

/// <summary>Several commands drawn as one boxed run of buttons.</summary>
/// <param name="CommandIds">Its members, in order.</param>
/// <remarks>
///     ⚠ <b>One <i>control</i>, where <see cref="ToolbarGroup" /> is one <i>choice</i>, and the
///     transport is why the two are separate records.</b> A transport bar is a single object in every
///     editor, every player and every tape machine there has ever been, so Play, Pause, Step and Stop
///     want the box — but they are four verbs, not four alternatives, and a segmented control would
///     announce them as a question with one answer and let the arrow keys "choose" Stop. The box is
///     appearance only: the buttons inside it are ordinary command-bound buttons, exactly as they are
///     on the open strip.
/// </remarks>
public sealed record ToolbarBox(params string[] CommandIds) : ToolbarEntry;

/// <summary>A button that opens a small menu of commands.</summary>
/// <param name="Title">What the button says when nothing supplies an icon.</param>
/// <param name="Icon">The id of the glyph on it, as <see cref="EditorIcons.All" /> keys it.</param>
/// <param name="CommandIds">What is on the menu, with <see langword="null" /> for a separator.</param>
/// <remarks>
///     What a snap-value picker, a build-target picker and a camera-speed picker are. The menu is
///     built from the registry through <see cref="MenuPresenter.Context" />, so its lines get their
///     labels, ticks, shortcuts and enablement from the same place every other view does.
/// </remarks>
public sealed record ToolbarDropdown(StringId Title, string? Icon, params string?[] CommandIds) : ToolbarEntry;
