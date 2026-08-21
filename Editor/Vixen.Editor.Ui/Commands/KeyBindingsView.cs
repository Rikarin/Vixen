// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>One line of the keybinding editor: a command, and where its chord came from.</summary>
/// <param name="Id">The command's id, which is what a keymap file names.</param>
/// <param name="Title">What it is called on screen.</param>
/// <param name="Category">Where the palette files it.</param>
/// <remarks>
///     ⚠ <b>The chord is <i>not</i> on this record and is read from the map every time a cell is
///     filled.</b> A row that carried its chord would be a second copy of the binding, and the way
///     that goes wrong is a rebind that changes the map and leaves the grid showing the old key until
///     something happened to rebuild it.
/// </remarks>
public sealed record KeyBindingRow(string Id, string Title, string Category);

/// <summary>The panel doc 20's A5 asks for: every command, its chord, and where the chord came from.</summary>
/// <remarks>
///     <para>
///         The panel is <c>KeyBindingsView.vxml</c>; this file is the accessibility modifier and the
///         record the grid's rows are made of. The emitter's partial carries no modifier, and
///         <c>Vixen.Editor.App</c> holds this type — so the declaration that says <c>public</c> has
///         to be here.
///     </para>
///     <para>
///         <b><see cref="KeyMap" /> has had conflict detection, per-command overrides, a
///         defaults-versus-overrides split and reset since it was written, and no way to reach any of
///         it.</b> Doc 11 flags that and doc 20 spells out the panel: a grid of command / category /
///         binding / source, a filter box, a "press a key" capture, conflict reporting inline,
///         per-row and global reset, and import and export of a keymap file.
///     </para>
///     <para>
///         ⚠ <b>The preset dropdown is the point and the grid is the consolation.</b> Doc 20's own
///         note is that "presets matter more than they look" — a Unity user and an Unreal user
///         disagree about most of the bar and both are certain — so the one control that turns a week
///         of friction into a choice is the preset picker, and it is first on the strip for that
///         reason.
///     </para>
///     <para>
///         ⚠ <b>Capture is a mode with a visible end, not a modal.</b> While it is on, every key the
///         panel sees is a candidate binding — including Escape, which is what cancels it, and which
///         is therefore the one chord this panel will not let you bind. The alternative is a dialog,
///         and a dialog that swallows keystrokes to record them cannot be driven by the automation
///         harness or screenshotted, which is <see cref="DialogService" />'s own argument turned round.
///     </para>
///     <para>
///         ⚠ <b>Import and export are events rather than file calls.</b> This assembly has no
///         <c>INativeDialogs</c> and is deliberately not allowed one — the shell is a
///         <c>UiDocument</c> and nothing else — so the panel says what the user asked for and the
///         application, which has the picker, answers. The same arrangement
///         <see cref="ConsoleView.Activated" /> uses and for the same reason.
///     </para>
/// </remarks>
public sealed partial class KeyBindingsView;
