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

/// <summary>Several commands drawn as one segmented control.</summary>
/// <param name="CommandIds">Its members, in order.</param>
/// <remarks>
///     ⚠ <b>Translate, Rotate and Scale are a choice and have to look like one.</b> Three adjacent
///     buttons that happen to be next to each other say nothing about being mutually exclusive; a
///     segmented control says it before anything is clicked. The commands are ordinary commands with
///     a <see cref="EditorCommand.RadioGroup" /> — nothing here enforces exclusivity, because what
///     makes them exclusive is that each one's <c>Checked</c> predicate reads the same state.
/// </remarks>
public sealed record ToolbarGroup(params string[] CommandIds) : ToolbarEntry;

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
