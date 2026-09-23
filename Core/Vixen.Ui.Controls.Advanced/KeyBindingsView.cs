// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One line of the keybinding panel: a command, and where its chord came from.</summary>
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

/// <summary>Every command, its chord, and where the chord came from — and the place to change one.</summary>
/// <remarks>
///     <para>
///         The panel is <c>KeyBindingsView.vxml</c>; this file is the accessibility modifier and the
///         record the grid's rows are made of. The emitter's partial carries no modifier, so the
///         declaration that says <c>public</c> has to be here.
///     </para>
///     <para>
///         ⚠ <b>A control library's since #650, and before that the editor's.</b> Doc 20's A5 asked
///         for it as an editor panel, and nothing in it was ever the editor's business: a
///         <see cref="KeyMap" /> and a <see cref="CommandRegistry" /> are <c>Vixen.Ui.Controls</c>
///         types, so an application that could dispatch a chord and draw one beside a menu item
///         could not show a list of them or let anybody change one. The editor now hosts this panel
///         as any application would, supplying its preset names through <see cref="PresetNames" />
///         and its keymap file through the two events below.
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
///         ⚠ <b>Import and export are events rather than file calls.</b> A control has no file
///         picker and is deliberately not given one, so the panel says what the user asked for and
///         the application, which has the picker, answers — reading the file with
///         <see cref="KeyMapYaml" /> or with whatever format it keeps its preferences in.
///     </para>
/// </remarks>
public sealed partial class KeyBindingsView;
