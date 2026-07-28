// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>A strip of buttons over command ids.</summary>
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
///         mean a plugin could not add a toolbar button without drawing one.
///     </para>
/// </remarks>
public sealed class ToolbarPresenter {
    readonly List<(ButtonBase Button, EditorCommand Command)> buttons = [];
    readonly CommandRegistry commands;
    readonly KeyMap keys;
    readonly UiElement host;

    UiElement? strip;

    /// <summary>Builds a toolbar into an element.</summary>
    /// <param name="host">Where the strip goes.</param>
    /// <param name="commands">What its buttons run.</param>
    /// <param name="keys">What their tooltips say the shortcut is.</param>
    public ToolbarPresenter(UiElement host, CommandRegistry commands, KeyMap keys) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        this.host = host;
        this.commands = commands;
        this.keys = keys;
    }

    /// <summary>The ids on it, with <c>null</c> for a separator.</summary>
    public IReadOnlyList<string?> Items { get; private set; } = [];

    /// <summary>The strip element, which is replaced by every rebuild.</summary>
    public UiElement Strip => strip!;

    /// <summary>Puts a set of commands on the toolbar.</summary>
    /// <param name="commandIds">Their ids, with <c>null</c> for a separator.</param>
    public void Show(params ReadOnlySpan<string?> commandIds) {
        Items = commandIds.ToArray();
        Rebuild();
    }

    /// <summary>Throws the strip away and builds it again.</summary>
    public void Rebuild() {
        buttons.Clear();
        strip?.Remove();

        strip = host.Add<UiElement>("toolbar");
        strip.AddHandler<ClickEvent>((_, args) => Chosen(args));

        foreach (var id in Items) {
            if (id is null) {
                strip.Add<Separator>().Orientation = Orientation.Vertical;
                continue;
            }

            if (commands.TryGet(id, out var command)) {
                buttons.Add((Button(strip, command), command));
            }
        }

        Refresh();
    }

    /// <summary>Asks every command on the strip whether it can run, and shows the answer.</summary>
    public void Refresh() {
        foreach (var (button, command) in buttons) {
            button.Disabled = !command.CanExecute;

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

    void Chosen(ClickEvent args) {
        foreach (var (button, command) in buttons) {
            if (ReferenceEquals(args.Source, button)) {
                commands.Execute(command.Id);
                return;
            }
        }
    }
}
