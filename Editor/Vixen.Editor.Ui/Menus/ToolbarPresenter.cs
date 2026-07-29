// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>A strip of buttons, segmented groups and dropdowns over command ids.</summary>
/// <remarks>
///     <para>
///         The third view over the registry, and the one that shows why the registry is worth
///         having: a toolbar is a list of ids, and every button's label, icon, tooltip, shortcut and
///         enabled state is looked up rather than declared here.
///     </para>
///     <para>
///         ⚠ <b>Enablement is refreshed on a tick rather than when something changes.</b> A menu can
///         wait until it opens; a toolbar is on screen the whole time, and there is no event for
///         "the selection changed in a way that makes Delete meaningful". One predicate per button
///         per frame is a handful of comparisons — and it is the same trade the command's own
///         remarks make about keeping the predicate cheap.
///     </para>
///     <para>
///         <b>A command with an icon gets an icon button; one without gets its title.</b> A toolbar
///         of identical blank squares is worse than a toolbar of words, and requiring an icon would
///         mean a plugin could not add a toolbar button without drawing one. It is also doc 20's
///         stated mitigation for the icon set being a design dependency: a glyph that has not been
///         drawn yet costs a wider button and never a blocked feature.
///     </para>
///     <para>
///         ⚠ <b>The bar grows sections, not entries.</b> Doc 20's toolbar is <i>mode buttons | save
///         and build | transform mode, space, pivot, snap | play | layout</i>, and two of those need
///         something a flat list of ids cannot express: a segmented control, so that the three gizmo
///         modes read as one choice, and a dropdown, so that a snap value is a popover rather than
///         eight more buttons. <see cref="Show(ReadOnlySpan{ToolbarEntry})" /> takes those;
///         <see cref="Show(ReadOnlySpan{string})" /> is still the flat form, and is the same thing
///         with every entry a button.
///     </para>
/// </remarks>
public sealed class ToolbarPresenter {
    readonly List<(ButtonBase Button, EditorCommand Command)> buttons = [];
    readonly List<ContextMenu> popovers = [];
    readonly CommandRegistry commands;
    readonly KeyMap keys;
    readonly UiElement host;

    /// <summary>Which of the host's children the strip is, whatever else has been added since.</summary>
    readonly int slot;

    UiElement? strip;

    /// <summary>Builds a toolbar into an element.</summary>
    /// <param name="host">Where the strip goes.</param>
    /// <param name="commands">What its buttons run.</param>
    /// <param name="keys">What their tooltips say the shortcut is.</param>
    /// <remarks>
    ///     ⚠ <b>Nothing is added until <see cref="Show(ReadOnlySpan{ToolbarEntry})" /> is called, and
    ///     the place it will go is remembered now.</b> A shell builds its chrome top to bottom and
    ///     puts the toolbar's commands on it last — so by the time there is a strip to add, appending
    ///     it to the host would put it after the docking workspace and the status bar rather than
    ///     under the menu bar. Remembering the position costs an integer; an empty strip built here
    ///     to hold the place would cost a bordered band of chrome in every shell that never asks for
    ///     a toolbar.
    /// </remarks>
    public ToolbarPresenter(UiElement host, CommandRegistry commands, KeyMap keys) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        this.host = host;
        this.commands = commands;
        this.keys = keys;

        slot = host.Children.Count;
    }

    /// <summary>What is on it, in order.</summary>
    public IReadOnlyList<ToolbarEntry> Entries { get; private set; } = [];

    /// <summary>The ids on it, with <c>null</c> for anything that is not a plain button.</summary>
    /// <remarks>
    ///     Kept because it is what the flat <see cref="Show(ReadOnlySpan{string})" /> describes and
    ///     what a caller that never asked for a section wants back. A group or a dropdown reads as a
    ///     separator here, which is what it looks like to something that only knows about buttons.
    /// </remarks>
    public IReadOnlyList<string?> Items =>
        [.. Entries.Select(entry => entry is ToolbarButton button ? button.CommandId : null)];

    /// <summary>The strip element, which is replaced by every rebuild.</summary>
    public UiElement Strip => strip!;

    /// <summary>Puts a set of commands on the toolbar.</summary>
    /// <param name="commandIds">Their ids, with <c>null</c> for a separator.</param>
    public void Show(params ReadOnlySpan<string?> commandIds) {
        var entries = new ToolbarEntry[commandIds.Length];

        for (var index = 0; index < commandIds.Length; index++) {
            entries[index] = commandIds[index] is { } id ? new ToolbarButton(id) : new ToolbarSeparator();
        }

        Show(entries);
    }

    /// <summary>Puts a described strip on the toolbar.</summary>
    /// <param name="entries">The buttons, rules, groups and dropdowns, in order.</param>
    public void Show(params ReadOnlySpan<ToolbarEntry> entries) {
        Entries = entries.ToArray();
        Rebuild();
    }

    /// <summary>Throws the strip away and builds it again.</summary>
    public void Rebuild() {
        buttons.Clear();

        // ⚠ The popovers go too, and they are not the strip's children. A `ContextMenu` hangs off the
        // document root so it can float over everything — the same arrangement `MenuBar.AddMenu`
        // uses — so a rebuild that removed only the strip would leave one invisible, still attached
        // to a button that no longer exists, per rebuild.
        foreach (var popover in popovers) {
            if (!popover.IsRemoved) {
                popover.Remove();
            }
        }

        popovers.Clear();
        strip?.Remove();

        strip = host.Add<UiElement>("toolbar");

        // ⚠ Into the place the constructor reserved. See its remarks: `Add` appends, and the strip
        // is built after the rest of the chrome is already in the host.
        if (strip.IndexInParent > slot) {
            host.Document.Move(strip, Math.Min(slot, host.Children.Count - 1));
        }

        strip.AddHandler<ClickEvent>((_, args) => Chosen(args));

        foreach (var entry in Entries) {
            switch (entry) {
                case ToolbarSeparator:
                    strip.Add<Separator>().Orientation = Orientation.Vertical;
                    break;

                case ToolbarButton(var id) when commands.TryGet(id, out var command):
                    buttons.Add((Button(strip, command), command));
                    break;

                case ToolbarGroup(var ids):
                    Segmented(ids);
                    break;

                case ToolbarDropdown(var title, var icon, var ids):
                    Dropdown(title, icon, ids);
                    break;

                default:
                    break;
            }
        }

        Refresh();
    }

    /// <summary>Asks every command on the strip whether it can run, and shows the answer.</summary>
    public void Refresh() {
        foreach (var (button, command) in buttons) {
            button.Disabled = !commands.CanExecute(command);

            if (command.Checked is null) {
                continue;
            }

            // Through the state rather than a class, because `:checked` is what the control theme
            // already draws a pressed toggle with.
            if (command.IsChecked) {
                button.State |= ElementState.Checked;
            } else {
                button.State &= ~ElementState.Checked;
            }
        }
    }

    /// <summary>Draws a set of commands as one segmented control.</summary>
    /// <remarks>
    ///     A class on the wrapper and nothing else: which corners are rounded and where the dividing
    ///     hairlines go is the theme's, and a presenter that positioned them would be one a theme
    ///     could not restyle.
    /// </remarks>
    void Segmented(IReadOnlyList<string> commandIds) {
        var group = Strip.Add<UiElement>("toolbar-group");

        foreach (var id in commandIds) {
            if (commands.TryGet(id, out var command)) {
                buttons.Add((Button(group, command), command));
            }
        }

        // A group whose every command has gone — a plugin's, unloaded — would otherwise be an empty
        // bordered box on the bar.
        if (group.Children.Count == 0) {
            group.Remove();
        }
    }

    /// <summary>Draws a button that opens a menu of commands.</summary>
    /// <remarks>
    ///     ⚠ <b>The menu is built once with the strip, not per click.</b> A rebuild is what a
    ///     registry change triggers, so the lines cannot go stale without the whole strip being
    ///     replaced — and building per click would mean a menu leaked for every time somebody looked
    ///     at the snap values and changed their mind.
    /// </remarks>
    void Dropdown(StringId title, string? icon, IReadOnlyList<string?> commandIds) {
        var menu = MenuPresenter.Context(host.Document, commands, keys, [.. commandIds]);
        popovers.Add(menu);

        var button = Chevron(Strip, title, icon);
        button.Clicked += pressed => menu.Open(pressed);
    }

    ButtonBase Button(UiElement into, EditorCommand command) {
        var label = command.Title.Text;
        var chord = keys.ChordFor(command.Id);
        var description = chord.IsBound ? $"{label} ({chord.Describe()})" : label;

        if (command.Icon is null) {
            var text = into.Add<Button>();
            text.Label = label;
            text.Variant = ControlVariant.Subtle;
            text.Size = ControlSize.Small;

            return text;
        }

        var icon = into.Add<IconButton>();
        icon.LeadingIcon.Geometry = command.Icon;
        icon.Variant = ControlVariant.Subtle;
        icon.Size = ControlSize.Small;

        // An icon button's label is what a screen reader reads and what a tooltip would show, so
        // the shortcut belongs in it: a button whose only affordance is a picture is the one that
        // most needs to say what it does.
        icon.Label = description;

        return icon;
    }

    /// <summary>The button a dropdown hangs off: a label or a glyph, and a chevron either way.</summary>
    static Button Chevron(UiElement into, StringId title, string? icon) {
        var button = into.Add<Button>();
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.AddClass("toolbar-dropdown");

        if (icon is not null && EditorIcons.Find(icon) is { } glyph) {
            button.LeadingIcon.Geometry = glyph;

            // Still set, still not drawn: a button whose only affordance is a picture is the one
            // that most needs to say what it is. See `IconButton`'s remarks about `Label`.
            button.Label = title.Text;
        } else {
            button.Label = title.Text;
        }

        // Appended rather than a part, because `ButtonBase` has a leading icon and no trailing one —
        // and a chevron is what tells a dropdown apart from a button that just does something.
        var chevron = button.Add<Icon>();
        chevron.Geometry = ControlIcons.ChevronDown;
        chevron.AddClass("chevron");

        return button;
    }

    void Chosen(ClickEvent args) {
        foreach (var (button, command) in buttons) {
            if (ReferenceEquals(args.Source, button)) {
                commands.Execute(command.Id);
                return;
            }
        }
    }
}
